using Microsoft.AspNetCore.Mvc;

namespace Apenir.API.Controllers;

[ApiController]
[Route("api/reports/[controller]")]
public class TestController : ControllerBase
{
	[HttpGet]
	public IActionResult Get() => Ok(new { status = "OK" });
}
