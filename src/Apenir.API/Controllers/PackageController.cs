using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Apenir.Core.Interfaces;
using Apenir.Core.Entities;
using Apenir.Core.Enums;
using Apenir.API.Filters;
using Apenir.Application.Common.Interfaces;
using Apenir.Application.Common.Models;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Apenir.API.Controllers;

[ApiController]
[Route("api/packages")]
[Authorize]
public class PackageController : ControllerBase
{
    private readonly IApplicationDbContext _context;
    private readonly ICurrentUserService _currentUserService;

    public PackageController(IApplicationDbContext context, ICurrentUserService currentUserService)
    {
        _context = context;
        _currentUserService = currentUserService;
    }

    [HttpPost]
    [AdminOnly]
    [EndpointSummary("Create a new master package (Admin only)")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<Package>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
    public async Task<IActionResult> CreatePackage([FromBody] CreatePackageRequest request, CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(ApiResponse.FailureResult("Package name is required."));
        }

        if (request.BasePrice < 0)
        {
            return BadRequest(ApiResponse.FailureResult("Base price cannot be negative."));
        }

        if (request.PlatformCommissionPct < 0 || request.PlatformCommissionPct > 100)
        {
            return BadRequest(ApiResponse.FailureResult("Platform commission percentage must be between 0 and 100."));
        }

        if (request.ServiceIds == null || !request.ServiceIds.Any())
        {
            return BadRequest(ApiResponse.FailureResult("A package must contain at least one service."));
        }

        // Validate that all services exist and are master services (CreatedByBranchId == null)
        var services = await _context.Services
            .Where(s => request.ServiceIds.Contains(s.Id) && s.CreatedByBranchId == null && s.IsActive)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        if (services.Count != request.ServiceIds.Distinct().Count())
        {
            return BadRequest(ApiResponse.FailureResult("Some service IDs are invalid, inactive, or are not master services."));
        }

        var package = new Package
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            BasePrice = request.BasePrice,
            PlatformCommissionPct = request.PlatformCommissionPct,
            IsActive = true,
            CreatedByBranchId = null,
            ServiceIds = request.ServiceIds.Distinct().ToList(),
            CreatedAt = DateTime.UtcNow
        };

        _context.Packages.Add(package);
        await _context.SaveChangesAsync(cancellationToken);

        return Ok(ApiResponse<Package>.SuccessResult(package, "PACKAGE_CREATED"));
    }

    [HttpGet("admin")]
    [AdminOnly]
    [EndpointSummary("Get all master packages (Admin only)")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<Package>>))]
    public async Task<IActionResult> GetAdminPackages(CancellationToken cancellationToken)
    {
        var packages = await _context.Packages
            .Where(p => p.CreatedByBranchId == null)
            .ToListAsync(cancellationToken);

        return Ok(ApiResponse<List<Package>>.SuccessResult(packages, "PACKAGES_RETRIEVED"));
    }

    [HttpGet("admin/{id}")]
    [AdminOnly]
    [EndpointSummary("Get a specific master package (Admin only)")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<Package>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
    public async Task<IActionResult> GetAdminPackage([FromRoute] string id, CancellationToken cancellationToken)
    {
        var package = await _context.Packages
            .FirstOrDefaultAsync(p => p.Id == id && p.CreatedByBranchId == null, cancellationToken);

        if (package == null)
        {
            return NotFound(ApiResponse.FailureResult("Master package not found."));
        }

        return Ok(ApiResponse<Package>.SuccessResult(package, "PACKAGE_RETRIEVED"));
    }

    [HttpPut("admin/{id}")]
    [AdminOnly]
    [EndpointSummary("Update a master package (Admin only)")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<Package>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
    public async Task<IActionResult> UpdateAdminPackage(
        [FromRoute] string id,
        [FromBody] CreatePackageRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(ApiResponse.FailureResult("Package name is required."));
        }

        if (request.BasePrice < 0)
        {
            return BadRequest(ApiResponse.FailureResult("Base price cannot be negative."));
        }

        if (request.PlatformCommissionPct < 0 || request.PlatformCommissionPct > 100)
        {
            return BadRequest(ApiResponse.FailureResult("Platform commission percentage must be between 0 and 100."));
        }

        if (request.ServiceIds == null || !request.ServiceIds.Any())
        {
            return BadRequest(ApiResponse.FailureResult("A package must contain at least one service."));
        }

        var package = await _context.Packages
            .FirstOrDefaultAsync(p => p.Id == id && p.CreatedByBranchId == null, cancellationToken);

        if (package == null)
        {
            return NotFound(ApiResponse.FailureResult("Master package not found."));
        }

        // Validate that all services exist and are master services (CreatedByBranchId == null)
        var services = await _context.Services
            .Where(s => request.ServiceIds.Contains(s.Id) && s.CreatedByBranchId == null && s.IsActive)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        if (services.Count != request.ServiceIds.Distinct().Count())
        {
            return BadRequest(ApiResponse.FailureResult("Some service IDs are invalid, inactive, or are not master services."));
        }

        package.Name = request.Name.Trim();
        package.Description = request.Description?.Trim();
        package.BasePrice = request.BasePrice;
        package.PlatformCommissionPct = request.PlatformCommissionPct;
        package.ServiceIds = request.ServiceIds.Distinct().ToList();

        _context.Packages.Update(package);
        await _context.SaveChangesAsync(cancellationToken);

        return Ok(ApiResponse<Package>.SuccessResult(package, "PACKAGE_UPDATED"));
    }

    [HttpDelete("admin/{id}")]
    [AdminOnly]
    [EndpointSummary("Deactivate a master package (Admin only)")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
    public async Task<IActionResult> DeactivateAdminPackage([FromRoute] string id, CancellationToken cancellationToken)
    {
        var package = await _context.Packages
            .FirstOrDefaultAsync(p => p.Id == id && p.CreatedByBranchId == null, cancellationToken);

        if (package == null)
        {
            return NotFound(ApiResponse.FailureResult("Master package not found."));
        }

        package.IsActive = false;
        _context.Packages.Update(package);
        await _context.SaveChangesAsync(cancellationToken);

        return Ok(ApiResponse.SuccessResult("Master package deactivated successfully."));
    }

    [HttpGet("lab")]
    [EndpointSummary("Get all packages available for the logged-in branch")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<BranchPackageDto>>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
    public async Task<IActionResult> GetBranchPackages(CancellationToken cancellationToken)
    {
        var branch = await GetCurrentBranchAsync(cancellationToken);
        if (branch == null)
        {
            return NotFound(ApiResponse.FailureResult("Branch not found for current user."));
        }

        // Fetch active admin packages and lab custom packages for this branch
        var allPackages = await _context.Packages
            .Where(p => (p.CreatedByBranchId == null && p.IsActive) || p.CreatedByBranchId == branch.Id)
            .ToListAsync(cancellationToken);

        // Fetch branch package links/overrides
        var branchPackages = await _context.BranchPackages
            .Where(bp => bp.BranchId == branch.Id)
            .ToListAsync(cancellationToken);

        // Fetch all services referenced by these packages
        var allServiceIds = allPackages.SelectMany(p => p.ServiceIds).Distinct().ToList();
        
        // Fetch BranchService overrides for customized service pricing/info at this branch
        var branchServices = await _context.BranchServices
            .AsNoTracking()
            .Where(bs => bs.BranchId == branch.Id && bs.IsActive)
            .ToListAsync(cancellationToken);

        var services = await _context.Services
            .AsNoTracking()
            .Where(s => allServiceIds.Contains(s.Id) && s.IsActive)
            .ToListAsync(cancellationToken);

        var result = allPackages.Select(p => {
            var bp = branchPackages.FirstOrDefault(link => link.PackageId == p.Id);
            
            var packageServices = p.ServiceIds.Select(sid => {
                var s = services.FirstOrDefault(service => service.Id == sid);
                var bs = branchServices.FirstOrDefault(overrideBs => overrideBs.ServiceId == sid);
                return new PackageServiceDetailDto
                {
                    ServiceId = sid,
                    Name = s?.Name ?? "Unknown",
                    Category = s?.Category ?? "Unknown",
                    Description = s?.Description ?? string.Empty,
                    BasePrice = s?.BasePrice ?? 0,
                    CustomPrice = bs?.CustomPrice
                };
            }).ToList();

            bool isActive = bp?.IsActive ?? false;
            // For branch-created custom packages, they are active by default if no override exists
            if (p.CreatedByBranchId == branch.Id && bp == null)
            {
                isActive = p.IsActive;
            }

            return new BranchPackageDto
            {
                PackageId = p.Id,
                Name = p.Name,
                Description = p.Description ?? string.Empty,
                BasePrice = p.BasePrice,
                CustomPrice = bp?.CustomPrice,
                PlatformCommissionPct = p.PlatformCommissionPct,
                CustomCommissionPct = bp?.CustomCommissionPct,
                IsActive = isActive,
                IsAdminPackage = p.CreatedByBranchId == null,
                Services = packageServices
            };
        }).ToList();

        return Ok(ApiResponse<List<BranchPackageDto>>.SuccessResult(result, "BRANCH_PACKAGES_RETRIEVED"));
    }

    [HttpPost("lab/{packageId}/subscribe")]
    [EndpointSummary("Subscribe/activate an admin-created package in the lab menu")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
    public async Task<IActionResult> SubscribePackage([FromRoute] string packageId, CancellationToken cancellationToken)
    {
        var branch = await GetCurrentBranchAsync(cancellationToken);
        if (branch == null)
        {
            return NotFound(ApiResponse.FailureResult("Branch not found."));
        }

        // Verify it is a valid active master package
        var package = await _context.Packages
            .FirstOrDefaultAsync(p => p.Id == packageId && p.CreatedByBranchId == null && p.IsActive, cancellationToken);

        if (package == null)
        {
            return NotFound(ApiResponse.FailureResult("Admin package not found or inactive."));
        }

        var bp = await _context.BranchPackages
            .FirstOrDefaultAsync(link => link.BranchId == branch.Id && link.PackageId == packageId, cancellationToken);

        if (bp == null)
        {
            bp = new BranchPackage
            {
                Id = Guid.NewGuid().ToString(),
                BranchId = branch.Id,
                PackageId = packageId,
                CustomPrice = null,
                CustomCommissionPct = null,
                IsActive = true
            };
            _context.BranchPackages.Add(bp);
        }
        else
        {
            bp.IsActive = true;
            _context.BranchPackages.Update(bp);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return Ok(ApiResponse.SuccessResult("Successfully subscribed to package."));
    }

    [HttpPost("lab/{packageId}/unsubscribe")]
    [EndpointSummary("Unsubscribe/deactivate an admin-created package from the lab menu")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
    public async Task<IActionResult> UnsubscribePackage([FromRoute] string packageId, CancellationToken cancellationToken)
    {
        var branch = await GetCurrentBranchAsync(cancellationToken);
        if (branch == null)
        {
            return NotFound(ApiResponse.FailureResult("Branch not found."));
        }

        var bp = await _context.BranchPackages
            .FirstOrDefaultAsync(link => link.BranchId == branch.Id && link.PackageId == packageId, cancellationToken);

        if (bp == null)
        {
            bp = new BranchPackage
            {
                Id = Guid.NewGuid().ToString(),
                BranchId = branch.Id,
                PackageId = packageId,
                IsActive = false
            };
            _context.BranchPackages.Add(bp);
        }
        else
        {
            bp.IsActive = false;
            _context.BranchPackages.Update(bp);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return Ok(ApiResponse.SuccessResult("Successfully unsubscribed from package."));
    }

    [HttpPut("lab/{packageId}/override")]
    [EndpointSummary("Override the price of an admin-created package for the branch")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
    public async Task<IActionResult> OverridePackagePrice(
        [FromRoute] string packageId,
        [FromBody] UpdatePackageOverrideRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null || request.CustomPrice < 0)
        {
            return BadRequest(ApiResponse.FailureResult("Valid custom price is required."));
        }

        var branch = await GetCurrentBranchAsync(cancellationToken);
        if (branch == null)
        {
            return NotFound(ApiResponse.FailureResult("Branch not found."));
        }

        var packageExists = await _context.Packages
            .AnyAsync(p => p.Id == packageId && p.CreatedByBranchId == null, cancellationToken);

        if (!packageExists)
        {
            return NotFound(ApiResponse.FailureResult("Admin package not found."));
        }

        var bp = await _context.BranchPackages
            .FirstOrDefaultAsync(link => link.BranchId == branch.Id && link.PackageId == packageId, cancellationToken);

        if (bp == null)
        {
            bp = new BranchPackage
            {
                Id = Guid.NewGuid().ToString(),
                BranchId = branch.Id,
                PackageId = packageId,
                CustomPrice = request.CustomPrice,
                CustomCommissionPct = null,
                IsActive = true
            };
            _context.BranchPackages.Add(bp);
        }
        else
        {
            bp.CustomPrice = request.CustomPrice;
            _context.BranchPackages.Update(bp);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return Ok(ApiResponse.SuccessResult("Package price override updated successfully."));
    }

    [HttpPost("lab/custom")]
    [EndpointSummary("Create a new custom package for the branch")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<Package>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
    public async Task<IActionResult> CreateLabCustomPackage(
        [FromBody] CreateLabPackageRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(ApiResponse.FailureResult("Package name is required."));
        }

        if (request.CustomPrice < 0)
        {
            return BadRequest(ApiResponse.FailureResult("Custom price cannot be negative."));
        }

        if (request.ServiceIds == null || !request.ServiceIds.Any())
        {
            return BadRequest(ApiResponse.FailureResult("A package must contain at least one service."));
        }

        var branch = await GetCurrentBranchAsync(cancellationToken);
        if (branch == null)
        {
            return NotFound(ApiResponse.FailureResult("Branch not found."));
        }

        // Validate that all services are active and available to this branch
        var validServiceIds = await _context.Services
            .Where(s => request.ServiceIds.Contains(s.Id) && 
                        (s.CreatedByBranchId == null || s.CreatedByBranchId == branch.Id) && 
                        s.IsActive)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        if (validServiceIds.Count != request.ServiceIds.Distinct().Count())
        {
            return BadRequest(ApiResponse.FailureResult("Some service IDs are invalid, inactive, or not available to this branch."));
        }

        var package = new Package
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            BasePrice = request.CustomPrice,
            PlatformCommissionPct = 15.00m,
            IsActive = true,
            CreatedByBranchId = branch.Id,
            ServiceIds = request.ServiceIds.Distinct().ToList(),
            CreatedAt = DateTime.UtcNow
        };

        var bp = new BranchPackage
        {
            Id = Guid.NewGuid().ToString(),
            BranchId = branch.Id,
            PackageId = package.Id,
            CustomPrice = request.CustomPrice,
            CustomCommissionPct = null,
            IsActive = true
        };

        _context.Packages.Add(package);
        _context.BranchPackages.Add(bp);
        await _context.SaveChangesAsync(cancellationToken);

        return Ok(ApiResponse<Package>.SuccessResult(package, "LAB_PACKAGE_CREATED"));
    }

    [HttpPut("lab/custom/{packageId}")]
    [EndpointSummary("Update an existing custom package owned by the branch")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<Package>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
    public async Task<IActionResult> UpdateLabCustomPackage(
        [FromRoute] string packageId,
        [FromBody] CreateLabPackageRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(ApiResponse.FailureResult("Package name is required."));
        }

        if (request.CustomPrice < 0)
        {
            return BadRequest(ApiResponse.FailureResult("Custom price cannot be negative."));
        }

        if (request.ServiceIds == null || !request.ServiceIds.Any())
        {
            return BadRequest(ApiResponse.FailureResult("A package must contain at least one service."));
        }

        var branch = await GetCurrentBranchAsync(cancellationToken);
        if (branch == null)
        {
            return NotFound(ApiResponse.FailureResult("Branch not found."));
        }

        var package = await _context.Packages
            .FirstOrDefaultAsync(p => p.Id == packageId && p.CreatedByBranchId == branch.Id, cancellationToken);

        if (package == null)
        {
            return NotFound(ApiResponse.FailureResult("Custom branch package not found."));
        }

        var validServiceIds = await _context.Services
            .Where(s => request.ServiceIds.Contains(s.Id) && 
                        (s.CreatedByBranchId == null || s.CreatedByBranchId == branch.Id) && 
                        s.IsActive)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        if (validServiceIds.Count != request.ServiceIds.Distinct().Count())
        {
            return BadRequest(ApiResponse.FailureResult("Some service IDs are invalid, inactive, or not available to this branch."));
        }

        package.Name = request.Name.Trim();
        package.Description = request.Description?.Trim();
        package.BasePrice = request.CustomPrice;
        package.ServiceIds = request.ServiceIds.Distinct().ToList();

        _context.Packages.Update(package);

        var bp = await _context.BranchPackages
            .FirstOrDefaultAsync(link => link.BranchId == branch.Id && link.PackageId == packageId, cancellationToken);

        if (bp != null)
        {
            bp.CustomPrice = request.CustomPrice;
            _context.BranchPackages.Update(bp);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return Ok(ApiResponse<Package>.SuccessResult(package, "LAB_PACKAGE_UPDATED"));
    }

    [HttpDelete("lab/custom/{packageId}")]
    [EndpointSummary("Deactivate/delete a custom package owned by the branch")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
    public async Task<IActionResult> DeactivateLabCustomPackage([FromRoute] string packageId, CancellationToken cancellationToken)
    {
        var branch = await GetCurrentBranchAsync(cancellationToken);
        if (branch == null)
        {
            return NotFound(ApiResponse.FailureResult("Branch not found."));
        }

        var package = await _context.Packages
            .FirstOrDefaultAsync(p => p.Id == packageId && p.CreatedByBranchId == branch.Id, cancellationToken);

        if (package == null)
        {
            return NotFound(ApiResponse.FailureResult("Custom branch package not found."));
        }

        package.IsActive = false;
        _context.Packages.Update(package);

        var bp = await _context.BranchPackages
            .FirstOrDefaultAsync(link => link.BranchId == branch.Id && link.PackageId == packageId, cancellationToken);

        if (bp != null)
        {
            bp.IsActive = false;
            _context.BranchPackages.Update(bp);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return Ok(ApiResponse.SuccessResult("Custom package deactivated successfully."));
    }

    private async Task<Branch?> GetCurrentBranchAsync(CancellationToken cancellationToken)
    {
        var currentUserId = _currentUserService.UserId?.ToString();
        if (string.IsNullOrEmpty(currentUserId)) return null;

        var branch = await _context.Branches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);

        if (branch == null)
        {
            var user = await _context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == currentUserId, cancellationToken);

            if (user != null && user.Role == UserRole.Staff && !string.IsNullOrEmpty(user.LabId))
            {
                branch = await _context.Branches.AsNoTracking()
                    .FirstOrDefaultAsync(b => b.LabId == user.LabId, cancellationToken);
            }
        }

        return branch;
    }
}

public record CreatePackageRequest(
    string Name,
    string? Description,
    decimal BasePrice,
    decimal PlatformCommissionPct,
    List<string> ServiceIds
);

public record UpdatePackageOverrideRequest(
    decimal CustomPrice
);

public record CreateLabPackageRequest(
    string Name,
    string? Description,
    decimal CustomPrice,
    List<string> ServiceIds
);

public class BranchPackageDto
{
    public string PackageId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal BasePrice { get; set; }
    public decimal? CustomPrice { get; set; }
    public decimal PlatformCommissionPct { get; set; }
    public decimal? CustomCommissionPct { get; set; }
    public bool IsActive { get; set; }
    public bool IsAdminPackage { get; set; }
    public List<PackageServiceDetailDto> Services { get; set; } = new();
}

public class PackageServiceDetailDto
{
    public string ServiceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal BasePrice { get; set; }
    public decimal? CustomPrice { get; set; }
}
