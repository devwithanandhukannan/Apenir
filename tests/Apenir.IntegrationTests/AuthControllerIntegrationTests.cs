using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Xunit;
using FluentAssertions;
using Apenir.Application.Common.Models;
using Apenir.Application.DTOs;
using Apenir.Core.Entities;

namespace Apenir.IntegrationTests
{
    public class AuthControllerIntegrationTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly CustomWebApplicationFactory _factory;
        private readonly HttpClient _client;

        public AuthControllerIntegrationTests(CustomWebApplicationFactory factory)
        {
            _factory = factory;
            _client = _factory.CreateClient();
        }

        [Fact]
        public async Task Login_ShouldReturnOk_WhenCredentialsAreValid()
        {
            // Arrange
            var email = "integration@apenir.com";
            var password = "AdminPassword123!";
            
            // PasswordHash of "AdminPassword123!" using BCrypt work factor 12 (mock password hasher)
            var passwordHasher = new Apenir.Infrastructure.Security.BCryptPasswordHasher();
            var hashedPassword = passwordHasher.Hash(password);

            var admin = new Admin
            {
                Id = Guid.NewGuid(),
                Email = email,
                PasswordHash = hashedPassword,
                IsActive = true,
                IsDeleted = false
            };

            _factory.AdminRepoMock.Reset();
            _factory.RefreshTokenRepoMock.Reset();

            _factory.AdminRepoMock
                .Setup(repo => repo.GetByEmailAsync(email, It.IsAny<CancellationToken>()))
                .ReturnsAsync(admin);

            _factory.AdminRepoMock
                .Setup(repo => repo.UpdateAsync(admin, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            _factory.RefreshTokenRepoMock
                .Setup(repo => repo.AddAsync(It.IsAny<RefreshToken>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var loginRequest = new LoginRequest
            {
                Email = email,
                Password = password
            };

            // Act
            var response = await _client.PostAsJsonAsync("/api/adminauth/login", loginRequest);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            
            var result = await response.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();
            result.Should().NotBeNull();
            result!.Success.Should().BeTrue();
            result.Data.Should().NotBeNull();
            result.Data!.AccessToken.Should().NotBeNullOrWhiteSpace();
            result.Data!.RefreshToken.Should().BeEmpty();
            response.Headers.Contains("Set-Cookie").Should().BeTrue();
        }

        [Fact]
        public async Task Login_ShouldReturnUnauthorized_WhenCredentialsAreInvalid()
        {
            // Arrange
            var email = "invalidadmin@apenir.com";
            
            _factory.AdminRepoMock.Reset();
            _factory.AdminRepoMock
                .Setup(repo => repo.GetByEmailAsync(email, It.IsAny<CancellationToken>()))
                .ReturnsAsync((Admin?)null);

            var loginRequest = new LoginRequest
            {
                Email = email,
                Password = "wrongPassword"
            };

            // Act
            var response = await _client.PostAsJsonAsync("/api/adminauth/login", loginRequest);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            
            var result = await response.Content.ReadFromJsonAsync<ApiResponse>();
            result.Should().NotBeNull();
            result!.Success.Should().BeFalse();
            result.Errors.Should().NotBeEmpty();
        }

        [Fact]
        public async Task Me_ShouldReturnUnauthorized_WhenNoTokenProvided()
        {
            // Act
            var response = await _client.GetAsync("/api/adminauth/me");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }
}
