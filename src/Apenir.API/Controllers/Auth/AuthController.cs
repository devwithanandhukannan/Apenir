using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Apenir.Core.Entities;
using Apenir.Core.Enums;
using Apenir.Core.Interfaces;
using Apenir.API.DTOs;
using Apenir.Infrastructure.Services;
using Apenir.API.Middleware;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Apenir.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IApplicationDbContext _context;
    private readonly IWhatsAppService _whatsappService;
    private readonly IConfiguration _configuration;

    public AuthController(IApplicationDbContext context, IWhatsAppService whatsappService, IConfiguration configuration)
    {
        _context = context;
        _whatsappService = whatsappService;
        _configuration = configuration;
    }

    [HttpPost("otp/send")]
    public async Task<IActionResult> SendOtp([FromBody] SendOtpRequest request)
    {
        var random = new Random();
        string otp = random.Next(100000, 999999).ToString();
        string hashedOtp = WhatsAppService.HashOtp(otp);
        var oldCodes = await _context.OtpCodes.Where(o => o.Phone == request.Phone).ToListAsync();
        _context.OtpCodes.RemoveRange(oldCodes);

        var otpEntry = new OtpCode
        {
            Phone = request.Phone,
            HashCode = hashedOtp,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };

        _context.OtpCodes.Add(otpEntry);
        await _context.SaveChangesAsync();

        await _whatsappService.SendTextMessageAsync(request.Phone, $"Your LabBook OTP is: {otp}. Valid for 5 minutes.");

        return Ok(new { message = "OTP_SENT" });
    }

    [HttpPost("otp/verify")]
    public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpRequest request)
    {
        string hashedInput = WhatsAppService.HashOtp(request.Otp);

        var otpRecord = await _context.OtpCodes
            .Where(o => o.Phone == request.Phone && o.ExpiresAt > DateTime.UtcNow)
            .FirstOrDefaultAsync();

        if (otpRecord == null || otpRecord.HashCode != hashedInput)
        {
            return BadRequest(new { error = "OTP_INVALID_OR_EXPIRED" });
        }

        _context.OtpCodes.Remove(otpRecord);

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Phone == request.Phone);
        if (user == null)
        {
            user = new User 
            { 
                Phone = request.Phone, 
                Role = UserRole.Customer 
            };
            _context.Users.Add(user);

            var customer = new Customer 
            { 
                UserId = user.Id, 
                Phone = request.Phone 
            };
            _context.Customers.Add(customer);

            await _context.SaveChangesAsync();
        }

        // Generate JWT Access Token using JwtHelper
        var secret = _configuration["Jwt:Secret"] ?? "super_secret_key_which_is_at_least_32_bytes_long_1234567890";
        var issuer = _configuration["Jwt:Issuer"] ?? "labbook.in";
        var audience = _configuration["Jwt:Audience"] ?? "labbook-app";

        string jwtToken = JwtHelper.GenerateToken(user.Id, user.Role.ToString(), user.Phone, secret, issuer, audience, 15);

        // Generate Refresh Token
        var secureBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(secureBytes);
        }
        string rawRefreshToken = Convert.ToBase64String(secureBytes);

        using var sha256 = SHA256.Create();
        byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawRefreshToken));
        string tokenHash = Convert.ToHexString(hashBytes).ToLower();

        var dbRefreshToken = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = tokenHash,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Request.Headers["User-Agent"].ToString()
        };

        _context.RefreshTokens.Add(dbRefreshToken);
        await _context.SaveChangesAsync();

        // Refresh Token as HttpOnly Cookie
        Response.Cookies.Append("refresh_token", rawRefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/api/auth/refresh",
            Expires = DateTime.UtcNow.AddDays(7)
        });

        return Ok(new AuthResponse(jwtToken, user.Role.ToString(), user.Phone));
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> RefreshToken()
    {
        if (!Request.Cookies.TryGetValue("refresh_token", out var rawRefreshToken) || string.IsNullOrEmpty(rawRefreshToken))
        {
            return BadRequest(new { error = "REFRESH_TOKEN_REQUIRED" });
        }

        using var sha256 = SHA256.Create();
        byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawRefreshToken));
        string tokenHash = Convert.ToHexString(hashBytes).ToLower();

        var dbToken = await _context.RefreshTokens
            .FirstOrDefaultAsync(r => r.TokenHash == tokenHash && !r.IsRevoked && r.ExpiresAt > DateTime.UtcNow);

        if (dbToken == null)
        {
            return Unauthorized(new { code = "TOKEN_INVALID", message = "Invalid or expired refresh token" });
        }

        // Rotate token: revoke old one immediately
        dbToken.IsRevoked = true;
        _context.RefreshTokens.Update(dbToken);

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == dbToken.UserId);
        if (user == null)
        {
            await _context.SaveChangesAsync();
            return Unauthorized(new { code = "TOKEN_INVALID", message = "User not found" });
        }

        // Generate new Access Token
        var secret = _configuration["Jwt:Secret"] ?? "super_secret_key_which_is_at_least_32_bytes_long_1234567890";
        var issuer = _configuration["Jwt:Issuer"] ?? "labbook.in";
        var audience = _configuration["Jwt:Audience"] ?? "labbook-app";

        string newAccessToken = JwtHelper.GenerateToken(user.Id, user.Role.ToString(), user.Phone, secret, issuer, audience, 15);

        // Generate new Refresh Token
        var secureBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(secureBytes);
        }
        string newRawRefreshToken = Convert.ToBase64String(secureBytes);
        byte[] newHashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(newRawRefreshToken));
        string newTokenHash = Convert.ToHexString(newHashBytes).ToLower();

        var newDbToken = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = newTokenHash,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Request.Headers["User-Agent"].ToString()
        };
        _context.RefreshTokens.Add(newDbToken);

        await _context.SaveChangesAsync();

        Response.Cookies.Append("refresh_token", newRawRefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/api/auth/refresh",
            Expires = DateTime.UtcNow.AddDays(7)
        });

        return Ok(new AuthResponse(newAccessToken, user.Role.ToString(), user.Phone));
    }
}