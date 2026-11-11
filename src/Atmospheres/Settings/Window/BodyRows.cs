using System;
using System.Collections.Generic;
using System.IO;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // One body in the window's list: its status, how far its simulation is along, and what the player set for it.
    internal sealed class BodyRow
    {
        internal string Name;
        internal int Index;
        internal BodyStatusKind Status;
        internal string StatusReason = "";
        internal FieldReadiness Readiness;
        internal double YearFactor = 1.0;
        internal double Frequency = 1.0;

        // Whether weather runs on the body in any form; the others are listed apart.
        internal bool Active
        {
            get { return Status != BodyStatusKind.Off; }
        }

        // "Off (NoAtmosphere)", "Cover Only", "Full", "Broken (reason)".
        internal string StatusText
        {
            get
            {
                string name = Status == BodyStatusKind.CoverOnly ? "Cover Only" : Status.ToString();
                return StatusReason.Length == 0 ? name : name + " (" + StatusReason + ")";
            }
        }

        // The simulation's state in words; a body that has no simulation says so.
        internal string ReadinessText
        {
            get
            {
                if (Status == BodyStatusKind.Off) return "No simulation";
                if (Status == BodyStatusKind.CoverOnly) return "Cover only, no simulation";
                switch (Readiness)
                {
                    case FieldReadiness.Ready: return "Simulation ready";
                    case FieldReadiness.CatchingUp: return "Simulation catching up";
                    case FieldReadiness.Seeding: return "Simulation starting";
                    default: return "Simulation failed";
                }
            }
        }
    }

    // The bodies of the window: the rows from the climates and the player's file bodies.cfg, and the writing of that file. The year factor and
    // the frequency of a body are the player's own, on top of the body's configuration and the global setting; they apply when the
    // body is next entered or the scene reloaded, as the climate of a body is read once.
    internal static class BodyRows
    {
        // The rows of all bodies given, active ones first, each group in the order given. The options are by body name.
        internal static List<BodyRow> Build(IEnumerable<BodyClimate> climates, IDictionary<string, BodyPlayerOptions> options,
            Func<int, FieldReadiness> readiness)
        {
            var active = new List<BodyRow>();
            var inactive = new List<BodyRow>();
            if (climates == null) return active;
            foreach (BodyClimate climate in climates)
            {
                if (climate == null) continue;
                BodyPlayerOptions own;
                if (options == null || !options.TryGetValue(climate.Name, out own)) own = BodyPlayerOptions.Default;
                var row = new BodyRow
                {
                    Name = climate.Name,
                    Index = climate.Index,
                    Status = climate.Status.Kind,
                    StatusReason = climate.Status.Reason,
                    Readiness = readiness != null ? readiness(climate.Index) : FieldReadiness.Seeding,
                    YearFactor = own.YearFactor,
                    Frequency = own.Frequency
                };
                (row.Active ? active : inactive).Add(row);
            }
            active.AddRange(inactive);
            return active;
        }

        // The options after one body's year factor and frequency are set; a body brought back to 1 and 1 has no entry. The values are
        // brought into their ranges.
        internal static Dictionary<string, BodyPlayerOptions> With(IDictionary<string, BodyPlayerOptions> options, string body,
            double yearFactor, double frequency)
        {
            var result = new Dictionary<string, BodyPlayerOptions>(StringComparer.OrdinalIgnoreCase);
            if (options != null)
            {
                foreach (KeyValuePair<string, BodyPlayerOptions> pair in options) result[pair.Key] = pair.Value;
            }
            if (string.IsNullOrWhiteSpace(body)) return result;
            var set = new BodyPlayerOptions(yearFactor, frequency);
            if (set.YearFactor == 1.0 && set.Frequency == 1.0) result.Remove(body.Trim());
            else result[body.Trim()] = set;
            return result;
        }

        // The player's options now, from bodies.cfg.
        internal static Dictionary<string, BodyPlayerOptions> Read()
        {
            return BodiesFile.Read(null);
        }

        // Writes the options into bodies.cfg and makes the body provider read them again. False where the file could not be written.
        internal static bool Write(IDictionary<string, BodyPlayerOptions> options)
        {
            return Write(ModFolder.PluginFile(AtmospheresSettings.Mod, BodiesFile.FileName), options, true);
        }

        // As Write, to a given path (the tests); the provider is made to read again only where asked.
        internal static bool Write(string path, IDictionary<string, BodyPlayerOptions> options, bool refreshProvider)
        {
            bool written = ModSettingsFile.Write(path, root =>
            {
                ConfigNode nodes = BodiesFile.ToNode(options);
                foreach (ConfigNode node in nodes.GetNodes(BodiesFile.SectionName)) root.AddNode(node);
            });
            if (written && refreshProvider)
            {
                var provider = Bodies.Provider as BodyConfigProvider;
                if (provider != null) provider.Reset();
            }
            return written;
        }
    }
}
