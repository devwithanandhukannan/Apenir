using System;
using System.Threading;
using System.Threading.Tasks;
using Apenir.Core.Entities;

namespace Apenir.Application.Common.Interfaces
{
    public interface IAdminRepository
    {
        Task<Admin?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
        Task<Admin?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);
        Task<Admin?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default);
        Task CreateAsync(Admin admin, CancellationToken cancellationToken = default);
        Task UpdateAsync(Admin admin, CancellationToken cancellationToken = default);
        Task DeleteAsync(Guid id, CancellationToken cancellationToken = default); // Soft delete
        Task<bool> ExistsAsync(string email, string username, CancellationToken cancellationToken = default);
    }
}
