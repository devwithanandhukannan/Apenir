using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Apenir.Core.Enums;

namespace Apenir.Core.Entities;

[Table("payrolls")]
public class Payroll
{
    [Key]
    [Required]
    [StringLength(36)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(36)]
    public string BranchId { get; set; } = string.Empty;

    [Required]
    [StringLength(20)]
    public string PeriodType { get; set; } = "Weekly";

    [Required]
    public DateOnly PeriodStart { get; set; }

    [Required]
    public DateOnly PeriodEnd { get; set; }

    [Required]
    public decimal GrossAmount { get; set; }

    [Required]
    public decimal PlatformCommission { get; set; }

    [Required]
    public decimal NetPayout { get; set; }

    [Required]
    public PayrollStatus Status { get; set; } = PayrollStatus.Pending;

    public DateTime? SettledAt { get; set; }

    [StringLength(100)]
    public string? RazorpayTransferId { get; set; }

    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [ForeignKey(nameof(BranchId))]
    public virtual Branch? Branch { get; set; }
}
