using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Apenir.Core.Entities;
using Apenir.Core.Enums;
using Apenir.Core.Interfaces;
using Apenir.Application.Common.Interfaces;
using Apenir.Application.Common.Models;
using Apenir.Application.DTOs;
using Apenir.API.Helpers;

namespace Apenir.API.Controllers
{
    [ApiController]
    [Route("api/lab")]
    public class LabController : ControllerBase
    {
        private readonly IApplicationDbContext _context;
        private readonly IPasswordHasher _passwordHasher;
        private readonly IJwtTokenService _jwtTokenService;
        private readonly IRefreshTokenRepository _refreshTokenRepository;
        private readonly ICurrentUserService _currentUserService;
        private readonly JwtSettings _jwtSettings;

        public LabController(
            IApplicationDbContext context,
            IPasswordHasher passwordHasher,
            IJwtTokenService jwtTokenService,
            IRefreshTokenRepository refreshTokenRepository,
            ICurrentUserService currentUserService,
            IOptions<JwtSettings> jwtSettings)
        {
            _context = context;
            _passwordHasher = passwordHasher;
            _jwtTokenService = jwtTokenService;
            _refreshTokenRepository = refreshTokenRepository;
            _currentUserService = currentUserService;
            _jwtSettings = jwtSettings.Value;
        }

        [HttpPost("login")]
        [AllowAnonymous]
        [EndpointSummary("Lab Portal Login")]
        [EndpointDescription("Authenticates a lab user with email and password, checking that their account status is Active.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<LoginResponse>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ApiResponse))]
        public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            {
                return BadRequest(ApiResponse.FailureResult("Email and Password are required."));
            }

            var lowercaseEmail = request.Email.Trim().ToLower();
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == lowercaseEmail && !u.IsDeleted && u.Role == UserRole.Lab, cancellationToken);

            if (user == null)
            {
                return Unauthorized(ApiResponse.FailureResult("Invalid credentials."));
            }

            // Verify Password
            if (string.IsNullOrWhiteSpace(user.PasswordHash) || !_passwordHasher.Verify(request.Password, user.PasswordHash))
            {
                return Unauthorized(ApiResponse.FailureResult("Invalid credentials."));
            }

            // Check Status is Active
            if (user.Status != "Active" || user.IsActive != true)
            {
                return Unauthorized(ApiResponse.FailureResult("Your account is not active. Please complete registration via the invitation email or contact your administrator."));
            }

            var accessToken = _jwtTokenService.GenerateAccessToken(user);
            var refreshTokenString = _jwtTokenService.GenerateRefreshToken();

            var refreshToken = new RefreshToken
            {
                Id = Guid.NewGuid(),
                Token = refreshTokenString,
                UserId = user.Id,
                ExpiresAt = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpiryDays),
                CreatedAt = DateTime.UtcNow,
                CreatedByIp = _currentUserService.IpAddress ?? "unknown",
                DeviceName = _currentUserService.UserAgent,
                UserAgent = _currentUserService.UserAgent,
                IpAddress = _currentUserService.IpAddress
            };

            await _refreshTokenRepository.AddAsync(refreshToken, cancellationToken);

            user.LastLoginAt = DateTime.UtcNow;
            _context.Users.Update(user);
            await _context.SaveChangesAsync(cancellationToken);

            // Set secure cookie
            CookieHelper.SetRefreshTokenCookie(HttpContext, refreshTokenString, "/api/auth/refresh");

            var response = new LoginResponse
            {
                AccessToken = accessToken,
                RefreshToken = string.Empty, // Hide from response body
                ExpiresIn = _jwtSettings.AccessTokenExpiryMinutes * 60,
                AdminId = user.Id,
                Email = user.Email ?? string.Empty
            };

            return Ok(ApiResponse<LoginResponse>.SuccessResult(response, "Login successful"));
        }

        [HttpGet("branches/{branchId}/staff")]
        [Authorize]
        [EndpointSummary("Get all staff associated with a branch")]
        [EndpointDescription("Returns a list of staff members (users with role Staff) who have been assigned to appointments in the specified branch.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<User>>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetBranchStaff([FromRoute] string branchId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(branchId))
            {
                return BadRequest(ApiResponse.FailureResult("Branch ID is required."));
            }

            var branchExists = await _context.Branches.AnyAsync(b => b.Id == branchId, cancellationToken);
            if (!branchExists)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            var staff = await _context.Appointments
                .Where(a => a.BranchId == branchId && a.AssignedStaffId != null)
                .Select(a => a.AssignedStaff!)
                .Where(s => !s.IsDeleted)
                .Distinct()
                .ToListAsync(cancellationToken);

            return Ok(ApiResponse<List<User>>.SuccessResult(staff, "Branch staff retrieved successfully."));
        }

        [HttpGet("branches/{branchId}/appointments")]
        [Authorize]
        [EndpointSummary("Get all appointments for a branch")]
        [EndpointDescription("Returns a list of all appointments scheduled for the specified branch, including customer and assigned staff details.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<Appointment>>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetBranchAppointments([FromRoute] string branchId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(branchId))
            {
                return BadRequest(ApiResponse.FailureResult("Branch ID is required."));
            }

            var branchExists = await _context.Branches.AnyAsync(b => b.Id == branchId, cancellationToken);
            if (!branchExists)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            var appointments = await _context.Appointments
                .Where(a => a.BranchId == branchId)
                .Include(a => a.CustomerUser)
                .Include(a => a.AssignedStaff)
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync(cancellationToken);

            return Ok(ApiResponse<List<Appointment>>.SuccessResult(appointments, "Branch appointments retrieved successfully."));
        }

        [HttpGet("branches/{branchId}/details")]
        [Authorize]
        [EndpointSummary("Get branch details and metrics")]
        [EndpointDescription("Returns branch details, lab user info, and general statistics like total revenue and appointment count.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<BranchDetailsResponse>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetBranchDetails([FromRoute] string branchId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(branchId))
            {
                return BadRequest(ApiResponse.FailureResult("Branch ID is required."));
            }

            var branch = await _context.Branches
                .Include(b => b.LabUser)
                .FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            var appointments = await _context.Appointments
                .Where(a => a.BranchId == branchId)
                .ToListAsync(cancellationToken);

            var totalAppointments = appointments.Count;
            var completedAppointments = appointments.Count(a => a.Status == AppointmentStatus.Completed);
            var pendingAppointments = appointments.Count(a => a.Status == AppointmentStatus.Pending);
            var totalRevenue = appointments.Sum(a => a.TotalAmount);
            var totalLabPayout = appointments.Sum(a => a.LabPayout);

            var staffCount = appointments
                .Where(a => a.AssignedStaffId != null)
                .Select(a => a.AssignedStaffId)
                .Distinct()
                .Count();

            var servicesCount = await _context.BranchServices
                .CountAsync(bs => bs.BranchId == branchId && bs.IsActive, cancellationToken);

            var activeSlotsCount = await _context.AppointmentSlots
                .CountAsync(s => s.BranchId == branchId && s.IsAvailable, cancellationToken);

            var response = new BranchDetailsResponse
            {
                Branch = branch,
                LabUser = branch.LabUser,
                Stats = new BranchStats
                {
                    TotalAppointments = totalAppointments,
                    CompletedAppointments = completedAppointments,
                    PendingAppointments = pendingAppointments,
                    TotalRevenue = totalRevenue,
                    TotalLabPayout = totalLabPayout,
                    TotalStaffCount = staffCount,
                    TotalServicesCount = servicesCount,
                    ActiveSlotsCount = activeSlotsCount
                }
            };

            return Ok(ApiResponse<BranchDetailsResponse>.SuccessResult(response, "Branch details retrieved successfully."));
        }
    }

    public class BranchDetailsResponse
    {
        public Branch Branch { get; set; } = null!;
        public User? LabUser { get; set; }
        public BranchStats Stats { get; set; } = new();
    }

    public class BranchStats
    {
        public int TotalAppointments { get; set; }
        public int CompletedAppointments { get; set; }
        public int PendingAppointments { get; set; }
        public decimal TotalRevenue { get; set; }
        public decimal TotalLabPayout { get; set; }
        public int TotalStaffCount { get; set; }
        public int TotalServicesCount { get; set; }
        public int ActiveSlotsCount { get; set; }
    }
}
