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

        // Cache durations
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
        private const string CacheKeyServices  = "wa_active_services";
        private const string CacheKeyBranches  = "wa_active_branches";

        // Formats a TimeOnly as "07:30 AM" — avoids broken hh\:mm escape in interpolated strings
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
                    // Normal shutdown
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred executing WhatsApp webhook processing.");
                }
            }

            _logger.LogInformation("WhatsApp Webhook Processor background service is stopping.");
        }

        // ─── Cache helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all active services, cached for 5 minutes.
        /// </summary>
        private async Task<List<Service>> GetCachedServicesAsync(IApplicationDbContext context, CancellationToken ct)
        {
            if (_cache.TryGetValue(CacheKeyServices, out List<Service>? cached) && cached != null)
                return cached;

            _logger.LogDebug("Cache miss – loading active services from DB.");
            var services = await context.Services
                .Where(s => s.IsActive)
                .OrderBy(s => s.Name)
                .ToListAsync(ct);

            _cache.Set(CacheKeyServices, services, CacheDuration);
            return services;
        }

        /// <summary>
        /// Returns all active branches, cached for 5 minutes.
        /// </summary>
        private async Task<List<Branch>> GetCachedBranchesAsync(IApplicationDbContext context, CancellationToken ct)
        {
            if (_cache.TryGetValue(CacheKeyBranches, out List<Branch>? cached) && cached != null)
                return cached;

            _logger.LogDebug("Cache miss – loading active branches from DB.");
            var branches = await context.Branches
                .Where(b => b.IsActive)
                .ToListAsync(ct);

            _cache.Set(CacheKeyBranches, branches, CacheDuration);
            return branches;
        }

        /// <summary>
        /// Returns distinct active districts that have at least one branch offering the given serviceId.
        /// Cached per service for 5 minutes.
        /// </summary>
        private async Task<List<string>> GetCachedDistrictsForServiceAsync(
            string serviceId, IApplicationDbContext context, CancellationToken ct)
        {
            var cacheKey = $"wa_districts_svc_{serviceId}";
            if (_cache.TryGetValue(cacheKey, out List<string>? cached) && cached != null)
                return cached;

            _logger.LogDebug("Cache miss – loading districts for service {ServiceId} from DB.", serviceId);

            // Only districts where at least one active branch offers this service
            var districts = await context.BranchServices
                .Where(bs => bs.ServiceId == serviceId && bs.IsActive)
                .Join(context.Branches,
                      bs => bs.BranchId,
                      b  => b.Id,
                      (bs, b) => b)
                .Where(b => b.IsActive)
                .Select(b => b.District.ToLower())
                .Distinct()
                .OrderBy(d => d)
                .ToListAsync(ct);

            _cache.Set(cacheKey, districts, CacheDuration);
            return districts;
        }

        // ─── Payload processing ──────────────────────────────────────────────────────

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

                            _logger.LogInformation("📩 Background processing message from: {From} | Type: {MsgType}", from, msgType);

                            // Auto-register customer if they don't exist yet
                            var user = await context.Users.FirstOrDefaultAsync(u => u.Phone == from, cancellationToken);
                            if (user == null)
                            {
                                _logger.LogInformation("👤 Creating new Customer user for phone {From} via WhatsApp auto-registration", from);
                                user = new User
                                {
                                    Phone = from,
                                    Name  = "WhatsApp User",
                                    Role  = UserRole.Customer
                                };
                                context.Users.Add(user);

                                var customer = new Customer
                                {
                                    UserId = user.Id,
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
                                else if (interactiveType == "list_reply")
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

                case WhatsAppState.MemberCount:
                    if (int.TryParse(text.Trim(), out int count) && count >= 1 && count <= 6)
                    {
                        session.MemberCount  = count;
                        session.CurrentState = WhatsAppState.Location;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendLocationRequest(to, httpClientFactory, configuration);
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

            if (session.CurrentState == WhatsAppState.Location)
            {
                session.LocationShared = true;
                session.CurrentState   = WhatsAppState.Confirm;
                await SaveSessionAsync(session, context, cancellationToken);

                var summary = await BuildBookingSummaryAsync(session, context, cancellationToken);
                await SendTextMessage(to,
                    $"📍 Location received!\n\n{summary}\n",
                    httpClientFactory, configuration);

                await SendPaymentRequest(to, session, context, httpClientFactory, configuration, cancellationToken);

                await SendTextMessage(to,
                    "👉 Once you complete the payment, reply with *DONE* to get your booking confirmation.",
                    httpClientFactory, configuration);
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

            switch (session.CurrentState)
            {
                case WhatsAppState.Start:
                    if (replyId == "menu_book")
                    {
                        session.CurrentState = WhatsAppState.ChoosingTest;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendServiceList(to, context, httpClientFactory, configuration, cancellationToken);
                    }
                    else if (replyId == "menu_bookings")
                    {
                        await SendViewBookings(to, context, httpClientFactory, configuration, cancellationToken);
                    }
                    else if (replyId == "menu_help")
                    {
                        await SendHelp(to, httpClientFactory, configuration);
                    }
                    break;

                case WhatsAppState.ChoosingTest:
                    // Look up from cache first
                    var allServices   = await GetCachedServicesAsync(context, cancellationToken);
                    var selectedService = allServices.FirstOrDefault(s => s.Id == replyId);
                    if (selectedService != null)
                    {
                        session.SelectedTestId = selectedService.Id;
                        session.CurrentState   = WhatsAppState.ChoosingCity;
                        await SaveSessionAsync(session, context, cancellationToken);
                        // Pass serviceId so city list is filtered by service availability
                        await SendCityList(to, selectedService.Id, context, httpClientFactory, configuration, cancellationToken);
                    }
                    else
                    {
                        await SendTextMessage(to, "Selected service is not active. Let's return to the main menu.", httpClientFactory, configuration);
                        session.CurrentState = WhatsAppState.Start;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendGreeting(to, httpClientFactory, configuration);
                    }
                    break;

                case WhatsAppState.ChoosingCity:
                    var city       = replyId.Replace("city_", "").ToLower();
                    // Validate the city has branches for the selected service (service-aware check)
                    var serviceDistricts = await GetCachedDistrictsForServiceAsync(session.SelectedTestId ?? "", context, cancellationToken);
                    var hasBranches      = serviceDistricts.Contains(city);
                    // Fallback: also check raw branches (in case BranchServices not configured)
                    if (!hasBranches)
                    {
                        var allBranchesCheck = await GetCachedBranchesAsync(context, cancellationToken);
                        hasBranches = allBranchesCheck.Any(b => b.District.ToLower() == city);
                    }
                    if (hasBranches)
                    {
                        session.SelectedCity = city;
                        session.CurrentState = WhatsAppState.ChoosingLab;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendLabList(to, city, session.SelectedTestId ?? "", context, httpClientFactory, configuration, cancellationToken);
                    }
                    else
                    {
                        await SendTextMessage(to, $"We don't have labs for this service in {city} yet. Please choose one of the available cities.", httpClientFactory, configuration);
                        await SendCityList(to, session.SelectedTestId ?? "", context, httpClientFactory, configuration, cancellationToken);
                    }
                    break;

                case WhatsAppState.ChoosingLab:
                    var labId       = replyId.Replace("lab_", "");
                    var allBranches = await GetCachedBranchesAsync(context, cancellationToken);
                    var selectedLab = allBranches.FirstOrDefault(b => b.Id == labId);
                    if (selectedLab != null)
                    {
                        session.SelectedLabId   = selectedLab.Id;
                        session.SelectedLabName = selectedLab.Name;
                        session.CurrentState    = WhatsAppState.ChoosingSlot;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendSlotList(to, selectedLab.Id, selectedLab.Name, context, httpClientFactory, configuration, cancellationToken);
                    }
                    break;

                case WhatsAppState.ChoosingSlot:
                    var slotId       = replyId.Replace("slot_", "");
                    // Slots are not cached – availability changes frequently
                    var selectedSlot = await context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == slotId, cancellationToken);
                    if (selectedSlot != null)
                    {
                        session.SelectedSlot = selectedSlot.Id;
                        session.CurrentState = WhatsAppState.MemberCount;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendPersonCountPrompt(to, httpClientFactory, configuration);
                    }
                    break;

                case WhatsAppState.Confirm:
                    if (replyId == "pay_now")
                    {
                        await SimulatePayment(to, session, context, httpClientFactory, configuration, cancellationToken);
                    }
                    break;

                case WhatsAppState.Done:
                    if (replyId == "menu_book")
                    {
                        session.CurrentState = WhatsAppState.ChoosingTest;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendServiceList(to, context, httpClientFactory, configuration, cancellationToken);
                    }
                    else if (replyId == "menu_bookings")
                    {
                        await SendViewBookings(to, context, httpClientFactory, configuration, cancellationToken);
                    }
                    else
                    {
                        session.CurrentState = WhatsAppState.Start;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendGreeting(to, httpClientFactory, configuration);
                    }
                    break;
            }
        }

        // ─── WhatsApp message senders ───────────────────────────────────────────────

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

        private async Task SendServiceList(
            string to,
            IApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            // Use cached services list
            var services = await GetCachedServicesAsync(context, cancellationToken);
            var top10    = services.Take(10).ToList();

            if (!top10.Any())
            {
                await SendTextMessage(to, "Sorry, no services are available at the moment. Please try again later.", httpClientFactory, configuration);
                return;
            }

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
                    header = new { type = "text", text = "🏥 Select a Service" },
                    body   = new { text  = "Choose the type of test you'd like to book:" },
                    footer = new { text  = "All tests are NABL certified" },
                    action = new
                    {
                        button   = "View services",
                        sections = new[]
                        {
                            new
                            {
                                title = "Diagnostic Services",
                                rows
                            }
                        }
                    }
                }
            };
            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendCityList(
            string to,
            string serviceId,
            IApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            // Only show cities where the SELECTED SERVICE is available – max 3 buttons
            var districts = await GetCachedDistrictsForServiceAsync(serviceId, context, cancellationToken);
            var top3      = districts.Take(3).ToList();

            if (!top3.Any())
            {
                await SendTextMessage(to, "Sorry, no locations are currently available for this service. Please choose a different test.", httpClientFactory, configuration);
                return;
            }

            var buttons = top3.Select(d => new
            {
                type  = "reply",
                reply = new
                {
                    id    = $"city_{d}",
                    title = char.ToUpper(d[0]) + d[1..]
                }
            }).ToArray();

            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type   = "button",
                    body   = new { text = "📍 *Select your city:*" },
                    action = new { buttons }
                }
            };
            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendLabList(
            string to, string city, string serviceId,
            IApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            // Resolve service from cache
            var allServices = await GetCachedServicesAsync(context, cancellationToken);
            var service     = allServices.FirstOrDefault(s => s.Id == serviceId);
            var basePrice   = service?.BasePrice ?? 0m;

            // Only show labs that ACTUALLY OFFER the selected service in this city
            var branchServices = await context.BranchServices
                .Where(bs => bs.ServiceId == serviceId && bs.IsActive)
                .ToListAsync(cancellationToken);

            var eligibleBranchIds = branchServices.Select(bs => bs.BranchId).ToHashSet();

            var allBranches  = await GetCachedBranchesAsync(context, cancellationToken);
            var cityBranches = allBranches
                .Where(b => b.District.ToLower() == city.ToLower() && eligibleBranchIds.Contains(b.Id))
                .ToList();

            // Fallback: if no BranchService records exist, show all active city branches
            // (means the service uses the global base price for all branches)
            if (!cityBranches.Any())
            {
                cityBranches = allBranches
                    .Where(b => b.District.ToLower() == city.ToLower())
                    .ToList();
            }

            if (!cityBranches.Any())
            {
                await SendTextMessage(to, $"No labs found in {city} for this service. Please choose another city.", httpClientFactory, configuration);
                return;
            }

            var rows = new List<object>();
            foreach (var b in cityBranches)
            {
                var overridePrice = branchServices.FirstOrDefault(bs => bs.BranchId == b.Id)?.CustomPrice;
                var displayPrice  = overridePrice ?? basePrice;

                rows.Add(new
                {
                    id          = $"lab_{b.Id}",
                    title       = b.Name.Length > 24 ? b.Name[..24] : b.Name,
                    description = $"{b.City} · {b.Pincode} · ₹{displayPrice}"
                });
            }

            var cityName = char.ToUpper(city[0]) + city[1..];

            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type   = "list",
                    header = new { type = "text", text = $"Labs in {cityName}" },
                    body   = new { text  = $"Choose a NABL-certified lab for *{service?.Name ?? "the test"}*:" },
                    footer = new { text  = "All labs open from 6 AM" },
                    action = new
                    {
                        button   = "View labs",
                        sections = new[]
                        {
                            new { title = $"Available in {cityName}", rows = rows.ToArray() }
                        }
                    }
                }
            };
            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendSlotList(
            string to, string labId, string labName,
            IApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            // Slots are real-time; do NOT cache them
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

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

            // BUG FIX: Use FormatTime() helper — hh\:mm in interpolated strings is a broken escape
            var rows = slots.Select(s => new
            {
                id          = $"slot_{s.Id}",
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

        private async Task SendPersonCountPrompt(string to, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            await SendTextMessage(to,
                "👥 *How many people need the blood test?*\n\n" +
                "Reply with a number (1–6).\n" +
                "e.g. reply *2* for 2 family members.",
                httpClientFactory, configuration);
        }

        private async Task SendLocationRequest(string to, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            await SendTextMessage(to,
                "📍 *Share your location*\n\n" +
                "Please tap the paperclip 📎 → Location → and share your current location.\n\n" +
                "This helps us confirm the nearest branch and arrange home sample collection if needed.",
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

            // Resolve service & price from real DB (or cache)
            var allServices  = await GetCachedServicesAsync(context, cancellationToken);
            var service      = allServices.FirstOrDefault(s => s.Id == session.SelectedTestId);
            var basePrice    = service?.BasePrice ?? 0m;

            // Branch-service price override (real-time)
            var branchService = await context.BranchServices
                .FirstOrDefaultAsync(bs =>
                    bs.BranchId  == session.SelectedLabId &&
                    bs.ServiceId == session.SelectedTestId &&
                    bs.IsActive, cancellationToken);
            decimal rate = branchService?.CustomPrice ?? basePrice;

            // Resolve lab name from DB for accuracy
            var allBranches = await GetCachedBranchesAsync(context, cancellationToken);
            var lab         = allBranches.FirstOrDefault(b => b.Id == session.SelectedLabId);
            var labName     = lab?.Name ?? session.SelectedLabName ?? "Lab";

            // Resolve customer name from DB
            var user         = await context.Users.FirstOrDefaultAsync(u => u.Phone == to, cancellationToken);
            var customerName = user?.Name ?? "Customer";
            if (string.IsNullOrWhiteSpace(customerName)) customerName = "Customer";

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
                    notify = new
                    {
                        sms   = false,
                        email = false
                    },
                    reminder_enable = false,
                    notes = new
                    {
                        phone = to,
                        lab   = labName
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
            session.CurrentState = WhatsAppState.Done;
            await SaveSessionAsync(session, context, cancellationToken);

            // ── Resolve real service from cache ──────────────────────────────────────
            var allServices = await GetCachedServicesAsync(context, cancellationToken);
            var service     = allServices.FirstOrDefault(s => s.Id == session.SelectedTestId);
            var basePrice   = service?.BasePrice ?? 0m;

            // ── Branch-service price override ────────────────────────────────────────
            var branchService = await context.BranchServices
                .FirstOrDefaultAsync(bs =>
                    bs.BranchId  == session.SelectedLabId &&
                    bs.ServiceId == session.SelectedTestId &&
                    bs.IsActive, cancellationToken);
            decimal rate = branchService?.CustomPrice ?? basePrice;

            // ── Resolve lab from cache ───────────────────────────────────────────────
            var allBranches = await GetCachedBranchesAsync(context, cancellationToken);
            var lab         = allBranches.FirstOrDefault(b => b.Id == session.SelectedLabId);
            var labName     = lab?.Name ?? session.SelectedLabName ?? "Lab";
            var labAddress  = lab != null ? $"{lab.City}, {lab.District} – {lab.Pincode}" : string.Empty;

            // ── Calculate total ──────────────────────────────────────────────────────
            int total = (int)rate + (session.MemberCount > 1
                ? (int)Math.Round((session.MemberCount - 1) * rate * 0.8m)
                : 0);

            // ── Update slot availability (real-time) ─────────────────────────────────
            var slot        = await context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == session.SelectedSlot, cancellationToken);
            string slotDisplay = "Confirmed Slot";
            if (slot != null)
            {
                // BUG FIX: Use FormatTime() helper instead of hh\:mm escape in interpolated strings
                slotDisplay = $"{slot.SlotDate:dddd, MMM dd yyyy} @ {FormatTime(slot.StartTime)}";
                slot.BookedCount++;
                if (slot.BookedCount >= slot.MaxCapacity)
                    slot.IsAvailable = false;
                context.AppointmentSlots.Update(slot);
            }

            // ── Resolve or create customer user ──────────────────────────────────────
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

            // ── Generate booking ID ──────────────────────────────────────────────────
            var bookingId = $"BK-{DateTime.UtcNow:yyyyMMdd}-{new Random().Next(1000, 9999)}";

            // ── Create appointment with all real data ────────────────────────────────
            var appointment = new Appointment
            {
                Id                 = Guid.NewGuid().ToString(),
                AppointmentNumber  = bookingId,
                CustomerUserId     = user.Id,
                BranchId           = session.SelectedLabId ?? string.Empty,
                AppointmentSlotId  = session.SelectedSlot  ?? string.Empty,
                LocationLatitude   = 0m,
                LocationLongitude  = 0m,
                LocationAddress    = "Shared via WhatsApp",
                Passcode           = new Random().Next(1000, 9999).ToString(),
                Status             = AppointmentStatus.Confirmed,
                TotalAmount        = total,
                PlatformCommission = service != null
                    ? total * (service.PlatformCommissionPct / 100m)
                    : total * 0.15m,
                LabPayout  = service != null
                    ? total * (1m - service.PlatformCommissionPct / 100m)
                    : total * 0.85m,
                CreatedAt   = DateTime.UtcNow,
                MemberCount = session.MemberCount
            };
            context.Appointments.Add(appointment);

            // ── Create appointment members (realistic defaults) ───────────────────────
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

            // ── Create payment record ────────────────────────────────────────────────
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

            // ── Send confirmation with all real data ─────────────────────────────────
            var commissionPct = service?.PlatformCommissionPct ?? 15m;
            var labPayout     = (int)(total * (1m - commissionPct / 100m));

            var confirmMsg =
                $"✅ *Booking Confirmed!*\n\n" +
                $"🆔 Booking ID: *{bookingId}*\n" +
                $"🩸 Service: {service?.Name ?? "Diagnostic Test"}\n" +
                $"🏥 Lab: {labName}\n" +
                (!string.IsNullOrEmpty(labAddress) ? $"📍 Address: {labAddress}\n" : "") +
                $"📅 Date & Time: {slotDisplay}\n" +
                $"👥 Persons: {session.MemberCount}\n" +
                $"💰 Amount Paid: ₹{total}\n\n" +
                $"🧪 *Instructions:*\n" +
                $"• Please fast for 8–10 hours before your test\n" +
                $"• Bring this booking ID: *{bookingId}*\n" +
                $"• Report will be sent to your WhatsApp within 24 hrs\n\n" +
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
                .Include(a => a.Branch)
                .Where(a => a.CustomerUserId == user.Id)
                .OrderByDescending(a => a.CreatedAt)
                .Take(3)
                .ToListAsync(cancellationToken);

            if (!bookings.Any())
            {
                await SendTextMessage(to, "You don't have any bookings yet.\n\nReply *hi* to return to the main menu.", httpClientFactory, configuration);
                return;
            }

            // Resolve service names in one DB round-trip using cache
            var allServices = await GetCachedServicesAsync(context, cancellationToken);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("📋 *Your Recent Bookings*\n");

            foreach (var b in bookings)
            {
                // Fetch slot for real date/time display
                var slot = await context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == b.AppointmentSlotId, cancellationToken);
                string slotDisplay = slot != null
                    ? $"{slot.SlotDate:MMM dd, yyyy} · {FormatTime(slot.StartTime)}"
                    : "Time TBD";

                sb.AppendLine("─────────────────────");
                sb.AppendLine($"🆔 Booking ID: *{b.AppointmentNumber}*");
                sb.AppendLine($"🏥 Lab: {b.Branch?.Name ?? "Lab Branch"}");
                sb.AppendLine($"📅 Date: {slotDisplay}");
                sb.AppendLine($"💰 Total Paid: ₹{b.TotalAmount}");
                sb.AppendLine($"✅ Status: {b.Status}");
            }
            sb.AppendLine("─────────────────────\n");
            sb.AppendLine("Reply *hi* to return to the main menu.");

            await SendTextMessage(to, sb.ToString(), httpClientFactory, configuration);
        }

        private async Task SendHelp(string to, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            await SendTextMessage(to,
                "❓ *Help & FAQ*\n\n" +
                "🩸 *Blood Test* — Fasting 8–10 hrs required\n" +
                "📍 *Location* — Multiple cities available\n" +
                "📲 *Reports* — Sent via WhatsApp in 24 hrs\n" +
                "💰 *Payment* — UPI, card, net banking\n" +
                "📞 *Support* — 1800-123-4567 (Mon–Sat, 8am–8pm)\n\n" +
                "Reply *hi* to return to the main menu.",
                httpClientFactory, configuration);
        }

        // ─── Low-level WhatsApp send helpers ───────────────────────────────────────

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

        // ─── Session helpers ────────────────────────────────────────────────────────

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

        // ─── Utilities ──────────────────────────────────────────────────────────────

        private static bool IsGreeting(string text) =>
            new[] { "hi", "hello", "hey", "hii", "helo", "hai", "start", "menu",
                    "namaste", "good morning", "good evening", "howdy" }
            .Any(g => text.Contains(g));

        private async Task<string> BuildBookingSummaryAsync(
            WhatsAppSession session,
            IApplicationDbContext context,
            CancellationToken cancellationToken)
        {
            // Resolve service from cache
            var allServices = await GetCachedServicesAsync(context, cancellationToken);
            var service     = allServices.FirstOrDefault(s => s.Id == session.SelectedTestId);

            // Resolve lab from cache
            var allBranches = await GetCachedBranchesAsync(context, cancellationToken);
            var lab         = allBranches.FirstOrDefault(b => b.Id == session.SelectedLabId);
            var labName     = lab?.Name ?? session.SelectedLabName ?? "Selected Lab";
            var labAddress  = lab != null ? $"{lab.City}, {lab.District}" : string.Empty;

            // Slot is real-time
            var slot       = await context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == session.SelectedSlot, cancellationToken);
            string slotDisplay = slot != null
                ? $"{slot.SlotDate:dddd, MMM dd yyyy} · {FormatTime(slot.StartTime)}"
                : session.SelectedSlot ?? "Selected Slot";

            // Calculate price
            var basePrice     = service?.BasePrice ?? 0m;
            var branchService = await context.BranchServices
                .FirstOrDefaultAsync(bs =>
                    bs.BranchId  == session.SelectedLabId &&
                    bs.ServiceId == session.SelectedTestId &&
                    bs.IsActive, cancellationToken);
            decimal rate  = branchService?.CustomPrice ?? basePrice;
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
                $"📍 Location: Shared ✓";
        }
    }
}
