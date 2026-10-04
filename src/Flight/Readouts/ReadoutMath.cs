using System;
using System.Globalization;

namespace ReDefinition.Flight.Readouts
{
    // The sums behind the readouts that need no game: free of Unity and KSP, tested
    // (ReadoutMathTests).
    internal static class ReadoutMath
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly string[] DistanceUnits = { "m", "km", "Mm", "Gm", "Tm" };

        // The phase angle, in degrees from 0 to 360, at which a transfer from a
        // circular orbit of radius `from` to one of radius `to` about the same body
        // meets its target: the target must lead by 180 degrees less what it covers
        // during the half ellipse's flight, pi * sqrt(((from + to) / 2)^3 / mu),
        // against its period, 2 pi * sqrt(to^3 / mu) (Hohmann). Negative for a
        // target lower down means it must trail; given here from 0 to 360.
        internal static double TransferAngle(double from, double to)
        {
            if (from <= 0.0 || to <= 0.0) return double.NaN;
            double angle = 180.0 * (1.0 - Math.Pow((from + to) / (2.0 * to), 1.5));
            return Wrap(angle);
        }

        // An angle into 0 .. 360.
        internal static double Wrap(double degrees)
        {
            double wrapped = degrees % 360.0;
            return wrapped < 0.0 ? wrapped + 360.0 : wrapped;
        }

        // How long until the phase angle, now `phase`, reaches `wanted`, when the
        // target moves `targetRate` and the vessel `ownRate` degrees a second, both
        // the same way round: the phase shrinks by the difference. NaN where the two
        // move at the same rate.
        internal static double TimeToPhase(double phase, double wanted, double ownRate, double targetRate)
        {
            double closing = ownRate - targetRate;
            if (Math.Abs(closing) < 1e-12) return double.NaN;
            double gap = closing > 0.0 ? Wrap(phase - wanted) : Wrap(wanted - phase);
            return gap / Math.Abs(closing);
        }

        // The last moment to light the engines to stop over the ground: at `speed`
        // metres a second towards it, with `acceleration` the engines give at full
        // thrust and `gravity` pulling down, the stopping distance
        // speed^2 / (2 (acceleration - gravity)); the burn takes
        // speed / (acceleration - gravity) and costs acceleration times that.
        // NaN for all three where the engines cannot beat gravity.
        internal static void SuicideBurn(double speed, double acceleration, double gravity, out double distance, out double seconds,
                                         out double deltaV)
        {
            double net = acceleration - gravity;
            if (net <= 0.0 || speed < 0.0)
            {
                distance = seconds = deltaV = double.NaN;
                return;
            }
            distance = speed * speed / (2.0 * net);
            seconds = speed / net;
            deltaV = acceleration * seconds;
        }

        // "85.4 km", "1.25 Mm", "412 m": a distance to three figures, as KSP writes
        // heights on its maneuver panel, with SI prefixes.
        internal static string Distance(double metres)
        {
            if (double.IsNaN(metres) || double.IsInfinity(metres)) return "-";
            double size = Math.Abs(metres);
            string[] units = DistanceUnits;
            int unit = 0;
            while (size >= 1000.0 && unit < units.Length - 1)
            {
                size /= 1000.0;
                metres /= 1000.0;
                unit++;
            }
            string format = size >= 100.0 ? "0" : size >= 10.0 ? "0.0" : "0.00";
            if (unit == 0) format = "0";
            return metres.ToString(format, Invariant) + " " + units[unit];
        }

        // "12.3 m/s", "1,234 m/s" without the comma.
        internal static string Speed(double metresPerSecond)
        {
            return Speed(metresPerSecond, " ");
        }

        // A speed with `separator` between the number and "m/s": KSP's speed
        // display writes none.
        internal static string Speed(double metresPerSecond, string separator)
        {
            if (double.IsNaN(metresPerSecond) || double.IsInfinity(metresPerSecond)) return "-";
            return ShownSpeed(metresPerSecond).ToString(Math.Abs(metresPerSecond) < 99.95 ? "0.0" : "0", Invariant) + separator + "m/s";
        }

        // A speed rounded as it is written, which alone decides the text: one
        // decimal below 100, none above; never -0.
        internal static double ShownSpeed(double metresPerSecond)
        {
            double shown = Math.Round(metresPerSecond, Math.Abs(metresPerSecond) < 99.95 ? 1 : 0);
            return shown == 0.0 ? 0.0 : shown;
        }

        // "12.3°".
        internal static string Angle(double degrees)
        {
            if (double.IsNaN(degrees) || double.IsInfinity(degrees)) return "-";
            double shown = Math.Round(degrees, 1);
            return (shown == 0.0 ? 0.0 : shown).ToString("0.0", Invariant) + "°";
        }

        // "T - 4m 35s" before, "T + 12s" after, as KSP's maneuver panel writes times.
        internal static string Countdown(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return "-";
            return (seconds >= 0.0 ? "T - " : "T + ") + Duration(Math.Abs(seconds));
        }

        // "4m 35s", "2h 05m", "3d 1h", "42s", in KSP's six-hour days.
        internal static string Duration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return "-";
            long whole = (long)Math.Round(Math.Abs(seconds));
            long days = whole / 21600, hours = whole % 21600 / 3600, minutes = whole % 3600 / 60, secs = whole % 60;
            string sign = seconds < 0.0 && whole > 0 ? "-" : "";
            if (days > 0) return sign + days + "d " + hours + "h";
            if (hours > 0) return sign + hours + "h " + minutes.ToString("00", Invariant) + "m";
            if (minutes > 0) return sign + minutes + "m " + secs.ToString("00", Invariant) + "s";
            return sign + secs + "s";
        }
    }
}
