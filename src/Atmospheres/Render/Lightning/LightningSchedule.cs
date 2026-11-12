using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // The numbers of the lightning (package T07): the lattice of the schedule, the flash density at full strength, the shares of the kinds.
    // Plain data with the compiled defaults; the settings package (T10) will carry the ones a player may change.
    internal sealed class LightningParams
    {
        // The side of a lattice square in m, and the longest distance of a square the schedule looks at (the caller widens it with height).
        public double SquareM = 6000.0;
        // The length of a time slot in s: one draw per square and slot.
        public double SlotS = 1.0;
        // Flashes per km2 and minute at full cell strength and full level. A cell of 15 km radius then flashes about 7 times a minute at its
        // peak (a severe storm: 5 to 30 flashes a minute, Earth thunderstorm statistics).
        public double MaxDensityPerKm2Min = 0.02;
        // The share of the flashes that reach the ground (the rest stay in the cloud); about a quarter of Earth's flashes do.
        public float GroundShare = 0.3f;
        // The least cell strength that flashes at all, and the exponent of the strength in the density.
        public float MinStrength = 0.12f;
        public double StrengthExponent = 1.5;
        // The cloud cover under which no bolt spawns (EVE spawns where its coverage is above 0.1), and the warp rate from which none spawns.
        public float MinCoverage = 0.1f;
        public float MaxWarpRate = 5f;
        // The radius of the scan around the camera: the larger of MinScanM and twice the height above the ground, at most MaxScanM.
        public double MinScanM = 20000.0, MaxScanM = 70000.0;
    }

    // One lattice square with convective activity, as the scan found it.
    internal struct LightningCell
    {
        public int Row, Col;
        public double LatDeg, LonDeg;
        // The size of the square's sides in m (north-south, east-west) and in degrees of latitude and longitude.
        public double NorthM, EastM, NorthDeg, EastDeg;
        // The cell strength 0 to 1, the precipitation level 0 to 1, the height of the cell's top above the ground in m and the cloud cover 0 to 1.
        public float Strength, Level01, TopM, Coverage;
    }

    internal enum FlashKind { Cloud = 0, Ground = 1 }

    // One scheduled flash. All numbers are derived from (seed, row, col, slot) and the cell, never from a running random generator.
    internal struct FlashEvent
    {
        public long Slot;
        public int Row, Col;
        // The start in s of game time.
        public double Ut;
        public double LatDeg, LonDeg;
        public FlashKind Kind;
        // The number of strokes (bumps of light) 1 to 3.
        public int Bumps;
        // The seed of the shape: bolt geometry, stroke timing, thunder.
        public uint Seed;
        public float Strength, Level01, TopM;
        // The heights above the ground in m where the channel starts (the charge region) and where the light centre of the cloud is.
        public float StartHeightM, CloudLightHeightM;
    }

    // The schedule of the flashes: deterministic from the field and UT. A lattice of squares on the body (rows of equal height, as many
    // columns per row as fit, so the squares are about equal in area); in every square and time slot one draw decides whether a flash
    // happens, with a probability that follows the cell's strength and the precipitation level. The draw does not change with the level,
    // only the threshold it is compared with: a higher level adds flashes and removes none.
    internal static class LightningSchedule
    {
        // The flash density per km2 and minute of a cell: zero below the least strength, rising with the strength to the exponent and with the level
        // (no rain, no flash). Monotone in both.
        internal static double DensityPerKm2Min(LightningParams p, float strength, float level01)
        {
            if (strength < p.MinStrength) return 0.0;
            double s = Clamp01(strength);
            double lv = Clamp01(level01);
            double rain = SmoothStep(0.05, 0.35, lv) * (0.6 + 0.4 * lv);
            return p.MaxDensityPerKm2Min * Math.Pow(s, p.StrengthExponent) * rain;
        }

        // The probability that a square flashes in one slot.
        internal static double SlotProbability(LightningParams p, ref LightningCell c)
        {
            double km2 = c.NorthM * c.EastM * 1e-6;
            double perMin = DensityPerKm2Min(p, c.Strength, c.Level01) * km2;
            double prob = perMin * (p.SlotS / 60.0);
            return prob > 0.5 ? 0.5 : prob;
        }

        // The expected flashes per minute of one square.
        internal static double FlashesPerMinute(LightningParams p, ref LightningCell c)
        {
            return SlotProbability(p, ref c) * 60.0 / p.SlotS;
        }

        // Whether a flash may spawn at all: below the warp rate and under a cover above the minimum (EVE's two gates).
        internal static bool Allows(LightningParams p, float warpRate, float coverage)
        {
            return warpRate < p.MaxWarpRate && coverage > p.MinCoverage;
        }

        // The slot of a time.
        internal static long SlotOf(LightningParams p, double ut)
        {
            return (long)Math.Floor(ut / p.SlotS);
        }

        // The scan radius for a camera height above the ground.
        internal static double ScanRangeM(LightningParams p, double heightAboveGroundM)
        {
            double r = Math.Max(p.MinScanM, 2.0 * Math.Max(heightAboveGroundM, 0.0));
            return Math.Min(r, p.MaxScanM);
        }

        // The flashes of one slot from the squares of the scan, appended to into. The order is the order of the cells, then the draw.
        internal static void EventsInSlot(LightningParams p, uint seed, long slot, IList<LightningCell> cells, List<FlashEvent> into)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                LightningCell c = cells[i];
                double prob = SlotProbability(p, ref c);
                if (prob <= 0.0) continue;
                uint h = SlotHash(seed, c.Row, c.Col, slot);
                if (NoiseHash.Unit(h) >= prob) continue;
                into.Add(MakeEvent(p, seed, slot, ref c, h));
            }
        }

        // The hash of a square in a slot.
        internal static uint SlotHash(uint seed, int row, int col, long slot)
        {
            unchecked
            {
                uint hi = NoiseHash.LowBias32((uint)(slot >> 32) + 0x632BE5ABu);
                return NoiseHash.Hash3(row, col, (int)slot, seed ^ hi);
            }
        }

        // A value in [0, 1) number k of a hash.
        internal static double Draw(uint h, int k)
        {
            unchecked { return NoiseHash.Unit(NoiseHash.LowBias32(h + 0x9E3779B9u * (uint)(k + 1))); }
        }

        private static FlashEvent MakeEvent(LightningParams p, uint seed, long slot, ref LightningCell c, uint h)
        {
            var e = new FlashEvent();
            e.Slot = slot;
            e.Row = c.Row;
            e.Col = c.Col;
            e.Ut = (slot + Draw(h, 0)) * p.SlotS;
            // The place inside the square.
            e.LatDeg = c.LatDeg + (Draw(h, 1) - 0.5) * c.NorthDeg;
            e.LonDeg = c.LonDeg + (Draw(h, 2) - 0.5) * c.EastDeg;
            e.Kind = Draw(h, 3) < p.GroundShare ? FlashKind.Ground : FlashKind.Cloud;
            double b = Draw(h, 4);
            e.Bumps = b < 0.4 ? 1 : b < 0.75 ? 2 : 3;
            e.Seed = NoiseHash.LowBias32(h ^ 0xA5A5A5A5u);
            e.Strength = c.Strength;
            e.Level01 = c.Level01;
            e.TopM = c.TopM;
            double top = Math.Max(c.TopM, 500f);
            e.StartHeightM = (float)(top * (0.45 + 0.2 * Draw(h, 5)));
            e.CloudLightHeightM = (float)(top * (0.35 + 0.35 * Draw(h, 6)));
            return e;
        }

        internal static double Clamp01(double x) { return x < 0.0 ? 0.0 : x > 1.0 ? 1.0 : x; }

        internal static double SmoothStep(double a, double b, double x)
        {
            double t = Clamp01((x - a) / (b - a));
            return t * t * (3.0 - 2.0 * t);
        }
    }
}
