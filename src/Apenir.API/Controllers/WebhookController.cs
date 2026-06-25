using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;

namespace YourProjectNamespace.Controllers
{
    [ApiController]
    [Route("api/[controller]")] 
    public class WebhookController : ControllerBase
    {
        private const string VerifyToken = "MySuperSecretToken123";
        
        // TODO: Replace with your actual Phone Number ID and System User Access Token from Meta
        private const string MetaPhoneId = "1198940716632437";
        private const string MetaAccessToken = "EAASkQDqaY3wBR6N03AbkTtsN6XTCPA93DfOtZCECnQVRgT245wWNC5KyEKjH4FprQuUwAEoDl3lSmxd5XUrImzHkBDQZBcspZC9PnrUL3UmoXafqkiQyk3YEg0U6VAFwSvtnmuwaHAFx2R6nqvNpLNny9LIVGM1YqvGWe5yMDUT1cqd3GlOHnYPZCDPOnS3b716yIA5d3DuG3JrZBffLG1zXdEnrxttyTux0n0kkATerMGKKXVP1L8Nh0HAhj8Qje2Ik8ffPFK9esvWaEpmZBQQQo2";

        /// <summary>
        /// 1. THE HANDSHAKE (GET)
        /// Verified successfully over api.anandhu-kannan.in via Cloudflare.
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
        /// Processes messages and button responses in real-time.
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

                        // CASE 1: Incoming Text (User says "Hi")
                        if (messageType == "text")
                        {
                            string textBody = firstMessage.GetProperty("text").GetProperty("body").GetString();
                            Console.WriteLine($"Text: {textBody}");

                            if (textBody.Equals("Hi", StringComparison.OrdinalIgnoreCase) || textBody.Equals("Hello", StringComparison.OrdinalIgnoreCase))
                            {
                                // Trigger the automated reply menu buttons
                                await SendWelcomeButtonsAsync(fromNumber);
                            }
                        }

                        // CASE 2: Incoming Button Click Response
                        else if (messageType == "interactive")
                        {
                            var interactive = firstMessage.GetProperty("interactive");
                            
                            if (interactive.TryGetProperty("button_reply", out var buttonReply))
                            {
                                string buttonId = buttonReply.GetProperty("id").GetString();
                                Console.WriteLine($"Button ID Clicked: {buttonId}");

                                if (buttonId == "btn_booking")
                                {
                                    // User tapped "Book a Slot" -> Send them the interactive availability slots list!
                                    await SendAvailableSlotsListAsync(fromNumber);
                                }
                                else if (buttonId == "btn_enquiry")
                                {
                                    // User tapped "General Enquiry" -> Ask for their question
                                    await SendTextMessageAsync(fromNumber, "Please type out your question, and our team will get back to you shortly!");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error parsing json: {ex.Message}");
            }

            return Ok();
        }

        #region Outbound Meta API Integrations

        private async Task SendWelcomeButtonsAsync(string toPhoneNumber)
        {
            using var client = new HttpClient();
            string url = $"https://graph.facebook.com/v25.0/{MetaPhoneId}/messages";
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MetaAccessToken);

            var payload = new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = toPhoneNumber,
                type = "interactive",
                interactive = new
                {
                    type = "button",
                    body = new { text = "Welcome! How can we assist you today? Please choose an option below to proceed." },
                    action = new
                    {
                        buttons = new[]
                        {
                            new { type = "reply", reply = new { id = "btn_booking", title = "Book a Slot 📅" } },
                            new { type = "reply", reply = new { id = "btn_enquiry", title = "General Enquiry 💬" } }
                        }
                    }
                }
            };

            string jsonPayload = JsonSerializer.Serialize(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(url, content);
            if (!response.IsSuccess