using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;

namespace Apenir.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")] 
    public class WebhookController : ControllerBase
    {
        private const string VerifyToken = "MySuperSecretToken123";
        
        // Meta Test Environment Credentials
        private const string MetaPhoneId = "1198940716632437"; 
        private const string MetaAccessToken = "EAASkQDqaY3wBR6N03AbkTtsN6XTCPA93DfOtZCECnQVRgT245wWNC5KyEKjH4FprQuUwAEoDl3lSmxd5XUrImzHkBDQZBcspZC9PnrUL3UmoXafqkiQyk3YEg0U6VAFwSvtnmuwaHAFx2R6nqvNpLNny9LIVGM1YqvGWe5yMDUT1cqd3GlOHnYPZCDPOnS3b716yIA5d3DuG3JrZBffLG1zXdEnrxttyTux0n0kkATerMGKKXVP1L8Nh0HAhj8Qje2Ik8ffPFK9esvWaEpmZBQQQo2";

        /// <summary>
        /// 1. THE HANDSHAKE (GET)
        /// Validates your server endpoint connection inside the Meta Dashboard.
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
                return Ok(challenge);
            }

            return Forbid();
        }

        /// <summary>
        /// 2. LIVE MESSAGES (POST)
        /// Catches incoming messages from Meta and triggers the auto-reply.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> ReceiveMessage()
        {
            using var reader = new StreamReader(Request.Body);
            string jsonString = await reader.ReadToEndAsync();

            try
            {
                using JsonDocument doc = JsonDocument.Parse(jsonString);
                JsonElement root = doc.RootElement;

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

                        Console.WriteLine($"📩 Inbound Message Received from: {fromNumber} | Type: {messageType}");

                        if (messageType == "text")
                        {
                            string textBody = firstMessage.GetProperty("text").GetProperty("body").GetString()?.Trim();
                            Console.WriteLine($"Text Body: {textBody}");

                            // Match "Hi" or "Hello" case-insensitively
                            if (textBody.Equals("Hi", StringComparison.OrdinalIgnoreCase) || 
                                textBody.Equals("Hello", StringComparison.OrdinalIgnoreCase))
                            {
                                // Send direct response back to the user
                                await SendTextMessageAsync(fromNumber, "Hi, Welcome!");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error parsing json: {ex.Message}");
            }

            return Ok(); // Always return 200 OK to Meta
        }

        #region Outbound Meta API Integrations

        /// <summary>
        /// Sends a direct plain-text message back to the sender via Meta Graph API.
        /// </summary>
        private async Task SendTextMessageAsync(string toPhoneNumber, string messageText)
        {
            using var client = new HttpClient();
            string url = $"https://facebook.com{MetaPhoneId}/messages";
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MetaAccessToken);

            var payload = new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = toPhoneNumber,
                type = "text",
                text = new { body = messageText }
            };

            string jsonPayload = JsonSerializer.Serialize(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ Error sending Text Message: {error}");
            }
            else
            {
                Console.WriteLine($"✅ Successfully replied to {toPhoneNumber}");
            }
        }

        #endregion
    }
}
