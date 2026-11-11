using System;

namespace ReDefinition.Atmospheres
{
    // What is left of the legacy storm cell scheduler after the weather generator replaced it (package C25 deleted StormCells, StormModel,
    // EveLayerStorm, EveCellLayer, EveStormMath and the legacy field bridge). StormEffect, StormLog and StormVolume still call a few of their
    // members; those files belong to the packages that adapt or delete them (C20, C21, C23), so the members stay here as inert shims: the
    // scheduler makes no cell, the EVE layers inject nothing, and the constants and the range function that two tests of the EVE coverage and
    // the swirl read are kept. This deviates from the plan rule "no inert interim copy" for EveLayerStorm and EveCellLayer because the callers
    // are outside the ownership of C25. Who removes what:
    //
    //   shim                                  caller                                          removed by
    //   StormCells (type, Cell, Rules,        StormEffect, StormLog                            C20 (StormEffect, StormLog)
    //     Collect, IntensityAt, Nearest,      StormVolume                                      C21 (StormVolume)
    //     Distance)
    //   StormModel (Resolve, Unit)            StormEffect                                      C20
    //                                         StormVolume                                      C21
    //   EveLayerStorm (all members)           StormEffect, StormVolume                         C20, C21
    //   EveCellLayer (Update, Release,        StormEffect, StormVolume                         C20, C21
    //     Summary, HasVolumetric, OwnSnowField, BaseAboveGroundM)
    //   EveCellLayer (LayerName, SnowLayerName, RangeM, WideFromAltitudeM, VolumeLiveBelowM, BaseAslM, RangeFor)
    //                                         EveCoverageTests, EveSwirlTests                  C23 (the two test files)
    //
    // Every member that a caller can reach is inert: it returns a neutral value and has no logic of the deleted classes. RangeFor and the
    // constants are the one exception, kept for the two tests of C23. The package that removes the last caller deletes this file.

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

        // No cell exists, so no place is near one.
        internal static double Distance(double latitudeA, double longitudeA, double latitudeB, double longitudeB, double bodyRadius)
        {
            return double.PositiveInfinity;
        }
    }

    [Obsolete("The cell scheduler is deleted. Removed with its last caller.")]
    internal static class StormModel
    {
        // No storm intensity: the cell scheduler is deleted, the field's level is the source.
        internal static double Resolve(double forceStorm, bool stormsEnabled, double scheduled)
        {
            return 0.0;
        }

        // No cell has a key, so nothing asks for its values.
        internal static double Unit(ulong key, int n)
        {
            return 0.0;
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
            return 0.0;
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
            return 0.0;
        }

        internal static void Update(CelestialBody body, StormCells.Cell[] cells, int count, double cameraAltitudeM)
        {
        }

        internal static void Release()
        {
        }
    }
}
