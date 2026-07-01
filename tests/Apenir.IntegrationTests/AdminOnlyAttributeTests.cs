using System.Collections.Generic;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Moq;
using Xunit;
using FluentAssertions;
using Apenir.API.Filters;

namespace Apenir.IntegrationTests;

public class AdminOnlyAttributeTests
{
    private AuthorizationFilterContext CreateContext(ClaimsPrincipal principal)
    {
        var httpContextMock = new Mock<HttpContext>();
        httpContextMock.Setup(c => c.User).Returns(principal);

        var actionContext = new ActionContext(
            httpContextMock.Object,
            new RouteData(),
            new ActionDescriptor()
        );

        return new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());
    }

    [Fact]
    public void OnAuthorization_ShouldReturnForbidden_WhenUserIsNotAuthenticated()
    {
        // Arrange
        var identity = new ClaimsIdentity(); // IsAuthenticated = false
        var principal = new ClaimsPrincipal(identity);
        var context = CreateContext(principal);
        var filter = new AdminOnlyAttribute();

        // Act
        filter.OnAuthorization(context);

        // Assert
        context.Result.Should().NotBeNull();
        context.Result.Should().BeOfType<JsonResult>();
        var jsonResult = (JsonResult)context.Result!;
        jsonResult.StatusCode.Should().Be(403);
    }

    [Fact]
    public void OnAuthorization_ShouldReturnForbidden_WhenUserDoesNotHaveAdminRole()
    {
        // Arrange
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Role, "Customer")
        }, "TestAuth"); // IsAuthenticated = true
        var principal = new ClaimsPrincipal(identity);
        var context = CreateContext(principal);
        var filter = new AdminOnlyAttribute();

        // Act
        filter.OnAuthorization(context);

        // Assert
        context.Result.Should().NotBeNull();
        var jsonResult = (JsonResult)context.Result!;
        jsonResult.StatusCode.Should().Be(403);
    }

    [Fact]
    public void OnAuthorization_ShouldPass_WhenUserIsAdmin()
    {
        // Arrange
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Role, "Admin")
        }, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        var context = CreateContext(principal);
        var filter = new AdminOnlyAttribute();

        // Act
        filter.OnAuthorization(context);

        // Assert
        context.Result.Should().BeNull();
    }

    [Fact]
    public void OnAuthorization_ShouldPass_WhenUserIsSuperAdmin()
    {
        // Arrange
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Role, "SuperAdmin")
        }, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        var context = CreateContext(principal);
        var filter = new AdminOnlyAttribute();

        // Act
        filter.OnAuthorization(context);

        // Assert
        context.Result.Should().BeNull();
    }
}
