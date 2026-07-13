using Xunit;
using FluentAssertions;
using Apenir.Application.DTOs;
using Apenir.Application.Features.AdminAuth.Validators;

namespace Apenir.UnitTests
{
    public class ValidatorTests
    {
        private readonly ChangePasswordRequestValidator _changePasswordValidator;

        public ValidatorTests()
        {
            _changePasswordValidator = new ChangePasswordRequestValidator();
        }

        [Fact]
        public void Validate_ShouldBeValid_WhenChangePasswordRequestMeetsAllCriteria()
        {
            // Arrange
            var request = new ChangePasswordRequest
            {
                CurrentPassword = "OldPassword123!",
                NewPassword = "NewPassword123$"
            };

            // Act
            var result = _changePasswordValidator.Validate(request);

            // Assert
            result.IsValid.Should().BeTrue();
        }

        [Theory]
        [InlineData("short")] // too short
        [InlineData("nouppercase123!")] // no uppercase
        [InlineData("NOLOWERCASE123!")] // no lowercase
        [InlineData("NoNumberHere!")] // no number
        [InlineData("NoSpecialChar123")] // no special character
        public void Validate_ShouldBeInvalid_WhenNewPasswordDoesNotMeetStrengthCriteria(string weakPassword)
        {
            // Arrange
            var request = new ChangePasswordRequest
            {
                CurrentPassword = "OldPassword123!",
                NewPassword = weakPassword
            };

            // Act
            var result = _changePasswordValidator.Validate(request);

            // Assert
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(e => e.PropertyName == nameof(ChangePasswordRequest.NewPassword));
        }

        [Fact]
        public void Validate_ShouldBeInvalid_WhenNewPasswordEqualsCurrentPassword()
        {
            // Arrange
            var request = new ChangePasswordRequest
            {
                CurrentPassword = "Password123!",
                NewPassword = "Password123!"
            };

            // Act
            var result = _changePasswordValidator.Validate(request);

            // Assert
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(e => e.ErrorMessage.Contains("cannot be the same"));
        }
    }
}
