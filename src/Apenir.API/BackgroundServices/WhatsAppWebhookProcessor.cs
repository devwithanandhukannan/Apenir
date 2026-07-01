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

        private async Task ProcessLocationMessage(string to, double lat, double lng, IApplicationDbContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
        {
            var session = await GetOrCreateSessionAsync(to, context, cancellationToken);

            if (session.CurrentState == WhatsAppState.Location)
            {
                session.LocationShared = true;
                session.CurrentState = WhatsAppState.Confirm;
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
                        await SendServiceList(to, context, httpClientFactory, configuration);
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
                    var selectedService = await context.Services.FirstOrDefaultAsync(s => s.Id == replyId, cancellationToken);
                    if (selectedService != null)
                    {
                        session.SelectedTestId = selectedService.Id;
                        session.CurrentState = WhatsAppState.ChoosingCity;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendCityList(to, context, httpClientFactory, configuration, cancellationToken);
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
                    var city = replyId.Replace("city_", "").ToLower();
                    var hasBranches = await context.Branches.AnyAsync(b => b.District.ToLower() == city && b.IsActive, cancellationToken);
                    if (hasBranches)
                    {
                        session.SelectedCity = city;
                        session.CurrentState = WhatsAppState.ChoosingLab;
                        await SaveSessionAsync(session, context, cancellationToken);
                        await SendLabList(to, city, session.SelectedTestId ?? "", context, httpClientFactory, configuration, cancellationToken);
                    }
                    else
                    {
                        await SendTextMessage(to, $"We don't have labs in {city} yet. Please choose one of the available cities.", httpClientFactory, configuration);
                        await SendCityList(to, context, httpClientFactory, configuration, cancellationToken);
                    }
                    break;

                case WhatsAppState.ChoosingLab:
                    var labId = replyId.Replace("lab_", "");
                    var selectedLab = await context.Branches.FirstOrDefaultAsync(b => b.Id == labId, cancellationToken);
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
                    var slotId = replyId.Replace("slot_", "");
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
                        await SendServiceList(to, context, httpClientFactory, configuration);
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

        private async Task SendServiceList(string to, IApplicationDbContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            var dbServices = await context.Services
                .Where(s => s.IsActive)
                .OrderBy(s => s.Name)
                .Take(10)
                .ToListAsync();

            var rows = dbServices.Select(s => new
            {
                id = s.Id,
                title = s.Name.Length > 24 ? s.Name[..24] : s.Name,
                description = s.Description != null && s.Description.Length > 72 ? s.Description[..72] : s.Description ?? string.Empty
            }).ToArray();

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
                                rows
                            }
                        }
                    }
                }
            };
            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendCityList(string to, IApplicationDbContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
        {
            var districts = await context.Branches
                .Where(b => b.IsActive)
                .Select(b => b.District)
                .Distinct()
                .Take(3)
                .ToListAsync(cancellationToken);

            var buttons = districts.Select(d => new
            {
                type = "reply",
                reply = new
                {
                    id = $"city_{d.ToLower()}",
                    title = char.ToUpper(d[0]) + d[1..]
                }
            }).ToList();

            if (!buttons.Any())
            {
                buttons.Add(new { type = "reply", reply = new { id = "city_kochi", title = "Kochi" } });
            }

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
                        buttons = buttons.ToArray()
                    }
                }
            };
            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendLabList(string to, string city, string serviceId, IApplicationDbContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
        {
            var service = await context.Services.FirstOrDefaultAsync(s => s.Id == serviceId, cancellationToken);
            var basePrice = service?.BasePrice ?? 0m;

            var branches = await context.Branches
                .Where(b => b.District.ToLower() == city.ToLower() && b.IsActive)
                .ToListAsync(cancellationToken);

            var branchServices = await context.BranchServices
                .Where(bs => bs.ServiceId == serviceId && bs.IsActive)
                .ToListAsync(cancellationToken);

            var rows = new List<object>();
            foreach (var b in branches)
            {
                var overridePrice = branchServices.FirstOrDefault(bs => bs.BranchId == b.Id)?.CustomPrice;
                var displayPrice = overridePrice ?? basePrice;

                rows.Add(new
                {
                    id = $"lab_{b.Id}",
                    title = b.Name.Length > 24 ? b.Name[..24] : b.Name,
                    description = $"{b.City} · Pincode: {b.Pincode} · Price: ₹{displayPrice}"
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
                    type = "list",
                    header = new { type = "text", text = $"Labs in {cityName}" },
                    body = new { text = $"Choose a NABL-certified lab for your {service?.Name ?? "test"}:" },
                    footer = new { text = "All labs open from 6 AM" },
                    action = new
                    {
                        button = "View labs",
                        sections = new[]
                        {
                            new { title = $"Available in {cityName}", rows = rows.ToArray() }
                        }
                    }
                }
            };
            await SendWhatsAppMessage(payload, httpClientFactory, configuration);
        }

        private async Task SendSlotList(string to, string labId, string labName, IApplicationDbContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
        {
            var slots = await context.AppointmentSlots
                .Where(s => s.BranchId == labId && s.IsAvailable && s.SlotDate >= DateOnly.FromDateTime(DateTime.UtcNow))
                .OrderBy(s => s.SlotDate).ThenBy(s => s.StartTime)
                .Take(10)
                .ToListAsync(cancellationToken);

            var rows = slots.Select(s => new
            {
                id = $"slot_{s.Id}",
                title = $"{s.SlotDate:MMM dd} {s.StartTime:hh:mm tt}",
                description = $"Capacity: {s.MaxCapacity - s.BookedCount} phlebotomists"
            }).ToArray();

            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "interactive",
                interactive = new
                {
                    type = "list",
                    header = new { type = "text", text = "Select a Time Slot" },
                    body = new { text = $"📅 Available slots for {labName}\n\nChoose an appointment time:" },
                    footer = new { text = "Fasting required for blood tests" },
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

        private async Task SendPaymentRequest(string to, WhatsAppSession session, IApplicationDbContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
        {
            var rzpKeyId = configuration["Razorpay:KeyId"];
            var rzpKeySecret = configuration["Razorpay:KeySecret"];

            var service = await context.Services.FirstOrDefaultAsync(s => s.Id == session.SelectedTestId, cancellationToken);
            var basePrice = service?.BasePrice ?? 400m;

            var branchService = await context.BranchServices
                .FirstOrDefaultAsync(bs => bs.BranchId == session.SelectedLabId && bs.ServiceId == session.SelectedTestId, cancellationToken);
            decimal rate = branchService?.CustomPrice ?? basePrice;

            int total = (int)rate + (session.MemberCount > 1 ? (int)Math.Round((session.MemberCount - 1) * rate * 0.8m) : 0);

            string paymentUrl = "https://rzp.io/i/example";
            try
            {
                var client = httpClientFactory.CreateClient();
                var authString = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{rzpKeyId}:{rzpKeySecret}"));
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authString);

                var rzpPayload = new
                {
                    amount = total * 100,
                    currency = "INR",
                    accept_partial = false,
                    description = $"LabCare {service?.Name ?? "Booking"} Payment",
                    customer = new
                    {
                        name = "Customer",
                        contact = $"+{to}",
                    },
                    notify = new
                    {
                        sms = false,
                        email = false
                    },
                    reminder_enable = false,
                    notes = new
                    {
                        phone = to,
                        lab = session.SelectedLabName
                    }
                };

                var content = new StringContent(JsonSerializer.Serialize(rzpPayload), Encoding.UTF8, "application/json");
                var response = await client.PostAsync("https://api.razorpay.com/v1/payment_links", content, cancellationToken);
                
                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(responseBody);
                    paymentUrl = doc.RootElement.GetProperty("short_url").GetString() ?? paymentUrl;
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
                recipient_type = "individual",
                to,
                type = "interactive",
                interactive = new
                {
                    type = "cta_url",
                    header = new { type = "text", text = "Payment Request" },
                    body = new { text = $"Please complete your payment of ₹{total} for {session.SelectedLabName}." },
                    footer = new { text = "Secure payment by Razorpay" },
                    action = new
                    {
                        name = "cta_url",
                        parameters = new
                        {
                            display_text = "Pay Now",
                            url = paymentUrl
                        }
                    }
                }
            };
            await SendWhatsAppMessage(waPayload, httpClientFactory, configuration);
        }

        private async Task SimulatePayment(string to, WhatsAppSession session, IApplicationDbContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
        {
            session.CurrentState = WhatsAppState.Done;
            await SaveSessionAsync(session, context, cancellationToken);

            var service = await context.Services.FirstOrDefaultAsync(s => s.Id == session.SelectedTestId, cancellationToken);
            var basePrice = service?.BasePrice ?? 400m;

            var branchService = await context.BranchServices
                .FirstOrDefaultAsync(bs => bs.BranchId == session.SelectedLabId && bs.ServiceId == session.SelectedTestId, cancellationToken);
            decimal rate = branchService?.CustomPrice ?? basePrice;

            int total = (int)rate + (session.MemberCount > 1 ? (int)Math.Round((session.MemberCount - 1) * rate * 0.8m) : 0);

            var slot = await context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == session.SelectedSlot, cancellationToken);
            string slotDisplay = "Selected Slot";
            if (slot != null)
            {
                slotDisplay = $"{slot.SlotDate:MMM dd} @ {slot.StartTime:hh:mm tt}";
                slot.BookedCount++;
                if (slot.BookedCount >= slot.MaxCapacity)
                {
                    slot.IsAvailable = false;
                }
                context.AppointmentSlots.Update(slot);
            }

            var bookingId = $"BK-{DateTime.UtcNow:yyyyMMdd}-{new Random().Next(1000, 9999)}";

            var user = await context.Users.FirstOrDefaultAsync(u => u.Phone == to, cancellationToken);
            if (user == null)
            {
                user = new User
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = "WhatsApp Customer",
                    Phone = to,
                    Role = UserRole.Customer,
                    IsActive = true
                };
                context.Users.Add(user);
                await context.SaveChangesAsync(cancellationToken);
            }

            var appointment = new Appointment
            {
                Id = Guid.NewGuid().ToString(),
                AppointmentNumber = bookingId,
                CustomerUserId = user.Id,
                BranchId = session.SelectedLabId ?? string.Empty,
                AppointmentSlotId = session.SelectedSlot ?? string.Empty,
                LocationLatitude = 0m,
                LocationLongitude = 0m,
                LocationAddress = "Shared via WhatsApp",
                Passcode = new Random().Next(1000, 9999).ToString(),
                Status = AppointmentStatus.Confirmed,
                TotalAmount = total,
                PlatformCommission = total * 0.15m,
                LabPayout = total * 0.85m,
                CreatedAt = DateTime.UtcNow,
                MemberCount = session.MemberCount
            };
            context.Appointments.Add(appointment);

            for (int i = 0; i < session.MemberCount; i++)
            {
                var member = new AppointmentMember
                {
                    Id = Guid.NewGuid().ToString(),
                    AppointmentId = appointment.Id,
                    MemberName = $"Patient {i + 1}",
                    Age = 30,
                    Gender = Gender.Other,
                    Relationship = i == 0 ? "Self" : "Family Member"
                };
                context.AppointmentMembers.Add(member);
            }

            var payment = new Payment
            {
                Id = Guid.NewGuid().ToString(),
                AppointmentId = appointment.Id,
                RazorpayOrderId = $"order_WA_{bookingId.Replace("-", "")}",
                RazorpayPaymentId = $"pay_WA_{bookingId.Replace("-", "")}",
                Status = PaymentStatus.Paid,
                PaymentMethod = PaymentMethod.UPI,
                PaidAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            };
            context.Payments.Add(payment);

            await context.SaveChangesAsync(cancellationToken);

            var confirmMsg =
                $"✅ *Booking Confirmed!*\n\n" +
                $"🆔 Booking ID: *{bookingId}*\n" +
                $"🩸 Service: {service?.Name ?? "Blood Test"}\n" +
                $"🏥 Lab: {session.SelectedLabName}\n" +
                $"📅 Date & Time: {slotDisplay}\n" +
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

        private async Task SendViewBookings(string to, IApplicationDbContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
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

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("📋 *Your Recent Bookings*\n");

            foreach (var b in bookings)
            {
                var slot = await context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == b.AppointmentSlotId, cancellationToken);
                string slotDisplay = slot != null ? $"{slot.SlotDate:MMM dd} · {slot.StartTime:hh:mm tt}" : "Time TBD";

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

        private async Task<string> BuildBookingSummaryAsync(WhatsAppSession session, IApplicationDbContext context, CancellationToken cancellationToken)
        {
            var service = await context.Services.FirstOrDefaultAsync(s => s.Id == session.SelectedTestId, cancellationToken);
            var slot = await context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == session.SelectedSlot, cancellationToken);
            string slotDisplay = slot != null ? $"{slot.SlotDate:MMM dd, yyyy} · {slot.StartTime:hh:mm tt}" : session.SelectedSlot ?? "Selected Slot";

            return
                $"📋 *Booking Summary*\n" +
                $"🩸 Service: {service?.Name ?? "Diagnostics"}\n" +
                $"🏥 Lab: {session.SelectedLabName}\n" +
                $"📅 Date & Time: {slotDisplay}\n" +
                $"👥 {session.MemberCount} person{(session.MemberCount > 1 ? "s" : "")}\n" +
                $"📍 Location: Shared ✓";
        }
    }
}
