using Microsoft.AspNetCore.Mvc;

namespace Apenir.API.Controllers
{
    [ApiController]
     [Route("api/[controller]")]
    public class WebhookController : ControllerBase
    {
        // Replace this with your actual environment variable or configuration string
        private const string WebhookVerifyToken = "MySuperSecretToken123";

        /// <summary>
        /// GET /webhook
        /// Handles the validation handshake from Meta.
        /// </summary>
        [HttpGet]
        public IActionResult VerifyWebhook(
            [FromQuery(Name = "hub.mode")] string mode,
            [FromQuery(Name = "hub.verify_token")] string token,
            [FromQuery(Name = "hub.challenge")] string challenge)
        {
            // Translates: if (mode && token === WEBHOOK_VERIFY_TOKEN)
            if (!string.IsNullOrEmpty(mode) && token == WebhookVerifyToken)
            {
                // Translates: res.status(200).send(challenge)
                return Ok(challenge);
            }

            // Translates: res.sendStatus(403)
            return Forbid();
        }

        /// <summary>
        /// POST /webhook
        /// Catches and logs all incoming webhook payload event data.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> ReceiveMessage()
        {
            Console.WriteLine("disco disco");
            // Translates reading the body stream directly to capture arbitrary JSON
            using var reader = new StreamReader(Request.Body);
            string jsonBody = await reader.ReadToEndAsync();

            // Translates: console.log(JSON.stringify(req.body, null, 2))
            Console.WriteLine("📥 Received Webhook Payload:");
            Console.WriteLine(jsonBody);

            // Translates: res.status(200).send('Webhook processed')
            return Ok("Webhook processed");
        }
    }
}