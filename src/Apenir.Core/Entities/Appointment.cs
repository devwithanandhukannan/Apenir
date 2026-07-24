using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Apenir.Core.Enums;

namespace Apenir.Core.Entities;

[Table("appointments")]
public class Appointment
{
    [Key]
    [Required]
    [StringLength(36)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(20)]
    public string AppointmentNumber { get; set; } = string.Empty;

    [Required]
    [StringLength(36)]
    public string CustomerUserId { get; set; } = string.Empty;

    [Required]
    [StringLength(36)]
    public string BranchId { get; set; } = string.Empty;

    [Required]
    [StringLength(36)]
    public string AppointmentSlotId { get; set; } = string.Empty;

    [Required]
    public decimal LocationLatitude { get; set; }

    [Required]
    public decimal LocationLongitude { get; set; }

    [Required]
    public string LocationAddress { get; set; } = string.Empty;

    [Required]
    [StringLength(8)]
    public string Passcode { get; set; } = string.Empty;

    [Required]
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Pending;

    [Required]
    public decimal TotalAmount { get; set; }

    [Required]
    public decimal PlatformCommission { get; set; }

    [Required]
    public decimal LabPayout { get; set; }

    [StringLength(36)]
    public string? AssignedStaffId { get; set; }

    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    [Required]
    public int MemberCount { get; set; } = 1;

    [StringLength(100)]
    public string? Landmark { get; set; }

    [StringLength(100)]
    public string? BuildingDetails { get; set; }

    [StringLength(20)]
    public string? Floor { get; set; }

    [StringLength(500)]
    public string? ReportPdfPath { get; set; }

    // ─── Phase 3: Multi-Lab Support ───────────────────────────────────────────

    /// <summary>
    /// True when this booking spans multiple labs (one lab can't handle all items).
    /// The parent appointment holds the full member list; child appointments each target one lab.
    /// </summary>
    public bool? IsMultiLab { get; set; } = false;

    /// <summary>
    /// For child sub-appointments created in a multi-lab booking.
    /// Null on the parent / single-lab appointments.
    /// </summary>
    [StringLength(36)]
    public string? ParentAppointmentId { get; set; }

    /// <summary>
    /// Service/Package IDs assigned to this lab in a multi-lab split.
    /// For single-lab bookings this mirrors the full cart.
    /// </summary>
    public List<string>? ItemIds { get; set; } = new();

    // ─── Navigation ──────────────────────────────────────────────────────────

    [ForeignKey(nameof(CustomerUserId))]
    public virtual User? CustomerUser { get; set; }

    [ForeignKey(nameof(BranchId))]
    public virtual Branch? Branch { get; set; }

    [ForeignKey(nameof(AppointmentSlotId))]
    public virtual AppointmentSlot? AppointmentSlot { get; set; }

    [ForeignKey(nameof(AssignedStaffId))]
    public virtual User? AssignedStaff { get; set; }
}

