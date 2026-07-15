using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Apenir.Core.Enums;

namespace Apenir.Core.Entities;

[Table("appointment_members")]
public class AppointmentMember
{
    [Key]
    [Required]
    [StringLength(36)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(36)]
    public string AppointmentId { get; set; } = string.Empty;

    [Required]
    [StringLength(120)]
    public string MemberName { get; set; } = string.Empty;

    [Required]
    public int Age { get; set; }

    [Required]
    public Gender Gender { get; set; }

    [Required]
    [StringLength(60)]
    public string Relationship { get; set; } = string.Empty;

    public string? AdditionalNotes { get; set; }

    [StringLength(50)]
    public string? UniqueNumber { get; set; }

    [StringLength(100)]
    public string? TestName { get; set; }

    /// <summary>
    /// Phase 2: Per-member selected service/package IDs. Stored as array in MongoDB.
    /// Enables per-member service assignment (e.g., member 1 needs Service A+B, member 2 needs Service C).
    /// </summary>
    public List<string> ServiceItemIds { get; set; } = new();

    /// <summary>
    /// Phase 2: The final amount charged for this member (after member discount logic).
    /// </summary>
    public decimal Amount { get; set; } = 0m;

    /// <summary>
    /// Phase 3: For multi-lab splits, which sub-appointment (child) this member belongs to.
    /// Null for single-lab appointments.
    /// </summary>
    [StringLength(36)]
    public string? SubAppointmentId { get; set; }

    // Navigation properties
    [ForeignKey(nameof(AppointmentId))]
    public virtual Appointment? Appointment { get; set; }
}
