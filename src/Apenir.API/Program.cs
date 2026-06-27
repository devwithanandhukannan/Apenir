using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Apenir.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddInfrastructureServices(builder.Configuration);

var app = builder.Build();

app.UseRouting();

app.UseMiddleware<Apenir.API.Middleware.JwtMiddleware>();

// Log every incoming request
app.Use(async (context, next) =>
{
    Console.WriteLine($"[{DateTime.UtcNow}] {context.Request.Method} {context.Request.Path}");
    await next();
});

app.MapGet("/", () => Results.Ok(new
{
    Status = "WhatsApp Webhook API Running v2"
}));

app.MapControllers();

app.Run();