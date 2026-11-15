using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Viewer;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.Viewer
{
    // The cloud viewer's model without the game: the key registry (complete against the source, defaults from the readers), the option sets and the A/B flip,
    // the presets' file form, the sidecar, the clock, the weather and pose presets, the cloud speed and the tuning table's per-key override and read check.
    [TestClass]
    public class CloudViewerTests
    {
        // A store in memory.
        private sealed class FakeStore : IOptionStore
        {
            internal readonly Dictionary<string, double> SettingValues = new Dictionary<string, double>();
            internal readonly Dictionary<string, float> OverrideValues = new Dictionary<string, float>();

            public double Setting(string key) { return SettingValues[key]; }
            public bool SetSetting(string key, double value) { SettingValues[key] = value; return true; }
            public bool TryOverride(string key, out float value) { return OverrideValues.TryGetValue(key, out value); }
            public void SetOverride(string key, float value) { OverrideValues[key] = value; }
            public void ClearOverride(string key) { OverrideValues.Remove(key); }

            public float Effective(string key, float compiledDefault)
            {
                float v;
                return OverrideValues.TryGetValue(key, out v) ? v : compiledDefault;
            }
        }

        // --- The registry ---

        [TestMethod]
        public void TheRegistryListsEachKeyOnceUnderACloudPrefix()
        {
            var seen = new HashSet<string>();
            var groups = new HashSet<string> { "cloud", "layers", "light", "cull", "recon", "shadow" };
            foreach (CloudKey k in CloudKeyRegistry.All)
            {
                Assert.IsTrue(seen.Add(k.Key), k.Key + " is listed twice");
                Assert.IsTrue(groups.Contains(k.Group), k.Key + " is not a key of the cloud pass");
                Assert.IsFalse(float.IsNaN(k.Default), k.Key);
            }
            foreach (CloudLayerKey t in CloudKeyRegistry.PerLayer) Assert.IsTrue(t.Prefix.EndsWith(".", StringComparison.Ordinal), t.Prefix);
        }

        // Every key the source of the atmospheres declares or reads under a cloud prefix is in the registry: a new key cannot be missing from the viewer.
        [TestMethod]
        public void EveryCloudKeyTheSourceReadsIsInTheRegistry()
        {
            string root = Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src", "Atmospheres");
            const string prefix = @"(?:cloud|layers|light|cull|recon|shadow)\.[A-Za-z][\w.]*";
            var declared = new Regex(@"\bTune\w*\s*=\s*""(" + prefix + @")""");
            var read = new Regex(@"Tuning\.(?:Get|Flag)\(\s*""(" + prefix + @")""");
            var missing = new List<string>();
            int found = 0;
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.IndexOf(Path.DirectorySeparatorChar + "Viewer" + Path.DirectorySeparatorChar, StringComparison.Ordinal) >= 0) continue;
                string text = File.ReadAllText(file);
                foreach (Match m in declared.Matches(text).Cast<Match>().Concat(read.Matches(text).Cast<Match>()))
                {
                    string key = m.Groups[1].Value;
                    found++;
                    bool listed = key.EndsWith(".", StringComparison.Ordinal)
                        ? CloudKeyRegistry.PerLayer.Any(t => t.Prefix == key)
                        : CloudKeyRegistry.All.Any(k => k.Key == key);
                    if (!listed) missing.Add(key + " (" + Path.GetFileName(file) + ")");
                }
            }
            Assert.IsTrue(found > 80, "the scan found only " + found + " keys");
            CollectionAssert.AreEqual(new string[0], missing.Distinct().ToArray(), string.Join(", ", missing.Distinct()));
        }

        [TestMethod]
        public void TheDefaultsAreTheReadersOwn()
        {
            Assert.AreEqual(OwnCloudOptions.AccumulateDefault, CloudKeyRegistry.Find(OwnCloudOptions.TuneAccumulate).Default);
            Assert.AreEqual(OwnCloudOptions.ReconResolutionDefault, CloudKeyRegistry.Find(OwnCloudOptions.TuneReconResolution).Default);
            Assert.AreEqual(new CloudLightSettings().MsBrightness, CloudKeyRegistry.Find(OwnCloudOptions.TuneMsBrightness).Default);
            Assert.AreEqual(new CullSettings().TapEndM, CloudKeyRegistry.Find(OwnCloudOptions.TuneTapEnd).Default);
            Assert.AreEqual(1f, CloudKeyRegistry.Find(CloudMotion.TuneScale).Default, "the cloud speed's default changes nothing");
            Assert.IsTrue(CloudKeyRegistry.Find(OwnCloudOptions.TuneDitherFix).Flag);
            Assert.AreEqual(KeyLiveness.FrameHistory, CloudKeyRegistry.Find(OwnCloudOptions.TuneAccumulate).Liveness);
            Assert.AreEqual(KeyLiveness.Change, CloudKeyRegistry.Find(CloudCoverShift.TuneShift).Liveness);
        }

        [TestMethod]
        public void PerLayerKeysAreFoundAndExpandedForTheStacksLayers()
        {
            CloudKey map = CloudKeyRegistry.Find("cloud.mapOverride.Kerbin-cumulus");
            Assert.IsNotNull(map);
            Assert.AreEqual(-1f, map.Default);
            Assert.AreEqual(KeyLiveness.Change, map.Liveness);
            Assert.IsNull(CloudKeyRegistry.Find("cloud.nothing"));
            Assert.IsNull(CloudKeyRegistry.Find("eve.slewRate"));
            List<CloudKey> keys = CloudKeyRegistry.Expand(new[] { "a", "b" });
            Assert.AreEqual(CloudKeyRegistry.All.Count + 2 * CloudKeyRegistry.PerLayer.Count, keys.Count);
            Assert.IsTrue(keys.Any(k => k.Key == "cloud.deckRule.b"));
        }

        [TestMethod]
        public void TheSearchMatchesEveryWordInTheKeyOrItsNote()
        {
            CloudKey k = CloudKeyRegistry.Find(OwnCloudOptions.TuneDitherSequence);
            Assert.IsTrue(CloudKeyRegistry.Matches(k, ""));
            Assert.IsTrue(CloudKeyRegistry.Matches(k, "DITHER"));
            Assert.IsTrue(CloudKeyRegistry.Matches(k, "layers blue"));
            Assert.IsFalse(CloudKeyRegistry.Matches(k, "layers moon"));
        }

        // --- Options, A/B, presets ---

        private static FakeStore Store()
        {
            var s = new FakeStore();
            s.SettingValues["cloudSoftness"] = 0.5;
            s.SettingValues["cloudSamples"] = 2;
            return s;
        }

        private static readonly string[] SettingKeys = { "cloudSoftness", "cloudSamples" };
        private static readonly string[] TuningKeys = { "recon.accumulate", "cloud.exposure" };

        [TestMethod]
        public void ASetCapturesTheSettingsAndOnlyTheOverridesThatAreSet()
        {
            FakeStore s = Store();
            s.SetOverride("recon.accumulate", 0.1f);
            OptionSet set = OptionSet.Capture(s, SettingKeys, TuningKeys);
            Assert.AreEqual(2, set.Settings.Count);
            Assert.AreEqual(1, set.Overrides.Count);
            Assert.AreEqual(0.1f, set.Overrides["recon.accumulate"]);
        }

        [TestMethod]
        public void TheFlipSwapsTheSidesAndAnOverrideNotInASetIsCleared()
        {
            FakeStore s = Store();
            var flip = new OptionFlip();
            Assert.IsFalse(flip.Flip(s, SettingKeys, TuningKeys), "nothing stored: nothing to flip");
            flip.StoreA(OptionSet.Capture(s, SettingKeys, TuningKeys));
            Assert.AreEqual('B', flip.Showing);
            s.SetSetting("cloudSoftness", 0.8);
            s.SetOverride("cloud.exposure", 1.5f);
            Assert.IsTrue(flip.Flip(s, SettingKeys, TuningKeys));
            Assert.AreEqual('A', flip.Showing);
            Assert.AreEqual(0.5, s.Setting("cloudSoftness"));
            Assert.IsFalse(s.OverrideValues.ContainsKey("cloud.exposure"), "A had no override of the key");
            Assert.IsTrue(flip.Flip(s, SettingKeys, TuningKeys));
            Assert.AreEqual('B', flip.Showing);
            Assert.AreEqual(0.8, s.Setting("cloudSoftness"));
            Assert.AreEqual(1.5f, s.OverrideValues["cloud.exposure"]);
        }

        [TestMethod]
        public void APresetSurvivesItsFileForm()
        {
            FakeStore s = Store();
            s.SetOverride("cloud.exposure", 1.25f);
            OptionSet set = OptionSet.Capture(s, SettingKeys, TuningKeys);
            // Written and read the way the viewer does (ModSettingsFile: ConfigNode.Save, ConfigNode.Load).
            string path = Path.Combine(Path.GetTempPath(), "cloud-viewer-preset-" + Guid.NewGuid().ToString("N") + ".cfg");
            OptionSet back;
            try
            {
                Assert.IsTrue(ModSettingsFile.Write(path, root => { foreach (ConfigNode child in set.ToNode().nodes) root.AddNode(child); }));
                back = ModSettingsFile.Read<OptionSet>(path, OptionSet.FromNode, null);
            }
            finally
            {
                File.Delete(path);
            }
            Assert.IsNotNull(back);
            CollectionAssert.AreEquivalent(set.Settings.ToList(), back.Settings.ToList());
            CollectionAssert.AreEquivalent(set.Overrides.ToList(), back.Overrides.ToList());
            Assert.AreEqual(0, OptionSet.FromNode(null).Settings.Count);
        }

        [TestMethod]
        public void APresetNameIsMadeSafeForAFile()
        {
            Assert.AreEqual("soft_A-1", ViewerPresets.SafeName("  soft A-1 "));
            Assert.AreEqual("a_b", ViewerPresets.SafeName("a/b"));
            Assert.AreEqual("", ViewerPresets.SafeName("../"));
            Assert.AreEqual("", ViewerPresets.SafeName(null));
            Assert.AreEqual(40, ViewerPresets.SafeName(new string('x', 99)).Length);
        }

        // --- Sidecar ---

        [TestMethod]
        public void TheSidecarNamesTheSceneAndOnlyTheOptionsThatDifferFromTheirDefaults()
        {
            FakeStore s = Store();
            s.SetOverride("cloud.exposure", 1.5f);
            var defs = new[]
            {
                SettingDef.Float("cloudSoftness", 0.5, 0.3, 1.0, "Clouds", "Cloud Softness"),
                SettingDef.Choice("cloudSamples", typeof(CloudDetailLevel), CloudDetailLevel.Medium, "Clouds", "Cloud Samples")
            };
            var keys = new List<CloudKey> { CloudKeyRegistry.Find("cloud.exposure"), CloudKeyRegistry.Find("recon.accumulate") };
            var record = new CaptureRecord { Picture = "p.png", Body = "Kerbin", Pose = "ground", Weather = "Overcast", Ut = 1234.5, Side = 'B', CloudSpeed = 0f };
            string text = ViewerSidecar.Format(record, defs, s.Setting, k => "", keys, s);
            ConfigNode node = ConfigNode.Parse(text).GetNode(ViewerSidecar.Head);
            Assert.IsNotNull(node, text);
            Assert.AreEqual("Kerbin", node.GetValue("body"));
            Assert.AreEqual("1234.5", node.GetValue("ut"));
            Assert.AreEqual("0", node.GetValue("cloudSpeed"));
            Assert.AreEqual("B", node.GetValue("abSide"));
            ConfigNode settings = node.GetNode("SETTINGS"), tuning = node.GetNode("TUNING");
            Assert.IsNull(settings.GetValue("cloudSoftness"), "a setting at its default is not listed");
            Assert.AreEqual("High", settings.GetValue("cloudSamples"));
            Assert.AreEqual("1.5", tuning.GetValue("cloud.exposure"));
            Assert.IsNull(tuning.GetValue("recon.accumulate"));
        }

        // --- Clock ---

        [TestMethod]
        public void TheClockLeavesNoShiftInTheGamesTime()
        {
            var clock = new ViewerClock(1000.0);
            Assert.AreEqual(1001.0, clock.Step(1001.0));
            clock.OffsetS = 3600.0;
            Assert.AreEqual(4602.0, clock.Step(1002.0), "the offset moves the time shown");
            Assert.AreEqual(1002.0, clock.Natural);
            clock.Freeze(true);
            Assert.AreEqual(4602.0, clock.Step(4603.0));
            Assert.AreEqual(4602.0, clock.Step(4603.5), "frozen: the time stays");
            Assert.AreEqual(1004.5, clock.Natural, "the natural time runs on while frozen");
            clock.Freeze(false);
            Assert.AreEqual(4602.5, clock.Step(4602.5), "it runs on from where it was held");
        }

        // --- Capture ---

        // The screen is read at the end of the frame the picture is asked for in: the window is hidden from the request through that frame and shown in the next.
        [TestMethod]
        public void TheWindowStaysHiddenThroughTheFrameThePictureIsAskedFor()
        {
            var schedule = new CaptureSchedule(2);
            Assert.IsFalse(schedule.Busy);
            Assert.IsFalse(schedule.Step(), "no request: nothing happens");
            Assert.IsTrue(schedule.Request());
            Assert.IsFalse(schedule.Request(), "one picture at a time");
            var hidden = new List<bool>();
            int askedIn = -1;
            for (int frame = 1; frame <= 6; frame++)
            {
                if (schedule.Step()) askedIn = frame;
                hidden.Add(schedule.Busy);
            }
            Assert.AreEqual(3, askedIn, "asked for after the two hidden frames");
            CollectionAssert.AreEqual(new[] { true, true, true, false, false, false }, hidden, "hidden in frames 1 to 3, the one the picture is asked for in included");
            Assert.IsTrue(schedule.Request(), "the next picture can start once the window is back");
        }

        // --- Weather, poses, altitude, speed ---

        private static readonly ViewerLayer[] Kerbin =
        {
            new ViewerLayer { Name = "Kerbin-weather-rain", Group = "weather", InnerAltitudeM = 300f, ThicknessM = 3000f },
            new ViewerLayer { Name = "Kerbin-cumulus", Group = "", InnerAltitudeM = 600f, ThicknessM = 7000f },
            new ViewerLayer { Name = "Kerbin-cumulonimbus", Group = "", InnerAltitudeM = 500f, ThicknessM = 12000f },
            new ViewerLayer { Name = "Kerbin-cirrus", Group = "", InnerAltitudeM = 9000f, ThicknessM = 2000f }
        };

        [TestMethod]
        public void TheRolesOfTheLayersAreReadFromTheirGroupAndName()
        {
            Assert.AreEqual(WeatherPresets.Role.Deck, WeatherPresets.RoleOf(Kerbin[0]));
            Assert.AreEqual(WeatherPresets.Role.Cumulus, WeatherPresets.RoleOf(Kerbin[1]));
            Assert.AreEqual(WeatherPresets.Role.Cumulonimbus, WeatherPresets.RoleOf(Kerbin[2]));
            Assert.AreEqual(WeatherPresets.Role.Cirrus, WeatherPresets.RoleOf(Kerbin[3]));
        }

        [TestMethod]
        public void TheRainDeckClosesTheDeckAndTheFieldClearsEveryOverride()
        {
            Dictionary<string, float> rain = WeatherPresets.Overrides(ViewerWeather.RainDeck, Kerbin).ToDictionary(p => p.Key, p => p.Value);
            Assert.AreEqual(1f, rain["cloud.mapOverride.Kerbin-weather-rain"]);
            Assert.AreEqual(0f, rain["cloud.deckRule.Kerbin-weather-rain"], "the deck rule off: the constant map draws everywhere");
            Assert.AreEqual(0f, rain["cloud.mapOverride.Kerbin-cumulonimbus"]);
            Assert.IsTrue(float.IsNaN(rain["cloud.typeOverride.Kerbin-cirrus"]));
            double level;
            ForceType type;
            WeatherPresets.Precipitation(ViewerWeather.RainDeck, out level, out type);
            Assert.AreEqual(ForceType.Rain, type);
            Assert.IsTrue(level > 0.0);
            List<KeyValuePair<string, float>> field = WeatherPresets.Overrides(ViewerWeather.Field, Kerbin);
            Assert.AreEqual(Kerbin.Length * 3, field.Count);
            Assert.IsTrue(field.All(p => float.IsNaN(p.Value)));
            // Every key a preset writes is a key of the registry.
            foreach (KeyValuePair<string, float> p in rain) Assert.IsNotNull(CloudKeyRegistry.Find(p.Key), p.Key);
        }

        [TestMethod]
        public void ThePosesTakeTheProbesViewsAndTheStacksHeights()
        {
            List<ViewerPose> poses = PosePresets.For(Kerbin);
            Assert.AreEqual(T09Probe.Views.Length + 2, poses.Count);
            Assert.AreEqual(600.0 + 3500.0, poses.First(p => p.Name == "inside").AglM, 1e-6);
            Assert.AreEqual(7600.0 + PosePresets.AboveDeckMarginM, poses.First(p => p.Name == "above").AglM, 1e-6);
            Assert.AreEqual(T09Probe.Views[0].AglM, PosePresets.For(new ViewerLayer[0]).First(p => p.Name == "ground").AglM);
            Assert.AreEqual(4550.0, PosePresets.For(new ViewerLayer[0]).First(p => p.Name == "inside").AglM, "no stack: the probe's own height");
        }

        [TestMethod]
        public void TheAltitudeSliderRunsOnALogScaleWithinItsRange()
        {
            double max = AltitudeScale.MaxAglM(70000.0);
            Assert.AreEqual(300000.0, max);
            Assert.AreEqual(5000.0, AltitudeScale.FromSlider(AltitudeScale.ToSlider(5000.0), max), 0.01);
            Assert.AreEqual(AltitudeScale.MinAglM, AltitudeScale.FromSlider(-3f, max));
            Assert.AreEqual(max, AltitudeScale.FromSlider(9f, max));
        }

        [TestMethod]
        public void TheCloudSpeedIsClampedAndItsDefaultIsOne()
        {
            Assert.AreEqual(1f, CloudMotion.DefaultScale);
            Assert.AreEqual(0f, CloudMotion.Clamp(-2f));
            Assert.AreEqual(CloudMotion.MaxScale, CloudMotion.Clamp(500f));
            Assert.AreEqual(1f, CloudMotion.Clamp(float.NaN));
            Assert.AreEqual(7.5f, CloudMotion.Clamp(7.5f));
        }

        // --- The tuning table ---

        [TestMethod]
        public void OneOverrideIsClearedAndTheReadsOfAWatchedKeyAreSeen()
        {
            var table = new TuningTable();
            table.SetOverride("a", 2f);
            table.SetOverride("b", 3f);
            table.BeginFrame(1);
            float v;
            Assert.IsTrue(table.TryGetOverride("a", out v));
            Assert.AreEqual(2f, v);
            table.ClearOverride("a");
            table.BeginFrame(2);
            Assert.AreEqual(7f, table.Get("a", 7f), "the compiled default stands again");
            Assert.AreEqual(3f, table.Get("b", 7f), "the other override stays");
            Assert.IsFalse(table.TryGetLoaded("a", out v));
            table.Watch("b");
            Assert.AreEqual(int.MinValue, table.LastWatchedRead);
            table.BeginFrame(3);
            table.Get("a", 0f);
            Assert.AreEqual(int.MinValue, table.LastWatchedRead, "another key's read is not the watched key's");
            table.Get("b", 0f);
            Assert.AreEqual(3, table.LastWatchedRead);
            table.Watch(null);
        }

        // The viewer's labels and sidecar look at keys every frame or at every picture: that is not the pass reading the key.
        [TestMethod]
        public void APeekIsNotAReadOfTheWatchedKey()
        {
            var table = new TuningTable();
            table.SetOverride("b", 3f);
            table.BeginFrame(1);
            table.Watch("b");
            table.BeginFrame(2);
            Assert.AreEqual(3f, table.Peek("b", 7f));
            Assert.AreEqual(7f, table.Peek("nothing", 7f));
            Assert.AreEqual(int.MinValue, table.LastWatchedRead, "only a reader's Get counts");
            table.Get("b", 0f);
            Assert.AreEqual(2, table.LastWatchedRead);
            table.Watch(null);
        }
    }
}
