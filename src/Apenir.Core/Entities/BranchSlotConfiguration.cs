using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Apenir.Core.Enums;

namespace Apenir.Core.Entities;

[Table("branch_slot_configurations")]
public class BranchSlotConfiguration
{
    [Key]
    [Required]
    [StringLength(36)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(36)]
    public string BranchId { get; set; } = string.Empty;

    [Required]
    public DayText DayText { get; set; }

    [Required]
    public TimeOnly StartTime { get; set; }

    [Required]
    public TimeOnly EndTime { get; set; }

    [Required]
    public int MaxCapacity { get; set; } = 1;

    [Required]
    public bool IsLeave { get; set; } = false;

    // Navigation properties
    [ForeignKey(nameof(BranchId))]
    public virtual Branch? Branch { get; set; }
}
