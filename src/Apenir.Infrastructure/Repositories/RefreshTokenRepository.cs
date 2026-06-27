using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;
using Apenir.Application.Common.Interfaces;
using Apenir.Core.Entities;
using Apenir.Infrastructure.Persistence;

namespace Apenir.Infrastructure.Repositories
{
    public class RefreshTokenRepository : IRefreshTokenRepository
    {
        private readonly MongoDbContext _context;

        public RefreshTokenRepository(MongoDbContext context)
        {
            _context = context;
        }

        public async Task AddAsync(RefreshToken token, CancellationToken cancellationToken = default)
        {
            await _context.RefreshTokens.InsertOneAsync(token, null, cancellationToken);
        }

        public async Task<RefreshToken?> GetByTokenAsync(string token, CancellationToken cancellationToken = default)
        {
            return await _context.RefreshTokens
                .Find(t => t.Token == token)
                .FirstOrDefaultAsync(cancellationToken);
        }

        public async Task<RefreshToken?> GetActiveTokenAsync(string token, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            return await _context.RefreshTokens
                .Find(t => t.Token == token && t.RevokedAt == null && t.ExpiresAt > now)
                .FirstOrDefaultAsync(cancellationToken);
        }

        public async Task RevokeAsync(string token, string? revokedByIp, string? replacedByToken, CancellationToken cancellationToken = default)
        {
            var filter = Builders<RefreshToken>.Filter.Eq(t => t.Token, token);
            var update = Builders<RefreshToken>.Update
                .Set(t => t.RevokedAt, DateTime.UtcNow)
                .Set(t => t.RevokedByIp, revokedByIp)
                .Set(t => t.ReplacedByToken, replacedByToken);

            await _context.RefreshTokens.UpdateOneAsync(filter, update, null, cancellationToken);
        }

        public async Task RevokeAllForAdminAsync(Guid adminId, string? revokedByIp, CancellationToken cancellationToken = default)
        {
            var filter = Builders<RefreshToken>.Filter.And(
                Builders<RefreshToken>.Filter.Eq(t => t.AdminId, adminId),
                Builders<RefreshToken>.Filter.Eq(t => t.RevokedAt, null)
            );
            var update = Builders<RefreshToken>.Update
                .Set(t => t.RevokedAt, DateTime.UtcNow)
                .Set(t => t.RevokedByIp, revokedByIp);

            await _context.RefreshTokens.UpdateManyAsync(filter, update, null, cancellationToken);
        }

        public async Task DeleteExpiredAsync(CancellationToken cancellationToken = default)
        {
            // Note: MongoDB TTL index should handle this automatically, 
            // but this manual cleanup provides an explicit fallback query.
            var filter = Builders<RefreshToken>.Filter.Lt(t => t.ExpiresAt, DateTime.UtcNow);
            await _context.RefreshTokens.DeleteManyAsync(filter, cancellationToken);
        }

        public async Task<List<RefreshToken>> GetByAdminIdAsync(Guid adminId, CancellationToken cancellationToken = default)
        {
            return await _context.RefreshTokens
                .Find(t => t.AdminId == adminId)
                .ToListAsync(cancellationToken);
        }
    }
}
