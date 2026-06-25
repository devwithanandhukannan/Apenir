using Microsoft.AspNetCore.Mvc;

namespace Apenir.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class HealthController : ControllerBase
    {
        [HttpGet]
        public IActionResult GetStudents()
        {
            var students = new[]
            {
                new { Id = 1, Name = "Alice", Age = 18, Sex = "Female" },
                new { Id = 2, Name = "Ben", Age = 19, Sex = "Male" },
                new { Id = 3, Name = "Carla", Age = 17, Sex = "Female" },
                new { Id = 4, Name = "David", Age = 20, Sex = "Male" },
                new { Id = 5, Name = "Ella", Age = 18, Sex = "Female" },
                new { Id = 6, Name = "Finn", Age = 19, Sex = "Male" },
                new { Id = 7, Name = "Grace", Age = 17, Sex = "Female" },
                new { Id = 8, Name = "Henry", Age = 20, Sex = "Male" },
                new { Id = 9, Name = "Ivy", Age = 18, Sex = "Female" },
                new { Id = 10, Name = "Jake", Age = 19, Sex = "Male" }
            };

            return Ok(new { Message = "Student list returned successfully", Students = students });
        }
    }
}
