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
                // 1. Seed Administrators
                var hasAdmin = await _context.Admins.AnyAsync();
                if (!hasAdmin)
                {
                    _logger.LogInformation("No administrators found in database. Seeding default administrator account...");

                    if (string.IsNullOrWhiteSpace(_adminSettings.DefaultEmail) || string.IsNullOrWhiteSpace(_adminSettings.DefaultPassword))
                    {
                        _logger.LogWarning("AdminSettings:DefaultEmail or AdminSettings:DefaultPassword is not configured. Skipping default administrator seeding.");
                    }
                    else
                    {
                        var defaultAdmin = new Admin
                        {
                            Id = Guid.NewGuid(),
                            Email = _adminSettings.DefaultEmail,
                            FullName = string.IsNullOrWhiteSpace(_adminSettings.DefaultFullName) ? "Super Admin" : _adminSettings.DefaultFullName,
                            PasswordHash = _passwordHasher.Hash(_adminSettings.DefaultPassword),
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

                // 2. Seed SuperAdmin User in users table
                var superAdminUser = await _context.Users.FirstOrDefaultAsync(u => u.Role == UserRole.SuperAdmin);
                if (superAdminUser == null)
                {
                    superAdminUser = new User
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = "Super Admin User",
                        Email = "admin@apenir.com",
                        Phone = "1800111111",
                        Role = UserRole.SuperAdmin,
                        PasswordHash = _passwordHasher.Hash("AdminPassword123!"),
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.Users.Add(superAdminUser);
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("SuperAdmin User seeded in users table.");
                }

                // 3. Seed Services
                if (!await _context.Services.AnyAsync())
                {
                    _logger.LogInformation("Seeding master diagnostic services...");
                    var services = new List<Service>
                    {
                        new() { Id = "service_blood", Name = "Blood Test", Description = "CBC, LFT, RFT, Lipid profile & more", Category = "Biochemistry", BasePrice = 400.00m, PlatformCommissionPct = 15.00m, IsActive = true, CreatedAt = DateTime.UtcNow },
                        new() { Id = "service_urine", Name = "Urine Analysis", Description = "Routine & microscopy urine exam", Category = "Hematology", BasePrice = 150.00m, PlatformCommissionPct = 15.00m, IsActive = true, CreatedAt = DateTime.UtcNow },
                        new() { Id = "service_ecg", Name = "ECG", Description = "12-lead electrocardiogram", Category = "Cardiology", BasePrice = 300.00m, PlatformCommissionPct = 15.00m, IsActive = true, CreatedAt = DateTime.UtcNow },
                        new() { Id = "service_xray", Name = "X-Ray", Description = "Chest X-Ray single view", Category = "Radiology", BasePrice = 500.00m, PlatformCommissionPct = 15.00m, IsActive = true, CreatedAt = DateTime.UtcNow },
                        new() { Id = "service_full", Name = "Full Body Checkup", Description = "Comprehensive standard body checkup", Category = "Biochemistry", BasePrice = 1500.00m, PlatformCommissionPct = 15.00m, IsActive = true, CreatedAt = DateTime.UtcNow }
                    };
                    _context.Services.AddRange(services);
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Master diagnostic services seeded successfully.");
                }

                // Get blood test service for foreign keys
                var bloodTestService = await _context.Services.FirstOrDefaultAsync(s => s.Name == "Blood Test");
                if (bloodTestService == null) return;

                // 4. Seed Lab Users and Branches
                if (!await _context.Branches.AnyAsync())
                {
                    _logger.LogInformation("Seeding lab manager users and branches...");

                    var labManagers = new List<(string Name, string Phone, string BranchName, string District, string City, string Pincode, decimal Lat, decimal Lng)>
                    {
                        ("Lal Manager", "1800123456", "Lal PathLabs — Kochi", "kochi", "Kochi", "682016", 9.9788m, 76.2798m),
                        ("SRL Manager", "1800123457", "SRL Diagnostics", "kochi", "Kochi", "682025", 9.9912m, 76.3012m),
                        ("Metro Manager", "1800123458", "Metro Diagnostics", "trivandrum", "Trivandrum", "695001", 8.5241m, 76.9366m)
                    };

                    foreach (var manager in labManagers)
                    {
                        // Create lab user
                        var labUser = new User
                        {
                            Id = Guid.NewGuid().ToString(),
                            Name = manager.Name,
                            Phone = manager.Phone,
                            Role = UserRole.Lab,
                            IsActive = true,
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.Users.Add(labUser);
                        await _context.SaveChangesAsync();

                        // Create branch
                        var branch = new Branch
                        {
                            Id = Guid.NewGuid().ToString(),
                            LabUserId = labUser.Id,
                            Name = manager.BranchName,
                            District = manager.District,
                            City = manager.City,
                            Pincode = manager.Pincode,
                            Latitude = manager.Lat,
                            Longitude = manager.Lng,
                            Phone = manager.Phone,
                            IsActive = true,
                            CreatedBy = superAdminUser.Id,
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.Branches.Add(branch);
                        await _context.SaveChangesAsync();

                        // Seed pricing overrides (BranchServices) for Blood Test
                        decimal customPrice = manager.BranchName.Contains("Lal") ? 450.00m :
                                             manager.BranchName.Contains("SRL") ? 420.00m : 400.00m;
                        var branchService = new BranchService
                        {
                            Id = Guid.NewGuid().ToString(),
                            BranchId = branch.Id,
                            ServiceId = bloodTestService.Id,
                            CustomPrice = customPrice,
                            CustomCommissionPct = 15.00m,
                            IsActive = true
                        };
                        _context.BranchServices.Add(branchService);

                        // Seed recurring availability templates (BranchSlotConfigurations)
                        var days = Enum.GetValues<DayText>();
                        var times = new List<TimeOnly>
                        {
                            new(6, 0), new(7, 0), new(9, 0), new(11, 0), new(12, 0), new(14, 0), new(16, 0)
                        };

                        foreach (var day in days)
                        {
                            foreach (var time in times)
                            {
                                var slotConfig = new BranchSlotConfiguration
                                {
                                    Id = Guid.NewGuid().ToString(),
                                    BranchId = branch.Id,
                                    DayText = day,
                                    StartTime = time,
                                    EndTime = time.AddHours(1),
                                    MaxCapacity = 3,
                                    IsLeave = false
                                };
                                _context.BranchSlotConfigurations.Add(slotConfig);
                            }
                        }
                        await _context.SaveChangesAsync();
                    }
                    _logger.LogInformation("Lab managers, branches, branch services, and slot templates seeded.");
                }

                // 5. Seed runtime AppointmentSlots for the next 7 days
                if (!await _context.AppointmentSlots.AnyAsync())
                {
                    _logger.LogInformation("Generating active calendar appointment slots for the next 7 days...");
                    var branches = await _context.Branches.ToListAsync();
                    var configs = await _context.BranchSlotConfigurations.ToListAsync();

                    var startDate = DateTime.UtcNow.Date;
                    for (int i = 0; i < 7; i++)
                    {
                        var targetDate = startDate.AddDays(i);
                        // Map DayOfWeek (Sunday = 0, Monday = 1, etc.) to DayText (Mon = 1, Tue = 2, ..., Sun = 7)
                        int dayVal = (int)targetDate.DayOfWeek;
                        DayText targetDay = dayVal == 0 ? DayText.Sun : (DayText)dayVal;

                        var matchingConfigs = configs.Where(c => c.DayText == targetDay).ToList();

                        foreach (var branch in branches)
                        {
                            var branchConfigs = matchingConfigs.Where(c => c.BranchId == branch.Id).ToList();
                            foreach (var config in branchConfigs)
                            {
                                var calendarSlot = new AppointmentSlot
                                {
                                    Id = Guid.NewGuid().ToString(),
                                    BranchId = branch.Id,
                                    SlotDate = DateOnly.FromDateTime(targetDate),
                                    StartTime = config.StartTime,
                                    EndTime = config.EndTime,
                                    MaxCapacity = config.MaxCapacity,
                                    BookedCount = 0,
                                    IsAvailable = true
                                };
                                _context.AppointmentSlots.Add(calendarSlot);
                            }
                        }
                    }
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Calendar appointment slots successfully seeded.");
                }

                // 6. Seed a sample booking flow (appointment, member, report, payment, log)
                if (!await _context.Appointments.AnyAsync())
                {
                    _logger.LogInformation("Seeding sample booking records (Appointments, Payments, Reports, Logs)...");
                    var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Name.Contains("Lal"));
                    if (branch != null)
                    {
                        var slot = await _context.AppointmentSlots.FirstOrDefaultAsync(s => s.BranchId == branch.Id);
                        
                        if (slot != null)
                        {
                            // Create sample Customer profile for the SuperAdmin User
                            var customer = new Customer
                        {
                            Id = Guid.NewGuid().ToString(),
                            UserId = superAdminUser.Id,
                            DateOfBirth = new DateOnly(1990, 5, 15),
                            GenderEnum = Gender.Male,
                            Address = "Flat 4B, Emerald Residency, Kochi",
                            District = "kochi"
                        };
                        _context.Customers.Add(customer);
                        await _context.SaveChangesAsync();

                        // 1. Create Appointment
                        var appointment = new Appointment
                        {
                            Id = Guid.NewGuid().ToString(),
                            AppointmentNumber = $"BK-{DateTime.UtcNow:yyyyMMdd}-9999",
                            CustomerUserId = superAdminUser.Id,
                            BranchId = branch.Id,
                            AppointmentSlotId = slot.Id,
                            LocationLatitude = branch.Latitude,
                            LocationLongitude = branch.Longitude,
                            LocationAddress = customer.Address,
                            Passcode = "1234",
                            Status = AppointmentStatus.Completed,
                            TotalAmount = 450.00m,
                            PlatformCommission = 67.50m,
                            LabPayout = 382.50m,
                            CreatedAt = DateTime.UtcNow.AddDays(-1),
                            UpdatedAt = DateTime.UtcNow
                        };
                        _context.Appointments.Add(appointment);
                        await _context.SaveChangesAsync();

                        // 2. Create AppointmentMember
                        var member = new AppointmentMember
                        {
                            Id = Guid.NewGuid().ToString(),
                            AppointmentId = appointment.Id,
                            MemberName = "John Doe",
                            Age = 35,
                            Gender = Gender.Male,
                            Relationship = "Self",
                            AdditionalNotes = "Fasting for 10 hours completed."
                        };
                        _context.AppointmentMembers.Add(member);
                        await _context.SaveChangesAsync();

                        // 3. Create Report
                        var report = new Report
                        {
                            Id = Guid.NewGuid().ToString(),
                            AppointmentId = appointment.Id,
                            MemberId = member.Id,
                            FileUrl = "https://s3.amazonaws.com/labcare-reports/CBC_JohnDoe_BK9999.pdf",
                            FileName = "CBC_Report_JohnDoe.pdf",
                            UploadedBy = branch.LabUserId,
                            WhatsappSent = true,
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.Reports.Add(report);

                        // 4. Create Payment
                        var payment = new Payment
                        {
                            Id = Guid.NewGuid().ToString(),
                            AppointmentId = appointment.Id,
                            RazorpayOrderId = "order_OprSample9999",
                            RazorpayPaymentId = "pay_PmtSample9999",
                            Status = PaymentStatus.Paid,
                            PaymentMethod = PaymentMethod.UPI,
                            PaidAt = DateTime.UtcNow.AddDays(-1),
                            CreatedAt = DateTime.UtcNow.AddDays(-1)
                        };
                        _context.Payments.Add(payment);

                        // 5. Create StaffOrderLog
                        var log = new StaffOrderLog
                        {
                            Id = Guid.NewGuid().ToString(),
                            AppointmentId = appointment.Id,
                            StaffId = superAdminUser.Id, // Phlebotomist
                            Status = StaffOrderStatus.Collected,
                            Note = "Blood sample collected successfully.",
                            ContextData = "{\"battery\": 85, \"network\": \"4G\", \"location_accuracy\": \"5m\"}",
                            LoggedAt = DateTime.UtcNow.AddDays(-1)
                        };
                        _context.StaffOrderLogs.Add(log);

                        // 6. Create Payroll
                        var payroll = new Payroll
                        {
                            Id = Guid.NewGuid().ToString(),
                            BranchId = branch.Id,
                            PeriodType = "Weekly",
                            PeriodStart = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7)),
                            PeriodEnd = DateOnly.FromDateTime(DateTime.UtcNow),
                            GrossAmount = 450.00m,
                            PlatformCommission = 67.50m,
                            NetPayout = 382.50m,
                            Status = PayrollStatus.Pending,
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.Payrolls.Add(payroll);

                        // Update slot booked count
                        slot.BookedCount = 1;
                        _context.AppointmentSlots.Update(slot);

                        await _context.SaveChangesAsync();
                        _logger.LogInformation("Sample booking transaction flow seeded successfully.");
                    }
                }
            }
        }
        catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while seeding the database.");
            }
        }
    }
}
