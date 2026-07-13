using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System;
using System.Security.Claims;

namespace Apenir.API.Filters;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class StaffOnlyAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (user == null || !user.Identity!.IsAuthenticated)
        {
            context.Result = new JsonResult(new { code = "FORBIDDEN", message = "Insufficient permissions" })
            {
                StatusCode = 403
            };
            return;
        }

        var roleClaim = user.FindFirst(ClaimTypes.Role);
        if (roleClaim == null || roleClaim.Value != "Staff")
        {
            context.Result = new JsonResult(new { code = "FORBIDDEN", message = "Insufficient permissions" })
            {
                StatusCode = 403
            };
        }
    }
}
