using System;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;
using Apenir.Application.Common.Interfaces;
using Apenir.Core.Entities;
using Apenir.Infrastructure.Persistence;

namespace Apenir.Infrastructure.Repositories
{
    public class AdminRepository : IAdminRepository
    {
        private readonly MongoDbContext _context;

        public AdminRepository(MongoDbContext context)
        {
            _context = context;
        }

        public async Task<Admin?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return await _context.Admins
                .Find(a => a.Id == id && !a.IsDeleted)
                .FirstOrDefaultAsync(cancellationToken);
        }

        public async Task<Admin?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            var lowercaseEmail = email.ToLowerInvariant();
            return await _context.Admins
                .Find(a => a.Email.ToLower() == lowercaseEmail && !a.IsDeleted)
                .FirstOrDefaultAsync(cancellationToken);
        }


        public async Task CreateAsync(Admin admin, CancellationToken cancellationToken = default)
        {
            await _context.Admins.InsertOneAsync(admin, null, cancellationToken);
        }

        public async Task UpdateAsync(Admin admin, CancellationToken cancellationToken = default)
        {
            var filter = Builders<Admin>.Filter.Eq(a => a.Id, admin.Id);
            await _context.Admins.ReplaceOneAsync(filter, admin, new ReplaceOptions(), cancellationToken);
        }

        public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            // Soft delete
            var filter = Builders<Admin>.Filter.Eq(a => a.Id, id);
            var update = Builders<Admin>.Update
                .Set(a => a.IsDeleted, true)
                .Set(a => a.UpdatedAt, DateTime.UtcNow);

            await _context.Admins.UpdateOneAsync(filter, update, null, cancellationToken);
        }

        public async Task<bool> ExistsAsync(string email, CancellationToken cancellationToken = default)
        {
            var lowercaseEmail = email.ToLowerInvariant();
            
            return await _context.Admins
                .Find(a => a.Email.ToLower() == lowercaseEmail && !a.IsDeleted)
                .AnyAsync(cancellationToken);
        }
    }
}
