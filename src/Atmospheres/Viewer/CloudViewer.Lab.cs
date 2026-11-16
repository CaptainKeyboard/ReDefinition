using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres.Viewer
{
    // The viewer's weather lab, cost readout and preset book (VIEW3): the generator's levers and the seed with their Rebuild Weather, the readout of the cells and anvil sources, the cost of
    // the cloud pass and of the last change, and the built-in and saved presets with the A/B compare between two of them. Nothing here writes the save or a settings file: the levers and
    // the seed are held in memory and taken back at Close.
    internal sealed partial class CloudViewer
    {
        private readonly WeatherLab lab = new WeatherLab(new GameLabHost());
        private readonly CostReadout cost = new CostReadout();
        private int rebuildRequestsSeen;
        private bool rebuildPoked;
        private readonly List<string> readout = new List<string>();
        private string readoutNote = "Press Refresh Readout";

        // The settings that the viewer's modes list shows and A/B and the presets hold besides the Clouds group (the modes are part of it).
        internal WeatherLab Lab { get { return lab; } }
        internal CostReadout Cost { get { return cost; } }
        internal IList<string> ReadoutLines { get { return readout; } }
        internal string ReadoutNote { get { return readoutNote; } }

        // Rebuild Weather: the staged levers and seed are put into force, the generator makes its state again on a worker and swaps it in, and the cloud pass fetches the new maps at once
        // (not at its interval) and drops its history. The old weather shows until the new maps are there; the main thread does the parameter set and the swap only.
        internal void RebuildWeather()
        {
            if (!Active) return;
            cost.Mark("Rebuild Weather");
            lab.Commit("Rebuild Weather");
            rebuildPoked = false;
            AtmospheresLog.Say("cloud viewer: Rebuild Weather (levers " + GeneratorViewerOverrides.Describe() + ", seed " + lab.Seed + ")");
        }

        // The weather lab's frame: the cost sample, the cloud pass told to fetch once the new state stands, and the readout refreshed once a rebuild is done.
        private void TickLab()
        {
            cost.Add(Time.unscaledDeltaTime * 1000.0, GpuCostSource.CloudPassMs());
            OwnCloudModule m = OwnCloudModule.Current;
            if (WeatherRebuild.Requests != rebuildRequestsSeen)
            {
                rebuildRequestsSeen = WeatherRebuild.Requests;
                rebuildPoked = false;
            }
            if (WeatherRebuild.Phase == RebuildPhase.Fetching && m != null)
            {
                // The new state stands: the first call also drops the history and rebuilds the heights; the following ones only ask for another map round until one has the new maps.
                if (!rebuildPoked) { m.RequestWeatherRebuild(); rebuildPoked = true; }
                else m.PokeMapFetch();
            }
            if (WeatherRebuild.Phase == RebuildPhase.Done && rebuildPoked)
            {
                rebuildPoked = false;
                AtmospheresLog.Say("cloud viewer: " + WeatherRebuild.Describe());
                RefreshReadout();
                if (Changed != null) Changed();
            }
            // A stack that was built anew from the specs has its own anvil levers: the committed ones are put on again.
            if (m != null && !ReferenceEquals(m.Stack, labStack))
            {
                labStack = m.Stack;
                lab.ReapplyLayers();
            }
        }

        private ICloudLayerSource labStack;

        // The readout of the cells and anvil sources in the field and of the cover and type shares of each layer, for the body the camera is over and the game time now.
        internal void RefreshReadout()
        {
            readout.Clear();
            GeneratorField gf = GeneratorModule.Current;
            CelestialBody body = FlightGlobals.currentMainBody;
            OwnCloudModule module = OwnCloudModule.Current;
            LayerStackSource stack = module == null ? null : module.Stack as LayerStackSource;
            if (gf == null || body == null || stack == null || gf.StateOf(body.flightGlobalsIndex) == null)
            {
                readoutNote = "No weather state or cloud stack yet";
                return;
            }
            double ut = Planetarium.GetUniversalTime();
            var cells = new List<AtmoSim.Core.ConvectiveCell>(2048);
            double snapshotUt;
            bool have = gf.GetCells(body.flightGlobalsIndex, ut, cells, out snapshotUt);
            if (!have)
            {
                readoutNote = "The worker has made no cell list yet; press Refresh Readout again";
                return;
            }
            double radius = body.Radius;
            readout.Add(WeatherReadout.DensityLine("Cells", cells.Count, radius));
            for (int l = 0; l < stack.LayerCount; l++)
            {
                CloudLayerDef def = stack.LayerAt(l);
                if (AnvilMap.AnvilTypeIndex(def) < 0) continue;
                // The sources the cell list gives this layer, counted with the layer's own cluster scale on scratch maps (the layer's maps are not touched).
                var report = new AnvilMap.Report();
                var inp = new AnvilInputs { Width = GeneratorMaps.Width, Height = GeneratorMaps.Height, RadiusM = radius, ShearRadS = 0.0, UpdraftMs = gf.CellUpdraftMsOf(body.flightGlobalsIndex) };
                int n = inp.Width * inp.Height;
                AnvilMap.ApplyCells(def, cells, new byte[n], new byte[n], inp, report);
                int sources = report.Members.Count;
                readout.Add(WeatherReadout.DensityLine("Anvil sources of " + def.Name, sources, radius) + "; cellList " + (def.CellList ? "on" : "off (the layer draws from the sampled map)")
                            + "; cells with a tower " + report.Cells + ", absorbed " + report.Absorbed);
            }
            for (int l = 0; l < stack.LayerCount; l++)
            {
                CloudLayerDef def = stack.LayerAt(l);
                int index = l;
                WeatherReadout.LayerShares s = WeatherReadout.Shares((double lat, double lon, out float c, out float t) => stack.SampleMaps(index, lat, lon, out c, out t), def.Types.Count);
                var names = new List<string>();
                foreach (CloudTypeDef type in def.Types) names.Add(type.Name);
                readout.Add(WeatherReadout.SharesLine(def.Name, names, s));
            }
            readoutNote = "Field at UT " + snapshotUt.ToString("0", CultureInfo.InvariantCulture) + ", seed " + AtmospheresSettings.GenSeed + "; " + WeatherReadout.RealSource;
        }

        // --- Presets ---

        private List<PresetEntry> presetEntries;

        // The list of the presets: Today, the registered slots and the saved files (built again when asked, since files come and go).
        internal List<PresetEntry> PresetEntries()
        {
            var builtIn = ViewerPresetBook.BuiltIn(settingKeys, key =>
            {
                SettingDef def = AtmospheresSettings.Definition(key);
                return def == null || def.Kind == SettingKind.Text ? (double?)null : def.Default;
            }, LayerNames());
            presetEntries = ViewerPresetBook.Entries(builtIn, PresetNames(), name => () => ReadPreset(name));
            return presetEntries;
        }

        private static OptionSet ReadPreset(string name)
        {
            return ModSettingsFile.Read<OptionSet>(PresetPath(ViewerPresets.SafeName(name)), OptionSet.FromNode, null);
        }

        // Loads an entry of the preset list (a saved file or a built-in set).
        internal bool LoadEntry(PresetEntry entry)
        {
            if (entry == null || !Active) return false;
            OptionSet set = entry.Make();
            return set != null && ApplySet(set, entry.Name);
        }

        private bool ApplySet(OptionSet set, string label)
        {
            EnsureKeys(true);
            cost.Mark("preset " + label);
            foreach (string k in keyNames) Note(k);
            set.ApplyTo(store, keyNames, lab);
            Weather = set.Weather;
            CloudBridgeLights.RequestHistoryReset("cloud viewer preset");
            AutoCommitLab("preset " + label);
            AtmospheresLog.Say("cloud viewer: preset " + label + " loaded (" + set.Settings.Count + " settings, " + set.Overrides.Count + " overrides, " + set.Extras.Count + " lab levers)");
            if (Changed != null) Changed();
            return true;
        }

        // A set that changed the lab's levers or seed brings its weather with it: the rebuild is asked for at once (the old weather shows meanwhile).
        private void AutoCommitLab(string why)
        {
            if (lab.Pending) lab.Commit(why);
        }

        // The two presets of the A/B compare, by name; empty for none.
        internal string CompareA { get; private set; } = "";
        internal string CompareB { get; private set; } = "";

        internal void CycleCompare(bool sideA)
        {
            List<PresetEntry> entries = PresetEntries();
            PresetEntry next = ViewerPresetBook.Next(entries, sideA ? CompareA : CompareB);
            if (next == null) return;
            if (sideA) CompareA = next.Name;
            else CompareB = next.Name;
        }

        // Puts preset A into the store and keeps it as the A side, then preset B: B shows, and the flip (button or key) swaps them.
        internal bool StartCompare()
        {
            List<PresetEntry> entries = PresetEntries();
            PresetEntry a = entries.Find(e => string.Equals(e.Name, CompareA, StringComparison.OrdinalIgnoreCase));
            PresetEntry b = entries.Find(e => string.Equals(e.Name, CompareB, StringComparison.OrdinalIgnoreCase));
            if (a == null || b == null || !LoadEntry(a)) return false;
            StoreA();
            if (!LoadEntry(b)) return false;
            AtmospheresLog.Say("cloud viewer: A/B compare of " + a.Name + " (A) and " + b.Name + " (B), showing B");
            return true;
        }
    }
}
