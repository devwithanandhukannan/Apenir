using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Apenir.Core.Entities;

[Table("packages")]
public class Package
{
    [Key]
    [Required]
    [StringLength(36)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Required]
    public decimal BasePrice { get; set; }

    [Required]
    public decimal PlatformCommissionPct { get; set; } = 15.00m;

    [Required]
    public bool IsActive { get; set; } = true;

    [StringLength(36)]
    public string? CreatedByBranchId { get; set; }

    [Required]
    public List<string> ServiceIds { get; set; } = new();

    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
