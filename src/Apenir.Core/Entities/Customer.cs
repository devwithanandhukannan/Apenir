namespace Apenir.Core.Entities;

public class Customer
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = "WhatsApp Customer";
    public string Phone { get; set; } = string.Empty;
    public string? Gender { get; set; }
    public string? Dob { get; set; }
    public string? Address { get; set; }
    public string? District { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}