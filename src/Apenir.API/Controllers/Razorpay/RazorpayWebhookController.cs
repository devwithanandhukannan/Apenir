using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Apenir.Core.Entities;
using Apenir.Core.Enums;
using Apenir.Core.Interfaces;

using System.Net.Http;
using Apenir.Application.Common.Interfaces;

namespace Apenir.API.Controllers;

[ApiController]
[Route("api/razorpay")]
[AllowAnonymous]
public class RazorpayWebhookController : ControllerBase
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _paymentLocks = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _slotLocks = new();
    private readonly IApplicationDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IWhatsAppService _whatsAppService;
    private readonly ILogger<RazorpayWebhookController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISettingsService _settingsService;

    public RazorpayWebhookController(
        IApplicationDbContext context,
        IConfiguration configuration,
        IWhatsAppService whatsAppService,
        ILogger<RazorpayWebhookController> logger,
        IHttpClientFactory httpClientFactory,
        ISettingsService settingsService)
    {
        _context = context;
        _configuration = configuration;
        _whatsAppService = whatsAppService;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _settingsService = settingsService;
    }

    [HttpPost("webhook")]
    [EndpointSummary("Razorpay Payment Success Webhook")]
    [EndpointDescription("Receives Razorpay payment event webhooks, verifies signature, captures booking, updates slot capacity, and notifies customer and lab via WhatsApp.")]
    public async Task<IActionResult> ReceiveWebhook(CancellationToken cancellationToken)
    {
        // 1. Read Raw Body
        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync(cancellationToken);

        // 2. Validate Signature
        var signature = Request.Headers["X-Razorpay-Signature"].ToString();
        var secret = await _settingsService.GetRazorpayWebhookSecretAsync() ?? string.Empty;

        if (!string.IsNullOrEmpty(secret) && !VerifySignature(rawBody, signature, secret))
        {
            _logger.LogWarning("⚠️ Razorpay Webhook received with invalid signature.");
            return BadRequest(new { status = "error", message = "Invalid signature verification" });
        }

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            var rzpEvent = root.GetProperty("event").GetString();

            _logger.LogInformation("Webhook event received: {Event}", rzpEvent);

            if (rzpEvent == "payment.captured" || rzpEvent == "order.paid" || rzpEvent == "payment_link.paid")
            {
                // Find payload entity
                JsonElement entity = default;
                bool foundEntity = false;

                if (root.TryGetProperty("payload", out var payloadEl))
                {
                    if (payloadEl.TryGetProperty("payment", out var pEl) && pEl.TryGetProperty("entity", out entity))
                    {
                        foundEntity = true;
                    }
                    else if (payloadEl.TryGetProperty("payment_link", out var plEl) && plEl.TryGetProperty("entity", out entity))
                    {
                        foundEntity = true;
                    }
                    else if (payloadEl.TryGetProperty("order", out var oEl) && oEl.TryGetProperty("entity", out entity))
                    {
                        foundEntity = true;
                    }
                }

                if (foundEntity && entity.TryGetProperty("notes", out var notes))
                {
                    // Check if this is a multi-lab webhook
                    if (notes.TryGetProperty("is_multi_lab", out var mlProp) && mlProp.GetString() == "true")
                    {
                        var parentId = notes.TryGetProperty("parent_appointment_id", out var piEl) ? piEl.GetString() : string.Empty;
                        var paymentId = entity.TryGetProperty("id", out var payIdEl) ? payIdEl.GetString() : $"pay_webhook_{Guid.NewGuid().ToString("N")[..12]}";
                        var orderId = entity.TryGetProperty("order_id", out var ordIdEl) ? ordIdEl.GetString() : $"order_webhook_{Guid.NewGuid().ToString("N")[..12]}";

                        if (!string.IsNullOrEmpty(parentId))
                        {
                            await ProcessMultiLabPaymentBooking(parentId, paymentId ?? string.Empty, orderId ?? string.Empty, cancellationToken);
                        }
                    }
                    else if (notes.TryGetProperty("phone", out var phoneProp) && notes.TryGetProperty("selected_test_id", out var testProp))
                    {
                        var to = phoneProp.GetString() ?? string.Empty;
                        var testId = testProp.GetString() ?? string.Empty;
                        var labId = notes.TryGetProperty("selected_lab_id", out var labEl) ? labEl.GetString() : string.Empty;
                        var slotId = notes.TryGetProperty("selected_slot_id", out var slotEl) ? slotEl.GetString() : string.Empty;
                        
                        notes.TryGetProperty("member_count", out var mcEl);
                        int.TryParse(mcEl.GetString() ?? "1", out var memberCount);
                        if (memberCount < 1) memberCount = 1;

                        var building = notes.TryGetProperty("building_details", out var bdEl) ? bdEl.GetString() : string.Empty;
                        var landmark = notes.TryGetProperty("landmark", out var lmEl) ? lmEl.GetString() : string.Empty;
                        var floor = notes.TryGetProperty("floor", out var flEl) ? flEl.GetString() : string.Empty;

                        notes.TryGetProperty("latitude", out var latEl);
                        double.TryParse(latEl.GetString() ?? "0", out var latitude);

                        notes.TryGetProperty("longitude", out var lngEl);
                        double.TryParse(lngEl.GetString() ?? "0", out var longitude);

                        var paymentId = entity.TryGetProperty("id", out var payIdEl) ? payIdEl.GetString() : $"pay_webhook_{Guid.NewGuid().ToString("N")[..12]}";
                        var orderId = entity.TryGetProperty("order_id", out var ordIdEl) ? ordIdEl.GetString() : $"order_webhook_{Guid.NewGuid().ToString("N")[..12]}";

                        var memberSelections = notes.TryGetProperty("member_selections", out var msEl) ? msEl.GetString() : string.Empty;

                        // Handle Booking dynamically
                        await ProcessPaymentBooking(
                            to, testId, labId, slotId, memberCount,
                            building ?? "Not specified", landmark ?? "Not specified", floor ?? "Not specified",
                            (decimal)latitude, (decimal)longitude, paymentId ?? string.Empty, orderId ?? string.Empty,
                            memberSelections ?? string.Empty,
                            cancellationToken
                        );
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Razorpay webhook payload.");
            return StatusCode(500, new { status = "error", message = ex.Message });
        }

        return Ok(new { status = "success" });
    }

    private async Task ProcessPaymentBooking(
        string to, string testId, string labId, string slotId, int memberCount,
        string building, string landmark, string floor, decimal latitude, decimal longitude,
        string paymentId, string orderId, string memberSelections, CancellationToken cancellationToken)
    {
        var lockObj = _paymentLocks.GetOrAdd(paymentId, _ => new SemaphoreSlim(1, 1));
        await lockObj.WaitAsync(cancellationToken);

        try
        {
            var slotLock = _slotLocks.GetOrAdd(slotId, _ => new SemaphoreSlim(1, 1));
            await slotLock.WaitAsync(cancellationToken);
            try
            {
            // 1. Fetch related data
            var itemIds = testId.Split(',').Select(id => id.Trim()).ToList();
            var services = await _context.Services.AsNoTracking().Where(s => itemIds.Contains(s.Id)).ToListAsync(cancellationToken);
            var packages = await _context.Packages.AsNoTracking().Where(p => itemIds.Contains(p.Id)).ToListAsync(cancellationToken);

            var lab = await _context.Branches.FirstOrDefaultAsync(b => b.Id == labId, cancellationToken);
            var slot = await _context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == slotId, cancellationToken);

            // 2. Fetch or create Customer early to check for existing recent appointments
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Phone == to, cancellationToken);
            if (user != null)
            {
                var recentAppt = await _context.Appointments.FirstOrDefaultAsync(
                    a => a.CustomerUserId == user.Id && a.AppointmentSlotId == slotId && a.CreatedAt >= DateTime.UtcNow.AddMinutes(-30),
                    cancellationToken);

                if (recentAppt != null)
                {
                    _logger.LogInformation("ℹ️ Booking already exists (recently created via SimulatePayment) for User {UserId} and Slot {SlotId}. Skipping webhook.", user.Id, slotId);
                    return;
                }
            }

            if (slot == null || !slot.IsAvailable || slot.BookedCount + memberCount > slot.MaxCapacity)
            {
                _logger.LogWarning("⚠️ Slot not available: {SlotId}. Refusing webhook creation.", slotId);
                // Send warning WhatsApp message
                await _whatsAppService.SendTextMessageAsync(to, "⚠️ We received your payment, but the selected slot has filled up. Our customer support will contact you to reschedule your test immediately.");
                return;
            }

            // Check if appointment already created for this payment ID to prevent duplicate webhook deliveries
            var paymentExists = await _context.Payments.AnyAsync(p => p.RazorpayPaymentId == paymentId, cancellationToken);
            if (paymentExists)
            {
                _logger.LogInformation("ℹ️ Webhook received for already processed payment ID: {PaymentId}", paymentId);
                return;
            }

            if (user == null)
            {
                user = new User
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = "WhatsApp User",
                    Phone = to,
                    Role = UserRole.Customer,
                    IsActive = true
                };
                _context.Users.Add(user);

                var customer = new Customer
                {
                    Id = Guid.NewGuid().ToString(),
                    UserId = user.Id,
                    Phone = to,
                    Name = "WhatsApp User"
                };
                _context.Customers.Add(customer);
                await _context.SaveChangesAsync(cancellationToken);
            }

            // Calculate Pricing for multiple items
            var branchServices = await _context.BranchServices
                .Where(bs => bs.BranchId == labId && itemIds.Contains(bs.ServiceId) && bs.IsActive)
                .ToListAsync(cancellationToken);

            var branchPackages = await _context.BranchPackages
                .Where(bp => bp.BranchId == labId && itemIds.Contains(bp.PackageId) && bp.IsActive)
                .ToListAsync(cancellationToken);

            var parsedSelections = new List<(string Name, List<string> ItemIds)>();
            if (!string.IsNullOrWhiteSpace(memberSelections))
            {
                var parts = memberSelections.Split(';');
                foreach (var part in parts)
                {
                    if (string.IsNullOrWhiteSpace(part)) continue;
                    var subparts = part.Split(':');
                    if (subparts.Length == 2)
                    {
                        var name = subparts[0];
                        var items = subparts[1].Split(',').Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();
                        parsedSelections.Add((name, items));
                    }
                }
            }

            decimal totalBaseAmount = 0m;
            decimal totalCommissionPct = 0m;
            int totalItemsCount = 0;

            if (parsedSelections.Any())
            {
                for (int i = 0; i < parsedSelections.Count; i++)
                {
                    var selection = parsedSelections[i];
                    decimal memberSum = 0m;
                    foreach (var itemId in selection.ItemIds)
                    {
                        var s = services.FirstOrDefault(x => x.Id == itemId);
                        if (s != null)
                        {
                            var bs = branchServices.FirstOrDefault(x => x.ServiceId == itemId);
                            memberSum += bs?.CustomPrice ?? s.BasePrice;
                            totalCommissionPct += bs?.CustomCommissionPct ?? s.PlatformCommissionPct;
                            totalItemsCount++;
                        }
                        else
                        {
                            var p = packages.FirstOrDefault(x => x.Id == itemId);
                            if (p != null)
                            {
                                var bp = branchPackages.FirstOrDefault(x => x.PackageId == itemId);
                                memberSum += bp?.CustomPrice ?? p.BasePrice;
                                totalCommissionPct += bp?.CustomCommissionPct ?? p.PlatformCommissionPct;
                                totalItemsCount++;
                            }
                        }
                    }

                    if (i == 0)
                    {
                        totalBaseAmount += memberSum;
                    }
                    else
                    {
                        totalBaseAmount += memberSum * 0.8m;
                    }
                }
            }
            else
            {
                decimal rate = 0m;
                foreach (var itemId in itemIds)
                {
                    var s = services.FirstOrDefault(x => x.Id == itemId);
                    if (s != null)
                    {
                        var bs = branchServices.FirstOrDefault(x => x.ServiceId == itemId);
                        rate += bs?.CustomPrice ?? s.BasePrice;
                        totalCommissionPct += bs?.CustomCommissionPct ?? s.PlatformCommissionPct;
                        totalItemsCount++;
                    }
                    else
                    {
                        var p = packages.FirstOrDefault(x => x.Id == itemId);
                        if (p != null)
                        {
                            var bp = branchPackages.FirstOrDefault(x => x.PackageId == itemId);
                            rate += bp?.CustomPrice ?? p.BasePrice;
                            totalCommissionPct += bp?.CustomCommissionPct ?? p.PlatformCommissionPct;
                            totalItemsCount++;
                        }
                    }
                }

                totalBaseAmount = rate + (memberCount > 1
                    ? (memberCount - 1) * rate * 0.8m
                    : 0);
            }

            decimal avgCommissionPct = totalItemsCount > 0 ? (totalCommissionPct / totalItemsCount) : 15.00m;

            // Compute OSRM travel cost
            double roadDistance = 0;
            if (lab != null)
            {
                var client = _httpClientFactory.CreateClient();
                roadDistance = await GetRoadDistanceKm((double)latitude, (double)longitude, (double)lab.Latitude, (double)lab.Longitude, client);
            }
            decimal travelCost = (decimal)roadDistance * (lab?.PerKmCharge ?? 0m);

            int total = (int)Math.Round(totalBaseAmount) + (int)Math.Round(travelCost);

            // 3. Update Slot
            slot.BookedCount += memberCount;
            if (slot.BookedCount >= slot.MaxCapacity)
                slot.IsAvailable = false;
            _context.AppointmentSlots.Update(slot);

            // 4. Create Booking ID
            var bookingId = $"BK-{DateTime.UtcNow:yyyyMMdd}-{new Random().Next(1000, 9999)}";

            var itemNames = services.Select(s => s.Name).Concat(packages.Select(p => p.Name)).ToList();
            var itemNamesStr = string.Join(", ", itemNames);
            var locationAddress = $"{building}, Floor {floor}, Landmark: {landmark} | Tests: {itemNamesStr}";

            // 5. Create Appointment
            var appointment = new Appointment
            {
                Id = Guid.NewGuid().ToString(),
                AppointmentNumber = bookingId,
                CustomerUserId = user.Id,
                BranchId = labId,
                AppointmentSlotId = slotId,
                LocationLatitude = latitude,
                LocationLongitude = longitude,
                LocationAddress = locationAddress,
                BuildingDetails = building,
                Floor = floor,
                Landmark = landmark,
                Passcode = new Random().Next(1000, 9999).ToString(),
                Status = AppointmentStatus.Confirmed,
                TotalAmount = total,
                PlatformCommission = Math.Round(totalBaseAmount * (avgCommissionPct / 100m), 2),
                LabPayout = total - Math.Round(totalBaseAmount * (avgCommissionPct / 100m), 2),
                CreatedAt = DateTime.UtcNow,
                MemberCount = memberCount,
                ItemIds = itemIds // Phase 2+3: persist cart items on appointment
            };
            _context.Appointments.Add(appointment);

            // Create Members
            var customerName = string.IsNullOrWhiteSpace(user.Name) ? "Patient" : user.Name;
            if (parsedSelections.Any())
            {
                for (int i = 0; i < parsedSelections.Count; i++)
                {
                    var selection = parsedSelections[i];
                    var memberName = string.IsNullOrWhiteSpace(selection.Name) || selection.Name == "Self" ? customerName : selection.Name;
                    var relationship = i == 0 ? "Self" : "Family Member";

                    var memberItemNames = services.Where(s => selection.ItemIds.Contains(s.Id)).Select(s => s.Name)
                                            .Concat(packages.Where(p => selection.ItemIds.Contains(p.Id)).Select(p => p.Name))
                                            .ToList();
                    var memberTestNamesStr = string.Join(", ", memberItemNames);

                    // Phase 2: calculate per-member price
                    decimal memberAmount = 0m;
                    foreach (var itemId in selection.ItemIds)
                    {
                        var s = services.FirstOrDefault(x => x.Id == itemId);
                        if (s != null) { var bs = branchServices.FirstOrDefault(x => x.ServiceId == itemId); memberAmount += bs?.CustomPrice ?? s.BasePrice; }
                        else { var p = packages.FirstOrDefault(x => x.Id == itemId); if (p != null) { var bp = branchPackages.FirstOrDefault(x => x.PackageId == itemId); memberAmount += bp?.CustomPrice ?? p.BasePrice; } }
                    }
                    if (i > 0) memberAmount = Math.Round(memberAmount * 0.8m, 2);

                    var member = new AppointmentMember
                    {
                        Id = Guid.NewGuid().ToString(),
                        AppointmentId = appointment.Id,
                        MemberName = memberName,
                        Age = 0,
                        Gender = Gender.Other,
                        Relationship = relationship,
                        UniqueNumber = $"MEM-{Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper()}",
                        TestName = memberTestNamesStr,
                        ServiceItemIds = selection.ItemIds, // Phase 2: store per-member services
                        Amount = memberAmount               // Phase 2: store per-member amount
                    };
                    _context.AppointmentMembers.Add(member);
                }
            }
            else
            {
                decimal baseRatePerMember = 0m;
                var allItemNames2 = services.Select(s => s.Name).Concat(packages.Select(p => p.Name)).ToList();
                foreach (var itemId in itemIds)
                {
                    var s = services.FirstOrDefault(x => x.Id == itemId);
                    if (s != null) { var bs = branchServices.FirstOrDefault(x => x.ServiceId == itemId); baseRatePerMember += bs?.CustomPrice ?? s.BasePrice; }
                    else { var p = packages.FirstOrDefault(x => x.Id == itemId); if (p != null) { var bp = branchPackages.FirstOrDefault(x => x.PackageId == itemId); baseRatePerMember += bp?.CustomPrice ?? p.BasePrice; } }
                }

                for (int i = 0; i < memberCount; i++)
                {
                    decimal memberAmount = i == 0 ? baseRatePerMember : Math.Round(baseRatePerMember * 0.8m, 2);
                    var member = new AppointmentMember
                    {
                        Id = Guid.NewGuid().ToString(),
                        AppointmentId = appointment.Id,
                        MemberName = i == 0 ? customerName : $"Member {i + 1}",
                        Age = 0,
                        Gender = Gender.Other,
                        Relationship = i == 0 ? "Self" : "Family Member",
                        UniqueNumber = $"MEM-{Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper()}",
                        TestName = itemNamesStr,
                        ServiceItemIds = itemIds,   // Phase 2: all items for each member
                        Amount = memberAmount        // Phase 2: per-member amount
                    };
                    _context.AppointmentMembers.Add(member);
                }
            }

            // Create Payment record
            var payment = new Payment
            {
                Id = Guid.NewGuid().ToString(),
                AppointmentId = appointment.Id,
                RazorpayOrderId = orderId,
                RazorpayPaymentId = paymentId,
                Status = PaymentStatus.Paid,
                PaymentMethod = PaymentMethod.UPI,
                PaidAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            };
            _context.Payments.Add(payment);

            await _context.SaveChangesAsync(cancellationToken);

            // Reset WhatsApp session to Done state so they don't get stuck
            var session = await _context.WhatsAppSessions.FirstOrDefaultAsync(s => s.Phone == to, cancellationToken);
            if (session != null)
            {
                session.CurrentState = WhatsAppState.Done;
                session.SelectedLabId = labId;
                session.SelectedSlot = slotId;
                session.MemberCount = memberCount;
                session.SelectedTestId = testId;
                session.UpdatedAt = DateTime.UtcNow;
                _context.WhatsAppSessions.Update(session);
                await _context.SaveChangesAsync(cancellationToken);
            }

            var slotDisplay = $"{slot.SlotDate:dddd, MMM dd yyyy} @ {slot.StartTime:hh:mm tt}";

            // Generate Invoice PDF
            var invoicesFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "invoices");
            if (!Directory.Exists(invoicesFolder))
            {
                Directory.CreateDirectory(invoicesFolder);
            }
            var invoiceFileName = $"Invoice_{bookingId}.pdf";
            var invoiceFilePath = Path.Combine(invoicesFolder, invoiceFileName);

            var receiptText = $"APENIR DIAGNOSTIC SERVICES - INVOICE\n" +
                              $"--------------------------------------------------\n" +
                              $"Booking ID   : {bookingId}\n" +
                              $"Customer Name: {customerName}\n" +
                              $"Phone        : +{to}\n" +
                              $"Lab Branch   : {lab?.Name ?? "Selected Lab"}\n" +
                              $"Date & Time  : {slotDisplay}\n" +
                              $"Members      : {memberCount}\n" +
                              $"--------------------------------------------------\n" +
                              $"Booked Tests:\n" +
                              string.Join("\n", itemNames.Select(n => " - " + n)) + "\n" +
                              $"--------------------------------------------------\n" +
                              $"Total Paid   : INR {total}\n" +
                              $"--------------------------------------------------\n" +
                              $"Thank you for choosing Apenir Medical!";

            var pdfBytes = CreateSimplePdf(receiptText);
            await System.IO.File.WriteAllBytesAsync(invoiceFilePath, pdfBytes, cancellationToken);

            var req = HttpContext.Request;
            var reqBaseUrl = $"{req.Scheme}://{req.Host}{req.PathBase}";
            var invoiceUrl = $"{reqBaseUrl}/invoices/{invoiceFileName}";

            // Notify Lab via WhatsApp NotificationPhone
            if (lab != null && !string.IsNullOrEmpty(lab.NotificationPhone))
            {
                var labNotification = $"🔔 *New Diagnostic Request!*\n\n" +
                                      $"Booking ID: *{bookingId}*\n" +
                                      $"Test: {itemNamesStr}\n" +
                                      $"Slot: {slotDisplay}\n" +
                                      $"Members: {memberCount}\n" +
                                      $"Place: {appointment.LocationAddress}\n" +
                                      $"Customer Phone: +{to}";

                await _whatsAppService.SendTextMessageAsync(lab.NotificationPhone, labNotification);
            }

            // Send WhatsApp confirmation and Invoice to Customer
            var confirmMsg =
                $"✅ *Booking Confirmed!*\n\n" +
                $"🆔 Booking ID: *{bookingId}*\n" +
                $"🩸 Service: {itemNamesStr}\n" +
                $"🏥 Lab: {lab?.Name ?? "Selected Lab"}\n" +
                $"📅 Date & Time: {slotDisplay}\n" +
                $"👥 Persons: {memberCount}\n" +
                $"💰 Amount Paid: ₹{total}\n" +
                $"🔑 Passcode/OTP: *{appointment.Passcode}*\n\n" +
                $"🧪 *Instructions:*\n" +
                $"• Fast for 8-10 hours prior to sample collection.\n" +
                $"• Show the phlebotomist your Passcode (*{appointment.Passcode}*).\n" +
                $"• Report PDF will be sent to your WhatsApp on completion.\n\n" +
                $"Thank you for choosing LabCare! 🙏";

            await _whatsAppService.SendTextMessageAsync(to, confirmMsg);
            await _whatsAppService.SendDocumentMessageAsync(to, invoiceUrl, $"Invoice_{bookingId}.pdf");
            }
            finally
            {
                slotLock.Release();
            }
        }
        finally
        {
            lockObj.Release();
        }
    }

    private async Task<double> GetRoadDistanceKm(double lat1, double lng1, double lat2, double lng2, HttpClient client)
    {
        try
        {
            var lon1Str = lng1.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var lat1Str = lat1.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var lon2Str = lng2.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var lat2Str = lat2.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var url = $"http://router.project-osrm.org/route/v1/driving/{lon1Str},{lat1Str};{lon2Str},{lat2Str}?overview=false";

            client.Timeout = TimeSpan.FromSeconds(3);
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
                            return distanceMeters / 1000.0;
                        }
                    }
                }
            }
        }
        catch
        {
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

    private bool VerifySignature(string rawBody, string signature, string secret)
    {
        try
        {
            var keyBytes = Encoding.UTF8.GetBytes(secret);
            var bodyBytes = Encoding.UTF8.GetBytes(rawBody);
            using var hmac = new HMACSHA256(keyBytes);
            var hashBytes = hmac.ComputeHash(bodyBytes);
            var computedSignature = Convert.ToHexString(hashBytes).ToLower();
            return string.Equals(computedSignature, signature, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task ProcessMultiLabPaymentBooking(
        string parentAppointmentId, string paymentId, string orderId, CancellationToken cancellationToken)
    {
        var lockObj = _paymentLocks.GetOrAdd(parentAppointmentId, _ => new SemaphoreSlim(1, 1));
        await lockObj.WaitAsync(cancellationToken);

        try
        {
            // 1. Fetch parent appointment
            var parentAppt = await _context.Appointments
                .FirstOrDefaultAsync(a => a.Id == parentAppointmentId, cancellationToken);

            if (parentAppt == null)
            {
                _logger.LogWarning("⚠️ Parent appointment not found for Id: {ParentId}", parentAppointmentId);
                return;
            }

            // Check if already processed
            var paymentExists = await _context.Payments.AnyAsync(p => p.RazorpayPaymentId == paymentId, cancellationToken);
            if (paymentExists)
            {
                _logger.LogInformation("ℹ️ Webhook received for already processed multi-lab payment ID: {PaymentId}", paymentId);
                return;
            }

            // 2. Fetch children
            var childAppts = await _context.Appointments
                .Where(a => a.ParentAppointmentId == parentAppointmentId)
                .ToListAsync(cancellationToken);

            // 3. Mark parent as Confirmed
            parentAppt.Status = AppointmentStatus.Confirmed;
            _context.Appointments.Update(parentAppt);

            // Create payment record for parent
            var parentPayment = new Payment
            {
                Id = Guid.NewGuid().ToString(),
                AppointmentId = parentAppt.Id,
                RazorpayOrderId = orderId,
                RazorpayPaymentId = paymentId,
                Status = PaymentStatus.Paid,
                PaymentMethod = PaymentMethod.UPI,
                PaidAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            };
            _context.Payments.Add(parentPayment);

            // 4. Mark children as Confirmed & create payment records
            foreach (var child in childAppts)
            {
                child.Status = AppointmentStatus.Confirmed;
                _context.Appointments.Update(child);

                var childPayment = new Payment
                {
                    Id = Guid.NewGuid().ToString(),
                    AppointmentId = child.Id,
                    RazorpayOrderId = orderId,
                    RazorpayPaymentId = paymentId, // same payment reference
                    Status = PaymentStatus.Paid,
                    PaymentMethod = PaymentMethod.UPI,
                    PaidAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow
                };
                _context.Payments.Add(childPayment);

                // Notify each lab!
                var lab = await _context.Branches.FirstOrDefaultAsync(b => b.Id == child.BranchId, cancellationToken);
                if (lab != null && !string.IsNullOrEmpty(lab.NotificationPhone))
                {
                    var slot = await _context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == child.AppointmentSlotId, cancellationToken);
                    var slotDisplay = slot != null ? $"{slot.SlotDate:dd MMM yyyy} @ {slot.StartTime:hh:mm tt}" : "Not specified";
                    
                    // Fetch service names for child
                    var serviceNames = await _context.Services.AsNoTracking().Where(s => child.ItemIds.Contains(s.Id)).Select(s => s.Name).ToListAsync(cancellationToken);
                    var packageNames = await _context.Packages.AsNoTracking().Where(p => child.ItemIds.Contains(p.Id)).Select(p => p.Name).ToListAsync(cancellationToken);
                    var itemNamesStr = string.Join(", ", serviceNames.Concat(packageNames));

                    var labNotification = $"🔔 *New Diagnostic Request!*\n\n" +
                                          $"Booking ID: *{child.AppointmentNumber}*\n" +
                                          $"Test: {itemNamesStr}\n" +
                                          $"Slot: {slotDisplay}\n" +
                                          $"Members: {child.MemberCount}\n" +
                                          $"Place: {child.LocationAddress}";

                    try
                    {
                        await _whatsAppService.SendTextMessageAsync(lab.NotificationPhone, labNotification);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send WhatsApp notification to lab {LabId}", child.BranchId);
                    }
                }
            }

            await _context.SaveChangesAsync(cancellationToken);

            // Send notification to customer
            var customer = await _context.Users.FirstOrDefaultAsync(u => u.Id == parentAppt.CustomerUserId, cancellationToken);
            if (customer != null && !string.IsNullOrEmpty(customer.Phone))
            {
                var customerMsg = $"✅ *Payment Confirmed!*\n\n" +
                                  $"🆔 Booking ID: *{parentAppt.AppointmentNumber}*\n" +
                                  $"💰 Total Paid: ₹{parentAppt.TotalAmount}\n" +
                                  $"🔑 Shared Passcode: *{parentAppt.Passcode}*\n\n" +
                                  $"Your bookings are confirmed with respective labs. They will visit your location as scheduled.";

                try
                {
                    await _whatsAppService.SendTextMessageAsync(customer.Phone, customerMsg);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send WhatsApp notification to customer");
                }
            }
        }
        finally
        {
            lockObj.Release();
        }
    }
}
