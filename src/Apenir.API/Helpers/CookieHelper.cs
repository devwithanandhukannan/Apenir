using Microsoft.AspNetCore.Http;
using System;

namespace Apenir.API.Helpers
{
    public static class CookieHelper
    {
        private const string COOKIE_NAME = "refresh_token";

        public static void SetRefreshTokenCookie(HttpContext httpContext, string token, string path = "/", int expiryDays = 7)
        {
            httpContext.Response.Cookies.Append(COOKIE_NAME, token, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,
                Path = path,
                Expires = DateTime.UtcNow.AddDays(expiryDays)
            });
        }

        public static void DeleteRefreshTokenCookie(HttpContext httpContext, string path = "/")
        {
            httpContext.Response.Cookies.Delete(COOKIE_NAME, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,
                Path = path
            });
        }

        public static string? GetRefreshTokenCookie(HttpContext httpContext)
        {
            if (httpContext.Request.Cookies.TryGetValue(COOKIE_NAME, out var token))
            {
                return token;
            }
            return null;
        }
    }
}
