using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Apenir.Core.Entities;
using Apenir.Core.Enums;
using Apenir.Core.Interfaces;
using Apenir.Application.Common.Interfaces;
using Apenir.Application.Common.Models;

namespace Apenir.API.Controllers;

[ApiController]
[Route("api/appointments")]
[Authorize]
public class BookingController : ControllerBase
{
    private readonly IApplicationDbContext _context;
    private readonly ICurrentUserService _currentUserService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWhatsAppService _whatsAppService;
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;
    private readonly ISettingsService _settingsService;

    public BookingController(
        IApplicationDbContext context,
        ICurrentUserService currentUserService,
        IHttpClientFactory httpClientFactory,
        IWhatsAppService whatsAppService,
        IConfiguration configuration,
        IMemoryCache cache,
        ISettingsService settingsService)
    {
        _context = context;
        _currentUserService = currentUserService;
        _httpClientFactory = httpClientFactory;
        _whatsAppService = whatsAppService;
        _configuration = configuration;
        _cache = cache;
        _settingsService = settingsService;
    }

    [HttpGet]
    [EndpointSummary("Get authenticated customer's appointments")]
    public async Task<IActionResult> GetCustomerAppointments(CancellationToken cancellationToken)
    {
        var currentUserId = _currentUserService.UserId?.ToString();
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized(ApiResponse<List<Appointment>>.FailureResult("User not authenticated."));
        }

        var appointments = await _context.Appointments
            .Where(a => a.CustomerUserId == currentUserId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

        var branchIds = appointments.Select(a => a.BranchId).Distinct().ToList();
        var branches = await _context.Branches
            .Where(b => branchIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, cancellationToken);

        var slotIds = appointments.Select(a => a.AppointmentSlotId).Distinct().ToList();
        var slots = await _context.AppointmentSlots
            .Where(s => slotIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, cancellationToken);

        foreach (var appt in appointments)
        {
            if (branches.TryGetValue(appt.BranchId, out var branch))
            {
                appt.Branch = branch;
            }
            if (slots.TryGetValue(appt.AppointmentSlotId, out var slot))
            {
                appt.AppointmentSlot = slot;
            }
        }

        return Ok(ApiResponse<List<Appointment>>.SuccessResult(appointments, "APPOINTMENTS_RETRIEVED"));
    }

    [HttpGet("slots")]
    [AllowAnonymous]
    [EndpointSummary("Get available slots for a service near coordinates")]
    public async Task<IActionResult> GetNearbySlots(
        [FromQuery] double latitude,
        [FromQuery] double longitude,
        [FromQuery] string serviceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(serviceId))
        {
            return BadRequest(ApiResponse.FailureResult("ServiceId is required."));
        }

        // 1. Fetch active branches
        var branches = await _context.Branches
            .Where(b => b.IsActive)
            .ToListAsync(cancellationToken);

        var nearbyBranchIds = new List<string>();

        // Find branches within service range (straight-line Haversine threshold check)
        foreach (var branch in branches)
        {
            var distance = CalculateDistanceKm(latitude, longitude, (double)branch.Latitude, (double)branch.Longitude);
            if (distance <= branch.ServiceRangeKm)
            {
                nearbyBranchIds.Add(branch.Id);
            }
        }

        if (!nearbyBranchIds.Any())
        {
            return Ok(ApiResponse<List<AppointmentSlot>>.SuccessResult(new List<AppointmentSlot>(), "No branches within service range."));
        }

        // 2. Filter branches that offer the requested service
        var branchServices = await _context.BranchServices
            .Where(bs => bs.ServiceId == serviceId && bs.IsActive && nearbyBranchIds.Contains(bs.BranchId))
            .Select(bs => bs.BranchId)
            .ToListAsync(cancellationToken);

        if (!branchServices.Any())
        {
            return Ok(ApiResponse<List<AppointmentSlot>>.SuccessResult(new List<AppointmentSlot>(), "No branches offer this service near you."));
        }

        // 3. Fetch slots for those branches
        var indianTime = DateTime.UtcNow.AddHours(5).AddMinutes(30);
        var today = DateOnly.FromDateTime(indianTime);
        var currentTime = TimeOnly.FromDateTime(indianTime);

        var slots = await _context.AppointmentSlots
            .Where(s => branchServices.Contains(s.BranchId) && s.IsAvailable && s.SlotDate >= today)
            .OrderBy(s => s.SlotDate)
            .ThenBy(s => s.StartTime)
            .ToListAsync(cancellationToken);

        // Filter out past slots for today
        slots = slots.Where(s => s.SlotDate > today || s.StartTime > currentTime).ToList();

        return Ok(ApiResponse<List<AppointmentSlot>>.SuccessResult(slots, "Available slots retrieved successfully."));
    }

    [HttpGet("region-availability")]
    [AllowAnonymous]
    [EndpointSummary("Real-time check: which labs are available near a location for given items")]
    public async Task<IActionResult> GetRegionAvailability(
        [FromQuery] double latitude,
        [FromQuery] double longitude,
        [FromQuery] string? itemIds,
        CancellationToken cancellationToken)
    {
        // Cache key based on ~500m grid cell (2 decimal places) + item set
        var lat2 = Math.Round(latitude, 2);
        var lng2 = Math.Round(longitude, 2);
        var itemKey = string.IsNullOrEmpty(itemIds) ? "all" : string.Join(",", itemIds.Split(',').OrderBy(x => x));
        var cacheKey = $"region_avail_{lat2}_{lng2}_{itemKey}";

        if (_cache.TryGetValue(cacheKey, out List<RegionAvailabilityResult>? cached) && cached != null)
            return Ok(ApiResponse<List<RegionAvailabilityResult>>.SuccessResult(cached, "region_availability_cached"));

        var indianTime = DateTime.UtcNow.AddHours(5).AddMinutes(30);
        var today = DateOnly.FromDateTime(indianTime);
        var currentTime = TimeOnly.FromDateTime(indianTime);
        var allBranches = await _context.Branches.Where(b => b.IsActive).ToListAsync(cancellationToken);

        var ids = string.IsNullOrEmpty(itemIds)
            ? new List<string>()
            : itemIds.Split(',').Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();

        // Fetch branch services/packages relevant to requested items (or all if no items)
        var allBranchServices = await _context.BranchServices
            .Where(bs => bs.IsActive && (ids.Count == 0 || ids.Contains(bs.ServiceId)))
            .ToListAsync(cancellationToken);

        var allBranchPackages = await _context.BranchPackages
            .Where(bp => bp.IsActive && (ids.Count == 0 || ids.Contains(bp.PackageId)))
            .ToListAsync(cancellationToken);

        // Fetch slots for today across all active branches
        var todaySlots = await _context.AppointmentSlots
            .Where(s => s.IsAvailable && s.SlotDate == today)
            .ToListAsync(cancellationToken);
        // Filter out past slots for today
        todaySlots = todaySlots.Where(s => s.StartTime > currentTime).ToList();

        // Fetch all future slots for next-available calculation
        var futureSlots = await _context.AppointmentSlots
            .Where(s => s.IsAvailable && s.SlotDate > today)
            .OrderBy(s => s.SlotDate).ThenBy(s => s.StartTime)
            .ToListAsync(cancellationToken);

        var results = new List<RegionAvailabilityResult>();

        foreach (var branch in allBranches)
        {
            var distance = CalculateDistanceKm(latitude, longitude, (double)branch.Latitude, (double)branch.Longitude);
            if (distance > branch.ServiceRangeKm) continue;

            var branchSvcIds = allBranchServices.Where(bs => bs.BranchId == branch.Id).Select(bs => bs.ServiceId).ToHashSet();
            var branchPkgIds = allBranchPackages.Where(bp => bp.BranchId == branch.Id).Select(bp => bp.PackageId).ToHashSet();

            // Count how many requested items this branch covers
            int coveredCount = ids.Count == 0
                ? branchSvcIds.Count + branchPkgIds.Count
                : ids.Count(id => branchSvcIds.Contains(id) || branchPkgIds.Contains(id));

            bool isFullyEligible = ids.Count == 0 || coveredCount >= ids.Count;

            // Today's available capacity
            var todayBranchSlots = todaySlots.Where(s => s.BranchId == branch.Id).ToList();
            bool hasSlotsToday = todayBranchSlots.Any(s => s.BookedCount < s.MaxCapacity);

            // Next available slot after today
            var nextSlot = futureSlots.FirstOrDefault(s => s.BranchId == branch.Id && s.BookedCount < s.MaxCapacity);

            results.Add(new RegionAvailabilityResult
            {
                BranchId = branch.Id,
                Name = branch.Name,
                City = branch.City,
                District = branch.District,
                Distance = Math.Round(distance, 2),
                Latitude = (double)branch.Latitude,
                Longitude = (double)branch.Longitude,
                ServicesAvailableCount = branchSvcIds.Count + branchPkgIds.Count,
                ServicesRequestedCount = ids.Count,
                ServicesCoveredCount = coveredCount,
                IsFullyEligible = isFullyEligible,
                HasAvailableSlotsToday = hasSlotsToday,
                NextAvailableSlotDate = nextSlot?.SlotDate.ToString("yyyy-MM-dd"),
                NextAvailableSlotTime = nextSlot?.StartTime.ToString("HH:mm"),
            });
        }

        // Sort: fully eligible + has today slots first
        results = results
            .OrderByDescending(r => r.IsFullyEligible)
            .ThenByDescending(r => r.HasAvailableSlotsToday)
            .ThenBy(r => r.Distance)
            .ToList();

        _cache.Set(cacheKey, results, TimeSpan.FromMinutes(3));

        return Ok(ApiResponse<List<RegionAvailabilityResult>>.SuccessResult(results, "region_availability"));
    }

    [HttpGet("location-catalog")]
    [AllowAnonymous]
    [EndpointSummary("Get all diagnostic services/packages and their branch custom pricing within range of a location")]
    public async Task<IActionResult> GetLocationCatalog(
        [FromQuery] double latitude,
        [FromQuery] double longitude,
        CancellationToken cancellationToken)
    {
        var allBranches = await _context.Branches.Where(b => b.IsActive).ToListAsync(cancellationToken);
        var nearbyBranches = new List<Branch>();
        var branchCoverage = new List<RegionAvailabilityResult>();

        var indianTime = DateTime.UtcNow.AddHours(5).AddMinutes(30);
        var today = DateOnly.FromDateTime(indianTime);
        var currentTime = TimeOnly.FromDateTime(indianTime);

        var activeSlots = await _context.AppointmentSlots
            .Where(s => s.IsAvailable && s.SlotDate >= today)
            .OrderBy(s => s.SlotDate).ThenBy(s => s.StartTime)
            .ToListAsync(cancellationToken);

        activeSlots = activeSlots.Where(s => s.SlotDate > today || s.StartTime > currentTime).ToList();

        foreach (var branch in allBranches)
        {
            var distance = CalculateDistanceKm(latitude, longitude, (double)branch.Latitude, (double)branch.Longitude);
            if (distance > branch.ServiceRangeKm) continue;

            nearbyBranches.Add(branch);

            var branchSlots = activeSlots.Where(s => s.BranchId == branch.Id && s.BookedCount < s.MaxCapacity).ToList();
            var hasSlotsToday = branchSlots.Any(s => s.SlotDate == today);
            var nextSlot = branchSlots.FirstOrDefault();

            branchCoverage.Add(new RegionAvailabilityResult
            {
                BranchId = branch.Id,
                Name = branch.Name,
                City = branch.City,
                District = branch.District,
                Distance = Math.Round(distance, 2),
                Latitude = (double)branch.Latitude,
                Longitude = (double)branch.Longitude,
                ServicesAvailableCount = 0,
                ServicesRequestedCount = 0,
                ServicesCoveredCount = 0,
                IsFullyEligible = true,
                HasAvailableSlotsToday = hasSlotsToday,
                NextAvailableSlotDate = nextSlot?.SlotDate.ToString("yyyy-MM-dd"),
                NextAvailableSlotTime = nextSlot?.StartTime.ToString("HH:mm")
            });
        }

        if (!nearbyBranches.Any())
        {
            return Ok(ApiResponse<LocationCatalogResponse>.SuccessResult(new LocationCatalogResponse(), "No labs found near this location."));
        }

        var branchIds = nearbyBranches.Select(b => b.Id).ToList();

        var branchServices = await _context.BranchServices
            .Where(bs => bs.IsActive && branchIds.Contains(bs.BranchId))
            .ToListAsync(cancellationToken);

        var branchPackages = await _context.BranchPackages
            .Where(bp => bp.IsActive && branchIds.Contains(bp.BranchId))
            .ToListAsync(cancellationToken);

        var serviceIds = branchServices.Select(bs => bs.ServiceId).Distinct().ToList();
        var packageIds = branchPackages.Select(bp => bp.PackageId).Distinct().ToList();

        var services = await _context.Services
            .Where(s => s.IsActive && serviceIds.Contains(s.Id))
            .ToListAsync(cancellationToken);

        var packages = await _context.Packages
            .Where(p => p.IsActive && packageIds.Contains(p.Id))
            .ToListAsync(cancellationToken);

        foreach (var coverage in branchCoverage)
        {
            var svcsCount = branchServices.Count(bs => bs.BranchId == coverage.BranchId && services.Any(s => s.Id == bs.ServiceId));
            var pkgsCount = branchPackages.Count(bp => bp.BranchId == coverage.BranchId && packages.Any(p => p.Id == bp.PackageId));
            coverage.ServicesAvailableCount = svcsCount + pkgsCount;
        }

        var branchServiceMapping = branchServices
            .Select(bs => {
                var s = services.FirstOrDefault(x => x.Id == bs.ServiceId);
                return new BranchServiceMappingDto
                {
                    BranchId = bs.BranchId,
                    ServiceId = bs.ServiceId,
                    Price = bs.CustomPrice ?? s?.BasePrice ?? 0m,
                    OriginalPrice = bs.CustomOriginalPrice ?? s?.OriginalPrice
                };
            })
            .Where(x => x.Price > 0)
            .ToList();

        var branchPackageMapping = branchPackages
            .Select(bp => {
                var p = packages.FirstOrDefault(x => x.Id == bp.PackageId);
                return new BranchPackageMappingDto
                {
                    BranchId = bp.BranchId,
                    PackageId = bp.PackageId,
                    Price = bp.CustomPrice ?? p?.BasePrice ?? 0m,
                    OriginalPrice = bp.CustomOriginalPrice ?? p?.OriginalPrice
                };
            })
            .Where(x => x.Price > 0)
            .ToList();

        var activeServiceIds = branchServiceMapping.Select(x => x.ServiceId).ToHashSet();
        var activePackageIds = branchPackageMapping.Select(x => x.PackageId).ToHashSet();

        services = services.Where(s => activeServiceIds.Contains(s.Id)).ToList();
        packages = packages.Where(p => activePackageIds.Contains(p.Id)).ToList();

        var response = new LocationCatalogResponse
        {
            Branches = branchCoverage.OrderBy(b => b.Distance).ToList(),
            Services = services,
            Packages = packages,
            BranchServices = branchServiceMapping,
            BranchPackages = branchPackageMapping
        };

        return Ok(ApiResponse<LocationCatalogResponse>.SuccessResult(response, "Location catalog retrieved successfully."));
    }

    [HttpGet("eligible-labs")]
    [AllowAnonymous]
    [EndpointSummary("Get eligible labs for a cart of items near coordinates. Returns single-lab matches and, if none covers all, a multi-lab split suggestion.")]
    public async Task<IActionResult> GetEligibleLabs(
        [FromQuery] double latitude,
        [FromQuery] double longitude,
        [FromQuery] string itemIds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(itemIds))
        {
            return BadRequest(ApiResponse.FailureResult("itemIds is required."));
        }

        var ids = itemIds.Split(',').Select(id => id.Trim()).ToList();
        var allBranches = await _context.Branches.Where(b => b.IsActive).ToListAsync(cancellationToken);
        
        var services = await _context.Services.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync(cancellationToken);
        var packages = await _context.Packages.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);

        var branchServices = await _context.BranchServices
            .Where(bs => ids.Contains(bs.ServiceId) && bs.IsActive)
            .ToListAsync(cancellationToken);

        var branchPackages = await _context.BranchPackages
            .Where(bp => ids.Contains(bp.PackageId) && bp.IsActive)
            .ToListAsync(cancellationToken);

        // Build per-branch coverage map
        var branchCoverage = new Dictionary<string, (Branch Branch, HashSet<string> CoveredIds, double RoadDistance, decimal BaseTotal, decimal TravelFee)>();
        var httpClient = _httpClientFactory.CreateClient();

        foreach (var branch in allBranches)
        {
            var distance = CalculateDistanceKm(latitude, longitude, (double)branch.Latitude, (double)branch.Longitude);
            if (distance > branch.ServiceRangeKm) continue;

            var svcIds = branchServices.Where(bs => bs.BranchId == branch.Id).Select(bs => bs.ServiceId).ToHashSet();
            var pkgIds = branchPackages.Where(bp => bp.BranchId == branch.Id).Select(bp => bp.PackageId).ToHashSet();
            var coveredIds = ids.Where(id => svcIds.Contains(id) || pkgIds.Contains(id)).ToHashSet();

            decimal totalPrice = 0m;
            foreach (var id in coveredIds)
            {
                var bs = branchServices.FirstOrDefault(x => x.BranchId == branch.Id && x.ServiceId == id);
                if (bs != null) { totalPrice += bs.CustomPrice ?? services.FirstOrDefault(s => s.Id == id)?.BasePrice ?? 0m; }
                else
                {
                    var bp = branchPackages.FirstOrDefault(x => x.BranchId == branch.Id && x.PackageId == id);
                    if (bp != null) totalPrice += bp.CustomPrice ?? packages.FirstOrDefault(p => p.Id == id)?.BasePrice ?? 0m;
                }
            }

            var roadDistance = await GetRoadDistanceKm(latitude, longitude, (double)branch.Latitude, (double)branch.Longitude, httpClient);
            decimal travelCost = (decimal)roadDistance * (branch.PerKmCharge ?? 0m);

            branchCoverage[branch.Id] = (branch, coveredIds, roadDistance, totalPrice, Math.Round(travelCost));
        }

        // ── Single-lab matches (covers all items) ──────────────────────────────
        var eligibleLabs = new List<object>();
        foreach (var (branchId, (branch, coveredIds, roadDistance, baseTotal, travelFee)) in branchCoverage)
        {
            if (coveredIds.Count >= ids.Count)
            {
                eligibleLabs.Add(new
                {
                    BranchId = branch.Id,
                    Name = branch.Name,
                    City = branch.City,
                    District = branch.District,
                    Distance = CalculateDistanceKm(latitude, longitude, (double)branch.Latitude, (double)branch.Longitude),
                    RoadDistance = roadDistance,
                    BaseTotal = baseTotal,
                    TravelFee = travelFee,
                    GrandTotal = baseTotal + travelFee,
                    IsMultiLab = false,
                    SplitSuggestion = (object?)null
                });
            }
        }

        // ── Multi-lab split suggestion (when no single lab covers everything) ──
        object? splitSuggestion = null;
        if (!eligibleLabs.Any() && branchCoverage.Any())
        {
            // Greedy set-cover: iteratively pick the branch covering the most uncovered items
            var uncovered = ids.ToHashSet();
            var splits = new List<object>();
            decimal splitTotalBase = 0m;
            decimal splitTotalTravel = 0m;

            while (uncovered.Any())
            {
                // Pick the nearby branch covering the most uncovered items
                var best = branchCoverage
                    .Where(kv => kv.Value.CoveredIds.Intersect(uncovered).Any())
                    .OrderByDescending(kv => kv.Value.CoveredIds.Intersect(uncovered).Count())
                    .ThenBy(kv => kv.Value.RoadDistance)
                    .FirstOrDefault();

                if (best.Key == null) break; // no more branches can help

                var assignedIds = best.Value.CoveredIds.Intersect(uncovered).ToList();
                uncovered.ExceptWith(assignedIds);

                // Compute price for only the assigned items in this lab
                decimal labBase = 0m;
                foreach (var id in assignedIds)
                {
                    var bs = branchServices.FirstOrDefault(x => x.BranchId == best.Key && x.ServiceId == id);
                    if (bs != null) labBase += bs.CustomPrice ?? services.FirstOrDefault(s => s.Id == id)?.BasePrice ?? 0m;
                    else
                    {
                        var bp = branchPackages.FirstOrDefault(x => x.BranchId == best.Key && x.PackageId == id);
                        if (bp != null) labBase += bp.CustomPrice ?? packages.FirstOrDefault(p => p.Id == id)?.BasePrice ?? 0m;
                    }
                }

                var assignedItemNames = services.Where(s => assignedIds.Contains(s.Id)).Select(s => s.Name)
                    .Concat(packages.Where(p => assignedIds.Contains(p.Id)).Select(p => p.Name)).ToList();

                splitTotalBase += labBase;
                splitTotalTravel += best.Value.TravelFee;

                splits.Add(new
                {
                    BranchId = best.Value.Branch.Id,
                    Name = best.Value.Branch.Name,
                    City = best.Value.Branch.City,
                    District = best.Value.Branch.District,
                    RoadDistance = best.Value.RoadDistance,
                    TravelFee = best.Value.TravelFee,
                    AssignedItemIds = assignedIds,
                    AssignedItemNames = assignedItemNames,
                    BaseTotal = labBase,
                    GrandTotal = labBase + best.Value.TravelFee
                });
            }

            if (!uncovered.Any() && splits.Any())
            {
                splitSuggestion = new
                {
                    Labs = splits,
                    TotalBase = splitTotalBase,
                    TotalTravel = splitTotalTravel,
                    GrandTotal = splitTotalBase + splitTotalTravel,
                    UncoveredItemIds = (List<string>?)null
                };
            }
            else if (uncovered.Any())
            {
                var uncoveredNames = services.Where(s => uncovered.Contains(s.Id)).Select(s => s.Name)
                    .Concat(packages.Where(p => uncovered.Contains(p.Id)).Select(p => p.Name)).ToList();
                splitSuggestion = new
                {
                    Labs = splits,
                    TotalBase = splitTotalBase,
                    TotalTravel = splitTotalTravel,
                    GrandTotal = splitTotalBase + splitTotalTravel,
                    UncoveredItemIds = uncovered.ToList(),
                    UncoveredItemNames = uncoveredNames
                };
            }
        }

        var responseData = new
        {
            EligibleLabs = eligibleLabs,
            SplitSuggestion = splitSuggestion,
            HasSingleLabOption = eligibleLabs.Any()
        };

        return Ok(ApiResponse<object>.SuccessResult(responseData, "Eligible labs retrieved successfully."));
    }

    [HttpGet("slots/branch/{branchId}")]
    [AllowAnonymous]
    [EndpointSummary("Get available slots for a specific lab branch")]
    public async Task<IActionResult> GetBranchSlots(
        [FromRoute] string branchId,
        CancellationToken cancellationToken)
    {
        var indianTime = DateTime.UtcNow.AddHours(5).AddMinutes(30);
        var today = DateOnly.FromDateTime(indianTime);
        var currentTime = TimeOnly.FromDateTime(indianTime);
        var configs = await _context.BranchSlotConfigurations
            .Where(c => c.BranchId == branchId)
            .ToListAsync(cancellationToken);

        if (configs.Any())
        {
            var existingSlots = await _context.AppointmentSlots
                .Where(s => s.BranchId == branchId && s.SlotDate >= today)
                .ToListAsync(cancellationToken);

            var newSlots = new List<AppointmentSlot>();

            for (int i = 0; i < 7; i++)
            {
                var date = today.AddDays(i);
                var dayOfWeek = date.DayOfWeek;
                DayText dayEnum = dayOfWeek switch
                {
                    DayOfWeek.Monday => DayText.Mon,
                    DayOfWeek.Tuesday => DayText.Tue,
                    DayOfWeek.Wednesday => DayText.Wed,
                    DayOfWeek.Thursday => DayText.Thu,
                    DayOfWeek.Friday => DayText.Fri,
                    DayOfWeek.Saturday => DayText.Sat,
                    DayOfWeek.Sunday => DayText.Sun,
                    _ => DayText.Mon
                };

                var configsForDay = configs.Where(c => c.DayText == dayEnum).ToList();

                foreach (var config in configsForDay)
                {
                    var exists = existingSlots.Any(s => s.SlotDate == date && s.StartTime == config.StartTime);
                    if (!exists)
                    {
                        newSlots.Add(new AppointmentSlot
                        {
                            Id = Guid.NewGuid().ToString(),
                            BranchId = branchId,
                            SlotDate = date,
                            StartTime = config.StartTime,
                            EndTime = config.EndTime,
                            MaxCapacity = config.MaxCapacity,
                            BookedCount = 0,
                            IsAvailable = !config.IsLeave
                        });
                    }
                }
            }

            if (newSlots.Any())
            {
                _context.AppointmentSlots.AddRange(newSlots);
                await _context.SaveChangesAsync(cancellationToken);
            }
        }

        var slots = await _context.AppointmentSlots
            .Where(s => s.BranchId == branchId && s.IsAvailable && s.SlotDate >= today)
            .OrderBy(s => s.SlotDate).ThenBy(s => s.StartTime)
            .ToListAsync(cancellationToken);

        slots = slots.Where(s => s.SlotDate > today || s.StartTime > currentTime).ToList();

        return Ok(ApiResponse<List<AppointmentSlot>>.SuccessResult(slots, "Slots retrieved successfully."));
    }

    [HttpPost("{id}/cancel")]
    [EndpointSummary("Cancel appointment")]
    public async Task<IActionResult> CancelAppointment([FromRoute] string id, CancellationToken cancellationToken)
    {
        var currentUserId = _currentUserService.UserId?.ToString();
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized(ApiResponse.FailureResult("User not authenticated."));
        }

        var appointment = await _context.Appointments
            .Include(a => a.AppointmentSlot)
            .FirstOrDefaultAsync(a => a.Id == id && a.CustomerUserId == currentUserId, cancellationToken);

        if (appointment == null)
        {
            return NotFound(ApiResponse.FailureResult("Appointment not found."));
        }

        if (appointment.Status == AppointmentStatus.Cancelled)
        {
            return BadRequest(ApiResponse.FailureResult("Appointment is already cancelled."));
        }

        if (appointment.AppointmentSlot != null)
        {
            var slot = appointment.AppointmentSlot;
            slot.BookedCount = Math.Max(0, slot.BookedCount - appointment.MemberCount);
            slot.IsAvailable = true;
            _context.AppointmentSlots.Update(slot);
        }

        appointment.Status = AppointmentStatus.Cancelled;
        _context.Appointments.Update(appointment);
        await _context.SaveChangesAsync(cancellationToken);

        return Ok(ApiResponse.SuccessResult("Appointment cancelled successfully."));
    }

    [HttpPost("book")]
    [EndpointSummary("Book diagnostic appointment via location")]
    [EndpointDescription("Validates geographic distance, applies OSRM travel pricing, generates a Razorpay payment link, and returns it to the client.")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<object>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
    public async Task<IActionResult> BookAppointment([FromBody] WebBookingRequest request, CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.SlotId))
        {
            return BadRequest(ApiResponse.FailureResult("SlotId and coordinates are required."));
        }

        var itemIds = request.ItemIds ?? (string.IsNullOrEmpty(request.ServiceId) ? new List<string>() : new List<string> { request.ServiceId });
        if (!itemIds.Any())
        {
            return BadRequest(ApiResponse.FailureResult("At least one diagnostic service or package is required."));
        }

        var currentUserId = _currentUserService.UserId?.ToString();
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized(ApiResponse.FailureResult("User not authenticated."));
        }

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId, cancellationToken);
        if (user == null || string.IsNullOrWhiteSpace(user.Phone))
        {
            return BadRequest(ApiResponse.FailureResult("A registered phone number is required to process payment."));
        }

        // 1. Fetch Slot
        var slot = await _context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == request.SlotId, cancellationToken);
        if (slot == null)
        {
            return NotFound(ApiResponse.FailureResult("Appointment slot not found."));
        }

        // Fetch Services and Packages
        var services = await _context.Services.AsNoTracking().Where(s => itemIds.Contains(s.Id)).ToListAsync(cancellationToken);
        var packages = await _context.Packages.AsNoTracking().Where(p => itemIds.Contains(p.Id)).ToListAsync(cancellationToken);

        if (services.Count + packages.Count != itemIds.Distinct().Count())
        {
            return BadRequest(ApiResponse.FailureResult("Some selected diagnostic services or packages could not be found."));
        }

        // Check Slot Capacity
        var memberCount = request.MemberCount < 1 ? 1 : request.MemberCount;
        if (!slot.IsAvailable || slot.BookedCount + memberCount > slot.MaxCapacity)
        {
            return BadRequest(ApiResponse.FailureResult("Selected slot has insufficient capacity."));
        }

        // 2. Fetch Branch
        var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == slot.BranchId, cancellationToken);
        if (branch == null)
        {
            return NotFound(ApiResponse.FailureResult("Lab branch not found."));
        }

        // 3. Proximity validation (Haversine straight-line check)
        var straightLineDistance = CalculateDistanceKm(request.Latitude, request.Longitude, (double)branch.Latitude, (double)branch.Longitude);
        if (straightLineDistance > branch.ServiceRangeKm)
        {
            return BadRequest(ApiResponse.FailureResult("Service is not available at your selected location. The coordinates are outside the laboratory branch's service coverage range."));
        }

        // Calculate OSRM road distance
        var client = _httpClientFactory.CreateClient();
        var roadDistance = await GetRoadDistanceKm(request.Latitude, request.Longitude, (double)branch.Latitude, (double)branch.Longitude, client);
        
        // Calculate pricing
        var branchServices = await _context.BranchServices
            .Where(bs => bs.BranchId == branch.Id && itemIds.Contains(bs.ServiceId) && bs.IsActive)
            .ToListAsync(cancellationToken);

        var branchPackages = await _context.BranchPackages
            .Where(bp => bp.BranchId == branch.Id && itemIds.Contains(bp.PackageId) && bp.IsActive)
            .ToListAsync(cancellationToken);

        // Validate that the selected branch offers all requested services and packages
        if (branchServices.Count + branchPackages.Count < itemIds.Distinct().Count())
        {
            return BadRequest(ApiResponse.FailureResult("One or more of the selected diagnostic services or packages are not offered by the chosen laboratory branch."));
        }

        decimal totalBaseAmount = 0m;
        var encodedSelections = new List<string>();

        if (request.MemberSelections != null && request.MemberSelections.Any())
        {
            for (int i = 0; i < request.MemberSelections.Count; i++)
            {
                var selection = request.MemberSelections[i];
                decimal memberSum = 0m;
                foreach (var itemId in selection.ItemIds)
                {
                    var s = services.FirstOrDefault(x => x.Id == itemId);
                    if (s != null)
                    {
                        var bs = branchServices.FirstOrDefault(x => x.ServiceId == itemId);
                        memberSum += bs?.CustomPrice ?? s.BasePrice;
                    }
                    else
                    {
                        var p = packages.FirstOrDefault(x => x.Id == itemId);
                        if (p != null)
                        {
                            var bp = branchPackages.FirstOrDefault(x => x.PackageId == itemId);
                            memberSum += bp?.CustomPrice ?? p.BasePrice;
                        }
                    }
                }

                totalBaseAmount += memberSum;
 
                var memberNameSanitized = string.IsNullOrWhiteSpace(selection.Name) ? (i == 0 ? "Self" : $"Member {i + 1}") : selection.Name.Replace(":", "").Replace(";", "").Replace(",", "");
                encodedSelections.Add($"{memberNameSanitized}:{string.Join(",", selection.ItemIds)}");
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
                }
                else
                {
                    var p = packages.FirstOrDefault(x => x.Id == itemId);
                    if (p != null)
                    {
                        var bp = branchPackages.FirstOrDefault(x => x.PackageId == itemId);
                        rate += bp?.CustomPrice ?? p.BasePrice;
                    }
                }
            }

            totalBaseAmount = rate * memberCount;

            for (int i = 0; i < memberCount; i++)
            {
                encodedSelections.Add($"{(i == 0 ? "Self" : $"Member {i + 1}")}:{string.Join(",", itemIds)}");
            }
        }

        // Extra travel cost based on OSRM road distance
        decimal travelCost = (decimal)roadDistance * (branch.PerKmCharge ?? 0m);

        int total = (int)Math.Round(totalBaseAmount) + (int)Math.Round(travelCost);
        var memberSelectionsStr = string.Join(";", encodedSelections);

        // Generate Razorpay Payment Link
        var rzpKeyId = await _settingsService.GetRazorpayKeyIdAsync();
        var rzpKeySecret = await _settingsService.GetRazorpayKeySecretAsync();
        var itemNames = services.Select(s => s.Name).Concat(packages.Select(p => p.Name)).ToList();
        var itemNamesStr = string.Join(", ", itemNames);
        
        string? paymentUrl = null;
        try
        {
            var cleanPhone = user.Phone.Trim().Replace("+", "").Replace(" ", "").Replace("-", "");
            var contactStr = cleanPhone.StartsWith("91") ? $"+{cleanPhone}" : $"+91{cleanPhone}";

            var rzpClient = _httpClientFactory.CreateClient();
            var authString = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{rzpKeyId}:{rzpKeySecret}"));
            rzpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authString);

            var rzpPayload = new
            {
                amount = total * 100,
                currency = "INR",
                accept_partial = false,
                description = $"LabCare Booking: {itemNamesStr}",
                customer = new
                {
                    name = user.Name ?? "Web User",
                    contact = contactStr,
                },
                notify = new { sms = false, email = false },
                reminder_enable = false,
                notes = new
                {
                    phone = user.Phone.Trim(),
                    lab = branch.Name,
                    selected_test_id = string.Join(",", itemIds),
                    selected_lab_id = branch.Id,
                    selected_slot_id = slot.Id,
                    member_count = memberCount.ToString(),
                    building_details = request.BuildingDetails ?? "",
                    landmark = request.Landmark ?? "",
                    floor = request.Floor ?? "",
                    latitude = request.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    longitude = request.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    member_selections = memberSelectionsStr
                }
            };

            var content = new StringContent(JsonSerializer.Serialize(rzpPayload), Encoding.UTF8, "application/json");
            var response = await rzpClient.PostAsync("https://api.razorpay.com/v1/payment_links", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                using var rzpDoc = JsonDocument.Parse(responseBody);
                paymentUrl = rzpDoc.RootElement.GetProperty("short_url").GetString();
            }
            else
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                return BadRequest(ApiResponse.FailureResult($"Razorpay link generation failed: {errorBody}"));
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, ApiResponse.FailureResult($"Razorpay integration error: {ex.Message}"));
        }

        return Ok(ApiResponse<object>.SuccessResult(new { paymentUrl }, "Payment link generated successfully."));
    }

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

    private async Task<double> GetRoadDistanceKm(double lat1, double lng1, double lat2, double lng2, HttpClient client)
    {
        try
        {
            // OSRM expects longitude,latitude ordering
            var lon1Str = lng1.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var lat1Str = lat1.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var lon2Str = lng2.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var lat2Str = lat2.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var url = $"http://router.project-osrm.org/route/v1/driving/{lon1Str},{lat1Str};{lon2Str},{lat2Str}?overview=false";
            
            client.Timeout = TimeSpan.FromSeconds(3); // Fast timeout for responsiveness
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
                            return distanceMeters / 1000.0; // convert to km
                        }
                    }
                }
            }
        }
        catch
        {
            // Fall back to Haversine straight-line distance on exception (API block, rate limits, timeout)
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

    [HttpGet("lookup/{token}")]
    [AllowAnonymous]
    [EndpointSummary("Look up appointment and member by unique token ID")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<LookupMemberResult>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
    public async Task<IActionResult> LookupMember([FromRoute] string token, CancellationToken cancellationToken)
    {
        var member = await _context.AppointmentMembers
            .FirstOrDefaultAsync(m => m.UniqueNumber == token, cancellationToken);

        if (member == null)
        {
            return NotFound(ApiResponse.FailureResult("Invalid token ID. No collection member found."));
        }

        var appointment = await _context.Appointments
            .Include(a => a.Branch)
            .FirstOrDefaultAsync(a => a.Id == member.AppointmentId, cancellationToken);

        if (appointment == null)
        {
            return NotFound(ApiResponse.FailureResult("Linked appointment not found."));
        }

        var customerUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Id == appointment.CustomerUserId, cancellationToken);

        var result = new LookupMemberResult
        {
            UniqueNumber = member.UniqueNumber ?? string.Empty,
            MemberName = member.MemberName,
            Age = member.Age,
            Gender = member.Gender.ToString(),
            Relationship = member.Relationship,
            TestName = member.TestName ?? string.Empty,
            AdditionalNotes = member.AdditionalNotes,
            AppointmentNumber = appointment.AppointmentNumber,
            AppointmentStatus = appointment.Status.ToString(),
            CustomerName = customerUser?.Name ?? "Patient",
            CustomerPhone = customerUser?.Phone ?? string.Empty,
            Address = appointment.LocationAddress,
            LabName = appointment.Branch?.Name ?? "Associated Lab",
            ReportPdfPath = appointment.ReportPdfPath
        };

        return Ok(ApiResponse<LookupMemberResult>.SuccessResult(result, "Member lookup successful."));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Phase 3: Multi-Lab Booking
    // ──────────────────────────────────────────────────────────────────────────

    [HttpPost("book-multi-lab")]
    [EndpointSummary("Book a multi-lab appointment where different services are performed by different labs")]
    public async Task<IActionResult> BookMultiLabAppointment(
        [FromBody] MultiLabBookingRequest request,
        CancellationToken cancellationToken)
    {
        if (request?.LabSplits == null || !request.LabSplits.Any())
            return BadRequest(ApiResponse.FailureResult("At least one lab split is required."));

        var currentUserId = _currentUserService.UserId?.ToString();
        if (string.IsNullOrEmpty(currentUserId))
            return Unauthorized(ApiResponse.FailureResult("User not authenticated."));

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId, cancellationToken);
        if (user == null || string.IsNullOrWhiteSpace(user.Phone))
            return BadRequest(ApiResponse.FailureResult("A registered phone number is required."));

        var memberCount = request.MemberSelections?.Count ?? request.MemberCount;
        if (memberCount < 1) memberCount = 1;

        // Validate all slots have capacity
        foreach (var split in request.LabSplits)
        {
            var slot = await _context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == split.SlotId, cancellationToken);
            if (slot == null || !slot.IsAvailable || slot.BookedCount + memberCount > slot.MaxCapacity)
                return BadRequest(ApiResponse.FailureResult($"Slot for lab split ({split.BranchId}) has insufficient capacity or is unavailable."));

            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == split.BranchId, cancellationToken);
            if (branch == null)
                return NotFound(ApiResponse.FailureResult($"Branch {split.BranchId} not found."));

            // Proximity check
            var dist = CalculateDistanceKm(request.Latitude, request.Longitude, (double)branch.Latitude, (double)branch.Longitude);
            if (dist > branch.ServiceRangeKm)
                return BadRequest(ApiResponse.FailureResult($"Branch {branch.Name} is outside your service range."));
        }

        // Build master booking ID
        var bookingId = $"BK-{DateTime.UtcNow:yyyyMMdd}-{new Random().Next(1000, 9999)}";
        var allItemIds = request.LabSplits.SelectMany(s => s.ItemIds).Distinct().ToList();
        var services = await _context.Services.AsNoTracking().Where(s => allItemIds.Contains(s.Id)).ToListAsync(cancellationToken);
        var packages = await _context.Packages.AsNoTracking().Where(p => allItemIds.Contains(p.Id)).ToListAsync(cancellationToken);
        var locationAddress = $"{request.BuildingDetails}, Floor {request.Floor}, Landmark: {request.Landmark}";

        // ── Create PARENT appointment (single entry point, no branch-specific slot) ──
        // Parent uses the first split's slot/branch for FK compliance but IsMultiLab=true distinguishes it
        var firstSplit = request.LabSplits[0];
        var firstSlot = await _context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == firstSplit.SlotId, cancellationToken);
        var firstBranch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == firstSplit.BranchId, cancellationToken);

        var parentPasscode = new Random().Next(1000, 9999).ToString();
        var parentAppointment = new Appointment
        {
            Id = Guid.NewGuid().ToString(),
            AppointmentNumber = bookingId,
            CustomerUserId = user.Id,
            BranchId = firstSplit.BranchId,
            AppointmentSlotId = firstSplit.SlotId,
            LocationLatitude = (decimal)request.Latitude,
            LocationLongitude = (decimal)request.Longitude,
            LocationAddress = locationAddress,
            BuildingDetails = request.BuildingDetails,
            Floor = request.Floor,
            Landmark = request.Landmark,
            Passcode = parentPasscode,
            Status = AppointmentStatus.Pending, // Payment pending
            TotalAmount = 0m, // Will be summed from children
            PlatformCommission = 0m,
            LabPayout = 0m,
            CreatedAt = DateTime.UtcNow,
            MemberCount = memberCount,
            IsMultiLab = true,
            ItemIds = allItemIds
        };
        _context.Appointments.Add(parentAppointment);

        // ── Create CHILD sub-appointments per lab split ──────────────────────────
        decimal totalGrand = 0m;
        var childPayloads = new List<object>();
        var httpClient = _httpClientFactory.CreateClient();

        for (int splitIdx = 0; splitIdx < request.LabSplits.Count; splitIdx++)
        {
            var split = request.LabSplits[splitIdx];
            var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == split.BranchId, cancellationToken);
            var slot = await _context.AppointmentSlots.FirstOrDefaultAsync(s => s.Id == split.SlotId, cancellationToken);
            if (branch == null || slot == null) continue;

            var branchServicesForSplit = await _context.BranchServices
                .Where(bs => bs.BranchId == split.BranchId && split.ItemIds.Contains(bs.ServiceId) && bs.IsActive)
                .ToListAsync(cancellationToken);
            var branchPackagesForSplit = await _context.BranchPackages
                .Where(bp => bp.BranchId == split.BranchId && split.ItemIds.Contains(bp.PackageId) && bp.IsActive)
                .ToListAsync(cancellationToken);

            // Price for this split's items
            decimal splitBase = 0m;
            decimal commissionPctSum = 0m;
            int commissionCount = 0;
            foreach (var itemId in split.ItemIds)
            {
                var bs = branchServicesForSplit.FirstOrDefault(x => x.ServiceId == itemId);
                var svc = services.FirstOrDefault(s => s.Id == itemId);
                if (bs != null && svc != null) { splitBase += bs.CustomPrice ?? svc.BasePrice; commissionPctSum += bs.CustomCommissionPct ?? svc.PlatformCommissionPct; commissionCount++; }
                else
                {
                    var bp = branchPackagesForSplit.FirstOrDefault(x => x.PackageId == itemId);
                    var pkg = packages.FirstOrDefault(p => p.Id == itemId);
                    if (bp != null && pkg != null) { splitBase += bp.CustomPrice ?? pkg.BasePrice; commissionPctSum += bp.CustomCommissionPct ?? pkg.PlatformCommissionPct; commissionCount++; }
                }
            }
            // No member discount applied for additional members
            decimal splitTotal = splitBase * memberCount;

            var roadDist = await GetRoadDistanceKm(request.Latitude, request.Longitude, (double)branch.Latitude, (double)branch.Longitude, httpClient);
            decimal travelCost = (decimal)roadDist * (branch.PerKmCharge ?? 0m);
            decimal splitGrand = splitTotal + Math.Round(travelCost);
            totalGrand += splitGrand;

            decimal avgComm = commissionCount > 0 ? commissionPctSum / commissionCount : 15m;
            var subBookingId = $"{bookingId}-L{splitIdx + 1}";

            var childAppointment = new Appointment
            {
                Id = Guid.NewGuid().ToString(),
                AppointmentNumber = subBookingId,
                CustomerUserId = user.Id,
                BranchId = split.BranchId,
                AppointmentSlotId = split.SlotId,
                LocationLatitude = (decimal)request.Latitude,
                LocationLongitude = (decimal)request.Longitude,
                LocationAddress = locationAddress,
                BuildingDetails = request.BuildingDetails,
                Floor = request.Floor,
                Landmark = request.Landmark,
                Passcode = parentPasscode, // shared passcode across all labs
                Status = AppointmentStatus.Pending,
                TotalAmount = splitGrand,
                PlatformCommission = splitGrand * (avgComm / 100m),
                LabPayout = splitGrand * (1m - avgComm / 100m),
                CreatedAt = DateTime.UtcNow,
                MemberCount = memberCount,
                IsMultiLab = true,
                ParentAppointmentId = parentAppointment.Id,
                ItemIds = split.ItemIds
            };
            _context.Appointments.Add(childAppointment);

            // Update slot capacity
            slot.BookedCount += memberCount;
            if (slot.BookedCount >= slot.MaxCapacity) slot.IsAvailable = false;
            _context.AppointmentSlots.Update(slot);

            // Create members for this child (pointing to child appointment)
            var parsedSelections = request.MemberSelections ?? new List<MemberServiceSelection>();
            var splitItemNames = services.Where(s => split.ItemIds.Contains(s.Id)).Select(s => s.Name)
                .Concat(packages.Where(p => split.ItemIds.Contains(p.Id)).Select(p => p.Name)).ToList();
            var splitItemNamesStr = string.Join(", ", splitItemNames);
            var customerName = string.IsNullOrWhiteSpace(user.Name) ? "Patient" : user.Name;

            if (parsedSelections.Any())
            {
                for (int mi = 0; mi < parsedSelections.Count; mi++)
                {
                    var sel = parsedSelections[mi];
                    // Only create a member record for this child if they have at least one item from this split
                    var memberItemsForThisSplit = sel.ItemIds.Where(id => split.ItemIds.Contains(id)).ToList();
                    if (!memberItemsForThisSplit.Any()) continue;

                    decimal memberAmt = 0m;
                    foreach (var id in memberItemsForThisSplit)
                    {
                        var bs = branchServicesForSplit.FirstOrDefault(x => x.ServiceId == id);
                        var svc = services.FirstOrDefault(s => s.Id == id);
                        if (bs != null && svc != null) memberAmt += bs.CustomPrice ?? svc.BasePrice;
                        else { var bp = branchPackagesForSplit.FirstOrDefault(x => x.PackageId == id); var pkg = packages.FirstOrDefault(p => p.Id == id); if (bp != null && pkg != null) memberAmt += bp.CustomPrice ?? pkg.BasePrice; }
                    }
                    // No member discount applied

                    var memberItemNames2 = services.Where(s => memberItemsForThisSplit.Contains(s.Id)).Select(s => s.Name)
                        .Concat(packages.Where(p => memberItemsForThisSplit.Contains(p.Id)).Select(p => p.Name)).ToList();

                    _context.AppointmentMembers.Add(new AppointmentMember
                    {
                        Id = Guid.NewGuid().ToString(),
                        AppointmentId = childAppointment.Id,
                        MemberName = string.IsNullOrWhiteSpace(sel.Name) ? (mi == 0 ? customerName : $"Member {mi + 1}") : sel.Name,
                        Age = 0,
                        Gender = Gender.Other,
                        Relationship = mi == 0 ? "Self" : "Family Member",
                        UniqueNumber = $"MEM-{Guid.NewGuid().ToString("N")[..8].ToUpper()}",
                        TestName = string.Join(", ", memberItemNames2),
                        ServiceItemIds = memberItemsForThisSplit,
                        Amount = memberAmt,
                        SubAppointmentId = childAppointment.Id
                    });
                }
            }
            else
            {
                for (int mi = 0; mi < memberCount; mi++)
                {
                    decimal memberAmt = splitBase;
                    _context.AppointmentMembers.Add(new AppointmentMember
                    {
                        Id = Guid.NewGuid().ToString(),
                        AppointmentId = childAppointment.Id,
                        MemberName = mi == 0 ? customerName : $"Member {mi + 1}",
                        Age = 0,
                        Gender = Gender.Other,
                        Relationship = mi == 0 ? "Self" : "Family Member",
                        UniqueNumber = $"MEM-{Guid.NewGuid().ToString("N")[..8].ToUpper()}",
                        TestName = splitItemNamesStr,
                        ServiceItemIds = split.ItemIds,
                        Amount = memberAmt,
                        SubAppointmentId = childAppointment.Id
                    });
                }
            }

            // WhatsApp notification to lab
            if (!string.IsNullOrEmpty(branch.NotificationPhone))
            {
                var labMsg = $"🔔 *Multi-Lab Booking: {subBookingId}*\n" +
                             $"Services: {splitItemNamesStr}\n" +
                             $"Slot: {slot.SlotDate:dd MMM yyyy} @ {slot.StartTime:hh:mm tt}\n" +
                             $"Members: {memberCount}\n" +
                             $"Address: {locationAddress}\n" +
                             $"Customer: +{user.Phone}";
                try { await _whatsAppService.SendTextMessageAsync(branch.NotificationPhone, labMsg); } catch { /* non-critical */ }
            }

            childPayloads.Add(new
            {
                SubAppointmentId = childAppointment.Id,
                AppointmentNumber = subBookingId,
                BranchId = split.BranchId,
                BranchName = branch.Name,
                SlotId = split.SlotId,
                ItemIds = split.ItemIds,
                GrandTotal = splitGrand
            });
        }

        // Update parent total
        parentAppointment.TotalAmount = totalGrand;
        _context.Appointments.Update(parentAppointment);

        await _context.SaveChangesAsync(cancellationToken);

        // Generate combined Razorpay payment link for full amount
        var rzpKeyId = await _settingsService.GetRazorpayKeyIdAsync();
        var rzpKeySecret = await _settingsService.GetRazorpayKeySecretAsync();
        string? paymentUrl = null;
        try
        {
            var cleanPhone = user.Phone.Trim().Replace("+", "").Replace(" ", "").Replace("-", "");
            var contactStr = cleanPhone.StartsWith("91") ? $"+{cleanPhone}" : $"+91{cleanPhone}";

            var rzpClient = _httpClientFactory.CreateClient();
            var authString = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{rzpKeyId}:{rzpKeySecret}"));
            rzpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authString);

            var memberSelectionsStr = request.MemberSelections != null
                ? string.Join(";", request.MemberSelections.Select(ms => $"{ms.Name}:{string.Join(",", ms.ItemIds)}"))
                : string.Empty;

            var rzpPayload = new
            {
                amount = (int)Math.Round(totalGrand) * 100,
                currency = "INR",
                accept_partial = false,
                description = $"Multi-Lab Booking: {bookingId}",
                customer = new { name = user.Name ?? "Web User", contact = contactStr },
                notify = new { sms = false, email = false },
                reminder_enable = false,
                notes = new
                {
                    phone = user.Phone.Trim(),
                    booking_id = bookingId,
                    parent_appointment_id = parentAppointment.Id,
                    is_multi_lab = "true",
                    member_count = memberCount.ToString(),
                    building_details = request.BuildingDetails ?? "",
                    landmark = request.Landmark ?? "",
                    floor = request.Floor ?? "",
                    latitude = request.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    longitude = request.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    member_selections = memberSelectionsStr
                }
            };

            var content = new StringContent(JsonSerializer.Serialize(rzpPayload), Encoding.UTF8, "application/json");
            var response = await rzpClient.PostAsync("https://api.razorpay.com/v1/payment_links", content, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                using var rzpDoc = JsonDocument.Parse(responseBody);
                paymentUrl = rzpDoc.RootElement.GetProperty("short_url").GetString();
            }
        }
        catch { /* Payment link generation failed */ }

        // Customer WhatsApp notification
        try
        {
            var labNames = request.LabSplits.Select((s, i) =>
            {
                var b = _context.Branches.FirstOrDefault(br => br.Id == s.BranchId);
                return $"  Lab {i + 1}: {b?.Name ?? s.BranchId}";
            });
            await _whatsAppService.SendTextMessageAsync(user.Phone,
                $"📋 *Multi-Lab Booking Initiated!*\n\n" +
                $"🆔 Booking ID: *{bookingId}*\n" +
                $"🔐 Shared Passcode: *{parentPasscode}*\n" +
                $"Your booking spans {request.LabSplits.Count} labs:\n" +
                string.Join("\n", labNames) + "\n\n" +
                $"💳 Total: ₹{(int)Math.Round(totalGrand)}\n" +
                $"Complete payment at: {paymentUrl}");
        }
        catch { /* non-critical */ }

        return Ok(ApiResponse<object>.SuccessResult(new
        {
            paymentUrl,
            bookingId,
            parentAppointmentId = parentAppointment.Id,
            totalAmount = (int)Math.Round(totalGrand),
            passcode = parentPasscode,
            labSplits = childPayloads
        }, "Multi-lab booking initiated. Complete payment to confirm."));
    }
}

public class LookupMemberResult
{
    public string UniqueNumber { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Gender { get; set; } = string.Empty;
    public string Relationship { get; set; } = string.Empty;
    public string TestName { get; set; } = string.Empty;
    public string? AdditionalNotes { get; set; }
    public string AppointmentNumber { get; set; } = string.Empty;
    public string AppointmentStatus { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string LabName { get; set; } = string.Empty;
    public string? ReportPdfPath { get; set; }
}

public class MemberServiceSelection
{
    public string Name { get; set; } = string.Empty;
    public List<string> ItemIds { get; set; } = new();
}

public class WebBookingRequest
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string ServiceId { get; set; } = string.Empty;
    public List<string>? ItemIds { get; set; }
    public string SlotId { get; set; } = string.Empty;
    public int MemberCount { get; set; } = 1;
    public string BuildingDetails { get; set; } = string.Empty;
    public string Landmark { get; set; } = string.Empty;
    public string Floor { get; set; } = string.Empty;
    public List<MemberServiceSelection>? MemberSelections { get; set; }
}

public class RegionAvailabilityResult
{
    public string BranchId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string? District { get; set; }
    public double Distance { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int ServicesAvailableCount { get; set; }
    public int ServicesRequestedCount { get; set; }
    public int ServicesCoveredCount { get; set; }
    public bool IsFullyEligible { get; set; }
    public bool HasAvailableSlotsToday { get; set; }
    public string? NextAvailableSlotDate { get; set; }
    public string? NextAvailableSlotTime { get; set; }
}

public class LocationCatalogResponse
{
    public List<RegionAvailabilityResult> Branches { get; set; } = new();
    public List<Service> Services { get; set; } = new();
    public List<Package> Packages { get; set; } = new();
    public List<BranchServiceMappingDto> BranchServices { get; set; } = new();
    public List<BranchPackageMappingDto> BranchPackages { get; set; } = new();
}

public class BranchServiceMappingDto
{
    public string BranchId { get; set; } = string.Empty;
    public string ServiceId { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal? OriginalPrice { get; set; }
}

public class BranchPackageMappingDto
{
    public string BranchId { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal? OriginalPrice { get; set; }
}

/// <summary>
/// Phase 3: Describes one lab's slice of a multi-lab booking.
/// Each LabSplitItem says: "go to this branch, book this slot, and perform these specific items."
/// </summary>
public class LabSplitItem
{
    public string BranchId { get; set; } = string.Empty;
    public string SlotId { get; set; } = string.Empty;
    public List<string> ItemIds { get; set; } = new();
}

/// <summary>
/// Phase 3: Full request body for POST /api/appointments/book-multi-lab
/// </summary>
public class MultiLabBookingRequest
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string BuildingDetails { get; set; } = string.Empty;
    public string Landmark { get; set; } = string.Empty;
    public string Floor { get; set; } = string.Empty;
    public int MemberCount { get; set; } = 1;

    /// <summary>
    /// One entry per lab, describing which items to perform at that lab and which slot.
    /// The frontend populates this from the SplitSuggestion returned by eligible-labs,
    /// after the user picks slots for each sub-lab.
    /// </summary>
    public List<LabSplitItem> LabSplits { get; set; } = new();

    /// <summary>
    /// Optional per-member service assignment (from the Phase 2 matrix UI).
    /// When provided, each member's ItemIds are cross-referenced against each LabSplitItem.ItemIds
    /// to create granular member records per child appointment.
    /// </summary>
    public List<MemberServiceSelection>? MemberSelections { get; set; }
}

