using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Apenir.Application.Common.Interfaces;

namespace Apenir.IntegrationTests
{
    public class CustomWebApplicationFactory : WebApplicationFactory<Program>
    {
        public Mock<IAdminRepository> AdminRepoMock { get; } = new();
        public Mock<IRefreshTokenRepository> RefreshTokenRepoMock { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            builder.ConfigureTestServices(services =>
            {
                // Replace registered repositories with our mocks
                services.Replace(ServiceDescriptor.Scoped<IAdminRepository>(_ => AdminRepoMock.Object));
                services.Replace(ServiceDescriptor.Scoped<IRefreshTokenRepository>(_ => RefreshTokenRepoMock.Object));
                
                // Replace seeder with a mock that does nothing to bypass MongoDB connection checks
                // services.Replace(ServiceDescriptor.Scoped<Apenir.Infrastructure.Services.IDatabaseSeeder>(_ => Mock.Of<Apenir.Infrastructure.Services.IDatabaseSeeder>()));
            });
        }
    }
}
