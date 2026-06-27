using Microsoft.AspNetCore.Http; // request, response, cookie, statuscode
using Microsoft.Extensions.Configuration;
using System.Security.Claims; // get the info of users eg (userid, userrole)
using System.Threading.Tasks; // async, await, task
using System;
using System.Linq; // first(), where(), select(), firstordefault()

namespace Apenir.API.Middleware;

public class JwtMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;

    public JwtMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _configuration = configuration;
    }

    public async Task Invoke(HttpContext context)
    {
        var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
        if (authHeader != null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader.Substring("Bearer ".Length).Trim();
            var jwtSecret = _configuration["Jwt:Secret"] ?? "super_secret_key_which_is_at_least_32_bytes_long_1234567890";

            var result = JwtHelper.ValidateToken(token, jwtSecret);

            if (result.IsValid)
            {
                var claims = new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, result.UserId),
                    new Claim(ClaimTypes.Role, result.Role),
                    new Claim("phone", result.Phone)
                };

                var identity = new ClaimsIdentity(claims, "Jwt");
                context.User = new ClaimsPrincipal(identity);
                context.Items["User"] = result;
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                var errorResponse = result.Error == "TOKEN_EXPIRED" 
                    ? new { code = "TOKEN_EXPIRED", message = "Access token expired, refresh needed" }
                    : new { code = "TOKEN_INVALID", message = "Invalid or tampered token" };
                
                await context.Response.WriteAsJsonAsync(errorResponse);
                return;
            }
        }

        await _next(context);
    }
}
