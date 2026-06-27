using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Apenir.Core.Interfaces;
using Apenir.Core.Entities;
using Apenir.Core.Enums;

namespace Apenir.API.Controllers
{
    public static class DemoData
    {
        public static readonly Dictionary<string, List<Lab>> LabsByCity = new()
        {
            ["kochi"] = new()
            {
                new Lab { Id = "lrl", Name = "Lal PathLabs — Kochi", Area = "MG Road, Ernakulam", Rating = "4.7★", Distance = "1.2 km", Rate = 450 },
                new Lab { Id = "srl", Name = "SRL Diagnostics", Area = "Palarivattom, Kochi", Rating = "4.5★", Distance = "2.4 km", Rate = 420 },
                new Lab { Id = "thyro", Name = "Thyrocare — Kakkanad", Area = "Kakkanad, Kochi", Rating = "4.6★", Distance = "3.8 km", Rate = 380 },
            },
            ["trivandrum"] = new()
            {
                new Lab { Id = "met", Name = "Metro Diagnostics", Area = "Palayam, Trivandrum", Rating = "4.4★", Distance = "0.9 km", Rate = 400 },
                new Lab { Id = "health", Name = "HealthCare Labs", Area = "Kowdiar, Trivandrum", Rating = "4.6★", Distance = "2.1 km", Rate = 430 },
            }
        };

        public static readonly List<TimeSlot> Slots = new()
        {
            new TimeSlot { Time = "06:00 AM", Available = true },
            new TimeSlot { Time = "07:00 AM", Available = true },
            new TimeSlot { Time = "08:00 AM", Available = false },
            new TimeSlot { Time = "09:00 AM", Available = true },
            new TimeSlot { Time = "10:00 AM", Available = false },
            new TimeSlot { Time = "11:00 AM", Available = true },
            new TimeSlot { Time = "12:00 PM", Available = true },
            new TimeSlot { Time = "02:00 PM", Available = true },
            new TimeSlot { Time = "03:00 PM", Available = false },
            new TimeSlot { Time = "04:00 PM", Available = true },
        };
    }

    public class Lab
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Area { get; set; } = "";
        public string Rating { get; set; } = "";
        public string Distance { get; set; } = "";
        public int Rate { get; set; }
    }

    public class TimeSlot
    {
        public string Time { get; set; } = "";
        public bool Available { get; set; }
    }

    // ===================================================
    // Controller
    // ===================================================
    [ApiController]
    [Route("api/whatsapp/webhook")]
    public class WebhookController : ControllerBase
    {
        private const string VERIFY_TOKEN = "MySuperSecretToken123";
        private const string WA_API_VERSION = "v25.0";
        private const string PHONE_NUMBER_ID = "1198940716632437";
        private const string ACCESS_TOKEN = "EAAOLfrg5aQcBR6rZAn4kZCqpXJM5BvvJ7KtSSyuqMVxWNwHCM4pRGkN9ZCk36yiYz2JmOPyaagbKXebQ5rYmg0aRON9BQFg8BI9oC30dQkFIIoMQIIkai4JqNtTZCZA6M7mNQbE4MQhXed9QJmBw5t0zC70MdK0GeaNv94L2sc2IATTTHzSdAAmB2S85eno85i8ykizpoCbZCqbZCZAaVXmpFe3rymd2eu9CLhmCfuf6IzXJDq0RZA1tDGY4fm9znXqs1YKAx0SH5UOlyZAWY021ZBdavWuPXZCB3mt6sf4ZBpQZDZD";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IApplicationDbContext _context;

        public WebhookController(IHttpClientFactory httpClientFactory, IApplicationDbContext context)
        {
            _httpClientFactory = httpClientFactory;
            _context = context;
        }

        // ===================================================
        // GET: Meta Webhook Verification
        // ===================================================
        [HttpGet]
        public IActionResult VerifyWebhook(
            [FromQuery(Name = "hub.mode")] string mode,
            [FromQuery(Name = "hub.verify_token")] string token,
            [FromQuery(Name = "hub.challenge")] string challenge)
        {
            Console.WriteLine("====================================");
            Console.WriteLine("Webhook Verification Request");
            Console.WriteLine("====================================");
            Console.WriteLine($"Mode      : {mode}");
            Console.WriteLine($"Token     : {token}");
            Console.WriteLine($"Challenge : {challenge}");

            if (mode == "subscribe" && token == VERIFY_TOKEN)
            {
                Console.WriteLine("Verification Successful");
                return Content(challenge, "text/plain");
            }

            Console.WriteLine("Verification Failed");
            return StatusCode(403);
        }

        // ===================================================
        // POST: Receive & Process WhatsApp Messages
        // ===================================================
        [HttpPost]
        public async Task<IActionResult> ReceiveWebhook()
        {
            Console.WriteLine("====================================");
            Console.WriteLine("Incoming WhatsApp Webhook");
            Console.WriteLine("====================================");

            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            Console.WriteLine(body);

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                var entries = root.GetProperty("entry");
                foreach (var entry in entries.EnumerateArray())
                {
                    var changes = entry.GetProperty("changes");
                    foreach (var change in changes.EnumerateArray())
                    {
                        var value = change.GetProperty("value");
                        if (!value.TryGetProperty("messages", out var messages)) continue;

                        foreach (var message in messages.EnumerateArray())
                        {
                            var from = message.GetProperty("from").GetString()!;
                            var msgType = message.GetProperty("type").GetString();

                            Console.WriteLine($"📩 Message from: {from} | Type: {msgType}");

                            // Auto-register patient/customer if they don't exist yet
                            var user = await _context.Users.FirstOrDefaultAsync(u => u.Phone == from);
                            if (user == null)
                            {
                                Console.WriteLine($"👤 Creating new Customer user for phone {from} via WhatsApp auto-registration");
                                user = new User
                                {
                                    Phone = from,
                                    Role = UserRole.Customer
                                };
                                _context.Users.Add(user);

                                var customer = new Customer
                                {
                                    UserId = user.Id,
                                    Phone = from,
                                    Name = "WhatsApp Customer"
                                };
                                _context.Customers.Add(customer);
                                await _context.SaveChangesAsync();
                            }

                            if (msgType == "text")
                            {
                                var text = message.GetProperty("text").GetProperty("body").GetString()!;
                                Console.WriteLine($"   Text: {text}");
                                await ProcessTextMessage(from, text);
                            }
                            else if (msgType == "location")
                            {
                                var lat = message.GetProperty("location").GetProperty("latitude").GetDouble();
                                var lng = message.GetProperty("location").GetProperty("longitude").GetDouble();
                                Console.WriteLine($"   Location: {lat}, {lng}");
                                await ProcessLocationMessage(from, lat, lng);
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
                                    Console.WriteLine($"   Interactive: {replyId} — {replyTitle}");
                                    await ProcessInteractiveReply(from, replyId, replyTitle ?? "");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error processing webhook: {ex.Message}");
            }

            return Ok();
        }

        // ===================================================
        // Main message router
        // ===================================================
        private async Task ProcessTextMessage(string to, string text)
        {
            var session = await GetOrCreateSessionAsync(to);
            var lower = text.ToLower().Trim();

            // Greeting triggers — restart flow
            if (IsGreeting(lower))
            {
                session.CurrentState = WhatsAppState.Start;
                await SaveSessionAsync(session);
                await SendGreeting(to);
                return;
            }

            // Step-based text input handling
            switch (session.CurrentState)
            {
                case WhatsAppState.Start:
                    await SendGreeting(to);
                    break;

                case WhatsAppState.MemberCount:
                    if (int.TryParse(text.Trim(), out int count) && count >= 1 && count <= 6)
                    {
                        session.MemberCount = count;
                        session.CurrentState = WhatsAppState.Location;
                        await SaveSessionAsync(session);
                        await SendLocationRequest(to);
                    }
                    else
                    {
                        await SendTextMessage(to, "Please enter a number between 1 and 6. How many people need the blood test?");
                    }
                    break;

                default:
                    // Catch-all: show main menu
                    await SendGreeting(to);
                    break;
            }
        }

        private async Task ProcessLocationMessage(string to, double lat, double lng)
        {
            var session = await GetOrCreateSessionAsync(to);

            if (session.CurrentState == WhatsAppState.Location)
            {
                session.LocationShared = true;
                session.CurrentState = WhatsAppState.Confirm;
                await SaveSessionAsync(session);

                var summary = BuildBookingSummary(session);
                await SendTextMessage(to,
                    $"📍 Location received!\n\n{summary}\n\n" +
                    "💳 To confirm your booking, complete the payment below.\n\n" +
                    "👉 Reply *PAY* to simulate payment (demo mode)");
            }
        }

        private async Task ProcessInteractiveReply(string to, string replyId, string replyTitle)
        {
            var session = await GetOrCreateSessionAsync(to);
            Console.WriteLine($"🔀 Step: {session.CurrentState} | Reply: {replyId}");

            switch (session.CurrentState)
            {
                // ── Main Menu ──────────────────────────────────────────
                case WhatsAppState.Start:
                    if (replyId == "menu_book")
                    {
                        session.CurrentState = WhatsAppState.ChoosingTest;
                        await SaveSessionAsync(session);
                        await SendServiceList(to);
                    }
                    else if (replyId == "menu_bookings")
                    {
                        await SendViewBookings(to);
                    }
                    else if (replyId == "menu_help")
                    {
                        await SendHelp(to);
                    }
                    break;

                // ── Service Selection ──────────────────────────────────
                case WhatsAppState.ChoosingTest:
                    session.SelectedTestId = replyId; // e.g. "service_blood"
                    if (replyId == "service_blood")
                    {
                        session.CurrentState = WhatsAppState.ChoosingCity;
                        await SaveSessionAsync(session);
                        await SendCityList(to);
                    }
                    else
                    {
                        await SendTextMessage(to, "This service is coming soon! Let's book a Blood Test for now.");
                        session.CurrentState = WhatsAppState.ChoosingCity;
                        await SaveSessionAsync(session);
                        await SendCityList(to);
                    }
                    break;

                // ── City Selection ─────────────────────────────────────
                case WhatsAppState.ChoosingCity:
                    var city = replyId.Replace("city_", "");
                    if (DemoData.LabsByCity.ContainsKey(city))
                    {
                        session.SelectedCity = city;
                        session.CurrentState = WhatsAppState.ChoosingLab;
                        await SaveSessionAsync(session);
                        await SendLabList(to, city);
                    }
                    else
                    {
                        await SendTextMessage(to, "We don't have labs in that city yet. Please choose Kochi or Trivandrum.");
                        await SendCityList(to);
                    }
                    break;

                // ── Lab Selection ──────────────────────────────────────
                case WhatsAppState.ChoosingLab:
                    var labId = replyId.Replace("lab_", "");
                    var labs = DemoData.LabsByCity.GetValueOrDefault(session.SelectedCity ?? "", new List<Lab>());
                    var selectedLab = labs.Find(l => l.Id == labId);
                    if (selectedLab != null)
                    {
                        session.SelectedLabId = selectedLab.Id;
                        session.SelectedLabName = selectedLab.Name;
                        session.CurrentState = WhatsAppState.ChoosingSlot;
                        await SaveSessionAsync(session);
                        await SendSlotList(to, selectedLab.Name);
                    }
                    break;

                // ── Slot Selection ─────────────────────────────────────
                case WhatsAppState.ChoosingSlot:
                    var slotTime = replyId.Replace("slot_", "").Replace("_", " ");
                    session.SelectedSlot = slotTime;
                    session.CurrentState = WhatsAppState.MemberCount;
                    await SaveSessionAsync(session);
                    await SendPersonCountPrompt(to);
                    break;

                // ── Payment Trigger (via button) ───────────────────────
                case WhatsAppState.Confirm:
                    if (replyId == "pay_now")
                    {
                        await SimulatePayment(to, session);
                    }
                    break;

                // ── Post-booking ───────────────────────────────────────
                case WhatsAppState.Done:
                    if (replyId == "menu_book") 
                    { 
                        session.CurrentState = WhatsAppState.ChoosingTest; 
                        await SaveSessionAsync(session);
                        await SendServiceList(to); 
                    }
                    else if (replyId == "menu_bookings") 
                    { 
                        await SendViewBookings(to); 
                    }
                    else 
                    { 
                        session.CurrentState = WhatsAppState.Start; 
                        await SaveSessionAsync(session);
                        await SendGreeting(to); 
                    }
                    break;
            }
        }

        // ===================================================
        // Message Builders
        // ===================================================
        private async Task SendGreeting(string to)
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

            await SendWhatsAppMessage(payload);
        }

        private async Task SendServiceList(string to)
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

            await SendWhatsAppMessage(payload);
        }

        private async Task SendCityList(string to)
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

            await SendWhatsAppMessage(payload);
        }

        private async Task SendLabList(string to, string city)
        {
            var labs = DemoData.LabsByCity.GetValueOrDefault(city, new List<Lab>());
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

            await SendWhatsAppMessage(payload);
        }

        private async Task SendSlotList(string to, string labName)
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

            await SendWhatsAppMessage(payload);
        }

        private async Task SendPersonCountPrompt(string to)
        {
            await SendTextMessage(to,
                "👥 *How many people need the blood test?*\n\n" +
                "Reply with a number (1–6).\n" +
                "e.g. reply *2* for 2 family members.");
        }

        private async Task SendLocationRequest(string to)
        {
            await SendTextMessage(to,
                "📍 *Share your location*\n\n" +
                "Please tap the paperclip 📎 → Location → and share your current location.\n\n" +
                "This helps us confirm the nearest branch and arrange home sample collection if needed.");
        }

        private async Task SimulatePayment(string to, WhatsAppSession session)
        {
            session.CurrentState = WhatsAppState.Done;
            await SaveSessionAsync(session);

            var bookingId = $"BK-{DateTime.UtcNow:yyyyMMdd}-{new Random().Next(1000, 9999)}";
            var labs = DemoData.LabsByCity.GetValueOrDefault(session.SelectedCity ?? "", new List<Lab>());
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

            await SendWhatsAppMessage(payload);
        }

        private async Task SendViewBookings(string to)
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
                "Reply *hi* to return to the main menu.");
        }

        private async Task SendHelp(string to)
        {
            await SendTextMessage(to,
                "❓ *Help & FAQ*\n\n" +
                "🩸 *Blood Test* — Fasting 8–10 hrs required\n" +
                "📍 *Location* — Kochi & Trivandrum available\n" +
                "📲 *Reports* — Sent via WhatsApp in 24 hrs\n" +
                "💰 *Payment* — UPI, card, net banking\n" +
                "📞 *Support* — 1800-123-4567 (Mon–Sat, 8am–8pm)\n\n" +
                "Reply *hi* to return to the main menu.");
        }

        // ===================================================
        // WhatsApp API Sender
        // ===================================================
        private async Task SendTextMessage(string to, string text)
        {
            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "text",
                text = new { body = text }
            };
            await SendWhatsAppMessage(payload);
        }

        private async Task SendWhatsAppMessage(object payload)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                var url = $"https://graph.facebook.com/{WA_API_VERSION}/{PHONE_NUMBER_ID}/messages";
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ACCESS_TOKEN);

                Console.WriteLine($"📤 Sending to {url}");
                Console.WriteLine(json);

                var response = await client.PostAsync(url, content);
                var responseBody = await response.Content.ReadAsStringAsync();

                Console.WriteLine($"📬 WA Response [{(int)response.StatusCode}]: {responseBody}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to send WhatsApp message: {ex.Message}");
            }
        }

        // ===================================================
        // Helpers
        // ===================================================
        private async Task<WhatsAppSession> GetOrCreateSessionAsync(string phone)
        {
            var session = await _context.WhatsAppSessions.FirstOrDefaultAsync(s => s.Phone == phone);
            if (session == null)
            {
                session = new WhatsAppSession
                {
                    Phone = phone,
                    CurrentState = WhatsAppState.Start,
                    UpdatedAt = DateTime.UtcNow
                };
                _context.WhatsAppSessions.Add(session);
                await _context.SaveChangesAsync();
            }
            return session;
        }

        private async Task SaveSessionAsync(WhatsAppSession session)
        {
            session.UpdatedAt = DateTime.UtcNow;
            _context.WhatsAppSessions.Update(session);
            await _context.SaveChangesAsync();
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