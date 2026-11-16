using System;
using System.Collections.Generic;
using System.Globalization;

namespace ReDefinition.Atmospheres.Viewer
{
    // One lever of the weather lab: a field of the generator's parameter set (cell instability, updraft scale and the like) or a key of the cloud layers' anvil set (cellList,
    // anvilClusterScale). The range is the reader's where it clamps (the layer keys, BodyRuleReaders.Layers) and estimated otherwise.
    internal sealed class LabKnob
    {
        internal LabKnob(string field, string title, string note, double min, double max, bool flag, bool layer, bool rangeEstimated)
        {
            Field = field;
            Title = title;
            Note = note;
            Min = min;
            Max = max;
            Flag = flag;
            Layer = layer;
            RangeEstimated = rangeEstimated;
        }

        // The field of BodyParams, or the key of the layer's cfg node.
        internal string Field { get; }
        internal string Title { get; }
        internal string Note { get; }
        internal double Min { get; }
        internal double Max { get; }
        internal bool Flag { get; }
        // A lever of the cloud layers' anvil set (applied to every layer that has an anvil type), not of the generator.
        internal bool Layer { get; }
        internal bool RangeEstimated { get; }

        // The id the presets and the A/B sets store it under.
        internal string Id
        {
            get { return (Layer ? "layer." : "gen.") + Field; }
        }
    }

    // The levers the viewer offers. All of them take effect only at Rebuild Weather (the generator makes its state again, the layers' maps are fetched again).
    internal static class LabKnobs
    {
        internal static readonly IReadOnlyList<LabKnob> All = new List<LabKnob>
        {
            new LabKnob("CellInstabilityWeight", "Cell Instability Weight", "1 the physical square root of the energy, 0 off: the strength of a cell follows its air", 0, 1, false, false, true),
            new LabKnob("CellEnergyFull", "Cell Energy For Full Strength", "the energy at which a cell has its full strength", 0.05, 1.5, false, false, true),
            new LabKnob("CellLiftEnergyShare", "Cell Lift Energy Share", "the share of the lift term that counts as cell energy", 0, 1, false, false, true),
            new LabKnob("CellUpdraftScale", "Cell Updraft Scale", "the body's multiplier on the updraft of a full-strength cell (1 derived)", 0.1, 4, false, false, true),
            new LabKnob("CellBuoyancyNumber", "Cell Buoyancy Number", "the dimensionless buoyancy of a full-strength cell (6 m/s on Earth is 0.00035)", 0.00005, 0.002, false, false, true),
            new LabKnob("CellRainThreshold", "Cell Rain Threshold", "the cloud-water level at which a cell rains", 0, 0.5, false, false, true),
            new LabKnob("CellFrontTerm", "Cell Front Term", "the front term of the presence probability of a cell", 0, 1, false, false, true),
            new LabKnob("CellTrigger", "Cell Trigger In The Extratropics", "cells outside the tropics need a front", 0, 1, true, false, false),
            new LabKnob("CellTriggerLo", "Cell Trigger Low", "the front strength where the trigger factor starts", 0, 1.5, false, false, true),
            new LabKnob("CellTriggerHi", "Cell Trigger High", "the front strength where the trigger factor is full", 0, 2, false, false, true),
            new LabKnob("StationaryCells", "Cells Alive At Any Time", "a cell is born at any time of its 3 h bucket and lives on into the next", 0, 1, true, false, false),
            new LabKnob("cellList", "Towers From The Cell List", "the layer's towers come from the generator's cells instead of the sampled map", 0, 1, true, true, false),
            new LabKnob("anvilClusterScale", "Anvil Cluster Scale", "cells within this many anvil reaches are one storm (1 derived)", 0, 10, false, true, false),
            new LabKnob("anvilScale", "Anvil Spread Scale", "the multiplier on the spread of the anvil beyond the tower", 0, 10, false, true, false),
            new LabKnob("anvilUpdraftMs", "Anvil Updraft", "the updraft at the anvil base in m/s, 0 the body's own", 0, 100, false, true, false)
        };

        internal static LabKnob Find(string id)
        {
            foreach (LabKnob k in All) if (k.Id == id) return k;
            return null;
        }
    }

    // What the lab needs of the game: the baseline values the generator and the layers have now, the layers' levers, the seed, and the rebuild.
    internal interface ILabHost
    {
        // The value of a generator field in the state that stands (NaN where there is no state yet).
        double GeneratorValue(string field);
        // The value of a layer lever on the first layer that has an anvil type (NaN where none has).
        double LayerValue(string field);
        void SetLayerValue(string field, double value);
        // Puts the layers' levers back as they were before the lab set them.
        void RestoreLayers();
        int Seed { get; set; }
        void RequestRebuild(string reason);
    }

    // The staged levers of the weather lab, the seed, and the commit of both at Rebuild Weather. Nothing a lever or the seed holds reaches the weather before Commit; Close takes it all
    // back and asks for one more rebuild where something had been committed.
    internal sealed class WeatherLab
    {
        private readonly ILabHost host;
        private readonly Dictionary<string, double> staged = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> committed = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> baseline = new Dictionary<string, double>(StringComparer.Ordinal);
        private int? stagedSeed, committedSeed;
        private int seedAtOpen;
        private bool opened;

        internal WeatherLab(ILabHost host)
        {
            this.host = host;
        }

        // The seed the game had when the viewer opened.
        internal int SeedAtOpen
        {
            get { return seedAtOpen; }
        }

        internal void Open()
        {
            staged.Clear();
            committed.Clear();
            baseline.Clear();
            stagedSeed = committedSeed = null;
            seedAtOpen = host.Seed;
            opened = true;
        }

        // The value before the lab: read once while nothing is committed (the state's own value), kept after.
        internal double Baseline(LabKnob knob)
        {
            double v;
            if (baseline.TryGetValue(knob.Id, out v)) return v;
            v = knob.Layer ? host.LayerValue(knob.Field) : host.GeneratorValue(knob.Field);
            // A value that is not there yet (no state) is not kept: the next read asks again.
            if (!double.IsNaN(v) && committed.Count == 0) baseline[knob.Id] = v;
            return v;
        }

        // The value the table shows: the staged one, else the baseline.
        internal double Value(LabKnob knob)
        {
            double v;
            return staged.TryGetValue(knob.Id, out v) ? v : Baseline(knob);
        }

        internal bool Changed(LabKnob knob)
        {
            return staged.ContainsKey(knob.Id);
        }

        internal void Set(LabKnob knob, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            if (knob.Flag) value = value != 0.0 ? 1.0 : 0.0;
            double b = Baseline(knob);
            if (!double.IsNaN(b) && b == value) staged.Remove(knob.Id);
            else staged[knob.Id] = value;
        }

        internal void Reset(LabKnob knob)
        {
            staged.Remove(knob.Id);
        }

        internal int Seed
        {
            get { return stagedSeed ?? committedSeed ?? host.Seed; }
        }

        internal void SetSeed(int seed)
        {
            if (seed == (committedSeed ?? seedAtOpen)) stagedSeed = null;
            else stagedSeed = seed;
        }

        // Whether anything staged differs from what was committed: the button's cue.
        internal bool Pending
        {
            get
            {
                if (stagedSeed.HasValue && stagedSeed != committedSeed) return true;
                if (staged.Count != committed.Count) return true;
                foreach (KeyValuePair<string, double> pair in staged)
                {
                    double c;
                    if (!committed.TryGetValue(pair.Key, out c) || c != pair.Value) return true;
                }
                return false;
            }
        }

        // Puts the staged levers and the seed into force and asks for the rebuild. The generator's levers go to GeneratorViewerOverrides (read when the state is made), the layers'
        // levers are set on the layers at once (read at the next map round, which the rebuild forces), the seed becomes the game's seed.
        internal void Commit(string reason)
        {
            var generator = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, double> pair in staged)
            {
                LabKnob knob = LabKnobs.Find(pair.Key);
                if (knob != null && !knob.Layer) generator[knob.Field] = pair.Value;
            }
            GeneratorViewerOverrides.Replace(generator);
            host.RestoreLayers();
            foreach (KeyValuePair<string, double> pair in staged)
            {
                LabKnob knob = LabKnobs.Find(pair.Key);
                if (knob != null && knob.Layer) host.SetLayerValue(knob.Field, pair.Value);
            }
            committed.Clear();
            foreach (KeyValuePair<string, double> pair in staged) committed[pair.Key] = pair.Value;
            if (stagedSeed.HasValue)
            {
                committedSeed = stagedSeed;
                host.Seed = stagedSeed.Value;
            }
            host.RequestRebuild(reason);
        }

        // Puts the layers' committed levers on again (the stack was built anew from the specs and has its own).
        internal void ReapplyLayers()
        {
            foreach (KeyValuePair<string, double> pair in committed)
            {
                LabKnob knob = LabKnobs.Find(pair.Key);
                if (knob != null && knob.Layer) host.SetLayerValue(knob.Field, pair.Value);
            }
        }

        // The staged levers and the seed as numbers under their ids (for a preset or A/B): "gen.<field>", "layer.<key>", "seed".
        internal void Capture(Dictionary<string, double> into)
        {
            foreach (KeyValuePair<string, double> pair in staged) into[pair.Key] = pair.Value;
            if (stagedSeed.HasValue) into["seed"] = stagedSeed.Value;
            else if (committedSeed.HasValue) into["seed"] = committedSeed.Value;
        }

        // Stages a set that Capture made (and no more: a lever the set does not name goes back to its baseline); the weather changes at the next Rebuild Weather.
        internal void Stage(IDictionary<string, double> from)
        {
            staged.Clear();
            stagedSeed = null;
            if (from == null) return;
            foreach (KeyValuePair<string, double> pair in from)
            {
                if (pair.Key == "seed") { SetSeed((int)pair.Value); continue; }
                LabKnob knob = LabKnobs.Find(pair.Key);
                if (knob != null) Set(knob, pair.Value);
            }
        }

        // The end of the session: the levers and the seed are taken back, and one more rebuild asked for where the weather had been changed.
        internal bool Close()
        {
            if (!opened) return false;
            opened = false;
            bool changed = committed.Count > 0 || committedSeed.HasValue;
            GeneratorViewerOverrides.Clear();
            host.RestoreLayers();
            if (committedSeed.HasValue) host.Seed = seedAtOpen;
            staged.Clear();
            committed.Clear();
            stagedSeed = committedSeed = null;
            if (changed) host.RequestRebuild("viewer closed");
            return changed;
        }

        internal static string Format(LabKnob knob, double v)
        {
            return knob.Flag ? (v != 0.0 ? "On" : "Off") : v.ToString("0.#####", CultureInfo.InvariantCulture);
        }
    }
}
