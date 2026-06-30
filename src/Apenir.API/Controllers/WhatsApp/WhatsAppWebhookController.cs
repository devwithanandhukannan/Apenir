using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Apenir.Core.Interfaces;
using Apenir.Core.Entities;
using Apenir.Core.Enums;
using Apenir.API.BackgroundServices;

namespace Apenir.API.Controllers
{
    // ===================================================
    // Hardcoded demo data
    // ===================================================
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
        private const string DEFAULT_VERIFY_TOKEN = "MySuperSecretToken123";
        private readonly IWhatsAppWebhookQueue _queue;
        private readonly IConfiguration _configuration;

        public WebhookController(IWhatsAppWebhookQueue queue, IConfiguration configuration)
        {
            _queue = queue;
            _configuration = configuration;
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

            var verifyToken = _configuration["WhatsApp:VerifyToken"] ?? DEFAULT_VERIFY_TOKEN;

            if (mode == "subscribe" && token == verifyToken)
            {
                Console.WriteLine("✅ Verification Successful");
                return Content(challenge, "text/plain");
            }

            Console.WriteLine("❌ Verification Failed");
            return StatusCode(403);
        }

        // ===================================================
        // POST: Receive & Process WhatsApp Messages (Async Queue)
        // ===================================================
        [HttpPost]
        public async Task<IActionResult> ReceiveWebhook()
        {
            Console.WriteLine("====================================");
            Console.WriteLine("Incoming WhatsApp Webhook");
            Console.WriteLine("====================================");

            // Enable buffering so we can read the stream for signature and later reset it
            Request.EnableBuffering();

            byte[] bodyBytes;
            using (var ms = new MemoryStream())
            {
                await Request.Body.CopyToAsync(ms);
                bodyBytes = ms.ToArray();
            }

            // Reset the body stream position so other reader/parsers can access it
            Request.Body.Position = 0;

            var body = Encoding.UTF8.GetString(bodyBytes);
            Console.WriteLine(body);

            // Verify X-Hub-Signature-256
            if (Request.Headers.TryGetValue("X-Hub-Signature-256", out var signatureHeader))
            {
                if (!VerifySignature(signatureHeader.ToString(), bodyBytes))
                {
                    Console.WriteLine("❌ Webhook Signature Verification Failed");
                    return Unauthorized("Invalid webhook signature");
                }
                Console.WriteLine("✅ Webhook Signature Verified");
            }
            else
            {
                Console.WriteLine("⚠️ X-Hub-Signature-256 header missing from incoming webhook");
                // In production, you might want to return Unauthorized here if app secret is configured
                var appSecret = _configuration["WhatsApp:AppSecret"];
                if (!string.IsNullOrEmpty(appSecret))
                {
                    return Unauthorized("Missing webhook signature");
                }
            }

            // Queue the raw body for background processing
            _queue.QueueWebhook(body);

            return Ok();
        }

        private bool VerifySignature(string signatureHeader, byte[] bodyBytes)
        {
            var appSecret = _configuration["WhatsApp:AppSecret"];
            if (string.IsNullOrEmpty(appSecret))
            {
                // If no AppSecret is configured, bypass verification (useful for dev/testing)
                Console.WriteLine("⚠️ Bypassing Webhook Signature Verification (WhatsApp:AppSecret is not configured)");
                return true;
            }

            if (string.IsNullOrEmpty(signatureHeader) || !signatureHeader.StartsWith("sha256="))
            {
                return false;
            }

            var expectedSignature = signatureHeader.Substring("sha256=".Length);
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
            var hashBytes = hmac.ComputeHash(bodyBytes);
            var computedSignature = Convert.ToHexString(hashBytes).ToLower();

            return string.Equals(expectedSignature, computedSignature, StringComparison.OrdinalIgnoreCase);
        }
    }
}