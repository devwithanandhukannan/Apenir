using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using FluentAssertions;
using Apenir.Application.Common.Interfaces;
using Apenir.Application.Common.Models;
using Apenir.Application.DTOs;
using Apenir.Application.Features.AdminAuth.Commands;
using Apenir.Core.Entities;
using Apenir.Core.Exceptions;

namespace Apenir.UnitTests
{
    public class LoginHandlerTests
    {
        private readonly Mock<IAdminRepository> _adminRepoMock;
        private readonly Mock<IRefreshTokenRepository> _refreshTokenRepoMock;
        private readonly Mock<IPasswordHasher> _hasherMock;
        private readonly Mock<IJwtTokenService> _jwtMock;
        private readonly Mock<ICurrentUserService> _currentUserMock;
        private readonly IOptions<JwtSettings> _jwtSettingsOptions;

        public LoginHandlerTests()
        {
            _adminRepoMock = new Mock<IAdminRepository>();
            _refreshTokenRepoMock = new Mock<IRefreshTokenRepository>();
            _hasherMock = new Mock<IPasswordHasher>();
            _jwtMock = new Mock<IJwtTokenService>();
            _currentUserMock = new Mock<ICurrentUserService>();

            var settings = new JwtSettings
            {
                Secret = "TestSecretKeyMustBeVeryLongToMeetHmacRequirements1234567890",
                Issuer = "TestIssuer",
                Audience = "TestAudience",
                AccessTokenExpiryMinutes = 15,
                RefreshTokenExpiryDays = 7,
                ClockSkewSeconds = 0
            };
            _jwtSettingsOptions = Options.Create(settings);
        }

        [Fact]
        public async Task Handle_ShouldReturnTokens_WhenCredentialsAreValid()
        {
            // Arrange
            var password = "ValidPassword123!";
            var hashedPassword = "hashed_password";
            var username = "adminuser";
            var email = "admin@apenir.com";

            var admin = new Admin
            {
                Id = Guid.NewGuid(),
                Username = username,
                Email = email,
                PasswordHash = hashedPassword,
                IsActive = true,
                IsDeleted = false
            };

            _adminRepoMock.Setup(repo => repo.GetByUsernameAsync(username, It.IsAny<CancellationToken>()))
                .ReturnsAsync(admin);

            _hasherMock.Setup(h => h.Verify(password, hashedPassword))
                .Returns(true);

            _jwtMock.Setup(j => j.GenerateAccessToken(admin))
                .Returns("access_token");

            _jwtMock.Setup(j => j.GenerateRefreshToken())
                .Returns("refresh_token");

            _currentUserMock.Setup(c => c.IpAddress).Returns("127.0.0.1");
            _currentUserMock.Setup(c => c.UserAgent).Returns("test-agent");

            var handler = new AdminLoginCommandHandler(
                _adminRepoMock.Object,
                _refreshTokenRepoMock.Object,
                _hasherMock.Object,
                _jwtMock.Object,
                _currentUserMock.Object,
                _jwtSettingsOptions
            );

            var request = new LoginRequest
            {
                UsernameOrEmail = username,
                Password = password
            };

            // Act
            var result = await handler.Handle(new AdminLoginCommand(request), CancellationToken.None);

            // Assert
            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
            result.Data.Should().NotBeNull();
            result.Data!.AccessToken.Should().Be("access_token");
            result.Data!.RefreshToken.Should().Be("refresh_token");
            result.Data!.Username.Should().Be(username);
            result.Data!.Email.Should().Be(email);

            _refreshTokenRepoMock.Verify(repo => repo.AddAsync(It.IsAny<RefreshToken>(), It.IsAny<CancellationToken>()), Times.Once);
            _adminRepoMock.Verify(repo => repo.UpdateAsync(admin, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Handle_ShouldThrowInvalidCredentialsException_WhenAdminNotFound()
        {
            // Arrange
            _adminRepoMock.Setup(repo => repo.GetByUsernameAsync("nonexistent", It.IsAny<CancellationToken>()))
                .ReturnsAsync((Admin?)null);

            var handler = new AdminLoginCommandHandler(
                _adminRepoMock.Object,
                _refreshTokenRepoMock.Object,
                _hasherMock.Object,
                _jwtMock.Object,
                _currentUserMock.Object,
                _jwtSettingsOptions
            );

            var request = new LoginRequest
            {
                UsernameOrEmail = "nonexistent",
                Password = "anyPassword"
            };

            // Act
            var act = () => handler.Handle(new AdminLoginCommand(request), CancellationToken.None);

            // Assert
            await act.Should().ThrowAsync<InvalidCredentialsException>();
        }

        [Fact]
        public async Task Handle_ShouldThrowAccountDisabledException_WhenAdminIsInactive()
        {
            // Arrange
            var password = "ValidPassword123!";
            var hashedPassword = "hashed_password";
            var username = "adminuser";

            var admin = new Admin
            {
                Id = Guid.NewGuid(),
                Username = username,
                Email = "admin@apenir.com",
                PasswordHash = hashedPassword,
                IsActive = false,
                IsDeleted = false
            };

            _adminRepoMock.Setup(repo => repo.GetByUsernameAsync(username, It.IsAny<CancellationToken>()))
                .ReturnsAsync(admin);

            _hasherMock.Setup(h => h.Verify(password, hashedPassword))
                .Returns(true);

            var handler = new AdminLoginCommandHandler(
                _adminRepoMock.Object,
                _refreshTokenRepoMock.Object,
                _hasherMock.Object,
                _jwtMock.Object,
                _currentUserMock.Object,
                _jwtSettingsOptions
            );

            var request = new LoginRequest
            {
                UsernameOrEmail = username,
                Password = password
            };

            // Act
            var act = () => handler.Handle(new AdminLoginCommand(request), CancellationToken.None);

            // Assert
            await act.Should().ThrowAsync<AccountDisabledException>();
        }
    }
}
