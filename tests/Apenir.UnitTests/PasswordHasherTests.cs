using Xunit;
using FluentAssertions;
using Apenir.Infrastructure.Security;

namespace Apenir.UnitTests
{
    public class PasswordHasherTests
    {
        private readonly BCryptPasswordHasher _hasher;

        public PasswordHasherTests()
        {
            _hasher = new BCryptPasswordHasher();
        }

        [Fact]
        public void Hash_ShouldReturnHashedPassword()
        {
            // Arrange
            var password = "SuperSecretPassword123!";

            // Act
            var hash = _hasher.Hash(password);

            // Assert
            hash.Should().NotBeNullOrWhiteSpace();
            hash.Should().NotBe(password);
        }

        [Fact]
        public void Verify_ShouldReturnTrue_WhenPasswordMatches()
        {
            // Arrange
            var password = "SuperSecretPassword123!";
            var hash = _hasher.Hash(password);

            // Act
            var result = _hasher.Verify(password, hash);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public void Verify_ShouldReturnFalse_WhenPasswordDoesNotMatch()
        {
            // Arrange
            var password = "SuperSecretPassword123!";
            var wrongPassword = "WrongPassword123!";
            var hash = _hasher.Hash(password);

            // Act
            var result = _hasher.Verify(wrongPassword, hash);

            // Assert
            result.Should().BeFalse();
        }
    }
}
