using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Apenir.Core.Entities;

[Table("branches")]
public class Branch
{
    [Key]
    [Required]
    [StringLength(36)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(36)]
    public string LabUserId { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(60)]
    public string District { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string City { get; set; } = string.Empty;

    [Required]
    [StringLength(10)]
    public string Pincode { get; set; } = string.Empty;

    [Required]
    public decimal Latitude { get; set; }

    [Required]
    public decimal Longitude { get; set; }

    [Required]
    [StringLength(20)]
    public string Phone { get; set; } = string.Empty;

    [Required]
    public bool IsActive { get; set; } = true;

    [StringLength(50)]
    public string? Status { get; set; }

    [StringLength(6)]
    public string? LabId { get; set; }

    public double ServiceRangeKm { get; set; } = 10.0;

    [StringLength(20)]
    public string? NotificationPhone { get; set; }

    [Required]
    [StringLength(36)]
    public string CreatedBy { get; set; } = string.Empty;

    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [ForeignKey(nameof(LabUserId))]
    public virtual User? LabUser { get; set; }

    [ForeignKey(nameof(CreatedBy))]
    public virtual User? Creator { get; set; }
}
