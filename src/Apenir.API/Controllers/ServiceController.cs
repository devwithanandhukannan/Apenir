using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Apenir.Core.Interfaces;
using Apenir.Core.Entities;
using Apenir.API.Filters;
using Apenir.Application.Common.Models;
using System.Threading.Tasks;
using System;

namespace Apenir.API.Controllers;

[ApiController]
[Route("api/services")]
[Authorize]
public class ServiceController : ControllerBase
{
    private readonly IApplicationDbContext _context;

    public ServiceController(IApplicationDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetServices(
        [FromQuery] string? filter = null,
        [FromQuery] string? category = null,
        [FromQuery] string? name = null,
        [FromQuery] string? search = null)
    {
        var query = _context.Services.AsNoTracking().Where(s => s.IsActive);

        if (!string.IsNullOrWhiteSpace(category))
        {
            var categoryTerm = category.Trim().ToLower();
            query = query.Where(s => s.Category != null && s.Category.ToLower().Contains(categoryTerm));
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var nameTerm = name.Trim().ToLower();
            query = query.Where(s => s.Name != null && s.Name.ToLower().Contains(nameTerm));
        }

        var searchTerm = !string.IsNullOrWhiteSpace(search) ? search : filter;
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.Trim().ToLower();
            query = query.Where(s => 
                (s.Name != null && s.Name.ToLower().Contains(term)) || 
                (s.Category != null && s.Category.ToLower().Contains(term)) || 
                (s.Description != null && s.Description.ToLower().Contains(term)));
        }

        var services = await query.ToListAsync();

        return Ok(ApiResponse<List<Service>>.SuccessResult(services, "SERVICES_RETRIEVED"));
    }

    [HttpPost]
    [AdminOnly]
    public async Task<IActionResult> AddService([FromBody] CreateServiceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(ApiResponse.FailureResult("Service name cannot be empty."));
        }

        if (string.IsNullOrWhiteSpace(request.Category))
        {
            return BadRequest(ApiResponse.FailureResult("Service category cannot be empty."));
        }

        if (request.BasePrice < 0)
        {
            return BadRequest(ApiResponse.FailureResult("Base price cannot be negative."));
        }

        var service = new Service
        {
            Name = request.Name,
            Description = request.Description,
            Category = request.Category,
            BasePrice = request.BasePrice,
            PlatformCommissionPct = request.PlatformCommissionPct >= 0 ? request.PlatformCommissionPct : 15.00m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _context.Services.Add(service);
        await _context.SaveChangesAsync();

        return Ok(ApiResponse<Service>.SuccessResult(service, "SERVICE_ADDED"));
    }
}

public record CreateServiceRequest(
    string Name,
    string? Description,
    string Category,
    decimal BasePrice,
    decimal PlatformCommissionPct
);
