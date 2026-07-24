using System;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Apenir.Core.Interfaces;

using Apenir.Application.Common.Interfaces;

namespace Apenir.Infrastructure.Services;

public class WhatsAppService : IWhatsAppService
{
    private readonly ISettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;

    public WhatsAppService(ISettingsService settingsService, IHttpClientFactory httpClientFactory)
    {
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
    }

    public async Task SendTextMessageAsync(string toPhone, string message)
    {
        try
        {
            var accessToken = await _settingsService.GetWhatsAppAccessTokenAsync();
            var phoneNumberId = await _settingsService.GetWhatsAppPhoneNumberIdAsync();
            var apiVersion = await _settingsService.GetWhatsAppApiVersionAsync() ?? "v25.0";

            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(phoneNumberId))
            {
                Console.WriteLine("[META WHATSAPP SERVICE]: AccessToken or PhoneNumberId not configured. Logging outbound message instead.");
                Console.WriteLine($"[META WHATSAPP API OUTBOUND to {toPhone}]: {message}");
                return;
            }

            // Sanitize phone number (remove any non-digit characters like +, spaces, dashes, etc.)
            var sanitizedPhone = new string(toPhone.Where(char.IsDigit).ToArray());

            var payload = new
            {
                messaging_product = "whatsapp",
                to = sanitizedPhone,
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

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Meta API error (Status {response.StatusCode}): {responseBody}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[META WHATSAPP SERVICE] Exception: {ex.Message}");
            throw; // Re-throw to propagate exception for settings test trigger and logs
        }
    }

    public async Task SendDocumentMessageAsync(string toPhone, string url, string filename)
    {
        try
        {
            var accessToken = await _settingsService.GetWhatsAppAccessTokenAsync();
            var phoneNumberId = await _settingsService.GetWhatsAppPhoneNumberIdAsync();
            var apiVersion = await _settingsService.GetWhatsAppApiVersionAsync() ?? "v25.0";

            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(phoneNumberId))
            {
                Console.WriteLine("[META WHATSAPP SERVICE]: AccessToken or PhoneNumberId not configured. Logging outbound document instead.");
                Console.WriteLine($"[META WHATSAPP API OUTBOUND DOCUMENT to {toPhone}]: Link={url}, Filename={filename}");
                return;
            }

            // Sanitize phone number (remove any non-digit characters like +, spaces, dashes, etc.)
            var sanitizedPhone = new string(toPhone.Where(char.IsDigit).ToArray());

            var payload = new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = sanitizedPhone,
                type = "document",
                document = new
                {
                    link = url,
                    filename = filename
                }
            };

            var client = _httpClientFactory.CreateClient();
            var targetUrl = $"https://graph.facebook.com/{apiVersion}/{phoneNumberId}/messages";
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            Console.WriteLine($"[META WHATSAPP SERVICE] 📤 Sending document request to {targetUrl}");
            var response = await client.PostAsync(targetUrl, content);
            var responseBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"[META WHATSAPP SERVICE] 📬 Status: {response.StatusCode} | Body: {responseBody}");

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Meta API error (Status {response.StatusCode}): {responseBody}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[META WHATSAPP SERVICE] Exception: {ex.Message}");
            throw; // Re-throw to propagate exception for settings test trigger and logs
        }
    }

    public static string HashOtp(string otp)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(otp));
        return Convert.ToHexString(bytes).ToLower();
    }
}