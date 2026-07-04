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

namespace Apenir.API.Controllers;

[ApiController]
[Route("api/razorpay")]
[AllowAnonymous]
public class RazorpayWebhookController : ControllerBase
{
    private readonly IApplicationDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IWhatsAppService _whatsAppService;
    private readonly ILogger<RazorpayWebhookController> _logger;

    public RazorpayWebhookController(
        IApplicationDbContext context,
        IConfiguration configuration,
        IWhatsAppService whatsAppService,
        ILogger<RazorpayWebhookController> logger)
    {
        _context = context;
        _configuration = configuration;
        _whatsAppService = whatsAppService;
        _logger = logger;
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
        var secret = _configuration["Razorpay:WebhookSecret"] ?? string.Empty;

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
                    // Check if this notes payload has our booking properties
                    if (notes.TryGetProperty("phone", out var phoneProp) && notes.TryGetProperty("selected_test_id", out var testProp))
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

                        // Handle Booking dynamically
                        await ProcessPaymentBooking(
                            to, testId, labId, slotId, memberCount,
                            building ?? "Not specified", landmark ?? "Not specified", floor ?? "Not specified",
                            (decimal)latitude, (decimal)longitude, paymentId ?? string.Empty, orderId ?? string.Empty,
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
        string paymentId, string orderId, CancellationToken cancellationToken)
    {
        // 1. Fetch related data
        var service = await _context.Services.FirstOrDefaultAsync(s => s.Id == testId, cancellationToken);
        var lab = await _context.Branches.FirstOrDefaultAsync(b => b.Id == labId, cancellationToken);
        var slot = await _context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == slotId, cancellationToken);

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

        // 2. Fetch or create Customer
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Phone == to, cancellationToken);
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

        // Calculate Pricing
        var basePrice = service?.BasePrice ?? 0m;
        var branchService = await _context.BranchServices
            .FirstOrDefaultAsync(bs => bs.BranchId == labId && bs.ServiceId == testId && bs.IsActive, cancellationToken);
        decimal rate = branchService?.CustomPrice ?? basePrice;

        int total = (int)rate + (memberCount > 1
            ? (int)Math.Round((memberCount - 1) * rate * 0.8m)
            : 0);

        // 3. Update Slot
        slot.BookedCount += memberCount;
        if (slot.BookedCount >= slot.MaxCapacity)
            slot.IsAvailable = false;
        _context.AppointmentSlots.Update(slot);

        // 4. Create Booking ID
        var bookingId = $"BK-{DateTime.UtcNow:yyyyMMdd}-{new Random().Next(1000, 9999)}";

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
            LocationAddress = $"{building}, Floor {floor}, Landmark: {landmark}",
            BuildingDetails = building,
            Floor = floor,
            Landmark = landmark,
            Passcode = new Random().Next(1000, 9999).ToString(),
            Status = AppointmentStatus.Confirmed,
            TotalAmount = total,
            PlatformCommission = total * (((branchService != null && branchService.CustomCommissionPct.HasValue) ? branchService.CustomCommissionPct.Value : (service != null ? service.PlatformCommissionPct : 15.00m)) / 100m),
            LabPayout = total * (1m - ((branchService != null && branchService.CustomCommissionPct.HasValue) ? branchService.CustomCommissionPct.Value : (service != null ? service.PlatformCommissionPct : 15.00m)) / 100m),
            CreatedAt = DateTime.UtcNow,
            MemberCount = memberCount
        };
        _context.Appointments.Add(appointment);

        // Create Members
        var customerName = string.IsNullOrWhiteSpace(user.Name) ? "Patient" : user.Name;
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

        // Notify Lab via WhatsApp NotificationPhone
        if (lab != null && !string.IsNullOrEmpty(lab.NotificationPhone))
        {
            var labNotification = $"🔔 *New Diagnostic Request!*\n\n" +
                                  $"Booking ID: *{bookingId}*\n" +
                                  $"Test: {service?.Name}\n" +
                                  $"Slot: {slotDisplay}\n" +
                                  $"Members: {memberCount}\n" +
                                  $"Place: {appointment.LocationAddress}\n" +
                                  $"Customer Phone: +{to}";

            await _whatsAppService.SendTextMessageAsync(lab.NotificationPhone, labNotification);
        }

        // Send WhatsApp confirmation to Customer
        var confirmMsg =
            $"✅ *Booking Confirmed!*\n\n" +
            $"🆔 Booking ID: *{bookingId}*\n" +
            $"🩸 Service: {service?.Name ?? "Diagnostic Test"}\n" +
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
}
