using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Apenir.API.Middleware;

public static class JwtHelper
{
    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .Replace("=", "")
            .Replace("+", "-")
            .Replace("/", "_");
    }

    private static byte[] Base64UrlDecode(string input)
    {
        string output = input.Replace('-', '+').Replace('_', '/');
        switch (output.Length % 4)
        {
            case 2: output += "=="; break;
            case 3: output += "="; break;
        }
        return Convert.FromBase64String(output);
    }

    public static string GenerateToken(string userId, string role, string phone, string secret, string issuer, string audience, int expiryMinutes)
    {
        var header = new { alg = "HS256", typ = "JWT" };
        
        long iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long exp = DateTimeOffset.UtcNow.AddMinutes(expiryMinutes).ToUnixTimeSeconds();
        
        var payload = new Dictionary<string, object>
        {
            { "sub", userId },
            { "role", role },
            { "phone", phone },
            { "iat", iat },
            { "exp", exp },
            { "iss", issuer },
            { "aud", audience }
        };

        string headerJson = JsonSerializer.Serialize(header);
        string payloadJson = JsonSerializer.Serialize(payload);

        string headerBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        string payloadBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

        string unsignedToken = $"{headerBase64}.{payloadBase64}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        byte[] signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(unsignedToken));
        string signatureBase64 = Base64UrlEncode(signatureBytes);

        return $"{unsignedToken}.{signatureBase64}";
    }

    public static ClaimsResult ValidateToken(string token, string secret)
    {
        if (string.IsNullOrEmpty(token))
            return new ClaimsResult { IsValid = false, Error = "TOKEN_INVALID" };

        var parts = token.Split('.');
        if (parts.Length != 3)
            return new ClaimsResult { IsValid = false, Error = "TOKEN_INVALID" };

        string headerBase64 = parts[0];
        string payloadBase64 = parts[1];
        string signatureBase64 = parts[2];

        try
        {
            string unsignedToken = $"{headerBase64}.{payloadBase64}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            byte[] computedSignatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(unsignedToken));
            string computedSignatureBase64 = Base64UrlEncode(computedSignatureBytes);

            // Constant-time comparison
            if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signatureBase64),
                Encoding.UTF8.GetBytes(computedSignatureBase64)))
            {
                return new ClaimsResult { IsValid = false, Error = "TOKEN_INVALID" };
            }

            string payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(payloadBase64));
            var claims = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson);

            if (claims == null)
                return new ClaimsResult { IsValid = false, Error = "TOKEN_INVALID" };

            if (claims.TryGetValue("exp", out var expElement))
            {
                long exp = expElement.GetInt64();
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (now > exp)
                {
                    return new ClaimsResult { IsValid = false, Error = "TOKEN_EXPIRED" };
                }
            }
            else
            {
                return new ClaimsResult { IsValid = false, Error = "TOKEN_INVALID" };
            }

            string userId = claims.TryGetValue("sub", out var subEl) ? subEl.GetString() ?? "" : "";
            string role = claims.TryGetValue("role", out var roleEl) ? roleEl.GetString() ?? "" : "";
            string phone = claims.TryGetValue("phone", out var phoneEl) ? phoneEl.GetString() ?? "" : "";

            return new ClaimsResult
            {
                IsValid = true,
                UserId = userId,
                Role = role,
                Phone = phone
            };
        }
        catch
        {
            return new ClaimsResult { IsValid = false, Error = "TOKEN_INVALID" };
        }
    }
}

public class ClaimsResult
{
    public bool IsValid { get; set; }
    public string? Error { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
}
