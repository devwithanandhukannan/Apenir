using Microsoft.EntityFrameworkCore;
using Apenir.Core.Entities;

namespace Apenir.Core.Interfaces;

public interface IApplicationDbContext
{
    DbSet<User> Users { get; }
    DbSet<Customer> Customers { get; }
    DbSet<OtpCode> OtpCodes { get; }
    DbSet<WhatsAppSession> WhatsAppSessions { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
