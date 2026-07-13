using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Apenir.API.Middleware;
using Apenir.Application;
using Apenir.Infrastructure;
using Apenir.Infrastructure.Services;
using Apenir.API.BackgroundServices;
using Apenir.Core.Interfaces;
using Apenir.Application.Common.Interfaces;
using Apenir.Core.Enums;
using Apenir.Core.Entities;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);

// Add layers to DI container
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddInfrastructureServices(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache(); // Used by WhatsAppWebhookProcessor to cache services/branches for 5 min

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.SetIsOriginAllowed(origin => true)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

// Register background WhatsApp processing services
builder.Services.AddSingleton<IWhatsAppWebhookQueue, WhatsAppWebhookQueue>();
builder.Services.AddHostedService<WhatsAppWebhookProcessor>();

// Configure OpenAPI (.NET 10 style document generation)
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, cancellationToken) =>
    {
        if (document == null)
        {
            return Task.CompletedTask;
        }

        document.Servers = new List<OpenApiServer>
        {
            new OpenApiServer { Url = "https://api.anandhu-kannan.in", Description = "Production Server" }
        };

        document.Info = new OpenApiInfo
        {
            Title = "Apenir Platform API",
            Version = "v1",
            Description = "API documentation for the Apenir Platform, including Admin Authentication and management services."
        };

        var securityScheme = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Name = "Authorization",
            In = ParameterLocation.Header,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Enter your JWT Access Token (do not type 'Bearer ' prefix, just the token)."
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes.Add("Bearer", securityScheme);

        document.Security ??= new List<OpenApiSecurityRequirement>();
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", document)] = new List<string>()
        });

        return Task.CompletedTask;
    });
});

var app = builder.Build();

// Enable Middlewares in priority order
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<GlobalExceptionHandlingMiddleware>();

// Configure Development vs Production Pipelines
app.MapOpenApi();
app.MapScalarApiReference(options =>
{
    options.WithTitle("Apenir API Documentation")
           .WithTheme(ScalarTheme.DeepSpace)
           .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
});

app.UseRouting();
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();

// Fallback Status Route
app.MapGet("/", () => Results.Ok(new { Status = "Apenir API is running", Environment = app.Environment.EnvironmentName }));

app.MapControllers();

using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
    var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

    var defaultEmail = config["AdminSettings:DefaultEmail"] ?? "admin@gmail.com";
    var defaultPass = config["AdminSettings:DefaultPassword"] ?? "admin@123";
    var defaultName = config["AdminSettings:DefaultFullName"] ?? "Super Admin";

    // 1. Remove all other SuperAdmin accounts to clean obsolete seed admin accounts
    var otherAdmins = context.Users.Where(u => u.Role == UserRole.SuperAdmin && u.Email != defaultEmail).ToList();
    if (otherAdmins.Any())
    {
        context.Users.RemoveRange(otherAdmins);
        await context.SaveChangesAsync();
        Console.WriteLine($"[DB INITIALIZATION] Removed {otherAdmins.Count} obsolete SuperAdmin accounts.");
    }

    // 2. Find or seed the configured admin user
    var adminUser = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
        context.Users.Where(u => u.Email == defaultEmail));

    if (adminUser == null)
    {
        adminUser = new User
        {
            Id = Guid.NewGuid().ToString(),
            Name = defaultName,
            Email = defaultEmail,
            PasswordHash = passwordHasher.Hash(defaultPass),
            Role = UserRole.SuperAdmin,
            IsActive = true,
            IsDeleted = false,
            Status = "Active",
            CreatedAt = DateTime.UtcNow,
            Permissions = new List<string>()
        };

        context.Users.Add(adminUser);
        await context.SaveChangesAsync();
        Console.WriteLine($"[DB INITIALIZATION] Default Admin user created: Email={defaultEmail}");
    }
    else
    {
        bool updated = false;

        if (adminUser.Role != UserRole.SuperAdmin)
        {
            adminUser.Role = UserRole.SuperAdmin;
            updated = true;
        }
        if (adminUser.Name != defaultName)
        {
            adminUser.Name = defaultName;
            updated = true;
        }
        // Verify password: if verification fails, update hash
        if (!passwordHasher.Verify(adminUser.PasswordHash, defaultPass))
        {
            adminUser.PasswordHash = passwordHasher.Hash(defaultPass);
            updated = true;
        }

        if (updated)
        {
            await context.SaveChangesAsync();
            Console.WriteLine($"[DB INITIALIZATION] Default Admin user details updated to match appsettings.json: Email={defaultEmail}");
        }
    }
}

app.Run();