using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Threading.Tasks;

namespace Apenir.API.Controllers
{
    [ApiController]
    [Route("api/webhook")]   // IMPORTANT: matches Meta URL
    public class WebhookController : ControllerBase
    {
        private const string WebhookVerifyToken = "MySuperSecretToken123";

        /// <summary>
        /// Meta webhook verification (GET)
        /// </summary>
        [HttpGet]
        public IActionResult VerifyWebhook(
            [FromQuery(Name = "hub.mode")] string mode,
            [FromQuery(Name = "hub.verify_token")] string token,
            [FromQuery(Name = "hub.challenge")] string challenge)
        {
            Console.WriteLine("🔐 Webhook verification request received");

            if (!string.IsNullOrEmpty(mode) && token == WebhookVerifyToken)
            {
                Console.WriteLine("✅ Webhook verified successfully");
                return Content(challenge, "text/plain"); // IMPORTANT
            }

            Console.WriteLine("❌ Webhook verification failed");
            return Forbid();
        }

        /// <summary>
        /// Meta webhook event receiver (POST)
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> ReceiveMessage()
        {
            Console.WriteLine("📥 Incoming WhatsApp webhook hit");

            using var reader = new StreamReader(Request.Body);
            string jsonBody = await reader.ReadToEndAsync();

            Console.WriteLine("📦 Payload:");
            Console.WriteLine(jsonBody);

            return Ok("Webhook processed");
        }
    }
}