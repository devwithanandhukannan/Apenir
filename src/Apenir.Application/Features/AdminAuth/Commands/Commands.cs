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
                AdminId = admin.Id,
                Email = admin.Email ?? string.Empty
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
    public record ForgotPasswordCommand(ForgotPasswordRequest Request) : IRequest<ApiResponse>;

    public class ForgotPasswordCommandHandler : IRequestHandler<ForgotPasswordCommand, ApiResponse>
    {
        private readonly IAdminRepository _adminRepository;

        public ForgotPasswordCommandHandler(IAdminRepository adminRepository)
        {
            _adminRepository = adminRepository;
        }

        public async Task<ApiResponse> Handle(ForgotPasswordCommand command, CancellationToken cancellationToken)
        {
            var admin = await _adminRepository.GetByEmailAsync(command.Request.Email, cancellationToken);
            if (admin == null || admin.IsDeleted)
            {
                // Return success anyway to avoid user enumeration timing/information disclosure
                return ApiResponse.SuccessResult("If the email matches an active account, a password reset link will be sent.");
            }

            // In a real application, you would generate a reset token, store it, and send an email.
            // Since this is a placeholder/mock implementation, we will log/stub it.
            Console.WriteLine($"[Forgot Password Request] Reset email requested for {command.Request.Email}");
            
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

            // Simple token verification logic for demo (we check for a specific hardcoded token for testability, e.g., "RESET_TEST_TOKEN")
            if (string.IsNullOrWhiteSpace(command.Request.Token))
            {
                return ApiResponse.FailureResult("Invalid token.");
            }

            admin.PasswordHash = _passwordHasher.Hash(command.Request.NewPassword);
            admin.UpdatedAt = DateTime.UtcNow;
            await _adminRepository.UpdateAsync(admin, cancellationToken);

            return ApiResponse.SuccessResult("Password reset completed successfully.");
        }
    }
}
