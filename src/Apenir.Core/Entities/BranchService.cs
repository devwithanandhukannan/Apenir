using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Apenir.Core.Entities;

[Table("branch_services")]
public class BranchService
{
    [Key]
    [Required]
    [StringLength(36)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(36)]
    public string BranchId { get; set; } = string.Empty;

    [Required]
    [StringLength(36)]
    public string ServiceId { get; set; } = string.Empty;

    public decimal? CustomPrice { get; set; }

    public decimal? CustomOriginalPrice { get; set; }

    public decimal? CustomCommissionPct { get; set; }

    [Required]
    public bool IsActive { get; set; } = true;

    // Navigation properties
    [ForeignKey(nameof(BranchId))]
    public virtual Branch? Branch { get; set; }

    [ForeignKey(nameof(ServiceId))]
    public virtual Service? Service { get; set; }
}
