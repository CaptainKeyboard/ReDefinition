using System;
using System.Globalization;

namespace ReDefinition.Core
{
    // The game's command line as ReDefinition reads it: the -redefinition switches that start a probe, a
    // profiler or a test scene, none of which a player gives. The line is read when asked, never cached:
    // Environment.GetCommandLineArgs makes a new array each time, so what runs every frame reads it once into a field.
    // Each reader has a form that takes the arguments, for the tests, and one that reads the game's own.
    internal static class CommandLine
    {
        // Whether the switch is on the command line, spelled as given.
        internal static bool Has(string name)
        {
            return Has(Environment.GetCommandLineArgs(), name, false);
        }

        // Whether the switch is on the command line; with ignoreCase in any case.
        internal static bool Has(string name, bool ignoreCase)
        {
            return Has(Environment.GetCommandLineArgs(), name, ignoreCase);
        }

        // The same on the arguments given.
        internal static bool Has(string[] args, string name, bool ignoreCase)
        {
            return IndexOf(args, name, ignoreCase) >= 0;
        }

        // The word after the switch, spelled as given; null where the switch is not given or is the last word.
        internal static string After(string name)
        {
            return After(Environment.GetCommandLineArgs(), name, false);
        }

        // The word after the switch; with ignoreCase the switch may be in any case.
        internal static string After(string name, bool ignoreCase)
        {
            return After(Environment.GetCommandLineArgs(), name, ignoreCase);
        }

        // The same on the arguments given.
        internal static string After(string[] args, string name, bool ignoreCase)
        {
            int at = IndexOf(args, name, ignoreCase);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        }

        // The whole number after the switch; fallback where the switch is not given or is the last word. A word that
        // is not a whole number throws a FormatException, as the probes' int.Parse always did: a measurement
        // run with a mistyped value is better stopped than run with another.
        internal static int Int(string name, int fallback, bool ignoreCase = false)
        {
            return Int(Environment.GetCommandLineArgs(), name, fallback, ignoreCase);
        }

        // The same on the arguments given.
        internal static int Int(string[] args, string name, int fallback, bool ignoreCase = false)
        {
            string word = After(args, name, ignoreCase);
            return word == null ? fallback : int.Parse(word, CultureInfo.InvariantCulture);
        }

        // The number after the switch; fallback where the switch is not given, is the last word or is followed by a
        // word that is not a number.
        internal static float Float(string name, float fallback, bool ignoreCase = false)
        {
            return Float(Environment.GetCommandLineArgs(), name, fallback, ignoreCase);
        }

        // The same on the arguments given.
        internal static float Float(string[] args, string name, float fallback, bool ignoreCase = false)
        {
            float value;
            return float.TryParse(After(args, name, ignoreCase), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value : fallback;
        }

        // Where the switch stands in the arguments; -1 for nowhere.
        private static int IndexOf(string[] args, string name, bool ignoreCase)
        {
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], name, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return i;
            return -1;
        }
    }
}
