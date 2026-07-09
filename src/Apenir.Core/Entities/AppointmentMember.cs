using System;
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

    [StringLength(100)]
    public string? UniqueSampleId { get; set; }

    // Navigation properties
    [ForeignKey(nameof(AppointmentId))]
    public virtual Appointment? Appointment { get; set; }
}
