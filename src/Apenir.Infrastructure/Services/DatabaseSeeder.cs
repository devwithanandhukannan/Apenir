using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Apenir.Application.Common.Interfaces;
using Apenir.Application.Common.Models;
using Apenir.Core.Entities;
using Apenir.Infrastructure.Data;

namespace Apenir.Infrastructure.Services
{
    public interface IDatabaseSeeder
    {
        Task SeedAsync();
    }

    public class DatabaseSeeder : IDatabaseSeeder
    {
        private readonly AppDbContext _context;
        private readonly IPasswordHasher _passwordHasher;
        private readonly AdminSettings _adminSettings;
        private readonly ILogger<DatabaseSeeder> _logger;

        public DatabaseSeeder(
            AppDbContext context,
            IPasswordHasher passwordHasher,
            IOptions<AdminSettings> adminSettings,
            ILogger<DatabaseSeeder> logger)
        {
            _context = context;
            _passwordHasher = passwordHasher;
            _adminSettings = adminSettings.Value;
            _logger = logger;
        }

        public async Task SeedAsync()
        {
            try
            {
                var hasAdmin = await _context.Admins.AnyAsync();
                if (!hasAdmin)
                {
                    _logger.LogInformation("No administrators found in database. Seeding default administrator account...");

                    var defaultAdmin = new Admin
                    {
                        Id = Guid.NewGuid(),
                        Email = string.IsNullOrWhiteSpace(_adminSettings.DefaultEmail) ? "admin@apenir.com" : _adminSettings.DefaultEmail,
                        FullName = string.IsNullOrWhiteSpace(_adminSettings.DefaultFullName) ? "Default Administrator" : _adminSettings.DefaultFullName,
                        PasswordHash = _passwordHasher.Hash(string.IsNullOrWhiteSpace(_adminSettings.DefaultPassword) ? "Admin@Pass123" : _adminSettings.DefaultPassword),
                        IsActive = true,
                        IsDeleted = false,
                        Roles = new List<string> { "SuperAdmin", "Admin" },
                        Permissions = new List<string> { "all" },
                        CreatedAt = DateTime.UtcNow
                    };

                    _context.Admins.Add(defaultAdmin);
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Default administrator account successfully seeded. Email: {Email}", defaultAdmin.Email);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while seeding the database.");
            }
        }
    }
}
