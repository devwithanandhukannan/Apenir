using Microsoft.EntityFrameworkCore;
using Apenir.Core.Entities;

namespace Apenir.Core.Interfaces;

public interface IApplicationDbContext
{
    DbSet<User> Users { get; }
    DbSet<Customer> Customers { get; }
    DbSet<Branch> Branches { get; }
    DbSet<Service> Services { get; }
    DbSet<BranchService> BranchServices { get; }
    DbSet<BranchSlotConfiguration> BranchSlotConfigurations { get; }
    DbSet<AppointmentSlot> AppointmentSlots { get; }
    DbSet<Appointment> Appointments { get; }
    DbSet<AppointmentMember> AppointmentMembers { get; }
    DbSet<Report> Reports { get; }
    DbSet<Payment> Payments { get; }
    DbSet<PaymentBatch> PaymentBatches { get; }
    DbSet<Payroll> Payrolls { get; }
    DbSet<StaffOrderLog> StaffOrderLogs { get; }
    DbSet<OtpCode> OtpCodes { get; }
    DbSet<WhatsAppSession> WhatsAppSessions { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<BranchInvite> BranchInvites { get; }
    DbSet<StaffInvite> StaffInvites { get; }
    DbSet<PackageItem> PackageItems { get; }
    DbSet<BranchTravelCharge> BranchTravelCharges { get; }
    DbSet<Notification> Notifications { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
