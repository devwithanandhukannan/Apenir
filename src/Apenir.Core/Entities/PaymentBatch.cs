using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Apenir.Core.Enums;

namespace Apenir.Core.Entities;

[Table("payment_batches")]
public class PaymentBatch
{
    [Key]
    [Required]
    [StringLength(36)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(36)]
    public string BranchId { get; set; } = string.Empty;

    /// <summary>
    /// Array of Payment IDs included in this batch.
    /// </summary>
    public List<string> PaymentIds { get; set; } = new();

    /// <summary>
    /// Array of Appointment IDs associated with the payments in this batch.
    /// Derived from Payment.AppointmentId for each payment.
    /// </summary>
    public List<string> AppointmentIds { get; set; } = new();

    /// <summary>
    /// Number of payments in the batch.
    /// </summary>
    [Required]
    public int PaymentCount { get; set; }

    /// <summary>
    /// Sum of TotalAmount from associated appointments.
    /// </summary>
    [Required]
    public decimal TotalGrossAmount { get; set; }

    /// <summary>
    /// Sum of PlatformCommission from associated appointments.
    /// </summary>
    [Required]
    public decimal TotalPlatformCommission { get; set; }

    /// <summary>
    /// Sum of LabPayout from associated appointments — the actual amount owed to the lab.
    /// </summary>
    [Required]
    public decimal TotalNetPayout { get; set; }

    [Required]
    public PaymentBatchStatus Status { get; set; } = PaymentBatchStatus.Initiated;

    [StringLength(36)]
    public string? CreatedBy { get; set; }

    [StringLength(36)]
    public string? ConfirmedByLabUser { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }

    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
