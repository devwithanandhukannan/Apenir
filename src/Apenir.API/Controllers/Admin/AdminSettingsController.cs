using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Apenir.Core.Entities;
using Apenir.Core.Interfaces;
using Apenir.API.Filters;
using Apenir.Application.Common.Interfaces;
using Apenir.Application.Common.Models;

namespace Apenir.API.Controllers.Admin;

[ApiController]
[Route("api/admin/settings")]
[Authorize]
[AdminOnly]
public class AdminSettingsController : ControllerBase
{
    private readonly ISettingsService _settingsService;
    private readonly IWhatsAppService _whatsAppService;
    private readonly IEmailService _emailService;
    private readonly IHttpClientFactory _httpClientFactory;

    public AdminSettingsController(
        ISettingsService settingsService,
        IWhatsAppService whatsAppService,
        IEmailService emailService,
        IHttpClientFactory httpClientFactory)
    {
        _settingsService = settingsService;
        _whatsAppService = whatsAppService;
        _emailService = emailService;
        _httpClientFactory = httpClientFactory;
    }

    [HttpGet]
    [EndpointSummary("Get Admin/System Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSettings()
    {
        var settings = await _settingsService.GetSettingsAsync();
        
        var response = new
        {
            whatsAppPhoneNumberId = settings.WhatsAppPhoneNumberId ?? string.Empty,
            whatsAppApiVersion = settings.WhatsAppApiVersion ?? "v25.0",
            whatsAppAccessToken = string.IsNullOrEmpty(settings.WhatsAppAccessToken) ? string.Empty : "********",
            
            razorpayKeyId = settings.RazorpayKeyId ?? string.Empty,
            razorpayKeySecret = string.IsNullOrEmpty(settings.RazorpayKeySecret) ? string.Empty : "********",
            razorpayWebhookSecret = settings.RazorpayWebhookSecret ?? string.Empty,
            
            smtpHost = settings.SmtpHost ?? string.Empty,
            smtpPort = settings.SmtpPort == 0 ? 587 : settings.SmtpPort,
            smtpUsername = settings.SmtpUsername ?? string.Empty,
            smtpPassword = string.IsNullOrEmpty(settings.SmtpPassword) ? string.Empty : "********",
            smtpEnableSsl = settings.SmtpEnableSsl,
            smtpFromEmail = settings.SmtpFromEmail ?? string.Empty
        };

        return Ok(ApiResponse<object>.SuccessResult(response));
    }

    [HttpPut]
    [EndpointSummary("Update Admin/System Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateSettings([FromBody] SystemSettingsDto dto)
    {
        if (dto == null) return BadRequest(ApiResponse.FailureResult("Invalid request body"));

        var settings = new SystemSettings
        {
            WhatsAppAccessToken = dto.WhatsAppAccessToken,
            WhatsAppPhoneNumberId = dto.WhatsAppPhoneNumberId,
            WhatsAppApiVersion = dto.WhatsAppApiVersion,
            RazorpayKeyId = dto.RazorpayKeyId,
            RazorpayKeySecret = dto.RazorpayKeySecret,
            RazorpayWebhookSecret = dto.RazorpayWebhookSecret,
            SmtpHost = dto.SmtpHost,
            SmtpPort = dto.SmtpPort,
            SmtpUsername = dto.SmtpUsername,
            SmtpPassword = dto.SmtpPassword,
            SmtpEnableSsl = dto.SmtpEnableSsl,
            SmtpFromEmail = dto.SmtpFromEmail
        };

        await _settingsService.SaveSettingsAsync(settings);
        return Ok(ApiResponse.SuccessResult("Settings updated successfully"));
    }

    [HttpPost("test-whatsapp")]
    [EndpointSummary("Test WhatsApp Config")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> TestWhatsApp([FromBody] TestWhatsAppDto dto)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.PhoneNumber))
        {
            return BadRequest(ApiResponse.FailureResult("Recipient phone number is required"));
        }

        try
        {
            // Send test message
            await _whatsAppService.SendTextMessageAsync(dto.PhoneNumber.Trim(), "Hello! This is a test message from OmniLab settings panel. WhatsApp API is working correctly.");
            return Ok(ApiResponse.SuccessResult("Test message sent successfully. Please check your device."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse.FailureResult($"WhatsApp test failed: {ex.Message}"));
        }
    }

    [HttpPost("test-email")]
    [EndpointSummary("Test SMTP Config")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> TestEmail([FromBody] TestEmailDto dto)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Email))
        {
            return BadRequest(ApiResponse.FailureResult("Recipient email is required"));
        }

        try
        {
            // Send test email
            await _emailService.SendEmailAsync(dto.Email.Trim(), "OmniLab SMTP Test", "<p>This is a test email sent from OmniLab settings panel. SMTP settings are valid and working!</p>");
            return Ok(ApiResponse.SuccessResult("Test email sent successfully. Please check your inbox."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse.FailureResult($"SMTP test failed: {ex.Message}"));
        }
    }

    [HttpPost("test-razorpay")]
    [EndpointSummary("Test Razorpay Config")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> TestRazorpay()
    {
        try
        {
            var keyId = await _settingsService.GetRazorpayKeyIdAsync();
            var keySecret = await _settingsService.GetRazorpayKeySecretAsync();

            if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(keySecret))
            {
                return BadRequest(ApiResponse.FailureResult("Razorpay Key ID and Key Secret must be configured"));
            }

            var client = _httpClientFactory.CreateClient();
            var authString = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{keyId}:{keySecret}"));
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authString);

            // Make a call to Razorpay orders list endpoint to verify credentials
            var response = await client.GetAsync("https://api.razorpay.com/v1/orders?count=1");
            if (response.IsSuccessStatusCode)
            {
                return Ok(ApiResponse.SuccessResult("Razorpay credentials are valid and active!"));
            }
            else
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                return BadRequest(ApiResponse.FailureResult($"Razorpay validation failed (HTTP {(int)response.StatusCode}): {errorBody}"));
            }
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse.FailureResult($"Razorpay connection test failed: {ex.Message}"));
        }
    }
}

public class SystemSettingsDto
{
    public string? WhatsAppAccessToken { get; set; }
    public string? WhatsAppPhoneNumberId { get; set; }
    public string? WhatsAppApiVersion { get; set; }
    public string? RazorpayKeyId { get; set; }
    public string? RazorpayKeySecret { get; set; }
    public string? RazorpayWebhookSecret { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; }
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }
    public bool SmtpEnableSsl { get; set; }
    public string? SmtpFromEmail { get; set; }
}

public class TestWhatsAppDto
{
    public string PhoneNumber { get; set; } = string.Empty;
}

public class TestEmailDto
{
    public string Email { get; set; } = string.Empty;
}
