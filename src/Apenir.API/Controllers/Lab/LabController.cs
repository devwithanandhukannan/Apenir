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

        /// <summary>
        /// Returns the Branch for the currently authenticated user.
        /// Works for both Lab users (matched via LabUserId) and Staff users (matched via their LabId).
        /// </summary>
        private async Task<Branch?> GetCurrentBranchAsync(CancellationToken cancellationToken)
        {
            var currentUserId = _currentUserService.UserId?.ToString();
            if (string.IsNullOrEmpty(currentUserId)) return null;

            // Primary lookup: the logged-in user is the lab owner
            var branch = await _context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);

            if (branch != null) return branch;

            // Fallback: the logged-in user is a Staff member – find the branch via their LabId
            var user = await _context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == currentUserId, cancellationToken);

            if (user != null && user.Role == UserRole.Staff && !string.IsNullOrEmpty(user.LabId))
            {
                branch = await _context.Branches.AsNoTracking()
                    .FirstOrDefaultAsync(b => b.LabId == user.LabId, cancellationToken);
            }

            return branch;
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
            CookieHelper.SetRefreshTokenCookie(HttpContext, refreshTokenString, "/");

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
                LabId = userBranch?.LabId,
                Role = user.Role.ToString().ToLower()
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
            var branch = await GetCurrentBranchAsync(cancellationToken);

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            var staff = await _context.Users.AsNoTracking()
                .Where(u => u.Role == UserRole.Staff && u.LabId == branch.LabId && !u.IsDeleted)
                .ToListAsync(cancellationToken);

            foreach (var s in staff) s.PasswordHash = null;

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
            var branch = await GetCurrentBranchAsync(cancellationToken);

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            var appointments = await _context.Appointments.AsNoTracking()
                .Where(a => a.BranchId == branch.Id)
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync(cancellationToken);

            var customerIds = appointments.Select(a => a.CustomerUserId).Distinct().ToList();
            var staffIds = appointments.Where(a => a.AssignedStaffId != null).Select(a => a.AssignedStaffId!).Distinct().ToList();

            var customers = await _context.Users.AsNoTracking().Where(u => customerIds.Contains(u.Id)).ToListAsync(cancellationToken);
            var staff = await _context.Users.AsNoTracking().Where(u => staffIds.Contains(u.Id)).ToListAsync(cancellationToken);

            foreach (var c in customers) c.PasswordHash = null;
            foreach (var s in staff) s.PasswordHash = null;

            var customersDict = customers.ToDictionary(c => c.Id);
            var staffDict = staff.ToDictionary(s => s.Id);

            foreach (var app in appointments)
            {
                if (customersDict.TryGetValue(app.CustomerUserId, out var customer))
                {
                    app.CustomerUser = customer;
                }
                if (app.AssignedStaffId != null && staffDict.TryGetValue(app.AssignedStaffId, out var staffMember))
                {
                    app.AssignedStaff = staffMember;
                }
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
            var branch = await _context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            var labUser = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == branch.LabUserId, cancellationToken);
            if (labUser != null) labUser.PasswordHash = null;

            var appointments = await _context.Appointments.AsNoTracking()
                .Where(a => a.BranchId == branch.Id)
                .ToListAsync(cancellationToken);

            var totalAppointments = appointments.Count;
            var completedAppointments = appointments.Count(a => a.Status == AppointmentStatus.Completed);
            var pendingAppointments = appointments.Count(a => a.Status == AppointmentStatus.Pending);
            var totalRevenue = appointments.Where(a => a.Status != AppointmentStatus.Cancelled).Sum(a => a.TotalAmount);
            var totalLabPayout = appointments.Where(a => a.Status != AppointmentStatus.Cancelled).Sum(a => a.LabPayout);

            var staffCount = await _context.Users.AsNoTracking()
                .CountAsync(u => u.Role == UserRole.Staff && u.LabId == branch.LabId && !u.IsDeleted, cancellationToken);

            var servicesCount = await _context.BranchServices.AsNoTracking()
                .CountAsync(bs => bs.BranchId == branch.Id && bs.IsActive, cancellationToken);

            var activeSlotsCount = await _context.AppointmentSlots.AsNoTracking()
                .CountAsync(s => s.BranchId == branch.Id && s.IsAvailable, cancellationToken);

            var response = new BranchDetailsResponse
            {
                Branch = branch,
                LabUser = labUser,
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

                [HttpPut("details")]
        [Authorize]
        [EndpointSummary("Update branch profile details")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> UpdateBranchDetails([FromBody] UpdateBranchDetailsRequest request, CancellationToken cancellationToken)
        {
            if (request == null) return BadRequest(ApiResponse.FailureResult("Request body is required."));

            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);
            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            branch.Name = request.Name;
            branch.Phone = request.Phone;
            branch.City = request.City;
            branch.District = request.District;
            branch.Pincode = request.Pincode;
            branch.Latitude = request.Latitude;
            branch.Longitude = request.Longitude;
            branch.ServiceRangeKm = request.ServiceRangeKm;
            branch.PerKmCharge = request.PerKmCharge;

            _context.Branches.Update(branch);
            await _context.SaveChangesAsync(cancellationToken);

            return Ok(ApiResponse.SuccessResult("Branch profile details updated successfully."));
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
                    // Allow staff who belong to this branch
                    var currentUser = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == currentUserId, cancellationToken);
                    if (currentUser == null || currentUser.Role != UserRole.Staff || currentUser.LabId != branch.LabId)
                    {
                        return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.FailureResult("Access denied to this branch."));
                    }
                }
            }
            else
            {
                branch = await GetCurrentBranchAsync(cancellationToken);
            }

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Lab/branch configuration not found for this user."));
            }

            var targetBranchId = branch.Id;
            await GenerateSlotsForNext7Days(targetBranchId, _context, cancellationToken);

            // Default date range: today to next 7 days
            var start = startDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var end = endDate ?? start.AddDays(7);

            var slots = await _context.AppointmentSlots.AsNoTracking()
                .Where(s => s.BranchId == targetBranchId && s.SlotDate >= start && s.SlotDate <= end)
                .OrderBy(s => s.SlotDate)
                .ThenBy(s => s.StartTime)
                .ToListAsync(cancellationToken);

            var slotIds = slots.Select(s => s.Id).ToList();

            var appointments = await _context.Appointments.AsNoTracking()
                .Where(a => a.BranchId == targetBranchId && slotIds.Contains(a.AppointmentSlotId))
                .ToListAsync(cancellationToken);

            if (appointments.Any())
            {
                var customerUserIds = appointments.Select(a => a.CustomerUserId).Distinct().ToList();
                var staffIds = appointments.Where(a => a.AssignedStaffId != null).Select(a => a.AssignedStaffId!).Distinct().ToList();

                var customers = await _context.Users.AsNoTracking().Where(u => customerUserIds.Contains(u.Id)).ToListAsync(cancellationToken);
                var staffMembers = await _context.Users.AsNoTracking().Where(u => staffIds.Contains(u.Id)).ToListAsync(cancellationToken);

                foreach (var c in customers) c.PasswordHash = null;
                foreach (var s in staffMembers) s.PasswordHash = null;

                var customersDict = customers.ToDictionary(c => c.Id);
                var staffDict = staffMembers.ToDictionary(s => s.Id);
                var slotsDict = slots.ToDictionary(s => s.Id);

                foreach (var a in appointments)
                {
                    if (customersDict.TryGetValue(a.CustomerUserId, out var customer))
                    {
                        a.CustomerUser = customer;
                    }
                    if (a.AssignedStaffId != null && staffDict.TryGetValue(a.AssignedStaffId, out var staffMember))
                    {
                        a.AssignedStaff = staffMember;
                    }
                    if (slotsDict.TryGetValue(a.AppointmentSlotId, out var slot))
                    {
                        a.AppointmentSlot = slot;
                    }
                }
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

            // Calculate extra statistics
            var activeStaffList = await _context.Users.AsNoTracking()
                .Where(u => u.Role == UserRole.Staff && u.LabId == branch.LabId && !u.IsDeleted)
                .ToListAsync(cancellationToken);

            var activeStaffCount = activeStaffList.Count(u => u.IsActive == true);

            var staffWorkload = activeStaffList.Select(s => new StaffWorkloadDto
            {
                StaffId = s.Id,
                StaffName = s.Name ?? "Unknown Staff",
                ActiveAssignmentsCount = appointments.Count(a => a.AssignedStaffId == s.Id && (a.Status == AppointmentStatus.Assigned || a.Status == AppointmentStatus.Collected)),
                CompletedAssignmentsCount = appointments.Count(a => a.AssignedStaffId == s.Id && a.Status == AppointmentStatus.Completed)
            }).ToList();

            var slotOccupancy = slots.Select(s => new SlotCapacityStatDto
            {
                SlotId = s.Id,
                TimeWindow = $"{s.StartTime} - {s.EndTime} ({s.SlotDate:yyyy-MM-dd})",
                TotalCapacity = s.MaxCapacity,
                BookedCount = appointments.Count(a => a.AppointmentSlotId == s.Id),
                OccupancyPercentage = s.MaxCapacity > 0 ? Math.Round((double)appointments.Count(a => a.AppointmentSlotId == s.Id) / s.MaxCapacity * 100, 2) : 0
            }).ToList();

            var appointmentIds = appointments.Select(a => a.Id).ToList();
            var members = await _context.AppointmentMembers.AsNoTracking()
                .Where(m => appointmentIds.Contains(m.AppointmentId))
                .ToListAsync(cancellationToken);

            var maleCount = members.Count(m => m.Gender == Gender.Male);
            var femaleCount = members.Count(m => m.Gender == Gender.Female);
            var otherCount = members.Count(m => m.Gender == Gender.Other);
            var avgAge = members.Any() ? Math.Round(members.Average(m => m.Age), 1) : 0;

            var extraStats = new LabDashboardExtraStats
            {
                AverageOrderValue = totalBookings > 0 ? Math.Round(grossRevenue / totalBookings, 2) : 0,
                CompletionRate = totalBookings > 0 ? Math.Round((decimal)completedCount / totalBookings * 100, 2) : 0,
                CancellationRate = totalBookings > 0 ? Math.Round((decimal)cancelledCount / totalBookings * 100, 2) : 0,
                ActiveStaffCount = activeStaffCount,
                StaffWorkload = staffWorkload,
                SlotOccupancy = slotOccupancy,
                PatientDemographics = new PatientDemographicsDto
                {
                    MaleCount = maleCount,
                    FemaleCount = femaleCount,
                    OtherCount = otherCount,
                    AverageAge = (double)avgAge
                }
            };

            var dailySlots = slots
                .GroupBy(s => s.SlotDate)
                .Select(g => new DailySlotCalendarDto
                {
                    Date = g.Key,
                    DayName = g.Key.ToString("dddd"),
                    Slots = g.Select(s => new SlotCalendarDetailDto
                    {
                        SlotId = s.Id,
                        TimeWindow = $"{s.StartTime} - {s.EndTime}",
                        MaxCapacity = s.MaxCapacity,
                        BookedCount = appointments.Count(a => a.AppointmentSlotId == s.Id),
                        IsAvailable = s.IsAvailable
                    }).ToList()
                })
                .OrderBy(d => d.Date)
                .ToList();

            var response = new LabDashboardSummaryResponse
            {
                TotalBookings = totalBookings,
                AssignedCount = assignedCount,
                CollectedCount = collectedCount,
                CompletedCount = completedCount,
                PendingCount = pendingCount,
                ConfirmedCount = confirmedCount,
                CancelledCount = cancelledCount,
                GrossRevenue = grossRevenue,
                NetPayout = netPayout,
                DailyCalendar = dailySlots,
                ExtraStats = extraStats
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
                branch = await _context.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);
                if (branch != null && branch.LabUserId != currentUserId)
                {
                    // Allow staff who belong to this branch
                    var currentUser = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == currentUserId, cancellationToken);
                    if (currentUser == null || currentUser.Role != UserRole.Staff || currentUser.LabId != branch.LabId)
                    {
                        return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.FailureResult("Access denied to this branch."));
                    }
                }
            }
            else
            {
                branch = await GetCurrentBranchAsync(cancellationToken);
            }

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            var pending = await _context.Appointments.AsNoTracking()
                .Where(a => a.BranchId == branch.Id && a.AssignedStaffId == null && 
                            (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed))
                .OrderBy(a => a.CreatedAt)
                .ToListAsync(cancellationToken);

            if (pending.Any())
            {
                var customerUserIds = pending.Select(a => a.CustomerUserId).Distinct().ToList();
                var slotIds = pending.Select(a => a.AppointmentSlotId).Distinct().ToList();

                var customers = await _context.Users.AsNoTracking().Where(u => customerUserIds.Contains(u.Id)).ToListAsync(cancellationToken);
                var slots = await _context.AppointmentSlots.AsNoTracking().Where(s => slotIds.Contains(s.Id)).ToListAsync(cancellationToken);

                foreach (var c in customers) c.PasswordHash = null;

                var customersDict = customers.ToDictionary(c => c.Id);
                var slotsDict = slots.ToDictionary(s => s.Id);

                foreach (var a in pending)
                {
                    if (customersDict.TryGetValue(a.CustomerUserId, out var customer))
                    {
                        a.CustomerUser = customer;
                    }
                    if (slotsDict.TryGetValue(a.AppointmentSlotId, out var slot))
                    {
                        a.AppointmentSlot = slot;
                    }
                }
            }

            return Ok(ApiResponse<List<Appointment>>.SuccessResult(pending, "Pending staff assignments retrieved successfully."));
        }

        [HttpPost("appointments/{id}/assign-staff")]
        [Authorize]
        [EndpointSummary("Assign staff member to appointment")]
        [EndpointDescription("Assigns a specific staff member to the specified appointment. Transitions status to Assigned.")]
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

            var staffUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == request.StaffId && u.Role == UserRole.Staff && !u.IsDeleted, cancellationToken);
            if (staffUser == null)
            {
                return BadRequest(ApiResponse.FailureResult("Invalid or non-existent staff member selected."));
            }

            if (!string.IsNullOrWhiteSpace(request.BranchId))
            {
                var branchExists = await _context.Branches.AnyAsync(b => b.Id == request.BranchId && b.LabUserId == currentUserId, cancellationToken);
                if (branchExists)
                {
                    appointment.BranchId = request.BranchId;
                }
            }

            appointment.AssignedStaffId = request.StaffId;
            appointment.Status = AppointmentStatus.Assigned;
            appointment.UpdatedAt = DateTime.UtcNow;

            _context.Appointments.Update(appointment);
            await _context.SaveChangesAsync(cancellationToken);

            // Fetch detail for WhatsApp notification
            var customerUser = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == appointment.CustomerUserId, cancellationToken);
            var slot = await _context.AppointmentSlots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == appointment.AppointmentSlotId, cancellationToken);

            if (!string.IsNullOrEmpty(staffUser.Phone))
            {
                var slotDateStr = slot != null ? slot.SlotDate.ToString("dd-MMM-yyyy") : "N/A";
                var slotTimeStr = slot != null ? $"{slot.StartTime} - {slot.EndTime}" : "N/A";
                var staffMsg = $"📋 *New Task Assigned!*\n\nHello {staffUser.Name ?? "Staff"},\n\nYou have been assigned a new collection task.\n\n*Appointment ID:* {appointment.AppointmentNumber}\n*Patient Name:* {customerUser?.Name ?? "Customer"}\n*Address:* {appointment.LocationAddress}\n*Scheduled Slot:* {slotDateStr} ({slotTimeStr})\n\nPlease check your appointments page for details.";
                
                try
                {
                    await _whatsAppService.SendTextMessageAsync(staffUser.Phone, staffMsg);
                }
                catch (Exception ex)
                {
                    // Log to console but do not fail the request
                    Console.WriteLine($"Failed to send WhatsApp alert to staff: {staffUser.Phone}. Error: {ex.Message}");
                }
            }

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
            var branch = await GetCurrentBranchAsync(cancellationToken);

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            var allServices = await _context.Services.AsNoTracking()
                .Where(s => s.IsActive && (s.CreatedByBranchId == null || s.CreatedByBranchId == branch.Id))
                .ToListAsync(cancellationToken);

            var branchServices = await _context.BranchServices.AsNoTracking()
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
                return NotFound(ApiResponse.FailureResult("Lab/branch configuration not found for this user."));
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
                CustomPrice = request.CustomPrice, // If provided, overrides base price, otherwise remains null
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
                user.LabId = ownerBranch.LabId;
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
                    LabId = ownerBranch.LabId,
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
            var verifyUrl = $"{frontendUrl.TrimEnd('/')}/register/staff?token={token}";

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

        [HttpPost("appointments/{appointmentId}/reports")]
        [Authorize]
        [EndpointSummary("Submit typed diagnostic report data")]
        [EndpointDescription("Submits/updates typed test results for a specific patient under an appointment. If all patients in the appointment have results submitted, the appointment status transitions to Completed.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> SubmitReport(
            [FromRoute] string appointmentId,
            [FromBody] SubmitReportRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.MemberId) || string.IsNullOrWhiteSpace(request.ResultData))
            {
                return BadRequest(ApiResponse.FailureResult("MemberId and ResultData are required."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();
            var appointment = await _context.Appointments
                .FirstOrDefaultAsync(a => a.Id == appointmentId, cancellationToken);

            if (appointment == null)
            {
                return NotFound(ApiResponse.FailureResult("Appointment not found."));
            }

            var branch = await _context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == appointment.BranchId, cancellationToken);

            if (branch == null || branch.LabUserId != currentUserId)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.FailureResult("Access denied to this appointment."));
            }

            var memberExists = await _context.AppointmentMembers.AsNoTracking()
                .AnyAsync(m => m.Id == request.MemberId && m.AppointmentId == appointmentId, cancellationToken);

            if (!memberExists)
            {
                return BadRequest(ApiResponse.FailureResult("Member does not belong to this appointment."));
            }

            var report = await _context.Reports
                .FirstOrDefaultAsync(r => r.AppointmentId == appointmentId && r.MemberId == request.MemberId, cancellationToken);

            if (report == null)
            {
                report = new Report
                {
                    Id = Guid.NewGuid().ToString(),
                    AppointmentId = appointmentId,
                    MemberId = request.MemberId,
                    CreatedAt = DateTime.UtcNow
                };
                _context.Reports.Add(report);
            }
            else
            {
                _context.Reports.Update(report);
            }

            report.ResultData = request.ResultData;
            report.UploadedBy = currentUserId ?? string.Empty;

            await _context.SaveChangesAsync(cancellationToken);

            // Automatically check if all members have reports to transition status to Completed
            var totalMembers = await _context.AppointmentMembers.AsNoTracking()
                .Where(m => m.AppointmentId == appointmentId)
                .CountAsync(cancellationToken);

            var reportedMembersCount = await _context.Reports.AsNoTracking()
                .Where(r => r.AppointmentId == appointmentId && !string.IsNullOrEmpty(r.ResultData))
                .Select(r => r.MemberId)
                .Distinct()
                .CountAsync(cancellationToken);

            if (reportedMembersCount >= totalMembers)
            {
                appointment.Status = AppointmentStatus.Completed;
                appointment.UpdatedAt = DateTime.UtcNow;
                _context.Appointments.Update(appointment);
            }

            await _context.SaveChangesAsync(cancellationToken);

            return Ok(ApiResponse.SuccessResult("Report data submitted successfully."));
        }

        [HttpGet("appointments/{appointmentId}/reports")]
        [Authorize]
        [EndpointSummary("Get all diagnostic reports for an appointment")]
        [EndpointDescription("Returns all reports and typed result data submitted for the members of this appointment.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<AppointmentReportDto>>))]
        [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetReports([FromRoute] string appointmentId, CancellationToken cancellationToken)
        {
            var currentUserId = _currentUserService.UserId?.ToString();
            var appointment = await _context.Appointments.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == appointmentId, cancellationToken);

            if (appointment == null)
            {
                return NotFound(ApiResponse.FailureResult("Appointment not found."));
            }

            var branch = await _context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == appointment.BranchId, cancellationToken);

            if (branch == null || branch.LabUserId != currentUserId)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.FailureResult("Access denied to this appointment."));
            }

            var reports = await _context.Reports.AsNoTracking()
                .Where(r => r.AppointmentId == appointmentId)
                .ToListAsync(cancellationToken);

            var members = await _context.AppointmentMembers.AsNoTracking()
                .Where(m => m.AppointmentId == appointmentId)
                .ToListAsync(cancellationToken);

            var memberDict = members.ToDictionary(m => m.Id, m => m.MemberName);

            var result = reports.Select(r => new AppointmentReportDto
            {
                ReportId = r.Id,
                AppointmentId = r.AppointmentId,
                MemberId = r.MemberId,
                MemberName = memberDict.TryGetValue(r.MemberId, out var name) ? name : string.Empty,
                FileUrl = r.FileUrl,
                FileName = r.FileName,
                ResultData = r.ResultData,
                CreatedAt = r.CreatedAt
            }).ToList();

            return Ok(ApiResponse<List<AppointmentReportDto>>.SuccessResult(result, "Reports retrieved successfully."));
        }

        [HttpPost("appointments/list")]
        [Authorize]
        [EndpointSummary("Search and Filter Appointments for the lab owner")]
        [EndpointDescription("Returns a paginated list of all appointments scheduled for the lab owner's branches, with optional filters.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<PaginatedList<Appointment>>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        public async Task<IActionResult> ListAppointments([FromBody] LabSearchAppointmentsRequest? request, CancellationToken cancellationToken)
        {
            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);

            if (branch == null)
            {
                var emptyResponse = new PaginatedList<Appointment>
                {
                    Items = new List<Appointment>(),
                    PageNumber = 1,
                    PageSize = 0,
                    RowsPerPage = request?.RowsPerPage ?? 10,
                    PageCount = 0,
                    TotalRows = 0
                };
                return Ok(ApiResponse<PaginatedList<Appointment>>.SuccessResult(emptyResponse, "Lab/branch configuration not found for this user."));
            }

            var branchId = branch.Id;

            var query = _context.Appointments.AsNoTracking()
                .Where(a => a.BranchId == branchId);

            if (request != null)
            {
                if (!string.IsNullOrWhiteSpace(request.AppointmentNumber))
                {
                    var appNumQuery = request.AppointmentNumber.Trim().ToLower();
                    query = query.Where(a => a.AppointmentNumber.ToLower().Contains(appNumQuery));
                }

                if (request.Status.HasValue)
                {
                    query = query.Where(a => a.Status == request.Status.Value);
                }

                if (!string.IsNullOrWhiteSpace(request.CustomerName))
                {
                    var custNameQuery = request.CustomerName.Trim().ToLower();
                    var customerIds = await _context.Users.AsNoTracking()
                        .Where(u => u.Role == UserRole.Customer && u.Name != null && u.Name.ToLower().Contains(custNameQuery))
                        .Select(u => u.Id)
                        .ToListAsync(cancellationToken);

                    query = query.Where(a => customerIds.Contains(a.CustomerUserId));
                }

                if (!string.IsNullOrWhiteSpace(request.StaffName))
                {
                    var staffNameQuery = request.StaffName.Trim().ToLower();
                    var staffIds = await _context.Users.AsNoTracking()
                        .Where(u => u.Role == UserRole.Staff && u.Name != null && u.Name.ToLower().Contains(staffNameQuery))
                        .Select(u => u.Id)
                        .ToListAsync(cancellationToken);

                    query = query.Where(a => a.AssignedStaffId != null && staffIds.Contains(a.AssignedStaffId));
                }

                if (request.StartDate.HasValue || request.EndDate.HasValue)
                {
                    var slotsQuery = _context.AppointmentSlots.AsNoTracking().Where(s => s.BranchId == branchId);
                    if (request.StartDate.HasValue)
                    {
                        slotsQuery = slotsQuery.Where(s => s.SlotDate >= request.StartDate.Value);
                    }
                    if (request.EndDate.HasValue)
                    {
                        slotsQuery = slotsQuery.Where(s => s.SlotDate <= request.EndDate.Value);
                    }

                    var slotIds = await slotsQuery.Select(s => s.Id).ToListAsync(cancellationToken);
                    query = query.Where(a => slotIds.Contains(a.AppointmentSlotId));
                }
            }

            var totalRows = await query.CountAsync(cancellationToken);
            var pageNumber = request?.PageNumber ?? 1;
            var rowsPerPage = request?.RowsPerPage ?? 10;
            if (pageNumber < 1) pageNumber = 1;
            if (rowsPerPage < 1) rowsPerPage = 10;

            var pageCount = (int)Math.Ceiling((double)totalRows / rowsPerPage);

            var appointmentsList = await query
                .OrderByDescending(a => a.CreatedAt)
                .Skip((pageNumber - 1) * rowsPerPage)
                .Take(rowsPerPage)
                .ToListAsync(cancellationToken);

            if (appointmentsList.Any())
            {
                var customerUserIds = appointmentsList.Select(a => a.CustomerUserId).Distinct().ToList();
                var staffIds = appointmentsList.Where(a => a.AssignedStaffId != null).Select(a => a.AssignedStaffId!).Distinct().ToList();
                var slotIds = appointmentsList.Select(a => a.AppointmentSlotId).Distinct().ToList();

                var customers = await _context.Users.AsNoTracking().Where(u => customerUserIds.Contains(u.Id)).ToListAsync(cancellationToken);
                var staffMembers = await _context.Users.AsNoTracking().Where(u => staffIds.Contains(u.Id)).ToListAsync(cancellationToken);
                var slots = await _context.AppointmentSlots.AsNoTracking().Where(s => slotIds.Contains(s.Id)).ToListAsync(cancellationToken);

                foreach (var c in customers) c.PasswordHash = null;
                foreach (var s in staffMembers) s.PasswordHash = null;

                var customersDict = customers.ToDictionary(c => c.Id);
                var staffDict = staffMembers.ToDictionary(s => s.Id);
                var slotsDict = slots.ToDictionary(s => s.Id);

                foreach (var a in appointmentsList)
                {
                    if (customersDict.TryGetValue(a.CustomerUserId, out var customer))
                    {
                        a.CustomerUser = customer;
                    }
                    if (a.AssignedStaffId != null && staffDict.TryGetValue(a.AssignedStaffId, out var staffMember))
                    {
                        a.AssignedStaff = staffMember;
                    }
                    if (slotsDict.TryGetValue(a.AppointmentSlotId, out var slot))
                    {
                        a.AppointmentSlot = slot;
                    }
                }
            }

            var response = new PaginatedList<Appointment>
            {
                Items = appointmentsList,
                PageNumber = pageNumber,
                PageSize = appointmentsList.Count,
                RowsPerPage = rowsPerPage,
                PageCount = pageCount,
                TotalRows = totalRows
            };

            return Ok(ApiResponse<PaginatedList<Appointment>>.SuccessResult(response, "Appointments retrieved successfully."));
        }

        [HttpPost("staff")]
        [Authorize]
        [EndpointSummary("Add Staff member directly")]
        [EndpointDescription("Directly creates and registers a staff member in Active status.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> AddStaff([FromBody] AddStaffRequest request, CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Email) ||
                string.IsNullOrWhiteSpace(request.Phone) || string.IsNullOrWhiteSpace(request.Password))
            {
                return BadRequest(ApiResponse.FailureResult("Name, Email, Phone, and Password are required."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Lab/branch configuration not found for this user."));
            }

            var emailTaken = await _context.Users.AsNoTracking()
                .AnyAsync(u => u.Email != null && u.Email.ToLower() == request.Email.Trim().ToLower(), cancellationToken);

            if (emailTaken)
            {
                return BadRequest(ApiResponse.FailureResult("A user with this email address already exists."));
            }

            var newStaff = new User
            {
                Id = Guid.NewGuid().ToString(),
                Name = request.Name.Trim(),
                Email = request.Email.Trim().ToLower(),
                Phone = request.Phone.Trim(),
                PasswordHash = _passwordHasher.Hash(request.Password),
                Role = UserRole.Staff,
                LabId = branch.LabId,
                IsActive = true,
                IsDeleted = false,
                Status = "Active",
                CreatedAt = DateTime.UtcNow
            };

            _context.Users.Add(newStaff);
            await _context.SaveChangesAsync(cancellationToken);

            return Ok(ApiResponse.SuccessResult("Staff member added successfully."));
        }

        [HttpPut("staff/{staffId}")]
        [Authorize]
        [EndpointSummary("Edit staff details")]
        [EndpointDescription("Updates properties of a staff member registered under the logged-in lab branch.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> EditStaff([FromRoute] string staffId, [FromBody] EditStaffRequest request, CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Phone))
            {
                return BadRequest(ApiResponse.FailureResult("Name, Email, and Phone are required."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Lab/branch configuration not found for this user."));
            }

            var staff = await _context.Users
                .FirstOrDefaultAsync(u => u.Id == staffId && u.Role == UserRole.Staff && u.LabId == branch.LabId && !u.IsDeleted, cancellationToken);

            if (staff == null)
            {
                return NotFound(ApiResponse.FailureResult("Staff member not found under this lab."));
            }

            var newEmail = request.Email.Trim().ToLower();
            if (staff.Email != newEmail)
            {
                var emailTaken = await _context.Users.AsNoTracking()
                    .AnyAsync(u => u.Email != null && u.Email.ToLower() == newEmail && u.Id != staffId, cancellationToken);

                if (emailTaken)
                {
                    return BadRequest(ApiResponse.FailureResult("A user with this email address already exists."));
                }
                staff.Email = newEmail;
            }

            staff.Name = request.Name.Trim();
            staff.Phone = request.Phone.Trim();
            staff.IsActive = request.IsActive;
            staff.Status = request.IsActive ? "Active" : "Inactive";

            if (!string.IsNullOrWhiteSpace(request.Password))
            {
                staff.PasswordHash = _passwordHasher.Hash(request.Password);
            }

            staff.UpdatedAt = DateTime.UtcNow;

            _context.Users.Update(staff);
            await _context.SaveChangesAsync(cancellationToken);

            return Ok(ApiResponse.SuccessResult("Staff member updated successfully."));
        }

        [HttpDelete("staff/{staffId}")]
        [Authorize]
        [EndpointSummary("Delete staff member")]
        [EndpointDescription("Marks a staff member as deleted and removes them from all active assigned cases.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> DeleteStaff([FromRoute] string staffId, CancellationToken cancellationToken)
        {
            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Lab/branch configuration not found for this user."));
            }

            var staff = await _context.Users
                .FirstOrDefaultAsync(u => u.Id == staffId && u.Role == UserRole.Staff && u.LabId == branch.LabId && !u.IsDeleted, cancellationToken);

            if (staff == null)
            {
                return NotFound(ApiResponse.FailureResult("Staff member not found under this lab."));
            }

            // Mark staff as deleted
            staff.IsDeleted = true;
            staff.IsActive = false;
            staff.Status = "Deleted";
            staff.UpdatedAt = DateTime.UtcNow;

            _context.Users.Update(staff);

            // Revert assigned cases/appointments that are not completed/cancelled
            var activeAppointments = await _context.Appointments
                .Where(a => a.AssignedStaffId == staffId && a.Status != AppointmentStatus.Completed && a.Status != AppointmentStatus.Cancelled)
                .ToListAsync(cancellationToken);

            foreach (var app in activeAppointments)
            {
                app.AssignedStaffId = null;
                if (app.Status == AppointmentStatus.Assigned || app.Status == AppointmentStatus.Collected)
                {
                    app.Status = AppointmentStatus.Confirmed;
                }
                app.UpdatedAt = DateTime.UtcNow;
                _context.Appointments.Update(app);
            }

            await _context.SaveChangesAsync(cancellationToken);

            return Ok(ApiResponse.SuccessResult("Staff member deleted successfully."));
        }

        [HttpPost("payment-batches/list")]
        [Authorize]
        [EndpointSummary("List payment batches for the lab")]
        [EndpointDescription("Returns a paginated list of payment batches for the logged-in lab owner's branches.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<PaginatedList<LabBatchListDto>>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        public async Task<IActionResult> ListPaymentBatches([FromBody] LabSearchBatchesRequest? request, CancellationToken cancellationToken)
        {
            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);

            if (branch == null)
            {
                var emptyResponse = new PaginatedList<LabBatchListDto>
                {
                    Items = new List<LabBatchListDto>(),
                    PageNumber = 1,
                    PageSize = 0,
                    RowsPerPage = request?.RowsPerPage ?? 10,
                    PageCount = 0,
                    TotalRows = 0
                };
                return Ok(ApiResponse<PaginatedList<LabBatchListDto>>.SuccessResult(emptyResponse, "Lab/branch configuration not found for this user."));
            }

            var branchId = branch.Id;

            var query = _context.PaymentBatches.AsNoTracking()
                .Where(pb => pb.BranchId == branchId);

            if (request != null)
            {
                if (request.Status.HasValue)
                {
                    query = query.Where(pb => pb.Status == request.Status.Value);
                }

                if (request.StartDate.HasValue)
                {
                    query = query.Where(pb => pb.CreatedAt >= request.StartDate.Value);
                }

                if (request.EndDate.HasValue)
                {
                    query = query.Where(pb => pb.CreatedAt <= request.EndDate.Value);
                }
            }

            var totalRows = await query.CountAsync(cancellationToken);
            var pageNumber = request?.PageNumber ?? 1;
            var rowsPerPage = request?.RowsPerPage ?? 10;
            if (pageNumber < 1) pageNumber = 1;
            if (rowsPerPage < 1) rowsPerPage = 10;

            var pageCount = (int)Math.Ceiling((double)totalRows / rowsPerPage);

            var batches = await query
                .OrderByDescending(pb => pb.CreatedAt)
                .Skip((pageNumber - 1) * rowsPerPage)
                .Take(rowsPerPage)
                .ToListAsync(cancellationToken);

            var items = batches.Select(pb => new LabBatchListDto
            {
                Id = pb.Id,
                BranchId = pb.BranchId,
                BranchName = branch.Name,
                PaymentCount = pb.PaymentCount,
                TotalGrossAmount = pb.TotalGrossAmount,
                TotalPlatformCommission = pb.TotalPlatformCommission,
                TotalNetPayout = pb.TotalNetPayout,
                Status = pb.Status,
                CreatedAt = pb.CreatedAt,
                ConfirmedAt = pb.ConfirmedAt,
                Notes = pb.Notes
            }).ToList();

            var response = new PaginatedList<LabBatchListDto>
            {
                Items = items,
                PageNumber = pageNumber,
                PageSize = items.Count,
                RowsPerPage = rowsPerPage,
                PageCount = pageCount,
                TotalRows = totalRows
            };

            return Ok(ApiResponse<PaginatedList<LabBatchListDto>>.SuccessResult(response, "Payment batches retrieved successfully."));
        }

        [HttpGet("payment-batches/{batchId}")]
        [Authorize]
        [EndpointSummary("Get payment batch details")]
        [EndpointDescription("Returns detailed information about a payment batch including its payments and appointments.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<LabBatchDetailResponse>))]
        [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetPaymentBatchDetails([FromRoute] string batchId, CancellationToken cancellationToken)
        {
            var batch = await _context.PaymentBatches.AsNoTracking()
                .FirstOrDefaultAsync(pb => pb.Id == batchId, cancellationToken);

            if (batch == null)
            {
                return NotFound(ApiResponse.FailureResult("Payment batch not found."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == batch.BranchId, cancellationToken);

            if (branch == null || branch.LabUserId != currentUserId)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.FailureResult("Access denied to this batch."));
            }

            var payments = await _context.Payments.AsNoTracking()
                .Where(p => batch.PaymentIds.Contains(p.Id))
                .ToListAsync(cancellationToken);

            var appointments = await _context.Appointments.AsNoTracking()
                .Where(a => batch.AppointmentIds.Contains(a.Id))
                .ToListAsync(cancellationToken);

            var customerUserIds = appointments
                .Select(a => a.CustomerUserId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();

            var customerUsers = await _context.Users.AsNoTracking()
                .Where(u => customerUserIds.Contains(u.Id))
                .ToListAsync(cancellationToken);

            var appointmentDict = appointments.ToDictionary(a => a.Id);
            var customerDict = customerUsers.ToDictionary(u => u.Id);

            var paymentItems = payments.Select(p =>
            {
                appointmentDict.TryGetValue(p.AppointmentId, out var appt);
                var customerName = string.Empty;
                if (appt != null && !string.IsNullOrEmpty(appt.CustomerUserId))
                {
                    customerDict.TryGetValue(appt.CustomerUserId, out var cust);
                    customerName = cust?.Name ?? string.Empty;
                }

                return new LabBatchPaymentItemDto
                {
                    PaymentId = p.Id,
                    AppointmentId = p.AppointmentId,
                    AppointmentNumber = appt?.AppointmentNumber ?? string.Empty,
                    CustomerName = customerName,
                    TotalAmount = appt?.TotalAmount ?? 0,
                    PlatformCommission = appt?.PlatformCommission ?? 0,
                    LabPayout = appt?.LabPayout ?? 0,
                    PaidAt = p.PaidAt,
                    PaymentMethod = p.PaymentMethod
                };
            }).ToList();

            var response = new LabBatchDetailResponse
            {
                Id = batch.Id,
                BranchId = batch.BranchId,
                BranchName = branch.Name,
                PaymentCount = batch.PaymentCount,
                TotalGrossAmount = batch.TotalGrossAmount,
                TotalPlatformCommission = batch.TotalPlatformCommission,
                TotalNetPayout = batch.TotalNetPayout,
                Status = batch.Status,
                CreatedAt = batch.CreatedAt,
                ConfirmedAt = batch.ConfirmedAt,
                ConfirmedByLabUser = batch.ConfirmedByLabUser,
                Notes = batch.Notes,
                Payments = paymentItems
            };

            return Ok(ApiResponse<LabBatchDetailResponse>.SuccessResult(response, "Payment batch details retrieved successfully."));
        }

        [HttpPost("payment-batches/{batchId}/confirm-receipt")]
        [Authorize]
        [EndpointSummary("Confirm batch payment receipt")]
        [EndpointDescription("Lab owner confirms that the payout amount for this batch has been received. Changes batch status to Settled.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> ConfirmBatchReceipt([FromRoute] string batchId, CancellationToken cancellationToken)
        {
            var batch = await _context.PaymentBatches
                .FirstOrDefaultAsync(pb => pb.Id == batchId, cancellationToken);

            if (batch == null)
            {
                return NotFound(ApiResponse.FailureResult("Payment batch not found."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == batch.BranchId, cancellationToken);

            if (branch == null || branch.LabUserId != currentUserId)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.FailureResult("Access denied to this batch."));
            }

            if (batch.Status != PaymentBatchStatus.Paid)
            {
                return BadRequest(ApiResponse.FailureResult("Only batches with 'Paid' status can be confirmed."));
            }

            batch.Status = PaymentBatchStatus.Settled;
            batch.ConfirmedByLabUser = currentUserId;
            batch.ConfirmedAt = DateTime.UtcNow;

            _context.PaymentBatches.Update(batch);
            await _context.SaveChangesAsync(cancellationToken);

            // Notify Admin of settlement
            var adminUser = await _context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Role == UserRole.SuperAdmin, cancellationToken);

            if (adminUser != null && !string.IsNullOrEmpty(adminUser.Phone))
            {
                var adminMsg = $"✅ *Payout Settlement Confirmed!*\n\n" +
                               $"Lab: *{branch.Name}*\n" +
                               $"Batch ID: {batch.Id[..8].ToUpper()}\n" +
                               $"Amount: ₹{batch.TotalNetPayout}\n" +
                               $"Status: Settled.";

                try { await _whatsAppService.SendTextMessageAsync(adminUser.Phone, adminMsg); } catch { }
            }

            return Ok(ApiResponse.SuccessResult("Batch payment receipt confirmed successfully."));
        }

        [HttpGet("unbatched-payments")]
        [Authorize]
        [EndpointSummary("Get unbatched payments for the lab")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<LabBatchPaymentItemDto>>))]
        [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetUnbatchedPayments(CancellationToken cancellationToken)
        {
            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);

            if (branch == null)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.FailureResult("Access denied. No branch configured for this lab owner."));
            }

            // Find all appointments for this branch
            var appointments = await _context.Appointments.AsNoTracking()
                .Where(a => a.BranchId == branch.Id)
                .ToListAsync(cancellationToken);

            var appointmentIds = appointments.Select(a => a.Id).ToList();
            if (appointmentIds.Count == 0)
            {
                return Ok(ApiResponse<List<LabBatchPaymentItemDto>>.SuccessResult(new List<LabBatchPaymentItemDto>(), "No unbatched payments found."));
            }

            // Find all paid payments for these appointments that have no BatchId
            var payments = await _context.Payments.AsNoTracking()
                .Where(p => appointmentIds.Contains(p.AppointmentId) && p.Status == PaymentStatus.Paid && p.BatchId == null)
                .ToListAsync(cancellationToken);

            if (payments.Count == 0)
            {
                return Ok(ApiResponse<List<LabBatchPaymentItemDto>>.SuccessResult(new List<LabBatchPaymentItemDto>(), "No unbatched payments found."));
            }

            // Fetch customer details
            var customerUserIds = appointments
                .Select(a => a.CustomerUserId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();

            var customerUsers = await _context.Users.AsNoTracking()
                .Where(u => customerUserIds.Contains(u.Id))
                .ToListAsync(cancellationToken);

            var appointmentMembers = await _context.AppointmentMembers.AsNoTracking()
                .Where(am => appointmentIds.Contains(am.AppointmentId))
                .ToListAsync(cancellationToken);

            var appointmentDict = appointments.ToDictionary(a => a.Id);
            var customerDict = customerUsers.ToDictionary(u => u.Id);
            var memberDict = appointmentMembers
                .GroupBy(am => am.AppointmentId)
                .ToDictionary(g => g.Key, g => g.First().MemberName);

            var result = payments.Select(p =>
            {
                appointmentDict.TryGetValue(p.AppointmentId, out var appt);
                var customerName = string.Empty;
                if (appt != null)
                {
                    if (!string.IsNullOrEmpty(appt.CustomerUserId))
                    {
                        customerDict.TryGetValue(appt.CustomerUserId, out var cust);
                        customerName = cust?.Name ?? string.Empty;
                    }
                    if (string.IsNullOrEmpty(customerName))
                    {
                        memberDict.TryGetValue(appt.Id, out var memName);
                        customerName = memName ?? string.Empty;
                    }
                    if (string.IsNullOrEmpty(customerName))
                    {
                        customerName = "WhatsApp User";
                    }
                }

                return new LabBatchPaymentItemDto
                {
                    PaymentId = p.Id,
                    AppointmentId = p.AppointmentId,
                    AppointmentNumber = appt?.AppointmentNumber ?? string.Empty,
                    CustomerName = customerName,
                    TotalAmount = appt?.TotalAmount ?? 0,
                    PlatformCommission = appt?.PlatformCommission ?? 0,
                    LabPayout = appt?.LabPayout ?? 0,
                    PaidAt = p.PaidAt,
                    PaymentMethod = p.PaymentMethod
                };
            }).ToList();

            return Ok(ApiResponse<List<LabBatchPaymentItemDto>>.SuccessResult(result, "Unbatched payments retrieved successfully."));
        }

        [HttpPost("payment-batches")]
        [Authorize]
        [EndpointSummary("Request a payout batch")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<PaymentBatch>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ApiResponse))]
        public async Task<IActionResult> RequestPayout([FromBody] LabCreateBatchRequest request, CancellationToken cancellationToken)
        {
            if (request == null || request.PaymentIds == null || request.PaymentIds.Count == 0)
            {
                return BadRequest(ApiResponse.FailureResult("At least one PaymentId is required."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);

            if (branch == null)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.FailureResult("Access denied. No branch configured for this lab owner."));
            }

            // Verify payments exist, belong to this branch, and are paid + unbatched
            var appointments = await _context.Appointments.AsNoTracking()
                .Where(a => a.BranchId == branch.Id)
                .ToListAsync(cancellationToken);

            var appointmentIds = appointments.Select(a => a.Id).ToList();

            var payments = await _context.Payments
                .Where(p => request.PaymentIds.Contains(p.Id) && appointmentIds.Contains(p.AppointmentId) && p.Status == PaymentStatus.Paid && p.BatchId == null)
                .ToListAsync(cancellationToken);

            if (payments.Count != request.PaymentIds.Count)
            {
                return BadRequest(ApiResponse.FailureResult("One or more selected payments are invalid, already batched, or not fully paid."));
            }

            var appDict = appointments.ToDictionary(a => a.Id);

            decimal grossTotal = 0;
            decimal commTotal = 0;
            decimal payoutTotal = 0;
            var paymentIds = new List<string>();
            var appIds = new List<string>();

            foreach (var p in payments)
            {
                appDict.TryGetValue(p.AppointmentId, out var app);
                if (app != null)
                {
                    grossTotal += app.TotalAmount;
                    commTotal += app.PlatformCommission;
                    payoutTotal += app.LabPayout;
                    paymentIds.Add(p.Id);
                    appIds.Add(app.Id);
                }
            }

            var batch = new PaymentBatch
            {
                Id = Guid.NewGuid().ToString(),
                BranchId = branch.Id,
                PaymentIds = paymentIds,
                AppointmentIds = appIds,
                PaymentCount = payments.Count,
                TotalGrossAmount = grossTotal,
                TotalPlatformCommission = commTotal,
                TotalNetPayout = payoutTotal,
                Status = PaymentBatchStatus.Initiated,
                CreatedBy = currentUserId,
                Notes = request.Notes ?? "Payout requested by laboratory owner."
            };

            _context.PaymentBatches.Add(batch);

            // Link payments to the batch
            foreach (var p in payments)
            {
                p.BatchId = batch.Id;
                _context.Payments.Update(p);
            }

            await _context.SaveChangesAsync(cancellationToken);

            // Send WhatsApp notification to Admin!
            var adminUser = await _context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Role == UserRole.SuperAdmin, cancellationToken);
            
            if (adminUser != null && !string.IsNullOrEmpty(adminUser.Phone))
            {
                var adminMsg = $"🔔 *New Payout Request!*\n\n" +
                               $"Lab: *{branch.Name}*\n" +
                               $"Bookings Count: {batch.PaymentCount}\n" +
                               $"Amount Owed: ₹{batch.TotalNetPayout}\n" +
                               $"Please pay manually and approve in the console.";
                
                try { await _whatsAppService.SendTextMessageAsync(adminUser.Phone, adminMsg); } catch { }
            }

            return Ok(ApiResponse<PaymentBatch>.SuccessResult(batch, "Payout batch request submitted successfully."));
        }

        [HttpPost("payment-batches/{batchId}/reject")]
        [Authorize]
        [EndpointSummary("Reject batch payment receipt")]
        [EndpointDescription("Lab owner rejects the payout receipt, releasing payments back to unbatched status. Status becomes Abandoned.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> RejectBatchReceipt([FromRoute] string batchId, CancellationToken cancellationToken)
        {
            var batch = await _context.PaymentBatches
                .FirstOrDefaultAsync(pb => pb.Id == batchId, cancellationToken);

            if (batch == null)
            {
                return NotFound(ApiResponse.FailureResult("Payment batch not found."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == batch.BranchId, cancellationToken);

            if (branch == null || branch.LabUserId != currentUserId)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.FailureResult("Access denied to this batch."));
            }

            if (batch.Status != PaymentBatchStatus.Paid && batch.Status != PaymentBatchStatus.Initiated)
            {
                return BadRequest(ApiResponse.FailureResult("Only initiated or paid batches can be rejected."));
            }

            // Release all payments in this batch
            var payments = await _context.Payments
                .Where(p => p.BatchId == batchId)
                .ToListAsync(cancellationToken);

            foreach (var payment in payments)
            {
                payment.BatchId = null;
                _context.Payments.Update(payment);
            }

            batch.Status = PaymentBatchStatus.Abandoned;
            batch.Notes = (batch.Notes ?? "") + " [Rejected by Lab]";

            _context.PaymentBatches.Update(batch);
            await _context.SaveChangesAsync(cancellationToken);

            // Notify Admin of rejection
            var adminUser = await _context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Role == UserRole.SuperAdmin, cancellationToken);

            if (adminUser != null && !string.IsNullOrEmpty(adminUser.Phone))
            {
                var adminMsg = $"❌ *Payout Request Rejected by Lab!*\n\n" +
                               $"Lab: *{branch.Name}*\n" +
                               $"Batch ID: {batch.Id[..8].ToUpper()}\n" +
                               $"Amount Owed: ₹{batch.TotalNetPayout}\n" +
                               $"The payments have been returned to the unbatched pool.";

                try { await _whatsAppService.SendTextMessageAsync(adminUser.Phone, adminMsg); } catch { }
            }

            return Ok(ApiResponse.SuccessResult("Payment batch receipt rejected and payments released."));
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

        [HttpPost("change-password")]
        [Authorize]
        [EndpointSummary("Change lab user password")]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.OldPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
            {
                return BadRequest(ApiResponse.FailureResult("Old password and new password are required."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId, cancellationToken);
            if (user == null)
            {
                return NotFound(ApiResponse.FailureResult("User not found."));
            }

            if (!_passwordHasher.Verify(request.OldPassword, user.PasswordHash ?? string.Empty))
            {
                return BadRequest(ApiResponse.FailureResult("Incorrect old password."));
            }

            user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
            user.UpdatedAt = DateTime.UtcNow;

            _context.Users.Update(user);
            await _context.SaveChangesAsync(cancellationToken);

            return Ok(ApiResponse.SuccessResult("Password changed successfully."));
        }

        [HttpPost("change-email/request")]
        [Authorize]
        [EndpointSummary("Request email change with pre-verification")]
        public async Task<IActionResult> RequestChangeEmail([FromBody] ChangeEmailRequest request, CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.NewEmail))
            {
                return BadRequest(ApiResponse.FailureResult("New email address is required."));
            }

            var targetEmail = request.NewEmail.Trim().ToLower();

            // Check if email already in use
            var emailInUse = await _context.Users.AnyAsync(u => u.Email != null && u.Email.ToLower() == targetEmail && !u.IsDeleted, cancellationToken);
            if (emailInUse)
            {
                return BadRequest(ApiResponse.FailureResult("Email is already in use by another account."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId, cancellationToken);
            if (user == null)
            {
                return NotFound(ApiResponse.FailureResult("User not found."));
            }

            var random = new Random();
            string verificationCode = random.Next(100000, 999999).ToString();

            user.PendingNewEmail = targetEmail;
            user.EmailVerificationToken = verificationCode;
            user.EmailVerificationTokenExpiry = DateTime.UtcNow.AddMinutes(15);

            _context.Users.Update(user);
            await _context.SaveChangesAsync(cancellationToken);

            // Send verification email
            var emailSubject = "Apenir Email Verification Code";
            var emailBody = $"Your code to change your Apenir email to {targetEmail} is: <strong>{verificationCode}</strong>. Valid for 15 minutes.";
            await _emailService.SendEmailAsync(targetEmail, emailSubject, emailBody);

            return Ok(ApiResponse.SuccessResult("Verification code sent to the new email address successfully."));
        }

        [HttpPost("change-email/confirm")]
        [Authorize]
        [EndpointSummary("Confirm email change")]
        public async Task<IActionResult> ConfirmChangeEmail([FromBody] ConfirmChangeEmailRequest request, CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Code))
            {
                return BadRequest(ApiResponse.FailureResult("Verification code is required."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId, cancellationToken);
            if (user == null)
            {
                return NotFound(ApiResponse.FailureResult("User not found."));
            }

            if (user.EmailVerificationToken != request.Code.Trim() || user.EmailVerificationTokenExpiry < DateTime.UtcNow)
            {
                return BadRequest(ApiResponse.FailureResult("Invalid or expired verification code."));
            }

            if (string.IsNullOrWhiteSpace(user.PendingNewEmail))
            {
                return BadRequest(ApiResponse.FailureResult("No pending email change request found."));
            }

            user.Email = user.PendingNewEmail;
            user.PendingNewEmail = null;
            user.EmailVerificationToken = null;
            user.EmailVerificationTokenExpiry = null;
            user.UpdatedAt = DateTime.UtcNow;

            _context.Users.Update(user);
            await _context.SaveChangesAsync(cancellationToken);

            return Ok(ApiResponse.SuccessResult("Email updated successfully."));
        }

        [HttpPost("appointments/upload-report")]
        [Authorize]
        [Consumes("multipart/form-data")]
        [EndpointSummary("Upload diagnostic test report PDF")]
        public async Task<IActionResult> UploadReport(
            [FromForm] string? appointmentId,
            [FromForm] string? memberUniqueNumber,
            [FromForm] IFormFile file,
            CancellationToken cancellationToken)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(ApiResponse.FailureResult("PDF report file is required."));
            }

            if (!file.ContentType.Contains("pdf", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(ApiResponse.FailureResult("Only PDF files are allowed."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);
            if (branch == null)
            {
                return BadRequest(ApiResponse.FailureResult("Branch not found for current user."));
            }

            Appointment? appointment = null;

            if (!string.IsNullOrWhiteSpace(appointmentId))
            {
                appointment = await _context.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId && a.BranchId == branch.Id, cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(memberUniqueNumber))
            {
                var member = await _context.AppointmentMembers.FirstOrDefaultAsync(m => m.UniqueNumber == memberUniqueNumber.Trim(), cancellationToken);
                if (member != null)
                {
                    appointment = await _context.Appointments.FirstOrDefaultAsync(a => a.Id == member.AppointmentId && a.BranchId == branch.Id, cancellationToken);
                }
            }

            if (appointment == null)
            {
                return NotFound(ApiResponse.FailureResult("Appointment not found. Make sure the ID or member unique ID is correct and belongs to your branch."));
            }

            // Save file
            var reportsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "reports");
            if (!Directory.Exists(reportsFolder))
            {
                Directory.CreateDirectory(reportsFolder);
            }

            var fileName = $"{appointment.AppointmentNumber}_{Guid.NewGuid().ToString("N").Substring(0, 8)}.pdf";
            var filePath = Path.Combine(reportsFolder, fileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }

            var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
            var reportUrl = $"{baseUrl}/reports/{fileName}";

            appointment.ReportPdfPath = reportUrl;
            appointment.Status = AppointmentStatus.Completed; // Mark completed when report is uploaded
            appointment.UpdatedAt = DateTime.UtcNow;

            _context.Appointments.Update(appointment);
            await _context.SaveChangesAsync(cancellationToken);

            // Send report PDF via WhatsApp to customer
            var customer = await _context.Users.FirstOrDefaultAsync(u => u.Id == appointment.CustomerUserId, cancellationToken);
            if (customer != null && !string.IsNullOrEmpty(customer.Phone))
            {
                var caption = $"🔬 *Your Lab Reports are Ready!*\n\nHello {customer.Name ?? "Customer"}, your diagnostic reports for booking *{appointment.AppointmentNumber}* have been uploaded. You can download the PDF here.";
                await _whatsAppService.SendTextMessageAsync(customer.Phone, caption);
                await _whatsAppService.SendDocumentMessageAsync(customer.Phone, reportUrl, $"{appointment.AppointmentNumber}_Report.pdf");
            }

            return Ok(ApiResponse<string>.SuccessResult(reportUrl, "Report PDF uploaded and sent to customer successfully."));
        }
    }

    public record InviteStaffRequest(string Email, string Name);
    
    public class CompleteStaffRegistrationRequest
    {
        public string Token { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
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

    public record LabSearchBatchesRequest(
        PaymentBatchStatus? Status,
        DateTime? StartDate,
        DateTime? EndDate,
        int PageNumber = 1,
        int RowsPerPage = 10
    );

    public class LabBatchListDto
    {
        public string Id { get; set; } = string.Empty;
        public string BranchId { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;
        public int PaymentCount { get; set; }
        public decimal TotalGrossAmount { get; set; }
        public decimal TotalPlatformCommission { get; set; }
        public decimal TotalNetPayout { get; set; }
        public PaymentBatchStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ConfirmedAt { get; set; }
        public string? Notes { get; set; }
    }

    public class LabBatchDetailResponse
    {
        public string Id { get; set; } = string.Empty;
        public string BranchId { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;
        public int PaymentCount { get; set; }
        public decimal TotalGrossAmount { get; set; }
        public decimal TotalPlatformCommission { get; set; }
        public decimal TotalNetPayout { get; set; }
        public PaymentBatchStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ConfirmedAt { get; set; }
        public string? ConfirmedByLabUser { get; set; }
        public string? Notes { get; set; }
        public List<LabBatchPaymentItemDto> Payments { get; set; } = new();
    }

    public class LabBatchPaymentItemDto
    {
        public string PaymentId { get; set; } = string.Empty;
        public string AppointmentId { get; set; } = string.Empty;
        public string AppointmentNumber { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public decimal PlatformCommission { get; set; }
        public decimal LabPayout { get; set; }
        public DateTime? PaidAt { get; set; }
        public PaymentMethod? PaymentMethod { get; set; }
    }

    public class SubmitReportRequest
    {
        public string MemberId { get; set; } = string.Empty;
        public string ResultData { get; set; } = string.Empty;
    }

    public class AppointmentReportDto
    {
        public string ReportId { get; set; } = string.Empty;
        public string AppointmentId { get; set; } = string.Empty;
        public string MemberId { get; set; } = string.Empty;
        public string MemberName { get; set; } = string.Empty;
        public string? FileUrl { get; set; }
        public string? FileName { get; set; }
        public string? ResultData { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public record LabSearchAppointmentsRequest(
        string? AppointmentNumber,
        AppointmentStatus? Status,
        string? CustomerName,
        string? StaffName,
        DateOnly? StartDate,
        DateOnly? EndDate,
        int PageNumber = 1,
        int RowsPerPage = 10
    );

    public class AddStaffRequest
    {
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class EditStaffRequest
    {
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public string? Password { get; set; }
    }

    public class AssignStaffRequest
    {
        public string StaffId { get; set; } = string.Empty;
        public string? BranchId { get; set; }
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

    public class LabDashboardSummaryResponse
    {
        public int TotalBookings { get; set; }
        public int AssignedCount { get; set; }
        public int CollectedCount { get; set; }
        public int CompletedCount { get; set; }
        public int PendingCount { get; set; }
        public int ConfirmedCount { get; set; }
        public int CancelledCount { get; set; }
        public decimal GrossRevenue { get; set; }
        public decimal NetPayout { get; set; }
        public List<DailySlotCalendarDto> DailyCalendar { get; set; } = new();
        public LabDashboardExtraStats ExtraStats { get; set; } = new();
    }

    public class DailySlotCalendarDto
    {
        public DateOnly Date { get; set; }
        public string DayName { get; set; } = string.Empty;
        public List<SlotCalendarDetailDto> Slots { get; set; } = new();
    }

    public class SlotCalendarDetailDto
    {
        public string SlotId { get; set; } = string.Empty;
        public string TimeWindow { get; set; } = string.Empty;
        public int MaxCapacity { get; set; }
        public int BookedCount { get; set; }
        public bool IsAvailable { get; set; }
    }

    public class LabDashboardExtraStats
    {
        public decimal AverageOrderValue { get; set; }
        public decimal CompletionRate { get; set; }
        public decimal CancellationRate { get; set; }
        public int ActiveStaffCount { get; set; }
        public List<StaffWorkloadDto> StaffWorkload { get; set; } = new();
        public List<SlotCapacityStatDto> SlotOccupancy { get; set; } = new();
        public PatientDemographicsDto PatientDemographics { get; set; } = new();
    }

    public class StaffWorkloadDto
    {
        public string StaffId { get; set; } = string.Empty;
        public string StaffName { get; set; } = string.Empty;
        public int ActiveAssignmentsCount { get; set; }
        public int CompletedAssignmentsCount { get; set; }
    }

    public class SlotCapacityStatDto
    {
        public string SlotId { get; set; } = string.Empty;
        public string TimeWindow { get; set; } = string.Empty;
        public int TotalCapacity { get; set; }
        public int BookedCount { get; set; }
        public double OccupancyPercentage { get; set; }
    }

    public class PatientDemographicsDto
    {
        public int MaleCount { get; set; }
        public int FemaleCount { get; set; }
        public int OtherCount { get; set; }
        public double AverageAge { get; set; }
    }

    public class UpdateBranchDetailsRequest
    {
        public string Name { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string District { get; set; } = string.Empty;
        public string Pincode { get; set; } = string.Empty;
        public decimal Latitude { get; set; }
        public decimal Longitude { get; set; }
        public double ServiceRangeKm { get; set; }
        public decimal? PerKmCharge { get; set; }
    }

    public class ChangePasswordRequest
    {
        public string OldPassword { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
    }

    public class ChangeEmailRequest
    {
        public string NewEmail { get; set; } = string.Empty;
    }

    public class ConfirmChangeEmailRequest
    {
        public string Code { get; set; } = string.Empty;
    }

    public class LabCreateBatchRequest
    {
        public List<string> PaymentIds { get; set; } = new();
        public string? Notes { get; set; }
    }
}
