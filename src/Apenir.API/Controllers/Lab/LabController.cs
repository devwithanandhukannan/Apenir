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
            [FromQuery] string branchId,
            [FromQuery] DateOnly? startDate,
            [FromQuery] DateOnly? endDate,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(branchId))
            {
                return BadRequest(ApiResponse.FailureResult("Branch ID is required."));
            }

            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);
            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            // Default date range: today to next 7 days
            var start = startDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var end = endDate ?? start.AddDays(7);

            var appointments = await _context.Appointments
                .Where(a => a.BranchId == branchId && a.AppointmentSlot!.SlotDate >= start && a.AppointmentSlot!.SlotDate <= end)
                .Include(a => a.CustomerUser)
                .Include(a => a.AssignedStaff)
                .Include(a => a.AppointmentSlot)
                .ToListAsync(cancellationToken);

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
                .Where(s => s.BranchId == branchId && s.SlotDate >= start && s.SlotDate <= end)
                .OrderBy(s => s.SlotDate)
                .ThenBy(s => s.StartTime)
                .ToListAsync(cancellationToken);

            var response = new LabDashboardSummaryResponse
            {
                TodayBookingsCount = appointments.Count(a => a.AppointmentSlot!.SlotDate == DateOnly.FromDateTime(DateTime.UtcNow)),
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
        public async Task<IActionResult> GetPendingAssignments([FromQuery] string branchId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(branchId))
            {
                return BadRequest(ApiResponse.FailureResult("Branch ID is required."));
            }

            var pending = await _context.Appointments
                .Where(a => a.BranchId == branchId && a.AssignedStaffId == null && 
                            (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed))
                .Include(a => a.CustomerUser)
                .Include(a => a.AppointmentSlot)
                .OrderBy(a => a.CreatedAt)
                .ToListAsync(cancellationToken);

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

        [HttpGet("branches/{branchId}/services")]
        [Authorize]
        [EndpointSummary("Get all diagnostic services active or available for a branch")]
        [EndpointDescription("Returns all branch-specific services merged with the master service metadata.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<BranchServiceDto>>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetBranchServices([FromRoute] string branchId, CancellationToken cancellationToken)
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

        [HttpPut("branches/{branchId}/services/{serviceId}")]
        [Authorize]
        [EndpointSummary("Update custom pricing and active status for a branch service")]
        [EndpointDescription("Overrides pricing or toggles the status of a specific service for the branch.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> UpdateBranchService(
            [FromRoute] string branchId,
            [FromRoute] string serviceId,
            [FromBody] UpdateBranchServiceRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                return BadRequest(ApiResponse.FailureResult("Request body is required."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);
            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            if (branch.LabUserId != currentUserId)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.FailureResult("Access denied to this branch configuration."));
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

        [HttpGet("staff")]
        [Authorize]
        [EndpointSummary("Get all active staff/phlebotomists for the lab")]
        [EndpointDescription("Returns a list of all active staff members registered under the lab owner's branch.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<User>>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetStaffList([FromQuery] string branchId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(branchId))
            {
                return BadRequest(ApiResponse.FailureResult("Branch ID is required."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();
            var branch = await _context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);

            if (branch == null || branch.LabUserId != currentUserId)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.FailureResult("Access denied to this branch."));
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

        [HttpPost("payment-batches/list")]
        [Authorize]
        [EndpointSummary("List payment batches for the lab")]
        [EndpointDescription("Returns a paginated list of payment batches for the logged-in lab owner's branches.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<PaginatedList<LabBatchListDto>>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        public async Task<IActionResult> ListPaymentBatches([FromBody] LabSearchBatchesRequest? request, CancellationToken cancellationToken)
        {
            var currentUserId = _currentUserService.UserId?.ToString();

            var branches = await _context.Branches.AsNoTracking()
                .Where(b => b.LabUserId == currentUserId)
                .ToListAsync(cancellationToken);

            var branchIds = branches.Select(b => b.Id).ToList();
            if (branchIds.Count == 0)
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
                return Ok(ApiResponse<PaginatedList<LabBatchListDto>>.SuccessResult(emptyResponse, "No branches found for this lab owner."));
            }

            var branchDict = branches.ToDictionary(b => b.Id, b => b.Name);

            var query = _context.PaymentBatches.AsNoTracking()
                .Where(pb => branchIds.Contains(pb.BranchId));

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
                BranchName = branchDict.TryGetValue(pb.BranchId, out var name) ? name : string.Empty,
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

}
