using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Apenir.Core.Interfaces;
using Apenir.Core.Entities;
using Apenir.Application.Common.Models;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Apenir.API.Controllers;

[ApiController]
[Route("api/public")]
[AllowAnonymous]
public class PublicCatalogController : ControllerBase
{
    private readonly IApplicationDbContext _context;

    public PublicCatalogController(IApplicationDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Public unauthenticated endpoint to list all active services with optional query filters.
    /// </summary>
    [HttpGet("services")]
    [EndpointSummary("Get all active public services")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<Service>>))]
    public async Task<IActionResult> GetPublicServices(
        [FromQuery] string? filter = null,
        [FromQuery] string? category = null,
        [FromQuery] string? name = null,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
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

        var services = await query.ToListAsync(cancellationToken);

        return Ok(ApiResponse<List<Service>>.SuccessResult(services, "SERVICES_RETRIEVED"));
    }

    /// <summary>
    /// Public unauthenticated endpoint to get details of a specific service by ID.
    /// </summary>
    [HttpGet("services/{id}")]
    [EndpointSummary("Get a specific public service by ID")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<Service>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
    public async Task<IActionResult> GetPublicServiceById(
        [FromRoute] string id,
        CancellationToken cancellationToken = default)
    {
        var service = await _context.Services.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.IsActive, cancellationToken);

        if (service == null)
        {
            return NotFound(ApiResponse.FailureResult("Service not found or inactive."));
        }

        return Ok(ApiResponse<Service>.SuccessResult(service, "SERVICE_RETRIEVED"));
    }

    /// <summary>
    /// Public unauthenticated endpoint to list all active master health packages.
    /// </summary>
    [HttpGet("packages")]
    [EndpointSummary("Get all active public master packages")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<BranchPackageDto>>))]
    public async Task<IActionResult> GetPublicPackages(
        [FromQuery] string? filter = null,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        var masterPackagesQuery = _context.Packages.AsNoTracking()
            .Where(p => p.CreatedByBranchId == null && p.IsActive);

        var searchTerm = !string.IsNullOrWhiteSpace(search) ? search : filter;
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.Trim().ToLower();
            masterPackagesQuery = masterPackagesQuery.Where(p =>
                (p.Name != null && p.Name.ToLower().Contains(term)) ||
                (p.Description != null && p.Description.ToLower().Contains(term)));
        }

        var masterPackages = await masterPackagesQuery.ToListAsync(cancellationToken);

        var allServiceIds = masterPackages.SelectMany(p => p.ServiceIds).Distinct().ToList();

        var services = await _context.Services.AsNoTracking()
            .Where(s => allServiceIds.Contains(s.Id) && s.IsActive)
            .ToListAsync(cancellationToken);

        var result = masterPackages.Select(p => new BranchPackageDto
        {
            PackageId = p.Id,
            Name = p.Name,
            Description = p.Description ?? string.Empty,
            BasePrice = p.BasePrice,
            CustomPrice = null,
            PlatformCommissionPct = p.PlatformCommissionPct,
            CustomCommissionPct = null,
            IsActive = true,
            IsAdminPackage = true,
            Services = p.ServiceIds.Select(sid => {
                var s = services.FirstOrDefault(service => service.Id == sid);
                return new PackageServiceDetailDto
                {
                    ServiceId = sid,
                    Name = s?.Name ?? "Unknown",
                    Category = s?.Category ?? "Unknown",
                    Description = s?.Description ?? string.Empty,
                    BasePrice = s?.BasePrice ?? 0,
                    CustomPrice = null
                };
            }).ToList()
        }).ToList();

        return Ok(ApiResponse<List<BranchPackageDto>>.SuccessResult(result, "PACKAGES_RETRIEVED"));
    }

    /// <summary>
    /// Public unauthenticated endpoint to get details of a specific master package by ID.
    /// </summary>
    [HttpGet("packages/{id}")]
    [EndpointSummary("Get a specific public master package by ID")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<BranchPackageDto>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
    public async Task<IActionResult> GetPublicPackageById(
        [FromRoute] string id,
        CancellationToken cancellationToken = default)
    {
        var package = await _context.Packages.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.CreatedByBranchId == null && p.IsActive, cancellationToken);

        if (package == null)
        {
            return NotFound(ApiResponse.FailureResult("Package not found or inactive."));
        }

        var services = await _context.Services.AsNoTracking()
            .Where(s => package.ServiceIds.Contains(s.Id) && s.IsActive)
            .ToListAsync(cancellationToken);

        var result = new BranchPackageDto
        {
            PackageId = package.Id,
            Name = package.Name,
            Description = package.Description ?? string.Empty,
            BasePrice = package.BasePrice,
            CustomPrice = null,
            PlatformCommissionPct = package.PlatformCommissionPct,
            CustomCommissionPct = null,
            IsActive = true,
            IsAdminPackage = true,
            Services = package.ServiceIds.Select(sid => {
                var s = services.FirstOrDefault(service => service.Id == sid);
                return new PackageServiceDetailDto
                {
                    ServiceId = sid,
                    Name = s?.Name ?? "Unknown",
                    Category = s?.Category ?? "Unknown",
                    Description = s?.Description ?? string.Empty,
                    BasePrice = s?.BasePrice ?? 0,
                    CustomPrice = null
                };
            }).ToList()
        };

        return Ok(ApiResponse<BranchPackageDto>.SuccessResult(result, "PACKAGE_RETRIEVED"));
    }
}
