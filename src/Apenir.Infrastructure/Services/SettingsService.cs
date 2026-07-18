using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Apenir.Core.Entities;
using Apenir.Core.Interfaces;
using Apenir.Application.Common.Interfaces;
using Apenir.Application.Common.Models;

namespace Apenir.Infrastructure.Services;

public class SettingsService : ISettingsService
{
    private readonly IApplicationDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly string _encryptionKey;

    public SettingsService(IApplicationDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
        _encryptionKey = configuration["JwtSettings:Secret"] ?? "fallback-encryption-key-for-apenir-settings";
    }

    public async Task<SystemSettings> GetSettingsAsync()
    {
        var settings = await _context.SystemSettings.FirstOrDefaultAsync();
        if (settings == null)
        {
            settings = new SystemSettings
            {
                WhatsAppAccessToken = _configuration["WhatsApp:AccessToken"],
                WhatsAppPhoneNumberId = _configuration["WhatsApp:PhoneNumberId"],
                WhatsAppApiVersion = _configuration["WhatsApp:ApiVersion"] ?? "v25.0",
                RazorpayKeyId = _configuration["Razorpay:KeyId"],
                RazorpayKeySecret = _configuration["Razorpay:KeySecret"],
                RazorpayWebhookSecret = _configuration["Razorpay:WebhookSecret"],
                SmtpHost = _configuration["SmtpSettings:Host"],
                SmtpPort = int.TryParse(_configuration["SmtpSettings:Port"], out var port) ? port : 587,
                SmtpUsername = _configuration["SmtpSettings:Username"],
                SmtpPassword = _configuration["SmtpSettings:Password"],
                SmtpEnableSsl = bool.TryParse(_configuration["SmtpSettings:EnableSsl"], out var ssl) && ssl,
                SmtpFromEmail = _configuration["SmtpSettings:FromEmail"]
            };

            if (!string.IsNullOrEmpty(settings.WhatsAppAccessToken))
                settings.WhatsAppAccessToken = EncryptionHelper.Encrypt(settings.WhatsAppAccessToken, _encryptionKey);
            if (!string.IsNullOrEmpty(settings.RazorpayKeySecret))
                settings.RazorpayKeySecret = EncryptionHelper.Encrypt(settings.RazorpayKeySecret, _encryptionKey);
            if (!string.IsNullOrEmpty(settings.SmtpPassword))
                settings.SmtpPassword = EncryptionHelper.Encrypt(settings.SmtpPassword, _encryptionKey);

            _context.SystemSettings.Add(settings);
            await _context.SaveChangesAsync();
        }
        else
        {
            bool updated = false;

            var configWaToken = _configuration["WhatsApp:AccessToken"];
            if (!string.IsNullOrEmpty(configWaToken))
            {
                string? decryptedToken = null;
                try { decryptedToken = settings.WhatsAppAccessToken != null ? EncryptionHelper.Decrypt(settings.WhatsAppAccessToken, _encryptionKey) : null; } catch { decryptedToken = settings.WhatsAppAccessToken; }
                if (decryptedToken != configWaToken)
                {
                    settings.WhatsAppAccessToken = EncryptionHelper.Encrypt(configWaToken, _encryptionKey);
                    updated = true;
                }
            }

            var configWaPhone = _configuration["WhatsApp:PhoneNumberId"];
            if (!string.IsNullOrEmpty(configWaPhone) && settings.WhatsAppPhoneNumberId != configWaPhone)
            {
                settings.WhatsAppPhoneNumberId = configWaPhone;
                updated = true;
            }

            var configWaApi = _configuration["WhatsApp:ApiVersion"];
            if (!string.IsNullOrEmpty(configWaApi) && settings.WhatsAppApiVersion != configWaApi)
            {
                settings.WhatsAppApiVersion = configWaApi;
                updated = true;
            }

            var configRzpKey = _configuration["Razorpay:KeyId"];
            if (!string.IsNullOrEmpty(configRzpKey) && settings.RazorpayKeyId != configRzpKey)
            {
                settings.RazorpayKeyId = configRzpKey;
                updated = true;
            }

            var configRzpSecret = _configuration["Razorpay:KeySecret"];
            if (!string.IsNullOrEmpty(configRzpSecret))
            {
                string? decryptedSecret = null;
                try { decryptedSecret = settings.RazorpayKeySecret != null ? EncryptionHelper.Decrypt(settings.RazorpayKeySecret, _encryptionKey) : null; } catch { decryptedSecret = settings.RazorpayKeySecret; }
                if (decryptedSecret != configRzpSecret)
                {
                    settings.RazorpayKeySecret = EncryptionHelper.Encrypt(configRzpSecret, _encryptionKey);
                    updated = true;
                }
            }

            if (updated)
            {
                _context.SystemSettings.Update(settings);
                await _context.SaveChangesAsync();
                Console.WriteLine("[DB INITIALIZATION] SystemSettings auto-synced with appsettings.json configurations.");
            }
        }

        return settings;
    }

    public async Task SaveSettingsAsync(SystemSettings newSettings)
    {
        var existing = await _context.SystemSettings.FirstOrDefaultAsync();
        if (existing == null)
        {
            existing = new SystemSettings();
            _context.SystemSettings.Add(existing);
        }

        existing.WhatsAppPhoneNumberId = newSettings.WhatsAppPhoneNumberId;
        existing.WhatsAppApiVersion = newSettings.WhatsAppApiVersion ?? "v25.0";
        existing.RazorpayKeyId = newSettings.RazorpayKeyId;
        existing.RazorpayWebhookSecret = newSettings.RazorpayWebhookSecret;
        existing.SmtpHost = newSettings.SmtpHost;
        existing.SmtpPort = newSettings.SmtpPort;
        existing.SmtpUsername = newSettings.SmtpUsername;
        existing.SmtpEnableSsl = newSettings.SmtpEnableSsl;
        existing.SmtpFromEmail = newSettings.SmtpFromEmail;

        if (newSettings.WhatsAppAccessToken != "********" && newSettings.WhatsAppAccessToken != null)
            existing.WhatsAppAccessToken = EncryptionHelper.Encrypt(newSettings.WhatsAppAccessToken, _encryptionKey);
        
        if (newSettings.RazorpayKeySecret != "********" && newSettings.RazorpayKeySecret != null)
            existing.RazorpayKeySecret = EncryptionHelper.Encrypt(newSettings.RazorpayKeySecret, _encryptionKey);

        if (newSettings.SmtpPassword != "********" && newSettings.SmtpPassword != null)
            existing.SmtpPassword = EncryptionHelper.Encrypt(newSettings.SmtpPassword, _encryptionKey);

        await _context.SaveChangesAsync();
    }

    public async Task<string?> GetWhatsAppAccessTokenAsync()
    {
        var settings = await GetSettingsAsync();
        var token = settings.WhatsAppAccessToken;
        if (string.IsNullOrEmpty(token)) return _configuration["WhatsApp:AccessToken"];
        return EncryptionHelper.Decrypt(token, _encryptionKey);
    }

    public async Task<string?> GetWhatsAppPhoneNumberIdAsync()
    {
        var settings = await GetSettingsAsync();
        return !string.IsNullOrEmpty(settings.WhatsAppPhoneNumberId) 
            ? settings.WhatsAppPhoneNumberId 
            : _configuration["WhatsApp:PhoneNumberId"];
    }

    public async Task<string?> GetWhatsAppApiVersionAsync()
    {
        var settings = await GetSettingsAsync();
        return !string.IsNullOrEmpty(settings.WhatsAppApiVersion) 
            ? settings.WhatsAppApiVersion 
            : (_configuration["WhatsApp:ApiVersion"] ?? "v25.0");
    }

    public async Task<string?> GetRazorpayKeyIdAsync()
    {
        var settings = await GetSettingsAsync();
        return !string.IsNullOrEmpty(settings.RazorpayKeyId) 
            ? settings.RazorpayKeyId 
            : _configuration["Razorpay:KeyId"];
    }

    public async Task<string?> GetRazorpayKeySecretAsync()
    {
        var settings = await GetSettingsAsync();
        var secret = settings.RazorpayKeySecret;
        if (string.IsNullOrEmpty(secret)) return _configuration["Razorpay:KeySecret"];
        return EncryptionHelper.Decrypt(secret, _encryptionKey);
    }

    public async Task<string?> GetRazorpayWebhookSecretAsync()
    {
        var settings = await GetSettingsAsync();
        return !string.IsNullOrEmpty(settings.RazorpayWebhookSecret) 
            ? settings.RazorpayWebhookSecret 
            : _configuration["Razorpay:WebhookSecret"];
    }

    public async Task<SmtpSettings> GetSmtpSettingsAsync()
    {
        var settings = await GetSettingsAsync();
        var decryptedPassword = string.IsNullOrEmpty(settings.SmtpPassword) 
            ? _configuration["SmtpSettings:Password"] 
            : EncryptionHelper.Decrypt(settings.SmtpPassword, _encryptionKey);

        return new SmtpSettings
        {
            Host = !string.IsNullOrEmpty(settings.SmtpHost) ? settings.SmtpHost : _configuration["SmtpSettings:Host"],
            Port = settings.SmtpPort > 0 ? settings.SmtpPort : (int.TryParse(_configuration["SmtpSettings:Port"], out var p) ? p : 587),
            Username = !string.IsNullOrEmpty(settings.SmtpUsername) ? settings.SmtpUsername : _configuration["SmtpSettings:Username"],
            Password = decryptedPassword,
            EnableSsl = settings.SmtpPort > 0 ? settings.SmtpEnableSsl : (bool.TryParse(_configuration["SmtpSettings:EnableSsl"], out var s) && s),
            FromEmail = !string.IsNullOrEmpty(settings.SmtpFromEmail) ? settings.SmtpFromEmail : _configuration["SmtpSettings:FromEmail"]
        };
    }
}

internal static class EncryptionHelper
{
    public static string Encrypt(string plainText, string secretKey)
    {
        if (string.IsNullOrEmpty(plainText)) return plainText;
        byte[] keyBytes = new byte[32];
        byte[] sourceKeyBytes = Encoding.UTF8.GetBytes(secretKey);
        Array.Copy(sourceKeyBytes, keyBytes, Math.Min(sourceKeyBytes.Length, keyBytes.Length));

        using Aes aes = Aes.Create();
        aes.Key = keyBytes;
        aes.GenerateIV();
        byte[] iv = aes.IV;

        using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using var ms = new System.IO.MemoryStream();
        ms.Write(iv, 0, iv.Length);
        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            cs.Write(plainBytes, 0, plainBytes.Length);
            cs.FlushFinalBlock();
        }
        return Convert.ToBase64String(ms.ToArray());
    }

    public static string Decrypt(string cipherText, string secretKey)
    {
        if (string.IsNullOrEmpty(cipherText)) return cipherText;
        try
        {
            byte[] fullCipher = Convert.FromBase64String(cipherText);
            byte[] keyBytes = new byte[32];
            byte[] sourceKeyBytes = Encoding.UTF8.GetBytes(secretKey);
            Array.Copy(sourceKeyBytes, keyBytes, Math.Min(sourceKeyBytes.Length, keyBytes.Length));

            using Aes aes = Aes.Create();
            aes.Key = keyBytes;
            
            byte[] iv = new byte[aes.BlockSize / 8];
            if (fullCipher.Length < iv.Length) return cipherText;
            Array.Copy(fullCipher, 0, iv, 0, iv.Length);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
            using var ms = new System.IO.MemoryStream();
            using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Write))
            {
                cs.Write(fullCipher, iv.Length, fullCipher.Length - iv.Length);
                cs.FlushFinalBlock();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return cipherText;
        }
    }
}
