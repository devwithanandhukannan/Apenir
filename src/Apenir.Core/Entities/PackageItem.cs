using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Apenir.Core.Entities
{
    [Table("package_items")]
    public class PackageItem
    {
        [Key]
        [Required]
        [StringLength(36)]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [Required]
        [StringLength(36)]
        public string PackageId { get; set; } = string.Empty; // FK to Service (IsPackage = true)

        [Required]
        [StringLength(36)]
        public string SubtestId { get; set; } = string.Empty; // FK to Service (IsPackage = false)

        [ForeignKey(nameof(PackageId))]
        public virtual Service? Package { get; set; }

        [ForeignKey(nameof(SubtestId))]
        public virtual Service? Subtest { get; set; }
    }
}
