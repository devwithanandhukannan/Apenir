using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Apenir.Core.Interfaces;
using Apenir.Infrastructure.Data;
using Apenir.Infrastructure.Services;

namespace Apenir.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("MongoAtlas");

        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException("Connection string 'MongoAtlas' not found.");
        }

        services.AddDbContext<AppDbContext>(options => 
            options.UseMongoDB(connectionString, "Apenir"));

        services.AddScoped<IApplicationDbContext>(provider => 
            provider.GetRequiredService<AppDbContext>());

        services.AddScoped<IWhatsAppService, WhatsAppService>();

        return services;
    }
}