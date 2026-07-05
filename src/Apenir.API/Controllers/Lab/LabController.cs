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
        private readonly IEmailService _emailService;
        private readonly IWhatsAppService _whatsAppService;
        private readonly JwtSettings _jwtSettings;

        public LabController(
            IApplicationDbContext context,
            IPasswordHasher passwordHasher,
            IJwtTokenService jwtTokenService,
            IRefreshTokenRepository refreshTokenRepository,
            ICurrentUserService currentUserService,
            IEmailService emailService,
            IWhatsAppService whatsAppService,
            IOptions<JwtSettings> jwtSettings)
        {
            _context = context;
            _passwordHasher = passwordHasher;
            _jwtTokenService = jwtTokenService;
            _refreshTokenRepository = refreshTokenRepository;
            _currentUserService = currentUserService;
            _emailService = emailService;
            _whatsAppService = whatsAppService;
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
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == lowercaseEmail && !u.IsDeleted && (u.Role == UserRole.Lab || u.Role == UserRole.Staff), cancellationToken);

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

            // Retrieve associated Branch details
            Branch? userBranch = null;
            if (user.Role == UserRole.Lab)
            {
                userBranch = await _context.Branches.FirstOrDefaultAsync(b => b.LabUserId == user.Id, cancellationToken);
            }
            else if (user.Role == UserRole.Staff && !string.IsNullOrEmpty(user.LabId))
            {
                userBranch = await _context.Branches.FirstOrDefaultAsync(b => b.LabId == user.LabId, cancellationToken);
            }

            var response = new LoginResponse
            {
                AccessToken = accessToken,
                RefreshToken = string.Empty, // Hide from response body
                ExpiresIn = _jwtSettings.AccessTokenExpiryMinutes * 60,
                AdminId = user.Id,
                Email = user.Email ?? string.Empty,
                BranchId = userBranch?.Id,
                LabId = userBranch?.LabId
            };

            return Ok(ApiResponse<LoginResponse>.SuccessResult(response, "Login successful"));
        }

        [HttpGet("staff")]
        [Authorize]
        [EndpointSummary("Get all staff associated with the logged-in lab")]
        [EndpointDescription("Returns a list of all staff members (users with role Staff) registered under the logged-in lab.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<User>>))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetBranchStaff(CancellationToken cancellationToken)
        {
            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            var staff = await _context.Users
                .Where(u => u.Role == UserRole.Staff && u.LabId == branch.LabId && !u.IsDeleted)
                .ToListAsync(cancellationToken);

            return Ok(ApiResponse<List<User>>.SuccessResult(staff, "Branch staff retrieved successfully."));
        }

        [HttpGet("appointments")]
        [Authorize]
        [EndpointSummary("Get all appointments for the logged-in lab")]
        [EndpointDescription("Returns a list of all appointments scheduled for the logged-in lab, including customer and assigned staff details.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<Appointment>>))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetBranchAppointments(CancellationToken cancellationToken)
        {
            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            var appointments = await _context.Appointments
                .Where(a => a.BranchId == branch.Id)
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync(cancellationToken);

            var customerIds = appointments.Select(a => a.CustomerUserId).Distinct().ToList();
            var staffIds = appointments.Where(a => a.AssignedStaffId != null).Select(a => a.AssignedStaffId!).Distinct().ToList();

            var customers = await _context.Users.Where(u => customerIds.Contains(u.Id)).ToListAsync(cancellationToken);
            var staff = await _context.Users.Where(u => staffIds.Contains(u.Id)).ToListAsync(cancellationToken);

            foreach (var app in appointments)
            {
                app.CustomerUser = customers.FirstOrDefault(c => c.Id == app.CustomerUserId);
                app.AssignedStaff = staff.FirstOrDefault(s => s.Id == app.AssignedStaffId);
                app.Branch = branch;
            }

            return Ok(ApiResponse<List<Appointment>>.SuccessResult(appointments, "Branch appointments retrieved successfully."));
        }

        [HttpGet("details")]
        [Authorize]
        [EndpointSummary("Get logged-in lab details and metrics")]
        [EndpointDescription("Returns branch details, lab user info, and general statistics like total revenue and appointment count.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<BranchDetailsResponse>))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetBranchDetails(CancellationToken cancellationToken)
        {
            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches
                .FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            branch.LabUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == branch.LabUserId, cancellationToken);

            var appointments = await _context.Appointments
                .Where(a => a.BranchId == branch.Id)
                .ToListAsync(cancellationToken);

            var totalAppointments = appointments.Count;
            var completedAppointments = appointments.Count(a => a.Status == AppointmentStatus.Completed);
            var pendingAppointments = appointments.Count(a => a.Status == AppointmentStatus.Pending);
            var totalRevenue = appointments.Where(a => a.Status != AppointmentStatus.Cancelled).Sum(a => a.TotalAmount);
            var totalLabPayout = appointments.Where(a => a.Status != AppointmentStatus.Cancelled).Sum(a => a.LabPayout);

            var staffCount = await _context.Users
                .CountAsync(u => u.Role == UserRole.Staff && u.LabId == branch.LabId && !u.IsDeleted, cancellationToken);

            var servicesCount = await _context.BranchServices
                .CountAsync(bs => bs.BranchId == branch.Id && bs.IsActive, cancellationToken);

            var activeSlotsCount = await _context.AppointmentSlots
                .CountAsync(s => s.BranchId == branch.Id && s.IsAvailable, cancellationToken);

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

        [HttpGet("dashboard/summary")]
        [Authorize]
        [EndpointSummary("Get dashboard summary metrics and slots")]
        [EndpointDescription("Returns KPI metrics, Daily Slot Calendar data, and active stats for a branch within a date range.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<LabDashboardSummaryResponse>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetDashboardSummary(
            [FromQuery] string? branchId,
            [FromQuery] DateOnly? startDate,
            [FromQuery] DateOnly? endDate,
            CancellationToken cancellationToken)
        {
            var currentUserId = _currentUserService.UserId?.ToString();
            Branch? branch = null;

            if (!string.IsNullOrWhiteSpace(branchId))
            {
                branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);
                if (branch != null && branch.LabUserId != currentUserId)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.FailureResult("Access denied to this branch."));
                }
            }
            else
            {
                branch = await _context.Branches.FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);
            }

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            var targetBranchId = branch.Id;
            await GenerateSlotsForNext7Days(targetBranchId, _context, cancellationToken);

            // Default date range: today to next 7 days
            var start = startDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var end = endDate ?? start.AddDays(7);

            var slotIds = await _context.AppointmentSlots
                .Where(s => s.BranchId == targetBranchId && s.SlotDate >= start && s.SlotDate <= end)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);

            var appointments = await _context.Appointments
                .Where(a => a.BranchId == targetBranchId && slotIds.Contains(a.AppointmentSlotId))
                .ToListAsync(cancellationToken);

            var customerIds = appointments.Select(a => a.CustomerUserId).Distinct().ToList();
            var staffIds = appointments.Where(a => a.AssignedStaffId != null).Select(a => a.AssignedStaffId!).Distinct().ToList();
            var appSlotIds = appointments.Select(a => a.AppointmentSlotId).Distinct().ToList();

            var customers = await _context.Users.Where(u => customerIds.Contains(u.Id)).ToListAsync(cancellationToken);
            var staff = await _context.Users.Where(u => staffIds.Contains(u.Id)).ToListAsync(cancellationToken);
            var appSlots = await _context.AppointmentSlots.Where(s => appSlotIds.Contains(s.Id)).ToListAsync(cancellationToken);

            foreach (var app in appointments)
            {
                app.CustomerUser = customers.FirstOrDefault(c => c.Id == app.CustomerUserId);
                app.AssignedStaff = staff.FirstOrDefault(s => s.Id == app.AssignedStaffId);
                app.AppointmentSlot = appSlots.FirstOrDefault(s => s.Id == app.AppointmentSlotId);
            }

            var totalBookings = appointments.Count;
            var assignedCount = appointments.Count(a => a.Status == AppointmentStatus.Assigned);
            var collectedCount = appointments.Count(a => a.Status == AppointmentStatus.Collected);
            var completedCount = appointments.Count(a => a.Status == AppointmentStatus.Completed);
            var pendingCount = appointments.Count(a => a.Status == AppointmentStatus.Pending);
            var confirmedCount = appointments.Count(a => a.Status == AppointmentStatus.Confirmed);
            var cancelledCount = appointments.Count(a => a.Status == AppointmentStatus.Cancelled);

            var grossRevenue = appointments.Where(a => a.Status != AppointmentStatus.Cancelled).Sum(a => a.TotalAmount);
            var netPayout = appointments.Where(a => a.Status != AppointmentStatus.Cancelled).Sum(a => a.LabPayout);

            var slots = await _context.AppointmentSlots
                .Where(s => s.BranchId == targetBranchId && s.SlotDate >= start && s.SlotDate <= end)
                .OrderBy(s => s.SlotDate)
                .ThenBy(s => s.StartTime)
                .ToListAsync(cancellationToken);

            var response = new LabDashboardSummaryResponse
            {
                TodayBookingsCount = appointments.Count(a => a.AppointmentSlot != null && a.AppointmentSlot.SlotDate == DateOnly.FromDateTime(DateTime.UtcNow)),
                Funnel = new LabDashboardFunnel
                {
                    PendingCount = pendingCount,
                    ConfirmedCount = confirmedCount,
                    AssignedCount = assignedCount,
                    CollectedCount = collectedCount,
                    CompletedCount = completedCount,
                    CancelledCount = cancelledCount
                },
                Financials = new LabDashboardFinancials
                {
                    GrossRevenue = grossRevenue,
                    NetPayout = netPayout
                },
                Slots = slots,
                Appointments = appointments
            };

            return Ok(ApiResponse<LabDashboardSummaryResponse>.SuccessResult(response, "Dashboard summary retrieved successfully."));
        }

        [HttpGet("appointments/pending-assignment")]
        [Authorize]
        [EndpointSummary("Get all appointments pending staff assignment")]
        [EndpointDescription("Returns appointments for a branch where AssignedStaffId is null and status is Pending or Confirmed.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<Appointment>>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetPendingAssignments([FromQuery] string? branchId, CancellationToken cancellationToken)
        {
            var currentUserId = _currentUserService.UserId?.ToString();
            Branch? branch = null;

            if (!string.IsNullOrWhiteSpace(branchId))
            {
                branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);
                if (branch != null && branch.LabUserId != currentUserId)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.FailureResult("Access denied to this branch."));
                }
            }
            else
            {
                branch = await _context.Branches.FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);
            }

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            var pending = await _context.Appointments
                .Where(a => a.BranchId == branch.Id && a.AssignedStaffId == null && 
                            (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed))
                .OrderBy(a => a.CreatedAt)
                .ToListAsync(cancellationToken);

            var customerIds = pending.Select(a => a.CustomerUserId).Distinct().ToList();
            var appSlotIds = pending.Select(a => a.AppointmentSlotId).Distinct().ToList();

            var customers = await _context.Users.Where(u => customerIds.Contains(u.Id)).ToListAsync(cancellationToken);
            var appSlots = await _context.AppointmentSlots.Where(s => appSlotIds.Contains(s.Id)).ToListAsync(cancellationToken);

            foreach (var app in pending)
            {
                app.CustomerUser = customers.FirstOrDefault(c => c.Id == app.CustomerUserId);
                app.AppointmentSlot = appSlots.FirstOrDefault(s => s.Id == app.AppointmentSlotId);
            }

            return Ok(ApiResponse<List<Appointment>>.SuccessResult(pending, "Pending staff assignments retrieved successfully."));
        }

        [HttpPost("appointments/{id}/assign-staff")]
        [Authorize]
        [EndpointSummary("Assign a phlebotomist/staff member to an appointment")]
        [EndpointDescription("Assigns a staff member to a branch appointment and transitions the status to Assigned.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> AssignStaff(
            [FromRoute] string id,
            [FromBody] AssignStaffRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.StaffId))
            {
                return BadRequest(ApiResponse.FailureResult("Staff ID is required."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();
            var appointment = await _context.Appointments
                .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

            if (appointment == null)
            {
                return NotFound(ApiResponse.FailureResult("Appointment not found."));
            }

            appointment.Branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == appointment.BranchId, cancellationToken);

            if (appointment.Branch == null || appointment.Branch.LabUserId != currentUserId)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.FailureResult("Access denied to this branch's appointments."));
            }

            var staffExists = await _context.Users.AnyAsync(u => u.Id == request.StaffId && u.Role == UserRole.Staff && !u.IsDeleted, cancellationToken);
            if (!staffExists)
            {
                return BadRequest(ApiResponse.FailureResult("Invalid or non-existent staff member selected."));
            }

            appointment.AssignedStaffId = request.StaffId;
            appointment.Status = AppointmentStatus.Assigned;
            appointment.UpdatedAt = DateTime.UtcNow;

            _context.Appointments.Update(appointment);
            await _context.SaveChangesAsync(cancellationToken);

            return Ok(ApiResponse.SuccessResult("Staff member assigned to appointment successfully."));
        }

        [HttpGet("services")]
        [Authorize]
        [EndpointSummary("Get all diagnostic services active or available for the logged-in lab")]
        [EndpointDescription("Returns all branch-specific services merged with the master service metadata.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<BranchServiceDto>>))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetBranchServices(CancellationToken cancellationToken)
        {
            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            var allServices = await _context.Services
                .Where(s => s.IsActive && (s.CreatedByBranchId == null || s.CreatedByBranchId == branch.Id))
                .ToListAsync(cancellationToken);

            var branchServices = await _context.BranchServices
                .Where(bs => bs.BranchId == branch.Id)
                .ToListAsync(cancellationToken);

            var result = allServices.Select(s => {
                var overrideBs = branchServices.FirstOrDefault(bs => bs.ServiceId == s.Id);
                return new BranchServiceDto
                {
                    BranchServiceId = overrideBs?.Id ?? string.Empty,
                    ServiceId = s.Id,
                    Name = s.Name,
                    Category = s.Category,
                    Description = s.Description ?? string.Empty,
                    BasePrice = s.BasePrice,
                    CustomPrice = overrideBs?.CustomPrice,
                    CustomCommissionPct = overrideBs?.CustomCommissionPct,
                    IsActive = overrideBs?.IsActive ?? false
                };
            }).ToList();

            return Ok(ApiResponse<List<BranchServiceDto>>.SuccessResult(result, "Branch services retrieved successfully."));
        }

        [HttpPut("services/{serviceId}")]
        [Authorize]
        [EndpointSummary("Update custom pricing and active status for a branch service")]
        [EndpointDescription("Overrides pricing or toggles the status of a specific service for the branch.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> UpdateBranchService(
            [FromRoute] string serviceId,
            [FromBody] UpdateBranchServiceRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                return BadRequest(ApiResponse.FailureResult("Request body is required."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            var branchService = await _context.BranchServices
                .FirstOrDefaultAsync(bs => bs.BranchId == branch.Id && bs.ServiceId == serviceId, cancellationToken);

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
                    BranchId = branch.Id,
                    ServiceId = serviceId,
                    CustomPrice = request.CustomPrice,
                    IsActive = request.IsActive
                };
                _context.BranchServices.Add(branchService);
            }
            else
            {
                branchService.CustomPrice = request.CustomPrice;
                branchService.IsActive = request.IsActive;
                _context.BranchServices.Update(branchService);
            }

            await _context.SaveChangesAsync(cancellationToken);
            return Ok(ApiResponse.SuccessResult("Branch service updated successfully."));
        }

        [HttpPost("services")]
        [Authorize]
        [EndpointSummary("Create a new private service for the logged-in branch")]
        [EndpointDescription("Creates a service record owned by this branch, then automatically adds a branch service override entry.")]
        public async Task<IActionResult> CreateBranchCustomService(
            [FromBody] CreateBranchCustomServiceRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Category))
            {
                return BadRequest(ApiResponse.FailureResult("Name and Category are required."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            // Create the Master Service record
            var service = new Service
            {
                Id = Guid.NewGuid().ToString(),
                Name = request.Name.Trim(),
                Category = request.Category.Trim(),
                Description = request.Description?.Trim(),
                BasePrice = request.BasePrice,
                PlatformCommissionPct = 15.00m, // Follow admin policy data by default
                IsActive = true,
                CreatedByBranchId = branch.Id,
                CreatedAt = DateTime.UtcNow
            };

            // Create the BranchService override link
            var branchService = new BranchService
            {
                Id = Guid.NewGuid().ToString(),
                BranchId = branch.Id,
                ServiceId = service.Id,
                CustomPrice = request.CustomPrice, // If provided, overrides base price, otherwise remains null (follow admin base price)
                CustomCommissionPct = null, // Follow admin commission by default
                IsActive = true
            };

            _context.Services.Add(service);
            _context.BranchServices.Add(branchService);
            await _context.SaveChangesAsync(cancellationToken);

            var dto = new BranchServiceDto
            {
                BranchServiceId = branchService.Id,
                ServiceId = service.Id,
                Name = service.Name,
                Category = service.Category,
                Description = service.Description ?? string.Empty,
                BasePrice = service.BasePrice,
                CustomPrice = branchService.CustomPrice,
                CustomCommissionPct = branchService.CustomCommissionPct,
                IsActive = branchService.IsActive
            };

            return Ok(ApiResponse<BranchServiceDto>.SuccessResult(dto, "Custom service created successfully."));
        }

        [HttpGet("slots/configurations")]
        [Authorize]
        [EndpointSummary("Get all slot configurations for the logged-in branch")]
        public async Task<IActionResult> GetSlotConfigurations(CancellationToken cancellationToken)
        {
            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);
            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            var configs = await _context.BranchSlotConfigurations
                .Where(c => c.BranchId == branch.Id)
                .ToListAsync(cancellationToken);

            return Ok(ApiResponse<List<BranchSlotConfiguration>>.SuccessResult(configs, "Slot configurations retrieved successfully."));
        }

        [HttpPost("slots/configurations")]
        [Authorize]
        [EndpointSummary("Create a slot configuration for the logged-in branch")]
        public async Task<IActionResult> CreateSlotConfiguration([FromBody] CreateSlotConfigurationRequest request, CancellationToken cancellationToken)
        {
            if (request == null) return BadRequest(ApiResponse.FailureResult("Request body is required."));
            if (!TimeOnly.TryParse(request.StartTime, out var startTime) || !TimeOnly.TryParse(request.EndTime, out var endTime))
            {
                return BadRequest(ApiResponse.FailureResult("Invalid start or end time format. Use HH:mm format."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);
            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            var config = new BranchSlotConfiguration
            {
                Id = Guid.NewGuid().ToString(),
                BranchId = branch.Id,
                DayText = request.DayText,
                StartTime = startTime,
                EndTime = endTime,
                MaxCapacity = request.MaxCapacity,
                IsLeave = request.IsLeave
            };

            _context.BranchSlotConfigurations.Add(config);
            await _context.SaveChangesAsync(cancellationToken);

            return Ok(ApiResponse<BranchSlotConfiguration>.SuccessResult(config, "Slot configuration created successfully."));
        }

        [HttpPut("slots/configurations/{id}")]
        [Authorize]
        [EndpointSummary("Update an existing slot configuration")]
        public async Task<IActionResult> UpdateSlotConfiguration([FromRoute] string id, [FromBody] UpdateSlotConfigurationRequest request, CancellationToken cancellationToken)
        {
            if (request == null) return BadRequest(ApiResponse.FailureResult("Request body is required."));
            if (!TimeOnly.TryParse(request.StartTime, out var startTime) || !TimeOnly.TryParse(request.EndTime, out var endTime))
            {
                return BadRequest(ApiResponse.FailureResult("Invalid start or end time format. Use HH:mm format."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);
            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            var config = await _context.BranchSlotConfigurations
                .FirstOrDefaultAsync(c => c.Id == id && c.BranchId == branch.Id, cancellationToken);

            if (config == null)
            {
                return NotFound(ApiResponse.FailureResult("Slot configuration not found."));
            }

            config.DayText = request.DayText;
            config.StartTime = startTime;
            config.EndTime = endTime;
            config.MaxCapacity = request.MaxCapacity;
            config.IsLeave = request.IsLeave;

            _context.BranchSlotConfigurations.Update(config);
            await _context.SaveChangesAsync(cancellationToken);

            return Ok(ApiResponse.SuccessResult("Slot configuration updated successfully."));
        }

        [HttpDelete("slots/configurations/{id}")]
        [Authorize]
        [EndpointSummary("Delete a slot configuration")]
        public async Task<IActionResult> DeleteSlotConfiguration([FromRoute] string id, CancellationToken cancellationToken)
        {
            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);
            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            var config = await _context.BranchSlotConfigurations
                .FirstOrDefaultAsync(c => c.Id == id && c.BranchId == branch.Id, cancellationToken);

            if (config == null)
            {
                return NotFound(ApiResponse.FailureResult("Slot configuration not found."));
            }

            _context.BranchSlotConfigurations.Remove(config);
            await _context.SaveChangesAsync(cancellationToken);

            return Ok(ApiResponse.SuccessResult("Slot configuration deleted successfully."));
        }

        [HttpPost("staff/invite")]
        [Authorize]
        [EndpointSummary("Invite Staff member")]
        [EndpointDescription("Registers a pending staff user with 'notverified' status and triggers an SMTP verification email.")]
        public async Task<IActionResult> InviteStaff([FromBody] InviteStaffRequest request, CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest(ApiResponse.FailureResult("Email and Name are required."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();
            var ownerBranch = await _context.Branches.FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);
            if (ownerBranch == null)
            {
                return BadRequest(ApiResponse.FailureResult("Logged-in user does not manage a lab branch."));
            }

            var lowercaseEmail = request.Email.Trim().ToLower();
            
            // Check if user already exists
            var existingUser = await _context.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == lowercaseEmail && !u.IsDeleted, cancellationToken);
            
            User user;

            if (existingUser != null)
            {
                if (existingUser.Status != "notverified")
                {
                    return BadRequest(ApiResponse.FailureResult("A user with this email already exists."));
                }
                
                user = existingUser;
                user.Name = request.Name.Trim();
                user.CreatedAt = DateTime.UtcNow;
                _context.Users.Update(user);
            }
            else
            {
                user = new User
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = request.Name.Trim(),
                    Email = request.Email.Trim(),
                    Role = UserRole.Staff,
                    IsActive = true,
                    IsDeleted = false,
                    Status = "notverified",
                    CreatedAt = DateTime.UtcNow,
                    Permissions = new List<string>()
                };
                _context.Users.Add(user);
            }

            var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            var invite = new StaffInvite
            {
                Id = Guid.NewGuid().ToString(),
                Email = request.Email.Trim(),
                Name = request.Name.Trim(),
                BranchId = ownerBranch.Id,
                Token = token,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(15),
                IsUsed = false
            };

            _context.StaffInvites.Add(invite);
            await _context.SaveChangesAsync(cancellationToken);

            var config = HttpContext.RequestServices.GetService(typeof(Microsoft.Extensions.Configuration.IConfiguration)) as Microsoft.Extensions.Configuration.IConfiguration;
            var frontendUrl = config?["FrontendUrl"] ?? "https://admin.anandhu-kannan.in";
            var verifyUrl = $"{frontendUrl.TrimEnd('/')}/staff/register?token={token}";

            var emailSubject = $"Welcome to Apenir - Complete Registration for {request.Name}";
            var emailBody = $@"
<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>Apenir Staff Partner Invitation</title>
</head>
<body style='margin: 0; padding: 0; background-color: #f9fafb; font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, Helvetica, Arial, sans-serif; -webkit-font-smoothing: antialiased;'>
    <table cellpadding='0' cellspacing='0' width='100%' style='background-color: #f9fafb; min-height: 100vh; padding: 60px 20px;'>
        <tr>
            <td align='center' valign='top'>
                <table cellpadding='0' cellspacing='0' width='100%' style='max-width: 500px; background-color: #ffffff; border: 1px solid #e5e7eb; border-radius: 16px; padding: 40px; box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.05); text-align: left;'>
                    <tr>
                        <td align='left' style='padding-bottom: 32px;'>
                            <span style='font-size: 20px; font-weight: 700; color: #111827; letter-spacing: -0.5px;'>Apenir Staff Partner</span>
                        </td>
                    </tr>
                    <tr>
                        <td style='color: #111827; font-size: 16px; font-weight: 600; padding-bottom: 12px;'>
                            Hello {request.Name},
                        </td>
                    </tr>
                    <tr>
                        <td style='color: #4b5563; font-size: 14px; line-height: 1.6; padding-bottom: 32px;'>
                            You have been invited to join the Apenir Medical Lab Booking platform as a staff phlebotomist for <strong>{ownerBranch.Name}</strong>.
                            <br/><br/>
                            Please click the button below to complete your registration and set up your password:
                        </td>
                    </tr>
                    <tr>
                        <td align='left' style='padding-bottom: 32px;'>
                            <a href='{verifyUrl}' style='display: inline-block; background-color: #111827; color: #ffffff; text-decoration: none; padding: 12px 24px; font-size: 14px; font-weight: 500; border-radius: 8px;'>Complete Registration</a>
                        </td>
                    </tr>
                    <tr>
                        <td style='color: #9ca3af; font-size: 12px; line-height: 1.5; padding-bottom: 24px; border-top: 1px solid #f3f4f6; padding-top: 24px;'>
                            If you're having trouble clicking the button, copy and paste the URL below into your browser:
                            <br/>
                            <a href='{verifyUrl}' style='color: #2563eb; text-decoration: none; word-break: break-all;'>{verifyUrl}</a>
                        </td>
                    </tr>
                    <tr>
                        <td style='color: #ef4444; font-size: 12px; font-weight: 500; padding-bottom: 16px;'>
                            ⏱️ This link is only valid for 15 minutes.
                        </td>
                    </tr>
                    <tr>
                        <td style='color: #9ca3af; font-size: 11px; line-height: 1.4;'>
                            If you did not request this invitation, you can safely ignore this email.
                        </td>
                    </tr>
                </table>
            </td>
        </tr>
    </table>
</body>
</html>";

            await _emailService.SendEmailAsync(request.Email.Trim(), emailSubject, emailBody);

            return Ok(ApiResponse.SuccessResult("Staff invited and email sent successfully."));
        }

        [HttpGet("staff/verify")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<object>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        public async Task<IActionResult> VerifyStaffInvite([FromQuery] string token, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return BadRequest(ApiResponse.FailureResult("Token is required."));
            }

            var invite = await _context.StaffInvites.FirstOrDefaultAsync(i => i.Token == token, cancellationToken);
            if (invite == null || invite.IsUsed || invite.ExpiresAt < DateTime.UtcNow)
            {
                return BadRequest(ApiResponse.FailureResult("Invitation link is expired or already used."));
            }

            return Ok(ApiResponse<object>.SuccessResult(new { email = invite.Email, name = invite.Name }));
        }

        [HttpPost("staff/verify")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        public async Task<IActionResult> CompleteStaffRegistration([FromBody] CompleteStaffRegistrationRequest request, CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Token))
            {
                return BadRequest(ApiResponse.FailureResult("Invalid registration payload."));
            }

            var invite = await _context.StaffInvites.FirstOrDefaultAsync(i => i.Token == request.Token, cancellationToken);
            if (invite == null || invite.IsUsed || invite.ExpiresAt < DateTime.UtcNow)
            {
                return BadRequest(ApiResponse.FailureResult("Invitation link is expired or already used."));
            }

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == invite.Email.ToLower() && !u.IsDeleted, cancellationToken);
            if (user == null)
            {
                return BadRequest(ApiResponse.FailureResult("User shell not found."));
            }

            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == invite.BranchId, cancellationToken);
            if (branch != null)
            {
                user.LabId = branch.LabId;
            }

            user.PasswordHash = _passwordHasher.Hash(request.Password);
            user.Phone = request.Phone;
            user.Status = "Active";
            user.IsActive = true;
            user.UpdatedAt = DateTime.UtcNow;

            invite.IsUsed = true;

            _context.Users.Update(user);
            _context.StaffInvites.Update(invite);
            await _context.SaveChangesAsync(cancellationToken);

            return Ok(ApiResponse.SuccessResult("Staff account registration completed successfully."));
        }

        [HttpPost("appointments/{id}/upload-report")]
        [Authorize]
        [EndpointSummary("Upload diagnostic report PDF")]
        [EndpointDescription("Saves the uploaded report PDF, changes status to Completed, and immediately sends the PDF file to the customer's WhatsApp.")]
        public async Task<IActionResult> UploadReport([FromRoute] string id, [FromForm] IFormFile report, CancellationToken cancellationToken)
        {
            if (report == null || report.Length == 0)
            {
                return BadRequest(ApiResponse.FailureResult("Report PDF file is required."));
            }

            var appointment = await _context.Appointments
                .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

            if (appointment == null)
            {
                return NotFound(ApiResponse.FailureResult("Appointment not found."));
            }

            appointment.CustomerUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == appointment.CustomerUserId, cancellationToken);



            var uploadDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "reports");
            if (!Directory.Exists(uploadDir))
            {
                Directory.CreateDirectory(uploadDir);
            }

            var filename = $"Report_{appointment.AppointmentNumber}.pdf";
            var filePath = Path.Combine(uploadDir, filename);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await report.CopyToAsync(stream, cancellationToken);
            }

            var relativeUrl = $"/reports/{filename}";
            appointment.ReportPdfPath = relativeUrl;
            appointment.Status = AppointmentStatus.Completed;
            appointment.UpdatedAt = DateTime.UtcNow;

            _context.Appointments.Update(appointment);
            await _context.SaveChangesAsync(cancellationToken);

            var requestScheme = Request.Scheme;
            var requestHost = Request.Host;
            var absoluteUrl = $"{requestScheme}://{requestHost}{relativeUrl}";

            if (appointment.CustomerUser != null && !string.IsNullOrEmpty(appointment.CustomerUser.Phone))
            {
                await _whatsAppService.SendDocumentMessageAsync(appointment.CustomerUser.Phone, absoluteUrl, filename);
            }

        return Ok(ApiResponse<string>.SuccessResult(relativeUrl, "Report uploaded and delivered to WhatsApp successfully."));
        }

        [HttpGet("appointments/{appointmentId}/members")]
        [Authorize]
        [EndpointSummary("Get all members added by staff for an appointment")]
        public async Task<IActionResult> GetAppointmentMembers([FromRoute] string appointmentId, CancellationToken cancellationToken)
        {
            var currentUserId = _currentUserService.UserId?.ToString();
            var appointment = await _context.Appointments
                .FirstOrDefaultAsync(a => a.Id == appointmentId, cancellationToken);

            if (appointment == null)
            {
                return NotFound(ApiResponse.FailureResult("Appointment not found."));
            }

            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == appointment.BranchId, cancellationToken);
            if (branch == null || branch.LabUserId != currentUserId)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.FailureResult("Access denied."));
            }

            var members = await _context.AppointmentMembers
                .Where(m => m.AppointmentId == appointmentId)
                .ToListAsync(cancellationToken);

            return Ok(ApiResponse<List<AppointmentMember>>.SuccessResult(members, "Appointment members retrieved successfully."));
        }

        [HttpPost("appointments/{appointmentId}/members/{memberId}/upload-report")]
        [Authorize]
        [EndpointSummary("Upload diagnostic report PDF for a specific member")]
        public async Task<IActionResult> UploadMemberReport(
            [FromRoute] string appointmentId, 
            [FromRoute] string memberId, 
            [FromForm] IFormFile report, 
            CancellationToken cancellationToken)
        {
            if (report == null || report.Length == 0)
            {
                return BadRequest(ApiResponse.FailureResult("Report PDF file is required."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();
            var appointment = await _context.Appointments
                .FirstOrDefaultAsync(a => a.Id == appointmentId, cancellationToken);

            if (appointment == null)
            {
                return NotFound(ApiResponse.FailureResult("Appointment not found."));
            }

            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == appointment.BranchId, cancellationToken);
            if (branch == null || branch.LabUserId != currentUserId)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.FailureResult("Access denied."));
            }

            var member = await _context.AppointmentMembers
                .FirstOrDefaultAsync(m => m.Id == memberId && m.AppointmentId == appointmentId, cancellationToken);

            if (member == null)
            {
                return NotFound(ApiResponse.FailureResult("Appointment member not found."));
            }

            appointment.CustomerUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == appointment.CustomerUserId, cancellationToken);

            var uploadDir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "wwwroot", "reports");
            if (!System.IO.Directory.Exists(uploadDir))
            {
                System.IO.Directory.CreateDirectory(uploadDir);
            }

            var safeMemberName = string.Join("_", member.MemberName.Split(System.IO.Path.GetInvalidFileNameChars()));
            var filename = $"Report_{appointment.AppointmentNumber}_{safeMemberName}_{DateTime.UtcNow.Ticks}.pdf";
            var filePath = System.IO.Path.Combine(uploadDir, filename);

            using (var stream = new System.IO.FileStream(filePath, System.IO.FileMode.Create))
            {
                await report.CopyToAsync(stream, cancellationToken);
            }

            var relativeUrl = $"/reports/{filename}";

            var existingReport = await _context.Reports
                .FirstOrDefaultAsync(r => r.AppointmentId == appointmentId && r.MemberId == memberId, cancellationToken);

            if (existingReport != null)
            {
                existingReport.FileUrl = relativeUrl;
                existingReport.FileName = filename;
                existingReport.UploadedBy = currentUserId ?? string.Empty;
                existingReport.WhatsappSent = false;
                _context.Reports.Update(existingReport);
            }
            else
            {
                var newReport = new Report
                {
                    Id = Guid.NewGuid().ToString(),
                    AppointmentId = appointmentId,
                    MemberId = memberId,
                    FileUrl = relativeUrl,
                    FileName = filename,
                    UploadedBy = currentUserId ?? string.Empty,
                    WhatsappSent = false,
                    CreatedAt = DateTime.UtcNow
                };
                _context.Reports.Add(newReport);
            }

            var allMembersCount = await _context.AppointmentMembers.CountAsync(m => m.AppointmentId == appointmentId, cancellationToken);
            var reportsCount = await _context.Reports.CountAsync(r => r.AppointmentId == appointmentId, cancellationToken);
            
            var totalReports = existingReport == null ? reportsCount + 1 : reportsCount;

            if (allMembersCount > 0 && totalReports >= appointment.MemberCount)
            {
                appointment.Status = AppointmentStatus.Completed;
                appointment.UpdatedAt = DateTime.UtcNow;
                _context.Appointments.Update(appointment);
            }

            await _context.SaveChangesAsync(cancellationToken);

            var requestScheme = Request.Scheme;
            var requestHost = Request.Host;
            var absoluteUrl = $"{requestScheme}://{requestHost}{relativeUrl}";

            if (appointment.CustomerUser != null && !string.IsNullOrEmpty(appointment.CustomerUser.Phone))
            {
                var message = $"📄 *Diagnostic Report Available*\n\nHere is the report for *{member.MemberName}*.";
                await _whatsAppService.SendTextMessageAsync(appointment.CustomerUser.Phone, message);
                await _whatsAppService.SendDocumentMessageAsync(appointment.CustomerUser.Phone, absoluteUrl, filename);
                
                var savedReport = await _context.Reports.FirstOrDefaultAsync(r => r.AppointmentId == appointmentId && r.MemberId == memberId, cancellationToken);
                if (savedReport != null)
                {
                    savedReport.WhatsappSent = true;
                    _context.Reports.Update(savedReport);
                    await _context.SaveChangesAsync(cancellationToken);
                }
            }

            return Ok(ApiResponse<string>.SuccessResult(relativeUrl, $"Report for {member.MemberName} uploaded and delivered to WhatsApp successfully."));
        }

        private async Task GenerateSlotsForNext7Days(string branchId, IApplicationDbContext context, CancellationToken cancellationToken)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var configs = await context.BranchSlotConfigurations
                .Where(c => c.BranchId == branchId)
                .ToListAsync(cancellationToken);

            if (!configs.Any()) return;

            var existingSlots = await context.AppointmentSlots
                .Where(s => s.BranchId == branchId && s.SlotDate >= today)
                .ToListAsync(cancellationToken);

            var newSlots = new List<AppointmentSlot>();

            for (int i = 0; i < 7; i++)
            {
                var date = today.AddDays(i);
                var dayOfWeek = date.DayOfWeek;
                DayText dayEnum = dayOfWeek switch
                {
                    DayOfWeek.Monday => DayText.Mon,
                    DayOfWeek.Tuesday => DayText.Tue,
                    DayOfWeek.Wednesday => DayText.Wed,
                    DayOfWeek.Thursday => DayText.Thu,
                    DayOfWeek.Friday => DayText.Fri,
                    DayOfWeek.Saturday => DayText.Sat,
                    DayOfWeek.Sunday => DayText.Sun,
                    _ => DayText.Mon
                };

                var configsForDay = configs.Where(c => c.DayText == dayEnum).ToList();

                foreach (var config in configsForDay)
                {
                    var exists = existingSlots.Any(s => s.SlotDate == date && s.StartTime == config.StartTime);
                    if (!exists)
                    {
                        newSlots.Add(new AppointmentSlot
                        {
                            Id = Guid.NewGuid().ToString(),
                            BranchId = branchId,
                            SlotDate = date,
                            StartTime = config.StartTime,
                            EndTime = config.EndTime,
                            MaxCapacity = config.MaxCapacity,
                            BookedCount = 0,
                            IsAvailable = !config.IsLeave
                        });
                    }
                }
            }

            if (newSlots.Any())
            {
                context.AppointmentSlots.AddRange(newSlots);
                await context.SaveChangesAsync(cancellationToken);
            }
        }
    }

    public record InviteStaffRequest(string Email, string Name);
    
    public class CompleteStaffRegistrationRequest
    {
        public string Token { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
    }

    public class LabDashboardSummaryResponse
    {
        public int TodayBookingsCount { get; set; }
        public LabDashboardFunnel Funnel { get; set; } = new();
        public LabDashboardFinancials Financials { get; set; } = new();
        public List<AppointmentSlot> Slots { get; set; } = new();
        public List<Appointment> Appointments { get; set; } = new();
    }

    public class LabDashboardFunnel
    {
        public int PendingCount { get; set; }
        public int ConfirmedCount { get; set; }
        public int AssignedCount { get; set; }
        public int CollectedCount { get; set; }
        public int CompletedCount { get; set; }
        public int CancelledCount { get; set; }
    }

    public class LabDashboardFinancials
    {
        public decimal GrossRevenue { get; set; }
        public decimal NetPayout { get; set; }
    }

    public class AssignStaffRequest
    {
        public string StaffId { get; set; } = string.Empty;
    }

    public class BranchServiceDto
    {
        public string BranchServiceId { get; set; } = string.Empty;
        public string ServiceId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public decimal BasePrice { get; set; }
        public decimal? CustomPrice { get; set; }
        public decimal? CustomCommissionPct { get; set; }
        public bool IsActive { get; set; }
    }

    public class UpdateBranchServiceRequest
    {
        public decimal? CustomPrice { get; set; }
        public bool IsActive { get; set; }
    }

    public class CreateBranchCustomServiceRequest
    {
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string? Description { get; set; }
        public decimal BasePrice { get; set; }
        public decimal? CustomPrice { get; set; }
    }

    public class CreateSlotConfigurationRequest
    {
        public DayText DayText { get; set; }
        public string StartTime { get; set; } = string.Empty;
        public string EndTime { get; set; } = string.Empty;
        public int MaxCapacity { get; set; } = 1;
        public bool IsLeave { get; set; }
    }

    public class UpdateSlotConfigurationRequest
    {
        public DayText DayText { get; set; }
        public string StartTime { get; set; } = string.Empty;
        public string EndTime { get; set; } = string.Empty;
        public int MaxCapacity { get; set; }
        public bool IsLeave { get; set; }
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
