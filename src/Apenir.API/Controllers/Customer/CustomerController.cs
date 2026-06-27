using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Apenir.Core.Interfaces;
using Apenir.Core.Entities;
using Apenir.API.Filters;
using System.Security.Claims;
using System.Threading.Tasks;
using System;

namespace Apenir.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[CustomerOnly]
public class CustomerController : ControllerBase
{
    private readonly IApplicationDbContext _context;

    public CustomerController(IApplicationDbContext context)
    {
        _context = context;
    }

    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile()
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(new { error = "USER_NOT_AUTHENTICATED" });
        }

        var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserId == userId);
        if (customer == null)
        {
            return NotFound(new { error = "CUSTOMER_PROFILE_NOT_FOUND" });
        }

        return Ok(new
        {
            id = customer.Id,
            name = customer.Name,
            phone = customer.Phone,
            gender = customer.Gender,
            dob = customer.Dob,
            district = customer.District,
            address = customer.Address
        });
    }

    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(new { error = "USER_NOT_AUTHENTICATED" });
        }

        var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserId == userId);
        if (customer == null)
        {
            return NotFound(new { error = "CUSTOMER_PROFILE_NOT_FOUND" });
        }

        customer.Name = request.Name ?? customer.Name;
        customer.Gender = request.Gender;
        customer.Dob = request.Dob;
        customer.Address = request.Address;
        customer.District = request.District;

        _context.Customers.Update(customer);
        await _context.SaveChangesAsync();

        return Ok(new { message = "PROFILE_UPDATED" });
    }
}

public record UpdateProfileRequest(
    string? Name,
    string? Gender,
    string? Dob,
    string? Address,
    string? District
);
