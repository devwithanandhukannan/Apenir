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
            var userExists = await _context.Users.AnyAsync(u => u.Email != null && u.Email.ToLower() == lowercaseEmail && !u.IsDeleted, cancellationToken);
            if (userExists)
            {
                return BadRequest(ApiResponse.FailureResult("A user with this email already exists."));
            }

            // Create User and Branch shells in 'invited' status if they don't exist
            var userId = Guid.NewGuid().ToString();
            var branchId = Guid.NewGuid().ToString();
            var adminId = _currentUserService.UserId?.ToString() ?? "system";

            var user = new User
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

            var branch = new Branch
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
            var requestScheme = Request.Scheme;
            var requestHost = Request.Host;
            var verifyUrl = $"{requestScheme}://{requestHost}/api/lab-invitation/verify?token={token}";

            var emailSubject = $"Welcome to Apenir - Complete Registration for {request.LabName}";
            var emailBody = $@"
                <div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px; border: 1px solid #e2e8f0; border-radius: 8px;'>
                    <h2 style='color: #4f46e5;'>Apenir Lab Partner Invitation</h2>
                    <p>Hello,</p>
                    <p>You have been invited to join the Apenir Medical Lab Booking platform as a lab partner for <strong>{request.LabName}</strong>.</p>
                    <p>Please click the button below to complete your registration, set up your password, and enter your branch location details:</p>
                    <div style='text-align: center; margin: 30px 0;'>
                        <a href='{verifyUrl}' style='background-color: #4f46e5; color: white; padding: 12px 24px; text-decoration: none; border-radius: 6px; font-weight: bold; display: inline-block;'>Complete Registration</a>
                    </div>
                    <p style='color: #ef4444; font-weight: bold;'>Important: This invitation link is only valid for 15 minutes.</p>
                    <p>If you did not request this invitation, please ignore this email or contact our support team.</p>
                    <hr style='border: 0; border-top: 1px solid #cbd5e1; margin: 20px 0;' />
                    <p style='font-size: 12px; color: #64748b;'>This is an automated email. Please do not reply directly.</p>
                </div>";

            await _emailService.SendEmailAsync(request.Email.Trim(), emailSubject, emailBody);

            return Ok(ApiResponse.SuccessResult("Lab invited and email sent successfully."));
        }

        [HttpGet("verify")]
        [AllowAnonymous]
        [Produces("text/html")]
        public async Task<IActionResult> VerifyInvite([FromQuery] string token, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return Content(GetExpiredHtml(), "text/html");
            }

            var invite = await _context.BranchInvites.FirstOrDefaultAsync(i => i.Token == token, cancellationToken);
            if (invite == null || invite.IsUsed || invite.ExpiresAt < DateTime.UtcNow)
            {
                return Content(GetExpiredHtml(), "text/html");
            }

            return Content(GetRegistrationFormHtml(token, invite.LabName), "text/html");
        }

        [HttpPost("verify")]
        [AllowAnonymous]
        [Consumes("application/x-www-form-urlencoded")]
        [Produces("text/html")]
        public async Task<IActionResult> CompleteRegistration([FromForm] CompleteRegistrationRequest request, CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Token))
            {
                return Content(GetExpiredHtml(), "text/html");
            }

            var invite = await _context.BranchInvites.FirstOrDefaultAsync(i => i.Token == request.Token, cancellationToken);
            if (invite == null || invite.IsUsed || invite.ExpiresAt < DateTime.UtcNow)
            {
                return Content(GetExpiredHtml(), "text/html");
            }

            // Find User
            var lowercaseEmail = invite.Email.ToLower();
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == lowercaseEmail && !u.IsDeleted, cancellationToken);
            if (user == null)
            {
                return Content("<html><body><h1>Error</h1><p>User shell not found.</p></body></html>", "text/html");
            }

            // Find Branch
            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.LabUserId == user.Id, cancellationToken);
            if (branch == null)
            {
                return Content("<html><body><h1>Error</h1><p>Branch shell not found.</p></body></html>", "text/html");
            }

            // Update User
            user.PasswordHash = _passwordHasher.Hash(request.Password);
            user.Phone = request.Phone;
            user.Status = "Active";
            user.IsActive = true;
            user.UpdatedAt = DateTime.UtcNow;

            // Update Branch
            branch.Phone = request.Phone;
            branch.City = request.City;
            branch.District = request.District;
            branch.Pincode = request.Pincode;
            branch.Latitude = request.Latitude;
            branch.Longitude = request.Longitude;
            branch.Status = "Active";
            branch.IsActive = true;

            // Mark invitation as used
            invite.IsUsed = true;

            _context.Users.Update(user);
            _context.Branches.Update(branch);
            _context.BranchInvites.Update(invite);

            await _context.SaveChangesAsync(cancellationToken);

            return Content(GetSuccessHtml(), "text/html");
        }

        private string GetExpiredHtml()
        {
            return @"
<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>Apenir - Invitation Expired</title>
    <link href='https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;600;800&display=swap' rel='stylesheet'>
    <style>
        body {
            font-family: 'Outfit', sans-serif;
            background: linear-gradient(135deg, #0f172a 0%, #1e1b4b 100%);
            color: #f8fafc;
            min-height: 100vh;
            display: flex;
            justify-content: center;
            align-items: center;
            padding: 20px;
        }
        .card {
            background: rgba(30, 41, 59, 0.7);
            backdrop-filter: blur(16px);
            border: 1px solid rgba(255, 255, 255, 0.1);
            border-radius: 24px;
            padding: 45px 30px;
            text-align: center;
            width: 100%;
            max-width: 450px;
            box-shadow: 0 20px 40px rgba(0, 0, 0, 0.3);
        }
        .icon {
            font-size: 64px;
            color: #ef4444;
            margin-bottom: 20px;
        }
        h1 {
            font-size: 26px;
            font-weight: 800;
            margin-bottom: 12px;
            color: #f8fafc;
        }
        p {
            color: #94a3b8;
            font-size: 15px;
            line-height: 1.6;
            margin-bottom: 25px;
        }
        .contact {
            font-size: 13px;
            color: #6366f1;
            font-weight: 600;
            text-transform: uppercase;
            letter-spacing: 1px;
        }
    </style>
</head>
<body>
    <div class='card'>
        <div class='icon'>⚠️</div>
        <h1>Invitation Link Expired</h1>
        <p>The invitation session has timed out (15-minute limit exceeded) or this link has already been used.</p>
        <div class='contact'>Please contact your administrator</div>
    </div>
</body>
</html>";
        }

        private string GetRegistrationFormHtml(string token, string labName)
        {
            return $@"
<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>Apenir - Complete Lab Registration</title>
    <link href='https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;600;800&display=swap' rel='stylesheet'>
    <style>
        * {{ box-sizing: border-box; margin: 0; padding: 0; }}
        body {{
            font-family: 'Outfit', sans-serif;
            background: linear-gradient(135deg, #0f172a 0%, #1e1b4b 100%);
            color: #f8fafc;
            min-height: 100vh;
            display: flex;
            justify-content: center;
            align-items: center;
            padding: 20px;
        }}
        .card {{
            background: rgba(30, 41, 59, 0.7);
            backdrop-filter: blur(16px);
            -webkit-backdrop-filter: blur(16px);
            border: 1px solid rgba(255, 255, 255, 0.1);
            border-radius: 24px;
            padding: 40px;
            width: 100%;
            max-width: 500px;
            box-shadow: 0 20px 40px rgba(0, 0, 0, 0.3);
        }}
        h1 {{
            font-size: 28px;
            font-weight: 800;
            margin-bottom: 8px;
            background: linear-gradient(to right, #38bdf8, #818cf8);
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
        }}
        p.subtitle {{
            color: #94a3b8;
            font-size: 14px;
            margin-bottom: 30px;
        }}
        .form-group {{
            margin-bottom: 20px;
        }}
        label {{
            display: block;
            font-size: 13px;
            font-weight: 600;
            margin-bottom: 6px;
            color: #cbd5e1;
            text-transform: uppercase;
            letter-spacing: 0.5px;
        }}
        input {{
            width: 100%;
            padding: 12px 16px;
            border-radius: 12px;
            border: 1px solid rgba(255, 255, 255, 0.1);
            background: rgba(15, 23, 42, 0.6);
            color: #fff;
            font-size: 15px;
            transition: all 0.3s ease;
        }}
        input:focus {{
            outline: none;
            border-color: #6366f1;
            box-shadow: 0 0 0 3px rgba(99, 102, 241, 0.2);
        }}
        .btn {{
            display: block;
            width: 100%;
            padding: 14px;
            background: linear-gradient(135deg, #6366f1 0%, #4f46e5 100%);
            color: white;
            border: none;
            border-radius: 12px;
            font-size: 16px;
            font-weight: 600;
            cursor: pointer;
            transition: all 0.3s ease;
            box-shadow: 0 4px 12px rgba(99, 102, 241, 0.3);
            margin-top: 10px;
        }}
        .btn:hover {{
            transform: translateY(-2px);
            box-shadow: 0 6px 20px rgba(99, 102, 241, 0.4);
        }}
        .btn:active {{
            transform: translateY(0);
        }}
    </style>
</head>
<body>
    <div class='card'>
        <h1>Complete Registration</h1>
        <p class='subtitle'>Activate your partner account for <strong>{labName}</strong>.</p>
        <form method='POST' action='/api/lab-invitation/verify' onsubmit='return validateForm()'>
            <input type='hidden' name='token' value='{token}'>
            
            <div class='form-group'>
                <label for='password'>Create Password</label>
                <input type='password' id='password' name='password' required placeholder='Min 8 characters'>
            </div>

            <div class='form-group'>
                <label for='phone'>Contact Phone Number</label>
                <input type='text' id='phone' name='phone' required placeholder='+919876543210'>
            </div>

            <div style='display: grid; grid-template-columns: 1fr 1fr; gap: 16px;'>
                <div class='form-group'>
                    <label for='city'>City</label>
                    <input type='text' id='city' name='city' required placeholder='Kochi'>
                </div>
                <div class='form-group'>
                    <label for='district'>District</label>
                    <input type='text' id='district' name='district' required placeholder='Ernakulam'>
                </div>
            </div>

            <div class='form-group'>
                <label for='pincode'>Pincode</label>
                <input type='text' id='pincode' name='pincode' required placeholder='682016'>
            </div>

            <div style='display: grid; grid-template-columns: 1fr 1fr; gap: 16px;'>
                <div class='form-group'>
                    <label for='latitude'>Latitude</label>
                    <input type='number' step='any' id='latitude' name='latitude' required placeholder='9.9788'>
                </div>
                <div class='form-group'>
                    <label for='longitude'>Longitude</label>
                    <input type='number' step='any' id='longitude' name='longitude' required placeholder='76.2798'>
                </div>
            </div>

            <button type='submit' class='btn'>Activate Account</button>
        </form>
    </div>
    <script>
        function validateForm() {{
            var pass = document.getElementById('password').value;
            if (pass.length < 8) {{
                alert('Password must be at least 8 characters long.');
                return false;
            }}
            return true;
        }}
    </script>
</body>
</html>";
        }

        private string GetSuccessHtml()
        {
            return @"
<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>Apenir - Activation Successful</title>
    <link href='https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;600;800&display=swap' rel='stylesheet'>
    <style>
        body {
            font-family: 'Outfit', sans-serif;
            background: linear-gradient(135deg, #0f172a 0%, #1e1b4b 100%);
            color: #f8fafc;
            min-height: 100vh;
            display: flex;
            justify-content: center;
            align-items: center;
            padding: 20px;
        }
        .card {
            background: rgba(30, 41, 59, 0.7);
            backdrop-filter: blur(16px);
            border: 1px solid rgba(255, 255, 255, 0.1);
            border-radius: 24px;
            padding: 45px 30px;
            text-align: center;
            width: 100%;
            max-width: 450px;
            box-shadow: 0 20px 40px rgba(0, 0, 0, 0.3);
        }
        .icon {
            font-size: 64px;
            color: #10b981;
            margin-bottom: 20px;
        }
        h1 {
            font-size: 26px;
            font-weight: 800;
            margin-bottom: 12px;
            color: #f8fafc;
        }
        p {
            color: #94a3b8;
            font-size: 15px;
            line-height: 1.6;
            margin-bottom: 25px;
        }
        .success-text {
            font-size: 13px;
            color: #10b981;
            font-weight: 600;
            text-transform: uppercase;
            letter-spacing: 1px;
        }
    </style>
</head>
<body>
    <div class='card'>
        <div class='icon'>✓</div>
        <h1>Account Activated</h1>
        <p>Your lab branch and user accounts are now active. You may now log in using your email and password.</p>
        <div class='success-text'>Registration Complete</div>
    </div>
</body>
</html>";
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
    }
}
