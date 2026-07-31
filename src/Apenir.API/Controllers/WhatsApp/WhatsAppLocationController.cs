using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading;
using System.Threading.Tasks;
using Apenir.Core.Interfaces;
using Apenir.Core.Entities;
using Apenir.Core.Enums;

namespace Apenir.API.Controllers.WhatsApp
{
    public class SetSessionLocationRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string AddressText { get; set; } = string.Empty;
        public string? BuildingDetails { get; set; }
        public string? Landmark { get; set; }
    }

    [ApiController]
    [Route("api/whatsapp/location")]
    public class WhatsAppLocationController : ControllerBase
    {
        private readonly IApplicationDbContext _context;

        public WhatsAppLocationController(IApplicationDbContext context)
        {
            _context = context;
        }

        [HttpPost("set-session-location")]
        public async Task<IActionResult> SetSessionLocation([FromBody] SetSessionLocationRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.SessionId))
            {
                return BadRequest(new { success = false, message = "Session ID is required." });
            }

            var session = await _context.WhatsAppSessions
                .FirstOrDefaultAsync(s => s.Id == request.SessionId || s.Phone == request.SessionId, cancellationToken);

            if (session == null)
            {
                return NotFound(new { success = false, message = "Session not found." });
            }

            session.Latitude = request.Latitude;
            session.Longitude = request.Longitude;
            session.BuildingDetails = !string.IsNullOrWhiteSpace(request.BuildingDetails) ? request.BuildingDetails : request.AddressText;
            session.Landmark = request.Landmark;
            session.LocationShared = true;
            session.CurrentState = WhatsAppState.ChoosingLab;
            session.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);

            return Ok(new { success = true, message = "Location updated successfully for WhatsApp session." });
        }
    }
}
