using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AtmoSim.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Viewer;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.Viewer
{
    // VIEW3: the registry's groups, ranges and markers and the registration of packages, the modes' markers and greying, the presets (built in, saved, A/B), the weather lab and its
    // rebuild bookkeeping, the readouts' formatting and the cost readout. All without the game.
    [TestClass]
    public class ViewerWorkbenchTests
    {
        // A source a package would write: keys, a per-layer key with the name between prefix and suffix, and a filled preset slot.
        private static class FakePackage
        {
            [CloudKeySource]
            internal static void Register(CloudKeyRegistrations r)
            {
                r.Key("cloud.heap.edgePower", 2f, KeySection.Edge, "edge power of the heaps", min: 0.5f, max: 6f);
                r.Flag("cloud.heap.cavity", true, KeySection.Shape, "cavity channel", marker: KeyMarker.AfterSceneChange);
                r.LayerKey("cloud.heap.layer.", ".edgePower", 1.5f, KeySection.Edge, "edge power of a layer");
                r.Preset(ViewerBuiltIns.NewModelSlot, "filled", new Dictionary<string, float> { { "cloud.heap.edgePower", 3f } });
            }
        }

        // --- The registry: groups, ranges, markers, registration ---

        [TestMethod]
        public void EveryCoreKeyIsPlacedInAGroupWithARangeAndAMarker()
        {
            foreach (CloudKey k in CloudKeyRegistry.All)
            {
                Assert.AreNotEqual(KeySection.Other, k.Section, k.Key + " has no group in CloudKeyLayout");
                Assert.AreNotEqual(KeySection.Modes, k.Section, k.Key + ": the Modes group holds settings, not tuning keys");
                Assert.IsFalse(float.IsNaN(k.Min) || float.IsNaN(k.Max), k.Key);
                Assert.IsTrue(k.Min < k.Max, k.Key + " range " + k.Min + " to " + k.Max);
                Assert.AreNotEqual(KeyMarker.Unchecked, k.Marker, k.Key);
            }
            Assert.AreEqual(0, CloudKeyRegistry.Conflicts.Count, string.Join("; ", CloudKeyRegistry.Conflicts));
            foreach (KeySection s in new[] { KeySection.Shape, KeySection.Edge, KeySection.Light, KeySection.Quality, KeySection.Weather })
                Assert.IsTrue(CloudKeyRegistry.All.Any(k => k.Section == s), s + " group is empty");
        }

        [TestMethod]
        public void ARangeIsStatedWhereTheReaderClampsAndEstimatedElsewhere()
        {
            CloudKey speed = CloudKeyRegistry.Find(CloudMotion.TuneScale);
            Assert.IsFalse(speed.RangeEstimated);
            Assert.AreEqual(0f, speed.Min);
            Assert.AreEqual(CloudMotion.MaxScale, speed.Max);
            CloudKey flag = CloudKeyRegistry.Find(OwnCloudOptions.TuneEmptyReach);
            Assert.IsTrue(flag.Flag);
            Assert.AreEqual(0f, flag.Min);
            Assert.AreEqual(1f, flag.Max);
            Assert.IsFalse(flag.RangeEstimated);
            CloudKey sentinel = CloudKeyRegistry.Find(CloudLookTuning.TuneBulgeFine);
            Assert.AreEqual(-1f, sentinel.Default);
            Assert.AreEqual(-1f, sentinel.Min, "a sentinel default starts the range");
            Assert.IsTrue(sentinel.RangeEstimated);
            CloudKey sky = CloudKeyRegistry.Find(OwnCloudOptions.TuneAmbientGain);
            Assert.IsTrue(sky.Min <= sky.Default && sky.Default <= sky.Max, "the default is on the slider");
        }

        [TestMethod]
        public void EveryTuningKeyOfThePassIsInTheRegistryAlsoWhenItIsNamedKeyOrBuiltFromAPrefix()
        {
            // The scan of CloudViewerTests finds Tune* constants and literal reads; this one adds constants named Key* and prefix constants that end in a dot, in every file of the atmospheres
            // outside the viewer, so that a package's declaration style cannot slip past.
            string root = Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src", "Atmospheres");
            const string prefix = @"(?:cloud|layers|light|cull|recon|shadow)\.[A-Za-z][\w.]*";
            var declared = new Regex(@"\b(?:Tune|Key|Prefix)\w*\s*=\s*""(" + prefix + @")""");
            var missing = new List<string>();
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.IndexOf(Path.DirectorySeparatorChar + "Viewer" + Path.DirectorySeparatorChar, StringComparison.Ordinal) >= 0) continue;
                foreach (Match m in declared.Matches(File.ReadAllText(file)))
                {
                    string key = m.Groups[1].Value;
                    bool listed = key.EndsWith(".", StringComparison.Ordinal)
                        ? CloudKeyRegistry.PerLayer.Any(t => t.Prefix == key)
                        : CloudKeyRegistry.All.Any(k => k.Key == key) || CloudKeyRegistry.PerLayer.Any(t => t.Prefix + t.Suffix == key);
                    if (!listed) missing.Add(key + " (" + Path.GetFileName(file) + ")");
                }
            }
            CollectionAssert.AreEqual(new string[0], missing.Distinct().ToArray(), string.Join(", ", missing.Distinct()));
        }

        [TestMethod]
        public void APackageRegistersKeysLayerKeysAndPresetsWithoutEditingTheRegistry()
        {
            CloudKeyRegistrations r = CloudKeyRegistry.CollectFrom(new[] { typeof(FakePackage) });
            Assert.AreEqual(2, r.Keys.Count);
            Assert.AreEqual(1, r.LayerKeys.Count);
            CloudKey edge = r.Keys.First(k => k.Key == "cloud.heap.edgePower");
            Assert.AreEqual(KeySection.Edge, edge.Section);
            Assert.IsFalse(edge.RangeEstimated);
            Assert.AreEqual(6f, edge.Max);
            CloudKey cavity = r.Keys.First(k => k.Key == "cloud.heap.cavity");
            Assert.IsTrue(cavity.Flag);
            Assert.AreEqual(KeyMarker.AfterSceneChange, cavity.Marker);
            Assert.AreEqual("After Scene Change", CloudKeyRegistry.MarkerText(cavity.Marker));
            // A per-layer key with the layer's name between prefix and suffix is matched and expanded.
            CloudLayerKey layer = r.LayerKeys[0];
            Assert.IsTrue(layer.Matches("cloud.heap.layer.Kerbin-cumulus.edgePower"));
            Assert.IsFalse(layer.Matches("cloud.heap.layer..edgePower"));
            Assert.IsFalse(layer.Matches("cloud.heap.layer.a.b.edgePower"), "a layer name has no dot");
            Assert.IsFalse(layer.Matches("cloud.heap.layer.Kerbin-cumulus.cavity"));
            CloudKey made = layer.For("Kerbin-cumulus");
            Assert.AreEqual("cloud.heap.layer.Kerbin-cumulus.edgePower", made.Key);
            Assert.AreEqual(KeySection.Edge, made.Section);
            Assert.AreEqual(1.5f, made.Default);
        }

        [TestMethod]
        public void TheKeysAPackageRegistersAreFoundAndAConflictIsReported()
        {
            CloudKeyRegistrations r = CloudKeyRegistry.CollectFrom(new[] { typeof(FakePackage) });
            var core = new List<CloudKey> { new CloudKey("cloud.heap.edgePower", 1f, KeyLiveness.Frame, false, "core") };
            var conflicts = new List<string>();
            List<CloudKey> placed = CloudKeyRegistry.Place(core, r, conflicts);
            Assert.AreEqual(2, placed.Count, "the core key and the one new key");
            Assert.AreEqual(1, conflicts.Count);
            StringAssert.Contains(conflicts[0], "cloud.heap.edgePower");
            var layerConflicts = new List<string>();
            List<CloudLayerKey> joined = CloudKeyRegistry.JoinLayers(r.LayerKeys, r, layerConflicts);
            Assert.AreEqual(1, joined.Count);
            Assert.AreEqual(1, layerConflicts.Count);
        }

        [TestMethod]
        public void AFilledPresetSlotReplacesTheEmptyOneOfTheSameNameAndTwoFilledOnesConflict()
        {
            var r = new CloudKeyRegistrations();
            r.Preset("New Model 96 Steps", "empty", null);
            r.Preset("new model 96 steps", "filled", new Dictionary<string, float> { { "cloud.sunGain", 2f } });
            Assert.AreEqual(0, r.MergePresets().Count);
            Assert.AreEqual(1, r.Presets.Count);
            Assert.IsFalse(r.Presets[0].IsEmpty);
            r.Preset("new model 96 steps", "again", new Dictionary<string, float> { { "cloud.sunGain", 3f } });
            Assert.AreEqual(1, r.MergePresets().Count);
            Assert.AreEqual(2f, r.Presets[0].Keys["cloud.sunGain"], "the first filled one stays");
        }

        [TestMethod]
        public void TheNewModelSlotOfTheViewerIsFilledByTheCloudModelPackage()
        {
            NamedKeyPreset slot = CloudKeyRegistry.Presets.FirstOrDefault(p => p.Name == ViewerBuiltIns.NewModelSlot);
            Assert.IsNotNull(slot);
            Assert.IsFalse(slot.IsEmpty, "the viewer registers the slot empty, CloudModelKeys fills it");
            Assert.AreEqual(1, CloudKeyRegistry.Presets.Count(p => p.Name == ViewerBuiltIns.NewModelSlot), "one entry per name");
            Assert.IsNotNull(CloudKeyRegistry.Presets.FirstOrDefault(p => p.Name == CloudModelKeys.Look128));
            Assert.IsTrue(ViewerPresetBook.IsReserved("96 steps"));
            Assert.IsTrue(ViewerPresetBook.IsReserved("Today"));
            Assert.IsFalse(ViewerPresetBook.IsReserved("my look"));
        }

        // --- The modes ---

        [TestMethod]
        public void EveryModeIsASettingOfTheCloudsGroupWithACheckedMarkerAndItsReader()
        {
            Assert.IsTrue(ViewerModes.All.Count >= 15);
            var seen = new HashSet<string>();
            foreach (ViewerMode m in ViewerModes.All)
            {
                Assert.IsTrue(seen.Add(m.Key), m.Key + " twice");
                SettingDef def = AtmospheresSettings.Definition(m.Key);
                Assert.IsNotNull(def, m.Key);
                Assert.AreEqual(AtmospheresSettings.Categories.Clouds, def.Category, m.Key);
                Assert.AreNotEqual(KeyMarker.Unchecked, m.Marker, m.Key);
                Assert.IsTrue(m.Where.Length > 20, m.Key + " names no reader");
            }
            foreach (string key in new[] { AtmospheresSettings.Keys.OwnClouds, AtmospheresSettings.Keys.CloudSource, AtmospheresSettings.Keys.RtOwnClouds,
                                           AtmospheresSettings.Keys.RtOwnCloudsLayerCap, AtmospheresSettings.Keys.LayerCap })
                Assert.IsNotNull(ViewerModes.Find(key), key + " is one of the owner's modes");
        }

        [TestMethod]
        public void AModeIsGreyedWithTheSettingsWindowsOwnReasonAndTheBodysStatedSource()
        {
            WindowEnvironment env = WindowEnvironment.Full();
            var values = new Dictionary<string, double>();
            foreach (SettingDef d in SettingsTable.All) if (d.Kind != SettingKind.Text) values[d.Key] = d.Default;
            Func<string, double> value = k => values[k];
            SettingDef rt = AtmospheresSettings.Definition(AtmospheresSettings.Keys.RtOwnClouds);
            values[AtmospheresSettings.Keys.Enabled] = 1.0;
            values[AtmospheresSettings.Keys.WeatherEffects] = 1.0;
            values[AtmospheresSettings.Keys.CloudSource] = (double)(int)CloudSourceKind.Ours;
            values[AtmospheresSettings.Keys.OwnClouds] = 1.0;
            Assert.IsNull(ViewerModes.GreyReason(rt, value, env, false), "with the own clouds on the row is open");
            values[AtmospheresSettings.Keys.OwnClouds] = 0.0;
            string why = ViewerModes.GreyReason(rt, value, env, false);
            Assert.IsNotNull(why);
            Assert.AreEqual(SettingHelp.ReasonText(AtmospheresSettings.GreyReasons.NeedsOwnClouds), why);
            SettingDef source = AtmospheresSettings.Definition(AtmospheresSettings.Keys.CloudSource);
            StringAssert.Contains(ViewerModes.GreyReason(source, value, env, true), "Config");
        }

        // --- Options, presets, A/B ---

        private sealed class FakeLabHost : ILabHost
        {
            internal readonly Dictionary<string, double> Generator = new Dictionary<string, double> { { "CellUpdraftScale", 1.0 }, { "CellInstabilityWeight", 1.0 }, { "StationaryCells", 1.0 } };
            internal readonly Dictionary<string, double> Layers = new Dictionary<string, double> { { "cellList", 0.0 }, { "anvilClusterScale", 1.0 }, { "anvilScale", 1.0 }, { "anvilUpdraftMs", 0.0 } };
            internal readonly Dictionary<string, double> LayerOriginals = new Dictionary<string, double>();
            internal readonly List<string> Rebuilds = new List<string>();
            public int Seed { get; set; } = 7;
            public double GeneratorValue(string field) { double v; return Generator.TryGetValue(field, out v) ? v : double.NaN; }
            public double LayerValue(string field) { double v; return Layers.TryGetValue(field, out v) ? v : double.NaN; }
            public void SetLayerValue(string field, double value) { if (!LayerOriginals.ContainsKey(field)) LayerOriginals[field] = Layers[field]; Layers[field] = value; }
            public void RestoreLayers() { foreach (KeyValuePair<string, double> p in LayerOriginals) Layers[p.Key] = p.Value; LayerOriginals.Clear(); }
            public void RequestRebuild(string reason) { Rebuilds.Add(reason); }
        }

        private static FakeViewerRig.Store Store()
        {
            return new FakeViewerRig.Store();
        }

        private static readonly string[] Keys = { CloudLookTuning.TuneSigma, OwnCloudModule.TuneSunGain };
        private static readonly string[] SettingKeys = { AtmospheresSettings.Keys.CloudSoftness, AtmospheresSettings.Keys.ForceLevel };

        [TestMethod]
        public void AnOptionSetKeepsTheLabsLeversAndTheSeedThroughTheFileFormAndThePlayersPresetsDoNotTouchDefaults()
        {
            GeneratorViewerOverrides.Clear();
            var host = new FakeLabHost();
            var lab = new WeatherLab(host);
            lab.Open();
            lab.Set(LabKnobs.Find("gen.CellUpdraftScale"), 2.0);
            lab.Set(LabKnobs.Find("layer.cellList"), 1.0);
            lab.SetSeed(99);
            FakeViewerRig.Store store = Store();
            store.Overrides[CloudLookTuning.TuneSigma] = 1.4f;
            OptionSet set = OptionSet.Capture(store, SettingKeys, Keys, ViewerWeather.Field, lab);
            Assert.AreEqual(2.0, set.Extras["gen.CellUpdraftScale"]);
            Assert.AreEqual(1.0, set.Extras["layer.cellList"]);
            Assert.AreEqual(99.0, set.Extras["seed"]);
            OptionSet back = OptionSet.FromNode(set.ToNode());
            CollectionAssert.AreEquivalent(set.Extras.ToList(), back.Extras.ToList());
            CollectionAssert.AreEquivalent(set.Overrides.ToList(), back.Overrides.ToList());
            // Applying the set to another lab stages the levers and nothing reaches the weather.
            var other = new WeatherLab(new FakeLabHost());
            other.Open();
            back.ApplyTo(Store(), Keys, other);
            Assert.AreEqual(2.0, other.Value(LabKnobs.Find("gen.CellUpdraftScale")));
            Assert.AreEqual(99, other.Seed);
            Assert.IsTrue(other.Pending);
            Assert.AreEqual(0, GeneratorViewerOverrides.Count, "staged levers are not in force before the rebuild");
        }

        [TestMethod]
        public void TodayIsTheShippedDefaultsAndASlotSetsOnlyItsKeys()
        {
            var keys = new[] { AtmospheresSettings.Keys.CloudSoftness, AtmospheresSettings.Keys.CloudSamples };
            OptionSet today = ViewerPresetBook.Today(keys, k => AtmospheresSettings.Definition(k).Default);
            Assert.AreEqual(AtmospheresSettings.Definition(AtmospheresSettings.Keys.CloudSoftness).Default, today.Settings[AtmospheresSettings.Keys.CloudSoftness]);
            Assert.AreEqual(0, today.Overrides.Count, "Today holds no tuning override: every key is cleared");
            FakeViewerRig.Store store = Store();
            store.Overrides[CloudLookTuning.TuneSigma] = 3f;
            today.ApplyTo(store, Keys);
            Assert.IsFalse(store.Overrides.ContainsKey(CloudLookTuning.TuneSigma), "loading Today clears the override");
            var slot = new NamedKeyPreset("Slot", "", new Dictionary<string, float> { { OwnCloudModule.TuneSunGain, 2f } }, new Dictionary<string, double> { { AtmospheresSettings.Keys.CloudSoftness, 0.25 } });
            OptionSet fromSlot = ViewerPresetBook.FromSlot(slot);
            fromSlot.ApplyTo(store, Keys);
            Assert.AreEqual(2f, store.Overrides[OwnCloudModule.TuneSunGain]);
            Assert.AreEqual(0.25, store.Settings[AtmospheresSettings.Keys.CloudSoftness]);
        }

        [TestMethod]
        public void ThePresetListHoldsTheBuiltInOnesFirstAndASavedNameCannotShadowThem()
        {
            List<PresetEntry> builtIn = ViewerPresetBook.BuiltIn(new[] { AtmospheresSettings.Keys.CloudSoftness }, k => 0.5);
            Assert.AreEqual("Today", builtIn[0].Name);
            List<PresetEntry> withModes = ViewerPresetBook.BuiltIn(new[] { AtmospheresSettings.Keys.OwnClouds, AtmospheresSettings.Keys.CloudSource, AtmospheresSettings.Keys.CloudSoftness }, k => 0.5);
            OptionSet today = withModes[0].Make();
            Assert.IsFalse(today.Settings.ContainsKey(AtmospheresSettings.Keys.OwnClouds), "Today never switches the pass off");
            Assert.IsFalse(today.Settings.ContainsKey(AtmospheresSettings.Keys.CloudSource));
            Assert.IsTrue(today.Settings.ContainsKey(AtmospheresSettings.Keys.CloudSoftness));
            Assert.IsTrue(builtIn.Any(e => e.Name == ViewerBuiltIns.NewModelSlot));
            List<PresetEntry> all = ViewerPresetBook.Entries(builtIn, new[] { "mine", "today", "Zed" }, name => () => new OptionSet());
            Assert.AreEqual(builtIn.Count + 2, all.Count, "a saved file named like a built-in one is left out");
            Assert.IsFalse(all.Last().BuiltIn);
            Assert.AreEqual(all[1].Name, ViewerPresetBook.Next(all, "Today").Name);
            Assert.AreEqual("Today", ViewerPresetBook.Next(all, all.Last().Name).Name, "the list wraps");
            Assert.IsNull(ViewerPresetBook.Next(new List<PresetEntry>(), "x"));
        }

        [TestMethod]
        public void TheFlipSwapsTwoPresetsIncludingTheLabsLeversAndFlipsBack()
        {
            GeneratorViewerOverrides.Clear();
            var lab = new WeatherLab(new FakeLabHost());
            lab.Open();
            FakeViewerRig.Store store = Store();
            // A: sigma 2 and a stronger updraft; B: no override and the baseline updraft.
            var a = new OptionSet();
            a.Overrides[CloudLookTuning.TuneSigma] = 2f;
            a.Extras["gen.CellUpdraftScale"] = 3.0;
            var b = new OptionSet();
            var flip = new OptionFlip();
            a.ApplyTo(store, Keys, lab);
            flip.StoreA(OptionSet.Capture(store, SettingKeys, Keys, ViewerWeather.Field, lab));
            b.ApplyTo(store, Keys, lab);
            Assert.IsFalse(store.Overrides.ContainsKey(CloudLookTuning.TuneSigma));
            Assert.AreEqual(1.0, lab.Value(LabKnobs.Find("gen.CellUpdraftScale")));
            ViewerWeather weather = ViewerWeather.Field;
            Assert.IsTrue(flip.Flip(store, SettingKeys, Keys, ref weather, lab));
            Assert.AreEqual('A', flip.Showing);
            Assert.AreEqual(2f, store.Overrides[CloudLookTuning.TuneSigma]);
            Assert.AreEqual(3.0, lab.Value(LabKnobs.Find("gen.CellUpdraftScale")));
            Assert.IsTrue(flip.Flip(store, SettingKeys, Keys, ref weather, lab));
            Assert.AreEqual('B', flip.Showing);
            Assert.IsFalse(store.Overrides.ContainsKey(CloudLookTuning.TuneSigma));
            Assert.AreEqual(1.0, lab.Value(LabKnobs.Find("gen.CellUpdraftScale")));
        }

        // --- The weather lab ---

        [TestMethod]
        public void LabLeversActOnlyAtCommitAreHeldInMemoryAndCloseTakesThemAllBack()
        {
            GeneratorViewerOverrides.Clear();
            var host = new FakeLabHost();
            var lab = new WeatherLab(host);
            lab.Open();
            LabKnob updraft = LabKnobs.Find("gen.CellUpdraftScale"), cells = LabKnobs.Find("layer.cellList"), cluster = LabKnobs.Find("layer.anvilClusterScale");
            Assert.AreEqual(1.0, lab.Baseline(updraft));
            lab.Set(updraft, 2.5);
            lab.Set(cells, 1.0);
            lab.Set(cluster, 1.0);
            Assert.IsTrue(lab.Changed(updraft));
            Assert.IsFalse(lab.Changed(cluster), "a value equal to the baseline is no change");
            Assert.IsTrue(lab.Pending);
            Assert.AreEqual(0, GeneratorViewerOverrides.Count, "nothing is in force before Rebuild Weather");
            Assert.AreEqual(0.0, host.Layers["cellList"]);
            lab.SetSeed(11);
            lab.Commit("test");
            double v;
            Assert.IsTrue(GeneratorViewerOverrides.TryGet("CellUpdraftScale", out v));
            Assert.AreEqual(2.5, v);
            Assert.AreEqual(1.0, host.Layers["cellList"]);
            Assert.AreEqual(11, host.Seed);
            Assert.AreEqual("test", host.Rebuilds.Single());
            Assert.IsFalse(lab.Pending, "after the commit nothing waits");
            lab.Set(updraft, 4.0);
            Assert.IsTrue(lab.Pending);
            lab.Reset(updraft);
            Assert.AreEqual(1.0, lab.Value(updraft), "reset puts the staged value back to the baseline");
            Assert.IsTrue(lab.Pending, "a reset that differs from what is in force waits for the next rebuild");
            Assert.IsTrue(lab.Close());
            Assert.AreEqual(0, GeneratorViewerOverrides.Count);
            Assert.AreEqual(0.0, host.Layers["cellList"], "the layers' levers are put back");
            Assert.AreEqual(7, host.Seed, "the game's seed is put back");
            Assert.AreEqual(2, host.Rebuilds.Count, "one more rebuild to bring the old weather back");
            Assert.IsFalse(lab.Close(), "a second close does nothing");
        }

        [TestMethod]
        public void ClosingALabThatChangedNothingAsksForNoRebuild()
        {
            GeneratorViewerOverrides.Clear();
            var host = new FakeLabHost();
            var lab = new WeatherLab(host);
            lab.Open();
            lab.Set(LabKnobs.Find("gen.CellUpdraftScale"), 2.0);
            Assert.IsFalse(lab.Close());
            Assert.AreEqual(0, host.Rebuilds.Count);
        }

        [TestMethod]
        public void EveryLabKnobIsAFieldOfTheGeneratorOrALayerKeyWithAValidRange()
        {
            var p = new BodyParams();
            foreach (LabKnob k in LabKnobs.All)
            {
                Assert.IsTrue(k.Min < k.Max, k.Id);
                if (k.Layer) continue;
                Assert.IsFalse(double.IsNaN(GeneratorViewerOverrides.Read(p, k.Field)), k.Field + " is not a numeric field of the generator's parameters");
            }
            Assert.AreEqual(LabKnobs.All.Count, LabKnobs.All.Select(k => k.Id).Distinct().Count());
        }

        [TestMethod]
        public void TheViewersLeversReachTheGeneratorsParametersAndTheSameSeedGivesTheSameWeather()
        {
            GeneratorViewerOverrides.Clear();
            BodyFacts f = FieldFixture.HomeFacts();
            BodyClimate c = FieldFixture.Climate(f);
            BodyParams plain = FieldFixture.Params(c, f);
            double baselineScale = plain.CellUpdraftScale;
            GeneratorViewerOverrides.Replace(new Dictionary<string, double> { { "CellUpdraftScale", baselineScale * 2.0 }, { "StationaryCells", 0.0 } });
            BodyParams tuned = FieldFixture.Params(c, f);
            GeneratorViewerOverrides.Clear();
            Assert.AreEqual(baselineScale * 2.0, tuned.CellUpdraftScale, 1e-12);
            Assert.IsFalse(tuned.StationaryCells);
            Assert.IsTrue(FieldFixture.Params(c, f).StationaryCells, "with the set empty the parameters are the body's own");
            // A rebuild with the same inputs gives the same weather: the generator is a pure function of (body, seed, position, time).
            GeneratorState first = FieldFixture.State(c, plain);
            GeneratorState second = FieldFixture.State(c, FieldFixture.Params(c, f));
            Assert.AreEqual(first.DefinitionId, second.DefinitionId);
            string error;
            GeneratorState otherSeed = GeneratorState.Create(FieldFixture.BodyIndex, c.Name, FieldFixture.Params(c, f), GeneratorBodies.SeedOf(c) + 1UL, null, PrecipType.None, out error);
            Assert.IsNotNull(otherSeed, error);
            Assert.AreNotEqual(first.DefinitionId, otherSeed.DefinitionId, "another seed is another weather");
            int differing = 0;
            for (int i = 0; i < 40; i++)
            {
                double lat = 0.2 + 0.03 * i, lon = 0.1 + 0.05 * i, ut = 5.0e6 + 3000.0 * i;
                Weather a, b, o;
                first.Generator.Sample(lat, lon, 0.0, ut, out a);
                second.Generator.Sample(lat, lon, 0.0, ut, out b);
                otherSeed.Generator.Sample(lat, lon, 0.0, ut, out o);
                Assert.AreEqual(a.Coverage, b.Coverage, "same seed, same weather at sample " + i);
                Assert.AreEqual(a.PrecipMmH, b.PrecipMmH);
                if (a.Coverage != o.Coverage || a.PrecipMmH != o.PrecipMmH) differing++;
            }
            Assert.IsTrue(differing > 5, "another seed changes the weather at " + differing + " of 40 points");
        }

        // --- The rebuild's bookkeeping ---

        [TestMethod]
        public void ARebuildWalksFromBuildingThroughFetchingToDoneAndStatesItsCost()
        {
            WeatherRebuild.Reset();
            Assert.AreEqual(RebuildPhase.Idle, WeatherRebuild.Phase);
            WeatherRebuild.Begin("test", new[] { 3, 4 });
            Assert.IsTrue(WeatherRebuild.Active);
            Assert.IsTrue(WeatherRebuild.IsDue(3));
            Assert.IsFalse(WeatherRebuild.IsDue(9));
            WeatherRebuild.AddFrame(4.0);
            WeatherRebuild.AddFrame(WeatherRebuild.FreezeBudgetMs + 5.0);
            Assert.AreEqual(1, WeatherRebuild.FramesOverBudget);
            Assert.IsTrue(WeatherRebuild.OverBudget);
            WeatherRebuild.Finished(3, 12.0, true);
            Assert.IsTrue(WeatherRebuild.Active, "one body is still due");
            WeatherRebuild.Finished(3, 99.0, true);
            Assert.AreEqual(12.0, WeatherRebuild.WorkerMs, "a body that is not due adds nothing");
            WeatherRebuild.Finished(4, 8.0, false);
            Assert.AreEqual(RebuildPhase.Fetching, WeatherRebuild.Phase);
            Assert.AreEqual(1, WeatherRebuild.StatesSwapped);
            WeatherRebuild.AddFrame(50.0);
            Assert.AreEqual(1, WeatherRebuild.FramesOverBudget, "frames after the swap are not the rebuild's");
            WeatherRebuild.MapsFetched();
            Assert.AreEqual(RebuildPhase.Done, WeatherRebuild.Phase);
            string text = WeatherRebuild.Describe();
            StringAssert.Contains(text, "OVER");
            StringAssert.Contains(text, "worker 20 ms");
            WeatherRebuild.Reset();
        }

        [TestMethod]
        public void ARebuildWithNothingDueGoesStraightToFetching()
        {
            WeatherRebuild.Reset();
            WeatherRebuild.Begin("empty", new int[0]);
            Assert.AreEqual(RebuildPhase.Fetching, WeatherRebuild.Phase);
            WeatherRebuild.Reset();
        }

        // --- The readouts ---

        [TestMethod]
        public void TheDensityIsPerMillionSquareKilometresAgainstTheRealBand()
        {
            // Kerbin: radius 600 km, area 4.52 million km2.
            Assert.AreEqual(4.5239, WeatherReadout.AreaMillionKm2(600000.0), 1e-3);
            Assert.AreEqual(3.5, WeatherReadout.PerMillionKm2((int)Math.Round(3.5 * WeatherReadout.AreaMillionKm2(600000.0)), 600000.0), 0.4);
            Assert.AreEqual("within", WeatherReadout.AgainstReal(3.5));
            Assert.AreEqual("below", WeatherReadout.AgainstReal(0.5));
            Assert.AreEqual("above", WeatherReadout.AgainstReal(395.9));
            Assert.AreEqual("unknown", WeatherReadout.AgainstReal(double.NaN));
            string line = WeatherReadout.DensityLine("Cells", 1791, 600000.0);
            StringAssert.StartsWith(line, "Cells 1791, 395.9 per million km2, above the real band 2 to 5 (113.1 x 3.5)");
            Assert.IsTrue(double.IsNaN(WeatherReadout.PerMillionKm2(5, 0.0)));
            StringAssert.Contains(WeatherReadout.DensityLine("Cells", 5, 0.0), "density unknown");
        }

        [TestMethod]
        public void TheSharesAreAreaWeightedAndTheTypesShareTheCoveredArea()
        {
            // The whole globe covered with type 0.5 of three types: all covered, all in the middle type.
            WeatherReadout.LayerShares all = WeatherReadout.Shares((double lat, double lon, out float c, out float t) => { c = 1f; t = 0.5f; }, 3);
            Assert.AreEqual(1.0, all.MeanCover, 1e-9);
            Assert.AreEqual(1.0, all.CoveredShare, 1e-9);
            Assert.AreEqual(1.0, all.TypeShare[1], 1e-9);
            // The northern hemisphere covered: half of the area, as the cosine weights sum.
            WeatherReadout.LayerShares half = WeatherReadout.Shares((double lat, double lon, out float c, out float t) => { c = lat > 0 ? 0.8f : 0f; t = 0f; }, 2);
            Assert.AreEqual(0.5, half.CoveredShare, 0.01);
            Assert.AreEqual(0.4, half.MeanCover, 0.01);
            Assert.AreEqual(1.0, half.TypeShare[0], 1e-9);
            // A band near the pole weighs little: the cells at 85 degrees cover a small share of the area.
            WeatherReadout.LayerShares pole = WeatherReadout.Shares((double lat, double lon, out float c, out float t) => { c = Math.Abs(lat) > 80 ? 1f : 0f; t = 1f; }, 2);
            Assert.IsTrue(pole.CoveredShare < 0.05, "a polar cap of 10 degrees is " + pole.CoveredShare);
            string text = WeatherReadout.SharesLine("high", new[] { "cirrus", "cirrostratus" }, pole);
            StringAssert.StartsWith(text, "high: cover ");
            StringAssert.Contains(text, "cirrostratus 100 %");
        }

        // --- The cost readout ---

        [TestMethod]
        public void TheCostIsSmoothedAndTheChangeIsStatedAfterItSettled()
        {
            var cost = new CostReadout();
            for (int i = 0; i < 400; i++) cost.Add(16.0, 2.0);
            Assert.AreEqual(2.0, cost.GpuMs, 0.01);
            Assert.AreEqual(16.0, cost.FrameMs, 0.01);
            cost.Mark("layers.leapScale");
            Assert.IsTrue(cost.Watching);
            StringAssert.Contains(cost.ChangeLine(), "measuring");
            for (int i = 0; i < 600; i++) cost.Add(18.0, 2.6);
            Assert.IsFalse(cost.Watching);
            Assert.AreEqual(0.6, cost.DeltaGpuMs, 0.05, "the delta is the smoothed value after the settle against the one before the change");
            Assert.IsTrue(cost.DeltaFrameMs > 0.5);
            string line = cost.ChangeLine();
            StringAssert.Contains(line, "layers.leapScale");
            StringAssert.Contains(line, "GPU +");
        }

        [TestMethod]
        public void WithoutTheProfilerOnlyTheFrameIsShownAndTheLineSaysHow()
        {
            var cost = new CostReadout();
            for (int i = 0; i < 50; i++) cost.Add(10.0, double.NaN);
            Assert.IsTrue(double.IsNaN(cost.GpuMs));
            string line = cost.NowLine(false);
            StringAssert.Contains(line, "-redefinitionGpuProfile");
            StringAssert.Contains(line, "10.00 ms (100 fps)");
            StringAssert.Contains(cost.NowLine(true), "GPU -");
            Assert.AreEqual("-", CostReadout.Ms(double.NaN).Trim());
            Assert.AreEqual("+0.25 ms", CostReadout.Signed(0.25));
            Assert.AreEqual("-0.25 ms", CostReadout.Signed(-0.25));
            Assert.IsTrue(CostReadout.SourceNote.Contains("begin and end markers"));
        }

        [TestMethod]
        public void TheProfilersSegmentReaderExistsWhereTheViewerLooksForIt()
        {
            // The viewer reaches the profiler by name (the main mod is another assembly): the type, the method and the property must stay.
            Type t = typeof(ReDefinition.Probe.GpuProfiler);
            Assert.AreEqual(GpuCostSource.ProfilerType, t.FullName);
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            Assert.IsNotNull(t.GetMethod(GpuCostSource.Method, any, null, new[] { typeof(string) }, null));
            Assert.IsNotNull(t.GetProperty(GpuCostSource.OnProperty, any));
        }

        // The start of 2026-11-16 (run3): a rebuild stood at Building for 60 s. Frame 1 made the body's entry; the old state stays in the field, so every later frame found the entry current
        // with a state and stopped before the sliced thermal table could run.
        [TestMethod]
        public void ARebuildsFramesAreNotEndedByTheOldStateThatStaysInTheField()
        {
            WeatherRebuild.Reset();
            try
            {
                WeatherRebuild.Begin("test", new[] { 3 });
                int stepsRun = 0;
                const int slices = 5;
                // The model of Step's gate: a frame runs unless the settled test says to stop; the rebuild's work ends after the slices and the worker (one more frame).
                for (int frame = 0; frame < 20 && WeatherRebuild.Phase == RebuildPhase.Building; frame++)
                {
                    if (GeneratorModule.IsSettled(true, true, WeatherRebuild.IsDue(3))) continue;
                    stepsRun++;
                    if (stepsRun == slices + 2) WeatherRebuild.Finished(3, 1.0, true);
                }
                Assert.AreEqual(slices + 2, stepsRun, "the rebuild's frames all ran");
                Assert.AreEqual(RebuildPhase.Fetching, WeatherRebuild.Phase);
                Assert.IsTrue(GeneratorModule.IsSettled(true, true, WeatherRebuild.IsDue(3)), "after the rebuild the body is settled again");
                Assert.IsFalse(GeneratorModule.IsSettled(false, true, false), "a new climate is not settled");
                Assert.IsFalse(GeneratorModule.IsSettled(true, false, false), "no state is not settled");
            }
            finally
            {
                WeatherRebuild.Reset();
            }
        }
    }
}
