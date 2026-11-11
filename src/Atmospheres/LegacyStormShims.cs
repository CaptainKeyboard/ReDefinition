using System;

namespace ReDefinition.Atmospheres
{
    // What is left of the legacy storm cell scheduler after the weather generator replaced it (package C25 deleted StormCells, StormModel,
    // EveLayerStorm, EveCellLayer, EveStormMath and the legacy field bridge). StormEffect, StormLog and StormVolume still call a few of their
    // members; those files belong to the packages that adapt or delete them (C20, C21), so the members stay here as inert shims: the
    // scheduler makes no cell, the EVE layers inject nothing, and the constants that a test of the cell layer's configuration reads are
    // kept. The last caller package deletes this file together with the caller.

    [Obsolete("The cell scheduler is deleted; the weather generator (GeneratorField) is the source of weather. Removed with its last caller.")]
    internal static class StormCells
    {
        internal const int Capacity = 64;

        internal struct Cell
        {
            internal ulong Key;
            internal bool Forced;
            internal double Latitude, Longitude;
            internal double RadiusM;
            internal double Height, Envelope;
            internal double Heading, SpeedMs;
            internal double BaseAglM;
            internal double AgeSeconds;
            internal double DistanceM;

            internal double Strength
            {
                get { return Height * Envelope; }
            }
        }

        internal struct Rules
        {
            internal double BodyRadius, YearSeconds, ForcedPhase, WinterLine, Frequency, RadiusKm;
            internal int Seed;
            internal bool Scheduled;
            internal bool HasForced;
            internal double ForcedLatitude, ForcedLongitude, ForcedRadiusKm;
        }

        // No cell: the generator's systems are not cells of this scheduler.
        internal static int Collect(Cell[] cells, double universalTime, double latitude, double longitude, double rangeM, Rules rules)
        {
            return 0;
        }

        internal static double IntensityAt(Cell[] cells, int count, double latitude, double longitude, double bodyRadius)
        {
            return 0.0;
        }

        internal static int Nearest(Cell[] cells, int count, int max)
        {
            return 0;
        }

        // The great-circle distance between two places in degrees, on a sphere of that radius, in m.
        internal static double Distance(double latitudeA, double longitudeA, double latitudeB, double longitudeB, double bodyRadius)
        {
            double p1 = Geo.ToRad(latitudeA), p2 = Geo.ToRad(latitudeB);
            double dp = p2 - p1, dl = Geo.ToRad(longitudeB - longitudeA);
            double sp = Math.Sin(dp * 0.5), sl = Math.Sin(dl * 0.5);
            double a = sp * sp + Math.Cos(p1) * Math.Cos(p2) * sl * sl;
            return 2 * bodyRadius * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }
    }

    [Obsolete("The cell scheduler is deleted. Removed with its last caller.")]
    internal static class StormModel
    {
        // The intensity the legacy effect uses: the diagnostics value if it is from 0 to 1, otherwise the scheduled one when storms are on.
        internal static double Resolve(double forceStorm, bool stormsEnabled, double scheduled)
        {
            if (!double.IsNaN(forceStorm) && forceStorm >= 0) return Math.Max(0, Math.Min(1, forceStorm));
            if (!stormsEnabled || double.IsNaN(scheduled)) return 0;
            return Math.Max(0, Math.Min(1, scheduled));
        }

        // The n-th independent value of a key, in [0, 1) (a 64-bit mixing step).
        internal static double Unit(ulong key, int n)
        {
            unchecked
            {
                ulong z = key + 0x9E3779B97F4A7C15UL + ((ulong)n + 101UL) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z ^= z >> 31;
                return (z >> 11) / (double)(1UL << 53);
            }
        }
    }

    [Obsolete("The EVE storm layers are deleted: the cloud-map painter (C23) feeds EVE from the field. Removed with its last caller.")]
    internal static class EveLayerStorm
    {
        internal static bool Supplying
        {
            get { return false; }
        }

        internal static double BaseAboveGroundM(double groundAslM)
        {
            return Math.Max(120.0, EveCellLayer.BaseAslM - groundAslM);
        }

        internal static void Update(CelestialBody body, StormCells.Cell[] cells, int count, double cameraAltitudeM)
        {
        }

        internal static void Forget()
        {
        }
    }

    [Obsolete("The EVE storm layers are deleted: the cloud-map painter (C23) feeds EVE from the field. Removed with its last caller.")]
    internal static class EveCellLayer
    {
        // The names, ranges and base of the cell layer's configuration, read by the tests of the EVE coverage and the swirl.
        internal const string LayerName = "ReDefinition-Atmospheres-Storm";
        internal const string SnowLayerName = "ReDefinition-Atmospheres-Snow";
        internal const double RangeM = 150000;
        internal const double WideFromAltitudeM = 150000;
        internal const double VolumeLiveBelowM = 250000;
        internal const double BaseAslM = 500;

        internal static string Summary
        {
            get { return "deleted"; }
        }

        internal static bool HasVolumetric
        {
            get { return false; }
        }

        internal static bool OwnSnowField
        {
            get { return false; }
        }

        internal static double RangeFor(double cameraAltitudeM, double bodyRadiusM, bool map)
        {
            return map || cameraAltitudeM >= WideFromAltitudeM ? Math.PI * bodyRadiusM : RangeM;
        }

        internal static double BaseAboveGroundM(double groundAslM)
        {
            return Math.Max(120.0, BaseAslM - groundAslM);
        }

        internal static void Update(CelestialBody body, StormCells.Cell[] cells, int count, double cameraAltitudeM)
        {
        }

        internal static void Release()
        {
        }
    }
}
