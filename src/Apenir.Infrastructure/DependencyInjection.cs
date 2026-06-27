using System;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Apenir.Application.Common.Interfaces;
using Apenir.Application.Common.Models;
using Apenir.Infrastructure.Persistence;
using Apenir.Infrastructure.Repositories;
using Apenir.Infrastructure.Security;
using Apenir.Infrastructure.Services;

namespace Apenir.Infrastructure
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
        {
            // Bind settings from Configuration
            services.Configure<MongoSettings>(configuration.GetSection("MongoSettings"));
            services.Configure<JwtSettings>(configuration.GetSection("JwtSettings"));
            services.Configure<AdminSettings>(configuration.GetSection("AdminSettings"));

            // Register DbContext
            services.AddSingleton<MongoDbContext>();

            // Register Repositories
            services.AddScoped<IAdminRepository, AdminRepository>();
            services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();

            // Register Security Services
            services.AddScoped<IPasswordHasher, BCryptPasswordHasher>();
            services.AddScoped<IJwtTokenService, JwtTokenService>();
            services.AddScoped<ICurrentUserService, CurrentUserService>();

            // Register Seeder
            services.AddScoped<IDatabaseSeeder, DatabaseSeeder>();

            // HTTP context accessor is required for CurrentUserService
            services.AddHttpContextAccessor();

            // Configure JWT Authentication
            var jwtSettingsSection = configuration.GetSection("JwtSettings");
            var jwtSettings = jwtSettingsSection.Get<JwtSettings>() ?? new JwtSettings();
            var key = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(jwtSettings.Secret) ? "FallbackSuperSecretKey1234567890123456" : jwtSettings.Secret);

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.RequireHttpsMetadata = false; // Set to true in production
                options.SaveToken = true;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ValidateIssuer = true,
                    ValidIssuer = jwtSettings.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwtSettings.Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(jwtSettings.ClockSkewSeconds)
                };
            });

            services.AddAuthorization(options =>
            {
                // Fallback policy can require authenticated users, or define specific roles/permissions
                options.AddPolicy("RequireAdminRole", policy => policy.RequireRole("Admin", "SuperAdmin"));
            });

            return services;
        }
    }
}
