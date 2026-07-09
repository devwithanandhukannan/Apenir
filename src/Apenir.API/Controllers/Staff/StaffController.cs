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

namespace Apenir.API.Controllers;

[ApiController]
[Route("api/staff")]
[Authorize]
[StaffOnly]
public class StaffController : ControllerBase
{
    private readonly IApplicationDbContext _context;
    private readonly ICurrentUserService _currentUserService;
    private readonly IWhatsAppService _whatsAppService;

    public StaffController(
        IApplicationDbContext context,
        ICurrentUserService currentUserService,
        IWhatsAppService whatsAppService)
    {
        _context = context;
        _currentUserService = currentUserService;
        _whatsAppService = whatsAppService;
    }

    [HttpGet("appointments")]
    [EndpointSummary("Get assigned phlebotomy appointments")]
    [EndpointDescription("Returns list of appointments assigned to the logged-in staff member, masking sensitive passcodes.")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<StaffAppointmentDto>>))]
    public async Task<IActionResult> GetAssignedAppointments(CancellationToken cancellationToken)
    {
        var currentUserId = _currentUserService.UserId?.ToString();
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized(ApiResponse.FailureResult("User not authenticated."));
        }

        var appointments = await _context.Appointments
            .Where(a => a.AssignedStaffId == currentUserId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

        var customerIds = appointments.Select(a => a.CustomerUserId).Distinct().ToList();
        var slotIds = appointments.Select(a => a.AppointmentSlotId).Distinct().ToList();

        var customers = await _context.Users.Where(u => customerIds.Contains(u.Id)).ToListAsync(cancellationToken);
        var slots = await _context.AppointmentSlots.Where(s => slotIds.Contains(s.Id)).ToListAsync(cancellationToken);

        foreach (var a in appointments)
        {
            a.CustomerUser = customers.FirstOrDefault(c => c.Id == a.CustomerUserId);
            a.AppointmentSlot = slots.FirstOrDefault(s => s.Id == a.AppointmentSlotId);
        }

        var result = appointments.Select(a => new StaffAppointmentDto
        {
            Id = a.Id,
            AppointmentNumber = a.AppointmentNumber,
            CustomerName = a.CustomerUser?.Name ?? "Patient",
            CustomerPhone = a.CustomerUser?.Phone ?? string.Empty,
            LocationAddress = a.LocationAddress,
            LocationLatitude = a.LocationLatitude,
            LocationLongitude = a.LocationLongitude,
            Landmark = a.Landmark,
            BuildingDetails = a.BuildingDetails,
            Floor = a.Floor,
            Status = a.Status,
            MemberCount = a.MemberCount,
            SlotDate = a.AppointmentSlot?.SlotDate,
            SlotStartTime = a.AppointmentSlot?.StartTime
        }).ToList();

        return Ok(ApiResponse<List<StaffAppointmentDto>>.SuccessResult(result, "Assigned tasks retrieved."));
    }

    [HttpPost("appointments/{id}/status")]
    [EndpointSummary("Update appointment status and send WhatsApp notification")]
    [EndpointDescription("Transitions appointment status (Coming, Reached, ReachedLab) and updates the customer via WhatsApp.")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
    public async Task<IActionResult> UpdateStatus(
        [FromRoute] string id,
        [FromBody] UpdateTaskStatusRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return BadRequest(ApiResponse.FailureResult("Request body is required."));
        }

        var currentUserId = _currentUserService.UserId?.ToString();
        var appointment = await _context.Appointments
            .FirstOrDefaultAsync(a => a.Id == id && a.AssignedStaffId == currentUserId, cancellationToken);

        if (appointment == null)
        {
            return NotFound(ApiResponse.FailureResult("Assigned appointment not found."));
        }

        appointment.CustomerUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == appointment.CustomerUserId, cancellationToken);

        AppointmentStatus targetStatus;
        string waMessage;

        switch (request.Status.ToLower().Trim())
        {
            case "coming":
                targetStatus = AppointmentStatus.Assigned; // Or custom state if added, keeping standard Confirmed/Assigned
                waMessage = $"🚀 *Phlebotomist is on their way!*\n\nOur phlebotomist is en route to collect your diagnostic samples. Please be ready at your shared location.";
                break;

            case "reached":
                targetStatus = AppointmentStatus.Collected; // Transitions to Collected eventually, or stays Assigned with Reached note
                // Let's keep it in Assigned or custom state, here we trigger arrival message
                waMessage = $"📍 *Phlebotomist has arrived!*\n\nOur phlebotomist has reached your location. Please share your 4-digit Passcode/OTP (*{appointment.Passcode}*) to verify collection.";
                break;

            case "reachedlab":
                targetStatus = AppointmentStatus.Collected; // Marks samples delivered to lab
                waMessage = $"🔬 *Samples received at lab!*\n\nYour collected diagnostic samples have reached our laboratory safely and are being queued for testing. Reports will be sent here shortly.";
                break;

            default:
                return BadRequest(ApiResponse.FailureResult("Invalid status transition. Allowed values: coming, reached, reachedlab."));
        }

        // Save status transition
        appointment.Status = targetStatus;
        appointment.UpdatedAt = DateTime.UtcNow;
        _context.Appointments.Update(appointment);
        await _context.SaveChangesAsync(cancellationToken);

        // Send WhatsApp notification to customer
        if (appointment.CustomerUser != null && !string.IsNullOrEmpty(appointment.CustomerUser.Phone))
        {
            await _whatsAppService.SendTextMessageAsync(appointment.CustomerUser.Phone, waMessage);
        }

        return Ok(ApiResponse.SuccessResult($"Status transitioned and customer notified via WhatsApp successfully."));
    }

    [HttpPost("appointments/{id}/verify-otp")]
    [EndpointSummary("Verify customer OTP/passcode")]
    [EndpointDescription("Validates the 4-digit passcode. On success, sets status to Collected and returns existing customer profiles.")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<OtpVerificationResult>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
    public async Task<IActionResult> VerifyOtp(
        [FromRoute] string id,
        [FromBody] VerifyPasscodeRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Otp))
        {
            return BadRequest(ApiResponse.FailureResult("OTP passcode is required."));
        }

        var currentUserId = _currentUserService.UserId?.ToString();
        var appointment = await _context.Appointments
            .FirstOrDefaultAsync(a => a.Id == id && a.AssignedStaffId == currentUserId, cancellationToken);

        if (appointment == null)
        {
            return NotFound(ApiResponse.FailureResult("Assigned appointment not found."));
        }

        appointment.CustomerUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == appointment.CustomerUserId, cancellationToken);

        if (appointment.Passcode != request.Otp.Trim())
        {
            return BadRequest(ApiResponse.FailureResult("Invalid OTP passcode. Verification failed."));
        }

        // Verify and set collected status
        appointment.Status = AppointmentStatus.Collected;
        appointment.UpdatedAt = DateTime.UtcNow;
        _context.Appointments.Update(appointment);

        await _context.SaveChangesAsync(cancellationToken);

        // Notify WhatsApp
        if (appointment.CustomerUser != null && !string.IsNullOrEmpty(appointment.CustomerUser.Phone))
        {
            var staffUser = await _context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == currentUserId, cancellationToken);
            var staffName = staffUser?.Name ?? "Our Phlebotomist";

            var waMessage = $"✅ *Passcode Verified!*\n\nPhlebotomist *{staffName}* is a verified and trusted representative of Apenir. Diagnostic samples for {appointment.MemberCount} member(s) have been collected successfully and are on their way to the lab.";
            await _whatsAppService.SendTextMessageAsync(appointment.CustomerUser.Phone, waMessage);
        }

        // Fetch customer profiles linked to this phone number
        var customerPhone = appointment.CustomerUser?.Phone ?? string.Empty;
        var existingCustomers = await _context.Customers
            .Where(c => c.Phone == customerPhone)
            .ToListAsync(cancellationToken);

        var result = new OtpVerificationResult
        {
            Verified = true,
            MemberCount = appointment.MemberCount,
            CustomerPhone = customerPhone,
            ExistingProfiles = existingCustomers.Select(c => new CustomerProfileDto
            {
                Id = c.Id,
                Name = c.Name,
                Gender = c.Gender,
                Dob = c.Dob,
                Address = c.Address
            }).ToList()
        };

        return Ok(ApiResponse<OtpVerificationResult>.SuccessResult(result, "OTP passcode verified successfully."));
    }

    [HttpPost("appointments/{id}/members")]
    [EndpointSummary("Add appointment member details")]
    [EndpointDescription("Validates that exactly the booked number of members are provided and saves their details.")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
    public async Task<IActionResult> AddAppointmentMembers(
        [FromRoute] string id,
        [FromBody] AddAppointmentMembersRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null || request.Members == null || !request.Members.Any())
        {
            return BadRequest(ApiResponse.FailureResult("Member details are required."));
        }

        var currentUserId = _currentUserService.UserId?.ToString();
        var appointment = await _context.Appointments
            .FirstOrDefaultAsync(a => a.Id == id && a.AssignedStaffId == currentUserId, cancellationToken);

        if (appointment == null)
        {
            return NotFound(ApiResponse.FailureResult("Assigned appointment not found."));
        }

        if (request.Members.Count != appointment.MemberCount)
        {
            return BadRequest(ApiResponse.FailureResult($"This appointment was booked for {appointment.MemberCount} member(s). You must provide details for exactly {appointment.MemberCount} member(s)."));
        }

        // Delete any existing members for this appointment to handle re-submissions cleanly
        var existingMembers = await _context.AppointmentMembers
            .Where(m => m.AppointmentId == id)
            .ToListAsync(cancellationToken);
            
        if (existingMembers.Any())
        {
            _context.AppointmentMembers.RemoveRange(existingMembers);
        }

        var newMembers = request.Members.Select((m, index) => new AppointmentMember
        {
            Id = Guid.NewGuid().ToString(),
            AppointmentId = appointment.Id,
            MemberName = m.Name,
            Age = m.Age,
            Gender = Enum.TryParse<Gender>(m.Gender, true, out var genderEnum) ? genderEnum : Gender.Other,
            Relationship = m.Relationship ?? "Self",
            AdditionalNotes = m.AdditionalNotes,
            UniqueSampleId = $"{appointment.AppointmentNumber}-M{index + 1}"
        }).ToList();

        await _context.AppointmentMembers.AddRangeAsync(newMembers, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return Ok(ApiResponse.SuccessResult("Member details saved successfully."));
    }

    [HttpPost("appointments/walkin")]
    [EndpointSummary("Create a walk-in onsite appointment and register members")]
    [EndpointDescription("Registers a new customer profile (if new), creates a booking, adds members, and returns auto-generated sample IDs.")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<WalkinBookingResult>))]
    public async Task<IActionResult> CreateWalkinBooking(
        [FromBody] CreateWalkinBookingRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Phone) || string.IsNullOrWhiteSpace(request.ServiceId))
        {
            return BadRequest(ApiResponse.FailureResult("Phone, ServiceId, and member details are required."));
        }

        var currentUserId = _currentUserService.UserId?.ToString();
        var staffUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId, cancellationToken);
        if (staffUser == null || string.IsNullOrEmpty(staffUser.LabId))
        {
            return BadRequest(ApiResponse.FailureResult("Staff member is not assigned to any lab."));
        }

        // Get the branch associated with staff's LabId
        var branch = await _context.Branches.FirstOrDefaultAsync(b => b.LabId == staffUser.LabId, cancellationToken);
        if (branch == null)
        {
            return NotFound(ApiResponse.FailureResult("Associated lab branch not found."));
        }

        // Find or create customer
        var lowercasePhone = request.Phone.Trim();
        var customerUser = await _context.Users.FirstOrDefaultAsync(u => u.Phone == lowercasePhone, cancellationToken);
        if (customerUser == null)
        {
            customerUser = new User
            {
                Id = Guid.NewGuid().ToString(),
                Name = request.CustomerName?.Trim() ?? "Walkin User",
                Phone = lowercasePhone,
                Role = UserRole.Customer,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            _context.Users.Add(customerUser);

            var customer = new Customer
            {
                Id = Guid.NewGuid().ToString(),
                UserId = customerUser.Id,
                Phone = lowercasePhone,
                Name = customerUser.Name
            };
            _context.Customers.Add(customer);
            await _context.SaveChangesAsync(cancellationToken);
        }

        // Fetch Service & slot
        var service = await _context.Services.FirstOrDefaultAsync(s => s.Id == request.ServiceId, cancellationToken);
        if (service == null)
        {
            return NotFound(ApiResponse.FailureResult("Service not found."));
        }

        var branchService = await _context.BranchServices
            .FirstOrDefaultAsync(bs => bs.BranchId == branch.Id && bs.ServiceId == service.Id && bs.IsActive, cancellationToken);
        if (branchService == null)
        {
            return BadRequest(ApiResponse.FailureResult("This service is not active at the branch."));
        }

        var memberCount = request.Members.Count > 0 ? request.Members.Count : 1;

        // Pricing
        decimal rate = branchService.CustomPrice ?? service.BasePrice;
        decimal totalLabServicePrice = rate + (memberCount > 1 ? (memberCount - 1) * rate * 0.8m : 0m);
        decimal commissionPct = branchService.CustomCommissionPct ?? service.PlatformCommissionPct;
        decimal adminCommission = totalLabServicePrice * (commissionPct / 100m);
        decimal customerPrice = totalLabServicePrice + adminCommission; // Walkin onsite has no travel charge

        var bookingId = $"BK-W-{DateTime.UtcNow:yyyyMMdd}-{new Random().Next(1000, 9999)}";
        var appointment = new Appointment
        {
            Id = Guid.NewGuid().ToString(),
            AppointmentNumber = bookingId,
            CustomerUserId = customerUser.Id,
            BranchId = branch.Id,
            AssignedStaffId = currentUserId,
            Status = AppointmentStatus.Collected, // Instantly collected
            TotalAmount = customerPrice,
            PlatformCommission = adminCommission,
            LabPayout = totalLabServicePrice,
            LocationLatitude = branch.Latitude,
            LocationLongitude = branch.Longitude,
            LocationAddress = "On-site Walkin at Lab",
            Passcode = "0000",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MemberCount = memberCount
        };

        _context.Appointments.Add(appointment);

        var memberResults = new List<WalkinMemberDto>();
        for (int i = 0; i < memberCount; i++)
        {
            var m = request.Members[i];
            var uniqueSampleId = $"{bookingId}-M{i + 1}";
            var member = new AppointmentMember
            {
                Id = Guid.NewGuid().ToString(),
                AppointmentId = appointment.Id,
                MemberName = m.Name,
                Age = m.Age,
                Gender = Enum.TryParse<Gender>(m.Gender, true, out var genderEnum) ? genderEnum : Gender.Other,
                Relationship = m.Relationship ?? "Self",
                UniqueSampleId = uniqueSampleId
            };
            _context.AppointmentMembers.Add(member);

            memberResults.Add(new WalkinMemberDto
            {
                MemberId = member.Id,
                Name = member.MemberName,
                UniqueSampleId = uniqueSampleId
            });
        }

        // Add payment record as Created (pending pay on desk)
        var payment = new Payment
        {
            Id = Guid.NewGuid().ToString(),
            AppointmentId = appointment.Id,
            RazorpayOrderId = $"walkin_{bookingId}",
            RazorpayPaymentId = $"walkin_pay_{bookingId}",
            Status = PaymentStatus.Created,
            CreatedAt = DateTime.UtcNow
        };
        _context.Payments.Add(payment);

        await _context.SaveChangesAsync(cancellationToken);

        // Send a WhatsApp trusted phlebotomist registration summary to the Customer
        var staffName = staffUser.Name ?? "Our Phlebotomist";
        var welcomeMsg = $"🧬 *On-site Booking Registered!*\n\n" +
                         $"Booking ID: *{bookingId}*\n" +
                         $"Service: {service.Name}\n" +
                         $"Phlebotomist: {staffName}\n" +
                         $"Patient count: {memberCount}\n\n" +
                         $"Please make payment at the billing desk. Passcode/OTP validation completed.";
        await _whatsAppService.SendTextMessageAsync(customerUser.Phone, welcomeMsg);

        var result = new WalkinBookingResult
        {
            AppointmentId = appointment.Id,
            BookingNumber = bookingId,
            TotalAmount = customerPrice,
            Members = memberResults
        };

        return Ok(ApiResponse<WalkinBookingResult>.SuccessResult(result, "Walk-in onsite booking completed."));
    }
}

public class StaffAppointmentDto
{
    public string Id { get; set; } = string.Empty;
    public string AppointmentNumber { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public string LocationAddress { get; set; } = string.Empty;
    public decimal LocationLatitude { get; set; }
    public decimal LocationLongitude { get; set; }
    public string? Landmark { get; set; }
    public string? BuildingDetails { get; set; }
    public string? Floor { get; set; }
    public AppointmentStatus Status { get; set; }
    public int MemberCount { get; set; }
    public DateOnly? SlotDate { get; set; }
    public TimeOnly? SlotStartTime { get; set; }
}

public class UpdateTaskStatusRequest
{
    public string Status { get; set; } = string.Empty;
}

public class VerifyPasscodeRequest
{
    public string Otp { get; set; } = string.Empty;
}

public class OtpVerificationResult
{
    public bool Verified { get; set; }
    public int MemberCount { get; set; }
    public string CustomerPhone { get; set; } = string.Empty;
    public List<CustomerProfileDto> ExistingProfiles { get; set; } = new();
}

public class CustomerProfileDto
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Gender { get; set; }
    public string? Dob { get; set; }
    public string? Address { get; set; }
}

public class AddAppointmentMembersRequest
{
    public List<AppointmentMemberDto> Members { get; set; } = new();
}

public class AppointmentMemberDto
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Gender { get; set; } = string.Empty;
    public string? Relationship { get; set; }
    public string? AdditionalNotes { get; set; }
}

public class CreateWalkinBookingRequest
{
    public string Phone { get; set; } = string.Empty;
    public string? CustomerName { get; set; }
    public string ServiceId { get; set; } = string.Empty;
    public List<WalkinMemberInput> Members { get; set; } = new();
}

public class WalkinMemberInput
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Gender { get; set; } = string.Empty;
    public string? Relationship { get; set; }
}

public class WalkinBookingResult
{
    public string AppointmentId { get; set; } = string.Empty;
    public string BookingNumber { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public List<WalkinMemberDto> Members { get; set; } = new();
}

public class WalkinMemberDto
{
    public string MemberId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string UniqueSampleId { get; set; } = string.Empty;
}
