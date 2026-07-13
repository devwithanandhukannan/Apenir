using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Apenir.Application.Common.Models;
using Apenir.Application.DTOs;
using Apenir.Application.Features.AdminAuth.Commands;
using Apenir.Application.Features.AdminAuth.Queries;
using Apenir.Application.Features.Auth.Commands;
using Apenir.API.Helpers;

namespace Apenir.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AdminAuthController : ControllerBase
    {
        private readonly IMediator _mediator;

        public AdminAuthController(IMediator mediator)
        {
            _mediator = mediator;
        }

        [HttpPost("login")]
        [EndpointSummary("Administrator Login")]
        [EndpointDescription("Authenticates an administrator with credentials and returns access and refresh tokens.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<LoginResponse>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ApiResponse))]
        public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
        {
            var result = await _mediator.Send(new AdminLoginCommand(request), cancellationToken);
            if (result.Success && result.Data != null)
            {
                CookieHelper.SetRefreshTokenCookie(HttpContext, result.Data.RefreshToken, "/api/auth/refresh");
                result.Data.RefreshToken = string.Empty;
            }
            return Ok(result);
        }

        [Authorize]
        [HttpPost("change-password")]
        [EndpointSummary("Change Password")]
        [EndpointDescription("Allows an authenticated administrator to update their current password.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ApiResponse))]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
        {
            var result = await _mediator.Send(new ChangePasswordCommand(request), cancellationToken);
            return Ok(result);
        }

        [HttpPost("forgot-password")]
        [EndpointSummary("Forgot Password Request")]
        [EndpointDescription("Triggers a password reset request flow for the provided administrator email.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken cancellationToken)
        {
            var config = HttpContext.RequestServices.GetService(typeof(Microsoft.Extensions.Configuration.IConfiguration)) as Microsoft.Extensions.Configuration.IConfiguration;
            var frontendUrl = config?["FrontendUrl"] ?? "https://admin.anandhu-kannan.in";
            var result = await _mediator.Send(new ForgotPasswordCommand(request, frontendUrl), cancellationToken);
            return Ok(result);
        }

        [HttpPost("reset-password")]
        [EndpointSummary("Reset Password")]
        [EndpointDescription("Completes the password reset flow using a valid reset token.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken cancellationToken)
        {
            var result = await _mediator.Send(new ResetPasswordCommand(request), cancellationToken);
            return Ok(result);
        }

        [Authorize]
        [HttpGet("me")]
        [EndpointSummary("Retrieve Authenticated Admin Context")]
        [EndpointDescription("Fetches detailed profiles and metadata of the currently logged in administrator.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<CurrentAdminResponse>))]
        [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetCurrentUser(CancellationToken cancellationToken)
        {
            var result = await _mediator.Send(new GetCurrentAdminQuery(), cancellationToken);
            return Ok(result);
        }

        [HttpGet("validate-token")]
        [EndpointSummary("Verify Token Validity")]
        [EndpointDescription("Validates whether a given access token is authentic and not expired.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<TokenValidationResponse>))]
        public async Task<IActionResult> ValidateToken([FromQuery] string token, CancellationToken cancellationToken)
        {
            var result = await _mediator.Send(new ValidateTokenQuery(token), cancellationToken);
            return Ok(result);
        }
    }
}
