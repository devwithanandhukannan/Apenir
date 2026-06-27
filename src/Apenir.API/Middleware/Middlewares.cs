using System;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Apenir.Application.Common.Models;
using Apenir.Core.Exceptions;

namespace Apenir.API.Middleware
{
    // --- 1. Correlation ID Middleware ---
    public class CorrelationIdMiddleware
    {
        private readonly RequestDelegate _next;
        private const string CorrelationIdHeader = "X-Correlation-ID";

        public CorrelationIdMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!context.Request.Headers.TryGetValue(CorrelationIdHeader, out var correlationId))
            {
                correlationId = Guid.NewGuid().ToString();
            }

            context.Response.Headers[CorrelationIdHeader] = correlationId;

            // Make it available in HttpContext Items
            context.Items[CorrelationIdHeader] = correlationId;

            await _next(context);
        }
    }

    // --- 2. Request Logging Middleware ---
    public class RequestLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RequestLoggingMiddleware> _logger;

        public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var stopwatch = Stopwatch.StartNew();
            var request = context.Request;

            _logger.LogInformation("HTTP Request Started: {Method} {Path} from IP {IP}", 
                request.Method, request.Path, context.Connection.RemoteIpAddress);

            try
            {
                await _next(context);
                stopwatch.Stop();

                _logger.LogInformation("HTTP Request Finished: {Method} {Path} responded {StatusCode} in {ElapsedMs}ms",
                    request.Method, request.Path, context.Response.StatusCode, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception)
            {
                stopwatch.Stop();
                _logger.LogWarning("HTTP Request Failed: {Method} {Path} threw exception after {ElapsedMs}ms",
                    request.Method, request.Path, stopwatch.ElapsedMilliseconds);
                throw;
            }
        }
    }

    // --- 3. Security Headers Middleware ---
    public class SecurityHeadersMiddleware
    {
        private readonly RequestDelegate _next;

        public SecurityHeadersMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var headers = context.Response.Headers;

            headers["X-Frame-Options"] = "DENY";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-XSS-Protection"] = "1; mode=block";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

            var path = context.Request.Path.Value ?? "";
            if (path.StartsWith("/scalar", StringComparison.OrdinalIgnoreCase) || 
                path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase))
            {
                headers["Content-Security-Policy"] = "default-src 'self' 'unsafe-inline' 'unsafe-eval' data: blob: https:; frame-ancestors 'none';";
            }
            else
            {
                headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none';";
            }
            
            if (context.Request.IsHttps)
            {
                headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains; preload";
            }

            await _next(context);
        }
    }

    // --- 4. Global Exception Handling Middleware ---
    public class GlobalExceptionHandlingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<GlobalExceptionHandlingMiddleware> _logger;

        public GlobalExceptionHandlingMiddleware(RequestDelegate next, ILogger<GlobalExceptionHandlingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                await HandleExceptionAsync(context, ex);
            }
        }

        private Task HandleExceptionAsync(HttpContext context, Exception exception)
        {
            context.Response.ContentType = "application/json";

            var statusCode = HttpStatusCode.InternalServerError;
            string message = "An unexpected error occurred.";
            var errors = new System.Collections.Generic.List<string>();

            switch (exception)
            {
                case ValidationException valEx:
                    statusCode = HttpStatusCode.BadRequest;
                    message = valEx.Message;
                    errors.AddRange(valEx.Errors);
                    break;

                case InvalidCredentialsException credEx:
                    statusCode = HttpStatusCode.Unauthorized;
                    message = credEx.Message;
                    break;

                case AccountDisabledException disEx:
                    statusCode = HttpStatusCode.Forbidden;
                    message = disEx.Message;
                    break;

                case TokenExpiredException expEx:
                    statusCode = HttpStatusCode.Unauthorized;
                    message = expEx.Message;
                    break;

                case RefreshTokenExpiredException rtExpEx:
                    statusCode = HttpStatusCode.Unauthorized;
                    message = rtExpEx.Message;
                    break;

                case RefreshTokenRevokedException rtRevEx:
                    statusCode = HttpStatusCode.Unauthorized;
                    message = rtRevEx.Message;
                    break;

                case UnauthorizedException unauthEx:
                    statusCode = HttpStatusCode.Unauthorized;
                    message = unauthEx.Message;
                    break;

                default:
                    _logger.LogError(exception, "Unhandled system exception occurred.");
                    message = "A critical system error occurred. Please contact administrator.";
                    break;
            }

            context.Response.StatusCode = (int)statusCode;
            
            if (errors.Count == 0)
            {
                errors.Add(exception.Message);
            }

            var apiResponse = ApiResponse.FailureResult(errors, message);
            var json = JsonSerializer.Serialize(apiResponse, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            return context.Response.WriteAsync(json);
        }
    }
}
