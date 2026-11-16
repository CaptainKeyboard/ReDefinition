using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using AtmoSim.Core;

namespace ReDefinition.Atmospheres
{
    // The generator levers the cloud viewer sets for a test (cell instability, updraft scale and the like): fields of the generator's parameter set by name, applied after the body's
    // own LEVERS node whenever a state is built (GeneratorBodies.Build). They act only on the next build of a state, which the viewer asks for with its Rebuild Weather button
    // (WeatherRebuild); they are never written to a file, and the viewer clears them when it closes. Empty, they change nothing.
    internal static class GeneratorViewerOverrides
    {
        private static readonly Dictionary<string, double> Values = new Dictionary<string, double>(StringComparer.Ordinal);

        // Rises each time the set changes.
        internal static int Stamp { get; private set; }

        internal static int Count
        {
            get { return Values.Count; }
        }

        internal static bool TryGet(string field, out double value)
        {
            return Values.TryGetValue(field, out value);
        }

        // Replaces the set; true where it was not the same.
        internal static bool Replace(IReadOnlyDictionary<string, double> next)
        {
            bool same = next != null && next.Count == Values.Count;
            if (same)
            {
                foreach (KeyValuePair<string, double> pair in next)
                {
                    double v;
                    if (!Values.TryGetValue(pair.Key, out v) || v != pair.Value) { same = false; break; }
                }
            }
            else if (next == null && Values.Count == 0) same = true;
            if (same) return false;
            Values.Clear();
            if (next != null) foreach (KeyValuePair<string, double> pair in next) Values[pair.Key] = pair.Value;
            Stamp++;
            return true;
        }

        internal static void Clear()
        {
            Replace(null);
        }

        // The set put into a parameter set (a name that is no field is skipped; the viewer offers only fields of the generator).
        internal static void Apply(BodyParams p)
        {
            Apply(p, Values);
        }

        // The same from a snapshot (Snapshot), so that a worker thread never reads the set the viewer changes on the main thread.
        internal static void Apply(BodyParams p, IReadOnlyDictionary<string, double> levers)
        {
            if (p == null || levers == null || levers.Count == 0) return;
            GeneratorBodies.ApplyLevers(p, levers, null);
        }

        // A copy of the set now (main thread).
        internal static Dictionary<string, double> Snapshot()
        {
            return new Dictionary<string, double>(Values, StringComparer.Ordinal);
        }

        // The value of a field of a parameter set as a number (a switch as 0 or 1); NaN for a field that is none of the number types.
        internal static double Read(BodyParams p, string field)
        {
            FieldInfo f = p == null ? null : typeof(BodyParams).GetField(field, BindingFlags.Public | BindingFlags.Instance);
            if (f == null) return double.NaN;
            object v = f.GetValue(p);
            if (v is double) return (double)v;
            if (v is int) return (int)v;
            if (v is bool) return (bool)v ? 1.0 : 0.0;
            return double.NaN;
        }

        internal static string Describe()
        {
            var parts = new List<string>();
            foreach (KeyValuePair<string, double> pair in Values) parts.Add(pair.Key + " " + pair.Value.ToString("0.#####", CultureInfo.InvariantCulture));
            return parts.Count == 0 ? "none" : string.Join(", ", parts.ToArray());
        }
    }
}
