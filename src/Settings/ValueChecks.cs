using System;
using System.Globalization;

namespace ReDefinition.Settings
{
    // What a toggle or a slider takes as a value, asked of text. A module's setting (ModuleSetting.Refusal) and
    // a registered mod's setting (ProfileApplier.Refusal) are judged by the same rules, and each gives
    // the reason in its own words where they differ. Callable without the game.
    internal static class ValueChecks
    {
        // Null where the text is True or False in either case; otherwise why not.
        public static string Toggle(string value)
        {
            bool on;
            return bool.TryParse(value, out on) ? null : "not True or False";
        }

        // Null where the text is a number inside the range, a whole one where the slider snaps. Else why not.
        // "NaN" and "Infinity" parse, and NaN passes every comparison, so they are refused first with `notFinite`.
        public static string Slider(string value, float min, float max, bool wholeNumbers, string notFinite)
        {
            double number;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                return "not a number";
            if (double.IsNaN(number) || double.IsInfinity(number)) return notFinite;
            if (number < min - 1e-6 || number > max + 1e-6)
                return "outside " + min.ToString(CultureInfo.InvariantCulture) + " .. " + max.ToString(CultureInfo.InvariantCulture);
            // The slider would round it on its first frame, and the row turn Custom by itself.
            if (wholeNumbers && Math.Abs(number - Math.Round(number)) > 1e-6) return "not a whole number";
            return null;
        }
    }
}
