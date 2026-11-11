using System;

namespace ReDefinition.Atmospheres
{
    // The parameter envelope the simulation and the looks were tuned for. A body outside it is set to Broken(envelope) before the
    // simulation starts, instead of running with numbers nobody tried. A body's node can accept it with the key acceptOutsideEnvelope.
    internal static class BodyEnvelope
    {
        // The radius range in metres (small moons to scaled-up systems; a 10x scale like RSS is outside).
        internal const double MinRadiusM = 50000.0;
        internal const double MaxRadiusM = 2500000.0;
        // The atmosphere depth in metres (a thin shell has no room for decks) and the surface pressure in kPa.
        internal const double MinAtmosphereDepthM = 10000.0;
        internal const double MaxAtmosphereDepthM = 1000000.0;
        internal const double MaxSurfacePressureKPa = 20000.0;
        // The length of a day in seconds.
        internal const double MinDaySeconds = 600.0;
        internal const double MaxDaySeconds = 3.0e7;

        // The reason a body with an atmosphere is outside the envelope, or null where it is inside. A body without an atmosphere is not
        // checked: it is Off whatever its size.
        internal static string Violation(BodyFacts facts)
        {
            if (facts == null) return "no facts";
            if (!facts.HasAtmosphere) return null;
            if (!Within(facts.RadiusM, MinRadiusM, MaxRadiusM)) return "radius " + Round(facts.RadiusM) + " m is outside " + Round(MinRadiusM) + " to " + Round(MaxRadiusM) + " m";
            if (!Within(facts.AtmosphereDepthM, MinAtmosphereDepthM, MaxAtmosphereDepthM))
                return "atmosphere depth " + Round(facts.AtmosphereDepthM) + " m is outside " + Round(MinAtmosphereDepthM) + " to " + Round(MaxAtmosphereDepthM) + " m";
            if (facts.SeaLevelPressureKPa < 0 || facts.SeaLevelPressureKPa > MaxSurfacePressureKPa || double.IsNaN(facts.SeaLevelPressureKPa))
                return "surface pressure " + Round(facts.SeaLevelPressureKPa) + " kPa is outside 0 to " + Round(MaxSurfacePressureKPa) + " kPa";
            if (facts.DaySeconds > 0 && !Within(facts.DaySeconds, MinDaySeconds, MaxDaySeconds))
                return "day length " + Round(facts.DaySeconds) + " s is outside " + Round(MinDaySeconds) + " to " + Round(MaxDaySeconds) + " s";
            return null;
        }

        private static bool Within(double value, double min, double max)
        {
            return !double.IsNaN(value) && value >= min && value <= max;
        }

        private static string Round(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
