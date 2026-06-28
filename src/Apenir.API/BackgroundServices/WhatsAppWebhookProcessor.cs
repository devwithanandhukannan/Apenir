using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
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

        public WhatsAppWebhookProcessor(
            IWhatsAppWebhookQueue queue,
            IServiceProvider serviceProvider,
            ILogger<WhatsAppWebhookProcessor> logger)
        {
            _queue = queue;
            _serviceProvider = serviceProvider;
            _logger = logger;
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

        private async Task ProcessPayloadAsync(string body, CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

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
                            var from = message.GetProperty("from").GetString()!;
                            var msgType = message.GetProperty("type").GetString();

                            _logger.LogInformation("📩 Background processing message from: {From} | Type: {MsgType}", from, msgType);

                            // Auto-register patient/customer if they don't exist yet
                            var user = await context.Users.FirstOrDefaultAsync(u => u.Phone == from, cancellationToken);
                            if (user == null)
                            {
                                _logger.LogInformation("👤 Creating new Customer user for phone {From} via WhatsApp auto-registration", from);
                                user = new User
                                {
                                    Phone = from,
                                    Role = UserRole.Customer
                                };
                                context.Users.Add(user);

                                var customer = new Customer
                                {
                                    UserId = user.Id,
                                    Phone = from,
                                    Name = "WhatsApp Customer"
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
                                var interactive = message.GetProperty("interactive");
                                var interactiveType = interactive.GetProperty("type").GetString();

                                string? replyId = null;
                                string? replyTitle = null;

                                if (interactiveType == "button_reply")
                                {
                                    replyId = interactive.GetProperty("button_reply").GetProperty("id").GetString();
                                    replyTitle = interactive.GetProperty("button_reply").GetProperty("title").GetString();
                                }
                                else if (interactiveType == "list_reply")
                                {
                                    replyId = interactive.GetProperty("list_reply").GetProperty("id").GetString();
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

        private async Task ProcessTextMessage(string to, string text, IApplicationDbContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
        {
            var session = await GetOrCreateSessionAsync(to, context, cancellationToken);
            var lower = text.ToLower().Trim();

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
                        session.MemberCount = count;
                        session.CurrentState = WhatsAppState.Location;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendLocationRequest(to, httpClientFactory, configuration);
                    }
                    else
                    {
                        await SendTextMessage(to, "Please enter a number between 1 and 6. How many people need the blood test?", httpClientFactory, configuration);
                    }
                    break;

                default:
                    await SendGreeting(to, httpClientFactory, configuration);
                    break;
            }
        }

        private async Task ProcessLocationMessage(string to, double lat, double lng, IApplicationDbContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
        {
            var session = await GetOrCreateSessionAsync(to, context, cancellationToken);

            if (session.CurrentState == WhatsAppState.Location)
            {
                session.LocationShared = true;
                session.CurrentState = WhatsAppState.Confirm;
                await SaveSessionAsync(session, context, cancellationToken);

                var summary = BuildBookingSummary(session);
                await SendTextMessage(to,
                    $"📍 Location received!\n\n{summary}\n\n" +
                    "💳 To confirm your booking, complete the payment below.\n\n" +
                    "👉 Reply *PAY* to simulate payment (demo mode)",
                    httpClientFactory, configuration);
            }
        }

        private async Task ProcessInteractiveReply(string to, string replyId, string replyTitle, IApplicationDbContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
        {
            var session = await GetOrCreateSessionAsync(to, context, cancellationToken);

            switch (session.CurrentState)
            {
                case WhatsAppState.Start:
                    if (replyId == "menu_book")
                    {
                        session.CurrentState = WhatsAppState.ChoosingTest;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendServiceList(to, httpClientFactory, configuration);
                    }
                    else if (replyId == "menu_bookings")
                    {
                        await SendViewBookings(to, httpClientFactory, configuration);
                    }
                    else if (replyId == "menu_help")
                    {
                        await SendHelp(to, httpClientFactory, configuration);
                    }
                    break;

                case WhatsAppState.ChoosingTest:
                    session.SelectedTestId = replyId;
                    if (replyId == "service_blood")
                    {
                        session.CurrentState = WhatsAppState.ChoosingCity;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendCityList(to, httpClientFactory, configuration);
                    }
                    else
                    {
                        await SendTextMessage(to, "This service is coming soon! Let's book a Blood Test for now.", httpClientFactory, configuration);
                        session.CurrentState = WhatsAppState.ChoosingCity;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendCityList(to, httpClientFactory, configuration);
                    }
                    break;

                case WhatsAppState.ChoosingCity:
                    var city = replyId.Replace("city_", "");
                    if (DemoData.LabsByCity.ContainsKey(city))
                    {
                        session.SelectedCity = city;
                        session.CurrentState = WhatsAppState.ChoosingLab;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendLabList(to, city, httpClientFactory, configuration);
                    }
                    else
                    {
                        await SendTextMessage(to, "We don't have labs in that city yet. Please choose Kochi or Trivandrum.", httpClientFactory, configuration);
                        await SendCityList(to, httpClientFactory, configuration);
                    }
                    break;

                case WhatsAppState.ChoosingLab:
                    var labId = replyId.Replace("lab_", "");
                    var labs = DemoData.LabsByCity.TryGetValue(session.SelectedCity ?? "", out var cityLabs) ? cityLabs : new List<Apenir.API.Controllers.Lab>();
                    var selectedLab = labs.Find(l => l.Id == labId);
                    if (selectedLab != null)
                    {
                        session.SelectedLabId = selectedLab.Id;
                        session.SelectedLabName = selectedLab.Name;
                        session.CurrentState = WhatsAppState.ChoosingSlot;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendSlotList(to, selectedLab.Name, httpClientFactory, configuration);
                    }
                    break;

                case WhatsAppState.ChoosingSlot:
                    var slotTime = replyId.Replace("slot_", "").Replace("_", " ");
                    session.SelectedSlot = slotTime;
                    session.CurrentState = WhatsAppState.MemberCount;
                    await SaveSessionAsync(session, context, cancellationToken);
                    await SendPersonCountPrompt(to, httpClientFactory, configuration);
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
                        await SendServiceList(to, httpClientFactory, configuration);
                    }
                    else if (replyId == "menu_bookings")
                    {
                        await SendViewBookings(to, httpClientFactory, configuration);
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

        private async Task SendGreeting(string to, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type = "button",
                    header = new { type = "text", text = "🧬 LabCare Assistant" },
                    body = new
                    {
                        text = "👋 Hello! Welcome to *LabCare*.\n\nI can help you book blood tests and diagnostics at top NABL-certified labs near you.\n\nWhat would you like to do?"
                    },
                    footer = new { text = "LabCare · Trusted Diagnostics" },
                    action = new
                    {
                        buttons = new[]
                        {
                            new { type = "reply", reply = new { id = "menu_book",     title = "📅 Book a test"     } },
                            new { type = "reply", reply = new { id = "menu_bookings", title = "📋 My bookings"     } },
                            new { type = "reply", reply = new { id = "menu_help",     title = "❓ Help"            } },
                        }
                    }
                }
            };
            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendServiceList(string to, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type = "list",
                    header = new { type = "text", text = "🏥 Select a Service" },
                    body = new { text = "Choose the type of test you'd like to book:" },
                    footer = new { text = "All tests are NABL certified" },
                    action = new
                    {
                        button = "View services",
                        sections = new[]
                        {
                            new
                            {
                                title = "Diagnostic Services",
                                rows = new[]
                                {
                                    new { id = "service_blood",  title = "🩸 Blood Test",          description = "CBC, LFT, RFT, Lipid profile & more" },
                                    new { id = "service_urine",  title = "🔬 Urine Analysis",       description = "Routine & microscopy" },
                                    new { id = "service_ecg",    title = "🫀 ECG",                  description = "Electrocardiogram" },
                                    new { id = "service_xray",   title = "🫁 X-Ray",                description = "Chest, limb & spine" },
                                    new { id = "service_full",   title = "🧬 Full Body Checkup",    description = "Comprehensive health package" },
                                }
                            }
                        }
                    }
                }
            };
            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendCityList(string to, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type = "button",
                    body = new { text = "📍 *Select your city:*" },
                    action = new
                    {
                        buttons = new[]
                        {
                            new { type = "reply", reply = new { id = "city_kochi",       title = "Kochi"       } },
                            new { type = "reply", reply = new { id = "city_trivandrum",  title = "Trivandrum"  } },
                            new { type = "reply", reply = new { id = "city_other",       title = "Other city"  } },
                        }
                    }
                }
            };
            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendLabList(string to, string city, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            var labs = DemoData.LabsByCity.TryGetValue(city, out var cityLabs) ? cityLabs : new List<Apenir.API.Controllers.Lab>();
            var rows = labs.Select(l => new
            {
                id = $"lab_{l.Id}",
                title = l.Name,
                description = $"{l.Area} · {l.Distance} · {l.Rating} · ₹{l.Rate}"
            }).ToList();

            var cityName = char.ToUpper(city[0]) + city[1..];

            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type = "list",
                    header = new { type = "text", text = $"Labs in {cityName}" },
                    body = new { text = "Choose a NABL-certified lab for your blood test:" },
                    footer = new { text = "All labs open from 6 AM" },
                    action = new
                    {
                        button = "View labs",
                        sections = new[]
                        {
                            new { title = $"Available in {cityName}", rows }
                        }
                    }
                }
            };
            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendSlotList(string to, string labName, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            var availableSlots = DemoData.Slots.Where(s => s.Available).Take(6).ToList();
            var rows = availableSlots.Select(s => new
            {
                id = $"slot_{s.Time.Replace(" ", "_").Replace(":", "")}",
                title = s.Time,
                description = "Available"
            }).ToList();

            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type = "list",
                    header = new { type = "text", text = "Select a Time Slot" },
                    body = new { text = $"📅 *Jun 28, 2026* — {labName}\n\nChoose an available appointment time:" },
                    footer = new { text = "Fasting required · No food 8–10 hrs before" },
                    action = new
                    {
                        button = "View slots",
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

        private async Task SimulatePayment(string to, WhatsAppSession session, IApplicationDbContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
        {
            session.CurrentState = WhatsAppState.Done;
            await SaveSessionAsync(session, context, cancellationToken);

            var bookingId = $"BK-{DateTime.UtcNow:yyyyMMdd}-{new Random().Next(1000, 9999)}";
            var labs = DemoData.LabsByCity.TryGetValue(session.SelectedCity ?? "", out var cityLabs) ? cityLabs : new List<Apenir.API.Controllers.Lab>();
            var lab = labs.Find(l => l.Id == session.SelectedLabId);
            int rate = lab?.Rate ?? 450;
            int total = rate + (session.MemberCount > 1 ? (int)Math.Round((session.MemberCount - 1) * rate * 0.8) : 0);

            var confirmMsg =
                $"✅ *Booking Confirmed!*\n\n" +
                $"🆔 Booking ID: *{bookingId}*\n" +
                $"🩸 Service: Blood Test\n" +
                $"🏥 Lab: {session.SelectedLabName}\n" +
                $"📅 Date & Time: Jun 28, 2026 · {session.SelectedSlot}\n" +
                $"👥 Persons: {session.MemberCount}\n" +
                $"💰 Amount Paid: ₹{total}\n\n" +
                $"🧪 *Instructions:*\n" +
                $"• Please fast for 8–10 hours before your test\n" +
                $"• Bring this booking ID\n" +
                $"• Report will be sent to your WhatsApp within 24 hrs\n\n" +
                $"Thank you for choosing LabCare! 🙏";

            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type = "button",
                    body = new { text = confirmMsg },
                    footer = new { text = "LabCare · Trusted Diagnostics" },
                    action = new
                    {
                        buttons = new[]
                        {
                            new { type = "reply", reply = new { id = "menu_book",     title = "📅 Book another"  } },
                            new { type = "reply", reply = new { id = "menu_bookings", title = "📋 My bookings"   } },
                            new { type = "reply", reply = new { id = "menu_help",     title = "🏠 Main menu"     } },
                        }
                    }
                }
            };

            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendViewBookings(string to, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            await SendTextMessage(to,
                "📋 *Your Bookings*\n\n" +
                "─────────────────────\n" +
                "🩸 Blood Test\n" +
                "🏥 Lal PathLabs — Kochi\n" +
                "📅 Jun 28, 2026 · 07:00 AM\n" +
                "👥 2 persons\n" +
                "🆔 BK-20260628-4821\n" +
                "✅ Confirmed\n" +
                "─────────────────────\n\n" +
                "Reply *hi* to return to the main menu.",
                httpClientFactory, configuration);
        }

        private async Task SendHelp(string to, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            await SendTextMessage(to,
                "❓ *Help & FAQ*\n\n" +
                "🩸 *Blood Test* — Fasting 8–10 hrs required\n" +
                "📍 *Location* — Kochi & Trivandrum available\n" +
                "📲 *Reports* — Sent via WhatsApp in 24 hrs\n" +
                "💰 *Payment* — UPI, card, net banking\n" +
                "📞 *Support* — 1800-123-4567 (Mon–Sat, 8am–8pm)\n\n" +
                "Reply *hi* to return to the main menu.",
                httpClientFactory, configuration);
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
                var accessToken = configuration["WhatsApp:AccessToken"] ?? "EAAOLfrg5aQcBRxBOf5iIvplpEeWST5E7ZAGXXfydZAZBZAcMM4G8hFsBa1xdiTNLZB7JfkZB7ICzqZBTepiZCyJZBFk38hZBaWuuTH39GLJzyyC7AWgk2jiR4SZCJZAXK0DTHuZAuA3kY0HCZA6tAVyL0LkZBie9TFQ52XA75nvz9e9R8kTEHOILpa5juCbeCS31U1b8JomQP9d9bByyZAqDnqmGxdSRYCvjoRzEdoqyk2V9hVf7OiHefcHXZA7ZAtv2KHYavflyxjjZB8v8JdZAzi1KNXZCaSPfuNfjg9svbOzucjAqPZCAZDZD";
                var phoneNumberId = configuration["WhatsApp:PhoneNumberId"] ?? "1198940716632437";
                var apiVersion = configuration["WhatsApp:ApiVersion"] ?? "v25.0";

                var client = httpClientFactory.CreateClient();
                var url = $"https://graph.facebook.com/{apiVersion}/{phoneNumberId}/messages";
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                var response = await client.PostAsync(url, content);
                var responseBody = await response.Content.ReadAsStringAsync();
                
                _logger.LogInformation("📬 WA Response [{StatusCode}]: {ResponseBody}", (int)response.StatusCode, responseBody);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send WhatsApp message");
            }
        }

        private async Task<WhatsAppSession> GetOrCreateSessionAsync(string phone, IApplicationDbContext context, CancellationToken cancellationToken)
        {
            var session = await context.WhatsAppSessions.FirstOrDefaultAsync(s => s.Phone == phone, cancellationToken);
            if (session == null)
            {
                session = new WhatsAppSession
                {
                    Phone = phone,
                    CurrentState = WhatsAppState.Start,
                    UpdatedAt = DateTime.UtcNow
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

        private static string BuildBookingSummary(WhatsAppSession session)
        {
            return
                $"📋 *Booking Summary*\n" +
                $"🩸 Blood Test\n" +
                $"🏥 {session.SelectedLabName}\n" +
                $"📅 Jun 28, 2026 · {session.SelectedSlot}\n" +
                $"👥 {session.MemberCount} person{(session.MemberCount > 1 ? "s" : "")}\n" +
                $"📍 Location: Shared ✓";
        }
    }
}
