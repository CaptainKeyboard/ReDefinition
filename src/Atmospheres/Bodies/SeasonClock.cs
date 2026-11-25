using System;

namespace ReDefinition.Atmospheres
{
    // The season phase from the universal time. KSP has no axial tilt of its own, so the season is an artificial phase of the body's
    // orbit: one year of the orbit, scaled by the year factors. Phase 0 is the northern midwinter, 0.5 the northern midsummer; the
    // southern hemisphere runs a configured share of the year apart. The year, the tilt and the hemisphere offset of a body live in
    // its SeasonModel; the phase is an input of the weather field's insolation, not a second climate.
    internal static partial class SeasonClock
    {
        // The length of a season year in seconds: the orbit period times the factor. A factor that is not a positive finite number counts as 1.
        internal static double YearSeconds(double orbitPeriod, double yearLengthFactor)
        {
            double factor = yearLengthFactor > 0 && !double.IsInfinity(yearLengthFactor) ? yearLengthFactor : 1;
            return orbitPeriod * factor;
        }

        // The phase in [0, 1) at that time. A year length that is not positive, and a time that is not a finite number, give 0.
        internal static double Phase(double universalTime, double yearSeconds)
        {
            if (!(yearSeconds > 0) || double.IsInfinity(yearSeconds) || double.IsNaN(universalTime) || double.IsInfinity(universalTime)) return 0;
            return Wrap(universalTime / yearSeconds);
        }

        // The phase of the body's own clock at that time: the model's kind decides (orbit year, constant, none, fixed). A body without
        // seasons has phase 0.
        internal static double Phase(double universalTime, SeasonModel model)
        {
            if (model == null) return 0;
            switch (model.Clock)
            {
                case SeasonClockKind.Orbit:
                    return Phase(universalTime, model.YearSeconds);
                case SeasonClockKind.Fixed:
                    return Wrap(model.FixedPhase);
                default:
                    return 0;
            }
        }

        // The phase of a hemisphere: the northern one is the clock's, the southern one the model's offset later.
        internal static double HemispherePhase(double phase, bool north, SeasonModel model)
        {
            return north ? Wrap(phase) : Wrap(phase + (model == null ? 0.5 : model.EffectiveHemisphereOffset));
        }

        // The phase of a hemisphere with the default offset of half a year.
        internal static double HemispherePhase(double phase, bool north)
        {
            return north ? Wrap(phase) : Wrap(phase + DefaultHemisphereOffset);
        }

        // How far apart two phases are, as a part of the year, from 0 to 0.5: the year wraps, so 0.99 and 0.01 are 0.02 apart.
        internal static double PhaseDistance(double a, double b)
        {
            double d = Math.Abs(Wrap(a) - Wrap(b));
            return d > 0.5 ? 1 - d : d;
        }

        // A value brought into [0, 1). A value that is not a finite number gives 0.
        internal static double Wrap(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            double wrapped = value - Math.Floor(value);
            return wrapped >= 1 ? 0 : wrapped;
        }

        // The offset of the southern year against the northern one where nothing else is configured: half a year.
        internal const double DefaultHemisphereOffset = 0.5;
    }
}
