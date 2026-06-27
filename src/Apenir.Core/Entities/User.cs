using Apenir.Core.Enums;

namespace Apenir.Core.Entities;

public class User
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Phone { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Customer;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
