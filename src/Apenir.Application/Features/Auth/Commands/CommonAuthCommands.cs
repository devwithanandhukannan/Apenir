using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Apenir.Application.Common.Interfaces;
using Apenir.Application.Common.Models;
using Apenir.Application.DTOs;
using Apenir.Core.Entities;
using Apenir.Core.Exceptions;
using Apenir.Core.Interfaces;

namespace Apenir.Application.Features.Auth.Commands
{
    // --- Common Refresh Token ---
    public record CommonRefreshTokenCommand(RefreshTokenRequest Request, string? IpAddress, string? UserAgent) : IRequest<ApiResponse<RefreshTokenResponse>>;

    public class CommonRefreshTokenCommandHandler : IRequestHandler<CommonRefreshTokenCommand, ApiResponse<RefreshTokenResponse>>
    {
        private readonly IAdminRepository _adminRepository;
        private readonly IApplicationDbContext _context;
        private readonly IRefreshTokenRepository _refreshTokenRepository;
        private readonly IJwtTokenService _jwtTokenService;
        private readonly Microsoft.Extensions.Options.IOptions<JwtSettings> _jwtSettings;

        public CommonRefreshTokenCommandHandler(
            IAdminRepository adminRepository,
            IApplicationDbContext context,
            IRefreshTokenRepository refreshTokenRepository,
            IJwtTokenService jwtTokenService,
            Microsoft.Extensions.Options.IOptions<JwtSettings> jwtSettings)
        {
            _adminRepository = adminRepository;
            _context = context;
            _refreshTokenRepository = refreshTokenRepository;
            _jwtTokenService = jwtTokenService;
            _jwtSettings = jwtSettings;
        }

        public async Task<ApiResponse<RefreshTokenResponse>> Handle(CommonRefreshTokenCommand command, CancellationToken cancellationToken)
        {
            var req = command.Request;
            var activeToken = await _refreshTokenRepository.GetByTokenAsync(req.RefreshToken, cancellationToken);

            if (activeToken == null)
            {
                throw new RefreshTokenExpiredException("Invalid or non-existent refresh token.");
            }

            if (activeToken.IsRevoked)
            {
                await _refreshTokenRepository.RevokeAllForUserAsync(activeToken.UserId, command.IpAddress, cancellationToken);
                throw new RefreshTokenRevokedException("Warning: Replay attack detected. Token was already revoked. Revoking all tokens for security.");
            }

            if (activeToken.IsExpired)
            {
                throw new RefreshTokenExpiredException();
            }

            string newAccessToken;
            var newRefreshTokenString = _jwtTokenService.GenerateRefreshToken();

            // Check if UserId is a valid Guid (which indicates it might be an Admin, or just check the repository)
            if (Guid.TryParse(activeToken.UserId, out var adminGuid))
            {
                var admin = await _adminRepository.GetByIdAsync(adminGuid, cancellationToken);
                if (admin != null)
                {
                    if (admin.IsDeleted || !admin.IsActive) throw new AccountDisabledException();
                    newAccessToken = _jwtTokenService.GenerateAccessToken(admin);
                }
                else
                {
                    var user = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(_context.Users, u => u.Id == activeToken.UserId, cancellationToken);
                    if (user == null) throw new UnauthorizedException("User not found.");
                    newAccessToken = _jwtTokenService.GenerateAccessToken(user);
                }
            }
            else
            {
                var user = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(_context.Users, u => u.Id == activeToken.UserId, cancellationToken);
                if (user == null) throw new UnauthorizedException("User not found.");
                newAccessToken = _jwtTokenService.GenerateAccessToken(user);
            }

            // Revoke current token
            await _refreshTokenRepository.RevokeAsync(activeToken.Token, command.IpAddress, newRefreshTokenString, cancellationToken);

            // Add new token
            var newRefreshToken = new RefreshToken
            {
                Id = Guid.NewGuid(),
                Token = newRefreshTokenString,
                UserId = activeToken.UserId,
                ExpiresAt = DateTime.UtcNow.AddDays(_jwtSettings.Value.RefreshTokenExpiryDays),
                CreatedAt = DateTime.UtcNow,
                CreatedByIp = command.IpAddress ?? "unknown",
                DeviceName = command.UserAgent,
                UserAgent = command.UserAgent,
                IpAddress = command.IpAddress
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

    // --- Common Logout ---
    public record CommonLogoutCommand(string RefreshToken, string? IpAddress) : IRequest<ApiResponse>;

    public class CommonLogoutCommandHandler : IRequestHandler<CommonLogoutCommand, ApiResponse>
    {
        private readonly IRefreshTokenRepository _refreshTokenRepository;

        public CommonLogoutCommandHandler(IRefreshTokenRepository refreshTokenRepository)
        {
            _refreshTokenRepository = refreshTokenRepository;
        }

        public async Task<ApiResponse> Handle(CommonLogoutCommand command, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(command.RefreshToken))
            {
                return ApiResponse.FailureResult("Refresh token is required.");
            }

            var activeToken = await _refreshTokenRepository.GetByTokenAsync(command.RefreshToken, cancellationToken);
            if (activeToken != null && !activeToken.IsRevoked)
            {
                await _refreshTokenRepository.RevokeAsync(activeToken.Token, command.IpAddress, null, cancellationToken);
            }

            return ApiResponse.SuccessResult("Logout successful");
        }
    }
}
