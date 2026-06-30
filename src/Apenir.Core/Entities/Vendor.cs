using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Apenir.Core.Entities
{
	[Table("Vendors")]
	public class Vendor
	{
		[Key]
		[DatabaseGenerated(DatabaseGeneratedOption.Identity)]
		public Guid Id { get; private set; }

		[Required]
		[StringLength(255)]
		public string VendorName { get; private set; } = string.Empty;

		[Required]
		[StringLength(20)]
		public string ContactNumber1 { get; private set; } = string.Empty;

		[StringLength(20)]
		public string ContactNumber2 { get; private set; } = string.Empty;

		[Required]
		[StringLength(255)]
		[EmailAddress]
		public string Email { get; private set; } = string.Empty;

		[Required]
		public string PasswordHash { get; private set; } = string.Empty;

		[DatabaseGenerated(DatabaseGeneratedOption.Computed)]
		public DateTime CreatedAt { get; private set; }

		[DatabaseGenerated(DatabaseGeneratedOption.Computed)]
		public DateTime? UpdatedAt { get; private set; }

		// Private constructor for EF Core
		private Vendor() { }

		public Vendor(string vendorName, string contactNumber1, string contactNumber2, string email, string passwordHash)
		{
			if (string.IsNullOrWhiteSpace(vendorName))
				throw new ArgumentException("Vendor name cannot be empty.", nameof(vendorName));
			if (string.IsNullOrWhiteSpace(email))
				throw new ArgumentException("Email cannot be empty.", nameof(email));
			if (string.IsNullOrWhiteSpace(passwordHash))
				throw new ArgumentException("Password hash cannot be empty.", nameof(passwordHash));
			if (string.IsNullOrWhiteSpace(contactNumber1))
				throw new ArgumentException("Contact number 1 cannot be empty.", nameof(contactNumber1));

			Id = Guid.NewGuid();
			VendorName = vendorName;
			ContactNumber1 = contactNumber1;
			ContactNumber2 = contactNumber2;
			Email = email;
			PasswordHash = passwordHash;
		}
	}
}