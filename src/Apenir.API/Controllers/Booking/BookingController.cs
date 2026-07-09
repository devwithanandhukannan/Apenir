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
        var distance = Apenir.Application.Common.Helpers.PricingHelper.CalculateDistanceKm(request.Latitude, request.Longitude, (double)branch.Latitude, (double)branch.Longitude);
        if (distance > branch.ServiceRangeKm)
        {
            return BadRequest(ApiResponse.FailureResult("cannot have a service in that location."));
        }

        // Calculate travel fee
        var travelFee = await Apenir.Application.Common.Helpers.PricingHelper.CalculateTravelFeeAsync(_context, branch.Id, request.Latitude, request.Longitude, cancellationToken);

        // Calculate pricing
        var basePrice = service.BasePrice;
        var branchService = await _context.BranchServices
            .FirstOrDefaultAsync(bs => bs.BranchId == branch.Id && bs.ServiceId == service.Id && bs.IsActive, cancellationToken);
        if (branchService == null)
        {
            return BadRequest(ApiResponse.FailureResult("This service is not available at the selected branch."));
        }
        decimal rate = branchService.CustomPrice ?? basePrice;

        decimal totalLabServicePrice = rate + (memberCount > 1
            ? (memberCount - 1) * rate * 0.8m
            : 0m);

        decimal commissionPct = branchService.CustomCommissionPct ?? service.PlatformCommissionPct;
        decimal adminCommission = totalLabServicePrice * (commissionPct / 100m);
        decimal customerPrice = totalLabServicePrice + adminCommission + travelFee;
        decimal labPayout = totalLabServicePrice + travelFee;

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
            TotalAmount = customerPrice,
            PlatformCommission = adminCommission,
            LabPayout = labPayout,
            CreatedAt = DateTime.UtcNow,
            MemberCount = memberCount,
            ServiceIds = service.Id
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

    [HttpPost("book-multi")]
    [EndpointSummary("Book multiple diagnostic services (cart) via location")]
    [EndpointDescription("Validates geographic distance, slot capacity, and calculates cumulative prices for multiple cart services.")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<Appointment>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
    public async Task<IActionResult> BookMultiAppointment(
        [FromBody] WebMultiBookingRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null || request.ServiceIds == null || !request.ServiceIds.Any() || string.IsNullOrWhiteSpace(request.SlotId))
        {
            return BadRequest(ApiResponse.FailureResult("ServiceIds, SlotId, and coordinates are required."));
        }

        var currentUserId = _currentUserService.UserId?.ToString();
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized(ApiResponse.FailureResult("User not authenticated."));
        }

        // Fetch Slot
        var slot = await _context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == request.SlotId, cancellationToken);
        if (slot == null)
        {
            return NotFound(ApiResponse.FailureResult("Appointment slot not found."));
        }

        // Check Slot Capacity
        var memberCount = request.MemberCount < 1 ? 1 : request.MemberCount;
        if (!slot.IsAvailable || slot.BookedCount + memberCount > slot.MaxCapacity)
        {
            return BadRequest(ApiResponse.FailureResult("Selected slot has insufficient capacity."));
        }

        // Fetch Branch
        var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == slot.BranchId, cancellationToken);
        if (branch == null)
        {
            return NotFound(ApiResponse.FailureResult("Lab branch not found."));
        }

        // Proximity validation
        var distance = Apenir.Application.Common.Helpers.PricingHelper.CalculateDistanceKm(request.Latitude, request.Longitude, (double)branch.Latitude, (double)branch.Longitude);
        if (distance > branch.ServiceRangeKm)
        {
            return BadRequest(ApiResponse.FailureResult("cannot have a service in that location."));
        }

        // Calculate travel fee
        var travelFee = await Apenir.Application.Common.Helpers.PricingHelper.CalculateTravelFeeAsync(_context, branch.Id, request.Latitude, request.Longitude, cancellationToken);

        // Fetch Services and calculate pricing
        var allServices = await _context.Services.AsNoTracking().ToListAsync(cancellationToken);
        var services = allServices.Where(s => request.ServiceIds.Contains(s.Id)).ToList();
        if (services.Count != request.ServiceIds.Count)
        {
            return NotFound(ApiResponse.FailureResult("One or more diagnostic services could not be found."));
        }

        decimal totalLabServicePrice = 0m;
        decimal totalAdminCommission = 0m;

        foreach (var service in services)
        {
            var branchService = await _context.BranchServices
                .FirstOrDefaultAsync(bs => bs.BranchId == branch.Id && bs.ServiceId == service.Id && bs.IsActive, cancellationToken);
            if (branchService == null)
            {
                return BadRequest(ApiResponse.FailureResult($"Service {service.Name} is not active at the selected branch."));
            }

            decimal rate = branchService.CustomPrice ?? service.BasePrice;
            decimal servicePrice = rate + (memberCount > 1 ? (memberCount - 1) * rate * 0.8m : 0m);
            decimal commissionPct = branchService.CustomCommissionPct ?? service.PlatformCommissionPct;
            decimal adminCommission = servicePrice * (commissionPct / 100m);

            totalLabServicePrice += servicePrice;
            totalAdminCommission += adminCommission;
        }

        decimal customerPrice = totalLabServicePrice + totalAdminCommission + travelFee;
        decimal labPayout = totalLabServicePrice + travelFee;

        // Update Slot capacity
        slot.BookedCount += memberCount;
        if (slot.BookedCount >= slot.MaxCapacity)
            slot.IsAvailable = false;
        _context.AppointmentSlots.Update(slot);

        // Create Appointment
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
            TotalAmount = customerPrice,
            PlatformCommission = totalAdminCommission,
            LabPayout = labPayout,
            CreatedAt = DateTime.UtcNow,
            MemberCount = memberCount,
            ServiceIds = string.Join(",", request.ServiceIds)
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
                Relationship = i == 0 ? "Self" : "Family Member",
                UniqueSampleId = $"{bookingId}-M{i + 1}"
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

    [HttpGet("history/invoice/{id}")]
    [EndpointSummary("Generate PDF invoice receipt for an appointment")]
    [EndpointDescription("Generates and downloads a raw PDF invoice containing billing details, services, and payments.")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileResult))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
    public async Task<IActionResult> DownloadInvoice(
        [FromRoute] string id,
        CancellationToken cancellationToken)
    {
        var currentUserId = _currentUserService.UserId?.ToString();
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized(ApiResponse.FailureResult("User not authenticated."));
        }

        var appointment = await _context.Appointments
            .FirstOrDefaultAsync(a => a.Id == id && a.CustomerUserId == currentUserId, cancellationToken);
        if (appointment == null)
        {
            return NotFound(ApiResponse.FailureResult("Appointment not found."));
        }

        var customerUser = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == appointment.CustomerUserId, cancellationToken);
        var branch = await _context.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == appointment.BranchId, cancellationToken);

        var testIds = (appointment.ServiceIds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        var allServices = await _context.Services.AsNoTracking().ToListAsync(cancellationToken);
        var services = allServices.Where(s => testIds.Contains(s.Id)).ToList();

        var pdfBytes = GenerateInvoicePdfBytes(appointment, customerUser, branch, services);

        return File(pdfBytes, "application/pdf", $"Invoice_{appointment.AppointmentNumber}.pdf");
    }

    private static byte[] GenerateInvoicePdfBytes(
        Appointment appointment,
        User? customerUser,
        Branch? branch,
        List<Service> services)
    {
        var stringBuilder = new System.Text.StringBuilder();
        
        stringBuilder.Append("%PDF-1.4\n");
        stringBuilder.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        stringBuilder.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        stringBuilder.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        
        // Content Stream
        var contents = new System.Text.StringBuilder();
        contents.Append("BT\n");
        contents.Append("/F1 18 Tf\n70 750 Td\n(APENIR DIAGNOSTICS INVOICE) Tj\n");
        contents.Append("/F1 12 Tf\n0 -40 Td\n");
        contents.Append($"(Invoice Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm}) Tj\n0 -20 Td\n");
        contents.Append($"(Booking ID: {appointment.AppointmentNumber}) Tj\n0 -20 Td\n");
        contents.Append($"(Customer Name: {customerUser?.Name ?? "Patient"}) Tj\n0 -20 Td\n");
        contents.Append($"(Phone Number: +{customerUser?.Phone ?? string.Empty}) Tj\n0 -20 Td\n");
        contents.Append($"(Lab Branch: {branch?.Name ?? "Associated Lab"}) Tj\n0 -30 Td\n");
        
        contents.Append("(SERVICES BOOKED:) Tj\n0 -20 Td\n");
        foreach (var s in services)
        {
            contents.Append($"(- {s.Name} - {s.Category}) Tj\n0 -15 Td\n");
        }
        
        contents.Append("\n0 -25 Td\n");
        contents.Append($"(Total Paid Amount: INR {Math.Round(appointment.TotalAmount):F2}) Tj\n0 -20 Td\n");
        contents.Append("(Payment Status: PAID - Secured via UPI/Razorpay) Tj\n");
        contents.Append("ET\n");
        
        var contentStr = contents.ToString();
        var contentLength = System.Text.Encoding.UTF8.GetByteCount(contentStr);
        
        stringBuilder.Append($"4 0 obj\n<< /Length {contentLength} >>\nstream\n");
        stringBuilder.Append(contentStr);
        stringBuilder.Append("\nendstream\nendobj\n");
        
        stringBuilder.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        
        stringBuilder.Append("xref\n0 6\n0000000000 65535 f \n");
        stringBuilder.Append("0000000009 00000 n \n");
        stringBuilder.Append("0000000058 00000 n \n");
        stringBuilder.Append("0000000115 00000 n \n");
        stringBuilder.Append("0000000244 00000 n \n");
        stringBuilder.Append("0000000418 00000 n \n");
        stringBuilder.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n503\n%%EOF\n");
        
        return System.Text.Encoding.UTF8.GetBytes(stringBuilder.ToString());
    }
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

public class WebMultiBookingRequest
{
    public List<string> ServiceIds { get; set; } = new();
    public string SlotId { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string BuildingDetails { get; set; } = string.Empty;
    public string Landmark { get; set; } = string.Empty;
    public string Floor { get; set; } = string.Empty;
    public int MemberCount { get; set; } = 1;
}
