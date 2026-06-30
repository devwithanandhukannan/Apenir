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

        public async Task<Admin?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return await _context.Admins
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == id && !a.IsDeleted, cancellationToken);
        }

        public async Task<Admin?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            var lowercaseEmail = email.ToLowerInvariant();
            return await _context.Admins
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Email.ToLower() == lowercaseEmail && !a.IsDeleted, cancellationToken);
        }

        public async Task<bool> ExistsAsync(string email, CancellationToken cancellationToken = default)
        {
            var lowercaseEmail = email.ToLowerInvariant();
            
            return await _context.Admins
                .AnyAsync(a => a.Email.ToLower() == lowercaseEmail && !a.IsDeleted, cancellationToken);
        }
    }
}