using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Apenir.Core.Interfaces;

namespace Apenir.Application.Common.Helpers
{
    public static class PricingHelper
    {
        public static double CalculateDistanceKm(double lat1, double lng1, double lat2, double lng2)
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

        public static async Task<decimal> CalculateTravelFeeAsync(
            IApplicationDbContext context,
            string branchId,
            double userLat,
            double userLng,
            CancellationToken cancellationToken)
        {
            var branch = await context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);
            if (branch == null) return 0m;

            double distance = CalculateDistanceKm(userLat, userLng, (double)branch.Latitude, (double)branch.Longitude);

            // Fetch tiers for this branch
            var tiers = await context.BranchTravelCharges.AsNoTracking()
                .Where(t => t.BranchId == branchId)
                .OrderBy(t => t.MinDistanceKm)
                .ToListAsync(cancellationToken);

            if (!tiers.Any())
            {
                // Fallback: ₹10 per km
                return Math.Round((decimal)distance * 10m);
            }

            var matchedTier = tiers.FirstOrDefault(t => distance >= t.MinDistanceKm && distance <= t.MaxDistanceKm);
            if (matchedTier != null)
            {
                return matchedTier.Cost;
            }

            // If exceeds all configured ranges, use the highest tier cost or fallback
            var maxTier = tiers.Last();
            if (distance > maxTier.MaxDistanceKm)
            {
                // Extra kms charged at ₹10/km after max tier limit
                var extraDistance = distance - maxTier.MaxDistanceKm;
                return maxTier.Cost + Math.Round((decimal)extraDistance * 10m);
            }

            return 0m;
        }
    }
}
