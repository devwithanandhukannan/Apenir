using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Apenir.Core.Entities;

[Table("appointment_slots")]
public class AppointmentSlot
{
    [Key]
    [Required]
    [StringLength(36)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(36)]
    public string BranchId { get; set; } = string.Empty;

    [Required]
    public DateOnly SlotDate { get; set; }

    [Required]
    public TimeOnly StartTime { get; set; }

    [Required]
    public TimeOnly EndTime { get; set; }

    [Required]
    public int MaxCapacity { get; set; } = 1;

    [Required]
    public int BookedCount { get; set; } = 0;

    [Required]
    public bool IsAvailable { get; set; } = true;

    // Navigation properties
    [ForeignKey(nameof(BranchId))]
    public virtual Branch? Branch { get; set; }
}
