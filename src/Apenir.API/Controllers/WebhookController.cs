using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace YourProjectNamespace.Controllers
{
    [ApiController]
    [Route("api/[controller]")] // This makes the URL: /api/webhook
    public class WebhookController : ControllerBase
    {
        // Define a secret token. This MUST match exactly what you type into the Meta Dashboard.
        private const string VerifyToken = "MySuperSecretToken123";

        /// <summary>
        /// 1. THE HANDSHAKE (GET)
        /// Meta hits this once to verify your server exists.
        /// </summary>
        [HttpGet]
        public IActionResult VerifyWebhook(
            [FromQuery(Name = "hub.mode")] string mode,
            [FromQuery(Name = "hub.verify_token")] string token,
            [FromQuery(Name = "hub.challenge")] string challenge)
        {
            if (mode == "subscribe" && token == VerifyToken)
            {
                Console.WriteLine("✅ Webhook verified successfully by Meta!");
                return Ok(challenge); // You must return plain text containing just the challenge string
            }

            return Forbid(); // Return 403 if token mismatch
        }

        /// <summary>
        /// 2. LIVE MESSAGES (POST)
        /// WhatsApp hits this every time a customer sends a text or clicks a button.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> ReceiveMessage()
        {
            // Read the raw JSON from the incoming body
            using var reader = new StreamReader(Request.Body);
            string jsonString = await reader.ReadToEndAsync();

            try
            {
                using JsonDocument doc = JsonDocument.Parse(jsonString);
                JsonElement root = doc.RootElement;

                // Safely drill down to check if this is a WhatsApp message array
                if (root.TryGetProperty("object", out var objElement) && objElement.GetString() == "whatsapp_business_account")
                {
                    var entry = root.GetProperty("entry")[0];
                    var changes = entry.GetProperty("changes")[0];
                    var value = changes.GetProperty("value");

                    if (value.TryGetProperty("messages", out var messagesElement))
                    {
                        var firstMessage = messagesElement[0];
                        string fromNumber = firstMessage.GetProperty("from").GetString();
                        string messageType = firstMessage.GetProperty("type").GetString();

                        Console.WriteLine($"📩 Inbound Message Received from: {fromNumber}");

                        // CASE 1: Standard Text Message
                        if (messageType == "text")
                        {
                            string textBody = firstMessage.GetProperty("text").GetProperty("body").GetString();
                            Console.WriteLine($"Text: {textBody}");

                            // 💡 TODO: Pass textBody to your Booking Engine logic here!
                        }

                        // CASE 2: User Clicked a Template/Interactive Button
                        else if (messageType == "interactive")
                        {
                            var interactive = firstMessage.GetProperty("interactive");
                            string buttonId = interactive.GetProperty("button_reply").GetProperty("id").GetString();
                            Console.WriteLine($"Button ID Clicked: {buttonId}");

                            // 💡 TODO: Process their booking choice (e.g., slot time selected)
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error parsing json: {ex.Message}");
            }

            // Always return 200 OK fast so WhatsApp doesn't continuously retry sending the same message
            return Ok();
        }
    }
}