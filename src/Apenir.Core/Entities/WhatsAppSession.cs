using Apenir.Core.Enums;

namespace Apenir.Core.Entities;

public class WhatsAppSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Phone { get; set; } = string.Empty;
    public WhatsAppState CurrentState { get; set; } = WhatsAppState.Start;
    public string? SelectedTestId { get; set; }
    public string? SelectedCity { get; set; }
    public string? SelectedLabId { get; set; }
    public string? SelectedLabName { get; set; }
    public string? SelectedSlot { get; set; }
    public int MemberCount { get; set; }
    public bool LocationShared { get; set; }
    public string? Passcode { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Landmark { get; set; }
    public string? BuildingDetails { get; set; }
    public string? Floor { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}