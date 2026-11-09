using System;
using System.Collections.Generic;
using System.Globalization;

namespace ReDefinition.Engineer
{
    // How the planner writes its numbers: in the invariant culture, with
    // thousands separators, and in the units KSP's own delta-v app uses -- m/s, kN,
    // tonnes, seconds. Free of Unity, tested.
    internal static class EngineerFormat
    {
        // Every number is written in it, whatever the player's system culture.
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        // Metres per second, whole, with thousands separators.
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

        // Two places after the point, as for a thrust-to-weight ratio.
        internal static string Ratio(double value)
        {
            return Clean(value).ToString("0.00", Invariant);
        }

        // Kilometres, as an altitude is given on the map: 85 km, 12.4 km, 1,234 km.
        internal static string Distance(double metres)
        {
            double km = Clean(metres) / 1000.0;
            string text = km.ToString(Math.Abs(km) >= 100.0 ? "#,0" : "#,0.#", Invariant);
            // A value that rounds to nothing has no sign.
            if (text == "-0") text = "0";
            return text + " km";
        }

        // A text broken at spaces into lines of at most `chars` characters; a word longer than a line is cut. No text gives no lines.
        internal static List<string> Wrap(string text, int chars)
        {
            List<string> lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;
            chars = Math.Max(chars, 8);
            string line = "";
            foreach (string word in text.Split(' '))
            {
                string rest = word;
                while (rest.Length > chars)
                {
                    if (line.Length > 0) { lines.Add(line); line = ""; }
                    lines.Add(rest.Substring(0, chars));
                    rest = rest.Substring(chars);
                }
                if (rest.Length == 0) continue;
                if (line.Length == 0) line = rest;
                else if (line.Length + 1 + rest.Length <= chars) line += " " + rest;
                else { lines.Add(line); line = rest; }
            }
            if (line.Length > 0) lines.Add(line);
            return lines;
        }

        // Whole seconds, as KSP gives specific impulse.
        internal static string Isp(double seconds)
        {
            return Clean(seconds).ToString("0", Invariant) + " s";
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
