using System;
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
using Apenir.Application.Common.Interfaces;
using Apenir.Application.Common.Models;

namespace Apenir.API.Controllers;

[ApiController]
[Route("api/appointments")]
[Authorize]
public class BookingController : ControllerBase
{
    private readonly IApplicationDbContext _context;
    private readonly ICurrentUserService _currentUserService;

    public BookingController(IApplicationDbContext context, ICurrentUserService currentUserService)
    {
        _context = context;
        _currentUserService = currentUserService;
    }

    [HttpPost("book")]
    [EndpointSummary("Book diagnostic appointment via location")]
    [EndpointDescription("Validates geographic distance to the branch, checks slot capacity, and generates a pending/confirmed booking request.")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<Appointment>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
    public async Task<IActionResult> BookAppointment([FromBody] WebBookingRequest request, CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.ServiceId) || string.IsNullOrWhiteSpace(request.SlotId))
        {
            return BadRequest(ApiResponse.FailureResult("ServiceId, SlotId, and coordinates are required."));
        }

        var currentUserId = _currentUserService.UserId?.ToString();
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized(ApiResponse.FailureResult("User not authenticated."));
        }

        // 1. Fetch Slot and Service
        var slot = await _context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == request.SlotId, cancellationToken);
        if (slot == null)
        {
            return NotFound(ApiResponse.FailureResult("Appointment slot not found."));
        }

        var service = await _context.Services.FirstOrDefaultAsync(s => s.Id == request.ServiceId, cancellationToken);
        if (service == null)
        {
            return NotFound(ApiResponse.FailureResult("Diagnostic service not found."));
        }

        // Check Slot Capacity
        var memberCount = request.MemberCount < 1 ? 1 : request.MemberCount;
        if (!slot.IsAvailable || slot.BookedCount + memberCount > slot.MaxCapacity)
        {
            return BadRequest(ApiResponse.FailureResult("Selected slot has insufficient capacity."));
        }

        // 2. Fetch Branch
        var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == slot.BranchId, cancellationToken);
        if (branch == null)
        {
            return NotFound(ApiResponse.FailureResult("Lab branch not found."));
        }

        // 3. Proximity validation (Haversine distance check)
        var distance = CalculateDistanceKm(request.Latitude, request.Longitude, (double)branch.Latitude, (double)branch.Longitude);
        if (distance > branch.ServiceRangeKm)
        {
            return BadRequest(ApiResponse.FailureResult("cannot have a service in that location."));
        }

        // Calculate pricing
        var basePrice = service.BasePrice;
        var branchService = await _context.BranchServices
            .FirstOrDefaultAsync(bs => bs.BranchId == branch.Id && bs.ServiceId == service.Id && bs.IsActive, cancellationToken);
        decimal rate = branchService?.CustomPrice ?? basePrice;

        int total = (int)rate + (memberCount > 1
            ? (int)Math.Round((memberCount - 1) * rate * 0.8m)
            : 0);

        // 4. Update Slot capacity
        slot.BookedCount += memberCount;
        if (slot.BookedCount >= slot.MaxCapacity)
            slot.IsAvailable = false;
        _context.AppointmentSlots.Update(slot);

        // 5. Create Appointment
        var bookingId = $"BK-{DateTime.UtcNow:yyyyMMdd}-{new Random().Next(1000, 9999)}";
        var appointment = new Appointment
        {
            Id = Guid.NewGuid().ToString(),
            AppointmentNumber = bookingId,
            CustomerUserId = currentUserId,
            BranchId = branch.Id,
            AppointmentSlotId = slot.Id,
            LocationLatitude = (decimal)request.Latitude,
            LocationLongitude = (decimal)request.Longitude,
            LocationAddress = $"{request.BuildingDetails}, Floor {request.Floor}, Landmark: {request.Landmark}",
            BuildingDetails = request.BuildingDetails,
            Floor = request.Floor,
            Landmark = request.Landmark,
            Passcode = new Random().Next(1000, 9999).ToString(),
            Status = AppointmentStatus.Confirmed,
            TotalAmount = total,
            PlatformCommission = total * (((branchService != null && branchService.CustomCommissionPct.HasValue) ? branchService.CustomCommissionPct.Value : service.PlatformCommissionPct) / 100m),
            LabPayout = total * (1m - ((branchService != null && branchService.CustomCommissionPct.HasValue) ? branchService.CustomCommissionPct.Value : service.PlatformCommissionPct) / 100m),
            CreatedAt = DateTime.UtcNow,
            MemberCount = memberCount
        };

        _context.Appointments.Add(appointment);

        // Add Member Profiles
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId, cancellationToken);
        var customerName = user?.Name ?? "Patient";

        for (int i = 0; i < memberCount; i++)
        {
            var member = new AppointmentMember
            {
                Id = Guid.NewGuid().ToString(),
                AppointmentId = appointment.Id,
                MemberName = i == 0 ? customerName : $"Member {i + 1}",
                Age = 0,
                Gender = Gender.Other,
                Relationship = i == 0 ? "Self" : "Family Member"
            };
            _context.AppointmentMembers.Add(member);
        }

        // Add Payment record
        var payment = new Payment
        {
            Id = Guid.NewGuid().ToString(),
            AppointmentId = appointment.Id,
            RazorpayOrderId = $"order_WEB_{bookingId.Replace("-", "")}",
            RazorpayPaymentId = $"pay_WEB_{bookingId.Replace("-", "")}",
            Status = PaymentStatus.Paid,
            PaymentMethod = PaymentMethod.UPI,
            PaidAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        _context.Payments.Add(payment);

        await _context.SaveChangesAsync(cancellationToken);

        return Ok(ApiResponse<Appointment>.SuccessResult(appointment, "Appointment booked successfully."));
    }

    private static double CalculateDistanceKm(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371.0;
        var dLat = ToRadians(lat2 - lat1);
        var dLng = ToRadians(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private static double ToRadians(double val) => (Math.PI / 180) * val;
}

public class WebBookingRequest
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string ServiceId { get; set; } = string.Empty;
    public string SlotId { get; set; } = string.Empty;
    public int MemberCount { get; set; } = 1;
    public string BuildingDetails { get; set; } = string.Empty;
    public string Landmark { get; set; } = string.Empty;
    public string Floor { get; set; } = string.Empty;
}
