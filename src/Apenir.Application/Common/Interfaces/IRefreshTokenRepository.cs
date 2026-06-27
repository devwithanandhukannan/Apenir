using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Apenir.Core.Entities;

namespace Apenir.Application.Common.Interfaces
{
    public interface IRefreshTokenRepository
    {
        Task AddAsync(RefreshToken token, CancellationToken cancellationToken = default);
        Task<RefreshToken?> GetByTokenAsync(string token, CancellationToken cancellationToken = default);
        Task<RefreshToken?> GetActiveTokenAsync(string token, CancellationToken cancellationToken = default);
        Task RevokeAsync(string token, string? revokedByIp, string? replacedByToken, CancellationToken cancellationToken = default);
        Task RevokeAllForAdminAsync(Guid adminId, string? revokedByIp, CancellationToken cancellationToken = default);
        Task DeleteExpiredAsync(CancellationToken cancellationToken = default);
        Task<List<RefreshToken>> GetByAdminIdAsync(Guid adminId, CancellationToken cancellationToken = default);
    }
}
