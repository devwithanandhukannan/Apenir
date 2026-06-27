using System;

namespace Apenir.Core.Entities;

public class RefreshToken
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public bool IsRevoked { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}
