using System;
using System.Collections.Generic;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Xunit;
using FluentAssertions;
using Apenir.Application.Common.Models;
using Apenir.Core.Entities;
using Apenir.Infrastructure.Security;

namespace Apenir.UnitTests
{
    public class JwtTokenServiceTests
    {
        private readonly JwtTokenService _jwtService;
        private readonly JwtSettings _settings;

        public JwtTokenServiceTests()
        {
            _settings = new JwtSettings
            {
                Secret = "TestSecretKeyMustBeVeryLongToMeetHmacRequirements1234567890",
                Issuer = "TestIssuer",
                Audience = "TestAudience",
                AccessTokenExpiryMinutes = 5,
                RefreshTokenExpiryDays = 1,
                ClockSkewSeconds = 0
            };

            var options = Options.Create(_settings);
            _jwtService = new JwtTokenService(options);
        }

        [Fact]
        public void GenerateAccessToken_ShouldReturnToken_WithCorrectClaims()
        {
            // Arrange
            var admin = new Admin
            {
                Id = Guid.NewGuid(),
                Email = "test@apenir.com",
                FullName = "Test Admin",
                Roles = new List<string> { "Admin" },
                Permissions = new List<string> { "read:users" }
            };

            // Act
            var token = _jwtService.GenerateAccessToken(admin);

            // Assert
            token.Should().NotBeNullOrWhiteSpace();

            var principal = _jwtService.GetPrincipalFromExpiredToken(token);
            principal.Should().NotBeNull();
            principal!.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be(admin.Id.ToString());
            principal.FindFirst(ClaimTypes.Name)!.Value.Should().Be(admin.Email);
            principal.FindFirst(ClaimTypes.Email)!.Value.Should().Be(admin.Email);
            principal.FindFirst(ClaimTypes.Role)!.Value.Should().Be("Admin");
            principal.FindFirst("permission")!.Value.Should().Be("read:users");
        }

        [Fact]
        public void GenerateRefreshToken_ShouldReturnSecureRandomString()
        {
            // Act
            var token1 = _jwtService.GenerateRefreshToken();
            var token2 = _jwtService.GenerateRefreshToken();

            // Assert
            token1.Should().NotBeNullOrWhiteSpace();
            token1.Length.Should().BeGreaterThan(64); // Base64 encoding of 64 bytes is 88 chars
            token1.Should().NotBe(token2);
        }

        [Fact]
        public void ValidateAccessToken_ShouldReturnTrue_ForValidToken()
        {
            // Arrange
            var admin = new Admin
            {
                Id = Guid.NewGuid(),
                Email = "test@apenir.com",
                Roles = new List<string> { "Admin" }
            };
            var token = _jwtService.GenerateAccessToken(admin);

            // Act
            var isValid = _jwtService.ValidateAccessToken(token);

            // Assert
            isValid.Should().BeTrue();
        }
    }
}
