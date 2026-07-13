using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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
    private readonly IHttpClientFactory _httpClientFactory;

    public BookingController(
        IApplicationDbContext context,
        ICurrentUserService currentUserService,
        IHttpClientFactory httpClientFactory)
    {
        _context = context;
        _currentUserService = currentUserService;
        _httpClientFactory = httpClientFactory;
    }

    [HttpGet]
    [EndpointSummary("Get authenticated customer's appointments")]
    public async Task<IActionResult> GetCustomerAppointments(CancellationToken cancellationToken)
    {
        var currentUserId = _currentUserService.UserId?.ToString();
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized(ApiResponse<List<Appointment>>.FailureResult("User not authenticated."));
        }

        var appointments = await _context.Appointments
            .Include(a => a.Branch)
            .Include(a => a.AppointmentSlot)
            .Where(a => a.CustomerUserId == currentUserId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

        return Ok(ApiResponse<List<Appointment>>.SuccessResult(appointments, "APPOINTMENTS_RETRIEVED"));
    }

    [HttpGet("slots")]
    [AllowAnonymous]
    [EndpointSummary("Get available slots for a service near coordinates")]
    public async Task<IActionResult> GetNearbySlots(
        [FromQuery] double latitude,
        [FromQuery] double longitude,
        [FromQuery] string serviceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(serviceId))
        {
            return BadRequest(ApiResponse.FailureResult("ServiceId is required."));
        }

        // 1. Fetch active branches
        var branches = await _context.Branches
            .Where(b => b.IsActive)
            .ToListAsync(cancellationToken);

        var nearbyBranchIds = new List<string>();

        // Find branches within service range (straight-line Haversine threshold check)
        foreach (var branch in branches)
        {
            var distance = CalculateDistanceKm(latitude, longitude, (double)branch.Latitude, (double)branch.Longitude);
            if (distance <= branch.ServiceRangeKm)
            {
                nearbyBranchIds.Add(branch.Id);
            }
        }

        if (!nearbyBranchIds.Any())
        {
            return Ok(ApiResponse<List<AppointmentSlot>>.SuccessResult(new List<AppointmentSlot>(), "No branches within service range."));
        }

        // 2. Filter branches that offer the requested service
        var branchServices = await _context.BranchServices
            .Where(bs => bs.ServiceId == serviceId && bs.IsActive && nearbyBranchIds.Contains(bs.BranchId))
            .Select(bs => bs.BranchId)
            .ToListAsync(cancellationToken);

        if (!branchServices.Any())
        {
            return Ok(ApiResponse<List<AppointmentSlot>>.SuccessResult(new List<AppointmentSlot>(), "No branches offer this service near you."));
        }

        // 3. Fetch slots for those branches
        var slots = await _context.AppointmentSlots
            .Where(s => branchServices.Contains(s.BranchId) && s.IsAvailable && s.SlotDate >= DateOnly.FromDateTime(DateTime.Today))
            .OrderBy(s => s.SlotDate)
            .ThenBy(s => s.StartTime)
            .ToListAsync(cancellationToken);

        return Ok(ApiResponse<List<AppointmentSlot>>.SuccessResult(slots, "Available slots retrieved successfully."));
    }

    [HttpPost("book")]
    [EndpointSummary("Book diagnostic appointment via location")]
    [EndpointDescription("Validates geographic distance, applies OSRM travel pricing, updates slot capacity concurrently, and logs booking details.")]
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

        // 3. Proximity validation (Haversine straight-line check)
        var straightLineDistance = CalculateDistanceKm(request.Latitude, request.Longitude, (double)branch.Latitude, (double)branch.Longitude);
        if (straightLineDistance > branch.ServiceRangeKm)
        {
            return BadRequest(ApiResponse.FailureResult("cannot have a service in that location."));
        }

        // Calculate OSRM road distance
        var client = _httpClientFactory.CreateClient();
        var roadDistance = await GetRoadDistanceKm(request.Latitude, request.Longitude, (double)branch.Latitude, (double)branch.Longitude, client);
        
        // Calculate pricing
        var basePrice = service.BasePrice;
        var branchService = await _context.BranchServices
            .FirstOrDefaultAsync(bs => bs.BranchId == branch.Id && bs.ServiceId == service.Id && bs.IsActive, cancellationToken);
        if (branchService == null)
        {
            return BadRequest(ApiResponse.FailureResult("This service is not available at the selected branch."));
        }
        decimal rate = branchService.CustomPrice ?? basePrice;

        // Extra travel cost based on OSRM road distance
        decimal travelCost = (decimal)roadDistance * branch.PerKmCharge;

        int total = (int)rate + (memberCount > 1
            ? (int)Math.Round((memberCount - 1) * rate * 0.8m)
            : 0) + (int)Math.Round(travelCost);

        // 4. Update Slot capacity safely under database transaction (if context is a DbContext and supports transactions)
        if (_context is DbContext dbContext)
        {
            try
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                
                // Re-fetch slot to ensure fresh status under transaction
                var freshSlot = await _context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == request.SlotId, cancellationToken);
                if (freshSlot == null || !freshSlot.IsAvailable || freshSlot.BookedCount + memberCount > freshSlot.MaxCapacity)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return BadRequest(ApiResponse.FailureResult("Selected slot has insufficient capacity."));
                }

                freshSlot.BookedCount += memberCount;
                if (freshSlot.BookedCount >= freshSlot.MaxCapacity)
                    freshSlot.IsAvailable = false;

                _context.AppointmentSlots.Update(freshSlot);
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                // Standalone MongoDB dev server does not support replica set transactions. Fall back to normal updates if so.
                if (ex.Message.Contains("transaction", StringComparison.OrdinalIgnoreCase) || 
                    ex.Message.Contains("replica set", StringComparison.OrdinalIgnoreCase))
                {
                    slot.BookedCount += memberCount;
                    if (slot.BookedCount >= slot.MaxCapacity)
                        slot.IsAvailable = false;
                    _context.AppointmentSlots.Update(slot);
                }
                else
                {
                    throw;
                }
            }
        }
        else
        {
            slot.BookedCount += memberCount;
            if (slot.BookedCount >= slot.MaxCapacity)
                slot.IsAvailable = false;
            _context.AppointmentSlots.Update(slot);
        }

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
            LocationAddress = $"{request.BuildingDetails}, Floor {request.Floor}, Landmark: {request.Landmark} (Includes ₹{Math.Round(travelCost)} travel fee for {roadDistance:F2} km)",
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

    private async Task<double> GetRoadDistanceKm(double lat1, double lng1, double lat2, double lng2, HttpClient client)
    {
        try
        {
            // OSRM expects longitude,latitude ordering
            var lon1Str = lng1.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var lat1Str = lat1.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var lon2Str = lng2.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var lat2Str = lat2.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var url = $"http://router.project-osrm.org/route/v1/driving/{lon1Str},{lat1Str};{lon2Str},{lat2Str}?overview=false";
            
            client.Timeout = TimeSpan.FromSeconds(3); // Fast timeout for responsiveness
            var response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("code", out var code) && code.GetString() == "Ok")
                {
                    if (root.TryGetProperty("routes", out var routes) && routes.ValueKind == JsonValueKind.Array && routes.GetArrayLength() > 0)
                    {
                        var route = routes[0];
                        if (route.TryGetProperty("distance", out var distanceVal))
                        {
                            var distanceMeters = distanceVal.GetDouble();
                            return distanceMeters / 1000.0; // convert to km
                        }
                    }
                }
            }
        }
        catch
        {
            // Fall back to Haversine straight-line distance on exception (API block, rate limits, timeout)
        }

        return CalculateDistanceKm(lat1, lng1, lat2, lng2);
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
