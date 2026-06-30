using System.Security.Claims;
using Apenir.Core.Entities;

namespace Apenir.Application.Common.Interfaces
{
    public interface IJwtTokenService
    {
        string GenerateAccessToken(Admin admin);
        string GenerateAccessToken(User user);
        string GenerateRefreshToken();
        ClaimsPrincipal? GetPrincipalFromExpiredToken(string token);
        bool ValidateAccessToken(string token);
    }
}
