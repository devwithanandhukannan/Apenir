using System;
using System.Collections.Generic;
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

namespace Apenir.API.Controllers
{
    [ApiController]
    [Route("api/lab-invitation")]
    public class LabInvitationController : ControllerBase
    {
        private readonly IApplicationDbContext _context;
        private readonly IEmailService _emailService;
        private readonly IPasswordHasher _passwordHasher;
        private readonly ICurrentUserService _currentUserService;

        public LabInvitationController(
            IApplicationDbContext context, 
            IEmailService emailService, 
            IPasswordHasher passwordHasher,
            ICurrentUserService currentUserService)
        {
            _context = context;
            _emailService = emailService;
            _passwordHasher = passwordHasher;
            _currentUserService = currentUserService;
        }

        [HttpPost("invite")]
        [Authorize]
        [AdminOnly]
        [EndpointSummary("Trigger Lab Invitation Email")]
        [EndpointDescription("Registers a pending lab invite, creates database shells, and triggers an SMTP verification email.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> InviteLab([FromBody] InviteLabRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                return BadRequest(ApiResponse.FailureResult("Invalid request payload."));
            }

            if (string.IsNullOrWhiteSpace(request.Email))
            {
                return BadRequest(ApiResponse.FailureResult("Email is required."));
            }

            if (string.IsNullOrWhiteSpace(request.LabName))
            {
                return BadRequest(ApiResponse.FailureResult("Lab Name is required."));
            }

            var lowercaseEmail = request.Email.Trim().ToLower();

            // Check if user already exists
            var existingUser = await _context.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == lowercaseEmail && !u.IsDeleted, cancellationToken);
            
            User user;
            Branch branch;

            if (existingUser != null)
            {
                if (existingUser.Status != "invited")
                {
                    return BadRequest(ApiResponse.FailureResult("A user with this email already exists."));
                }
                
                user = existingUser;
                user.Name = request.LabName;
                user.CreatedAt = DateTime.UtcNow;
                _context.Users.Update(user);

                var existingBranch = await _context.Branches.FirstOrDefaultAsync(b => b.LabUserId == user.Id, cancellationToken);
                if (existingBranch != null)
                {
                    branch = existingBranch;
                    branch.Name = request.LabName;
                    branch.CreatedAt = DateTime.UtcNow;
                    _context.Branches.Update(branch);
                }
                else
                {
                    branch = new Branch
                    {
                        Id = Guid.NewGuid().ToString(),
                        LabUserId = user.Id,
                        Name = request.LabName,
                        District = "Pending",
                        City = "Pending",
                        Pincode = "Pending",
                        Phone = "Pending",
                        Latitude = 0m,
                        Longitude = 0m,
                        IsActive = true,
                        Status = "invited",
                        CreatedBy = _currentUserService.UserId?.ToString() ?? "system",
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.Branches.Add(branch);
                }
            }
            else
            {
                var userId = Guid.NewGuid().ToString();
                var branchId = Guid.NewGuid().ToString();
                var adminId = _currentUserService.UserId?.ToString() ?? "system";

                user = new User
                {
                    Id = userId,
                    Name = request.LabName,
                    Email = request.Email.Trim(),
                    Role = UserRole.Lab,
                    IsActive = true,
                    IsDeleted = false,
                    Status = "invited",
                    CreatedAt = DateTime.UtcNow,
                    Permissions = new List<string>()
                };

                branch = new Branch
                {
                    Id = branchId,
                    LabUserId = userId,
                    Name = request.LabName,
                    District = "Pending",
                    City = "Pending",
                    Pincode = "Pending",
                    Phone = "Pending",
                    Latitude = 0m,
                    Longitude = 0m,
                    IsActive = true,
                    Status = "invited",
                    CreatedBy = adminId,
                    CreatedAt = DateTime.UtcNow
                };

                _context.Users.Add(user);
                _context.Branches.Add(branch);
            }

            // Generate secure unique token
            var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            var invite = new BranchInvite
            {
                Id = Guid.NewGuid().ToString(),
                Email = request.Email.Trim(),
                LabName = request.LabName,
                Token = token,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(15),
                IsUsed = false
            };

            _context.BranchInvites.Add(invite);
            await _context.SaveChangesAsync(cancellationToken);

            // Trigger Email transmission
            var config = HttpContext.RequestServices.GetService(typeof(Microsoft.Extensions.Configuration.IConfiguration)) as Microsoft.Extensions.Configuration.IConfiguration;
            var frontendUrl = config?["FrontendUrl"] ?? "https://admin.anandhu-kannan.in";
            var verifyUrl = $"{frontendUrl.TrimEnd('/')}/register?token={token}";

            var emailSubject = $"Welcome to Apenir - Complete Registration for {request.LabName}";
            var emailBody = $@"
<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>Apenir Lab Partner Invitation</title>
</head>
<body style='margin: 0; padding: 0; background-color: #f9fafb; font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, Helvetica, Arial, sans-serif; -webkit-font-smoothing: antialiased;'>
    <table cellpadding='0' cellspacing='0' width='100%' style='background-color: #f9fafb; min-height: 100vh; padding: 60px 20px;'>
        <tr>
            <td align='center' valign='top'>
                <table cellpadding='0' cellspacing='0' width='100%' style='max-width: 500px; background-color: #ffffff; border: 1px solid #e5e7eb; border-radius: 16px; padding: 40px; box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.05); text-align: left;'>
                    <tr>
                        <td align='left' style='padding-bottom: 32px;'>
                            <span style='font-size: 20px; font-weight: 700; color: #111827; letter-spacing: -0.5px;'>Apenir Lab Partner</span>
                        </td>
                    </tr>
                    <tr>
                        <td style='color: #111827; font-size: 16px; font-weight: 600; padding-bottom: 12px;'>
                            Hello Partner,
                        </td>
                    </tr>
                    <tr>
                        <td style='color: #4b5563; font-size: 14px; line-height: 1.6; padding-bottom: 32px;'>
                            You have been invited to join the Apenir Medical Lab Booking platform as a lab partner for <strong>{request.LabName}</strong>.
                            <br/><br/>
                            Please click the button below to complete your registration, set up your password, and enter your branch location details:
                        </td>
                    </tr>
                    <tr>
                        <td align='left' style='padding-bottom: 32px;'>
                            <a href='{verifyUrl}' style='display: inline-block; background-color: #111827; color: #ffffff; text-decoration: none; padding: 12px 24px; font-size: 14px; font-weight: 500; border-radius: 8px;'>Complete Registration</a>
                        </td>
                    </tr>
                    <tr>
                        <td style='color: #9ca3af; font-size: 12px; line-height: 1.5; padding-bottom: 24px; border-top: 1px solid #f3f4f6; padding-top: 24px;'>
                            If you're having trouble clicking the button, copy and paste the URL below into your browser:
                            <br/>
                            <a href='{verifyUrl}' style='color: #2563eb; text-decoration: none; word-break: break-all;'>{verifyUrl}</a>
                        </td>
                    </tr>
                    <tr>
                        <td style='color: #ef4444; font-size: 12px; font-weight: 500; padding-bottom: 16px;'>
                            ⏱️ This link is only valid for 15 minutes.
                        </td>
                    </tr>
                    <tr>
                        <td style='color: #9ca3af; font-size: 11px; line-height: 1.4;'>
                            If you did not request this invitation, you can safely ignore this email.
                        </td>
                    </tr>
                </table>
            </td>
        </tr>
    </table>
</body>
</html>";

            await _emailService.SendEmailAsync(request.Email.Trim(), emailSubject, emailBody);

            return Ok(ApiResponse.SuccessResult("Lab invited and email sent successfully."));
        }

        [HttpGet("verify")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<object>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        public async Task<IActionResult> VerifyInvite([FromQuery] string token, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return BadRequest(ApiResponse.FailureResult("Token is required."));
            }

            var invite = await _context.BranchInvites.FirstOrDefaultAsync(i => i.Token == token, cancellationToken);
            if (invite == null || invite.IsUsed || invite.ExpiresAt < DateTime.UtcNow)
            {
                return BadRequest(ApiResponse.FailureResult("Invitation link is expired or already used."));
            }

            return Ok(ApiResponse<object>.SuccessResult(new { email = invite.Email, labName = invite.LabName }));
        }

        [HttpPost("verify")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        public async Task<IActionResult> CompleteRegistration([FromBody] CompleteRegistrationRequest request, CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Token))
            {
                return BadRequest(ApiResponse.FailureResult("Invalid registration payload."));
            }

            var invite = await _context.BranchInvites.FirstOrDefaultAsync(i => i.Token == request.Token, cancellationToken);
            if (invite == null || invite.IsUsed || invite.ExpiresAt < DateTime.UtcNow)
            {
                return BadRequest(ApiResponse.FailureResult("Invitation link is expired or already used."));
            }

            // Find User
            var lowercaseEmail = invite.Email.ToLower();
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == lowercaseEmail && !u.IsDeleted, cancellationToken);
            if (user == null)
            {
                return BadRequest(ApiResponse.FailureResult("User shell not found."));
            }

            // Find Branch
            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.LabUserId == user.Id, cancellationToken);
            if (branch == null)
            {
                return BadRequest(ApiResponse.FailureResult("Branch shell not found."));
            }

            // Update User
            user.PasswordHash = _passwordHasher.Hash(request.Password);
            user.Phone = string.IsNullOrWhiteSpace(request.Phone) ? "Pending" : request.Phone.Trim();
            user.Status = "Active";
            user.IsActive = true;
            user.UpdatedAt = DateTime.UtcNow;

            // Generate unique 6-digit LabId if not already set
            if (string.IsNullOrWhiteSpace(user.LabId))
            {
                var random = new Random();
                string generatedLabId;
                bool isUnique;
                do
                {
                    generatedLabId = random.Next(100000, 1000000).ToString();
                    isUnique = !await _context.Users.AnyAsync(u => u.LabId == generatedLabId, cancellationToken) &&
                               !await _context.Branches.AnyAsync(b => b.LabId == generatedLabId, cancellationToken);
                } while (!isUnique);

                user.LabId = generatedLabId;
                branch.LabId = generatedLabId;
            }
            else
            {
                branch.LabId = user.LabId;
            }

            // Update Branch
            branch.Phone = string.IsNullOrWhiteSpace(request.Phone) ? "Pending" : request.Phone.Trim();
            branch.City = string.IsNullOrWhiteSpace(request.City) ? "Pending" : request.City.Trim();
            branch.District = string.IsNullOrWhiteSpace(request.District) ? "Pending" : request.District.Trim();
            branch.Pincode = string.IsNullOrWhiteSpace(request.Pincode) ? "Pending" : request.Pincode.Trim();
            branch.Latitude = request.Latitude;
            branch.Longitude = request.Longitude;
            branch.Status = "Active";
            branch.IsActive = true;

            // Seed initial slot configurations if provided
            if (request.Slots != null && request.Slots.Count > 0)
            {
                foreach (var s in request.Slots)
                {
                    if (TimeOnly.TryParse(s.StartTime, out var startTime) && TimeOnly.TryParse(s.EndTime, out var endTime))
                    {
                        var slotConfig = new BranchSlotConfiguration
                        {
                            Id = Guid.NewGuid().ToString(),
                            BranchId = branch.Id,
                            DayText = s.DayText,
                            StartTime = startTime,
                            EndTime = endTime,
                            MaxCapacity = s.MaxCapacity,
                            IsLeave = false
                        };
                        _context.BranchSlotConfigurations.Add(slotConfig);
                    }
                }
            }

            // Mark invitation as used
            invite.IsUsed = true;

            _context.Users.Update(user);
            _context.Branches.Update(branch);
            _context.BranchInvites.Update(invite);

            await _context.SaveChangesAsync(cancellationToken);

            return Ok(ApiResponse.SuccessResult("Lab account registration completed successfully."));
        }
    }

    public record InviteLabRequest(
        string Email,
        string LabName
    );

    public class CompleteRegistrationRequest
    {
        public string Token { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string District { get; set; } = string.Empty;
        public string Pincode { get; set; } = string.Empty;
        public decimal Latitude { get; set; }
        public decimal Longitude { get; set; }
        public List<SlotConfigInput>? Slots { get; set; }
    }

    public class SlotConfigInput
    {
        public DayText DayText { get; set; }
        public string StartTime { get; set; } = string.Empty;
        public string EndTime { get; set; } = string.Empty;
        public int MaxCapacity { get; set; } = 1;
    }
}
