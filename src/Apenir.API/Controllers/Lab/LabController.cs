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


        [HttpGet("dashboard/summary")]
        [Authorize]
        [EndpointSummary("Get dashboard summary metrics and slots")]
        [EndpointDescription("Returns KPI metrics, Daily Slot Calendar data, and active stats for a branch within a date range.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<LabDashboardSummaryResponse>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetDashboardSummary(
            [FromQuery] DateOnly? startDate,
            [FromQuery] DateOnly? endDate,
            CancellationToken cancellationToken)
        {
            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Lab/branch configuration not found for this user."));
            }

            var branchId = branch.Id;

            // Default date range: today to next 7 days
            var start = startDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var end = endDate ?? start.AddDays(7);

            // Fetch the slots first to filter appointments by slot dates
            var slots = await _context.AppointmentSlots.AsNoTracking()
                .Where(s => s.BranchId == branchId && s.SlotDate >= start && s.SlotDate <= end)
                .OrderBy(s => s.SlotDate)
                .ThenBy(s => s.StartTime)
                .ToListAsync(cancellationToken);

            var slotIds = slots.Select(s => s.Id).ToList();

            var appointments = await _context.Appointments.AsNoTracking()
                .Where(a => a.BranchId == branchId && slotIds.Contains(a.AppointmentSlotId))
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
                Appointments = appointments,
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
        public async Task<IActionResult> GetPendingAssignments(CancellationToken cancellationToken)
        {
            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Lab/branch configuration not found for this user."));
            }

            var branchId = branch.Id;

            var pending = await _context.Appointments.AsNoTracking()
                .Where(a => a.BranchId == branchId && a.AssignedStaffId == null && 
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

            var branch = await _context.Branches
                .FirstOrDefaultAsync(b => b.Id == appointment.BranchId, cancellationToken);

            if (branch == null || branch.LabUserId != currentUserId)
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
        [EndpointSummary("Get all diagnostic services active or available for a branch")]
        [EndpointDescription("Returns all branch-specific services merged with the master service metadata.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<BranchServiceDto>>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetBranchServices(CancellationToken cancellationToken)
        {
            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Lab/branch configuration not found for this user."));
            }

            var branchId = branch.Id;

            var branchServices = await _context.BranchServices
                .Where(bs => bs.BranchId == branchId)
                .ToListAsync(cancellationToken);

            var serviceIds = branchServices.Select(bs => bs.ServiceId).Distinct().ToList();
            var services = await _context.Services
                .Where(s => serviceIds.Contains(s.Id))
                .ToListAsync(cancellationToken);

            var servicesDict = services.ToDictionary(s => s.Id);

            var result = branchServices.Select(bs =>
            {
                servicesDict.TryGetValue(bs.ServiceId, out var svc);
                return new BranchServiceDto
                {
                    BranchServiceId = bs.Id,
                    ServiceId = bs.ServiceId,
                    Name = svc?.Name ?? string.Empty,
                    Category = svc?.Category ?? string.Empty,
                    Description = svc?.Description ?? string.Empty,
                    BasePrice = svc?.BasePrice ?? 0,
                    CustomPrice = bs.CustomPrice,
                    CustomCommissionPct = bs.CustomCommissionPct,
                    IsActive = bs.IsActive
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
        [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ApiResponse))]
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

            var branchId = branch.Id;

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
                await _context.SaveChangesAsync(cancellationToken);
            }

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

        [HttpGet("staff")]
        [Authorize]
        [EndpointSummary("Get all active staff/phlebotomists for the lab")]
        [EndpointDescription("Returns a list of all active staff members registered under the lab owner's branch.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<User>>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetStaffList(CancellationToken cancellationToken)
        {
            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.LabUserId == currentUserId, cancellationToken);

            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Lab/branch configuration not found for this user."));
            }

            var staff = await _context.Users.AsNoTracking()
                .Where(u => u.Role == UserRole.Staff && u.LabId == branch.LabId && !u.IsDeleted && u.IsActive == true)
                .Select(u => new User
                {
                    Id = u.Id,
                    Name = u.Name,
                    Email = u.Email,
                    Phone = u.Phone,
                    Status = u.Status,
                    IsActive = u.IsActive
                })
                .ToListAsync(cancellationToken);

            return Ok(ApiResponse<List<User>>.SuccessResult(staff, "Staff list retrieved successfully."));
        }

        [HttpPost("staff")]
        [Authorize]
        [EndpointSummary("Add a new staff/phlebotomist member")]
        [EndpointDescription("Creates a new staff member account under the lab.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<User>))]
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

            // Hide password hash for response
            newStaff.PasswordHash = null;

            return Ok(ApiResponse<User>.SuccessResult(newStaff, "Staff member created successfully."));
        }

        [HttpPut("staff/{staffId}")]
        [Authorize]
        [EndpointSummary("Edit a staff/phlebotomist member")]
        [EndpointDescription("Updates details, active status, or resets password for a staff member under the lab.")]
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

            // Flat queries — no .Include()
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

            // Build lookup dictionaries for in-memory binding
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

            if (batch.Status != PaymentBatchStatus.Initiated)
            {
                return BadRequest(ApiResponse.FailureResult("Only batches with 'Initiated' status can be confirmed."));
            }

            batch.Status = PaymentBatchStatus.Settled;
            batch.ConfirmedByLabUser = currentUserId;
            batch.ConfirmedAt = DateTime.UtcNow;

            _context.PaymentBatches.Update(batch);
            await _context.SaveChangesAsync(cancellationToken);

            return Ok(ApiResponse.SuccessResult("Batch payment receipt confirmed successfully."));
        }
    }

    public class LabDashboardSummaryResponse
    {
        public int TodayBookingsCount { get; set; }
        public LabDashboardFunnel Funnel { get; set; } = new();
        public LabDashboardFinancials Financials { get; set; } = new();
        public List<AppointmentSlot> Slots { get; set; } = new();
        public List<Appointment> Appointments { get; set; } = new();
        public LabDashboardExtraStats ExtraStats { get; set; } = new();
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

}
