using System;
using System.Threading;
using System.Threading.Tasks;
using Apenir.Core.Entities;

namespace Apenir.Application.Common.Interfaces
{
    public interface IAdminRepository
    {
        Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
        Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);
        Task CreateAsync(User admin, CancellationToken cancellationToken = default);
        Task UpdateAsync(User admin, CancellationToken cancellationToken = default);
        Task DeleteAsync(Guid id, CancellationToken cancellationToken = default); // Soft delete
        Task<bool> ExistsAsync(string email, CancellationToken cancellationToken = default);
    }
}
