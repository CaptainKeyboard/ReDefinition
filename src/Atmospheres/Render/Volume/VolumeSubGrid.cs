using System;

namespace ReDefinition.Atmospheres
{
    // One convective core inside a coarse cell: where it stands (km from the cell's centre), how wide, and how fast its air rises.
    internal struct VolumeCore
    {
        public double OffsetEastKm, OffsetNorthKm, RadiusKm, UpdraftMs;
    }

    // The deterministic sub-grid rule of the volume pass: a coarse cell with a resolved convective updraft is filled with cores of 5 to
    // 25 km for the local 3D window. It follows the rule proven in the simulation (tools/atmosphere-sim, AtmoSim.Core/SubGrid.cs, the
    // criterion "convective scale" of RESULT.md). A pure function of (cell, time bucket, seed, the cell's updraft and cloud depth); it
    // writes nothing back into the field, allocates nothing, and the same inputs give the same cores on every machine.
    internal static class VolumeSubGrid
    {
        internal const double MinRadiusKm = 5.0, MaxRadiusKm = 25.0;
        internal const double MinUpdraftMs = 2.0;
        // A core lives this long; its slot is drawn again when the bucket changes (the window's smoothing fades the change).
        internal const double LifetimeS = 2700.0;
        internal const double MaxAreaFraction = 0.35;
        internal const int MaxCores = 4;

        // Fills cores (length at least MaxCores) and returns the count for one cell. cellKmEast and cellKmNorth are the cell's size.
        internal static int Cores(long cellIndex, double ut, ulong seed, double updraftMs, double cloudDepthM, double cellKmEast,
                                  double cellKmNorth, VolumeCore[] cores)
        {
            if (!(updraftMs >= MinUpdraftMs) || !(cloudDepthM > 0.0) || !(cellKmEast > 0.0) || !(cellKmNorth > 0.0)) return 0;
            int n = (int)(updraftMs / 6.0) + 1;
            if (n > MaxCores) n = MaxCores;
            double depthKm = cloudDepthM / 1000.0;
            double radius = Clamp(0.8 * depthKm, MinRadiusKm, MaxRadiusKm);
            double cell = cellKmEast * cellKmNorth;
            // Keep the cores inside a share of the cell: shrink the count first, then the radius down to the minimum.
            while (n > 1 && n * Math.PI * radius * radius > MaxAreaFraction * cell) n--;
            if (n * Math.PI * radius * radius > MaxAreaFraction * cell)
                radius = Math.Max(MinRadiusKm, Math.Sqrt(MaxAreaFraction * cell / (Math.PI * n)));
            for (int k = 0; k < n; k++)
            {
                // Staggered buckets so that the cores of one cell do not all change at once.
                double phase = (k + 1.0) / (n + 1.0) * LifetimeS;
                long bucket = (long)Math.Floor((ut + phase) / LifetimeS);
                ulong state = Mix(seed, cellIndex, bucket, k);
                double ex = 0.5 * cellKmEast - radius, ey = 0.5 * cellKmNorth - radius;
                if (ex < 0) ex = 0;
                if (ey < 0) ey = 0;
                cores[k].OffsetEastKm = (Uniform(ref state) * 2.0 - 1.0) * ex;
                cores[k].OffsetNorthKm = (Uniform(ref state) * 2.0 - 1.0) * ey;
                cores[k].RadiusKm = Clamp(radius * (0.85 + 0.3 * Uniform(ref state)), MinRadiusKm, MaxRadiusKm);
                cores[k].UpdraftMs = updraftMs * (0.7 + 0.6 * Uniform(ref state));
            }
            return n;
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }

        private static ulong Mix(ulong seed, long cell, long bucket, int k)
        {
            ulong h = seed ^ 0x9E3779B97F4A7C15UL;
            unchecked
            {
                h = (h ^ (ulong)cell) * 0xBF58476D1CE4E5B9UL;
                h = (h ^ (ulong)bucket) * 0x94D049BB133111EBUL;
                h = (h ^ (ulong)k) * 0xD6E8FEB86659FD93UL;
            }
            return h ^ (h >> 29);
        }

        // The next uniform number in [0, 1) of a SplitMix64 state.
        private static double Uniform(ref ulong state)
        {
            unchecked
            {
                state += 0x9E3779B97F4A7C15UL;
                ulong z = state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z ^= z >> 31;
                return (z >> 11) * (1.0 / 9007199254740992.0);
            }
        }
    }
}
