using System;
using System.Globalization;

namespace ReDefinition.Engineer
{
    // How the planner writes its numbers: in the invariant culture, with
    // thousands separators, and in the units KSP's own delta-v app uses -- m/s, kN,
    // tonnes, seconds. Free of Unity, tested.
    internal static class EngineerFormat
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        internal static string Speed(double metresPerSecond)
        {
            return Clean(metresPerSecond).ToString("#,0", Invariant) + " m/s";
        }

        // Kilonewtons, as KSP gives thrust.
        internal static string Force(double kiloNewtons)
        {
            double value = Clean(kiloNewtons);
            if (value >= 1000.0) return (value / 1000.0).ToString("#,0.00", Invariant) + " MN";
            return value.ToString(value >= 100.0 ? "#,0.0" : "0.00", Invariant) + " kN";
        }

        // Tonnes, as KSP gives mass; under one tonne in kilograms.
        internal static string Mass(double tonnes)
        {
            double value = Clean(tonnes);
            if (value < 1.0) return (value * 1000.0).ToString("#,0", Invariant) + " kg";
            return value.ToString(value >= 100.0 ? "#,0.0" : "#,0.00", Invariant) + " t";
        }

        // Kilonewton-metres.
        internal static string Torque(double kiloNewtonMetres)
        {
            return Clean(kiloNewtonMetres).ToString("#,0.00", Invariant) + " kNm";
        }

        internal static string Ratio(double value)
        {
            return Clean(value).ToString("0.00", Invariant);
        }

        internal static string Isp(double seconds)
        {
            return Clean(seconds).ToString("0", Invariant) + " s";
        }

        internal static string Cost(double funds)
        {
            return Clean(funds).ToString("#,0", Invariant);
        }

        internal static string Count(int count)
        {
            return count.ToString("#,0", Invariant);
        }

        internal static string Amount(double amount)
        {
            double value = Clean(amount);
            return value.ToString(value >= 100.0 ? "#,0" : "0.0", Invariant);
        }

        internal static string Distance(double metres)
        {
            double value = Clean(metres);
            if (value >= 1000.0) return (value / 1000.0).ToString("#,0.0", Invariant) + " km";
            return value.ToString("0", Invariant) + " m";
        }

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
