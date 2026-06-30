using System;
using System.Collections.Generic;

namespace Apenir.Application.DTOs
{
    public class LoginRequest
    {
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class LoginResponse
    {
        public string AccessToken { get; set; } = string.Empty;
        
        [System.Text.Json.Serialization.JsonIgnore]
        public string RefreshToken { get; set; } = string.Empty;
        
        public int ExpiresIn { get; set; }
        public Guid AdminId { get; set; }
        public string Email { get; set; } = string.Empty;
    }

    public class RefreshTokenRequest
    {
        public string RefreshToken { get; set; } = string.Empty;
    }

    public class RefreshTokenResponse
    {
        public string AccessToken { get; set; } = string.Empty;
        
        [System.Text.Json.Serialization.JsonIgnore]
        public string RefreshToken { get; set; } = string.Empty;
    }

    public class CurrentAdminResponse
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public List<string> Roles { get; set; } = new();
        public List<string> Permissions { get; set; } = new();
        public DateTime? LastLoginAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class ChangePasswordRequest
    {
        public string CurrentPassword { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
    }

    public class ForgotPasswordRequest
    {
        public string Email { get; set; } = string.Empty;
    }

    public class ResetPasswordRequest
    {
        public string Email { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
    }

    public class TokenValidationResponse
    {
        public bool IsValid { get; set; }
        public Guid? AdminId { get; set; }
        public string? Email { get; set; }
        public List<string>? Roles { get; set; }
        public List<string>? Permissions { get; set; }
    }
}
