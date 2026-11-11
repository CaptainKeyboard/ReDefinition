using System;

namespace AtmoSim.Core
{
    // The deterministic sub-grid rule (criterion 12): a grid cell with a resolved convective updraft is filled with cores of
    // 5-25 km for the local 3D window. A pure function of (cell, time bucket, seed, the cell's updraft and cloud depth); it
    // writes nothing back into the simulation.
    public struct SubCore
    {
        public double OffsetEastKm, OffsetNorthKm, RadiusKm, UpdraftMs;
    }

    public static class SubGrid
    {
        public const double MinRadiusKm = 5.0, MaxRadiusKm = 25.0;
        public const double MinUpdraftMs = 2.0;
        // A core lives this long; its slot is re-drawn when the bucket changes (the renderer crossfades the change).
        public const double LifetimeS = 2700.0;
        public const double MaxAreaFraction = 0.35;

        // Fills cores (length >= 4) and returns the count for one cell. cellKmEast and cellKmNorth are the cell's size.
        public static int Cores(long cellIndex, double ut, ulong seed, double updraftMs, double cloudDepthM, double cellKmEast, double cellKmNorth, SubCore[] cores)
        {
            if (updraftMs < MinUpdraftMs || cloudDepthM <= 0.0) return 0;
            int n = (int)(updraftMs / 6.0) + 1;
            if (n > 4) n = 4;
            double depthKm = cloudDepthM / 1000.0;
            double radius = DMath.Clamp(0.8 * depthKm, MinRadiusKm, MaxRadiusKm);
            double cell = cellKmEast * cellKmNorth;
            // keep the cores inside a share of the cell: shrink the count first, then the radius down to the minimum
            while (n > 1 && n * Math.PI * radius * radius > MaxAreaFraction * cell) n--;
            if (n * Math.PI * radius * radius > MaxAreaFraction * cell) radius = DMath.Max(MinRadiusKm, DMath.Sqrt(MaxAreaFraction * cell / (Math.PI * n)));
            for (int k = 0; k < n; k++)
            {
                // staggered buckets so that the cores of one cell do not all change at once
                double phase = (k + 1.0) / (n + 1.0) * LifetimeS;
                long bucket = (long)DMath.Floor((ut + phase) / LifetimeS);
                var rng = new Rng(Mix(seed, cellIndex, bucket, k));
                double ex = 0.5 * cellKmEast - radius, ey = 0.5 * cellKmNorth - radius;
                if (ex < 0) ex = 0; if (ey < 0) ey = 0;
                cores[k].OffsetEastKm = (rng.Uniform() * 2.0 - 1.0) * ex;
                cores[k].OffsetNorthKm = (rng.Uniform() * 2.0 - 1.0) * ey;
                cores[k].RadiusKm = radius * (0.85 + 0.3 * rng.Uniform());
                if (cores[k].RadiusKm < MinRadiusKm) cores[k].RadiusKm = MinRadiusKm;
                if (cores[k].RadiusKm > MaxRadiusKm) cores[k].RadiusKm = MaxRadiusKm;
                cores[k].UpdraftMs = updraftMs * (0.7 + 0.6 * rng.Uniform());
            }
            return n;
        }

        private static ulong Mix(ulong seed, long cell, long bucket, int k)
        {
            ulong h = seed ^ 0x9E3779B97F4A7C15UL;
            h = (h ^ (ulong)cell) * 0xBF58476D1CE4E5B9UL;
            h = (h ^ (ulong)bucket) * 0x94D049BB133111EBUL;
            h = (h ^ (ulong)k) * 0xD6E8FEB86659FD93UL;
            return h ^ (h >> 29);
        }
    }
}
