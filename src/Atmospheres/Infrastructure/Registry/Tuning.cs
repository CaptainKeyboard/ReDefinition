using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace ReDefinition.Atmospheres
{
    // The values behind Tuning: the numbers read from the ATMO_TUNING nodes plus an override layer, merged into one immutable table
    // that is swapped at a frame boundary. Separate from Tuning so that it is tested without the game.
    internal sealed class TuningTable
    {
        private readonly Dictionary<string, float> loaded = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> overrides = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly object gate = new object();
        private Dictionary<string, float> effective = new Dictionary<string, float>(StringComparer.Ordinal);
        private bool dirty;
        private int frame = int.MinValue;
        private long version;

        // Rises each time a new table is swapped in.
        internal long Version
        {
            get { return Interlocked.Read(ref version); }
        }

        // The value of a key, or the compiled default where neither the nodes nor an override name it. The table is the one swapped in at
        // the last BeginFrame, so every caller in one frame sees the same numbers.
        internal float Get(string key, float compiledDefault)
        {
            Dictionary<string, float> table = Volatile.Read(ref effective);
            float value;
            return table.TryGetValue(key, out value) ? value : compiledDefault;
        }

        // Takes the values of the nodes (key = number lines); a line that is not a number is skipped. Later nodes win. They replace the
        // values loaded before.
        internal void Load(IEnumerable<ConfigNode> nodes)
        {
            lock (gate)
            {
                loaded.Clear();
                if (nodes != null)
                {
                    foreach (ConfigNode node in nodes)
                    {
                        if (node == null) continue;
                        foreach (ConfigNode.Value v in node.values)
                        {
                            float number;
                            if (TryParse(v.value, out number)) loaded[v.name] = number;
                        }
                    }
                }
                dirty = true;
            }
        }

        // Sets a value that wins over the nodes (a series variant changes look constants this way). It takes effect at the next BeginFrame.
        internal void SetOverride(string key, float value)
        {
            lock (gate)
            {
                overrides[key] = value;
                dirty = true;
            }
        }

        // Removes every override; it takes effect at the next BeginFrame.
        internal void ClearOverrides()
        {
            lock (gate)
            {
                if (overrides.Count == 0) return;
                overrides.Clear();
                dirty = true;
            }
        }

        // Ends the frame boundary: when anything changed since the last call, a new table is swapped in. Called once per frame; a repeated
        // call in the same frame does nothing.
        internal void BeginFrame(int frameCount)
        {
            lock (gate)
            {
                if (frameCount == frame) return;
                frame = frameCount;
                if (!dirty) return;
                var next = new Dictionary<string, float>(loaded, StringComparer.Ordinal);
                foreach (KeyValuePair<string, float> pair in overrides) next[pair.Key] = pair.Value;
                Volatile.Write(ref effective, next);
                dirty = false;
                Interlocked.Increment(ref version);
            }
        }

        private static bool TryParse(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    // The look constants of every package: Tuning.Get("key", compiledDefault). The compiled value is the fallback, so a package works
    // without any file. The values come from nodes ATMO_TUNING { key = value } in GameData/ReDefinition/Atmospheres/Tuning/*.cfg (package
    // E40 owns the files; ModuleManager can patch them), cached per frame. SetOverride lets a series variant change a constant inside one
    // start of the game.
    internal static class Tuning
    {
        internal const string NodeName = "ATMO_TUNING";

        private static readonly TuningTable Table = new TuningTable();
        private static bool loadedFromGame;

        // The value of a key, or the compiled default.
        internal static float Get(string key, float compiledDefault)
        {
            return Table.Get(key, compiledDefault);
        }

        // A switch: a value other than 0 is true.
        internal static bool Flag(string key, bool compiledDefault)
        {
            return Table.Get(key, compiledDefault ? 1f : 0f) != 0f;
        }

        internal static void SetOverride(string key, float value)
        {
            Table.SetOverride(key, value);
        }

        internal static void ClearOverrides()
        {
            Table.ClearOverrides();
        }

        // Rises each time the table changed.
        internal static long Version
        {
            get { return Table.Version; }
        }

        // Called once per frame by the module runner, before the modules tick: reads the nodes of the game database once, then swaps in
        // changes.
        internal static void BeginFrame(int frameCount)
        {
            if (!loadedFromGame)
            {
                loadedFromGame = true;
                LoadFromGame();
            }
            Table.BeginFrame(frameCount);
        }

        // Reads the nodes again (after a ModuleManager reload, or a changed file).
        internal static void LoadFromGame()
        {
            try
            {
                ConfigNode[] nodes = GameDatabase.Instance == null ? null : GameDatabase.Instance.GetConfigNodes(NodeName);
                Table.Load(nodes);
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("tuning", "the tuning nodes could not be read, the compiled values are used", e);
            }
        }

        // The table itself, for tests.
        internal static TuningTable Instance
        {
            get { return Table; }
        }
    }
}
