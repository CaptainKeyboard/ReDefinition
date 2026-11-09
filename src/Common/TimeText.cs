using System;
using System.Globalization;

namespace ReDefinition.Core
{
    // Durations as the flight and the planner write them.
    internal static class TimeText
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        // 1h 02m 03s, 2m 05s, 7.4s; days as KSP counts them are not needed for a
        // burn.
        internal static string Time(double seconds)
        {
            double value = Clean(seconds);
            if (value < 60.0) return value.ToString("0.0", Invariant) + "s";
            long whole = (long)Math.Round(value);
            long hours = whole / 3600;
            long minutes = whole % 3600 / 60;
            long secs = whole % 60;
            if (hours > 0) return hours + "h " + minutes.ToString("00", Invariant) + "m " + secs.ToString("00", Invariant) + "s";
            return minutes + "m " + secs.ToString("00", Invariant) + "s";
        }

        // Not a number, infinite and negative zero all read as 0: a stage without
        // thrust has no burn time rather than an infinite one.
        private static double Clean(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0.0;
            return value == 0.0 ? 0.0 : value;
        }
    }
}
