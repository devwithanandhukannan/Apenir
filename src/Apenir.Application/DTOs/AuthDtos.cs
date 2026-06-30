using System.ComponentModel.DataAnnotations;

namespace Apenir.API.DTOs;

public record SendOtpRequest(
    [Required][RegularExpression(@"^\+?[1-9]\d{1,14}$", ErrorMessage = "Invalid phone format")] string Phone
);

public record VerifyOtpRequest(
    [Required] string Phone, 
    [Required][StringLength(6, MinimumLength = 6)] string Otp
);

public record AuthResponse(
    string AccessToken, 
    string Role, 
    string Phone,
    [property: System.Text.Json.Serialization.JsonIgnore] string RefreshToken = ""
);