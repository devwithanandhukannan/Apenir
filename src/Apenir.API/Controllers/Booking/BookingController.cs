using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
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
    private readonly IWhatsAppService _whatsAppService;

    public BookingController(
        IApplicationDbContext context,
        ICurrentUserService currentUserService,
        IHttpClientFactory httpClientFactory,
        IWhatsAppService whatsAppService)
    {
        _context = context;
        _currentUserService = currentUserService;
        _httpClientFactory = httpClientFactory;
        _whatsAppService = whatsAppService;
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

    [HttpGet("eligible-labs")]
    [AllowAnonymous]
    [EndpointSummary("Get eligible labs for a cart of items near coordinates")]
    public async Task<IActionResult> GetEligibleLabs(
        [FromQuery] double latitude,
        [FromQuery] double longitude,
        [FromQuery] string itemIds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(itemIds))
        {
            return BadRequest(ApiResponse.FailureResult("itemIds is required."));
        }

        var ids = itemIds.Split(',').Select(id => id.Trim()).ToList();
        var allBranches = await _context.Branches.Where(b => b.IsActive).ToListAsync(cancellationToken);
        
        var services = await _context.Services.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync(cancellationToken);
        var packages = await _context.Packages.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);

        var branchServices = await _context.BranchServices
            .Where(bs => ids.Contains(bs.ServiceId) && bs.IsActive)
            .ToListAsync(cancellationToken);

        var branchPackages = await _context.BranchPackages
            .Where(bp => ids.Contains(bp.PackageId) && bp.IsActive)
            .ToListAsync(cancellationToken);

        var eligibleLabs = new List<object>();

        foreach (var branch in allBranches)
        {
            var distance = CalculateDistanceKm(latitude, longitude, (double)branch.Latitude, (double)branch.Longitude);
            if (distance > branch.ServiceRangeKm)
                continue;

            int offeredServicesCount = branchServices.Where(bs => bs.BranchId == branch.Id).Select(bs => bs.ServiceId).Distinct().Count();
            int offeredPackagesCount = branchPackages.Where(bp => bp.BranchId == branch.Id).Select(bp => bp.PackageId).Distinct().Count();
            
            if (offeredServicesCount + offeredPackagesCount >= ids.Count)
            {
                // Calculate pricing
                decimal totalPrice = 0m;
                foreach (var id in ids)
                {
                    var bs = branchServices.FirstOrDefault(x => x.BranchId == branch.Id && x.ServiceId == id);
                    if (bs != null)
                    {
                        totalPrice += bs.CustomPrice ?? services.FirstOrDefault(s => s.Id == id)?.BasePrice ?? 0m;
                    }
                    else
                    {
                        var bp = branchPackages.FirstOrDefault(x => x.BranchId == branch.Id && x.PackageId == id);
                        if (bp != null)
                        {
                            totalPrice += bp.CustomPrice ?? packages.FirstOrDefault(p => p.Id == id)?.BasePrice ?? 0m;
                        }
                    }
                }

                // Calculate OSRM road distance and travel fee
                var client = _httpClientFactory.CreateClient();
                var roadDistance = await GetRoadDistanceKm(latitude, longitude, (double)branch.Latitude, (double)branch.Longitude, client);
                decimal travelCost = (decimal)roadDistance * (branch.PerKmCharge ?? 0m);

                eligibleLabs.Add(new
                {
                    BranchId = branch.Id,
                    Name = branch.Name,
                    City = branch.City,
                    District = branch.District,
                    Distance = distance,
                    RoadDistance = roadDistance,
                    BaseTotal = totalPrice,
                    TravelFee = Math.Round(travelCost),
                    GrandTotal = totalPrice + Math.Round(travelCost)
                });
            }
        }

        return Ok(ApiResponse<List<object>>.SuccessResult(eligibleLabs, "Eligible labs retrieved successfully."));
    }

    [HttpGet("slots/branch/{branchId}")]
    [AllowAnonymous]
    [EndpointSummary("Get available slots for a specific lab branch")]
    public async Task<IActionResult> GetBranchSlots(
        [FromRoute] string branchId,
        CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var configs = await _context.BranchSlotConfigurations
            .Where(c => c.BranchId == branchId)
            .ToListAsync(cancellationToken);

        if (configs.Any())
        {
            var existingSlots = await _context.AppointmentSlots
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
                _context.AppointmentSlots.AddRange(newSlots);
                await _context.SaveChangesAsync(cancellationToken);
            }
        }

        var slots = await _context.AppointmentSlots
            .Where(s => s.BranchId == branchId && s.IsAvailable && s.SlotDate >= today)
            .OrderBy(s => s.SlotDate).ThenBy(s => s.StartTime)
            .ToListAsync(cancellationToken);

        return Ok(ApiResponse<List<AppointmentSlot>>.SuccessResult(slots, "Slots retrieved successfully."));
    }

    [HttpPost("{id}/cancel")]
    [EndpointSummary("Cancel appointment")]
    public async Task<IActionResult> CancelAppointment([FromRoute] string id, CancellationToken cancellationToken)
    {
        var currentUserId = _currentUserService.UserId?.ToString();
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized(ApiResponse.FailureResult("User not authenticated."));
        }

        var appointment = await _context.Appointments
            .Include(a => a.AppointmentSlot)
            .FirstOrDefaultAsync(a => a.Id == id && a.CustomerUserId == currentUserId, cancellationToken);

        if (appointment == null)
        {
            return NotFound(ApiResponse.FailureResult("Appointment not found."));
        }

        if (appointment.Status == AppointmentStatus.Cancelled)
        {
            return BadRequest(ApiResponse.FailureResult("Appointment is already cancelled."));
        }

        if (appointment.AppointmentSlot != null)
        {
            var slot = appointment.AppointmentSlot;
            slot.BookedCount = Math.Max(0, slot.BookedCount - appointment.MemberCount);
            slot.IsAvailable = true;
            _context.AppointmentSlots.Update(slot);
        }

        appointment.Status = AppointmentStatus.Cancelled;
        _context.Appointments.Update(appointment);
        await _context.SaveChangesAsync(cancellationToken);

        return Ok(ApiResponse.SuccessResult("Appointment cancelled successfully."));
    }

    [HttpPost("book")]
    [EndpointSummary("Book diagnostic appointment via location")]
    [EndpointDescription("Validates geographic distance, applies OSRM travel pricing, updates slot capacity concurrently, and logs booking details.")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<Appointment>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
    public async Task<IActionResult> BookAppointment([FromBody] WebBookingRequest request, CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.SlotId))
        {
            return BadRequest(ApiResponse.FailureResult("SlotId and coordinates are required."));
        }

        var itemIds = request.ItemIds ?? (string.IsNullOrEmpty(request.ServiceId) ? new List<string>() : new List<string> { request.ServiceId });
        if (!itemIds.Any())
        {
            return BadRequest(ApiResponse.FailureResult("At least one diagnostic service or package is required."));
        }

        var currentUserId = _currentUserService.UserId?.ToString();
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized(ApiResponse.FailureResult("User not authenticated."));
        }

        // 1. Fetch Slot
        var slot = await _context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == request.SlotId, cancellationToken);
        if (slot == null)
        {
            return NotFound(ApiResponse.FailureResult("Appointment slot not found."));
        }

        // Fetch Services and Packages
        var services = await _context.Services.AsNoTracking().Where(s => itemIds.Contains(s.Id)).ToListAsync(cancellationToken);
        var packages = await _context.Packages.AsNoTracking().Where(p => itemIds.Contains(p.Id)).ToListAsync(cancellationToken);

        if (services.Count + packages.Count != itemIds.Distinct().Count())
        {
            return BadRequest(ApiResponse.FailureResult("Some selected diagnostic services or packages could not be found."));
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
        var branchServices = await _context.BranchServices
            .Where(bs => bs.BranchId == branch.Id && itemIds.Contains(bs.ServiceId) && bs.IsActive)
            .ToListAsync(cancellationToken);

        var branchPackages = await _context.BranchPackages
            .Where(bp => bp.BranchId == branch.Id && itemIds.Contains(bp.PackageId) && bp.IsActive)
            .ToListAsync(cancellationToken);

        decimal rate = 0m;
        decimal totalCommissionPct = 0m;
        int itemsCount = 0;

        foreach (var itemId in itemIds)
        {
            var s = services.FirstOrDefault(x => x.Id == itemId);
            if (s != null)
            {
                var bs = branchServices.FirstOrDefault(x => x.ServiceId == itemId);
                rate += bs?.CustomPrice ?? s.BasePrice;
                totalCommissionPct += bs?.CustomCommissionPct ?? s.PlatformCommissionPct;
                itemsCount++;
            }
            else
            {
                var p = packages.FirstOrDefault(x => x.Id == itemId);
                if (p != null)
                {
                    var bp = branchPackages.FirstOrDefault(x => x.PackageId == itemId);
                    rate += bp?.CustomPrice ?? p.BasePrice;
                    totalCommissionPct += bp?.CustomCommissionPct ?? p.PlatformCommissionPct;
                    itemsCount++;
                }
            }
        }

        decimal avgCommissionPct = itemsCount > 0 ? (totalCommissionPct / itemsCount) : 15.00m;

        // Extra travel cost based on OSRM road distance
        decimal travelCost = (decimal)roadDistance * (branch.PerKmCharge ?? 0m);

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
        var itemNames = services.Select(s => s.Name).Concat(packages.Select(p => p.Name)).ToList();
        var itemNamesStr = string.Join(", ", itemNames);
        var locationAddress = $"{request.BuildingDetails}, Floor {request.Floor}, Landmark: {request.Landmark} | Tests: {itemNamesStr} (Includes ₹{Math.Round(travelCost)} travel fee for {roadDistance:F2} km)";

        var appointment = new Appointment
        {
            Id = Guid.NewGuid().ToString(),
            AppointmentNumber = bookingId,
            CustomerUserId = currentUserId,
            BranchId = branch.Id,
            AppointmentSlotId = slot.Id,
            LocationLatitude = (decimal)request.Latitude,
            LocationLongitude = (decimal)request.Longitude,
            LocationAddress = locationAddress,
            BuildingDetails = request.BuildingDetails,
            Floor = request.Floor,
            Landmark = request.Landmark,
            Passcode = new Random().Next(1000, 9999).ToString(),
            Status = AppointmentStatus.Confirmed,
            TotalAmount = total,
            PlatformCommission = total * (avgCommissionPct / 100m),
            LabPayout = total * (1m - (avgCommissionPct / 100m)),
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
                Relationship = i == 0 ? "Self" : "Family Member",
                UniqueNumber = $"MEM-{Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper()}",
                TestName = itemNamesStr
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

        // Generate Invoice PDF
        var invoicesFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "invoices");
        if (!Directory.Exists(invoicesFolder))
        {
            Directory.CreateDirectory(invoicesFolder);
        }
        var invoiceFileName = $"Invoice_{bookingId}.pdf";
        var invoiceFilePath = Path.Combine(invoicesFolder, invoiceFileName);

        var slotDisplay = $"{slot.SlotDate:dddd, MMM dd yyyy} @ {slot.StartTime:hh:mm tt}";
        var receiptText = $"APENIR DIAGNOSTIC SERVICES - INVOICE\n" +
                          $"--------------------------------------------------\n" +
                          $"Booking ID   : {bookingId}\n" +
                          $"Customer Name: {customerName}\n" +
                          $"Phone        : +{user?.Phone ?? ""}\n" +
                          $"Lab Branch   : {branch?.Name ?? "Selected Lab"}\n" +
                          $"Date & Time  : {slotDisplay}\n" +
                          $"Members      : {memberCount}\n" +
                          $"--------------------------------------------------\n" +
                          $"Booked Tests:\n" +
                          string.Join("\n", itemNames.Select(n => " - " + n)) + "\n" +
                          $"--------------------------------------------------\n" +
                          $"Total Paid   : INR {total}\n" +
                          $"--------------------------------------------------\n" +
                          $"Thank you for choosing Apenir Medical!";

        try
        {
            var pdfBytes = CreateSimplePdf(receiptText);
            await System.IO.File.WriteAllBytesAsync(invoiceFilePath, pdfBytes, cancellationToken);

            // Send to user's WhatsApp if they have a phone number
            if (user != null && !string.IsNullOrEmpty(user.Phone))
            {
                var confirmMsg =
                    $"✅ *Booking Confirmed!*\n\n" +
                    $"🆔 Booking ID: *{bookingId}*\n" +
                    $"🩸 Service: {itemNamesStr}\n" +
                    $"🏥 Lab: {branch?.Name ?? "Selected Lab"}\n" +
                    $"📅 Date & Time: {slotDisplay}\n" +
                    $"👥 Persons: {memberCount}\n" +
                    $"💰 Amount Paid: ₹{total}\n" +
                    $"🔑 Passcode/OTP: *{appointment.Passcode}*\n\n" +
                    $"🧪 *Instructions:*\n" +
                    $"• Fast for 8-10 hours prior to sample collection.\n" +
                    $"• Show the phlebotomist your Passcode (*{appointment.Passcode}*).\n" +
                    $"• Report PDF will be sent to your WhatsApp on completion.\n\n" +
                    $"Thank you for choosing LabCare! 🙏";

                await _whatsAppService.SendTextMessageAsync(user.Phone, confirmMsg);

                // Build public invoice URL
                var req = HttpContext.Request;
                var reqBaseUrl = $"{req.Scheme}://{req.Host}{req.PathBase}";
                var invoiceUrl = $"{reqBaseUrl}/invoices/{invoiceFileName}";
                await _whatsAppService.SendDocumentMessageAsync(user.Phone, invoiceUrl, $"Invoice_{bookingId}.pdf");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error generating or sending receipt: {ex.Message}");
        }

        return Ok(ApiResponse<Appointment>.SuccessResult(appointment, "Appointment booked successfully."));
    }

    private static byte[] CreateSimplePdf(string text)
    {
        var content = "BT\n/F1 12 Tf\n20 800 Td\n16 TL\n";
        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            var escapedLine = line.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
            content += $"({escapedLine}) Tj\nT*\n";
        }
        content += "ET";
        
        var streamBytes = Encoding.UTF8.GetBytes(content);
        var streamLen = streamBytes.Length;
        
        var pdfHeader = "%PDF-1.4\n" +
                        "1 0 obj\n" +
                        "<< /Type /Catalog /Pages 2 0 R >>\n" +
                        "endobj\n" +
                        "2 0 obj\n" +
                        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>\n" +
                        "endobj\n" +
                        "3 0 obj\n" +
                        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> /Contents 4 0 R >>\n" +
                        "endobj\n" +
                        "4 0 obj\n" +
                        $"<< /Length {streamLen} >>\n" +
                        "stream\n";
        
        var pdfFooter = "\nendstream\n" +
                        "endobj\n" +
                        "xref\n" +
                        "0 5\n" +
                        "0000000000 65535 f \n" +
                        "0000000009 00000 n \n" +
                        "0000000058 00000 n \n" +
                        "0000000115 00000 n \n" +
                        "0000000284 00000 n \n" +
                        "trailer\n" +
                        "<< /Size 5 /Root 1 0 R >>\n" +
                        "startxref\n" +
                        "350\n" +
                        "%%EOF";
                        
        var headerBytes = Encoding.UTF8.GetBytes(pdfHeader);
        var footerBytes = Encoding.UTF8.GetBytes(pdfFooter);
        
        var result = new byte[headerBytes.Length + streamBytes.Length + footerBytes.Length];
        Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
        Buffer.BlockCopy(streamBytes, 0, result, headerBytes.Length, streamBytes.Length);
        Buffer.BlockCopy(footerBytes, 0, result, headerBytes.Length + streamBytes.Length, footerBytes.Length);
        
        return result;
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
    public List<string>? ItemIds { get; set; }
    public string SlotId { get; set; } = string.Empty;
    public int MemberCount { get; set; } = 1;
    public string BuildingDetails { get; set; } = string.Empty;
    public string Landmark { get; set; } = string.Empty;
    public string Floor { get; set; } = string.Empty;
}
