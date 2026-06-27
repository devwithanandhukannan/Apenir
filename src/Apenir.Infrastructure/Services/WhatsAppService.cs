using System;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Apenir.Core.Interfaces;

namespace Apenir.Infrastructure.Services;

public class WhatsAppService : IWhatsAppService
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    public WhatsAppService(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    public async Task SendTextMessageAsync(string toPhone, string message)
    {
        try
        {
            var accessToken = _configuration["WhatsApp:AccessToken"];
            var phoneNumberId = _configuration["WhatsApp:PhoneNumberId"];
            var apiVersion = _configuration["WhatsApp:ApiVersion"] ?? "v25.0";

            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(phoneNumberId))
            {
                Console.WriteLine("[META WHATSAPP SERVICE]: AccessToken or PhoneNumberId not configured. Logging outbound message instead.");
                Console.WriteLine($"[META WHATSAPP API OUTBOUND to {toPhone}]: {message}");
                return;
            }

            var payload = new
            {
                messaging_product = "whatsapp",
                to = toPhone,
                type = "text",
                text = new { body = message }
            };

            var client = _httpClientFactory.CreateClient();
            var url = $"https://graph.facebook.com/{apiVersion}/{phoneNumberId}/messages";
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            Console.WriteLine($"[META WHATSAPP SERVICE] 📤 Sending request to {url}");
            var response = await client.PostAsync(url, content);
            var responseBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"[META WHATSAPP SERVICE] 📬 Status: {response.StatusCode} | Body: {responseBody}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[META WHATSAPP SERVICE] Exception: {ex.Message}");
        }
    }

    public static string HashOtp(string otp)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(otp));
        return Convert.ToHexString(bytes).ToLower();
    }
}