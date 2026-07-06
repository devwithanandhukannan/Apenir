using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Apenir.API.DTOs;
using Apenir.Application.Common.Models;
using Apenir.Application.DTOs;
using Apenir.Application.Features.Auth.Commands;
using Apenir.API.Helpers;

namespace Apenir.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly IMediator _mediator;

        public AuthController(IMediator mediator)
        {
            _mediator = mediator;
        }

        [HttpPost("otp/send")]
        [EndpointSummary("Send OTP")]
        [EndpointDescription("Sends an OTP to the provided phone number.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        public async Task<IActionResult> SendOtp([FromBody] SendOtpRequest request, CancellationToken cancellationToken)
        {
            var result = await _mediator.Send(new SendOtpCommand(request), cancellationToken);
            return Ok(result);
        }

        [HttpPost("otp/verify")]
        [EndpointSummary("Verify OTP")]
        [EndpointDescription("Verifies the OTP and issues a JWT and Refresh Token.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<AuthResponse>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse<AuthResponse>))]
        public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpRequest request, CancellationToken cancellationToken)
        {
            var result = await _mediator.Send(
                new VerifyOtpCommand(request, Request.Headers["User-Agent"].ToString(), HttpContext.Connection.RemoteIpAddress?.ToString()), 
                cancellationToken);

            if (result.Success && result.Data != null)
            {
                CookieHelper.SetRefreshTokenCookie(HttpContext, result.Data.RefreshToken, "/api/auth/refresh");
                result.Data = result.Data with { RefreshToken = string.Empty }; // Hide from response body
            }

            if (!result.Success)
            {
                return BadRequest(result);
            }

            return Ok(result);
        }

        [HttpPost("refresh")]
        [EndpointSummary("Refresh Token")]
        [EndpointDescription("Issues a new access token using a valid refresh token.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<RefreshTokenResponse>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse<AuthResponse>))]
        [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ApiResponse<AuthResponse>))]
        public async Task<IActionResult> RefreshToken([FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] RefreshTokenRequest? request, CancellationToken cancellationToken)
        {
            string? refreshToken = request?.RefreshToken;
            var cookieToken = CookieHelper.GetRefreshTokenCookie(HttpContext);
            if (!string.IsNullOrEmpty(cookieToken))
            {
                refreshToken = cookieToken;
            }

            if (string.IsNullOrEmpty(refreshToken))
            {
                return BadRequest(ApiResponse<AuthResponse>.FailureResult("Refresh token is required."));
            }

            var result = await _mediator.Send(
                new CommonRefreshTokenCommand(new RefreshTokenRequest { RefreshToken = refreshToken }, HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers["User-Agent"].ToString()), 
                cancellationToken);

            if (result.Success && result.Data != null)
            {
                CookieHelper.SetRefreshTokenCookie(HttpContext, result.Data.RefreshToken, "/api/auth/refresh");
                result.Data.RefreshToken = string.Empty;
                return Ok(ApiResponse<AuthResponse>.SuccessResult(new AuthResponse(result.Data.AccessToken, string.Empty, string.Empty, string.Empty))); // Ideally we'd have a unified response type
            }
            
            return Unauthorized(result);
        }
        
        [HttpPost("logout")]
        [EndpointSummary("Logout")]
        [EndpointDescription("Revokes the current refresh token.")]
        public async Task<IActionResult> Logout([FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] RefreshTokenRequest? request, CancellationToken cancellationToken)
        {
            string? refreshToken = request?.RefreshToken;
            var cookieToken = CookieHelper.GetRefreshTokenCookie(HttpContext);
            if (!string.IsNullOrEmpty(cookieToken))
            {
                refreshToken = cookieToken;
            }

            if (string.IsNullOrEmpty(refreshToken))
            {
                return BadRequest(ApiResponse.FailureResult("Refresh token is required."));
            }

            var result = await _mediator.Send(new CommonLogoutCommand(refreshToken, HttpContext.Connection.RemoteIpAddress?.ToString()), cancellationToken);
            if (result.Success)
            {
                CookieHelper.DeleteRefreshTokenCookie(HttpContext, "/api/auth/refresh");
            }
            return Ok(result);
        }

        [Authorize]
        [HttpPost("logout-all")]
        [EndpointSummary("Logout From All Devices")]
        [EndpointDescription("Revokes all refresh tokens issued to the currently logged in user.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ApiResponse))]
        public async Task<IActionResult> LogoutAll(CancellationToken cancellationToken)
        {
            var result = await _mediator.Send(new CommonLogoutAllDevicesCommand(), cancellationToken);
            if (result.Success)
            {
                CookieHelper.DeleteRefreshTokenCookie(HttpContext, "/api/auth/refresh");
            }
            return Ok(result);
        }
    }
}