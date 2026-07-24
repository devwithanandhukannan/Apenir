using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System;
using System.Security.Claims;

namespace Apenir.API.Filters;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class AdminOnlyAttribute : Attribute, IAuthorizationFilter
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

        var roleClaims = System.Linq.Enumerable.Where(user.Claims, c => c.Type == ClaimTypes.Role || c.Type.Equals("role", StringComparison.OrdinalIgnoreCase));
        bool isAdmin = false;
        foreach (var claim in roleClaims)
        {
            if (claim.Value.Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase) || claim.Value.Equals("Admin", StringComparison.OrdinalIgnoreCase))
            {
                isAdmin = true;
                break;
            }
        }

        if (!isAdmin)
        {
            context.Result = new JsonResult(new { code = "FORBIDDEN", message = "Insufficient permissions" })
            {
                StatusCode = 403
            };
        }
    }
}
