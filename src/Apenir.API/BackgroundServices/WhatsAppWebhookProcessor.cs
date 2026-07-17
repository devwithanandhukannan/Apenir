using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Apenir.Core.Entities;
using Apenir.Core.Enums;
using Apenir.Core.Interfaces;
using Apenir.API.Controllers;

namespace Apenir.API.BackgroundServices
{
    public class WhatsAppWebhookProcessor : BackgroundService
    {
        private readonly IWhatsAppWebhookQueue _queue;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<WhatsAppWebhookProcessor> _logger;
        private readonly IMemoryCache _cache;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _userLocks = new();
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
        private const string CacheKeyServices  = "wa_active_services";
        private const string CacheKeyBranches  = "wa_active_branches";

        private static string FormatTime(TimeOnly t) => t.ToString("hh:mm tt");

        public WhatsAppWebhookProcessor(
            IWhatsAppWebhookQueue queue,
            IServiceProvider serviceProvider,
            ILogger<WhatsAppWebhookProcessor> logger,
            IMemoryCache cache)
        {
            _queue          = queue;
            _serviceProvider = serviceProvider;
            _logger         = logger;
            _cache          = cache;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("WhatsApp Webhook Processor background service is starting.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var payload = await _queue.DequeueAsync(stoppingToken);
                    await ProcessPayloadAsync(payload, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred executing WhatsApp webhook processing.");
                }
            }
        }

        // ─── Cache helpers ──────────────────────────────────────────────────────────

        private async Task<List<Service>> GetCachedServicesAsync(IApplicationDbContext context, CancellationToken ct)
        {
            if (_cache.TryGetValue(CacheKeyServices, out List<Service>? cached) && cached != null)
                return cached;

            var services = await context.Services
                .Where(s => s.IsActive)
                .OrderBy(s => s.Name)
                .ToListAsync(ct);

            _cache.Set(CacheKeyServices, services, CacheDuration);
            return services;
        }

        private async Task<List<Branch>> GetCachedBranchesAsync(IApplicationDbContext context, CancellationToken ct)
        {
            if (_cache.TryGetValue(CacheKeyBranches, out List<Branch>? cached) && cached != null)
                return cached;

            var branches = await context.Branches
                .Where(b => b.IsActive)
                .ToListAsync(ct);

            _cache.Set(CacheKeyBranches, branches, CacheDuration);
            return branches;
        }

        private static double CalculateDistanceKm(double lat1, double lng1, double lat2, double lng2)
        {
            const double R = 6371.0; // Earth radius in km
            var dLat = ToRadians(lat2 - lat1);
            var dLng = ToRadians(lng2 - lng1);
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                    Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        private static double ToRadians(double val) => (Math.PI / 180) * val;

        private async Task ProcessPayloadAsync(string body, CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var context           = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
            var configuration     = scope.ServiceProvider.GetRequiredService<IConfiguration>();

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (!root.TryGetProperty("entry", out var entries)) return;

                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("changes", out var changes)) continue;

                    foreach (var change in changes.EnumerateArray())
                    {
                        if (!change.TryGetProperty("value", out var value)) continue;
                        if (!value.TryGetProperty("messages", out var messages)) continue;

                        foreach (var message in messages.EnumerateArray())
                        {
                            var from    = message.GetProperty("from").GetString()!;
                            var msgType = message.GetProperty("type").GetString();

                            if (message.TryGetProperty("id", out var msgIdProp))
                            {
                                var msgId = msgIdProp.GetString();
                                if (!string.IsNullOrEmpty(msgId))
                                {
                                    var cacheKey = $"msg_{msgId}";
                                    if (_cache.TryGetValue(cacheKey, out _))
                                    {
                                        _logger.LogInformation("⏭️ Skipping duplicate message ID: {MsgId}", msgId);
                                        continue;
                                    }
                                    _cache.Set(cacheKey, true, TimeSpan.FromHours(1));
                                }
                            }

                            _logger.LogInformation("📩 Background processing message from: {From} | Type: {MsgType}", from, msgType);

                            var userLock = _userLocks.GetOrAdd(from, _ => new SemaphoreSlim(1, 1));
                            await userLock.WaitAsync(cancellationToken);
                            try
                            {
                                // Auto-register customer
                                var user = await context.Users.FirstOrDefaultAsync(u => u.Phone == from, cancellationToken);
                                if (user == null)
                                {
                                    user = new User
                                    {
                                        Id    = Guid.NewGuid().ToString(),
                                        Phone = from,
                                        Name  = "WhatsApp User",
                                        Role  = UserRole.Customer,
                                        IsActive = true,
                                        IsDeleted = false
                                    };
                                    context.Users.Add(user);

                                    var customer = new Customer
                                    {
                                        Id     = Guid.NewGuid().ToString(),
                                        UserId = user.Id,
                                        Phone  = from,
                                        Name   = "WhatsApp User"
                                    };
                                    context.Customers.Add(customer);
                                    await context.SaveChangesAsync(cancellationToken);
                                }

                                if (msgType == "text")
                                {
                                    var text = message.GetProperty("text").GetProperty("body").GetString()!;
                                    await ProcessTextMessage(from, text, context, httpClientFactory, configuration, cancellationToken);
                                }
                                else if (msgType == "location")
                                {
                                    var lat = message.GetProperty("location").GetProperty("latitude").GetDouble();
                                    var lng = message.GetProperty("location").GetProperty("longitude").GetDouble();
                                    await ProcessLocationMessage(from, lat, lng, context, httpClientFactory, configuration, cancellationToken);
                                }
                                else if (msgType == "interactive")
                                {
                                    var interactive     = message.GetProperty("interactive");
                                    var interactiveType = interactive.GetProperty("type").GetString();

                                    string? replyId    = null;
                                    string? replyTitle = null;

                                    if (interactiveType == "button_reply")
                                    {
                                        replyId    = interactive.GetProperty("button_reply").GetProperty("id").GetString();
                                        replyTitle = interactive.GetProperty("button_reply").GetProperty("title").GetString();
                                    }
                                    if (interactiveType == "list_reply")
                                    {
                                        replyId    = interactive.GetProperty("list_reply").GetProperty("id").GetString();
                                        replyTitle = interactive.GetProperty("list_reply").GetProperty("title").GetString();
                                    }

                                    if (replyId != null)
                                    {
                                        await ProcessInteractiveReply(from, replyId, replyTitle ?? "", context, httpClientFactory, configuration, cancellationToken);
                                    }
                                }
                            }
                            finally
                            {
                                userLock.Release();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse or process webhook payload in background.");
            }
        }

        private async Task ProcessTextMessage(
            string to, string text,
            IApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var session = await GetOrCreateSessionAsync(to, context, cancellationToken);
            var lower   = text.ToLower().Trim();

            if (IsGreeting(lower))
            {
                session.CurrentState = WhatsAppState.Start;
                await SaveSessionAsync(session, context, cancellationToken);
                await SendGreeting(to, httpClientFactory, configuration);
                return;
            }
            switch (session.CurrentState)
            {
                case WhatsAppState.Start:
                    await SendGreeting(to, httpClientFactory, configuration);
                    break;

                case WhatsAppState.AwaitingItemQuantity:
                    if (int.TryParse(text.Trim(), out int q) && q >= 1 && q <= 6)
                    {
                        var itemId = session.SelectedTestId;
                        if (!string.IsNullOrEmpty(itemId))
                        {
                            if (session.CartItemIds == null) session.CartItemIds = new List<string>();
                            session.CartItemIds.RemoveAll(id => id.StartsWith(itemId + ":"));
                            session.CartItemIds.Add($"{itemId}:{q}");
                            session.SelectedTestId = null;
                        }
                        session.CurrentState = WhatsAppState.ChoosingTest;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendCartOptions(to, session, context, httpClientFactory, configuration, cancellationToken);
                    }
                    else
                    {
                        var name = await GetItemNameById(session.SelectedTestId, context, cancellationToken);
                        await SendItemQuantityPrompt(to, name ?? "selected service", httpClientFactory, configuration);
                    }
                    break;

                case WhatsAppState.AwaitingAddressDetails:
                    // Extract building details, floor, landmark from typed text.
                    // For optimal parsing, if they comma-separate, we save it cleanly.
                    session.BuildingDetails = text.Trim();
                    session.Landmark = "Indicated in address details";
                    session.Floor = "Not specified";

                    // Auto-select closest eligible branch
                    var cartItemIds = (session.SelectedTestId ?? "").Split(',').Select(id => id.Trim()).ToList();
                    var allServices = await GetCachedServicesAsync(context, cancellationToken);
                    var allPackages = await context.Packages.AsNoTracking().Where(p => p.IsActive).ToListAsync(cancellationToken);

                    var branchServices = await context.BranchServices
                        .Where(bs => cartItemIds.Contains(bs.ServiceId) && bs.IsActive)
                        .ToListAsync(cancellationToken);

                    var branchPackages = await context.BranchPackages
                        .Where(bp => cartItemIds.Contains(bp.PackageId) && bp.IsActive)
                        .ToListAsync(cancellationToken);

                    var allBranches = await GetCachedBranchesAsync(context, cancellationToken);

                    double userLat = session.Latitude ?? 0.0;
                    double userLng = session.Longitude ?? 0.0;

                    var nearbyBranches = allBranches
                        .Where(b => b.IsActive)
                        .Select(b => new { Branch = b, Distance = CalculateDistanceKm(userLat, userLng, (double)b.Latitude, (double)b.Longitude) })
                        .Where(x => x.Distance <= x.Branch.ServiceRangeKm)
                        .ToList();

                    var eligibleBranches = new List<dynamic>();
                    foreach (var item in nearbyBranches)
                    {
                        var b = item.Branch;
                        int offeredServicesCount = branchServices.Where(bs => bs.BranchId == b.Id).Select(bs => bs.ServiceId).Distinct().Count();
                        int offeredPackagesCount = branchPackages.Where(bp => bp.BranchId == b.Id).Select(bp => bp.PackageId).Distinct().Count();
                        
                        int totalOfferedInCart = offeredServicesCount + offeredPackagesCount;
                        if (totalOfferedInCart >= cartItemIds.Count)
                        {
                            decimal totalPriceForBranch = 0m;
                            foreach (var itemId in cartItemIds)
                            {
                                var bs = branchServices.FirstOrDefault(x => x.BranchId == b.Id && x.ServiceId == itemId);
                                if (bs != null)
                                {
                                    totalPriceForBranch += bs.CustomPrice ?? allServices.FirstOrDefault(s => s.Id == itemId)?.BasePrice ?? 0m;
                                }
                                else
                                {
                                    var bp = branchPackages.FirstOrDefault(x => x.BranchId == b.Id && x.PackageId == itemId);
                                    if (bp != null)
                                    {
                                        totalPriceForBranch += bp.CustomPrice ?? allPackages.FirstOrDefault(p => p.Id == itemId)?.BasePrice ?? 0m;
                                    }
                                }
                            }

                            eligibleBranches.Add(new
                            {
                                Branch = b,
                                Distance = item.Distance,
                                Price = totalPriceForBranch
                            });
                        }
                    }

                    if (!eligibleBranches.Any())
                    {
                        await SendTextMessage(to, "❌ Sorry, no labs offering your selected items are within range of your location. Please select different items.", httpClientFactory, configuration);
                        session.CurrentState = WhatsAppState.Start;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendGreeting(to, httpClientFactory, configuration);
                        break;
                    }

                    // Auto-select the closest branch
                    var closest = eligibleBranches.OrderBy(x => x.Distance).First();
                    session.SelectedLabId = closest.Branch.Id;
                    session.SelectedLabName = closest.Branch.Name;
                    session.CurrentState = WhatsAppState.ChoosingSlot;
                    await SaveSessionAsync(session, context, cancellationToken);

                    await SendTextMessage(to, $"🏥 Selected Laboratory: {closest.Branch.Name} ({closest.Distance:F1} km away)", httpClientFactory, configuration);
                    await SendSlotList(to, closest.Branch.Id, closest.Branch.Name, context, httpClientFactory, configuration, cancellationToken);
                    break;
                case WhatsAppState.MemberCount:
                    if (int.TryParse(text.Trim(), out int count) && count >= 1 && count <= 6)
                    {
                        var slot = await context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == session.SelectedSlot, cancellationToken);
                        if (slot == null)
                        {
                            await SendTextMessage(to, "❌ Error: The selected slot could not be found. Please select a slot again.", httpClientFactory, configuration);
                            session.CurrentState = WhatsAppState.ChoosingSlot;
                            await SaveSessionAsync(session, context, cancellationToken);
                            if (!string.IsNullOrEmpty(session.SelectedLabId) && !string.IsNullOrEmpty(session.SelectedLabName))
                            {
                                await SendSlotList(to, session.SelectedLabId, session.SelectedLabName, context, httpClientFactory, configuration, cancellationToken);
                            }
                            break;
                        }

                        int availCapacity = slot.MaxCapacity - slot.BookedCount;
                        if (availCapacity < 0) availCapacity = 0;

                        if (count > availCapacity)
                        {
                            if (availCapacity == 0)
                            {
                                await SendTextMessage(to, "❌ Sorry, this slot has just filled up and has *0* spots left. Please select another slot.", httpClientFactory, configuration);
                                session.CurrentState = WhatsAppState.ChoosingSlot;
                                await SaveSessionAsync(session, context, cancellationToken);
                                if (!string.IsNullOrEmpty(session.SelectedLabId) && !string.IsNullOrEmpty(session.SelectedLabName))
                                {
                                    await SendSlotList(to, session.SelectedLabId, session.SelectedLabName, context, httpClientFactory, configuration, cancellationToken);
                                }
                            }
                            else
                            {
                                await SendTextMessage(to, $"❌ Sorry, only *{availCapacity}* spot(s) are available for this slot. Please enter a number between 1 and {availCapacity}, or type another number.", httpClientFactory, configuration);
                            }
                            break;
                        }

                        session.MemberCount  = count;
                        session.CurrentState = WhatsAppState.Confirm;
                        await SaveSessionAsync(session, context, cancellationToken);

                        var summary = await BuildBookingSummaryAsync(session, context, cancellationToken);
                        await SendTextMessage(to, $"📍 Address details registered!\n\n{summary}\n", httpClientFactory, configuration);
                        await SendPaymentRequest(to, session, context, httpClientFactory, configuration, cancellationToken);
                        await SendTextMessage(to, "👉 Once you complete the payment, reply with *DONE* or *PAY* to get your booking confirmation.", httpClientFactory, configuration);
                    }
                    else
                    {
                        await SendTextMessage(to, "Please enter a number between 1 and 6. How many people need the blood test?", httpClientFactory, configuration);
                    }
                    break;

                case WhatsAppState.Confirm:
                    if (lower == "done" || lower == "pay")
                    {
                        await VerifyPayment(to, session, context, httpClientFactory, configuration, cancellationToken);
                    }
                    else
                    {
                        await SendTextMessage(to, "Please complete the payment and reply with *DONE*.", httpClientFactory, configuration);
                    }
                    break;

                default:
                    await SendGreeting(to, httpClientFactory, configuration);
                    break;
            }
        }

        private async Task ProcessLocationMessage(
            string to, double lat, double lng,
            IApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var session = await GetOrCreateSessionAsync(to, context, cancellationToken);

            if (session.CurrentState == WhatsAppState.AwaitingLocation || session.CurrentState == WhatsAppState.Start)
            {
                var allBranches = await GetCachedBranchesAsync(context, cancellationToken);
                var nearbyBranches = allBranches
                    .Where(b => b.IsActive)
                    .Select(b => new { Branch = b, Distance = CalculateDistanceKm(lat, lng, (double)b.Latitude, (double)b.Longitude) })
                    .Where(x => x.Distance <= x.Branch.ServiceRangeKm)
                    .ToList();

                if (!nearbyBranches.Any())
                {
                    await SendTextMessage(to, "❌ Sorry, we do not offer home collection services in your location at this time.", httpClientFactory, configuration);
                    session.CurrentState = WhatsAppState.Start;
                    await SaveSessionAsync(session, context, cancellationToken);
                    await SendGreeting(to, httpClientFactory, configuration);
                    return;
                }

                session.Latitude = lat;
                session.Longitude = lng;
                session.LocationShared = true;
                session.CurrentState = WhatsAppState.ChoosingTest;
                session.CartItemIds = new List<string>(); // Initialize empty cart
                await SaveSessionAsync(session, context, cancellationToken);

                await SendTextMessage(to, "📍 Location received successfully!", httpClientFactory, configuration);

                var nearbyBranchIds = nearbyBranches.Select(x => x.Branch.Id).ToHashSet();
                var branchServices = await context.BranchServices
                    .Where(bs => bs.IsActive && nearbyBranchIds.Contains(bs.BranchId))
                    .Select(bs => bs.ServiceId)
                    .Distinct()
                    .ToListAsync(cancellationToken);

                var branchPackages = await context.BranchPackages
                    .Where(bp => bp.IsActive && nearbyBranchIds.Contains(bp.BranchId))
                    .Select(bp => bp.PackageId)
                    .Distinct()
                    .ToListAsync(cancellationToken);

                var allServices = await GetCachedServicesAsync(context, cancellationToken);
                var availableServices = allServices.Where(s => branchServices.Contains(s.Id)).ToList();

                var allPackages = await context.Packages.AsNoTracking().Where(p => p.IsActive).ToListAsync(cancellationToken);
                var availablePackages = allPackages.Where(p => branchPackages.Contains(p.Id)).ToList();

                if (!availableServices.Any() && !availablePackages.Any())
                {
                    await SendTextMessage(to, "❌ Sorry, no diagnostic services or health packages are available near your location at this time.", httpClientFactory, configuration);
                    session.CurrentState = WhatsAppState.Start;
                    await SaveSessionAsync(session, context, cancellationToken);
                    await SendGreeting(to, httpClientFactory, configuration);
                    return;
                }

                await SendOptionsList(to, availableServices, availablePackages, httpClientFactory, configuration);
            }
        }

        private async Task ProcessInteractiveReply(
            string to, string replyId, string replyTitle,
            IApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var session = await GetOrCreateSessionAsync(to, context, cancellationToken);

            _logger.LogInformation("🧭 Interactive reply received: From={From} | State={State} | ReplyId={ReplyId} | ReplyTitle={ReplyTitle}",
                to, session.CurrentState, replyId ?? "(null)", replyTitle ?? "(null)");

            if (replyId != null && replyId.StartsWith("cancel_id_"))
            {
                var apptId = replyId.Replace("cancel_id_", "");
                await CancelAppointmentOnWhatsApp(to, apptId, context, httpClientFactory, configuration, cancellationToken);
                return;
            }

            switch (replyId)
            {
                case "menu_book":
                    session.CurrentState = WhatsAppState.AwaitingLocation;
                    session.CartItemIds = new List<string>();
                    await SaveSessionAsync(session, context, cancellationToken);
                    await SendLocationRequest(to, httpClientFactory, configuration);
                    return;

                case "menu_bookings":
                    await SendViewBookings(to, context, httpClientFactory, configuration, cancellationToken);
                    await SendBookingOptionsAfterList(to, context, httpClientFactory, configuration, cancellationToken);
                    return;

                case "cancel_select_appt":
                    await SendActiveBookingsForCancellation(to, context, httpClientFactory, configuration, cancellationToken);
                    return;

                case "menu_help":
                    session.CurrentState = WhatsAppState.Start;
                    await SaveSessionAsync(session, context, cancellationToken);
                    await SendGreeting(to, httpClientFactory, configuration);
                    return;

                case "cart_add_more":
                    session.CurrentState = WhatsAppState.ChoosingTest;
                    await SaveSessionAsync(session, context, cancellationToken);
                    
                    var allBranches = await GetCachedBranchesAsync(context, cancellationToken);
                    var nearbyBranches = allBranches
                        .Where(b => b.IsActive)
                        .Select(b => new { Branch = b, Distance = CalculateDistanceKm(session.Latitude ?? 0.0, session.Longitude ?? 0.0, (double)b.Latitude, (double)b.Longitude) })
                        .Where(x => x.Distance <= x.Branch.ServiceRangeKm)
                        .ToList();
                    var nearbyBranchIds = nearbyBranches.Select(x => x.Branch.Id).ToHashSet();
                    
                    var bsIds = await context.BranchServices.Where(bs => bs.IsActive && nearbyBranchIds.Contains(bs.BranchId)).Select(bs => bs.ServiceId).Distinct().ToListAsync(cancellationToken);
                    var bpIds = await context.BranchPackages.Where(bp => bp.IsActive && nearbyBranchIds.Contains(bp.BranchId)).Select(bp => bp.PackageId).Distinct().ToListAsync(cancellationToken);

                    var svcs = (await GetCachedServicesAsync(context, cancellationToken)).Where(s => bsIds.Contains(s.Id)).ToList();
                    var pkgs = (await context.Packages.AsNoTracking().Where(p => p.IsActive).ToListAsync(cancellationToken)).Where(p => bpIds.Contains(p.Id)).ToList();

                    await SendOptionsList(to, svcs, pkgs, httpClientFactory, configuration);
                    return;

                case "cart_clear":
                    session.CartItemIds = new List<string>();
                    await SaveSessionAsync(session, context, cancellationToken);
                    await SendTextMessage(to, "🗑️ Your shopping cart has been cleared.", httpClientFactory, configuration);
                    
                    var bList = await GetCachedBranchesAsync(context, cancellationToken);
                    var nbList = bList
                        .Where(b => b.IsActive)
                        .Select(b => new { Branch = b, Distance = CalculateDistanceKm(session.Latitude ?? 0.0, session.Longitude ?? 0.0, (double)b.Latitude, (double)b.Longitude) })
                        .Where(x => x.Distance <= x.Branch.ServiceRangeKm)
                        .ToList();
                    var nbBranchIds = nbList.Select(x => x.Branch.Id).ToHashSet();
                    
                    var svIds = await context.BranchServices.Where(bs => bs.IsActive && nbBranchIds.Contains(bs.BranchId)).Select(bs => bs.ServiceId).Distinct().ToListAsync(cancellationToken);
                    var paIds = await context.BranchPackages.Where(bp => bp.IsActive && nbBranchIds.Contains(bp.BranchId)).Select(bp => bp.PackageId).Distinct().ToListAsync(cancellationToken);

                    var allSvcs = (await GetCachedServicesAsync(context, cancellationToken)).Where(s => svIds.Contains(s.Id)).ToList();
                    var allPkgs = (await context.Packages.AsNoTracking().Where(p => p.IsActive).ToListAsync(cancellationToken)).Where(p => paIds.Contains(p.Id)).ToList();

                    await SendOptionsList(to, allSvcs, allPkgs, httpClientFactory, configuration);
                    return;

                case "cart_checkout":
                    if (session.CartItemIds == null || !session.CartItemIds.Any())
                    {
                        await SendTextMessage(to, "⚠️ Your cart is empty. Please select a service first.", httpClientFactory, configuration);
                        return;
                    }
                    session.SelectedTestId = string.Join(",", session.CartItemIds);
                    session.CurrentState = WhatsAppState.AwaitingAddressDetails;
                    await SaveSessionAsync(session, context, cancellationToken);
                    await SendTextMessage(to, "📝 Please reply with your address details: Building name/number, floor, and landmark.\n\n(e.g., 'Flat 202, 2nd Floor, next to SBI Bank')", httpClientFactory, configuration);
                    return;
            }

            switch (session.CurrentState)
            {
                case WhatsAppState.ChoosingTest:
                    var allServices = await GetCachedServicesAsync(context, cancellationToken);
                    var allPackages = await context.Packages.AsNoTracking().Where(p => p.IsActive).ToListAsync(cancellationToken);

                    var selectedService = allServices.FirstOrDefault(s => s.Id == replyId);
                    var selectedPackage = allPackages.FirstOrDefault(p => p.Id == replyId);

                    if (selectedService != null || selectedPackage != null)
                    {
                        var name = selectedService?.Name ?? selectedPackage?.Name ?? "Item";
                        session.SelectedTestId = replyId;
                        session.CurrentState = WhatsAppState.AwaitingItemQuantity;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendItemQuantityPrompt(to, name, httpClientFactory, configuration);
                    }
                    else
                    {
                        session.CurrentState = WhatsAppState.Start;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendGreeting(to, httpClientFactory, configuration);
                    }
                    break;

                case WhatsAppState.AwaitingItemQuantity:
                    if (replyId.StartsWith("qty_count_"))
                    {
                        var qtyStr = replyId.Replace("qty_count_", "");
                        if (int.TryParse(qtyStr, out int qty) && qty >= 1 && qty <= 6)
                        {
                            var itemId = session.SelectedTestId;
                            if (!string.IsNullOrEmpty(itemId))
                            {
                                if (session.CartItemIds == null) session.CartItemIds = new List<string>();
                                session.CartItemIds.RemoveAll(id => id.StartsWith(itemId + ":"));
                                session.CartItemIds.Add($"{itemId}:{qty}");
                                session.SelectedTestId = null;
                            }
                            session.CurrentState = WhatsAppState.ChoosingTest;
                            await SaveSessionAsync(session, context, cancellationToken);
                            await SendCartOptions(to, session, context, httpClientFactory, configuration, cancellationToken);
                        }
                    }
                    else
                    {
                        var name = await GetItemNameById(session.SelectedTestId, context, cancellationToken);
                        await SendItemQuantityPrompt(to, name ?? "selected service", httpClientFactory, configuration);
                    }
                    break;

                case WhatsAppState.ChoosingLab:
                    var allBranches = await GetCachedBranchesAsync(context, cancellationToken);
                    var selectedLab = allBranches.FirstOrDefault(b => b.Id == replyId);
                    if (selectedLab != null)
                    {
                        session.SelectedLabId = selectedLab.Id;
                        session.SelectedLabName = selectedLab.Name;
                        session.CurrentState = WhatsAppState.ChoosingSlot;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendSlotList(to, selectedLab.Id, selectedLab.Name, context, httpClientFactory, configuration, cancellationToken);
                    }
                    break;

                case WhatsAppState.ChoosingSlot:
                    var slot = await context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == replyId, cancellationToken);
                    if (slot != null)
                    {
                        if (!slot.IsAvailable || slot.BookedCount >= slot.MaxCapacity)
                        {
                            await SendTextMessage(to, "⚠️ Sorry, that slot was just booked by another person. Please select a different slot.", httpClientFactory, configuration);
                            if (!string.IsNullOrEmpty(session.SelectedLabId) && !string.IsNullOrEmpty(session.SelectedLabName))
                            {
                                await SendSlotList(to, session.SelectedLabId, session.SelectedLabName, context, httpClientFactory, configuration, cancellationToken);
                            }
                        }
                        else
                        {
                            session.SelectedSlot = slot.Id;
                            
                            // Auto-compute memberCount from maximum service quantity
                            var maxCount = 1;
                            if (session.CartItemIds != null && session.CartItemIds.Any())
                            {
                                var qtys = session.CartItemIds
                                    .Select(id => id.Split(':'))
                                    .Select(parts => parts.Length > 1 && int.TryParse(parts[1], out var q) ? q : 1);
                                maxCount = qtys.Any() ? qtys.Max() : 1;
                            }

                            session.MemberCount = maxCount;
                            session.CurrentState = WhatsAppState.Confirm;
                            await SaveSessionAsync(session, context, cancellationToken);
                            
                            var summary = await BuildBookingSummaryAsync(session, context, cancellationToken);
                            await SendTextMessage(to, $"📅 Slot confirmed!\n\n{summary}\n", httpClientFactory, configuration);
                            await SendPaymentRequest(to, session, context, httpClientFactory, configuration, cancellationToken);
                            await SendTextMessage(to, "👉 Once you complete the payment, reply with *DONE* or *PAY* to get your booking confirmation.", httpClientFactory, configuration);
                        }
                    }
                    break;

                case WhatsAppState.MemberCount:
                    if (replyId.StartsWith("member_count_"))
                    {
                        var countStr = replyId.Replace("member_count_", "");
                        if (int.TryParse(countStr, out int count))
                        {
                            var maxAllowed = 6;
                            if (!string.IsNullOrEmpty(session.SelectedSlot))
                            {
                                var slotObj = await context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == session.SelectedSlot, cancellationToken);
                                if (slotObj != null)
                                {
                                    maxAllowed = slotObj.MaxCapacity - slotObj.BookedCount;
                                }
                            }

                            if (count <= maxAllowed && count > 0)
                            {
                                session.MemberCount  = count;
                                session.CurrentState = WhatsAppState.Confirm;
                                await SaveSessionAsync(session, context, cancellationToken);

                                var summary = await BuildBookingSummaryAsync(session, context, cancellationToken);
                                await SendTextMessage(to, $"📍 Address details registered!\n\n{summary}\n", httpClientFactory, configuration);
                                await SendPaymentRequest(to, session, context, httpClientFactory, configuration, cancellationToken);
                                await SendTextMessage(to, "👉 Once you complete the payment, reply with *DONE* or *PAY* to get your booking confirmation.", httpClientFactory, configuration);
                            }
                            else
                            {
                                await SendTextMessage(to, $"❌ Sorry, only *{maxAllowed}* spot(s) are available for this slot. Please select a valid number.", httpClientFactory, configuration);
                                await SendPersonCountPrompt(to, session, context, httpClientFactory, configuration, cancellationToken);
                            }
                        }
                    }
                    break;

                default:
                    session.CurrentState = WhatsAppState.Start;
                    await SaveSessionAsync(session, context, cancellationToken);
                    await SendGreeting(to, httpClientFactory, configuration);
                    break;
            }
        }

        private async Task SendOptionsList(string to, List<Service> services, List<Package> packages, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            var serviceRows = services.Take(10).Select(s => new
            {
                id          = s.Id,
                title       = s.Name.Length > 24 ? s.Name[..24] : s.Name,
                description = s.Description != null && s.Description.Length > 72
                                ? s.Description[..72]
                                : s.Description ?? $"₹{s.BasePrice} · {s.Category}"
            }).ToArray();

            var packageRows = packages.Take(10).Select(p => new
            {
                id          = p.Id,
                title       = p.Name.Length > 24 ? p.Name[..24] : p.Name,
                description = p.Description != null && p.Description.Length > 72
                                ? p.Description[..72]
                                : p.Description ?? $"₹{p.BasePrice}"
            }).ToArray();

            var sectionsList = new List<object>();
            if (serviceRows.Any())
            {
                sectionsList.Add(new { title = "🧬 Diagnostic Services", rows = serviceRows });
            }
            if (packageRows.Any())
            {
                sectionsList.Add(new { title = "📦 Health Packages", rows = packageRows });
            }

            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type   = "list",
                    header = new { type = "text", text = "🔬 Choose Services" },
                    body   = new { text  = "Select from our diagnostic tests or custom health packages to add to your cart:" },
                    footer = new { text  = "LabCare · Accurate & Fast" },
                    action = new
                    {
                        button   = "Browse items",
                        sections = sectionsList.ToArray()
                    }
                }
            };
            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendCartOptions(
            string to, WhatsAppSession session,
            IApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var cartItemIds = session.CartItemIds ?? new List<string>();
            var allServices = await GetCachedServicesAsync(context, cancellationToken);
            var allPackages = await context.Packages.AsNoTracking().Where(p => p.IsActive).ToListAsync(cancellationToken);
            var cartNames = new List<string>();
            foreach (var itemWithQty in cartItemIds)
            {
                var parts = itemWithQty.Split(':');
                var itemId = parts[0];
                var qty = parts.Length > 1 ? parts[1] : "1";

                var s = allServices.FirstOrDefault(x => x.Id == itemId);
                if (s != null) cartNames.Add($"{s.Name} * {qty}");
                else
                {
                    var p = allPackages.FirstOrDefault(x => x.Id == itemId);
                    if (p != null) cartNames.Add($"{p.Name} * {qty}");
                }
            }

            var text = $"🛒 *Shopping Cart* ({cartItemIds.Count} items):\n" +
                       (cartItemIds.Any() ? string.Join("\n", cartNames.Select(n => "• " + n)) : "_Empty_") + "\n\n" +
                       "Choose an option below to proceed:";
            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type = "button",
                    body = new { text },
                    footer = new { text = "Apenir Diagnostics" },
                    action = new
                    {
                        buttons = new[]
                        {
                            new { type = "reply", reply = new { id = "cart_add_more", title = "➕ Add More" } },
                            new { type = "reply", reply = new { id = "cart_clear", title = "🗑️ Clear Cart" } },
                            new { type = "reply", reply = new { id = "cart_checkout", title = "💳 Checkout" } }
                        }
                    }
                }
            };
            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendGreeting(string to, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type   = "button",
                    header = new { type = "text", text = "🧬 LabCare Assistant" },
                    body   = new
                    {
                        text = "👋 Hello! Welcome to *LabCare*.\n\nI can help you book blood tests and diagnostics at top NABL-certified labs near you.\n\nWhat would you like to do?"
                    },
                    footer = new { text = "LabCare · Trusted Diagnostics" },
                    action = new
                    {
                        buttons = new[]
                        {
                            new { type = "reply", reply = new { id = "menu_book",     title = "📅 Book a test"  } },
                            new { type = "reply", reply = new { id = "menu_bookings", title = "📋 My bookings"  } },
                            new { type = "reply", reply = new { id = "menu_help",     title = "❓ Help"          } },
                        }
                    }
                }
            };
            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendServiceListForNearby(string to, List<Service> services, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            var top10 = services.Take(10).ToList();
            var rows = top10.Select(s => new
            {
                id          = s.Id,
                title       = s.Name.Length > 24 ? s.Name[..24] : s.Name,
                description = s.Description != null && s.Description.Length > 72
                                ? s.Description[..72]
                                : s.Description ?? $"₹{s.BasePrice} · {s.Category}"
            }).ToArray();

            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type   = "list",
                    header = new { type = "text", text = "🧬 Select a Service" },
                    body   = new { text  = "Choose the diagnostic test you would like to book:" },
                    footer = new { text  = "All tests are NABL certified" },
                    action = new
                    {
                        button   = "View services",
                        sections = new[]
                        {
                            new { title = "Available Services", rows }
                        }
                    }
                }
            };
            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendLabList(
            string to,
            WhatsAppSession session,
            IApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var cartItemIds = (session.SelectedTestId ?? "").Split(',').Select(id => id.Trim()).ToList();

            var allServices = await GetCachedServicesAsync(context, cancellationToken);
            var allPackages = await context.Packages.AsNoTracking().Where(p => p.IsActive).ToListAsync(cancellationToken);

            var branchServices = await context.BranchServices
                .Where(bs => cartItemIds.Contains(bs.ServiceId) && bs.IsActive)
                .ToListAsync(cancellationToken);

            var branchPackages = await context.BranchPackages
                .Where(bp => cartItemIds.Contains(bp.PackageId) && bp.IsActive)
                .ToListAsync(cancellationToken);

            var allBranches = await GetCachedBranchesAsync(context, cancellationToken);

            double userLat = session.Latitude ?? 0.0;
            double userLng = session.Longitude ?? 0.0;

            var nearbyBranches = allBranches
                .Where(b => b.IsActive)
                .Select(b => new { Branch = b, Distance = CalculateDistanceKm(userLat, userLng, (double)b.Latitude, (double)b.Longitude) })
                .Where(x => x.Distance <= x.Branch.ServiceRangeKm)
                .ToList();

            var eligibleBranches = new List<object>();
            foreach (var item in nearbyBranches)
            {
                var b = item.Branch;
                int offeredServicesCount = branchServices.Where(bs => bs.BranchId == b.Id).Select(bs => bs.ServiceId).Distinct().Count();
                int offeredPackagesCount = branchPackages.Where(bp => bp.BranchId == b.Id).Select(bp => bp.PackageId).Distinct().Count();
                
                int totalOfferedInCart = offeredServicesCount + offeredPackagesCount;
                if (totalOfferedInCart >= cartItemIds.Count)
                {
                    decimal totalPriceForBranch = 0m;
                    foreach (var itemId in cartItemIds)
                    {
                        var bs = branchServices.FirstOrDefault(x => x.BranchId == b.Id && x.ServiceId == itemId);
                        if (bs != null)
                        {
                            totalPriceForBranch += bs.CustomPrice ?? allServices.FirstOrDefault(s => s.Id == itemId)?.BasePrice ?? 0m;
                        }
                        else
                        {
                            var bp = branchPackages.FirstOrDefault(x => x.BranchId == b.Id && x.PackageId == itemId);
                            if (bp != null)
                            {
                                totalPriceForBranch += bp.CustomPrice ?? allPackages.FirstOrDefault(p => p.Id == itemId)?.BasePrice ?? 0m;
                            }
                        }
                    }

                    eligibleBranches.Add(new
                    {
                        Branch = b,
                        Distance = item.Distance,
                        Price = totalPriceForBranch
                    });
                }
            }

            if (!eligibleBranches.Any())
            {
                await SendTextMessage(to, "❌ Sorry, no labs offering your selected items are within range of your location. Please select different items.", httpClientFactory, configuration);
                session.CurrentState = WhatsAppState.Start;
                await SaveSessionAsync(session, context, cancellationToken);
                await SendGreeting(to, httpClientFactory, configuration);
                return;
            }

            var rows = new List<object>();
            foreach (dynamic item in eligibleBranches)
            {
                var b = item.Branch;
                rows.Add(new
                {
                    id          = b.Id,
                    title       = b.Name.Length > 24 ? b.Name[..24] : b.Name,
                    description = $"{b.City} · {item.Distance:F1} km away · ₹{item.Price}"
                });
            }

            var itemNames = allServices.Where(s => cartItemIds.Contains(s.Id)).Select(s => s.Name)
                .Concat(allPackages.Where(p => cartItemIds.Contains(p.Id)).Select(p => p.Name)).ToList();
            var itemNamesStr = string.Join(", ", itemNames);
            var bodyText = $"Select a nearby lab for *{(itemNamesStr.Length > 45 ? itemNamesStr[..45] + "..." : itemNamesStr)}*:";

            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type   = "list",
                    header = new { type = "text", text = "Available Labs" },
                    body   = new { text  = bodyText },
                    footer = new { text  = "NABL certified partners" },
                    action = new
                    {
                        button   = "View labs",
                        sections = new[]
                        {
                            new { title = "Labs Nearby", rows = rows.ToArray() }
                        }
                    }
                }
            };
            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendViewBookings(
            string to,
            IApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var user = await context.Users.FirstOrDefaultAsync(u => u.Phone == to, cancellationToken);
            if (user == null)
            {
                await SendTextMessage(to, "❌ You don't have any bookings yet.", httpClientFactory, configuration);
                return;
            }

            var appts = await context.Appointments
                .Where(a => a.CustomerUserId == user.Id)
                .OrderByDescending(a => a.CreatedAt)
                .Take(5)
                .ToListAsync(cancellationToken);

            var branchIds = appts.Select(a => a.BranchId).Distinct().ToList();
            var branches = await context.Branches
                .Where(b => branchIds.Contains(b.Id))
                .ToDictionaryAsync(b => b.Id, cancellationToken);

            var slotIds = appts.Select(a => a.AppointmentSlotId).Distinct().ToList();
            var slots = await context.AppointmentSlots
                .Where(s => slotIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, cancellationToken);

            foreach (var appt in appts)
            {
                if (branches.TryGetValue(appt.BranchId, out var branch))
                {
                    appt.Branch = branch;
                }
                if (slots.TryGetValue(appt.AppointmentSlotId, out var slot))
                {
                    appt.AppointmentSlot = slot;
                }
            }

            if (!appts.Any())
            {
                await SendTextMessage(to, "❌ You don't have any bookings yet.", httpClientFactory, configuration);
                return;
            }

            var text = "📋 *Your Recent Bookings:*\n\n";
            foreach (var a in appts)
            {
                var slotDisplay = a.AppointmentSlot != null
                    ? $"{a.AppointmentSlot.SlotDate:dd-MM-yyyy} @ {FormatTime(a.AppointmentSlot.StartTime)}"
                    : "Not scheduled";
                text += $"🆔 *{a.AppointmentNumber}*\n" +
                        $"🏥 Lab: {a.Branch?.Name ?? "Lab"}\n" +
                        $"📅 Slot: {slotDisplay}\n" +
                        $"💰 Amount: ₹{a.TotalAmount}\n" +
                        $"🚦 Status: *{a.Status}*\n" +
                        $"-----------------------\n";
            }

            await SendTextMessage(to, text, httpClientFactory, configuration);
        }

        private static bool IsGreeting(string text) =>
            new[] { "hi", "hello", "hey", "hii", "helo", "hai", "start", "menu",
                    "namaste", "good morning", "good evening", "howdy" }
            .Any(g => text.Contains(g));

        private async Task<string> BuildBookingSummaryAsync(
            WhatsAppSession session,
            IApplicationDbContext context,
            CancellationToken cancellationToken)
        {
            var itemIdsWithQty = (session.SelectedTestId ?? "").Split(',').Select(id => id.Trim()).ToList();
            var itemIds = itemIdsWithQty.Select(id => id.Split(':')[0]).ToList();
            var allServices = await GetCachedServicesAsync(context, cancellationToken);
            var allPackages = await context.Packages.AsNoTracking().Where(p => p.IsActive).ToListAsync(cancellationToken);

            var services = allServices.Where(s => itemIds.Contains(s.Id)).ToList();
            var packages = allPackages.Where(p => itemIds.Contains(p.Id)).ToList();

            var allBranches = await GetCachedBranchesAsync(context, cancellationToken);
            var lab         = allBranches.FirstOrDefault(b => b.Id == session.SelectedLabId);
            var labName     = lab?.Name ?? session.SelectedLabName ?? "Selected Lab";
            var labAddress  = lab != null ? $"{lab.City}, {lab.District}" : string.Empty;

            var slot       = await context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == session.SelectedSlot, cancellationToken);
            string slotDisplay = slot != null
                ? $"{slot.SlotDate:dddd, MMM dd yyyy} · {FormatTime(slot.StartTime)}"
                : session.SelectedSlot ?? "Selected Slot";

            var branchServices = await context.BranchServices
                .Where(bs => bs.BranchId == session.SelectedLabId && itemIds.Contains(bs.ServiceId) && bs.IsActive)
                .ToListAsync(cancellationToken);

            var branchPackages = await context.BranchPackages
                .Where(bp => bp.BranchId == session.SelectedLabId && itemIds.Contains(bp.PackageId) && bp.IsActive)
                .ToListAsync(cancellationToken);

            decimal rate = 0m;
            var serviceLinesSummary = new List<string>();

            foreach (var itemWithQty in itemIdsWithQty)
            {
                var parts = itemWithQty.Split(':');
                var itemId = parts[0];
                var qty = parts.Length > 1 && int.TryParse(parts[1], out var q) ? q : 1;

                var s = services.FirstOrDefault(x => x.Id == itemId);
                decimal basePrice = 0m;
                decimal? originalPrice = null;
                string name = "Item";

                if (s != null)
                {
                    name = s.Name;
                    var bs = branchServices.FirstOrDefault(x => x.ServiceId == itemId);
                    basePrice = bs?.CustomPrice ?? s.BasePrice;
                    originalPrice = bs?.CustomOriginalPrice ?? s.OriginalPrice;
                }
                else
                {
                    var p = packages.FirstOrDefault(x => x.Id == itemId);
                    if (p != null)
                    {
                        name = p.Name;
                        var bp = branchPackages.FirstOrDefault(x => x.PackageId == itemId);
                        basePrice = bp?.CustomPrice ?? p.BasePrice;
                        originalPrice = bp?.CustomOriginalPrice ?? p.OriginalPrice;
                    }
                }

                // First copy pays full basePrice, subsequent copies get 20% discount (i.e. 80% price)
                var cost = basePrice + (qty > 1 ? (qty - 1) * basePrice * 0.8m : 0m);
                rate += cost;

                if (originalPrice.HasValue && originalPrice.Value > basePrice)
                {
                    var totalOrig = originalPrice.Value * qty;
                    serviceLinesSummary.Add($"• {name} * {qty} = ~₹{Math.Round(totalOrig)}~ ₹{Math.Round(cost)}");
                }
                else
                {
                    serviceLinesSummary.Add($"• {name} * {qty} = ₹{Math.Round(cost)}");
                }
            }

            int total = (int)Math.Round(rate);
            var itemNamesStr = string.Join("\n", serviceLinesSummary);

            return
                $"📋 *Booking Summary*\n\n" +
                $"🧪 *Services:*\n{itemNamesStr}\n\n" +
                $"🏥 Lab: {labName}\n" +
                (!string.IsNullOrEmpty(labAddress) ? $"📍 {labAddress}\n" : "") +
                $"📅 Date & Time: {slotDisplay}\n" +
                $"👥 {session.MemberCount} person{(session.MemberCount > 1 ? "s" : "")}\n" +
                $"💰 Total: ₹{total}\n" +
                $"🏠 Address: {session.BuildingDetails}\n" +
                $"📍 Location: Shared ✓";
        }

        private async Task GenerateSlotsForNext7Days(string branchId, IApplicationDbContext context, CancellationToken cancellationToken)
        {
            var nowIst = DateTime.UtcNow.AddHours(5).AddMinutes(30);
            var today = DateOnly.FromDateTime(nowIst);
            var configs = await context.BranchSlotConfigurations
                .Where(c => c.BranchId == branchId)
                .ToListAsync(cancellationToken);

            if (!configs.Any()) return;

            var existingSlots = await context.AppointmentSlots
                .Where(s => s.BranchId == branchId && s.SlotDate >= today)
                .ToListAsync(cancellationToken);

            var newSlots = new List<AppointmentSlot>();

            for (int i = 0; i < 7; i++)
            {
                var date = today.AddDays(i);
                var dayOfWeek = date.DayOfWeek;
                DayText dayEnum = dayOfWeek switch
                {
                    DayOfWeek.Monday => DayText.Mon,
                    DayOfWeek.Tuesday => DayText.Tue,
                    DayOfWeek.Wednesday => DayText.Wed,
                    DayOfWeek.Thursday => DayText.Thu,
                    DayOfWeek.Friday => DayText.Fri,
                    DayOfWeek.Saturday => DayText.Sat,
                    DayOfWeek.Sunday => DayText.Sun,
                    _ => DayText.Mon
                };

                var configsForDay = configs.Where(c => c.DayText == dayEnum).ToList();

                foreach (var config in configsForDay)
                {
                    var exists = existingSlots.Any(s => s.SlotDate == date && s.StartTime == config.StartTime);
                    if (!exists)
                    {
                        newSlots.Add(new AppointmentSlot
                        {
                            Id = Guid.NewGuid().ToString(),
                            BranchId = branchId,
                            SlotDate = date,
                            StartTime = config.StartTime,
                            EndTime = config.EndTime,
                            MaxCapacity = config.MaxCapacity,
                            BookedCount = 0,
                            IsAvailable = !config.IsLeave
                        });
                    }
                }
            }

            if (newSlots.Any())
            {
                context.AppointmentSlots.AddRange(newSlots);
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        private async Task SendSlotList(
            string to, string labId, string labName,
            IApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var nowIst = DateTime.UtcNow.AddHours(5).AddMinutes(30);
            var todayIst = DateOnly.FromDateTime(nowIst);
            var timeOnlyIst = TimeOnly.FromDateTime(nowIst);

            await GenerateSlotsForNext7Days(labId, context, cancellationToken);

            var rawSlots = await context.AppointmentSlots
                .Where(s => s.BranchId == labId && s.IsAvailable && s.SlotDate >= todayIst)
                .OrderBy(s => s.SlotDate).ThenBy(s => s.StartTime)
                .ToListAsync(cancellationToken);

            var slots = rawSlots
                .Where(s => s.SlotDate > todayIst || (s.SlotDate == todayIst && s.StartTime > timeOnlyIst))
                .Take(10)
                .ToList();

            if (!slots.Any())
            {
                await SendTextMessage(to, $"Sorry, there are no available slots for *{labName}* in the upcoming days. Please choose another lab.", httpClientFactory, configuration);
                return;
            }

            var rows = slots.Select(s => new
            {
                id          = s.Id,
                title       = $"{s.SlotDate:MMM dd} {FormatTime(s.StartTime)}",
                description = $"Available: {s.MaxCapacity - s.BookedCount} spot(s)"
            }).ToArray();

            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type   = "list",
                    header = new { type = "text", text = "Select a Time Slot" },
                    body   = new { text  = $"📅 Available slots for *{labName}*\n\nChoose an appointment time:" },
                    footer = new { text  = "Fasting required for blood tests" },
                    action = new
                    {
                        button   = "View slots",
                        sections = new[]
                        {
                            new { title = "Available slots", rows }
                        }
                    }
                }
            };
            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendPersonCountPrompt(
            string to,
            WhatsAppSession session,
            IApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var maxAllowed = 6;
            if (!string.IsNullOrEmpty(session.SelectedSlot))
            {
                var slot = await context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == session.SelectedSlot, cancellationToken);
                if (slot != null)
                {
                    var avail = slot.MaxCapacity - slot.BookedCount;
                    if (avail < maxAllowed)
                    {
                        maxAllowed = avail > 0 ? avail : 1;
                    }
                }
            }

            var rows = new System.Collections.Generic.List<object>();
            for(int i = 1; i <= maxAllowed; i++)
            {
                rows.Add(new {
                    id = $"member_count_{i}",
                    title = $"{i} Person{(i > 1 ? "s" : "")}",
                    description = $"Book for {i} person{(i > 1 ? "s" : "")}"
                });
            }

            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type = "list",
                    header = new { type = "text", text = "Number of Persons" },
                    body = new { text = $"👥 *How many people need the blood test?*\n\nThis slot has *{maxAllowed}* spot(s) available.\nChoose the number of people." },
                    footer = new { text = "Select an option below" },
                    action = new
                    {
                        button = "Select persons",
                        sections = new[]
                        {
                            new { title = "Number of Persons", rows = rows.ToArray() }
                        }
                    }
                }
            };
            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendLocationRequest(string to, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            await SendTextMessage(to,
                "📍 *Share your location*\n\n" +
                "Please tap the paperclip 📎 → Location → and share your current location.\n\n" +
                "This helps us check the nearest branch and offer services near you.",
                httpClientFactory, configuration);
        }

        private async Task SendPaymentRequest(
            string to,
            WhatsAppSession session,
            IApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var rzpKeyId     = configuration["Razorpay:KeyId"];
            var rzpKeySecret = configuration["Razorpay:KeySecret"];

            var itemIdsWithQty = (session.SelectedTestId ?? "").Split(',').Select(id => id.Trim()).ToList();
            var itemIds = itemIdsWithQty.Select(id => id.Split(':')[0]).ToList();
            var allServices = await GetCachedServicesAsync(context, cancellationToken);
            var allPackages = await context.Packages.AsNoTracking().Where(p => p.IsActive).ToListAsync(cancellationToken);

            var services = allServices.Where(s => itemIds.Contains(s.Id)).ToList();
            var packages = allPackages.Where(p => itemIds.Contains(p.Id)).ToList();

            var branchServices = await context.BranchServices
                .Where(bs => bs.BranchId == session.SelectedLabId && itemIds.Contains(bs.ServiceId) && bs.IsActive)
                .ToListAsync(cancellationToken);

            var branchPackages = await context.BranchPackages
                .Where(bp => bp.BranchId == session.SelectedLabId && itemIds.Contains(bp.PackageId) && bp.IsActive)
                .ToListAsync(cancellationToken);

            decimal rate = 0m;
            foreach (var itemWithQty in itemIdsWithQty)
            {
                var parts = itemWithQty.Split(':');
                var itemId = parts[0];
                var qty = parts.Length > 1 && int.TryParse(parts[1], out var q) ? q : 1;

                var s = services.FirstOrDefault(x => x.Id == itemId);
                decimal basePrice = 0m;
                if (s != null)
                {
                    var bs = branchServices.FirstOrDefault(x => x.ServiceId == itemId);
                    basePrice = bs?.CustomPrice ?? s.BasePrice;
                }
                else
                {
                    var p = packages.FirstOrDefault(x => x.Id == itemId);
                    if (p != null)
                    {
                        var bp = branchPackages.FirstOrDefault(x => x.PackageId == itemId);
                        basePrice = bp?.CustomPrice ?? p.BasePrice;
                    }
                }

                // First copy pays full basePrice, subsequent copies get 20% discount (i.e. 80% price)
                var cost = basePrice + (qty > 1 ? (qty - 1) * basePrice * 0.8m : 0m);
                rate += cost;
            }

            var allBranches = await GetCachedBranchesAsync(context, cancellationToken);
            var lab         = allBranches.FirstOrDefault(b => b.Id == session.SelectedLabId);
            var labName     = lab?.Name ?? session.SelectedLabName ?? "Lab";

            var user         = await context.Users.FirstOrDefaultAsync(u => u.Phone == to, cancellationToken);
            var customerName = user?.Name ?? "Customer";

            int total = (int)Math.Round(rate);

            var itemNames = services.Select(s => s.Name).Concat(packages.Select(p => p.Name)).ToList();
            var itemNamesStr = string.Join(", ", itemNames);

            string paymentUrl = "https://rzp.io/i/example";
            try
            {
                var client     = httpClientFactory.CreateClient();
                var authString = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{rzpKeyId}:{rzpKeySecret}"));
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authString);

                var rzpPayload = new
                {
                    amount         = total * 100,
                    currency       = "INR",
                    accept_partial = false,
                    description    = $"LabCare Booking: {itemNamesStr}",
                    customer       = new
                    {
                        name    = customerName,
                        contact = $"+{to}",
                    },
                    notify = new { sms = false, email = false },
                    reminder_enable = false,
                    notes = new
                    {
                        phone            = to,
                        lab              = labName,
                        selected_test_id = session.SelectedTestId ?? "",
                        selected_lab_id  = session.SelectedLabId ?? "",
                        selected_slot_id = session.SelectedSlot ?? "",
                        member_count     = session.MemberCount.ToString(),
                        member_selections = BuildMemberSelectionsNote(session),
                        building_details = session.BuildingDetails ?? "",
                        landmark         = session.Landmark ?? "",
                        floor            = session.Floor ?? "",
                        latitude         = (session.Latitude ?? 0.0).ToString(),
                        longitude        = (session.Longitude ?? 0.0).ToString()
                    }
                };

                var content  = new StringContent(JsonSerializer.Serialize(rzpPayload), Encoding.UTF8, "application/json");
                var response = await client.PostAsync("https://api.razorpay.com/v1/payment_links", content, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    using var rzpDoc = JsonDocument.Parse(responseBody);
                    paymentUrl = rzpDoc.RootElement.GetProperty("short_url").GetString() ?? paymentUrl;
                }
                else
                {
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError("Razorpay API Error: {Error}", errorBody);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create Razorpay payment link");
            }

            var waPayload = new
            {
                messaging_product = "whatsapp",
                recipient_type    = "individual",
                to,
                type = "interactive",
                interactive = new
                {
                    type   = "cta_url",
                    header = new { type = "text", text = "Payment Request" },
                    body   = new { text  = $"Please complete your payment of ₹{total} for *{labName}*.\n\nServices: {itemNamesStr}" },
                    footer = new { text  = "Secure payment by Razorpay" },
                    action = new
                    {
                        name       = "cta_url",
                        parameters = new
                        {
                            display_text = "Pay Now",
                            url          = paymentUrl
                        }
                    }
                }
            };
            await SendWhatsAppMessage(waPayload, httpClientFactory, configuration);
        }
        private async Task VerifyPayment(
            string to,
            WhatsAppSession session,
            IApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var user = await context.Users.FirstOrDefaultAsync(u => u.Phone == to, cancellationToken);
            if (user != null && !string.IsNullOrEmpty(session.SelectedSlot))
            {
                var appt = await context.Appointments
                    .FirstOrDefaultAsync(a => a.CustomerUserId == user.Id && a.AppointmentSlotId == session.SelectedSlot && a.Status == AppointmentStatus.Confirmed, cancellationToken);

                if (appt != null)
                {
                    session.CurrentState = WhatsAppState.Done;
                    await SaveSessionAsync(session, context, cancellationToken);
                    
                    var payload = new
                    {
                        messaging_product = "whatsapp",
                        to,
                        type = "interactive",
                        interactive = new
                        {
                            type   = "button",
                            body   = new { text = $"✅ We have already received your payment! Your booking ID is *{appt.AppointmentNumber}*." },
                            footer = new { text = "LabCare · Trusted Diagnostics" },
                            action = new
                            {
                                buttons = new[]
                                {
                                    new { type = "reply", reply = new { id = "menu_book",     title = "📅 Book another" } },
                                    new { type = "reply", reply = new { id = "menu_bookings", title = "📋 My bookings" } }
                                }
                            }
                        }
                    };
                    await SendWhatsAppMessage(payload, httpClientFactory, configuration);
                    return;
                }
            }

            await SendTextMessage(to, "⏳ We haven't received your payment confirmation yet. If you have already paid, please wait a moment for it to process. If your payment failed, please try paying again via the Razorpay link.", httpClientFactory, configuration);
        }
        private async Task SendHelp(string to, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            var helpText =
                "❓ *LabCare Help Center*\n\n" +
                "• *Fasting*: Most blood tests require fasting for 8–10 hours. Only drink water during this period.\n" +
                "• *Passcode*: Give the passcode to the collection staff member when they arrive.\n" +
                "• *Reports*: Delivered automatically to this WhatsApp thread once ready.\n\n" +
                "Need human support? Call us at *1800-111-222* (Mon-Sat, 9 AM - 6 PM).";

            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type   = "button",
                    body   = new { text = helpText },
                    footer = new { text = "LabCare · Trusted Diagnostics" },
                    action = new
                    {
                        buttons = new[]
                        {
                            new { type = "reply", reply = new { id = "menu_book", title = "📅 Book a test" } },
                            new { type = "reply", reply = new { id = "menu_help", title = "🏠 Main menu"    } },
                        }
                    }
                }
            };
            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendTextMessage(string to, string text, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "text",
                text = new { body = text }
            };
            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendWhatsAppMessage(object payload, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            try
            {
                var accessToken   = configuration["WhatsApp:AccessToken"] ?? string.Empty;
                var phoneNumberId = configuration["WhatsApp:PhoneNumberId"] ?? string.Empty;
                var apiVersion    = configuration["WhatsApp:ApiVersion"] ?? "v25.0";

                var client = httpClientFactory.CreateClient();
                var url    = $"https://graph.facebook.com/{apiVersion}/{phoneNumberId}/messages";
                var json   = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                var response     = await client.PostAsync(url, content);
                var responseBody = await response.Content.ReadAsStringAsync();

                _logger.LogInformation("📬 WA Response [{StatusCode}]: {ResponseBody}", (int)response.StatusCode, responseBody);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send WhatsApp message");
            }
        }

        private async Task<WhatsAppSession> GetOrCreateSessionAsync(
            string phone, IApplicationDbContext context, CancellationToken cancellationToken)
        {
            var session = await context.WhatsAppSessions.FirstOrDefaultAsync(s => s.Phone == phone, cancellationToken);
            if (session == null)
            {
                session = new WhatsAppSession
                {
                    Phone        = phone,
                    CurrentState = WhatsAppState.Start,
                    UpdatedAt    = DateTime.UtcNow
                };
                context.WhatsAppSessions.Add(session);
                await context.SaveChangesAsync(cancellationToken);
            }
            return session;
        }

        private async Task SendBookingOptionsAfterList(
            string to,
            IApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var user = await context.Users.FirstOrDefaultAsync(u => u.Phone == to, cancellationToken);
            if (user == null) return;

            var activeApptsCount = await context.Appointments
                .CountAsync(a => a.CustomerUserId == user.Id && a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.Completed, cancellationToken);

            var buttons = new List<object>
            {
                new { type = "reply", reply = new { id = "menu_book", title = "📅 Book a test" } }
            };

            if (activeApptsCount > 0)
            {
                buttons.Add(new { type = "reply", reply = new { id = "cancel_select_appt", title = "❌ Cancel Booking" } });
            }

            buttons.Add(new { type = "reply", reply = new { id = "menu_help", title = "🏠 Main Menu" } });

            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type = "button",
                    body = new { text = "What would you like to do next?" },
                    footer = new { text = "LabCare · Accurate & Fast" },
                    action = new
                    {
                        buttons = buttons.ToArray()
                    }
                }
            };

            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendActiveBookingsForCancellation(
            string to,
            IApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var user = await context.Users.FirstOrDefaultAsync(u => u.Phone == to, cancellationToken);
            if (user == null) return;

            var activeAppts = await context.Appointments
                .Where(a => a.CustomerUserId == user.Id && a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.Completed)
                .OrderByDescending(a => a.CreatedAt)
                .Take(10)
                .ToListAsync(cancellationToken);

            var branchIds = activeAppts.Select(a => a.BranchId).Distinct().ToList();
            var branches = await context.Branches
                .Where(b => branchIds.Contains(b.Id))
                .ToDictionaryAsync(b => b.Id, cancellationToken);

            var slotIds = activeAppts.Select(a => a.AppointmentSlotId).Distinct().ToList();
            var slots = await context.AppointmentSlots
                .Where(s => slotIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, cancellationToken);

            foreach (var appt in activeAppts)
            {
                if (branches.TryGetValue(appt.BranchId, out var branch))
                {
                    appt.Branch = branch;
                }
                if (slots.TryGetValue(appt.AppointmentSlotId, out var slot))
                {
                    appt.AppointmentSlot = slot;
                }
            }

            if (!activeAppts.Any())
            {
                await SendTextMessage(to, "❌ You do not have any active bookings that can be cancelled.", httpClientFactory, configuration);
                return;
            }

            var rows = activeAppts.Select(a => {
                var dateStr = a.AppointmentSlot != null ? a.AppointmentSlot.SlotDate.ToString("dd MMM") : "N/A";
                var timeStr = a.AppointmentSlot != null ? a.AppointmentSlot.StartTime.ToString("hh:mm tt") : "N/A";
                return new
                {
                    id = $"cancel_id_{a.Id}",
                    title = a.AppointmentNumber,
                    description = $"{a.Branch?.Name ?? "Lab"} · {dateStr} @ {timeStr}"
                };
            }).ToArray();

            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type = "list",
                    header = new { type = "text", text = "Cancel Booking" },
                    body = new { text = "Select the booking you wish to cancel:" },
                    footer = new { text = "This releases slot capacity" },
                    action = new
                    {
                        button = "Select booking",
                        sections = new[]
                        {
                            new { title = "Active Bookings", rows }
                        }
                    }
                }
            };

            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task CancelAppointmentOnWhatsApp(
            string to,
            string appointmentId,
            IApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var appointment = await context.Appointments
                .Include(a => a.AppointmentSlot)
                .FirstOrDefaultAsync(a => a.Id == appointmentId, cancellationToken);

            if (appointment == null)
            {
                await SendTextMessage(to, "❌ Booking not found.", httpClientFactory, configuration);
                return;
            }

            if (appointment.Status == AppointmentStatus.Cancelled)
            {
                await SendTextMessage(to, $"⚠️ Booking *{appointment.AppointmentNumber}* is already cancelled.", httpClientFactory, configuration);
                return;
            }

            if (appointment.AppointmentSlot != null)
            {
                var slot = appointment.AppointmentSlot;
                slot.BookedCount = Math.Max(0, slot.BookedCount - appointment.MemberCount);
                slot.IsAvailable = true;
                context.AppointmentSlots.Update(slot);
            }

            appointment.Status = AppointmentStatus.Cancelled;
            context.Appointments.Update(appointment);
            await context.SaveChangesAsync(cancellationToken);

            await SendTextMessage(to, $"✅ Booking *{appointment.AppointmentNumber}* has been successfully cancelled. The reserved slot has been released.", httpClientFactory, configuration);
            
            var session = await GetOrCreateSessionAsync(to, context, cancellationToken);
            session.CurrentState = WhatsAppState.Start;
            await SaveSessionAsync(session, context, cancellationToken);
        }

        private async Task SendItemQuantityPrompt(
            string to,
            string itemName,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration)
        {
            var rows = new System.Collections.Generic.List<object>();
            for(int i = 1; i <= 6; i++)
            {
                rows.Add(new {
                    id = $"qty_count_{i}",
                    title = $"{i} Person{(i > 1 ? "s" : "")}",
                    description = $"{i} person{(i > 1 ? "s" : "")} need this test"
                });
            }

            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type = "list",
                    header = new { type = "text", text = "Select Quantity" },
                    body = new { text = $"👥 *How many people need {itemName}?*\n\nYou can book this test/package for up to 6 persons." },
                    action = new
                    {
                        button = "Choose count",
                        sections = new[]
                        {
                            new { title = "Select count", rows = rows.ToArray() }
                        }
                    }
                }
            };
            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task<string?> GetItemNameById(string? itemId, IApplicationDbContext context, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(itemId)) return null;
            var services = await GetCachedServicesAsync(context, cancellationToken);
            var s = services.FirstOrDefault(x => x.Id == itemId);
            if (s != null) return s.Name;
            var p = await context.Packages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == itemId, cancellationToken);
            return p?.Name;
        }

        private string BuildMemberSelectionsNote(WhatsAppSession session)
        {
            var cartItemIds = session.CartItemIds ?? new List<string>();
            if (!cartItemIds.Any()) return string.Empty;

            var qtys = cartItemIds
                .Select(id => id.Split(':'))
                .Select(parts => new {
                    Id = parts[0],
                    Qty = parts.Length > 1 && int.TryParse(parts[1], out var q) ? q : 1
                }).ToList();

            var maxCount = qtys.Any() ? qtys.Max(x => x.Qty) : 1;
            var selections = new List<string>();

            for (int i = 0; i < maxCount; i++)
            {
                var name = i == 0 ? "Self" : $"Member {i + 1}";
                var memberItems = qtys.Where(x => x.Qty > i).Select(x => x.Id).ToList();
                if (memberItems.Any())
                {
                    selections.Add($"{name}:{string.Join(",", memberItems)}");
                }
            }

            return string.Join(";", selections);
        }

        private async Task SaveSessionAsync(WhatsAppSession session, IApplicationDbContext context, CancellationToken cancellationToken)
        {
            session.UpdatedAt = DateTime.UtcNow;
            context.WhatsAppSessions.Update(session);
            await context.SaveChangesAsync(cancellationToken);
        }
    }
}
