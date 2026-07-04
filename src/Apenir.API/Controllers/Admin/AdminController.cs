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
    }

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
