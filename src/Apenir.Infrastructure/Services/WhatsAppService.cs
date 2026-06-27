using System.Security.Cryptography;
using System.Text;
using Apenir.Core.Interfaces;

namespace Apenir.Infrastructure.Services;

public class WhatsAppService : IWhatsAppService
{
    public async Task SendTextMessageAsync(string toPhone, string message)
    {
        await Task.Delay(10);
        Console.WriteLine($"[META WHATSAPP API OUTBOUND to {toPhone}]: {message}");
    }

    public static string HashOtp(string otp)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(otp));
        return Convert.ToHexString(bytes).ToLower();
    }
}