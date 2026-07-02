using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Apenir.Core.Enums;

namespace Apenir.Core.Entities;

[Table("users")]
public class User
{
    [Key]
    [Required]
    [StringLength(36)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [StringLength(120)]
    public string? Name { get; set; } = string.Empty;

    [StringLength(255)]
    [EmailAddress]
    public string? Email { get; set; }

    [StringLength(20)]
    public string? Phone { get; set; }

    [Required]
    public UserRole Role { get; set; } = UserRole.Customer;

    [StringLength(255)]
    public string? PasswordHash { get; set; }

    public bool? IsActive { get; set; } = true;

    public bool IsDeleted { get; set; } = false;

    [StringLength(50)]
    public string? Status { get; set; }

    public List<string> Permissions { get; set; } = new();

    public DateTime? LastLoginAt { get; set; }

    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}
