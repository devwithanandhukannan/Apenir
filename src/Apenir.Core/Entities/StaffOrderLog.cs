using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Apenir.Core.Enums;

namespace Apenir.Core.Entities;

[Table("staff_order_logs")]
public class StaffOrderLog
{
    [Key]
    [Required]
    [StringLength(36)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(36)]
    public string AppointmentId { get; set; } = string.Empty;

    [Required]
    [StringLength(36)]
    public string StaffId { get; set; } = string.Empty;

    [Required]
    public StaffOrderStatus Status { get; set; }

    public string? Note { get; set; }

    public string? ContextData { get; set; }

    [Required]
    public DateTime LoggedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [ForeignKey(nameof(AppointmentId))]
    public virtual Appointment? Appointment { get; set; }

    [ForeignKey(nameof(StaffId))]
    public virtual User? Staff { get; set; }
}
