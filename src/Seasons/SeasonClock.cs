using System;

namespace ReDefinition.Seasons
{
    // The season phase of Kerbin from the universal time. KSP has no axial tilt, so the season is an artificial phase of Kerbin's
    // orbit: one year of the orbit, scaled by the player's year length factor. Phase 0 is the northern midwinter, 0.5 the northern
    // midsummer; the southern hemisphere runs half a year apart.
    internal static class SeasonClock
    {
        // The body the seasons apply to.
        internal const string BodyName = "Kerbin";

        // The phase of the southern hemisphere, in years, against the northern one.
        internal const double SouthernOffset = 0.5;

        // Whether the seasons apply to that body.
        internal static bool Applies(string bodyName)
        {
            return string.Equals(bodyName, BodyName, StringComparison.Ordinal);
        }

        // The length of a season year in seconds: the orbit period times the factor. A factor that is not a positive finite number counts as 1.
        internal static double YearSeconds(double orbitPeriod, double yearLengthFactor)
        {
            double factor = yearLengthFactor > 0 && !double.IsInfinity(yearLengthFactor) ? yearLengthFactor : 1;
            return orbitPeriod * factor;
        }

        // The phase in [0, 1) at that time. A year length that is not positive gives 0.
        internal static double Phase(double universalTime, double yearSeconds)
        {
            if (!(yearSeconds > 0) || double.IsInfinity(yearSeconds) || double.IsNaN(universalTime)) return 0;
            return Wrap(universalTime / yearSeconds);
        }

        // The phase of a hemisphere: the northern one is the clock's, the southern one half a year later.
        internal static double HemispherePhase(double phase, bool north)
        {
            return north ? Wrap(phase) : Wrap(phase + SouthernOffset);
        }

        // A value brought into [0, 1).
        internal static double Wrap(double value)
        {
            double wrapped = value - Math.Floor(value);
            return wrapped >= 1 ? 0 : wrapped;
        }

        // The phase now, from the game's time and Kerbin's orbit; null where the game has no Kerbin yet.
        internal static double? Current(double yearLengthFactor)
        {
            CelestialBody kerbin = FlightGlobals.Bodies == null ? null : FlightGlobals.GetBodyByName(BodyName);
            if (kerbin == null || kerbin.orbit == null) return null;
            return Phase(Planetarium.GetUniversalTime(), YearSeconds(kerbin.orbit.period, yearLengthFactor));
        }
    }
}
