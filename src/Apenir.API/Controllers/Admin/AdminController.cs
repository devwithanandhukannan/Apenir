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

            var labExists = await _context.Branches.AnyAsync(b => b.Id == labId, cancellationToken);
            if (!labExists)
            {
                return NotFound(ApiResponse.FailureResult("Lab not found."));
            }

            var staffIdsQuery = _context.Appointments
                .Where(a => a.BranchId == labId && a.AssignedStaffId != null)
                .Select(a => a.AssignedStaffId)
                .Distinct();

            var query = _context.Users.AsNoTracking()
                .Where(u => staffIdsQuery.Contains(u.Id) && !u.IsDeleted);

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
                    query = query.Where(a => a.CustomerUser != null && a.CustomerUser.Name != null && a.CustomerUser.Name.ToLower().Contains(custNameQuery));
                }

                if (!string.IsNullOrWhiteSpace(request.StaffName))
                {
                    var staffNameQuery = request.StaffName.Trim().ToLower();
                    query = query.Where(a => a.AssignedStaff != null && a.AssignedStaff.Name != null && a.AssignedStaff.Name.ToLower().Contains(staffNameQuery));
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

            var appointmentsList = await query
                .Include(a => a.CustomerUser)
                .Include(a => a.AssignedStaff)
                .Include(a => a.AppointmentSlot)
                .OrderByDescending(a => a.CreatedAt)
                .Skip((pageNumber - 1) * rowsPerPage)
                .Take(rowsPerPage)
                .ToListAsync(cancellationToken);

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
                .Include(b => b.LabUser)
                .FirstOrDefaultAsync(b => b.Id == labId, cancellationToken);

            if (lab == null)
            {
                return NotFound(ApiResponse.FailureResult("Lab not found."));
            }

            var appointments = await _context.Appointments
                .Where(a => a.BranchId == labId)
                .ToListAsync(cancellationToken);

            var totalAppointments = appointments.Count;
            var completedAppointments = appointments.Count(a => a.Status == AppointmentStatus.Completed);
            var pendingAppointments = appointments.Count(a => a.Status == AppointmentStatus.Pending);
            var totalRevenue = appointments.Sum(a => a.TotalAmount);
            var totalLabPayout = appointments.Sum(a => a.LabPayout);

            var staff = await _context.Appointments
                .Where(a => a.BranchId == labId && a.AssignedStaffId != null)
                .Select(a => a.AssignedStaff!)
                .Where(s => !s.IsDeleted)
                .Distinct()
                .ToListAsync(cancellationToken);

            var servicesCount = await _context.BranchServices
                .CountAsync(bs => bs.BranchId == labId && bs.IsActive, cancellationToken);

            var activeSlotsCount = await _context.AppointmentSlots
                .CountAsync(s => s.BranchId == labId && s.IsAvailable, cancellationToken);

            var response = new LabDetailsResponse
            {
                Lab = lab,
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
            var pageNumber = request?.PageNumber ?? 1;
            var rowsPerPage = request?.RowsPerPage ?? 10;
            if (pageNumber < 1) pageNumber = 1;
            if (rowsPerPage < 1) rowsPerPage = 10;

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
            var totalInbound = await _context.Payments.AsNoTracking()
                .Where(p => p.Status == PaymentStatus.Paid)
                .Include(p => p.Appointment)
                .SumAsync(p => p.Appointment != null ? p.Appointment.TotalAmount : 0, cancellationToken);

            var totalOutbound = await _context.Payrolls.AsNoTracking()
                .Where(pr => pr.Status == PayrollStatus.Settled)
                .SumAsync(pr => pr.NetPayout, cancellationToken);

            var totalProfit = await _context.Payments.AsNoTracking()
                .Where(p => p.Status == PaymentStatus.Paid)
                .Include(p => p.Appointment)
                .SumAsync(p => p.Appointment != null ? p.Appointment.PlatformCommission : 0, cancellationToken);

            var totalTransactions = await _context.Payments.AsNoTracking()
                .CountAsync(p => p.Status == PaymentStatus.Paid, cancellationToken);

            var pendingPayouts = await _context.Payrolls.AsNoTracking()
                .Where(pr => pr.Status == PayrollStatus.Pending)
                .SumAsync(pr => pr.NetPayout, cancellationToken);

            var lastSixMonths = DateTime.UtcNow.AddMonths(-6);
            var paymentsLastSixMonths = await _context.Payments.AsNoTracking()
                .Where(p => p.Status == PaymentStatus.Paid && p.CreatedAt >= lastSixMonths)
                .Include(p => p.Appointment)
                .ToListAsync(cancellationToken);

            var payrollsLastSixMonths = await _context.Payrolls.AsNoTracking()
                .Where(pr => pr.Status == PayrollStatus.Settled && pr.CreatedAt >= lastSixMonths)
                .ToListAsync(cancellationToken);

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

            var paymentsByMethod = await _context.Payments.AsNoTracking()
                .Where(p => p.Status == PaymentStatus.Paid)
                .Include(p => p.Appointment)
                .ToListAsync(cancellationToken);

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

    public class PaginatedList<T>
    {
        public List<T> Items { get; set; } = new();
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int RowsPerPage { get; set; }
        public int PageCount { get; set; }
        public int TotalRows { get; set; }
    }

    public class LabDetailsResponse
    {
        public Branch Lab { get; set; } = null!;
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
