using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Apenir.API.DTOs;
using Apenir.Application.Common.Interfaces;
using Apenir.Application.Common.Models;
using Apenir.Core.Entities;
using Apenir.Core.Enums;
using Apenir.Core.Exceptions;
using Apenir.Core.Interfaces;

namespace Apenir.Application.Features.Auth.Commands
{
    // --- 1. Send OTP ---
    public record SendOtpCommand(SendOtpRequest Request) : IRequest<ApiResponse>;

    public class SendOtpCommandHandler : IRequestHandler<SendOtpCommand, ApiResponse>
    {
        private readonly IApplicationDbContext _context;
        private readonly IWhatsAppService _whatsappService;

        public SendOtpCommandHandler(IApplicationDbContext context, IWhatsAppService whatsappService)
        {
            _context = context;
            _whatsappService = whatsappService;
        }

        public async Task<ApiResponse> Handle(SendOtpCommand command, CancellationToken cancellationToken)
        {
            var random = new Random();
            string otp = random.Next(100000, 999999).ToString();
            
            // Reusing HashOtp logic (assumed to be a static utility or via interface in a clean arch, but for now we'll hash it similarly)
            // Wait, HashOtp was on WhatsAppService. Let's assume we can compute SHA256 here or inject it.
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(otp));
            string hashedOtp = Convert.ToHexString(hashBytes).ToLower();

            var oldCodes = await _context.OtpCodes.Where(o => o.Phone == command.Request.Phone).ToListAsync(cancellationToken);
            _context.OtpCodes.RemoveRange(oldCodes);

            var otpEntry = new OtpCode
            {
                Phone = command.Request.Phone,
                HashCode = hashedOtp,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            };

            _context.OtpCodes.Add(otpEntry);
            await _context.SaveChangesAsync(cancellationToken);

            await _whatsappService.SendTextMessageAsync(command.Request.Phone, $"Your LabBook OTP is: {otp}. Valid for 5 minutes.");

            return ApiResponse.SuccessResult("OTP_SENT");
        }
    }

    // --- 2. Verify OTP ---
    public record VerifyOtpCommand(VerifyOtpRequest Request, string? UserAgent, string? IpAddress) : IRequest<ApiResponse<AuthResponse>>;

    public class VerifyOtpCommandHandler : IRequestHandler<VerifyOtpCommand, ApiResponse<AuthResponse>>
    {
        private readonly IApplicationDbContext _context;
        private readonly IJwtTokenService _jwtTokenService;
        private readonly IRefreshTokenRepository _refreshTokenRepository;
        private readonly Microsoft.Extensions.Options.IOptions<JwtSettings> _jwtSettings;

        public VerifyOtpCommandHandler(
            IApplicationDbContext context, 
            IJwtTokenService jwtTokenService, 
            IRefreshTokenRepository refreshTokenRepository,
            Microsoft.Extensions.Options.IOptions<JwtSettings> jwtSettings)
        {
            _context = context;
            _jwtTokenService = jwtTokenService;
            _refreshTokenRepository = refreshTokenRepository;
            _jwtSettings = jwtSettings;
        }

        public async Task<ApiResponse<AuthResponse>> Handle(VerifyOtpCommand command, CancellationToken cancellationToken)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(command.Request.Otp));
            string hashedInput = Convert.ToHexString(hashBytes).ToLower();

            var otpRecord = await _context.OtpCodes
                .Where(o => o.Phone == command.Request.Phone && o.ExpiresAt > DateTime.UtcNow)
                .FirstOrDefaultAsync(cancellationToken);

            if (otpRecord == null || otpRecord.HashCode != hashedInput)
            {
                return ApiResponse<AuthResponse>.FailureResult("OTP_INVALID_OR_EXPIRED");
            }

            _context.OtpCodes.Remove(otpRecord);

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Phone == command.Request.Phone, cancellationToken);
            if (user == null)
            {
                user = new User 
                { 
                    Phone = command.Request.Phone, 
                    Role = UserRole.Customer 
                };
                _context.Users.Add(user);

                var customer = new Customer 
                { 
                    UserId = user.Id, 
                    Phone = command.Request.Phone 
                };
                _context.Customers.Add(customer);

                await _context.SaveChangesAsync(cancellationToken);
            }

            var accessToken = _jwtTokenService.GenerateAccessToken(user);
            var refreshTokenString = _jwtTokenService.GenerateRefreshToken();

            var refreshToken = new RefreshToken
            {
                Id = Guid.NewGuid(),
                Token = refreshTokenString,
                UserId = user.Id,
                ExpiresAt = DateTime.UtcNow.AddDays(_jwtSettings.Value.RefreshTokenExpiryDays),
                CreatedAt = DateTime.UtcNow,
                CreatedByIp = command.IpAddress ?? "unknown",
                DeviceName = command.UserAgent,
                UserAgent = command.UserAgent,
                IpAddress = command.IpAddress
            };

            await _refreshTokenRepository.AddAsync(refreshToken, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            return ApiResponse<AuthResponse>.SuccessResult(new AuthResponse(accessToken, user.Role.ToString(), user.Phone, refreshTokenString));
        }
    }
}
