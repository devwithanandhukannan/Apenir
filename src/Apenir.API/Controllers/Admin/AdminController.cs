using System;
using System.Collections.Generic;
using System.Linq;
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

        [HttpPost("labs")]
        [EndpointSummary("Search and Filter Labs")]
        [EndpointDescription("Returns a list of labs matching optional name, district, city, and status filter criteria.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<Branch>>))]
        public async Task<IActionResult> SearchLabs([FromBody] SearchLabsRequest? request, CancellationToken cancellationToken)
        {
            var query = _context.Branches.Include(b => b.LabUser).AsNoTracking();

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

            var labs = await query.ToListAsync(cancellationToken);
            return Ok(ApiResponse<List<Branch>>.SuccessResult(labs, "LABS_RETRIEVED"));
        }

        [HttpPost("labs/{labId}/staff")]
        [EndpointSummary("Search and Filter Staff associated with a lab")]
        [EndpointDescription("Returns a paginated list of staff members (users with role Staff) assigned to the specified lab, with optional filters.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<PaginatedList<User>>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetLabStaff([FromRoute] string labId, [FromBody] SearchLabStaffRequest? request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(labId))
            {
                return BadRequest(ApiResponse.FailureResult("Lab ID is required."));
            }

            var lab = await _context.Branches.FirstOrDefaultAsync(b => b.Id == labId, cancellationToken);
            if (lab == null)
            {
                return NotFound(ApiResponse.FailureResult("Lab not found."));
            }

            var query = _context.Users.AsNoTracking()
                .Where(u => u.Role == UserRole.Staff && u.LabId == lab.LabId && !u.IsDeleted);

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

                if (!string.IsNullOrWhiteSpace(request.Phone))
                {
                    var phoneQuery = request.Phone.Trim().ToLower();
                    query = query.Where(u => u.Phone != null && u.Phone.ToLower().Contains(phoneQuery));
                }

                if (!string.IsNullOrWhiteSpace(request.Status))
                {
                    var statusQuery = request.Status.Trim().ToLower();
                    query = query.Where(u => u.Status != null && u.Status.ToLower() == statusQuery);
                }
            }

            var totalRows = await query.CountAsync(cancellationToken);
            var pageNumber = request?.PageNumber ?? 1;
            var rowsPerPage = request?.RowsPerPage ?? 10;
            if (pageNumber < 1) pageNumber = 1;
            if (rowsPerPage < 1) rowsPerPage = 10;

            var pageCount = (int)Math.Ceiling((double)totalRows / rowsPerPage);

            var staffList = await query
             .OrderBy(u => u.Name)
             .Skip((pageNumber - 1) * rowsPerPage)
             .Take(rowsPerPage)
             .Select(u => new User
             {
                 Id = u.Id,
                 Name = u.Name,
                 Email = u.Email,
                 Phone = u.Phone,
                 Status = u.Status
             })
     .ToListAsync(cancellationToken);


            var response = new PaginatedList<User>
            {
                Items = staffList,
                PageNumber = pageNumber,
                PageSize = staffList.Count,
                RowsPerPage = rowsPerPage,
                PageCount = pageCount,
                TotalRows = totalRows
            };

            return Ok(ApiResponse<PaginatedList<User>>.SuccessResult(response, "Lab staff retrieved successfully."));
        }

        [HttpPost("labs/{labId}/appointments")]
        [EndpointSummary("Search and Filter Appointments for a lab")]
        [EndpointDescription("Returns a paginated list of all appointments scheduled for the specified lab, with optional filters.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<PaginatedList<Appointment>>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetLabAppointments([FromRoute] string labId, [FromBody] SearchLabAppointmentsRequest? request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(labId))
            {
                return BadRequest(ApiResponse.FailureResult("Lab ID is required."));
            }

            var labExists = await _context.Branches.AnyAsync(b => b.Id == labId, cancellationToken);
            if (!labExists)
            {
                return NotFound(ApiResponse.FailureResult("Lab not found."));
            }

            var query = _context.Appointments.AsNoTracking()
                .Where(a => a.BranchId == labId);

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
                    var slotsQuery = _context.AppointmentSlots.AsNoTracking().Where(s => s.BranchId == labId);
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

                // Make sure password hashes are removed for security
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

            return Ok(ApiResponse<PaginatedList<Appointment>>.SuccessResult(response, "Lab appointments retrieved successfully."));
        }


        [HttpGet("labs/{labId}/details")]
        [EndpointSummary("Get lab details, staff and metrics")]
        [EndpointDescription("Returns lab details, primary contact user info, list of associated staff, and general statistics like total revenue and appointment count.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<LabDetailsResponse>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetLabDetails([FromRoute] string labId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(labId))
            {
                return BadRequest(ApiResponse.FailureResult("Lab ID is required."));
            }

            var lab = await _context.Branches
                .FirstOrDefaultAsync(b => b.Id == labId, cancellationToken);

            if (lab == null)
            {
                return NotFound(ApiResponse.FailureResult("Lab not found."));
            }

            if (!string.IsNullOrEmpty(lab.LabUserId))
            {
                lab.LabUser = await _context.Users
                    .FirstOrDefaultAsync(u => u.Id == lab.LabUserId, cancellationToken);
            }

            var appointments = await _context.Appointments
                .Where(a => a.BranchId == labId)
                .ToListAsync(cancellationToken);

            var totalAppointments = appointments.Count;
            var completedAppointments = appointments.Count(a => a.Status == AppointmentStatus.Completed);
            var pendingAppointments = appointments.Count(a => a.Status == AppointmentStatus.Pending);
            var totalRevenue = appointments.Sum(a => a.TotalAmount);
            var totalLabPayout = appointments.Sum(a => a.LabPayout);

            var staff = new List<User>();
            if (!string.IsNullOrEmpty(lab.LabId))
            {
                staff = await _context.Users
                    .Where(u => u.Role == UserRole.Staff && u.LabId == lab.LabId && !u.IsDeleted)
                    .ToListAsync(cancellationToken);
            }

            var servicesCount = await _context.BranchServices
                .CountAsync(bs => bs.BranchId == labId && bs.IsActive, cancellationToken);

            var activeSlotsCount = await _context.AppointmentSlots
                .CountAsync(s => s.BranchId == labId && s.IsAvailable, cancellationToken);

            if (lab.LabUser != null)
            {
                lab.LabUser.PasswordHash = null;
            }
            foreach (var s in staff)
            {
                s.PasswordHash = null;
            }

            var response = new LabDetailsResponse
            {
                Name = lab.Name,
                District = lab.District,
                City = lab.City,
                Pincode = lab.Pincode,
                Latitude = lab.Latitude,
                Longitude = lab.Longitude,
                Phone = lab.Phone,
                IsActive = lab.IsActive,
                Status = lab.Status,
                LabId = lab.LabId,
                CreatedAt = lab.CreatedAt,
                ContactPerson = lab.LabUser,
                Staff = staff,
                Stats = new LabStats
                {
                    TotalAppointments = totalAppointments,
                    CompletedAppointments = completedAppointments,
                    PendingAppointments = pendingAppointments,
                    TotalRevenue = totalRevenue,
                    TotalLabPayout = totalLabPayout,
                    TotalStaffCount = staff.Count,
                    TotalServicesCount = servicesCount,
                    ActiveSlotsCount = activeSlotsCount
                }
            };

            return Ok(ApiResponse<LabDetailsResponse>.SuccessResult(response, "Lab details retrieved successfully."));
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

        [HttpPut("labs/{labId}/status")]
        [EndpointSummary("Update active status of a lab/branch account")]
        [EndpointDescription("Allows administrators to activate or deactivate a lab branch account.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> UpdateLabStatus(
            [FromRoute] string labId,
            [FromBody] UpdateLabStatusRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                return BadRequest(ApiResponse.FailureResult("Request body is required."));
            }

            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == labId, cancellationToken);
            if (branch == null)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            branch.IsActive = request.IsActive;
            branch.Status = request.IsActive ? "Active" : "Inactive";
            _context.Branches.Update(branch);

            if (!string.IsNullOrEmpty(branch.LabUserId))
            {
                var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == branch.LabUserId, cancellationToken);
                if (user != null)
                {
                    user.IsActive = request.IsActive;
                    user.Status = request.IsActive ? "Active" : "Inactive";
                    _context.Users.Update(user);
                }
            }

            await _context.SaveChangesAsync(cancellationToken);
            return Ok(ApiResponse.SuccessResult($"Branch account status updated to {(request.IsActive ? "Active" : "Inactive")} successfully."));
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

        [HttpGet("branches/{branchId}/services")]
        [EndpointSummary("Get all services for a branch with override details")]
        [EndpointDescription("Returns all platform and custom services visible to a branch, merged with any per-branch price, commission, and active status overrides.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<AdminBranchServiceDto>>))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetBranchServicesAdmin(
            [FromRoute] string branchId,
            CancellationToken cancellationToken)
        {
            var branchExists = await _context.Branches.AnyAsync(b => b.Id == branchId, cancellationToken);
            if (!branchExists)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            // All services: admin-created (CreatedByBranchId == null) OR created by this branch
            var allServices = await _context.Services.AsNoTracking()
                .Where(s => s.IsActive && (s.CreatedByBranchId == null || s.CreatedByBranchId == branchId))
                .OrderBy(s => s.Category).ThenBy(s => s.Name)
                .ToListAsync(cancellationToken);

            var branchServices = await _context.BranchServices.AsNoTracking()
                .Where(bs => bs.BranchId == branchId)
                .ToListAsync(cancellationToken);

            var result = allServices.Select(s =>
            {
                var bs = branchServices.FirstOrDefault(x => x.ServiceId == s.Id);
                return new AdminBranchServiceDto
                {
                    BranchServiceId = bs?.Id,
                    ServiceId = s.Id,
                    Name = s.Name,
                    Category = s.Category,
                    Description = s.Description,
                    BasePrice = s.BasePrice,
                    DefaultCommissionPct = s.PlatformCommissionPct,
                    CustomPrice = bs?.CustomPrice,
                    CustomCommissionPct = bs?.CustomCommissionPct,
                    IsEnrolled = bs != null,
                    IsActive = bs?.IsActive ?? false,
                    IsCustom = s.CreatedByBranchId != null
                };
            }).ToList();

            return Ok(ApiResponse<List<AdminBranchServiceDto>>.SuccessResult(result, "Branch services retrieved successfully."));
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

        [HttpPut("branches/{branchId}/services/{serviceId}")]
        [EndpointSummary("Admin override of a specific branch's service pricing, commission, or active status")]
        [EndpointDescription("Allows administrators to customize the price, commission, or active status of a service for a specific branch.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> AdminOverrideBranchService(
            [FromRoute] string branchId,
            [FromRoute] string serviceId,
            [FromBody] AdminOverrideBranchServiceRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                return BadRequest(ApiResponse.FailureResult("Request body is required."));
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
                    CustomCommissionPct = request.CustomCommissionPct,
                    IsActive = request.IsActive
                };
                _context.BranchServices.Add(branchService);
            }
            else
            {
                branchService.CustomPrice = request.CustomPrice;
                branchService.CustomCommissionPct = request.CustomCommissionPct;
                branchService.IsActive = request.IsActive;
                _context.BranchServices.Update(branchService);
            }

            await _context.SaveChangesAsync(cancellationToken);
            return Ok(ApiResponse.SuccessResult("Branch service override saved successfully by Admin."));
        }

        [HttpPost("finance/payments")]
        [EndpointSummary("Search and Filter Customer Payments")]
        [EndpointDescription("Returns a paginated list of customer payments to the company, with optional filters.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<PaginatedList<Payment>>))]
        public async Task<IActionResult> GetPayments([FromBody] SearchPaymentsRequest? request, CancellationToken cancellationToken)
        {
            var query = _context.Payments.AsNoTracking();

            if (request != null)
            {
                if (request.Status.HasValue)
                {
                    query = query.Where(p => p.Status == request.Status.Value);
                }

                if (request.PaymentMethod.HasValue)
                {
                    query = query.Where(p => p.PaymentMethod == request.PaymentMethod.Value);
                }

                if (!string.IsNullOrWhiteSpace(request.CustomerName))
                {
                    var nameQuery = request.CustomerName.Trim().ToLower();
                    var customerIds = await _context.Users.AsNoTracking()
                        .Where(u => u.Role == UserRole.Customer && u.Name != null && u.Name.ToLower().Contains(nameQuery))
                        .Select(u => u.Id)
                        .ToListAsync(cancellationToken);

                    var appointmentIds = await _context.Appointments.AsNoTracking()
                        .Where(a => customerIds.Contains(a.CustomerUserId))
                        .Select(a => a.Id)
                        .ToListAsync(cancellationToken);

                    query = query.Where(p => appointmentIds.Contains(p.AppointmentId));
                }

                if (!string.IsNullOrWhiteSpace(request.LabName))
                {
                    var labQuery = request.LabName.Trim().ToLower();
                    var branchIds = await _context.Branches.AsNoTracking()
                        .Where(b => b.Name != null && b.Name.ToLower().Contains(labQuery))
                        .Select(b => b.Id)
                        .ToListAsync(cancellationToken);

                    var appointmentIds = await _context.Appointments.AsNoTracking()
                        .Where(a => branchIds.Contains(a.BranchId))
                        .Select(a => a.Id)
                        .ToListAsync(cancellationToken);

                    query = query.Where(p => appointmentIds.Contains(p.AppointmentId));
                }

                if (request.StartDate.HasValue)
                {
                    query = query.Where(p => p.CreatedAt >= request.StartDate.Value);
                }

                if (request.EndDate.HasValue)
                {
                    query = query.Where(p => p.CreatedAt <= request.EndDate.Value);
                }
            }

            var totalRows = await query.CountAsync(cancellationToken);
            var pageNumber = request?.PageNumber ?? 1;
            var rowsPerPage = request?.RowsPerPage ?? 10;
            if (pageNumber < 1) pageNumber = 1;
            if (rowsPerPage < 1) rowsPerPage = 10;

            var pageCount = (int)Math.Ceiling((double)totalRows / rowsPerPage);

            var payments = await query
                .OrderByDescending(p => p.CreatedAt)
                .Skip((pageNumber - 1) * rowsPerPage)
                .Take(rowsPerPage)
                .ToListAsync(cancellationToken);

            if (payments.Any())
            {
                var appointmentIds = payments.Select(p => p.AppointmentId).Distinct().ToList();
                var appointments = await _context.Appointments.AsNoTracking()
                    .Where(a => appointmentIds.Contains(a.Id))
                    .ToListAsync(cancellationToken);

                if (appointments.Any())
                {
                    var customerIds = appointments.Select(a => a.CustomerUserId).Distinct().ToList();
                    var branchIds = appointments.Select(a => a.BranchId).Distinct().ToList();

                    var customers = await _context.Users.AsNoTracking()
                        .Where(u => customerIds.Contains(u.Id))
                        .ToListAsync(cancellationToken);

                    var branches = await _context.Branches.AsNoTracking()
                        .Where(b => branchIds.Contains(b.Id))
                        .ToListAsync(cancellationToken);

                    foreach (var c in customers) c.PasswordHash = null;

                    var appointmentsDict = appointments.ToDictionary(a => a.Id);
                    var customersDict = customers.ToDictionary(c => c.Id);
                    var branchesDict = branches.ToDictionary(b => b.Id);

                    foreach (var a in appointments)
                    {
                        if (customersDict.TryGetValue(a.CustomerUserId, out var cust))
                        {
                            a.CustomerUser = cust;
                        }
                        if (branchesDict.TryGetValue(a.BranchId, out var br))
                        {
                            a.Branch = br;
                        }
                    }

                    foreach (var p in payments)
                    {
                        if (appointmentsDict.TryGetValue(p.AppointmentId, out var appt))
                        {
                            p.Appointment = appt;
                        }
                    }
                }
            }

            var response = new PaginatedList<Payment>
            {
                Items = payments,
                PageNumber = pageNumber,
                PageSize = payments.Count,
                RowsPerPage = rowsPerPage,
                PageCount = pageCount,
                TotalRows = totalRows
            };

            return Ok(ApiResponse<PaginatedList<Payment>>.SuccessResult(response, "PAYMENTS_RETRIEVED"));
        }

        [HttpPost("finance/payrolls")]
        [EndpointSummary("Search and Filter Lab Payrolls/Payouts")]
        [EndpointDescription("Returns a paginated list of payouts/payrolls from company to lab, with optional filters.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<PaginatedList<Payroll>>))]
        public async Task<IActionResult> GetPayrolls([FromBody] SearchPayrollsRequest? request, CancellationToken cancellationToken)
        {
            var query = _context.Payrolls.AsNoTracking();

            if (request != null)
            {
                if (request.Status.HasValue)
                {
                    query = query.Where(pr => pr.Status == request.Status.Value);
                }

                if (!string.IsNullOrWhiteSpace(request.LabName))
                {
                    var labQuery = request.LabName.Trim().ToLower();
                    query = query.Where(pr => pr.Branch != null && pr.Branch.Name != null && pr.Branch.Name.ToLower().Contains(labQuery));
                }

                if (request.StartDate.HasValue)
                {
                    query = query.Where(pr => pr.PeriodStart >= request.StartDate.Value);
                }

                if (request.EndDate.HasValue)
                {
                    query = query.Where(pr => pr.PeriodEnd <= request.EndDate.Value);
                }
            }

            var totalRows = await query.CountAsync(cancellationToken);
            var pageNumber = request?.PageNumber ?? 1;
            var rowsPerPage = request?.RowsPerPage ?? 10;
            if (pageNumber < 1) pageNumber = 1;
            if (rowsPerPage < 1) rowsPerPage = 10;

            var pageCount = (int)Math.Ceiling((double)totalRows / rowsPerPage);

            var payrolls = await query
                .Include(pr => pr.Branch)
                .OrderByDescending(pr => pr.CreatedAt)
                .Skip((pageNumber - 1) * rowsPerPage)
                .Take(rowsPerPage)
                .ToListAsync(cancellationToken);

            var response = new PaginatedList<Payroll>
            {
                Items = payrolls,
                PageNumber = pageNumber,
                PageSize = payrolls.Count,
                RowsPerPage = rowsPerPage,
                PageCount = pageCount,
                TotalRows = totalRows
            };

            return Ok(ApiResponse<PaginatedList<Payroll>>.SuccessResult(response, "PAYROLLS_RETRIEVED"));
        }

        [HttpPost("customers")]
        [EndpointSummary("Search and Filter Customers with Pagination")]
        [EndpointDescription("Returns a paginated list of customers (UserRole = Customer) matching optional filters.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<PaginatedList<User>>))]
        public async Task<IActionResult> GetCustomersList([FromBody] SearchCustomersRequest? request, CancellationToken cancellationToken)
        {
            var query = _context.Users.AsNoTracking().Where(u => u.Role == UserRole.Customer && !u.IsDeleted);

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

            var totalRows = await query.CountAsync(cancellationToken);
            var pageNumber = request?.PageNumber ?? 1;
            var rowsPerPage = request?.RowsPerPage ?? 10;
            if (pageNumber < 1) pageNumber = 1;
            if (rowsPerPage < 1) rowsPerPage = 10;

            var pageCount = (int)Math.Ceiling((double)totalRows / rowsPerPage);

            var customers = await query
                .OrderByDescending(u => u.CreatedAt)
                .Skip((pageNumber - 1) * rowsPerPage)
                .Take(rowsPerPage)
                .ToListAsync(cancellationToken);

            var response = new PaginatedList<User>
            {
                Items = customers,
                PageNumber = pageNumber,
                PageSize = customers.Count,
                RowsPerPage = rowsPerPage,
                PageCount = pageCount,
                TotalRows = totalRows
            };

            return Ok(ApiResponse<PaginatedList<User>>.SuccessResult(response, "CUSTOMERS_RETRIEVED"));
        }

        [HttpGet("customers/{customerId}")]
        [EndpointSummary("Get Customer Details")]
        [EndpointDescription("Returns profile info and demographical details of a customer.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<CustomerDetailsResponse>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetCustomerDetails([FromRoute] string customerId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(customerId))
            {
                return BadRequest(ApiResponse.FailureResult("Customer ID is required."));
            }

            var user = await _context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == customerId && u.Role == UserRole.Customer && !u.IsDeleted, cancellationToken);

            if (user == null)
            {
                return NotFound(ApiResponse.FailureResult("Customer not found."));
            }

            var profile = await _context.Customers.AsNoTracking()
                .FirstOrDefaultAsync(c => c.UserId == customerId, cancellationToken);

            var response = new CustomerDetailsResponse
            {
                User = user,
                Profile = profile
            };

            return Ok(ApiResponse<CustomerDetailsResponse>.SuccessResult(response, "CUSTOMER_DETAILS_RETRIEVED"));
        }

        [HttpPost("customers/{customerId}/appointments")]
        [EndpointSummary("Get Customer Appointments/Bookings")]
        [EndpointDescription("Returns a paginated list of bookings for the specified customer, with optional status and date filters.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<PaginatedList<Appointment>>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetCustomerAppointments([FromRoute] string customerId, [FromBody] SearchCustomerAppointmentsRequest? request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(customerId))
            {
                return BadRequest(ApiResponse.FailureResult("Customer ID is required."));
            }

            var customerExists = await _context.Users.AnyAsync(u => u.Id == customerId && u.Role == UserRole.Customer && !u.IsDeleted, cancellationToken);
            if (!customerExists)
            {
                return NotFound(ApiResponse.FailureResult("Customer not found."));
            }

            var query = _context.Appointments.AsNoTracking()
                .Where(a => a.CustomerUserId == customerId);

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

                if (request.StartDate.HasValue)
                {
                    query = query.Where(a => a.AppointmentSlot != null && a.AppointmentSlot.SlotDate >= request.StartDate.Value);
                }

                if (request.EndDate.HasValue)
                {
                    query = query.Where(a => a.AppointmentSlot != null && a.AppointmentSlot.SlotDate <= request.EndDate.Value);
                }
            }

            var totalRows = await query.CountAsync(cancellationToken);
            var pageNumber = request?.PageNumber ?? 1;
            var rowsPerPage = request?.RowsPerPage ?? 10;
            if (pageNumber < 1) pageNumber = 1;
            if (rowsPerPage < 1) rowsPerPage = 10;

            var pageCount = (int)Math.Ceiling((double)totalRows / rowsPerPage);

            var appointments = await query
                .Include(a => a.Branch)
                .Include(a => a.AppointmentSlot)
                .Include(a => a.AssignedStaff)
                .OrderByDescending(a => a.CreatedAt)
                .Skip((pageNumber - 1) * rowsPerPage)
                .Take(rowsPerPage)
                .ToListAsync(cancellationToken);

            var response = new PaginatedList<Appointment>
            {
                Items = appointments,
                PageNumber = pageNumber,
                PageSize = appointments.Count,
                RowsPerPage = rowsPerPage,
                PageCount = pageCount,
                TotalRows = totalRows
            };

            return Ok(ApiResponse<PaginatedList<Appointment>>.SuccessResult(response, "CUSTOMER_APPOINTMENTS_RETRIEVED"));
        }

        [HttpPost("customers/{customerId}/payments")]
        [EndpointSummary("Get Customer Payment Transactions")]
        [EndpointDescription("Returns a paginated list of all payment transactions of the specified customer with the company.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<PaginatedList<Payment>>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetCustomerPayments([FromRoute] string customerId, [FromBody] SearchCustomerPaymentsRequest? request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(customerId))
            {
                return BadRequest(ApiResponse.FailureResult("Customer ID is required."));
            }

            var customerExists = await _context.Users.AnyAsync(u => u.Id == customerId && u.Role == UserRole.Customer && !u.IsDeleted, cancellationToken);
            if (!customerExists)
            {
                return NotFound(ApiResponse.FailureResult("Customer not found."));
            }

            var query = _context.Payments.AsNoTracking()
                .Where(p => p.Appointment != null && p.Appointment.CustomerUserId == customerId);

            if (request != null)
            {
                if (request.Status.HasValue)
                {
                    query = query.Where(p => p.Status == request.Status.Value);
                }

                if (request.PaymentMethod.HasValue)
                {
                    query = query.Where(p => p.PaymentMethod == request.PaymentMethod.Value);
                }

                if (request.StartDate.HasValue)
                {
                    query = query.Where(p => p.CreatedAt >= request.StartDate.Value);
                }

                if (request.EndDate.HasValue)
                {
                    query = query.Where(p => p.CreatedAt <= request.EndDate.Value);
                }
            }

            var totalRows = await query.CountAsync(cancellationToken);
            var pageNumber = request?.PageNumber ?? 1;
            var rowsPerPage = request?.RowsPerPage ?? 10;
            if (pageNumber < 1) pageNumber = 1;
            if (rowsPerPage < 1) rowsPerPage = 10;

            var pageCount = (int)Math.Ceiling((double)totalRows / rowsPerPage);

            var payments = await query
                .Include(p => p.Appointment)
                    .ThenInclude(a => a!.Branch)
                .OrderByDescending(p => p.CreatedAt)
                .Skip((pageNumber - 1) * rowsPerPage)
                .Take(rowsPerPage)
                .ToListAsync(cancellationToken);

            var response = new PaginatedList<Payment>
            {
                Items = payments,
                PageNumber = pageNumber,
                PageSize = payments.Count,
                RowsPerPage = rowsPerPage,
                PageCount = pageCount,
                TotalRows = totalRows
            };

            return Ok(ApiResponse<PaginatedList<Payment>>.SuccessResult(response, "CUSTOMER_PAYMENTS_RETRIEVED"));
        }

        [HttpPost("finance/transactions")]
        [EndpointSummary("List all financial transactions (Inbound/Outbound)")]
        [EndpointDescription("Returns a paginated list of unified financial transactions (customer-to-company payments or company-to-lab payouts) with filtering.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<PaginatedList<FinanceTransactionDto>>))]
        public async Task<IActionResult> GetTransactions([FromBody] SearchFinanceTransactionsRequest? request, CancellationToken cancellationToken)
        {
            var pageNumber = request?.PageNumber ?? 1;
            var rowsPerPage = request?.RowsPerPage ?? 10;
            if (pageNumber < 1) pageNumber = 1;
            if (rowsPerPage < 1) rowsPerPage = 10;

            var transactionType = request?.Type ?? TransactionType.Inbound;

            if (transactionType == TransactionType.Inbound)
            {
                var query = _context.Payments.AsNoTracking().AsQueryable();

                if (request != null)
                {
                    if (request.PaymentStatus.HasValue)
                    {
                        query = query.Where(p => p.Status == request.PaymentStatus.Value);
                    }
                    if (request.PaymentMethod.HasValue)
                    {
                        query = query.Where(p => p.PaymentMethod == request.PaymentMethod.Value);
                    }
                    if (!string.IsNullOrWhiteSpace(request.CustomerName))
                    {
                        var nameQuery = request.CustomerName.Trim().ToLower();
                        query = query.Where(p => p.Appointment != null && p.Appointment.CustomerUser != null && p.Appointment.CustomerUser.Name != null && p.Appointment.CustomerUser.Name.ToLower().Contains(nameQuery));
                    }
                    if (!string.IsNullOrWhiteSpace(request.LabName))
                    {
                        var labQuery = request.LabName.Trim().ToLower();
                        query = query.Where(p => p.Appointment != null && p.Appointment.Branch != null && p.Appointment.Branch.Name != null && p.Appointment.Branch.Name.ToLower().Contains(labQuery));
                    }
                    if (request.StartDate.HasValue)
                    {
                        query = query.Where(p => p.CreatedAt >= request.StartDate.Value);
                    }
                    if (request.EndDate.HasValue)
                    {
                        query = query.Where(p => p.CreatedAt <= request.EndDate.Value);
                    }
                }

                var totalRows = await query.CountAsync(cancellationToken);
                var pageCount = (int)Math.Ceiling((double)totalRows / rowsPerPage);

                var payments = await query
                    .Include(p => p.Appointment)
                        .ThenInclude(a => a!.CustomerUser)
                    .Include(p => p.Appointment)
                        .ThenInclude(a => a!.Branch)
                    .OrderByDescending(p => p.CreatedAt)
                    .Skip((pageNumber - 1) * rowsPerPage)
                    .Take(rowsPerPage)
                    .ToListAsync(cancellationToken);

                var list = payments.Select(p => new FinanceTransactionDto
                {
                    Id = p.Id,
                    ReferenceNumber = p.Appointment?.AppointmentNumber ?? p.RazorpayOrderId,
                    Type = TransactionType.Inbound,
                    GrossAmount = p.Appointment?.TotalAmount ?? 0,
                    PlatformCommission = p.Appointment?.PlatformCommission ?? 0,
                    NetAmount = p.Appointment?.TotalAmount ?? 0,
                    Status = p.Status.ToString(),
                    EntityName = p.Appointment?.CustomerUser?.Name ?? "Unknown Customer",
                    TransactionDate = p.PaidAt ?? p.CreatedAt,
                    PaymentMethod = p.PaymentMethod?.ToString() ?? "Unknown"
                }).ToList();

                var response = new PaginatedList<FinanceTransactionDto>
                {
                    Items = list,
                    PageNumber = pageNumber,
                    PageSize = list.Count,
                    RowsPerPage = rowsPerPage,
                    PageCount = pageCount,
                    TotalRows = totalRows
                };

                return Ok(ApiResponse<PaginatedList<FinanceTransactionDto>>.SuccessResult(response, "TRANSACTIONS_RETRIEVED"));
            }
            else
            {
                var query = _context.Payrolls.AsNoTracking().AsQueryable();

                if (request != null)
                {
                    if (request.PayrollStatus.HasValue)
                    {
                        query = query.Where(pr => pr.Status == request.PayrollStatus.Value);
                    }
                    if (!string.IsNullOrWhiteSpace(request.LabName))
                    {
                        var labQuery = request.LabName.Trim().ToLower();
                        query = query.Where(pr => pr.Branch != null && pr.Branch.Name != null && pr.Branch.Name.ToLower().Contains(labQuery));
                    }
                    if (request.StartDate.HasValue)
                    {
                        var dateOnlyStart = DateOnly.FromDateTime(request.StartDate.Value);
                        query = query.Where(pr => pr.PeriodStart >= dateOnlyStart);
                    }
                    if (request.EndDate.HasValue)
                    {
                        var dateOnlyEnd = DateOnly.FromDateTime(request.EndDate.Value);
                        query = query.Where(pr => pr.PeriodEnd <= dateOnlyEnd);
                    }
                }

                var totalRows = await query.CountAsync(cancellationToken);
                var pageCount = (int)Math.Ceiling((double)totalRows / rowsPerPage);

                var payrolls = await query
                    .Include(pr => pr.Branch)
                    .OrderByDescending(pr => pr.CreatedAt)
                    .Skip((pageNumber - 1) * rowsPerPage)
                    .Take(rowsPerPage)
                    .ToListAsync(cancellationToken);

                var list = payrolls.Select(pr => new FinanceTransactionDto
                {
                    Id = pr.Id,
                    ReferenceNumber = $"PAY-{pr.Id[..8].ToUpper()}",
                    Type = TransactionType.Outbound,
                    GrossAmount = pr.GrossAmount,
                    PlatformCommission = pr.PlatformCommission,
                    NetAmount = pr.NetPayout,
                    Status = pr.Status.ToString(),
                    EntityName = pr.Branch?.Name ?? "Unknown Lab",
                    TransactionDate = pr.SettledAt ?? pr.CreatedAt,
                    PaymentMethod = "Bank Transfer"
                }).ToList();

                var response = new PaginatedList<FinanceTransactionDto>
                {
                    Items = list,
                    PageNumber = pageNumber,
                    PageSize = list.Count,
                    RowsPerPage = rowsPerPage,
                    PageCount = pageCount,
                    TotalRows = totalRows
                };

                return Ok(ApiResponse<PaginatedList<FinanceTransactionDto>>.SuccessResult(response, "TRANSACTIONS_RETRIEVED"));
            }
        }

        [HttpGet("finance/transactions/{id}")]
        [EndpointSummary("Get Financial Transaction Details")]
        [EndpointDescription("Returns detailed metadata of a specific inbound payment or outbound payout transaction.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<FinanceTransactionDetailResponse>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetTransactionDetail([FromRoute] string id, [FromQuery] TransactionType type, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return BadRequest(ApiResponse.FailureResult("Transaction ID is required."));
            }

            if (type == TransactionType.Inbound)
            {
                var payment = await _context.Payments.AsNoTracking()
                    .Include(p => p.Appointment)
                        .ThenInclude(a => a!.CustomerUser)
                    .Include(p => p.Appointment)
                        .ThenInclude(a => a!.Branch)
                    .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

                if (payment == null)
                {
                    return NotFound(ApiResponse.FailureResult("Payment transaction not found."));
                }

                var response = new FinanceTransactionDetailResponse
                {
                    Id = payment.Id,
                    ReferenceNumber = payment.Appointment?.AppointmentNumber ?? payment.RazorpayOrderId,
                    Type = TransactionType.Inbound,
                    GrossAmount = payment.Appointment?.TotalAmount ?? 0,
                    PlatformCommission = payment.Appointment?.PlatformCommission ?? 0,
                    NetAmount = payment.Appointment?.TotalAmount ?? 0,
                    Status = payment.Status.ToString(),
                    TransactionDate = payment.PaidAt ?? payment.CreatedAt,
                    PaymentMethod = payment.PaymentMethod?.ToString() ?? "Unknown",
                    RazorpayOrderId = payment.RazorpayOrderId,
                    RazorpayPaymentId = payment.RazorpayPaymentId,
                    CustomerName = payment.Appointment?.CustomerUser?.Name,
                    CustomerPhone = payment.Appointment?.CustomerUser?.Phone,
                    LabName = payment.Appointment?.Branch?.Name,
                    AppointmentStatus = payment.Appointment?.Status.ToString()
                };

                return Ok(ApiResponse<FinanceTransactionDetailResponse>.SuccessResult(response, "TRANSACTION_DETAIL_RETRIEVED"));
            }
            else
            {
                var payroll = await _context.Payrolls.AsNoTracking()
                    .Include(pr => pr.Branch)
                        .ThenInclude(b => b!.LabUser)
                    .FirstOrDefaultAsync(pr => pr.Id == id, cancellationToken);

                if (payroll == null)
                {
                    return NotFound(ApiResponse.FailureResult("Payout transaction not found."));
                }

                var response = new FinanceTransactionDetailResponse
                {
                    Id = payroll.Id,
                    ReferenceNumber = $"PAY-{payroll.Id[..8].ToUpper()}",
                    Type = TransactionType.Outbound,
                    GrossAmount = payroll.GrossAmount,
                    PlatformCommission = payroll.PlatformCommission,
                    NetAmount = payroll.NetPayout,
                    Status = payroll.Status.ToString(),
                    TransactionDate = payroll.SettledAt ?? payroll.CreatedAt,
                    PaymentMethod = "Bank Transfer",
                    RazorpayTransferId = payroll.RazorpayTransferId,
                    PeriodStart = payroll.PeriodStart,
                    PeriodEnd = payroll.PeriodEnd,
                    LabName = payroll.Branch?.Name,
                    LabContactPerson = payroll.Branch?.LabUser?.Name,
                    LabContactPhone = payroll.Branch?.LabUser?.Phone
                };

                return Ok(ApiResponse<FinanceTransactionDetailResponse>.SuccessResult(response, "TRANSACTION_DETAIL_RETRIEVED"));
            }
        }

        [HttpGet("finance/insights")]
        [EndpointSummary("Get Financial Analytics and KPIs")]
        [EndpointDescription("Returns summarized KPI card values, monthly trends, and payment method share analytics.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<FinanceInsightsResponse>))]
        public async Task<IActionResult> GetFinanceInsights(CancellationToken cancellationToken)
        {
            // Load all paid payments with their appointments into memory.
            // MongoDB EF Core provider cannot translate Include() + SumAsync()
            // with navigation property projections server-side.
            var allPaidPayments = await _context.Payments.AsNoTracking()
                .Where(p => p.Status == PaymentStatus.Paid)
                .ToListAsync(cancellationToken);

            // Resolve appointment navigation for paid payments
            var appointmentIds = allPaidPayments.Select(p => p.AppointmentId).Distinct().ToList();
            var appointments = await _context.Appointments.AsNoTracking()
                .Where(a => appointmentIds.Contains(a.Id))
                .ToListAsync(cancellationToken);
            var appointmentsDict = appointments.ToDictionary(a => a.Id);
            foreach (var p in allPaidPayments)
            {
                if (appointmentsDict.TryGetValue(p.AppointmentId, out var appt))
                {
                    p.Appointment = appt;
                }
            }

            var totalInbound = allPaidPayments.Sum(p => p.Appointment?.TotalAmount ?? 0);

            var settledPayrolls = await _context.Payrolls.AsNoTracking()
                .Where(pr => pr.Status == PayrollStatus.Settled)
                .ToListAsync(cancellationToken);
            var totalOutbound = settledPayrolls.Sum(pr => pr.NetPayout);

            var totalProfit = allPaidPayments.Sum(p => p.Appointment?.PlatformCommission ?? 0);

            var totalTransactions = allPaidPayments.Count;

            var pendingPayrollsList = await _context.Payrolls.AsNoTracking()
                .Where(pr => pr.Status == PayrollStatus.Pending)
                .ToListAsync(cancellationToken);
            var pendingPayouts = pendingPayrollsList.Sum(pr => pr.NetPayout);

            var lastSixMonths = DateTime.UtcNow.AddMonths(-6);
            var paymentsLastSixMonths = allPaidPayments
                .Where(p => p.CreatedAt >= lastSixMonths)
                .ToList();

            var payrollsLastSixMonths = settledPayrolls
                .Where(pr => pr.CreatedAt >= lastSixMonths)
                .ToList();

            var monthlyTrendsList = new List<MonthlyTrendDto>();
            for (int i = 5; i >= 0; i--)
            {
                var monthDate = DateTime.UtcNow.AddMonths(-i);
                var monthName = monthDate.ToString("MMMM yyyy");

                var monthPayments = paymentsLastSixMonths
                    .Where(p => p.CreatedAt.Month == monthDate.Month && p.CreatedAt.Year == monthDate.Year)
                    .ToList();

                var monthPayrolls = payrollsLastSixMonths
                    .Where(pr => pr.CreatedAt.Month == monthDate.Month && pr.CreatedAt.Year == monthDate.Year)
                    .ToList();

                var inboundVal = monthPayments.Sum(p => p.Appointment?.TotalAmount ?? 0);
                var outboundVal = monthPayrolls.Sum(pr => pr.NetPayout);
                var profitVal = monthPayments.Sum(p => p.Appointment?.PlatformCommission ?? 0);

                monthlyTrendsList.Add(new MonthlyTrendDto
                {
                    Month = monthName,
                    Inbound = inboundVal,
                    Outbound = outboundVal,
                    Profit = profitVal
                });
            }

            var paymentsByMethod = allPaidPayments;

            var totalInboundForShare = paymentsByMethod.Sum(p => p.Appointment?.TotalAmount ?? 0);

            var methodStats = paymentsByMethod
                .GroupBy(p => p.PaymentMethod ?? PaymentMethod.UPI)
                .Select(g => new PaymentMethodShare
                {
                    Method = g.Key.ToString(),
                    Amount = g.Sum(p => p.Appointment?.TotalAmount ?? 0),
                    Percentage = totalInboundForShare > 0 
                        ? Math.Round((double)(g.Sum(p => p.Appointment?.TotalAmount ?? 0) / totalInboundForShare) * 100, 2)
                        : 0
                })
                .ToList();

            var response = new FinanceInsightsResponse
            {
                Kpis = new FinanceKpis
                {
                    TotalInbound = totalInbound,
                    TotalOutbound = totalOutbound,
                    TotalProfit = totalProfit,
                    TotalTransactions = totalTransactions,
                    PendingPayouts = pendingPayouts
                },
                MonthlyTrends = monthlyTrendsList,
                PaymentMethodStats = methodStats
            };

            return Ok(ApiResponse<FinanceInsightsResponse>.SuccessResult(response, "FINANCIAL_INSIGHTS_RETRIEVED"));
        }
    }

    public record AdminOverrideBranchServiceRequest(
        decimal? CustomPrice,
        decimal? CustomCommissionPct,
        bool IsActive
    );

    public record UpdateBranchCommissionRequest(
        decimal CommissionPct
    );

    public class AdminBranchServiceDto
    {
        public string? BranchServiceId { get; set; }
        public string ServiceId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string? Description { get; set; }
        public decimal BasePrice { get; set; }
        public decimal DefaultCommissionPct { get; set; }
        public decimal? CustomPrice { get; set; }
        public decimal? CustomCommissionPct { get; set; }
        public bool IsEnrolled { get; set; }
        public bool IsActive { get; set; }
        public bool IsCustom { get; set; }
    }

    public record InviteLabRequest(
        string Email,
        string LabName
    );

    public record SearchLabsRequest(
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

    public record SearchCustomersRequest(
        string? Name,
        string? Email,
        string? Status,
        int PageNumber = 1,
        int RowsPerPage = 10
    );

    public record SearchCustomerAppointmentsRequest(
        string? AppointmentNumber,
        AppointmentStatus? Status,
        DateOnly? StartDate,
        DateOnly? EndDate,
        int PageNumber = 1,
        int RowsPerPage = 10
    );

    public record SearchCustomerPaymentsRequest(
        PaymentStatus? Status,
        PaymentMethod? PaymentMethod,
        DateTime? StartDate,
        DateTime? EndDate,
        int PageNumber = 1,
        int RowsPerPage = 10
    );

    public class CustomerDetailsResponse
    {
        public User User { get; set; } = null!;
        public Customer? Profile { get; set; }
    }

    public record SearchFinanceTransactionsRequest(
        TransactionType? Type,
        PaymentStatus? PaymentStatus,
        PaymentMethod? PaymentMethod,
        PayrollStatus? PayrollStatus,
        string? CustomerName,
        string? LabName,
        DateTime? StartDate,
        DateTime? EndDate,
        int PageNumber = 1,
        int RowsPerPage = 10
    );

    public class FinanceTransactionDto
    {
        public string Id { get; set; } = string.Empty;
        public string ReferenceNumber { get; set; } = string.Empty;
        public TransactionType Type { get; set; }
        public decimal GrossAmount { get; set; }
        public decimal PlatformCommission { get; set; }
        public decimal NetAmount { get; set; }
        public string Status { get; set; } = string.Empty;
        public string EntityName { get; set; } = string.Empty;
        public DateTime TransactionDate { get; set; }
        public string PaymentMethod { get; set; } = string.Empty;
    }

    public class FinanceTransactionDetailResponse
    {
        public string Id { get; set; } = string.Empty;
        public string ReferenceNumber { get; set; } = string.Empty;
        public TransactionType Type { get; set; }
        public decimal GrossAmount { get; set; }
        public decimal PlatformCommission { get; set; }
        public decimal NetAmount { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime TransactionDate { get; set; }
        public string PaymentMethod { get; set; } = string.Empty;

        // Inbound details
        public string? RazorpayOrderId { get; set; }
        public string? RazorpayPaymentId { get; set; }
        public string? CustomerName { get; set; }
        public string? CustomerPhone { get; set; }
        public string? LabName { get; set; }
        public string? AppointmentStatus { get; set; }

        // Outbound details
        public string? RazorpayTransferId { get; set; }
        public DateOnly? PeriodStart { get; set; }
        public DateOnly? PeriodEnd { get; set; }
        public string? LabContactPerson { get; set; }
        public string? LabContactPhone { get; set; }
    }

    public class FinanceInsightsResponse
    {
        public FinanceKpis Kpis { get; set; } = new();
        public List<MonthlyTrendDto> MonthlyTrends { get; set; } = new();
        public List<PaymentMethodShare> PaymentMethodStats { get; set; } = new();
    }

    public class FinanceKpis
    {
        public decimal TotalInbound { get; set; }
        public decimal TotalOutbound { get; set; }
        public decimal TotalProfit { get; set; }
        public int TotalTransactions { get; set; }
        public decimal PendingPayouts { get; set; }
    }

    public class MonthlyTrendDto
    {
        public string Month { get; set; } = string.Empty;
        public decimal Inbound { get; set; }
        public decimal Outbound { get; set; }
        public decimal Profit { get; set; }
    }

    public class PaymentMethodShare
    {
        public string Method { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public double Percentage { get; set; }
    }

    public record SearchPaymentsRequest(
        PaymentStatus? Status,
        PaymentMethod? PaymentMethod,
        string? CustomerName,
        string? LabName,
        DateTime? StartDate,
        DateTime? EndDate,
        int PageNumber = 1,
        int RowsPerPage = 10
    );

    public record SearchPayrollsRequest(
        PayrollStatus? Status,
        string? LabName,
        DateOnly? StartDate,
        DateOnly? EndDate,
        int PageNumber = 1,
        int RowsPerPage = 10
    );

    public record SearchLabStaffRequest(
        string? Name,
        string? Email,
        string? Phone,
        string? Status,
        int PageNumber = 1,
        int RowsPerPage = 10
    );

    public record SearchLabAppointmentsRequest(
        string? AppointmentNumber,
        AppointmentStatus? Status,
        string? CustomerName,
        string? StaffName,
        DateOnly? StartDate,
        DateOnly? EndDate,
        int PageNumber = 1,
        int RowsPerPage = 10
    );

    public record UpdateLabStatusRequest(
        bool IsActive
    );



    public class LabDetailsResponse
    {
        public string Name { get; set; } = string.Empty;
        public string District { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string Pincode { get; set; } = string.Empty;
        public decimal Latitude { get; set; }
        public decimal Longitude { get; set; }
        public string Phone { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public string? Status { get; set; }
        public string? LabId { get; set; }
        public DateTime CreatedAt { get; set; }

        public User? ContactPerson { get; set; }
        public List<User> Staff { get; set; } = new();
        public LabStats Stats { get; set; } = new();
    }

    public class LabStats
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
