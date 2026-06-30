using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Apenir.Application.Common.Interfaces;
using Apenir.Core.Entities;
using Apenir.Infrastructure.Data;

namespace Apenir.Infrastructure.Repositories
{
    public class RefreshTokenRepository : IRefreshTokenRepository
    {
        private readonly AppDbContext _context;

        public RefreshTokenRepository(AppDbContext context)
        {
            _context = context;
        }

        public async Task AddAsync(RefreshToken token, CancellationToken cancellationToken = default)
        {
            _context.RefreshTokens.Add(token);
            await _context.SaveChangesAsync(cancellationToken);
        }

        public async Task<RefreshToken?> GetByTokenAsync(string token, CancellationToken cancellationToken = default)
        {
            return await _context.RefreshTokens
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Token == token, cancellationToken);
        }

        public async Task<RefreshToken?> GetActiveTokenAsync(string token, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            return await _context.RefreshTokens
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Token == token && t.RevokedAt == null && t.ExpiresAt > now, cancellationToken);
        }

        public async Task RevokeAsync(string token, string? revokedByIp, string? replacedByToken, CancellationToken cancellationToken = default)
        {
            var dbToken = await _context.RefreshTokens.FirstOrDefaultAsync(t => t.Token == token, cancellationToken);
            if (dbToken == null)
            {
                return;
            }

            dbToken.RevokedAt = DateTime.UtcNow;
            dbToken.RevokedByIp = revokedByIp;
            dbToken.ReplacedByToken = replacedByToken;
            _context.RefreshTokens.Update(dbToken);
            await _context.SaveChangesAsync(cancellationToken);
        }

        public async Task RevokeAllForAdminAsync(Guid adminId, string? revokedByIp, CancellationToken cancellationToken = default)
        {
            var tokens = await _context.RefreshTokens
                .Where(t => t.AdminId == adminId && t.RevokedAt == null)
                .ToListAsync(cancellationToken);

            foreach (var token in tokens)
            {
                token.RevokedAt = DateTime.UtcNow;
                token.RevokedByIp = revokedByIp;
            }

            if (tokens.Count > 0)
            {
                _context.RefreshTokens.UpdateRange(tokens);
                await _context.SaveChangesAsync(cancellationToken);
            }
        }

        public async Task DeleteExpiredAsync(CancellationToken cancellationToken = default)
        {
            var expiredTokens = await _context.RefreshTokens
                .Where(t => t.ExpiresAt < DateTime.UtcNow)
                .ToListAsync(cancellationToken);

            if (expiredTokens.Count > 0)
            {
                _context.RefreshTokens.RemoveRange(expiredTokens);
                await _context.SaveChangesAsync(cancellationToken);
            }
        }

        public async Task<List<RefreshToken>> GetByAdminIdAsync(Guid adminId, CancellationToken cancellationToken = default)
        {
            return await _context.RefreshTokens
                .AsNoTracking()
                .Where(t => t.AdminId == adminId)
                .ToListAsync(cancellationToken);
        }
    }
}
