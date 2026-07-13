using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Apenir.Core.Enums;

namespace Apenir.Core.Entities;

[Table("customers")]
public class Customer
{
    [Key]
    [Required]
    [StringLength(36)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(36)]
    public string UserId { get; set; } = string.Empty;

    public DateOnly? DateOfBirth { get; set; }

    [Column("gender")]
    public Gender? GenderEnum { get; set; }

    public string? Address { get; set; }

    [StringLength(60)]
    public string? District { get; set; }

    // Navigation property
    [ForeignKey(nameof(UserId))]
    public virtual User? User { get; set; }

    // --- Compatibility properties for non-Core projects ---

    [NotMapped]
    public string Name
    {
        get => User?.Name ?? string.Empty;
        set
        {
            if (User != null) User.Name = value;
        }
    }

    [NotMapped]
    public string Phone
    {
        get => User?.Phone ?? string.Empty;
        set
        {
            if (User != null) User.Phone = value;
        }
    }

    [NotMapped]
    public string? Gender
    {
        get => GenderEnum?.ToString();
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                GenderEnum = null;
            }
            else if (Enum.TryParse<Gender>(value, true, out var parsed))
            {
                GenderEnum = parsed;
            }
        }
    }

    [NotMapped]
    public string? Dob
    {
        get => DateOfBirth?.ToString("yyyy-MM-dd");
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                DateOfBirth = null;
            }
            else if (DateOnly.TryParse(value, out var parsed))
            {
                DateOfBirth = parsed;
            }
        }
    }

    [NotMapped]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}