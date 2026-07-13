using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Apenir.Application.Common.Interfaces;
using Apenir.Application.Common.Models;
using Apenir.Application.DTOs;
using Apenir.Core.Exceptions;

namespace Apenir.Application.Features.AdminAuth.Queries
{
    // --- 1. Get Current Admin ---
    public record GetCurrentAdminQuery : IRequest<ApiResponse<CurrentAdminResponse>>;

    public class GetCurrentAdminQueryHandler : IRequestHandler<GetCurrentAdminQuery, ApiResponse<CurrentAdminResponse>>
    {
        private readonly IAdminRepository _adminRepository;
        private readonly ICurrentUserService _currentUserService;

        public GetCurrentAdminQueryHandler(IAdminRepository adminRepository, ICurrentUserService currentUserService)
        {
            _adminRepository = adminRepository;
            _currentUserService = currentUserService;
        }

        public async Task<ApiResponse<CurrentAdminResponse>> Handle(GetCurrentAdminQuery query, CancellationToken cancellationToken)
        {
            var adminId = _currentUserService.UserId;
            if (adminId == null)
            {
                throw new UnauthorizedException();
            }

            var admin = await _adminRepository.GetByIdAsync(adminId.Value, cancellationToken);
            if (admin == null || admin.IsDeleted)
            {
                throw new UnauthorizedException("User not found.");
            }

            if (admin.IsActive != true)
            {
                throw new AccountDisabledException();
            }

            var response = new CurrentAdminResponse
            {
                Id = admin.Id,
                Email = admin.Email ?? string.Empty,
                FullName = admin.Name ?? string.Empty,
                Roles = new List<string> { admin.Role.ToString() },
                Permissions = admin.Permissions,
                LastLoginAt = admin.LastLoginAt,
                CreatedAt = admin.CreatedAt ?? DateTime.UtcNow
            };

            return ApiResponse<CurrentAdminResponse>.SuccessResult(response, "Current admin retrieved successfully");
        }
    }

    // --- 2. Validate Token ---
    public record ValidateTokenQuery(string AccessToken) : IRequest<ApiResponse<TokenValidationResponse>>;

    public class ValidateTokenQueryHandler : IRequestHandler<ValidateTokenQuery, ApiResponse<TokenValidationResponse>>
    {
        private readonly IJwtTokenService _jwtTokenService;

        public ValidateTokenQueryHandler(IJwtTokenService jwtTokenService)
        {
            _jwtTokenService = jwtTokenService;
        }

        public Task<ApiResponse<TokenValidationResponse>> Handle(ValidateTokenQuery query, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(query.AccessToken))
            {
                return Task.FromResult(ApiResponse<TokenValidationResponse>.SuccessResult(new TokenValidationResponse { IsValid = false }, "Token validation failed"));
            }

            try
            {
                var principal = _jwtTokenService.GetPrincipalFromExpiredToken(query.AccessToken);
                if (principal == null)
                {
                    return Task.FromResult(ApiResponse<TokenValidationResponse>.SuccessResult(new TokenValidationResponse { IsValid = false }, "Token validation failed"));
                }

                // Verify expiration
                var expClaim = principal.Claims.FirstOrDefault(c => c.Type == "exp")?.Value;
                if (expClaim != null)
                {
                    var expTime = DateTimeOffset.FromUnixTimeSeconds(long.Parse(expClaim)).UtcDateTime;
                    if (expTime < DateTime.UtcNow)
                    {
                        return Task.FromResult(ApiResponse<TokenValidationResponse>.SuccessResult(new TokenValidationResponse { IsValid = false }, "Token has expired"));
                    }
                }

                var adminIdStr = principal.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
                var email = principal.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;
                var roles = principal.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList();
                var permissions = principal.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToList();

                var response = new TokenValidationResponse
                {
                    IsValid = true,
                    AdminId = adminIdStr,
                    Email = email,
                    Roles = roles,
                    Permissions = permissions
                };

                return Task.FromResult(ApiResponse<TokenValidationResponse>.SuccessResult(response, "Token is valid"));
            }
            catch
            {
                return Task.FromResult(ApiResponse<TokenValidationResponse>.SuccessResult(new TokenValidationResponse { IsValid = false }, "Token validation failed"));
            }
        }
    }
}
