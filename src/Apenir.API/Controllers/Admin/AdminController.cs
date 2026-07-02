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

        [HttpPost("invite-lab")]
        [EndpointSummary("Invite a Lab Branch")]
        [EndpointDescription("Creates a new Lab User and an associated Branch with 'invited' status.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> InviteLab([FromBody] InviteLabRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                return BadRequest(ApiResponse.FailureResult("Invalid request payload."));
            }

            if (string.IsNullOrWhiteSpace(request.Email))
            {
                return BadRequest(ApiResponse.FailureResult("Email is required."));
            }

            if (string.IsNullOrWhiteSpace(request.LabName))
            {
                return BadRequest(ApiResponse.FailureResult("Lab Name is required."));
            }

            var lowercaseEmail = request.Email.Trim().ToLower();

            // Check if user already exists
            var userExists = await _context.Users.AnyAsync(u => u.Email != null && u.Email.ToLower() == lowercaseEmail && !u.IsDeleted, cancellationToken);
            if (userExists)
            {
                return BadRequest(ApiResponse.FailureResult("A user with this email already exists."));
            }

            var userId = Guid.NewGuid().ToString();
            var branchId = Guid.NewGuid().ToString();
            var adminId = _currentUserService.UserId?.ToString() ?? "system";

            var user = new User
            {
                Id = userId,
                Name = request.LabName,
                Email = request.Email.Trim(),
                Role = UserRole.Lab,
                IsActive = true,
                IsDeleted = false,
                Status = "invited",
                CreatedAt = DateTime.UtcNow,
                Permissions = new List<string>()
            };

            var branch = new Branch
            {
                Id = branchId,
                LabUserId = userId,
                Name = request.LabName,
                District = "Pending",
                City = "Pending",
                Pincode = "Pending",
                Phone = "Pending",
                Latitude = 0m,
                Longitude = 0m,
                IsActive = true,
                Status = "invited",
                CreatedBy = adminId,
                CreatedAt = DateTime.UtcNow
            };

            _context.Users.Add(user);
            _context.Branches.Add(branch);

            await _context.SaveChangesAsync(cancellationToken);

            return Ok(ApiResponse.SuccessResult("Lab invited successfully."));
        }

        [HttpGet("check-email")]
        [EndpointSummary("Check if Email Exists")]
        [EndpointDescription("Checks whether a given email already exists in the users database.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<bool>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse<bool>))]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> CheckEmail([FromQuery] string email, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return BadRequest(ApiResponse<bool>.FailureResult("Email parameter is required."));
            }

            var lowercaseEmail = email.Trim().ToLower();
            var exists = await _context.Users.AnyAsync(u => u.Email != null && u.Email.ToLower() == lowercaseEmail && !u.IsDeleted, cancellationToken);

            return Ok(ApiResponse<bool>.SuccessResult(exists, exists ? "EMAIL_EXISTS" : "EMAIL_NOT_FOUND"));
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
