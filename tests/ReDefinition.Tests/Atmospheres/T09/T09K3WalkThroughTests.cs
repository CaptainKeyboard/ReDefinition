using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // Series K-T3 (T09ProbeK3.cs, the table in T09ProbeK3.Table.cs) against the fake world: the variants of a start run in order, each with its keys on while its window is open and back at
    // the start's values after it, the windows carry the K-T2 begin and end lines, the numeric frame and the aid frame are saved under unique names, and the table is consistent.
    public partial class T09WalkThroughTests
    {
        private Run StartK3(string[] variants, Action<Run> tweak = null, string startId = "k3near")
        {
            return Start(variants, x =>
            {
                x.Field.Cover = DiagCover;
                x.World.LayerReport = FourLayers(false);
                if (tweak != null) tweak(x);
            }, "1707", startId, "K-T3");
        }

        // The value of a setting while the window of a variant is open: the first frame after its "T09 view begin" line.
        private static Action<Run> Sample(Dictionary<string, Dictionary<string, double>> seen, string[] keys, string[] tunes = null)
        {
            var done = new HashSet<string>();
            return r =>
            {
                string last = r.Log.Count == 0 ? null : r.Log[r.Log.Count - 1];
                if (last == null || !last.StartsWith("T09 view begin: ", StringComparison.Ordinal)) return;
                string[] parts = last.Split(' ');
                string id = parts[4];
                if (!done.Add(id)) return;
                var values = new Dictionary<string, double>();
                foreach (string k in keys) values[k] = AtmospheresSettings.Number(k);
                if (tunes != null) foreach (string t in tunes) values[t] = Tuning.Get(t, -99f);
                seen[id] = values;
            };
        }

        [TestMethod]
        public void K3RunsEachVariantWithItsKeysOnAndTheStartsValuesBackAfterIt()
        {
            string[] ids = { "off_g", "plain_g", "noise_off_g", "tier_low_g", "h_all_off_g", "tint1_g", "off_a", "plain_a", "h_top_off_a" };
            Run r = StartK3(ids);
            var seen = new Dictionary<string, Dictionary<string, double>>();
            string[] keys = { AtmospheresSettings.Keys.CloudNoise, AtmospheresSettings.Keys.CloudNoiseTier, AtmospheresSettings.Keys.CloudBaseVariation, AtmospheresSettings.Keys.CloudBasePhysics,
                              AtmospheresSettings.Keys.CloudTopFromUpdraft, AtmospheresSettings.Keys.BaseRagged, AtmospheresSettings.Keys.CloudSource };
            Drive(r, 0.02, 3600.0, Sample(seen, keys, new[] { CloudLookTuning.TuneSunTint }));
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(1, r.Quits);
            Assert.AreEqual("k3near", r.Probe.StartId);
            Assert.IsTrue(r.Rows.All(l => l.Contains("\"series\":\"K-T3\"")), "every row names the series K-T3");
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("Probe: series K-T3 start k3near begins with variants " + string.Join(",", ids))));
            Assert.AreEqual((double)ids.Length, Number(r, "K3.windows"));

            double noiseDefault = AtmospheresSettings.Definition(AtmospheresSettings.Keys.CloudNoise).Default;
            double tierDefault = AtmospheresSettings.Definition(AtmospheresSettings.Keys.CloudNoiseTier).Default;
            Assert.AreEqual(0.0, seen["noise_off_g"][AtmospheresSettings.Keys.CloudNoise], "noise off while its window is open");
            Assert.AreEqual(noiseDefault, seen["tier_low_g"][AtmospheresSettings.Keys.CloudNoise], "the next variant starts from the start's value");
            Assert.AreEqual(0.0, seen["tier_low_g"][AtmospheresSettings.Keys.CloudNoiseTier]);
            Assert.AreEqual(tierDefault, seen["h_all_off_g"][AtmospheresSettings.Keys.CloudNoiseTier], "the tier is back");
            foreach (string k in new[] { AtmospheresSettings.Keys.CloudBaseVariation, AtmospheresSettings.Keys.CloudBasePhysics, AtmospheresSettings.Keys.CloudTopFromUpdraft, AtmospheresSettings.Keys.BaseRagged })
            {
                Assert.AreEqual(0.0, seen["h_all_off_g"][k], "T13 switch " + k + " off in h_all_off_g");
                Assert.AreEqual(1.0, seen["tint1_g"][k], "T13 switch " + k + " on again after it");
                Assert.AreEqual(1.0, seen["plain_a"][k]);
            }
            Assert.AreEqual(0.0, seen["h_top_off_a"][AtmospheresSettings.Keys.CloudTopFromUpdraft]);
            Assert.AreEqual(1.0, seen["h_top_off_a"][AtmospheresSettings.Keys.CloudBaseVariation], "only the top switch is off");
            Assert.AreEqual(1.0, seen["tint1_g"][CloudLookTuning.TuneSunTint], 1e-6, "the tuning key is on in its window");
            Assert.AreEqual(-99.0, seen["noise_off_g"][CloudLookTuning.TuneSunTint], 1e-6, "and gone in the others");
            Assert.AreEqual((double)(int)CloudSourceKind.None, seen["off_g"][AtmospheresSettings.Keys.CloudSource], "the off window has no clouds");
            Assert.AreEqual((double)(int)CloudSourceKind.Ours, seen["plain_g"][AtmospheresSettings.Keys.CloudSource]);

            // Each window: the K-T2 lines the evaluator reads, the numeric frame, the aid frame (drawn variants), unique picture names.
            foreach (string id in ids)
            {
                string view = id.EndsWith("_a", StringComparison.Ordinal) ? "above" : "ground";
                int begin = r.Log.FindIndex(l => l.StartsWith("T09 view begin: " + view + " " + id + " 1707x960"));
                int end = r.Log.FindIndex(l => l.StartsWith("T09 view end: " + view + " " + id + " 1707x960"));
                Assert.IsTrue(begin >= 0 && end > begin, id + " has a begin and an end line");
                if (!id.StartsWith("off_", StringComparison.Ordinal))
                {
                    Assert.IsTrue(Number(r, "T09d." + view + ".k3." + id + ".deck_median", id) > 0, id + " has a deck in its numeric frame");
                    Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-" + view + "_k3-" + id + "-1707x960-natural-half")), id + " natural picture");
                    Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-" + view + "_k3-aid1" + id + "-1707x960-aid-direct-half")), id + " aid picture");
                }
                else
                {
                    Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-" + view + "_k3-" + id + "-1707x960-natural-half")), id + " baseline picture");
                }
            }
            Assert.AreEqual(r.World.Saved.Count, r.World.Saved.Distinct().Count(), "every picture has its own name");
            foreach (string id in new[] { "plain_g", "noise_off_g", "tier_low_g", "h_all_off_g" })
                Assert.IsTrue(Values(r, "T09.noise.ground.flicker", id).Count > 0, id + " has a noise read");
            Assert.AreEqual(0, Values(r, "T09.noise.ground.flicker", "tint1_g").Count, "tint1_g has none");
            Assert.IsTrue(Values(r, "K3.keys", "noise_off_g")[0].Value.Contains("cloudNoise=0"), "the keys of a variant are written");
        }

        [TestMethod]
        public void K3SkyViewTakesThePicturesWithoutTheNumericFrame()
        {
            Run r = StartK3(new[] { "off_s", "plain_s", "skip_closed_s" });
            Drive(r, 0.02);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-sky3-skip_closed_s-1707x960-natural")), string.Join(",", r.World.Saved.ToArray()));
            Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-sky3-skip_closed_s-1707x960-aid-opticaldepth")));
            Assert.IsFalse(r.Rows.Any(l => l.Contains("T09d.sky3.")), "no numeric frame in the sky3 view");
        }

        [TestMethod]
        public void K3RainSetsTheDiagnosticsLevelAndGivesItBackToTheNextVariant()
        {
            Run r = StartK3(new[] { "off_r", "rain_r", "rain_wet_r", "wetdiff03_r", "off_g", "plain_g" });
            var seen = new Dictionary<string, Dictionary<string, double>>();
            Drive(r, 0.02, 3600.0, Sample(seen, new[] { AtmospheresSettings.Keys.ForceLevel, AtmospheresSettings.Keys.ForceType, AtmospheresSettings.Keys.WetSurfaces, AtmospheresSettings.Keys.WindowDroplets,
                                                       AtmospheresSettings.Keys.CloudSource }, new[] { "wet.wetDiffuse" }));
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(0.8, seen["off_r"][AtmospheresSettings.Keys.ForceLevel], 1e-6, "the rain window without clouds keeps the rain");
            Assert.AreEqual((double)(int)CloudSourceKind.None, seen["off_r"][AtmospheresSettings.Keys.CloudSource]);
            Assert.AreEqual(0.8, seen["rain_r"][AtmospheresSettings.Keys.ForceLevel], 1e-6);
            Assert.AreEqual((double)(int)ForceType.Rain, seen["rain_r"][AtmospheresSettings.Keys.ForceType]);
            Assert.AreEqual(0.0, seen["rain_r"][AtmospheresSettings.Keys.WetSurfaces], "the rain alone has no wet overlay");
            Assert.AreEqual(1.0, seen["rain_wet_r"][AtmospheresSettings.Keys.WetSurfaces]);
            Assert.AreEqual(0.3, seen["wetdiff03_r"]["wet.wetDiffuse"], 1e-6);
            Assert.AreEqual(-99.0, seen["plain_g"]["wet.wetDiffuse"], 1e-6);
            Assert.AreEqual(-1.0, seen["plain_g"][AtmospheresSettings.Keys.ForceLevel], 1e-6, "the clear sky is back");
            Assert.AreEqual(0.0, seen["plain_g"][AtmospheresSettings.Keys.WetSurfaces]);
            Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-ground_k3-rain_wet_r-1707x960-natural-half")), "the rain frames have their own names");
        }

        [TestMethod]
        public void K3StormVariantsNameThatTheLightningModuleIsNotThereInTheFake()
        {
            Run r = StartK3(new[] { "off_h", "plain_h", "ln_on_h", "ln_max1_h" });
            Drive(r, 0.02);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(1, r.Rows.Count(l => l.Contains("T09.warning.k3_no_lightning_module")), "named once");
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 view begin: horizon ln_max1_h 1707x960")));
            Assert.AreEqual(0, Values(r, "K3.flashes", "ln_on_h").Count, "no flash count without the module");
        }

        [TestMethod]
        public void K3SheetLayerVariantsSetTheKeyOfTheLayerTheStackNames()
        {
            Run r = StartK3(new[] { "plain_g", "lshift_g", "tfair_g", "toverride_g" });
            var seen = new Dictionary<string, Dictionary<string, double>>();
            string layer = CloudLookFixtures.Cumulus().Name;
            Drive(r, 0.02, 3600.0, Sample(seen, new string[0], new[] { CloudLookTuning.TuneLayerShift + "Kerbin-cumulus", CloudLookTuning.TuneTypeFair + "Kerbin-cumulus", CloudLookTuning.TuneTypeOverride + "Kerbin-cumulus" }));
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual("Kerbin-cumulus", layer);
            Assert.AreEqual(-0.1, seen["lshift_g"][CloudLookTuning.TuneLayerShift + "Kerbin-cumulus"], 1e-6);
            Assert.AreEqual(0.5, seen["tfair_g"][CloudLookTuning.TuneTypeFair + "Kerbin-cumulus"], 1e-6);
            Assert.AreEqual(1.0, seen["toverride_g"][CloudLookTuning.TuneTypeOverride + "Kerbin-cumulus"], 1e-6);
            Assert.AreEqual(-99.0, seen["tfair_g"][CloudLookTuning.TuneLayerShift + "Kerbin-cumulus"], 1e-6, "the previous variant's key is gone");
            Assert.AreEqual("Kerbin-cumulus", Values(r, "K3.sheet_layer", "lshift_g")[0].Value);
        }

        // The variants of each start of tests/series/K-T3.json, in order.
        private static List<KeyValuePair<string, string[]>> K3JsonStarts()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string path = null;
            while (dir != null)
            {
                string candidate = System.IO.Path.Combine(dir, "tests", "series", "K-T3.json");
                if (System.IO.File.Exists(candidate)) { path = candidate; break; }
                dir = System.IO.Path.GetDirectoryName(dir);
            }
            Assert.IsNotNull(path, "tests/series/K-T3.json is above the test binaries");
            string text = System.IO.File.ReadAllText(path);
            var starts = new List<KeyValuePair<string, string[]>>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "\"id\": \"(k3\\w+)\".*?\"variants\": \\[(.*?)\\]", System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                string[] ids = System.Text.RegularExpressions.Regex.Matches(m.Groups[2].Value, "\"([a-z0-9_]+)\"").Cast<System.Text.RegularExpressions.Match>().Select(x => x.Groups[1].Value).ToArray();
                starts.Add(new KeyValuePair<string, string[]>(m.Groups[1].Value, ids));
            }
            return starts;
        }

        [TestMethod]
        public void K3EveryStartOfTheSeriesRunsToTheEndInsideTheLimitAtTheFakeFrameRate()
        {
            List<KeyValuePair<string, string[]>> starts = K3JsonStarts();
            Assert.AreEqual(5, starts.Count);
            int windows = 0;
            foreach (KeyValuePair<string, string[]> s in starts)
            {
                Run r = StartK3(s.Value, null, s.Key);
                Drive(r, RealDt);
                Assert.IsTrue(r.Probe.Passed, s.Key + "\n" + Dump(r));
                Assert.AreEqual((double)s.Value.Length, Number(r, "K3.windows"), s.Key + ": every variant ran");
                Assert.AreEqual(0, r.Rows.Count(l => l.Contains("T09.warning.pictures_time")), s.Key + ": the pictures did not stop for time");
                Assert.IsTrue(r.World.RealS < 19 * 60 - 60, s.Key + " took " + r.World.RealS.ToString("0") + " s of the fake clock");
                Assert.AreEqual(r.World.Saved.Count, r.World.Saved.Distinct().Count(), s.Key + ": unique picture names");
                windows += s.Value.Length;
            }
            Assert.AreEqual(125, windows);
        }

        [TestMethod]
        public void K3AVariantTheTableDoesNotKnowFailsTheStartByName()
        {
            Run r = StartK3(new[] { "plain_g", "no_such_variant" });
            Drive(r, 0.05);
            AssertNamedFailure(r, "K-T3 has no variant no_such_variant");
        }

        [TestMethod]
        public void K3TheTableIsConsistent()
        {
            var seen = new HashSet<string>();
            var letters = new Dictionary<string, string> { { "ground", "_g" }, { "above", "_a" }, { "horizon", "_h" }, { "sky3", "_s" } };
            foreach (string id in K3Variants.AllIds)
            {
                Assert.IsTrue(seen.Add(id), "unique id " + id);
                K3Variant v;
                Assert.IsTrue(K3Variants.TryGet(id, out v));
                Assert.IsTrue(letters.ContainsKey(v.View), id + " names a known view");
                Assert.IsTrue(id.EndsWith(letters[v.View], StringComparison.Ordinal) || (v.View == "ground" && id.EndsWith("_r", StringComparison.Ordinal) && v.Group == "rain" || id == "off_r"),
                              id + " ends in its view's letter " + letters[v.View] + " (_r: the ground view in the rain)");
                Assert.AreEqual(v.View != K3Variants.Sky3, v.Readout, id + ": the sky3 view has no numeric frame");
                foreach (var s in v.Sets)
                {
                    SettingDef def = AtmospheresSettings.Definition(s.Key);
                    Assert.IsNotNull(def, id + ": " + s.Key + " is a setting");
                    Assert.AreEqual(s.Value, def.Limit(s.Value), 1e-6, id + ": " + s.Key + " = " + s.Value + " lies inside its range");
                }
                foreach (var t in v.Tunes) Assert.IsTrue(t.Key.Contains("."), id + ": " + t.Key + " is a dotted tuning key");
                if (v.Off) Assert.AreEqual(0, v.Tunes.Count + v.Sets.Count(s => s.Key != AtmospheresSettings.Keys.ForceLevel && s.Key != AtmospheresSettings.Keys.ForceType), id + ": an off window changes nothing but the rain");
                if (v.Special == K3Special.Storm) Assert.IsTrue(v.Sets.Any(s => s.Key == AtmospheresSettings.Keys.Lightning && s.Value == 1.0), id + " switches the lightning on");
            }
            Assert.IsTrue(K3Variants.AllIds.Count >= 100, "the table holds " + K3Variants.AllIds.Count);
        }
    }
}
