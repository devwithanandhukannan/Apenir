using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Apenir.Core.Entities;

[Table("reports")]
public class Report
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
    public string MemberId { get; set; } = string.Empty;

    public string? FileUrl { get; set; }

    [StringLength(255)]
    public string? FileName { get; set; }

    public string? ResultData { get; set; }

    [Required]
    [StringLength(36)]
    public string UploadedBy { get; set; } = string.Empty;

    [Required]
    public bool WhatsappSent { get; set; } = false;

    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [ForeignKey(nameof(AppointmentId))]
    public virtual Appointment? Appointment { get; set; }

    [ForeignKey(nameof(MemberId))]
    public virtual AppointmentMember? Member { get; set; }

    [ForeignKey(nameof(UploadedBy))]
    public virtual User? Uploader { get; set; }
}
