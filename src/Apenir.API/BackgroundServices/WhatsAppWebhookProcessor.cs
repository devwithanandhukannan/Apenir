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

                case WhatsAppState.AwaitingAddressDetails:
                    // Extract building details, floor, landmark from typed text.
                    // For optimal parsing, if they comma-separate, we save it cleanly.
                    session.BuildingDetails = text.Trim();
                    session.Landmark = "Indicated in address details";
                    session.Floor = "Not specified";
                    session.CurrentState = WhatsAppState.ChoosingLab;
                    await SaveSessionAsync(session, context, cancellationToken);

                    await SendLabList(to, session, context, httpClientFactory, configuration, cancellationToken);
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
                        await SimulatePayment(to, session, context, httpClientFactory, configuration, cancellationToken);
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
                await SaveSessionAsync(session, context, cancellationToken);

                await SendTextMessage(to, "📍 Location received successfully!", httpClientFactory, configuration);

                var nearbyBranchIds = nearbyBranches.Select(x => x.Branch.Id).ToHashSet();
                var branchServices = await context.BranchServices
                    .Where(bs => bs.IsActive && nearbyBranchIds.Contains(bs.BranchId))
                    .Select(bs => bs.ServiceId)
                    .Distinct()
                    .ToListAsync(cancellationToken);

                var allServices = await GetCachedServicesAsync(context, cancellationToken);
                var availableServices = allServices
                    .Where(s => branchServices.Contains(s.Id))
                    .ToList();

                if (!availableServices.Any())
                {
                    await SendTextMessage(to, "❌ Sorry, no diagnostic services are available near your location at this time.", httpClientFactory, configuration);
                    session.CurrentState = WhatsAppState.Start;
                    await SaveSessionAsync(session, context, cancellationToken);
                    await SendGreeting(to, httpClientFactory, configuration);
                    return;
                }

                await SendServiceListForNearby(to, availableServices, httpClientFactory, configuration);
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

            switch (replyId)
            {
                case "menu_book":
                    session.CurrentState = WhatsAppState.AwaitingLocation;
                    await SaveSessionAsync(session, context, cancellationToken);
                    await SendLocationRequest(to, httpClientFactory, configuration);
                    return;

                case "menu_bookings":
                    await SendViewBookings(to, context, httpClientFactory, configuration, cancellationToken);
                    return;

                case "menu_help":
                    session.CurrentState = WhatsAppState.Start;
                    await SaveSessionAsync(session, context, cancellationToken);
                    await SendGreeting(to, httpClientFactory, configuration);
                    return;
            }

            switch (session.CurrentState)
            {
                case WhatsAppState.ChoosingTest:
                    var allServices = await GetCachedServicesAsync(context, cancellationToken);
                    var selectedService = allServices.FirstOrDefault(s => s.Id == replyId);
                    if (selectedService != null)
                    {
                        session.SelectedTestId = selectedService.Id;
                        session.CurrentState = WhatsAppState.AwaitingAddressDetails;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendTextMessage(to, "📝 Please reply with your address details: Building name/number, floor, and landmark.\n\n(e.g., 'Flat 202, 2nd Floor, next to SBI Bank')", httpClientFactory, configuration);
                    }
                    else
                    {
                        session.CurrentState = WhatsAppState.Start;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendGreeting(to, httpClientFactory, configuration);
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
                            session.CurrentState = WhatsAppState.MemberCount;
                            await SaveSessionAsync(session, context, cancellationToken);
                            await SendPersonCountPrompt(to, session, context, httpClientFactory, configuration, cancellationToken);
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
            var serviceId = session.SelectedTestId ?? string.Empty;
            var allServices = await GetCachedServicesAsync(context, cancellationToken);
            var service = allServices.FirstOrDefault(s => s.Id == serviceId);
            var basePrice = service?.BasePrice ?? 0m;

            var branchServices = await context.BranchServices
                .Where(bs => bs.ServiceId == serviceId && bs.IsActive)
                .ToListAsync(cancellationToken);

            var eligibleBranchIds = branchServices.Select(bs => bs.BranchId).ToHashSet();
            var allBranches = await GetCachedBranchesAsync(context, cancellationToken);

            double userLat = session.Latitude ?? 0.0;
            double userLng = session.Longitude ?? 0.0;

            var nearbyBranches = allBranches
                .Where(b => b.IsActive && eligibleBranchIds.Contains(b.Id))
                .Select(b => new { Branch = b, Distance = CalculateDistanceKm(userLat, userLng, (double)b.Latitude, (double)b.Longitude) })
                .Where(x => x.Distance <= x.Branch.ServiceRangeKm)
                .ToList();

            if (!nearbyBranches.Any())
            {
                await SendTextMessage(to, "❌ Sorry, no labs offering this test are within range of your location. Please choose another test.", httpClientFactory, configuration);
                session.CurrentState = WhatsAppState.Start;
                await SaveSessionAsync(session, context, cancellationToken);
                await SendGreeting(to, httpClientFactory, configuration);
                return;
            }

            var rows = new List<object>();
            foreach (var item in nearbyBranches)
            {
                var b = item.Branch;
                var overridePrice = branchServices.FirstOrDefault(bs => bs.BranchId == b.Id)?.CustomPrice;
                var displayPrice = overridePrice ?? basePrice;

                rows.Add(new
                {
                    id          = b.Id,
                    title       = b.Name.Length > 24 ? b.Name[..24] : b.Name,
                    description = $"{b.City} · {item.Distance:F1} km away · ₹{displayPrice}"
                });
            }

            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type   = "list",
                    header = new { type = "text", text = "Available Labs" },
                    body   = new { text  = $"Select a nearby lab for *{service?.Name ?? "the test"}*:" },
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

        private async Task GenerateSlotsForNext7Days(string branchId, IApplicationDbContext context, CancellationToken cancellationToken)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
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
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            await GenerateSlotsForNext7Days(labId, context, cancellationToken);

            var slots = await context.AppointmentSlots
                .Where(s => s.BranchId == labId && s.IsAvailable && s.SlotDate >= today)
                .OrderBy(s => s.SlotDate).ThenBy(s => s.StartTime)
                .Take(10)
                .ToListAsync(cancellationToken);

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

            var allServices  = await GetCachedServicesAsync(context, cancellationToken);
            var service      = allServices.FirstOrDefault(s => s.Id == session.SelectedTestId);
            var basePrice    = service?.BasePrice ?? 0m;

            var branchService = await context.BranchServices
                .FirstOrDefaultAsync(bs =>
                    bs.BranchId  == session.SelectedLabId &&
                    bs.ServiceId == session.SelectedTestId &&
                    bs.IsActive, cancellationToken);
            if (branchService == null)
            {
                await SendTextMessage(to, "❌ Sorry, this test is no longer available at the selected lab branch.", httpClientFactory, configuration);
                session.CurrentState = WhatsAppState.Start;
                await SaveSessionAsync(session, context, cancellationToken);
                await SendGreeting(to, httpClientFactory, configuration);
                return;
            }
            decimal rate = branchService.CustomPrice ?? basePrice;

            var allBranches = await GetCachedBranchesAsync(context, cancellationToken);
            var lab         = allBranches.FirstOrDefault(b => b.Id == session.SelectedLabId);
            var labName     = lab?.Name ?? session.SelectedLabName ?? "Lab";

            var user         = await context.Users.FirstOrDefaultAsync(u => u.Phone == to, cancellationToken);
            var customerName = user?.Name ?? "Customer";

            int total = (int)rate + (session.MemberCount > 1
                ? (int)Math.Round((session.MemberCount - 1) * rate * 0.8m)
                : 0);

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
                    description    = $"LabCare {service?.Name ?? "Booking"} Payment",
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
                    body   = new { text  = $"Please complete your payment of ₹{total} for *{labName}*.\n\nService: {service?.Name ?? "Diagnostic Test"}" },
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

        private async Task SimulatePayment(
            string to,
            WhatsAppSession session,
            IApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var slot = await context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == session.SelectedSlot, cancellationToken);
            if (slot == null || !slot.IsAvailable || slot.BookedCount + session.MemberCount > slot.MaxCapacity)
            {
                await SendTextMessage(to, "❌ Sorry, the slot is no longer available. Please select another slot.", httpClientFactory, configuration);
                session.CurrentState = WhatsAppState.ChoosingSlot;
                await SaveSessionAsync(session, context, cancellationToken);
                if (!string.IsNullOrEmpty(session.SelectedLabId) && !string.IsNullOrEmpty(session.SelectedLabName))
                {
                    await SendSlotList(to, session.SelectedLabId, session.SelectedLabName, context, httpClientFactory, configuration, cancellationToken);
                }
                return;
            }

            session.CurrentState = WhatsAppState.Done;
            await SaveSessionAsync(session, context, cancellationToken);

            var allServices = await GetCachedServicesAsync(context, cancellationToken);
            var service     = allServices.FirstOrDefault(s => s.Id == session.SelectedTestId);
            var basePrice   = service?.BasePrice ?? 0m;

            var branchService = await context.BranchServices
                .FirstOrDefaultAsync(bs =>
                    bs.BranchId  == session.SelectedLabId &&
                    bs.ServiceId == session.SelectedTestId &&
                    bs.IsActive, cancellationToken);
            if (branchService == null)
            {
                await SendTextMessage(to, "❌ Sorry, this test is no longer available at the selected lab branch.", httpClientFactory, configuration);
                session.CurrentState = WhatsAppState.Start;
                await SaveSessionAsync(session, context, cancellationToken);
                await SendGreeting(to, httpClientFactory, configuration);
                return;
            }
            decimal rate = branchService.CustomPrice ?? basePrice;

            var allBranches = await GetCachedBranchesAsync(context, cancellationToken);
            var lab         = allBranches.FirstOrDefault(b => b.Id == session.SelectedLabId);
            var labName     = lab?.Name ?? session.SelectedLabName ?? "Lab";

            int total = (int)rate + (session.MemberCount > 1
                ? (int)Math.Round((session.MemberCount - 1) * rate * 0.8m)
                : 0);

            string slotDisplay = "Confirmed Slot";
            slotDisplay = $"{slot.SlotDate:dddd, MMM dd yyyy} @ {FormatTime(slot.StartTime)}";
            slot.BookedCount += session.MemberCount;
            if (slot.BookedCount >= slot.MaxCapacity)
                slot.IsAvailable = false;
            context.AppointmentSlots.Update(slot);

            var user = await context.Users.FirstOrDefaultAsync(u => u.Phone == to, cancellationToken);
            if (user == null)
            {
                user = new User
                {
                    Id       = Guid.NewGuid().ToString(),
                    Name     = "WhatsApp User",
                    Phone    = to,
                    Role     = UserRole.Customer,
                    IsActive = true
                };
                context.Users.Add(user);
                await context.SaveChangesAsync(cancellationToken);
            }
            var customerName = string.IsNullOrWhiteSpace(user.Name) ? "Patient" : user.Name;

            var bookingId = $"BK-{DateTime.UtcNow:yyyyMMdd}-{new Random().Next(1000, 9999)}";

            var appointment = new Appointment
            {
                Id                 = Guid.NewGuid().ToString(),
                AppointmentNumber  = bookingId,
                CustomerUserId     = user.Id,
                BranchId           = session.SelectedLabId ?? string.Empty,
                AppointmentSlotId  = session.SelectedSlot  ?? string.Empty,
                LocationLatitude   = (decimal)(session.Latitude ?? 0.0),
                LocationLongitude  = (decimal)(session.Longitude ?? 0.0),
                LocationAddress    = $"{session.BuildingDetails}, Floor {session.Floor}, Landmark: {session.Landmark}",
                Landmark           = session.Landmark,
                BuildingDetails    = session.BuildingDetails,
                Floor              = session.Floor,
                Passcode           = new Random().Next(1000, 9999).ToString(),
                Status             = AppointmentStatus.Confirmed,
                TotalAmount        = total,
                PlatformCommission = total * (((branchService != null && branchService.CustomCommissionPct.HasValue) ? branchService.CustomCommissionPct.Value : (service != null ? service.PlatformCommissionPct : 15.00m)) / 100m),
                LabPayout  = total * (1m - ((branchService != null && branchService.CustomCommissionPct.HasValue) ? branchService.CustomCommissionPct.Value : (service != null ? service.PlatformCommissionPct : 15.00m)) / 100m),
                CreatedAt   = DateTime.UtcNow,
                MemberCount = session.MemberCount
            };
            context.Appointments.Add(appointment);

            for (int i = 0; i < session.MemberCount; i++)
            {
                var member = new AppointmentMember
                {
                    Id            = Guid.NewGuid().ToString(),
                    AppointmentId = appointment.Id,
                    MemberName    = i == 0 ? customerName : $"Member {i + 1}",
                    Age           = 0,
                    Gender        = Gender.Other,
                    Relationship  = i == 0 ? "Self" : "Family Member"
                };
                context.AppointmentMembers.Add(member);
            }

            var payment = new Payment
            {
                Id                 = Guid.NewGuid().ToString(),
                AppointmentId      = appointment.Id,
                RazorpayOrderId    = $"order_WA_{bookingId.Replace("-", "")}",
                RazorpayPaymentId  = $"pay_WA_{bookingId.Replace("-", "")}",
                Status             = PaymentStatus.Paid,
                PaymentMethod      = PaymentMethod.UPI,
                PaidAt             = DateTime.UtcNow,
                CreatedAt          = DateTime.UtcNow
            };
            context.Payments.Add(payment);

            await context.SaveChangesAsync(cancellationToken);

            // Notify Lab Owner if registered
            if (lab != null && !string.IsNullOrEmpty(lab.NotificationPhone))
            {
                var labNotification = $"🔔 *New Diagnostic Request!*\n\n" +
                                      $"Booking ID: *{bookingId}*\n" +
                                      $"Test: {service?.Name}\n" +
                                      $"Slot: {slotDisplay}\n" +
                                      $"Members: {session.MemberCount}\n" +
                                      $"Place: {appointment.LocationAddress}\n" +
                                      $"Customer Phone: +{to}";

                await SendTextMessage(lab.NotificationPhone, labNotification, httpClientFactory, configuration);
            }

            var confirmMsg =
                $"✅ *Booking Confirmed!*\n\n" +
                $"🆔 Booking ID: *{bookingId}*\n" +
                $"🩸 Service: {service?.Name ?? "Diagnostic Test"}\n" +
                $"🏥 Lab: {labName}\n" +
                $"📅 Date & Time: {slotDisplay}\n" +
                $"👥 Persons: {session.MemberCount}\n" +
                $"💰 Amount Paid: ₹{total}\n" +
                $"🔑 Passcode/OTP: *{appointment.Passcode}*\n\n" +
                $"🧪 *Instructions:*\n" +
                $"• Fast for 8-10 hours prior to sample collection.\n" +
                $"• Show the phlebotomist your Passcode (*{appointment.Passcode}*).\n" +
                $"• Report PDF will be sent to your WhatsApp on completion.\n\n" +
                $"Thank you for choosing LabCare! 🙏";

            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type   = "button",
                    body   = new { text = confirmMsg },
                    footer = new { text = "LabCare · Trusted Diagnostics" },
                    action = new
                    {
                        buttons = new[]
                        {
                            new { type = "reply", reply = new { id = "menu_book",     title = "📅 Book another" } },
                            new { type = "reply", reply = new { id = "menu_bookings", title = "📋 My bookings"  } },
                            new { type = "reply", reply = new { id = "menu_help",     title = "🏠 Main menu"    } },
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
                await SendTextMessage(to, "You don't have any bookings yet.\n\nReply *hi* to return to the main menu.", httpClientFactory, configuration);
                return;
            }

            var bookings = await context.Appointments
                .Where(a => a.CustomerUserId == user.Id)
                .OrderByDescending(a => a.CreatedAt)
                .Take(3)
                .ToListAsync(cancellationToken);

            if (!bookings.Any())
            {
                await SendTextMessage(to, "You don't have any bookings yet.\n\nReply *hi* to return to the main menu.", httpClientFactory, configuration);
                return;
            }

            var allServices = await GetCachedServicesAsync(context, cancellationToken);
            var allBranches = await GetCachedBranchesAsync(context, cancellationToken);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("📋 *Your Recent Bookings*\n");

            foreach (var b in bookings)
            {
                var service = allServices.FirstOrDefault(s => s.Id == b.AppointmentSlotId || s.Id == b.Id);
                var lab = allBranches.FirstOrDefault(br => br.Id == b.BranchId);

                sb.AppendLine($"🆔 Booking ID: *{b.AppointmentNumber}*");
                sb.AppendLine($"🩸 Status: *{b.Status}*");
                sb.AppendLine($"🏥 Lab: {lab?.Name ?? "Lab Partner"}");
                sb.AppendLine($"💰 Total: ₹{b.TotalAmount}");
                sb.AppendLine($"🔑 Passcode: *{b.Passcode}*");
                sb.AppendLine("────────────────");
            }

            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type   = "button",
                    body   = new { text = sb.ToString() },
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

        private async Task SaveSessionAsync(WhatsAppSession session, IApplicationDbContext context, CancellationToken cancellationToken)
        {
            session.UpdatedAt = DateTime.UtcNow;
            context.WhatsAppSessions.Update(session);
            await context.SaveChangesAsync(cancellationToken);
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
            var allServices = await GetCachedServicesAsync(context, cancellationToken);
            var service     = allServices.FirstOrDefault(s => s.Id == session.SelectedTestId);

            var allBranches = await GetCachedBranchesAsync(context, cancellationToken);
            var lab         = allBranches.FirstOrDefault(b => b.Id == session.SelectedLabId);
            var labName     = lab?.Name ?? session.SelectedLabName ?? "Selected Lab";
            var labAddress  = lab != null ? $"{lab.City}, {lab.District}" : string.Empty;

            var slot       = await context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == session.SelectedSlot, cancellationToken);
            string slotDisplay = slot != null
                ? $"{slot.SlotDate:dddd, MMM dd yyyy} · {FormatTime(slot.StartTime)}"
                : session.SelectedSlot ?? "Selected Slot";

            var basePrice     = service?.BasePrice ?? 0m;
            var branchService = await context.BranchServices
                .FirstOrDefaultAsync(bs =>
                    bs.BranchId  == session.SelectedLabId &&
                    bs.ServiceId == session.SelectedTestId &&
                    bs.IsActive, cancellationToken);
            decimal rate  = (branchService != null ? (branchService.CustomPrice ?? basePrice) : basePrice);
            int     total = (int)rate + (session.MemberCount > 1
                ? (int)Math.Round((session.MemberCount - 1) * rate * 0.8m)
                : 0);

            return
                $"📋 *Booking Summary*\n" +
                $"🩸 Service: {service?.Name ?? "Diagnostic Test"}\n" +
                $"🏥 Lab: {labName}\n" +
                (!string.IsNullOrEmpty(labAddress) ? $"📍 {labAddress}\n" : "") +
                $"📅 Date & Time: {slotDisplay}\n" +
                $"👥 {session.MemberCount} person{(session.MemberCount > 1 ? "s" : "")}\n" +
                $"💰 Total: ₹{total}\n" +
                $"🏠 Address: {session.BuildingDetails}\n" +
                $"📍 Location: Shared ✓";
        }
    }
}
