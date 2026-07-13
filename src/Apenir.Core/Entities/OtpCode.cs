namespace Apenir.Core.Entities;

public class OtpCode
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Phone { get; set; } = string.Empty;
    public string HashCode { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public int Attempts { get; set; } = 0;
}