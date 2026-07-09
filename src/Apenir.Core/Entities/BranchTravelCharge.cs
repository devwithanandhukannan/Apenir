using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Apenir.Core.Entities
{
    [Table("branch_travel_charges")]
    public class BranchTravelCharge
    {
        [Key]
        [Required]
        [StringLength(36)]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [Required]
        [StringLength(36)]
        public string BranchId { get; set; } = string.Empty;

        [Required]
        public double MinDistanceKm { get; set; }

        [Required]
        public double MaxDistanceKm { get; set; }

        [Required]
        public decimal Cost { get; set; }

        [ForeignKey(nameof(BranchId))]
        public virtual Branch? Branch { get; set; }
    }
}
