using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

var app = builder.Build();

app.UseRouting();

// Logs EVERY request (very useful for debugging Meta)
app.Use(async (context, next) =>
{
    Console.WriteLine($"[{DateTime.UtcNow}] {context.Request.Method} {context.Request.Path}");
    await next();
});

app.MapGet("/", () => Results.Ok(new { Status = "API is running" }));

app.MapControllers();

app.Run();