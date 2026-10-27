using System;
using System.Collections.Generic;

namespace ReDefinition.Core
{
    // An enum's names and values from tables made once. The settings rows turn an enum into its name and
    // back in every frame their window is open, and Enum.Parse and Enum.ToString look the name up by
    // reflection on every call. The answers are the same: a name the tables lack goes to
    // Enum.Parse or Enum.TryParse, so a number or a bad name behaves as before.
    internal static class EnumNames<T> where T : struct
    {
        // The names by value, and the values by name -- once with the name's case counted, once without.
        private static readonly Dictionary<T, string> Names = new Dictionary<T, string>();
        private static readonly Dictionary<string, T> Exact = new Dictionary<string, T>(StringComparer.Ordinal);
        private static readonly Dictionary<string, T> IgnoringCase = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

        static EnumNames()
        {
            foreach (T value in (T[])Enum.GetValues(typeof(T)))
            {
                string name = value.ToString();
                // Two names of one value: the first one stays its name, as ToString gives one of them.
                if (!Names.ContainsKey(value)) Names[value] = name;
                Exact[name] = value;
                IgnoringCase[name] = value;
            }
        }

        // The value's name, as ToString gives it.
        public static string Name(T value)
        {
            string name;
            return Names.TryGetValue(value, out name) ? name : value.ToString();
        }

        // The value a name stands for, its case aside if asked; an exception for a name that is none, as
        // Enum.Parse has it.
        public static T Parse(string name, bool ignoreCase)
        {
            T value;
            if (name != null && (ignoreCase ? IgnoringCase : Exact).TryGetValue(name, out value)) return value;
            return (T)Enum.Parse(typeof(T), name, ignoreCase);
        }

        // As Enum.TryParse has it, with the case counted.
        public static bool TryParse(string name, out T value)
        {
            if (name != null && Exact.TryGetValue(name, out value)) return true;
            return Enum.TryParse(name, out value);
        }
    }
}
