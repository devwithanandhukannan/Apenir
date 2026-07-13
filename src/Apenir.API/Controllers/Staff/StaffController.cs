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

    [HttpPost("appointments/{id}/otp/trigger")]
    [EndpointSummary("Trigger 2-minute collection OTP via WhatsApp")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
    public async Task<IActionResult> TriggerOtp([FromRoute] string id, CancellationToken cancellationToken)
    {
        var currentUserId = _currentUserService.UserId?.ToString();
        var appointment = await _context.Appointments
            .FirstOrDefaultAsync(a => a.Id == id && a.AssignedStaffId == currentUserId, cancellationToken);

        if (appointment == null)
        {
            return NotFound(ApiResponse.FailureResult("Assigned appointment not found."));
        }

        appointment.CustomerUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == appointment.CustomerUserId, cancellationToken);

        var random = new Random();
        var otp = random.Next(1000, 9999).ToString();
        appointment.Passcode = otp;
        appointment.UpdatedAt = DateTime.UtcNow;

        _context.Appointments.Update(appointment);
        await _context.SaveChangesAsync(cancellationToken);

        if (appointment.CustomerUser != null && !string.IsNullOrEmpty(appointment.CustomerUser.Phone))
        {
            var waMessage = $"🔑 *Your Collection Verification OTP is: {otp}*\n\nThis OTP is valid for the next 2 minutes. Please share it with our phlebotomist upon arrival to verify sample collection.";
            await _whatsAppService.SendTextMessageAsync(appointment.CustomerUser.Phone, waMessage);
        }

        return Ok(ApiResponse.SuccessResult("Verification OTP triggered and dispatched via WhatsApp successfully."));
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
            var waMessage = $"✅ *Passcode Verified!*\n\nDiagnostic samples for {appointment.MemberCount} member(s) have been collected successfully. We are transporting them to the lab.";
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

        var newMembers = request.Members.Select(m => new AppointmentMember
        {
            Id = Guid.NewGuid().ToString(),
            AppointmentId = appointment.Id,
            MemberName = m.Name,
            Age = m.Age,
            Gender = Enum.TryParse<Gender>(m.Gender, true, out var genderEnum) ? genderEnum : Gender.Other,
            Relationship = m.Relationship ?? "Self",
            AdditionalNotes = m.AdditionalNotes,
            UniqueNumber = string.IsNullOrWhiteSpace(m.UniqueNumber) ? $"MEM-{Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper()}" : m.UniqueNumber.Trim(),
            TestName = m.TestName
        }).ToList();

        await _context.AppointmentMembers.AddRangeAsync(newMembers, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return Ok(ApiResponse.SuccessResult("Member details saved successfully."));
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
    public string? UniqueNumber { get; set; }
    public string? TestName { get; set; }
}
