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
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<BranchService> BranchServices => Set<BranchService>();
    public DbSet<BranchSlotConfiguration> BranchSlotConfigurations => Set<BranchSlotConfiguration>();
    public DbSet<AppointmentSlot> AppointmentSlots => Set<AppointmentSlot>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AppointmentMember> AppointmentMembers => Set<AppointmentMember>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Payroll> Payrolls => Set<Payroll>();
    public DbSet<StaffOrderLog> StaffOrderLogs => Set<StaffOrderLog>();
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
        modelBuilder.Entity<Branch>().ToCollection("branches");
        modelBuilder.Entity<Service>().ToCollection("services");
        modelBuilder.Entity<BranchService>().ToCollection("branch_services");
        modelBuilder.Entity<BranchSlotConfiguration>().ToCollection("branch_slot_configurations");
        modelBuilder.Entity<AppointmentSlot>().ToCollection("appointment_slots");
        modelBuilder.Entity<Appointment>().ToCollection("appointments");
        modelBuilder.Entity<AppointmentMember>().ToCollection("appointment_members");
        modelBuilder.Entity<Report>().ToCollection("reports");
        modelBuilder.Entity<Payment>().ToCollection("payments");
        modelBuilder.Entity<Payroll>().ToCollection("payrolls");
        modelBuilder.Entity<StaffOrderLog>().ToCollection("staff_order_logs");
        modelBuilder.Entity<OtpCode>().ToCollection("otp_codes");
        modelBuilder.Entity<WhatsAppSession>().ToCollection("whatsapp_sessions");
        modelBuilder.Entity<RefreshToken>().ToCollection("refresh_tokens");

        modelBuilder.Entity<Admin>().HasIndex(a => a.Email).IsUnique();
        modelBuilder.Entity<User>().HasIndex(u => u.Phone).IsUnique();
        modelBuilder.Entity<Customer>().HasIndex(c => c.Phone).IsUnique();
        modelBuilder.Entity<OtpCode>().HasIndex(o => o.Phone);
        modelBuilder.Entity<RefreshToken>().HasIndex(r => r.TokenHash);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Properties<DateOnly>()
            .HaveConversion<string>();

        configurationBuilder.Properties<TimeOnly>()
            .HaveConversion<string>();
    }
}