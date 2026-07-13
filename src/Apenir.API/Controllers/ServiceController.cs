using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Apenir.Core.Interfaces;
using Apenir.Core.Entities;
using Apenir.API.Filters;
using Apenir.Application.Common.Models;
using Apenir.Application.Common.Interfaces;
using System.Threading.Tasks;
using System.Threading;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Apenir.API.Controllers;

[ApiController]
[Route("api/services")]
[Authorize]
public class ServiceController : ControllerBase
{
    private readonly IApplicationDbContext _context;
    private readonly ICurrentUserService _currentUserService;

    public ServiceController(IApplicationDbContext context, ICurrentUserService currentUserService)
    {
        _context = context;
        _currentUserService = currentUserService;
    }

    /// <summary>GET /api/services — active services only (used by lab portal; excludes commission)</summary>
    [HttpGet]
    public async Task<IActionResult> GetServices(CancellationToken cancellationToken)
    {
        var services = await _context.Services
            .Where(s => s.IsActive)
            .OrderBy(s => s.Category).ThenBy(s => s.Name)
            .ToListAsync(cancellationToken);

        // Map to a DTO that excludes platform commission — lab admins must not see it
        var result = services.Select(s => new ServicePublicDto
        {
            Id = s.Id,
            Name = s.Name,
            Description = s.Description,
            Category = s.Category,
            BasePrice = s.BasePrice,
            IsActive = s.IsActive,
            CreatedByBranchId = s.CreatedByBranchId,
            CreatedAt = s.CreatedAt
        }).ToList();

        return Ok(ApiResponse<List<ServicePublicDto>>.SuccessResult(result, "SERVICES_RETRIEVED"));
    }

    /// <summary>GET /api/services/all — all services (admin only), includes lab-custom + commission + branch name label</summary>
    [HttpGet("all")]
    [AdminOnly]
    public async Task<IActionResult> GetAllServicesAdmin(CancellationToken cancellationToken)
    {
        var services = await _context.Services
            .OrderBy(s => s.Category).ThenBy(s => s.Name)
            .ToListAsync(cancellationToken);

        // For lab-custom services, resolve branch name
        var branchIds = services
            .Where(s => s.CreatedByBranchId != null)
            .Select(s => s.CreatedByBranchId!)
            .Distinct()
            .ToList();

        var branchNames = new Dictionary<string, string>();
        if (branchIds.Any())
        {
            var branches = await _context.Branches
                .Where(b => branchIds.Contains(b.Id))
                .Select(b => new { b.Id, b.Name })
                .ToListAsync(cancellationToken);
            branchNames = branches.ToDictionary(b => b.Id, b => b.Name);
        }

        var result = services.Select(s => new ServiceAdminDto
        {
            Id = s.Id,
            Name = s.Name,
            Description = s.Description,
            Category = s.Category,
            BasePrice = s.BasePrice,
            PlatformCommissionPct = s.PlatformCommissionPct,
            IsActive = s.IsActive,
            CreatedByBranchId = s.CreatedByBranchId,
            CreatedByBranchName = s.CreatedByBranchId != null && branchNames.ContainsKey(s.CreatedByBranchId)
                ? branchNames[s.CreatedByBranchId]
                : null,
            CreatedAt = s.CreatedAt
        }).ToList();

        return Ok(ApiResponse<List<ServiceAdminDto>>.SuccessResult(result, "ALL_SERVICES_RETRIEVED"));
    }

    /// <summary>POST /api/services — create master service (admin only)</summary>
    [HttpPost]
    [AdminOnly]
    public async Task<IActionResult> AddService([FromBody] CreateServiceRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(ApiResponse.FailureResult("Service name cannot be empty."));

        if (string.IsNullOrWhiteSpace(request.Category))
            return BadRequest(ApiResponse.FailureResult("Service category cannot be empty."));

        if (request.BasePrice < 0)
            return BadRequest(ApiResponse.FailureResult("Base price cannot be negative."));

        var service = new Service
        {
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            Category = request.Category.Trim(),
            BasePrice = request.BasePrice,
            PlatformCommissionPct = request.PlatformCommissionPct >= 0 ? request.PlatformCommissionPct : 15.00m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _context.Services.Add(service);
        await _context.SaveChangesAsync(cancellationToken);

        return Ok(ApiResponse<Service>.SuccessResult(service, "SERVICE_ADDED"));
    }

    /// <summary>PUT /api/services/{id} — update any service (admin only); can update name, description, category, price, commission, and active status</summary>
    [HttpPut("{id}")]
    [AdminOnly]
    public async Task<IActionResult> UpdateService(
        [FromRoute] string id,
        [FromBody] UpdateServiceRequest request,
        CancellationToken cancellationToken)
    {
        var service = await _context.Services.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (service == null)
            return NotFound(ApiResponse.FailureResult("Service not found."));

        if (!string.IsNullOrWhiteSpace(request.Name))
            service.Name = request.Name.Trim();

        if (request.Description != null)
            service.Description = request.Description.Trim();

        if (!string.IsNullOrWhiteSpace(request.Category))
            service.Category = request.Category.Trim();

        if (request.BasePrice.HasValue && request.BasePrice.Value >= 0)
            service.BasePrice = request.BasePrice.Value;

        if (request.PlatformCommissionPct.HasValue &&
            request.PlatformCommissionPct.Value >= 0 &&
            request.PlatformCommissionPct.Value <= 100)
            service.PlatformCommissionPct = request.PlatformCommissionPct.Value;

        if (request.IsActive.HasValue)
            service.IsActive = request.IsActive.Value;

        _context.Services.Update(service);
        await _context.SaveChangesAsync(cancellationToken);

        return Ok(ApiResponse<Service>.SuccessResult(service, "SERVICE_UPDATED"));
    }
}

// ── DTOs ─────────────────────────────────────────────────────────

/// <summary>Public-facing DTO — NO commission exposed to lab admins</summary>
public class ServicePublicDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = string.Empty;
    public decimal BasePrice { get; set; }
    public bool IsActive { get; set; }
    public string? CreatedByBranchId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Admin DTO — includes commission + lab source label</summary>
public class ServiceAdminDto : ServicePublicDto
{
    public decimal PlatformCommissionPct { get; set; }
    public string? CreatedByBranchName { get; set; }
}

public record CreateServiceRequest(
    string Name,
    string? Description,
    string Category,
    decimal BasePrice,
    decimal PlatformCommissionPct
);

public record UpdateServiceRequest(
    string? Name,
    string? Description,
    string? Category,
    decimal? BasePrice,
    decimal? PlatformCommissionPct,
    bool? IsActive
);
