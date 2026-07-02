using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Apenir.Infrastructure.Data;
using Apenir.Core.Entities;
using System.Linq;

class Program {
    static async Task Main() {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options =>
            options.UseMongoDB("mongodb+srv://workbridgeanandhu:P%40ssword1@cluster0.dseaj.mongodb.net/TaskManagerDb?appName=Cluster0", "Apenir"));
        var provider = services.BuildServiceProvider();
        var context = provider.GetRequiredService<AppDbContext>();

        var labId = "branch_user_lab_001";
        
        try {
            var slots = await context.AppointmentSlots
                .Where(s => s.BranchId == labId && s.IsAvailable && s.SlotDate >= DateOnly.FromDateTime(DateTime.UtcNow))
                .OrderBy(s => s.SlotDate).ThenBy(s => s.StartTime)
                .Take(10)
                .ToListAsync();
            Console.WriteLine($"Found {slots.Count} slots using EF Core Query.");
        } catch (Exception ex) {
            Console.WriteLine("CRASH IN QUERY: " + ex.Message);
        }
    }
}
