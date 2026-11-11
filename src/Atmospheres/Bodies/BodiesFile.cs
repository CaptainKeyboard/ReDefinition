using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // What the player set for one body: the year length and how often weather happens. Section BODY of PluginData/bodies.cfg.
    internal sealed class BodyPlayerOptions
    {
        internal const double MinYearFactor = 0.01;
        internal const double MaxYearFactor = 10.0;
        internal const double MinFrequency = 0.0;
        internal const double MaxFrequency = 4.0;

        internal static readonly BodyPlayerOptions Default = new BodyPlayerOptions(1.0, 1.0);

        internal BodyPlayerOptions(double yearFactor, double frequency)
        {
            YearFactor = Clamp(yearFactor, MinYearFactor, MaxYearFactor, 1.0);
            Frequency = Clamp(frequency, MinFrequency, MaxFrequency, 1.0);
        }

        // Key yearFactor: multiplies the body's year, in addition to the node's yearFactor and the global setting.
        internal double YearFactor { get; }
        // Key frequency: multiplies how often weather forms on the body (1 is the body's own rate, 0 none).
        internal double Frequency { get; }

        private static double Clamp(double value, double min, double max, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;
            return Math.Min(max, Math.Max(min, value));
        }
    }

    // PluginData/bodies.cfg: the per-body choices of the player, one BODY node per body (key name, yearFactor, frequency). A missing
    // or damaged file, a node without a name, and a value that is not a number all read as the defaults. The settings window of the
    // atmospheres (D36) writes through ToNode.
    internal static class BodiesFile
    {
        internal const string FileName = "bodies.cfg";
        internal const string SectionName = "BODY";

        // The options by body name (ordinal, case-insensitive) from a loaded file; the warnings say what was ignored.
        internal static Dictionary<string, BodyPlayerOptions> Parse(ConfigNode root, List<string> warnings)
        {
            var result = new Dictionary<string, BodyPlayerOptions>(StringComparer.OrdinalIgnoreCase);
            if (root == null) return result;
            foreach (ConfigNode section in root.GetNodes(SectionName))
            {
                string name = section.GetValue("name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    if (warnings != null) warnings.Add("bodies.cfg: a BODY node without a name is ignored");
                    continue;
                }
                var reader = new BodyNodeReader(section, "bodies.cfg.BODY[" + name.Trim() + "]", warnings);
                result[name.Trim()] = new BodyPlayerOptions(reader.Double("yearFactor", 1.0, BodyPlayerOptions.MinYearFactor, BodyPlayerOptions.MaxYearFactor),
                    reader.Double("frequency", 1.0, BodyPlayerOptions.MinFrequency, BodyPlayerOptions.MaxFrequency));
            }
            return result;
        }

        // The file of the mod, read once.
        internal static Dictionary<string, BodyPlayerOptions> Read(List<string> warnings)
        {
            return ModSettingsFile.Read(AtmospheresSettings.Mod, FileName, root => Parse(root, warnings),
                new Dictionary<string, BodyPlayerOptions>(StringComparer.OrdinalIgnoreCase));
        }

        // The node of a set of options, for the writer: one BODY node per body given.
        internal static ConfigNode ToNode(IDictionary<string, BodyPlayerOptions> options)
        {
            var root = new ConfigNode();
            if (options == null) return root;
            foreach (KeyValuePair<string, BodyPlayerOptions> pair in options)
            {
                if (pair.Value == null) continue;
                ConfigNode section = root.AddNode(SectionName);
                section.AddValue("name", pair.Key);
                section.AddValue("yearFactor", pair.Value.YearFactor.ToString("R", CultureInfo.InvariantCulture));
                section.AddValue("frequency", pair.Value.Frequency.ToString("R", CultureInfo.InvariantCulture));
            }
            return root;
        }
    }
}
