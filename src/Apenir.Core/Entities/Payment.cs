using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Apenir.Core.Enums;

namespace Apenir.Core.Entities;

[Table("payments")]
public class Payment
{
    [Key]
    [Required]
    [StringLength(36)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(36)]
    public string AppointmentId { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string RazorpayOrderId { get; set; } = string.Empty;

    [StringLength(100)]
    public string? RazorpayPaymentId { get; set; }

    [Required]
    public PaymentStatus Status { get; set; } = PaymentStatus.Created;

    public PaymentMethod? PaymentMethod { get; set; }

    public DateTime? PaidAt { get; set; }

    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [ForeignKey(nameof(AppointmentId))]
    public virtual Appointment? Appointment { get; set; }
}
