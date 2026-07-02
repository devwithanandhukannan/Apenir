using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Apenir.Application.Common.Interfaces;
using Apenir.Core.Entities;
using Apenir.Infrastructure.Data;

namespace Apenir.Infrastructure.Repositories
{
    public class AdminRepository : IAdminRepository
    {
        private readonly AppDbContext _context;

        public AdminRepository(AppDbContext context)
        {
            _context = context;
        }

        public async Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var userId = id.ToString();
            return await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted && u.Roles != null && u.Roles.Count > 0, cancellationToken);
        }

        public async Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            var lowercaseEmail = email.ToLowerInvariant();
            return await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == lowercaseEmail && !u.IsDeleted && u.Roles != null && u.Roles.Count > 0, cancellationToken);
        }

        public async Task<bool> ExistsAsync(string email, CancellationToken cancellationToken = default)
        {
            var lowercaseEmail = email.ToLowerInvariant();
            
            return await _context.Users
                .AnyAsync(u => u.Email != null && u.Email.ToLower() == lowercaseEmail && !u.IsDeleted && u.Roles != null && u.Roles.Count > 0, cancellationToken);
        }

        public async Task CreateAsync(User admin, CancellationToken cancellationToken = default)
        {
            _context.Users.Add(admin);
            await _context.SaveChangesAsync(cancellationToken);
        }

        public async Task UpdateAsync(User admin, CancellationToken cancellationToken = default)
        {
            _context.Users.Update(admin);
            await _context.SaveChangesAsync(cancellationToken);
        }

        public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var userId = id.ToString();
            var admin = await _context.Users.FindAsync(new object[] { userId }, cancellationToken);
            if (admin != null)
            {
                admin.IsDeleted = true;
                _context.Users.Update(admin);
                await _context.SaveChangesAsync(cancellationToken);
            }
        }
    }
}