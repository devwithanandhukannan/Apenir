using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Apenir.Application.Common.Interfaces;
using Apenir.Application.Common.Models;
using Apenir.Application.DTOs;
using Apenir.Core.Entities;
using Apenir.Core.Exceptions;

namespace Apenir.Application.Features.AdminAuth.Commands
{
    // --- 1. Admin Login ---
    public record AdminLoginCommand(LoginRequest Request) : IRequest<ApiResponse<LoginResponse>>;

    public class AdminLoginCommandHandler : IRequestHandler<AdminLoginCommand, ApiResponse<LoginResponse>>
    {
        private readonly IAdminRepository _adminRepository;
        private readonly IRefreshTokenRepository _refreshTokenRepository;
        private readonly IPasswordHasher _passwordHasher;
        private readonly IJwtTokenService _jwtTokenService;
        private readonly ICurrentUserService _currentUserService;
        private readonly JwtSettings _jwtSettings;

        public AdminLoginCommandHandler(
            IAdminRepository adminRepository,
            IRefreshTokenRepository refreshTokenRepository,
            IPasswordHasher passwordHasher,
            IJwtTokenService jwtTokenService,
            ICurrentUserService currentUserService,
            Microsoft.Extensions.Options.IOptions<JwtSettings> jwtSettings)
        {
            _adminRepository = adminRepository;
            _refreshTokenRepository = refreshTokenRepository;
            _passwordHasher = passwordHasher;
            _jwtTokenService = jwtTokenService;
            _currentUserService = currentUserService;
            _jwtSettings = jwtSettings.Value;
        }

        public async Task<ApiResponse<LoginResponse>> Handle(AdminLoginCommand command, CancellationToken cancellationToken)
        {
            var req = command.Request;
            var admin = await _adminRepository.GetByEmailAsync(req.Email, cancellationToken);

            if (admin == null || admin.IsDeleted)
            {
                throw new InvalidCredentialsException();
            }

            if (string.IsNullOrWhiteSpace(admin.PasswordHash) || !_passwordHasher.Verify(req.Password, admin.PasswordHash))
            {
                throw new InvalidCredentialsException();
            }

            if (admin.IsActive != true)
            {
                throw new AccountDisabledException();
            }

            var accessToken = _jwtTokenService.GenerateAccessToken(admin);
            var refreshTokenString = _jwtTokenService.GenerateRefreshToken();

            var refreshToken = new RefreshToken
            {
                Id = Guid.NewGuid(),
                Token = refreshTokenString,
                UserId = admin.Id.ToString(),
                ExpiresAt = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpiryDays),
                CreatedAt = DateTime.UtcNow,
                CreatedByIp = _currentUserService.IpAddress ?? "unknown",
                DeviceName = _currentUserService.UserAgent,
                UserAgent = _currentUserService.UserAgent,
                IpAddress = _currentUserService.IpAddress
            };

            await _refreshTokenRepository.AddAsync(refreshToken, cancellationToken);

            admin.LastLoginAt = DateTime.UtcNow;
            await _adminRepository.UpdateAsync(admin, cancellationToken);

            var response = new LoginResponse
            {
                AccessToken = accessToken,
                RefreshToken = refreshTokenString,
                ExpiresIn = _jwtSettings.AccessTokenExpiryMinutes * 60,
                AdminId = admin.Id.ToString(),
                Email = admin.Email ?? string.Empty,
                Role = "admin"
            };

            return ApiResponse<LoginResponse>.SuccessResult(response, "Login successful");
        }
    }

    // --- 2. Refresh Token ---
    public record RefreshTokenCommand(RefreshTokenRequest Request) : IRequest<ApiResponse<RefreshTokenResponse>>;

    public class RefreshTokenCommandHandler : IRequestHandler<RefreshTokenCommand, ApiResponse<RefreshTokenResponse>>
    {
        private readonly IAdminRepository _adminRepository;
        private readonly IRefreshTokenRepository _refreshTokenRepository;
        private readonly IJwtTokenService _jwtTokenService;
        private readonly ICurrentUserService _currentUserService;
        private readonly JwtSettings _jwtSettings;

        public RefreshTokenCommandHandler(
            IAdminRepository adminRepository,
            IRefreshTokenRepository refreshTokenRepository,
            IJwtTokenService jwtTokenService,
            ICurrentUserService currentUserService,
            Microsoft.Extensions.Options.IOptions<JwtSettings> jwtSettings)
        {
            _adminRepository = adminRepository;
            _refreshTokenRepository = refreshTokenRepository;
            _jwtTokenService = jwtTokenService;
            _currentUserService = currentUserService;
            _jwtSettings = jwtSettings.Value;
        }

        public async Task<ApiResponse<RefreshTokenResponse>> Handle(RefreshTokenCommand command, CancellationToken cancellationToken)
        {
            var req = command.Request;
            var activeToken = await _refreshTokenRepository.GetByTokenAsync(req.RefreshToken, cancellationToken);

            if (activeToken == null)
            {
                throw new RefreshTokenExpiredException("Invalid or non-existent refresh token.");
            }

            if (activeToken.IsRevoked)
            {
                await _refreshTokenRepository.RevokeAllForUserAsync(activeToken.UserId, _currentUserService.IpAddress, cancellationToken);
                throw new RefreshTokenRevokedException("Warning: Replay attack detected. Token was already revoked. Revoking all tokens for security.");
            }

            if (activeToken.IsExpired)
            {
                throw new RefreshTokenExpiredException();
            }

            if (!Guid.TryParse(activeToken.UserId, out var adminId)) 
            {
                throw new UnauthorizedException();
            }

            var admin = await _adminRepository.GetByIdAsync(adminId, cancellationToken);
            if (admin == null || admin.IsDeleted || admin.IsActive != true)
            {
                throw new AccountDisabledException();
            }

            var newAccessToken = _jwtTokenService.GenerateAccessToken(admin);
            var newRefreshTokenString = _jwtTokenService.GenerateRefreshToken();

            // Revoke current token
            await _refreshTokenRepository.RevokeAsync(activeToken.Token, _currentUserService.IpAddress, newRefreshTokenString, cancellationToken);

            // Add new token
            var newRefreshToken = new RefreshToken
            {
                Id = Guid.NewGuid(),
                Token = newRefreshTokenString,
                UserId = admin.Id.ToString(),
                ExpiresAt = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpiryDays),
                CreatedAt = DateTime.UtcNow,
                CreatedByIp = _currentUserService.IpAddress ?? "unknown",
                DeviceName = _currentUserService.UserAgent,
                UserAgent = _currentUserService.UserAgent,
                IpAddress = _currentUserService.IpAddress
            };

            await _refreshTokenRepository.AddAsync(newRefreshToken, cancellationToken);

            var response = new RefreshTokenResponse
            {
                AccessToken = newAccessToken,
                RefreshToken = newRefreshTokenString
            };

            return ApiResponse<RefreshTokenResponse>.SuccessResult(response, "Token refreshed successfully");
        }
    }

    // --- 3. Logout ---
    public record LogoutCommand(string RefreshToken) : IRequest<ApiResponse>;

    public class LogoutCommandHandler : IRequestHandler<LogoutCommand, ApiResponse>
    {
        private readonly IRefreshTokenRepository _refreshTokenRepository;
        private readonly ICurrentUserService _currentUserService;

        public LogoutCommandHandler(IRefreshTokenRepository refreshTokenRepository, ICurrentUserService currentUserService)
        {
            _refreshTokenRepository = refreshTokenRepository;
            _currentUserService = currentUserService;
        }

        public async Task<ApiResponse> Handle(LogoutCommand command, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(command.RefreshToken))
            {
                return ApiResponse.FailureResult("Refresh token is required.");
            }

            var activeToken = await _refreshTokenRepository.GetByTokenAsync(command.RefreshToken, cancellationToken);
            if (activeToken != null && !activeToken.IsRevoked)
            {
                await _refreshTokenRepository.RevokeAsync(activeToken.Token, _currentUserService.IpAddress, null, cancellationToken);
            }

            return ApiResponse.SuccessResult("Logout successful");
        }
    }

    // --- 4. Change Password ---
    public record ChangePasswordCommand(ChangePasswordRequest Request) : IRequest<ApiResponse>;

    public class ChangePasswordCommandHandler : IRequestHandler<ChangePasswordCommand, ApiResponse>
    {
        private readonly IAdminRepository _adminRepository;
        private readonly IPasswordHasher _passwordHasher;
        private readonly ICurrentUserService _currentUserService;

        public ChangePasswordCommandHandler(IAdminRepository adminRepository, IPasswordHasher passwordHasher, ICurrentUserService currentUserService)
        {
            _adminRepository = adminRepository;
            _passwordHasher = passwordHasher;
            _currentUserService = currentUserService;
        }

        public async Task<ApiResponse> Handle(ChangePasswordCommand command, CancellationToken cancellationToken)
        {
            var adminId = _currentUserService.UserId;
            if (adminId == null)
            {
                throw new UnauthorizedException();
            }

            var admin = await _adminRepository.GetByIdAsync(adminId.Value, cancellationToken);
            if (admin == null || admin.IsDeleted || admin.IsActive != true)
            {
                throw new AccountDisabledException();
            }

            if (!_passwordHasher.Verify(command.Request.CurrentPassword, admin.PasswordHash))
            {
                throw new InvalidCredentialsException("Current password matches incorrectly.");
            }

            admin.PasswordHash = _passwordHasher.Hash(command.Request.NewPassword);
            admin.UpdatedAt = DateTime.UtcNow;

            await _adminRepository.UpdateAsync(admin, cancellationToken);

            return ApiResponse.SuccessResult("Password changed successfully");
        }
    }

    // --- 5. Revoke Refresh Token ---
    public record RevokeRefreshTokenCommand(RevokeRefreshTokenRequest Request) : IRequest<ApiResponse>;
    public record RevokeRefreshTokenRequest(string RefreshToken);

    public class RevokeRefreshTokenCommandHandler : IRequestHandler<RevokeRefreshTokenCommand, ApiResponse>
    {
        private readonly IRefreshTokenRepository _refreshTokenRepository;
        private readonly ICurrentUserService _currentUserService;

        public RevokeRefreshTokenCommandHandler(IRefreshTokenRepository refreshTokenRepository, ICurrentUserService currentUserService)
        {
            _refreshTokenRepository = refreshTokenRepository;
            _currentUserService = currentUserService;
        }

        public async Task<ApiResponse> Handle(RevokeRefreshTokenCommand command, CancellationToken cancellationToken)
        {
            var token = command.Request.RefreshToken;
            var activeToken = await _refreshTokenRepository.GetByTokenAsync(token, cancellationToken);

            if (activeToken == null || activeToken.IsRevoked)
            {
                return ApiResponse.FailureResult("Invalid or already revoked refresh token.");
            }

            await _refreshTokenRepository.RevokeAsync(token, _currentUserService.IpAddress, null, cancellationToken);
            return ApiResponse.SuccessResult("Token revoked successfully");
        }
    }

    // --- 6. Logout All Devices ---
    public record LogoutAllDevicesCommand : IRequest<ApiResponse>;

    public class LogoutAllDevicesCommandHandler : IRequestHandler<LogoutAllDevicesCommand, ApiResponse>
    {
        private readonly IRefreshTokenRepository _refreshTokenRepository;
        private readonly ICurrentUserService _currentUserService;

        public LogoutAllDevicesCommandHandler(IRefreshTokenRepository refreshTokenRepository, ICurrentUserService currentUserService)
        {
            _refreshTokenRepository = refreshTokenRepository;
            _currentUserService = currentUserService;
        }

        public async Task<ApiResponse> Handle(LogoutAllDevicesCommand command, CancellationToken cancellationToken)
        {
            var adminId = _currentUserService.UserId;
            if (adminId == null)
            {
                throw new UnauthorizedException();
            }

            await _refreshTokenRepository.RevokeAllForUserAsync(adminId.Value.ToString(), _currentUserService.IpAddress, cancellationToken);
            return ApiResponse.SuccessResult("Successfully logged out from all devices");
        }
    }

    // --- 7. Forgot Password ---
    public record ForgotPasswordCommand(ForgotPasswordRequest Request, string FrontendUrl) : IRequest<ApiResponse>;

    public class ForgotPasswordCommandHandler : IRequestHandler<ForgotPasswordCommand, ApiResponse>
    {
        private readonly IAdminRepository _adminRepository;
        private readonly IEmailService _emailService;

        public ForgotPasswordCommandHandler(IAdminRepository adminRepository, IEmailService emailService)
        {
            _adminRepository = adminRepository;
            _emailService = emailService;
        }

        public async Task<ApiResponse> Handle(ForgotPasswordCommand command, CancellationToken cancellationToken)
        {
            var admin = await _adminRepository.GetByEmailAsync(command.Request.Email, cancellationToken);
            if (admin == null || admin.IsDeleted)
            {
                // Return success anyway to avoid user enumeration timing/information disclosure
                return ApiResponse.SuccessResult("If the email matches an active account, a password reset link will be sent.");
            }

            var token = Guid.NewGuid().ToString("N");
            admin.ResetPasswordToken = token;
            admin.ResetPasswordTokenExpiry = DateTime.UtcNow.AddMinutes(15);
            await _adminRepository.UpdateAsync(admin, cancellationToken);

            var frontendUrl = command.FrontendUrl;
            var resetUrl = $"{frontendUrl.TrimEnd('/')}/reset-password?token={token}&email={Uri.EscapeDataString(admin.Email!)}";

            var emailSubject = "Reset Your Apenir Admin Password";
            var emailBody = $@"
<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>Reset Your Password</title>
</head>
<body style='margin: 0; padding: 0; background-color: #f9fafb; font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, Helvetica, Arial, sans-serif; -webkit-font-smoothing: antialiased;'>
    <table cellpadding='0' cellspacing='0' width='100%' style='background-color: #f9fafb; min-height: 100vh; padding: 60px 20px;'>
        <tr>
            <td align='center' valign='top'>
                <table cellpadding='0' cellspacing='0' width='100%' style='max-width: 500px; background-color: #ffffff; border: 1px solid #e5e7eb; border-radius: 16px; padding: 40px; box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.05); text-align: left;'>
                    <tr>
                        <td align='left' style='padding-bottom: 32px;'>
                            <span style='font-size: 20px; font-weight: 700; color: #111827; letter-spacing: -0.5px;'>Apenir Admin</span>
                        </td>
                    </tr>
                    <tr>
                        <td style='color: #111827; font-size: 16px; font-weight: 600; padding-bottom: 12px;'>
                            Hello {admin.Name},
                        </td>
                    </tr>
                    <tr>
                        <td style='color: #4b5563; font-size: 14px; line-height: 1.6; padding-bottom: 32px;'>
                            We received a request to reset the password for your administrator account. Please click the button below to choose a new password:
                        </td>
                    </tr>
                    <tr>
                        <td align='left' style='padding-bottom: 32px;'>
                            <a href='{resetUrl}' style='display: inline-block; background-color: #111827; color: #ffffff; text-decoration: none; padding: 12px 24px; font-size: 14px; font-weight: 500; border-radius: 8px;'>Reset Password</a>
                        </td>
                    </tr>
                    <tr>
                        <td style='color: #9ca3af; font-size: 12px; line-height: 1.5; padding-bottom: 24px; border-top: 1px solid #f3f4f6; padding-top: 24px;'>
                            If you're having trouble clicking the button, copy and paste the URL below into your browser:
                            <br/>
                            <a href='{resetUrl}' style='color: #2563eb; text-decoration: none; word-break: break-all;'>{resetUrl}</a>
                        </td>
                    </tr>
                    <tr>
                        <td style='color: #ef4444; font-size: 12px; font-weight: 500; padding-bottom: 16px;'>
                            ⏱️ This link is only valid for 15 minutes.
                        </td>
                    </tr>
                    <tr>
                        <td style='color: #9ca3af; font-size: 11px; line-height: 1.4;'>
                            If you did not request this email, you can safely ignore it.
                        </td>
                    </tr>
                </table>
            </td>
        </tr>
    </table>
</body>
</html>";

            await _emailService.SendEmailAsync(admin.Email!, emailSubject, emailBody);
            
            return ApiResponse.SuccessResult("If the email matches an active account, a password reset link will be sent.");
        }
    }

    // --- 8. Reset Password ---
    public record ResetPasswordCommand(ResetPasswordRequest Request) : IRequest<ApiResponse>;

    public class ResetPasswordCommandHandler : IRequestHandler<ResetPasswordCommand, ApiResponse>
    {
        private readonly IAdminRepository _adminRepository;
        private readonly IPasswordHasher _passwordHasher;

        public ResetPasswordCommandHandler(IAdminRepository adminRepository, IPasswordHasher passwordHasher)
        {
            _adminRepository = adminRepository;
            _passwordHasher = passwordHasher;
        }

        public async Task<ApiResponse> Handle(ResetPasswordCommand command, CancellationToken cancellationToken)
        {
            var admin = await _adminRepository.GetByEmailAsync(command.Request.Email, cancellationToken);
            if (admin == null || admin.IsDeleted)
            {
                return ApiResponse.FailureResult("Invalid request parameters.");
            }

            if (string.IsNullOrWhiteSpace(command.Request.Token) || 
                admin.ResetPasswordToken != command.Request.Token || 
                admin.ResetPasswordTokenExpiry < DateTime.UtcNow)
            {
                return ApiResponse.FailureResult("Invalid or expired reset token.");
            }

            admin.PasswordHash = _passwordHasher.Hash(command.Request.NewPassword);
            admin.ResetPasswordToken = null;
            admin.ResetPasswordTokenExpiry = null;
            admin.UpdatedAt = DateTime.UtcNow;
            await _adminRepository.UpdateAsync(admin, cancellationToken);

            return ApiResponse.SuccessResult("Password reset completed successfully.");
        }
    }
}
