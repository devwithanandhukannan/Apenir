using Microsoft.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Extensions;
using Apenir.Core.Entities;
using Apenir.Core.Interfaces;

namespace Apenir.Infrastructure.Data;

public class AppDbContext : DbContext, IApplicationDbContext
{
    public DbSet<Admin> Admins => Set<Admin>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<WhatsAppSession> WhatsAppSessions => Set<WhatsAppSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        modelBuilder.Entity<Admin>().ToCollection("admins");
        modelBuilder.Entity<User>().ToCollection("users");
        modelBuilder.Entity<Customer>().ToCollection("customers");
        modelBuilder.Entity<OtpCode>().ToCollection("otp_codes");
        modelBuilder.Entity<WhatsAppSession>().ToCollection("whatsapp_sessions");
        modelBuilder.Entity<RefreshToken>().ToCollection("refresh_tokens");

        modelBuilder.Entity<Admin>().HasIndex(a => a.Email).IsUnique();
        modelBuilder.Entity<User>().HasIndex(u => u.Phone).IsUnique();
        modelBuilder.Entity<Customer>().HasIndex(c => c.Phone).IsUnique();
        modelBuilder.Entity<OtpCode>().HasIndex(o => o.Phone);
        modelBuilder.Entity<RefreshToken>().HasIndex(r => r.TokenHash);
    }
}