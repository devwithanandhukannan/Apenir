using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Apenir.Application.Common.Interfaces;
using Apenir.Application.Common.Models;
using Apenir.Core.Entities;
using Apenir.Core.Enums;
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
                // ─────────────────────────────────────────────────────────────
                // 1. ADMINS
                // ─────────────────────────────────────────────────────────────
                if (!await _context.Admins.AnyAsync())
                {
                    _logger.LogInformation("Seeding administrator accounts...");

                    var admins = new List<Admin>();

                    // Default admin from appsettings
                    if (!string.IsNullOrWhiteSpace(_adminSettings.DefaultEmail) &&
                        !string.IsNullOrWhiteSpace(_adminSettings.DefaultPassword))
                    {
                        admins.Add(new Admin
                        {
                            Id           = Guid.NewGuid(),
                            Email        = _adminSettings.DefaultEmail,
                            FullName     = string.IsNullOrWhiteSpace(_adminSettings.DefaultFullName) ? "Super Admin" : _adminSettings.DefaultFullName,
                            PasswordHash = _passwordHasher.Hash(_adminSettings.DefaultPassword),
                            IsActive     = true,
                            IsDeleted    = false,
                            Roles        = new List<string> { "SuperAdmin", "Admin" },
                            Permissions  = new List<string> { "all" },
                            CreatedAt    = DateTime.UtcNow.AddDays(-60)
                        });
                    }

                    // Demo Ops Manager
                    admins.Add(new Admin
                    {
                        Id           = Guid.NewGuid(),
                        Email        = "ops.manager@apenir.com",
                        FullName     = "Ravi Menon",
                        PasswordHash = _passwordHasher.Hash("OpsPass@2025"),
                        IsActive     = true,
                        IsDeleted    = false,
                        Roles        = new List<string> { "Admin", "OpsManager" },
                        Permissions  = new List<string> { "branches.read", "appointments.read", "reports.read", "payrolls.manage" },
                        LastLoginAt  = DateTime.UtcNow.AddHours(-3),
                        CreatedAt    = DateTime.UtcNow.AddDays(-30)
                    });

                    // Demo Support Lead
                    admins.Add(new Admin
                    {
                        Id           = Guid.NewGuid(),
                        Email        = "support@apenir.com",
                        FullName     = "Priya Nair",
                        PasswordHash = _passwordHasher.Hash("SupportPass@2025"),
                        IsActive     = true,
                        IsDeleted    = false,
                        Roles        = new List<string> { "Support" },
                        Permissions  = new List<string> { "customers.read", "appointments.read" },
                        LastLoginAt  = DateTime.UtcNow.AddHours(-1),
                        CreatedAt    = DateTime.UtcNow.AddDays(-15)
                    });

                    _context.Admins.AddRange(admins);
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Admins seeded ({Count} records).", admins.Count);
                }

                await BackfillLegacyUsersAsync();

                // ─────────────────────────────────────────────────────────────
                // 2. USERS  (SuperAdmin, Customer, Staff, Lab roles)
                // ─────────────────────────────────────────────────────────────
                var superAdminUser = await _context.Users.FirstOrDefaultAsync(u => u.Role == UserRole.SuperAdmin);
                if (superAdminUser == null)
                {
                    superAdminUser = new User
                    {
                        Id           = "user_superadmin_001",
                        Name         = "Super Admin User",
                        Email        = "admin@apenir.com",
                        Phone        = "1800111111",
                        Role         = UserRole.SuperAdmin,
                        PasswordHash = _passwordHasher.Hash("AdminPassword123!"),
                        IsActive     = true,
                        CreatedAt    = DateTime.UtcNow.AddDays(-90)
                    };
                    _context.Users.Add(superAdminUser);
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("SuperAdmin user seeded.");
                }

                // Customer users
                var customerUserCount = await _context.Users.CountAsync(u => u.Role == UserRole.Customer);
                if (customerUserCount < 5)
                {
                    var customerUsers = new List<User>
                    {
                        new() { Id = "user_cust_001", Name = "Ananya Sharma",   Email = "ananya.sharma@gmail.com",  Phone = "9876543210", Role = UserRole.Customer, IsActive = true,  CreatedAt = DateTime.UtcNow.AddDays(-45) },
                        new() { Id = "user_cust_002", Name = "Rohit Nair",      Email = "rohit.nair@yahoo.com",     Phone = "9876543211", Role = UserRole.Customer, IsActive = true,  CreatedAt = DateTime.UtcNow.AddDays(-30) },
                        new() { Id = "user_cust_003", Name = "Meera Pillai",    Email = "meera.pillai@outlook.com", Phone = "9876543212", Role = UserRole.Customer, IsActive = true,  CreatedAt = DateTime.UtcNow.AddDays(-20) },
                        new() { Id = "user_cust_004", Name = "Arjun Menon",     Email = "arjun.menon@gmail.com",    Phone = "9876543213", Role = UserRole.Customer, IsActive = true,  CreatedAt = DateTime.UtcNow.AddDays(-10) },
                        new() { Id = "user_cust_005", Name = "Divya Krishnan",  Email = "divya.k@gmail.com",        Phone = "9876543214", Role = UserRole.Customer, IsActive = false, CreatedAt = DateTime.UtcNow.AddDays(-60) },
                        new() { Id = "user_cust_006", Name = "Sanjay Verma",    Email = "sanjay.v@gmail.com",       Phone = "9876543215", Role = UserRole.Customer, IsActive = true,  CreatedAt = DateTime.UtcNow.AddDays(-5)  },
                    };
                    _context.Users.AddRange(customerUsers);
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Demo customer users seeded.");
                }

                // Staff users
                var staffUserCount = await _context.Users.CountAsync(u => u.Role == UserRole.Staff);
                if (staffUserCount < 3)
                {
                    var staffUsers = new List<User>
                    {
                        new() { Id = "user_staff_001", Name = "Arun Phlebotomist", Email = "arun.phlebo@apenir.com", Phone = "9000000101", Role = UserRole.Staff, IsActive = true, CreatedAt = DateTime.UtcNow.AddDays(-60) },
                        new() { Id = "user_staff_002", Name = "Kavitha Collector",  Email = "kavitha.c@apenir.com",   Phone = "9000000102", Role = UserRole.Staff, IsActive = true, CreatedAt = DateTime.UtcNow.AddDays(-50) },
                        new() { Id = "user_staff_003", Name = "Thomas Technician",  Email = "thomas.t@apenir.com",    Phone = "9000000103", Role = UserRole.Staff, IsActive = true, CreatedAt = DateTime.UtcNow.AddDays(-40) },
                    };
                    _context.Users.AddRange(staffUsers);
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Demo staff users seeded.");
                }

                // ─────────────────────────────────────────────────────────────
                // 3. SERVICES
                // ─────────────────────────────────────────────────────────────
                if (!await _context.Services.AnyAsync())
                {
                    _logger.LogInformation("Seeding master diagnostic services...");
                    var services = new List<Service>
                    {
                        new() { Id = "service_blood",    Name = "Blood Test",          Description = "CBC, LFT, RFT, Lipid profile & more",    Category = "Biochemistry",  BasePrice = 400.00m,  PlatformCommissionPct = 15.00m, IsActive = true,  CreatedAt = DateTime.UtcNow.AddDays(-90) },
                        new() { Id = "service_urine",    Name = "Urine Analysis",       Description = "Routine & microscopy urine exam",         Category = "Hematology",    BasePrice = 150.00m,  PlatformCommissionPct = 15.00m, IsActive = true,  CreatedAt = DateTime.UtcNow.AddDays(-90) },
                        new() { Id = "service_ecg",      Name = "ECG",                  Description = "12-lead electrocardiogram",               Category = "Cardiology",    BasePrice = 300.00m,  PlatformCommissionPct = 15.00m, IsActive = true,  CreatedAt = DateTime.UtcNow.AddDays(-90) },
                        new() { Id = "service_xray",     Name = "X-Ray",                Description = "Chest X-Ray single view",                Category = "Radiology",     BasePrice = 500.00m,  PlatformCommissionPct = 15.00m, IsActive = true,  CreatedAt = DateTime.UtcNow.AddDays(-90) },
                        new() { Id = "service_full",     Name = "Full Body Checkup",    Description = "Comprehensive standard body checkup",    Category = "Biochemistry",  BasePrice = 1500.00m, PlatformCommissionPct = 15.00m, IsActive = true,  CreatedAt = DateTime.UtcNow.AddDays(-90) },
                        new() { Id = "service_thyroid",  Name = "Thyroid Profile",      Description = "TSH, T3, T4 levels panel",               Category = "Endocrinology", BasePrice = 350.00m,  PlatformCommissionPct = 12.00m, IsActive = true,  CreatedAt = DateTime.UtcNow.AddDays(-60) },
                        new() { Id = "service_diabetes", Name = "Diabetes Screening",   Description = "Fasting glucose, HbA1c, post-prandial", Category = "Biochemistry",  BasePrice = 250.00m,  PlatformCommissionPct = 10.00m, IsActive = true,  CreatedAt = DateTime.UtcNow.AddDays(-60) },
                        new() { Id = "service_covid",    Name = "COVID-19 RT-PCR",      Description = "Real-time PCR test for SARS-CoV-2",      Category = "Microbiology",  BasePrice = 800.00m,  PlatformCommissionPct = 20.00m, IsActive = false, CreatedAt = DateTime.UtcNow.AddDays(-180) },
                    };
                    _context.Services.AddRange(services);
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Diagnostic services seeded ({Count} records).", services.Count);
                }

                var bloodTestService = await _context.Services.FirstOrDefaultAsync(s => s.Name == "Blood Test");
                if (bloodTestService == null) return;

                // ─────────────────────────────────────────────────────────────
                // 4. BRANCHES  (Lab users + Branches + Slot configs)
                // ─────────────────────────────────────────────────────────────
                if (!await _context.Branches.AnyAsync())
                {
                    _logger.LogInformation("Seeding lab users, branches & slot templates...");

                    var labManagers = new List<(string UserId, string Name, string Phone, string BranchName, string District, string City, string Pincode, decimal Lat, decimal Lng)>
                    {
                        ("user_lab_001", "Lal Manager",   "1800123456", "Lal PathLabs — Kochi",  "kochi",      "Kochi",      "682016", 9.9788m,  76.2798m),
                        ("user_lab_002", "SRL Manager",   "1800123457", "SRL Diagnostics",        "kochi",      "Kochi",      "682025", 9.9912m,  76.3012m),
                        ("user_lab_003", "Metro Manager", "1800123458", "Metro Diagnostics",      "trivandrum", "Trivandrum", "695001", 8.5241m,  76.9366m)
                    };

                    foreach (var mgr in labManagers)
                    {
                        var labUser = new User
                        {
                            Id        = mgr.UserId,
                            Name      = mgr.Name,
                            Phone     = mgr.Phone,
                            Role      = UserRole.Lab,
                            IsActive  = true,
                            CreatedAt = DateTime.UtcNow.AddDays(-90)
                        };
                        _context.Users.Add(labUser);
                        await _context.SaveChangesAsync();

                        var branch = new Branch
                        {
                            Id        = $"branch_{mgr.UserId}",
                            LabUserId = labUser.Id,
                            Name      = mgr.BranchName,
                            District  = mgr.District,
                            City      = mgr.City,
                            Pincode   = mgr.Pincode,
                            Latitude  = mgr.Lat,
                            Longitude = mgr.Lng,
                            Phone     = mgr.Phone,
                            IsActive  = true,
                            CreatedBy = superAdminUser.Id,
                            CreatedAt = DateTime.UtcNow.AddDays(-90)
                        };
                        _context.Branches.Add(branch);
                        await _context.SaveChangesAsync();

                        decimal customPrice = mgr.BranchName.Contains("Lal") ? 450.00m
                                           : mgr.BranchName.Contains("SRL") ? 420.00m : 400.00m;

                        _context.BranchServices.Add(new BranchService
                        {
                            Id                  = Guid.NewGuid().ToString(),
                            BranchId            = branch.Id,
                            ServiceId           = bloodTestService.Id,
                            CustomPrice         = customPrice,
                            CustomCommissionPct = 15.00m,
                            IsActive            = true
                        });

                        var slotTimes = new List<TimeOnly>
                        {
                            new(6, 0), new(7, 0), new(9, 0), new(11, 0), new(12, 0), new(14, 0), new(16, 0)
                        };
                        foreach (var day in Enum.GetValues<DayText>())
                        {
                            foreach (var time in slotTimes)
                            {
                                _context.BranchSlotConfigurations.Add(new BranchSlotConfiguration
                                {
                                    Id          = Guid.NewGuid().ToString(),
                                    BranchId    = branch.Id,
                                    DayText     = day,
                                    StartTime   = time,
                                    EndTime     = time.AddHours(1),
                                    MaxCapacity = 3,
                                    IsLeave     = false
                                });
                            }
                        }
                        await _context.SaveChangesAsync();
                    }
                    _logger.LogInformation("Lab users, branches, branch services and slot configs seeded.");
                }

                // ─────────────────────────────────────────────────────────────
                // 5. APPOINTMENT SLOTS  (next 7 days)
                // ─────────────────────────────────────────────────────────────
                if (!await _context.AppointmentSlots.AnyAsync())
                {
                    _logger.LogInformation("Generating appointment calendar slots (next 7 days)...");
                    var branches = await _context.Branches.ToListAsync();
                    var configs  = await _context.BranchSlotConfigurations.ToListAsync();
                    var startDate = DateTime.UtcNow.Date;

                    for (int i = 0; i < 7; i++)
                    {
                        var targetDate = startDate.AddDays(i);
                        int dayVal     = (int)targetDate.DayOfWeek;
                        DayText targetDay = dayVal == 0 ? DayText.Sun : (DayText)dayVal;

                        foreach (var branch in branches)
                        {
                            foreach (var cfg in configs.Where(c => c.BranchId == branch.Id && c.DayText == targetDay))
                            {
                                _context.AppointmentSlots.Add(new AppointmentSlot
                                {
                                    Id          = Guid.NewGuid().ToString(),
                                    BranchId    = branch.Id,
                                    SlotDate    = DateOnly.FromDateTime(targetDate),
                                    StartTime   = cfg.StartTime,
                                    EndTime     = cfg.EndTime,
                                    MaxCapacity = cfg.MaxCapacity,
                                    BookedCount = 0,
                                    IsAvailable = true
                                });
                            }
                        }
                    }
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Appointment slots seeded.");
                }

                // ─────────────────────────────────────────────────────────────
                // 6. CUSTOMERS
                // ─────────────────────────────────────────────────────────────
                if (!await _context.Customers.AnyAsync())
                {
                    _logger.LogInformation("Seeding customer profiles...");
                    var customers = new List<Customer>
                    {
                        new() { Id = Guid.NewGuid().ToString(), UserId = "user_cust_001", DateOfBirth = new DateOnly(1992,  3, 14), GenderEnum = Gender.Female, Address = "12A, Rose Apartments, MG Road, Kochi",              District = "kochi"      },
                        new() { Id = Guid.NewGuid().ToString(), UserId = "user_cust_002", DateOfBirth = new DateOnly(1988,  7, 22), GenderEnum = Gender.Male,   Address = "45, Green Villa, Edapally, Kochi",                  District = "kochi"      },
                        new() { Id = Guid.NewGuid().ToString(), UserId = "user_cust_003", DateOfBirth = new DateOnly(1995, 11,  5), GenderEnum = Gender.Female, Address = "Flat 2C, Sunrise Tower, Palarivattom, Kochi",       District = "kochi"      },
                        new() { Id = Guid.NewGuid().ToString(), UserId = "user_cust_004", DateOfBirth = new DateOnly(1985,  1, 30), GenderEnum = Gender.Male,   Address = "8, Jubilee Hills, Trivandrum",                       District = "trivandrum" },
                        new() { Id = Guid.NewGuid().ToString(), UserId = "user_cust_005", DateOfBirth = new DateOnly(1999,  6, 18), GenderEnum = Gender.Female, Address = "22, Palm Grove, Thrissur",                          District = "thrissur"   },
                        new() { Id = Guid.NewGuid().ToString(), UserId = "user_cust_006", DateOfBirth = new DateOnly(1978,  9,  2), GenderEnum = Gender.Male,   Address = "Flat 7D, Marina Enclave, Kozhikode",                District = "kozhikode"  },
                        new() { Id = Guid.NewGuid().ToString(), UserId = superAdminUser.Id, DateOfBirth = new DateOnly(1990, 5, 15), GenderEnum = Gender.Male,  Address = "Flat 4B, Emerald Residency, Kochi",                 District = "kochi"      },
                    };
                    _context.Customers.AddRange(customers);
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Customer profiles seeded ({Count} records).", customers.Count);
                }

                // ─────────────────────────────────────────────────────────────
                // 7. OTP CODES
                // ─────────────────────────────────────────────────────────────
                if (!await _context.OtpCodes.AnyAsync())
                {
                    _logger.LogInformation("Seeding demo OTP codes...");
                    var otpCodes = new List<OtpCode>
                    {
                        new() { Id = Guid.NewGuid().ToString(), Phone = "9876543210", HashCode = _passwordHasher.Hash("482910"), ExpiresAt = DateTime.UtcNow.AddMinutes(-5),  Attempts = 1 },  // Expired
                        new() { Id = Guid.NewGuid().ToString(), Phone = "9876543211", HashCode = _passwordHasher.Hash("371842"), ExpiresAt = DateTime.UtcNow.AddMinutes(10),  Attempts = 0 },  // Active
                        new() { Id = Guid.NewGuid().ToString(), Phone = "9876543212", HashCode = _passwordHasher.Hash("193847"), ExpiresAt = DateTime.UtcNow.AddMinutes(-30), Attempts = 3 },  // Expired & max attempts
                        new() { Id = Guid.NewGuid().ToString(), Phone = "9876543213", HashCode = _passwordHasher.Hash("562034"), ExpiresAt = DateTime.UtcNow.AddMinutes(8),   Attempts = 0 },  // Active
                        new() { Id = Guid.NewGuid().ToString(), Phone = "9000000101", HashCode = _passwordHasher.Hash("774521"), ExpiresAt = DateTime.UtcNow.AddMinutes(12),  Attempts = 1 },  // Active, 1 attempt
                    };
                    _context.OtpCodes.AddRange(otpCodes);
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("OTP codes seeded ({Count} records).", otpCodes.Count);
                }

                // ─────────────────────────────────────────────────────────────
                // 8. REFRESH TOKENS
                // ─────────────────────────────────────────────────────────────
                if (!await _context.RefreshTokens.AnyAsync())
                {
                    _logger.LogInformation("Seeding demo refresh tokens...");
                    var refreshTokens = new List<RefreshToken>
                    {
                        new()
                        {
                            Id           = Guid.NewGuid(),
                            Token        = "rt_ananya_active_001",
                            TokenHash    = _passwordHasher.Hash("rt_ananya_active_001"),
                            UserId       = "user_cust_001",
                            ExpiresAt    = DateTime.UtcNow.AddDays(7),
                            CreatedAt    = DateTime.UtcNow.AddHours(-2),
                            CreatedByIp  = "103.21.58.11",
                            DeviceName   = "iPhone 15 Pro",
                            UserAgent    = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)",
                            IpAddress    = "103.21.58.11",
                            IsRevoked    = false
                        },
                        new()
                        {
                            Id           = Guid.NewGuid(),
                            Token        = "rt_rohit_active_001",
                            TokenHash    = _passwordHasher.Hash("rt_rohit_active_001"),
                            UserId       = "user_cust_002",
                            ExpiresAt    = DateTime.UtcNow.AddDays(5),
                            CreatedAt    = DateTime.UtcNow.AddHours(-8),
                            CreatedByIp  = "49.206.12.44",
                            DeviceName   = "Samsung Galaxy S24",
                            UserAgent    = "Mozilla/5.0 (Linux; Android 14; SM-S928B)",
                            IpAddress    = "49.206.12.44",
                            IsRevoked    = false
                        },
                        new()
                        {
                            Id              = Guid.NewGuid(),
                            Token           = "rt_arjun_revoked_001",
                            TokenHash       = _passwordHasher.Hash("rt_arjun_revoked_001"),
                            UserId          = "user_cust_004",
                            ExpiresAt       = DateTime.UtcNow.AddDays(-1),
                            CreatedAt       = DateTime.UtcNow.AddDays(-8),
                            RevokedAt       = DateTime.UtcNow.AddDays(-1),
                            RevokedByIp     = "49.206.13.55",
                            CreatedByIp     = "49.206.13.55",
                            DeviceName      = "OnePlus 12",
                            UserAgent       = "Mozilla/5.0 (Linux; Android 14; CPH2573)",
                            IpAddress       = "49.206.13.55",
                            IsRevoked       = true
                        },
                        new()
                        {
                            Id           = Guid.NewGuid(),
                            Token        = "rt_staff_arun_001",
                            TokenHash    = _passwordHasher.Hash("rt_staff_arun_001"),
                            UserId       = "user_staff_001",
                            ExpiresAt    = DateTime.UtcNow.AddDays(6),
                            CreatedAt    = DateTime.UtcNow.AddHours(-1),
                            CreatedByIp  = "192.168.1.10",
                            DeviceName   = "Android Tablet",
                            UserAgent    = "ApenirApp/2.1 (Android 13; Generic Tablet)",
                            IpAddress    = "192.168.1.10",
                            IsRevoked    = false
                        },
                        new()
                        {
                            Id           = Guid.NewGuid(),
                            Token        = "rt_superadmin_001",
                            TokenHash    = _passwordHasher.Hash("rt_superadmin_001"),
                            UserId       = superAdminUser.Id,
                            ExpiresAt    = DateTime.UtcNow.AddDays(7),
                            CreatedAt    = DateTime.UtcNow.AddMinutes(-30),
                            CreatedByIp  = "127.0.0.1",
                            DeviceName   = "MacBook Pro",
                            UserAgent    = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)",
                            IpAddress    = "127.0.0.1",
                            IsRevoked    = false
                        },
                    };
                    _context.RefreshTokens.AddRange(refreshTokens);
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Refresh tokens seeded ({Count} records).", refreshTokens.Count);
                }

                // ─────────────────────────────────────────────────────────────
                // 9. WHATSAPP SESSIONS
                // ─────────────────────────────────────────────────────────────
                if (!await _context.WhatsAppSessions.AnyAsync())
                {
                    _logger.LogInformation("Seeding demo WhatsApp sessions...");
                    var sessions = new List<WhatsAppSession>
                    {
                        new()
                        {
                            Id              = Guid.NewGuid().ToString(),
                            Phone           = "9876543210",
                            CurrentState    = WhatsAppState.Done,
                            SelectedTestId  = "service_blood",
                            SelectedCity    = "Kochi",
                            SelectedLabId   = "branch_user_lab_001",
                            SelectedLabName = "Lal PathLabs — Kochi",
                            SelectedSlot    = "2026-07-02T09:00:00",
                            MemberCount     = 2,
                            LocationShared  = true,
                            Passcode        = "8421",
                            UpdatedAt       = DateTime.UtcNow.AddMinutes(-10)
                        },
                        new()
                        {
                            Id              = Guid.NewGuid().ToString(),
                            Phone           = "9876543211",
                            CurrentState    = WhatsAppState.ChoosingSlot,
                            SelectedTestId  = "service_full",
                            SelectedCity    = "Kochi",
                            SelectedLabId   = "branch_user_lab_002",
                            SelectedLabName = "SRL Diagnostics",
                            SelectedSlot    = null,
                            MemberCount     = 1,
                            LocationShared  = false,
                            Passcode        = null,
                            UpdatedAt       = DateTime.UtcNow.AddMinutes(-3)
                        },
                        new()
                        {
                            Id              = Guid.NewGuid().ToString(),
                            Phone           = "9876543212",
                            CurrentState    = WhatsAppState.ChoosingCity,
                            SelectedTestId  = "service_thyroid",
                            SelectedCity    = null,
                            SelectedLabId   = null,
                            SelectedLabName = null,
                            SelectedSlot    = null,
                            MemberCount     = 0,
                            LocationShared  = false,
                            Passcode        = null,
                            UpdatedAt       = DateTime.UtcNow.AddMinutes(-25)
                        },
                        new()
                        {
                            Id              = Guid.NewGuid().ToString(),
                            Phone           = "9876543213",
                            CurrentState    = WhatsAppState.AwaitingPayment,
                            SelectedTestId  = "service_ecg",
                            SelectedCity    = "Trivandrum",
                            SelectedLabId   = "branch_user_lab_003",
                            SelectedLabName = "Metro Diagnostics",
                            SelectedSlot    = "2026-07-03T11:00:00",
                            MemberCount     = 1,
                            LocationShared  = true,
                            Passcode        = "3397",
                            UpdatedAt       = DateTime.UtcNow.AddMinutes(-1)
                        },
                        new()
                        {
                            Id              = Guid.NewGuid().ToString(),
                            Phone           = "9000000101",
                            CurrentState    = WhatsAppState.Start,
                            SelectedTestId  = null,
                            SelectedCity    = null,
                            SelectedLabId   = null,
                            SelectedLabName = null,
                            SelectedSlot    = null,
                            MemberCount     = 0,
                            LocationShared  = false,
                            Passcode        = null,
                            UpdatedAt       = DateTime.UtcNow.AddSeconds(-30)
                        },
                    };
                    _context.WhatsAppSessions.AddRange(sessions);
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("WhatsApp sessions seeded ({Count} records).", sessions.Count);
                }

                // ─────────────────────────────────────────────────────────────
                // 10. BOOKING FLOW  (Appointment + Member + Report + Payment + Log + Payroll)
                // ─────────────────────────────────────────────────────────────
                if (!await _context.Appointments.AnyAsync())
                {
                    _logger.LogInformation("Seeding sample booking records...");
                    var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Name.Contains("Lal"));
                    var slot   = branch != null ? await _context.AppointmentSlots.FirstOrDefaultAsync(s => s.BranchId == branch.Id) : null;

                    if (branch != null && slot != null)
                    {
                        var appointment = new Appointment
                        {
                            Id                = Guid.NewGuid().ToString(),
                            AppointmentNumber = $"BK-{DateTime.UtcNow:yyyyMMdd}-9999",
                            CustomerUserId    = superAdminUser.Id,
                            BranchId          = branch.Id,
                            AppointmentSlotId = slot.Id,
                            LocationLatitude  = branch.Latitude,
                            LocationLongitude = branch.Longitude,
                            LocationAddress   = "Flat 4B, Emerald Residency, Kochi",
                            Passcode          = "1234",
                            Status            = AppointmentStatus.Completed,
                            TotalAmount       = 450.00m,
                            PlatformCommission = 67.50m,
                            LabPayout         = 382.50m,
                            AssignedStaffId   = "user_staff_001",
                            CreatedAt         = DateTime.UtcNow.AddDays(-1),
                            UpdatedAt         = DateTime.UtcNow,
                            MemberCount       = 1
                        };
                        _context.Appointments.Add(appointment);
                        await _context.SaveChangesAsync();

                        var member = new AppointmentMember
                        {
                            Id              = Guid.NewGuid().ToString(),
                            AppointmentId   = appointment.Id,
                            MemberName      = "Ananya Sharma",
                            Age             = 32,
                            Gender          = Gender.Female,
                            Relationship    = "Self",
                            AdditionalNotes = "Fasting for 12 hours. Diabetic patient — handle with care."
                        };
                        _context.AppointmentMembers.Add(member);
                        await _context.SaveChangesAsync();

                        _context.Reports.Add(new Report
                        {
                            Id            = Guid.NewGuid().ToString(),
                            AppointmentId = appointment.Id,
                            MemberId      = member.Id,
                            FileUrl       = "https://s3.amazonaws.com/apenir-reports/CBC_AnanyaSharma_BK9999.pdf",
                            FileName      = "CBC_Report_AnanyaSharma.pdf",
                            UploadedBy    = branch.LabUserId,
                            WhatsappSent  = true,
                            CreatedAt     = DateTime.UtcNow.AddHours(-20)
                        });

                        _context.Payments.Add(new Payment
                        {
                            Id                = Guid.NewGuid().ToString(),
                            AppointmentId     = appointment.Id,
                            RazorpayOrderId   = "order_OprSample9999",
                            RazorpayPaymentId = "pay_PmtSample9999",
                            Status            = PaymentStatus.Paid,
                            PaymentMethod     = PaymentMethod.UPI,
                            PaidAt            = DateTime.UtcNow.AddDays(-1),
                            CreatedAt         = DateTime.UtcNow.AddDays(-1)
                        });

                        _context.StaffOrderLogs.Add(new StaffOrderLog
                        {
                            Id            = Guid.NewGuid().ToString(),
                            AppointmentId = appointment.Id,
                            StaffId       = "user_staff_001",
                            Status        = StaffOrderStatus.Collected,
                            Note          = "Blood sample collected successfully. Patient cooperative.",
                            ContextData   = "{\"battery\": 85, \"network\": \"4G\", \"location_accuracy\": \"5m\"}",
                            LoggedAt      = DateTime.UtcNow.AddDays(-1)
                        });

                        _context.Payrolls.Add(new Payroll
                        {
                            Id                 = Guid.NewGuid().ToString(),
                            BranchId           = branch.Id,
                            PeriodType         = "Weekly",
                            PeriodStart        = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7)),
                            PeriodEnd          = DateOnly.FromDateTime(DateTime.UtcNow),
                            GrossAmount        = 450.00m,
                            PlatformCommission = 67.50m,
                            NetPayout          = 382.50m,
                            Status             = PayrollStatus.Pending,
                            CreatedAt          = DateTime.UtcNow
                        });

                        slot.BookedCount = 1;
                        _context.AppointmentSlots.Update(slot);

                        await _context.SaveChangesAsync();
                        _logger.LogInformation("Sample booking transaction flow seeded successfully.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while seeding the database.");
            }
        }

        private async Task BackfillLegacyUsersAsync()
        {
            // MongoDB EF Core LINQ3 cannot translate nullable OR comparisons in a Where clause.
            // Load all users into memory first, then filter with plain C#.
            var allUsers = await _context.Users.ToListAsync();

            var legacyUsers = allUsers
                .Where(u => string.IsNullOrWhiteSpace(u.Name) || u.CreatedAt == null || u.IsActive == null)
                .ToList();

            if (!legacyUsers.Any()) return;

            _logger.LogInformation("Backfilling {Count} legacy user records missing Name/CreatedAt/IsActive.", legacyUsers.Count);

            foreach (var user in legacyUsers)
            {
                if (string.IsNullOrWhiteSpace(user.Name))  user.Name      = "WhatsApp Customer";
                if (user.CreatedAt == null)                 user.CreatedAt = DateTime.UtcNow;
                if (user.IsActive == null)                  user.IsActive  = true;
            }

            await _context.SaveChangesAsync();
        }
    }
}
