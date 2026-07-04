using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Apenir.Core.Entities;
using Apenir.Core.Enums;
using Apenir.Core.Interfaces;
using Apenir.API.Filters;
using Apenir.Application.Common.Interfaces;
using Apenir.Application.Common.Models;

namespace Apenir.API.Controllers.Admin
{
    [ApiController]
    [Route("api/admin")]
    [Authorize]
    [AdminOnly]
    public class AdminController : ControllerBase
    {
        private readonly IApplicationDbContext _context;
        private readonly ICurrentUserService _currentUserService;

        public AdminController(IApplicationDbContext context, ICurrentUserService currentUserService)
        {
            _context = context;
            _currentUserService = currentUserService;
        }

        [HttpPost("branches")]
        [EndpointSummary("Search and Filter Lab Branches")]
        [EndpointDescription("Returns a list of lab branches matching optional name, district, city, and status filter criteria.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<Branch>>))]
        public async Task<IActionResult> GetBranches([FromBody] SearchBranchesRequest? request, CancellationToken cancellationToken)
        {
            var query = _context.Branches.AsNoTracking();

            if (request != null)
            {
                if (!string.IsNullOrWhiteSpace(request.Name))
                {
                    var nameQuery = request.Name.Trim().ToLower();
                    query = query.Where(b => b.Name != null && b.Name.ToLower().Contains(nameQuery));
                }

                if (!string.IsNullOrWhiteSpace(request.District))
                {
                    var districtQuery = request.District.Trim().ToLower();
                    query = query.Where(b => b.District != null && b.District.ToLower() == districtQuery);
                }

                if (!string.IsNullOrWhiteSpace(request.City))
                {
                    var cityQuery = request.City.Trim().ToLower();
                    query = query.Where(b => b.City != null && b.City.ToLower() == cityQuery);
                }

                if (!string.IsNullOrWhiteSpace(request.Status))
                {
                    var statusQuery = request.Status.Trim().ToLower();
                    query = query.Where(b => b.Status != null && b.Status.ToLower() == statusQuery);
                }
            }

            var branches = await query.ToListAsync(cancellationToken);
            return Ok(ApiResponse<List<Branch>>.SuccessResult(branches, "BRANCHES_RETRIEVED"));
        }

        [HttpPost("users/customers")]
        [EndpointSummary("Search and Filter Customers")]
        [EndpointDescription("Returns a list of customers matching optional name, email, and status filter criteria.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<User>>))]
        public async Task<IActionResult> GetCustomers([FromBody] SearchUsersRequest? request, CancellationToken cancellationToken)
        {
            return await GetUsersByRole(UserRole.Customer, request, cancellationToken);
        }

        [HttpPost("users/staff")]
        [EndpointSummary("Search and Filter Staff")]
        [EndpointDescription("Returns a list of staff members matching optional name, email, and status filter criteria.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<User>>))]
        public async Task<IActionResult> GetStaff([FromBody] SearchUsersRequest? request, CancellationToken cancellationToken)
        {
            return await GetUsersByRole(UserRole.Staff, request, cancellationToken);
        }

        [HttpPost("users/labs")]
        [EndpointSummary("Search and Filter Lab Users")]
        [EndpointDescription("Returns a list of lab manager users matching optional name, email, and status filter criteria.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<User>>))]
        public async Task<IActionResult> GetLabs([FromBody] SearchUsersRequest? request, CancellationToken cancellationToken)
        {
            return await GetUsersByRole(UserRole.Lab, request, cancellationToken);
        }

        private async Task<IActionResult> GetUsersByRole(UserRole role, SearchUsersRequest? request, CancellationToken cancellationToken)
        {
            var query = _context.Users.AsNoTracking().Where(u => u.Role == role && !u.IsDeleted);

            if (request != null)
            {
                if (!string.IsNullOrWhiteSpace(request.Name))
                {
                    var nameQuery = request.Name.Trim().ToLower();
                    query = query.Where(u => u.Name != null && u.Name.ToLower().Contains(nameQuery));
                }

                if (!string.IsNullOrWhiteSpace(request.Email))
                {
                    var emailQuery = request.Email.Trim().ToLower();
                    query = query.Where(u => u.Email != null && u.Email.ToLower().Contains(emailQuery));
                }

                if (!string.IsNullOrWhiteSpace(request.Status))
                {
                    var statusQuery = request.Status.Trim().ToLower();
                    query = query.Where(u => u.Status != null && u.Status.ToLower() == statusQuery);
                }
            }

            var users = await query.ToListAsync(cancellationToken);
            return Ok(ApiResponse<List<User>>.SuccessResult(users, "USERS_RETRIEVED"));
        }

        [HttpPut("branches/{branchId}/services/{serviceId}/commission")]
        [EndpointSummary("Update custom commission percentage for a specific branch service")]
        [EndpointDescription("Allows administrators to customize or increase the commission/interest percentage for a specific branch service.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> UpdateBranchServiceCommission(
            [FromRoute] string branchId,
            [FromRoute] string serviceId,
            [FromBody] UpdateBranchCommissionRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null || request.CommissionPct < 0 || request.CommissionPct > 100)
            {
                return BadRequest(ApiResponse.FailureResult("Invalid commission percentage. Must be between 0 and 100."));
            }

            var branchExists = await _context.Branches.AnyAsync(b => b.Id == branchId, cancellationToken);
            if (!branchExists)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            var branchService = await _context.BranchServices
                .FirstOrDefaultAsync(bs => bs.BranchId == branchId && bs.ServiceId == serviceId, cancellationToken);

            if (branchService == null)
            {
                // Verify master service exists before creating an override
                var serviceExists = await _context.Services.AnyAsync(s => s.Id == serviceId, cancellationToken);
                if (!serviceExists)
                {
                    return NotFound(ApiResponse.FailureResult("Master service not found."));
                }

                branchService = new BranchService
                {
                    Id = Guid.NewGuid().ToString(),
                    BranchId = branchId,
                    ServiceId = serviceId,
                    CustomPrice = null, // Follow admin base price by default
                    CustomCommissionPct = request.CommissionPct,
                    IsActive = true // Default to active when admin configures it
                };
                _context.BranchServices.Add(branchService);
            }
            else
            {
                branchService.CustomCommissionPct = request.CommissionPct;
                _context.BranchServices.Update(branchService);
            }

            await _context.SaveChangesAsync(cancellationToken);
            return Ok(ApiResponse.SuccessResult("Branch service commission updated successfully."));
        }
    }

    public record UpdateBranchCommissionRequest(
        decimal CommissionPct
    );

    public record InviteLabRequest(
        string Email,
        string LabName
    );

    public record SearchBranchesRequest(
        string? Name,
        string? District,
        string? City,
        string? Status
    );

    public record SearchUsersRequest(
        string? Name,
        string? Email,
        string? Status
    );
}
