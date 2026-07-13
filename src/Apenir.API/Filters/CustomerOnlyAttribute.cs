using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters; //IAuthorizationFilter
using System;
using System.Security.Claims; // get the info of users eg (userid, userrole)

namespace Apenir.API.Filters;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class CustomerOnlyAttribute : Attribute, IAuthorizationFilter
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
        if (roleClaim == null || roleClaim.Value != "Customer")
        {
            context.Result = new JsonResult(new { code = "FORBIDDEN", message = "Insufficient permissions" })
            {
                StatusCode = 403
            };
        }
    }
}
