using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The start soft59 of K-T2 (T09ProbeSoft.cs, E40 soft density): the variant table, its expectations, the plan against the time limit, and the whole scenario against the fake world:
    // both scenes, every view, every variant's three frames and its cost windows, the sun colour line, the failures it must name, and the arguments the runner passes.
    public partial class T09WalkThroughTests
    {
        private static readonly string[] SoftRemoved = { "cloudEntryRefinement", "refine", "surface", "layers.cloudStep", "layers.entryRamp", "cloudHashDither", "layers.edgePower" };

        private Run StartSoft(Action<Run> tweak = null)
        {
            return Start(new[] { "plain" }, x =>
            {
                x.World.LayerReport = FourLayers(false);
                if (tweak != null) tweak(x);
            }, "1707", "soft59", "K-T2");
        }

        // What the module does every frame (FillFrame's first line): the settings into the march's numbers and the soft rim; and, once, the sun colour the module would have read.
        private static void SoftFrame(Run r, Color scene, Color unmodulated, Color pass)
        {
            OwnCloudModule m = r.World.Module;
            OwnCloudOptions.Apply(m.Frame);
            if (m.SunRead.FrameNumber != 0) return;
            FieldInfo f = typeof(OwnCloudModule).GetField("sunRead", BindingFlags.Instance | BindingFlags.NonPublic);
            object read = new OwnCloudModule.SunColourRead
            {
                FrameNumber = 7, Scene = scene, Unmodulated = unmodulated, Pass = pass, HaveUnmodulated = true, ScattererLoaded = true, AirFromScatterer = true, Divided = true, SunAirHeightM = 7000f,
                SinElevation = Math.Sin(45.0 * Math.PI / 180.0), ViewerAltitudeM = 200.0
            };
            f.SetValue(m, read);
        }

        private static void SoftDrive(Run r, double dt = 0.04)
        {
            Drive(r, dt, 3600.0, x => SoftFrame(x, new Color(1.4f, 1.2f, 0.97f), new Color(2.0f, 1.9f, 1.85f), new Color(1.95f, 1.86f, 1.8f)));
        }

        [TestMethod]
        public void TheSoftTableIsTheTableOfTheEveningAndUsesNoRemovedKey()
        {
            var names = T09Probe.SoftVariants.Select(v => v.Name).ToList();
            CollectionAssert.AreEqual(new[] { "soft", "hard", "soft100s24", "softhash", "softr33", "softcut16", "softnodrop" }, names);
            Assert.AreEqual(names.Count, names.Distinct().Count(), "the names are unique");
            Assert.AreEqual(0, T09Probe.SoftVariants.Count(v => !v.Active), "no empty slot is left");
            T09Probe.SoftVariant hash = T09Probe.SoftVariants.First(v => v.Name == "softhash");
            Assert.AreEqual(1, hash.Pairs.Length);
            Assert.AreEqual("", hash.FairCost + hash.ClockCost, "softhash has frames and no cost window");
            Assert.IsTrue(hash.Runs(true, 'g') && hash.Runs(true, 'a'), "softhash runs in both fair views");
            Assert.IsFalse(T09Probe.SoftClockViews.Any(c => hash.Runs(false, c)), "softhash is not in the clock scene");
            // The table as built: { fair cost, clock cost, fair views run, clock views run } (the lead's table less the two cuts for the time: soft100s24's clock window, softnodrop's clock view).
            var expected = new Dictionary<string, string[]>
            {
                { "soft", new[] { "ga", "gias", "ga", "gias" } }, { "hard", new[] { "g", "g", "ga", "gias" } }, { "soft100s24", new[] { "g", "", "ga", "gias" } },
                { "softhash", new[] { "", "", "ga", "" } }, { "softr33", new[] { "ga", "gias", "ga", "gias" } },
                { "softcut16", new[] { "g", "g", "g", "g" } }, { "softnodrop", new[] { "g", "", "g", "" } }
            };
            foreach (T09Probe.SoftVariant v in T09Probe.SoftVariants)
            {
                string[] row = expected[v.Name];
                Assert.AreEqual(row[0], v.FairCost, v.Name + " fair cost");
                Assert.AreEqual(row[1], v.ClockCost, v.Name + " clock cost");
                foreach (char c in "ga") Assert.AreEqual(row[2].IndexOf(c) >= 0, v.Runs(true, c), v.Name + " fair view " + c);
                foreach (char c in "gias") Assert.AreEqual(row[3].IndexOf(c) >= 0, v.Runs(false, c), v.Name + " clock view " + c);
                Assert.IsNull(v.TableProblem(), v.Name + " is a sound row");
            }
            // A cost window outside the views a variant runs in is a table error.
            Assert.IsNotNull(new T09Probe.SoftVariant { Name = "bad", Pairs = new string[0], FairCost = "a", FairViews = "g" }.TableProblem());
            Assert.IsNotNull(new T09Probe.SoftVariant { Name = "bad", Pairs = new string[0], ClockCost = "g", ClockViews = "-" }.TableProblem());
            Assert.IsNotNull(new T09Probe.SoftVariant { Name = "bad", Pairs = new string[0], ClockCost = "x" }.TableProblem());
            Assert.IsNull(new T09Probe.SoftVariant { Name = "ok", Pairs = new string[0], ClockCost = "gi" }.TableProblem());
            foreach (T09Probe.SoftVariant v in T09Probe.SoftVariants.Where(x => x.Active))
            {
                foreach (string pair in v.Pairs)
                {
                    bool setting;
                    string key;
                    float value;
                    Assert.IsTrue(T09Probe.SoftSplit(pair, out setting, out key, out value), v.Name + ": " + pair);
                    if (setting) Assert.IsNotNull(AtmospheresSettings.Definition(key), v.Name + ": the setting " + key + " exists");
                    foreach (string removed in SoftRemoved) Assert.IsFalse(key.Contains(removed), v.Name + ": " + key + " is on the removal list");
                }
                foreach (char c in v.FairCost) Assert.IsTrue("ga".IndexOf(c) >= 0, v.Name + ": fair cost view " + c);
                foreach (char c in v.ClockCost) Assert.IsTrue("gias".IndexOf(c) >= 0, v.Name + ": clock cost view " + c);
            }
        }

        [TestMethod]
        public void TheVariantsMeanWhatTheirNamesSay()
        {
            T09Probe.SoftExpectation soft = T09Probe.SoftExpect(T09Probe.SoftVariants.First(v => v.Name == "soft"));
            Assert.AreEqual(0.5, soft.Softness, 1e-9, "soft is the default softness");
            Assert.IsTrue(soft.SoftDensity, "soft has the soft density on");
            // The owner's soft48 is the default: cloudSamples High is 48 grid points at the Medium step quality (the Medium tier does not set cloudSamples).
            Assert.AreEqual(48, soft.Samples);
            Assert.AreEqual(48, soft.GridPoints);
            Assert.AreEqual(AtmospheresSettings.Definition(AtmospheresSettings.Keys.CloudSoftness).Default, soft.Softness, 1e-9, "soft stands on the settings' defaults");

            T09Probe.SoftExpectation hard = T09Probe.SoftExpect(T09Probe.SoftVariants.First(v => v.Name == "hard"));
            Assert.IsFalse(hard.SoftDensity, "hard is the density before");
            Assert.AreEqual(soft.GridPoints, hard.GridPoints, "hard differs by the density alone");
            Assert.AreEqual(soft.Softness, hard.Softness, 1e-9);

            // The example of the table's comment: cloudSamples Medium is 32 grid points.
            T09Probe.SoftExpectation s32 = T09Probe.SoftExpect(new T09Probe.SoftVariant { Name = "soft32", Pairs = new[] { "s:" + AtmospheresSettings.Keys.CloudSamples + "=1" } });
            Assert.AreEqual(32, s32.GridPoints);
            Assert.AreEqual(0.5, s32.Softness, 1e-9);

            T09Probe.SoftExpectation s100 = T09Probe.SoftExpect(T09Probe.SoftVariants.First(v => v.Name == "soft100s24"));
            Assert.AreEqual(24, s100.GridPoints);
            Assert.AreEqual(1.0, s100.Softness, 1e-9);
            Assert.IsTrue(s100.SoftDensity);

            // The cost candidates: each differs from soft by its own switches alone.
            Func<string, T09Probe.SoftExpectation> of = n => T09Probe.SoftExpect(T09Probe.SoftVariants.First(v => v.Name == n));
            Assert.AreEqual(0.5f, soft.Resolution, 1e-6f, "the march target is half size by default");
            Assert.IsFalse(soft.SkipEmpty, "cloudSkipEmpty is off by default (so softr33 sets it)");
            Assert.IsFalse(soft.SpanTight);
            Assert.IsTrue(soft.DropEmpty, "the drop of layers with no visible cloud is on by default");
            Assert.AreEqual(2, soft.DitherSequence);
            T09Probe.SoftExpectation r33 = of("softr33");
            Assert.AreEqual(0.33f, r33.Resolution, 1e-6f);
            Assert.IsTrue(r33.SkipEmpty);
            Assert.AreEqual(48, r33.GridPoints);
            Assert.IsTrue(r33.DropEmpty && !r33.SpanTight && r33.SoftDensity);
            T09Probe.SoftExpectation cut = of("softcut16");
            Assert.AreEqual(16, cut.GridPoints, "48 points at gridFine 0.3333");
            Assert.IsTrue(cut.SpanTight);
            Assert.AreEqual(0.33f, cut.Resolution, 1e-6f);
            Assert.IsFalse(cut.SkipEmpty);
            T09Probe.SoftExpectation nodrop = of("softnodrop");
            Assert.IsFalse(nodrop.DropEmpty);
            Assert.AreEqual(0.5f, nodrop.Resolution, 1e-6f);
            Assert.AreEqual(1, of("softhash").DitherSequence);
            Assert.IsTrue(of("softhash").DropEmpty);
            int lw, lh;
            T09Probe.SoftMarchSize(1707, 960, 0.33f, out lw, out lh);
            Assert.AreEqual(563, lw);
            Assert.AreEqual(317, lh);
            T09Probe.SoftMarchSize(1707, 960, 0.5f, out lw, out lh);
            Assert.AreEqual(854, lw);
            Assert.AreEqual(480, lh);
            int none, total;
            T09Probe.SoftCountNone("L0 none L1 608-8042 L2 table 430-9990 L3 none", out none, out total);
            Assert.AreEqual(2, none);
            Assert.AreEqual(4, total);
        }

        [TestMethod]
        public void TheDefaultTableFitsTheTimeAndEachExtraCandidateIsPriced()
        {
            double estimate = T09Probe.SoftEstimateS(T09Probe.SoftVariants);
            Console.WriteLine("soft59 estimate " + estimate.ToString("0") + " s of " + T09Probe.SoftMaxSeconds.ToString("0") + " s");
            Assert.IsTrue(estimate <= T09Probe.SoftMaxSeconds - 60.0, "the default table needs " + estimate.ToString("0") + " s of " + T09Probe.SoftMaxSeconds.ToString("0"));
            Assert.AreEqual(1020.0, T09Probe.SoftMaxSeconds - 60.0, "the pin: 1020 s");
            Assert.IsTrue(estimate >= 600.0, "the estimate counts the windows: " + estimate.ToString("0"));
            Assert.AreEqual(1005.0, estimate, 1e-6, "the table as built: 1005 s (1058 s with soft100s24's clock window and softnodrop's clock view)");
            Assert.IsTrue(T09Probe.SoftPlaceS >= 7.0, "a placement was measured at 3.5 s: the estimate keeps at least twice that");
            // A cost candidate with one window in each of the ground views: the price of one more variant.
            var table = T09Probe.SoftVariants.Select(v => v).ToList();
            table.Add(new T09Probe.SoftVariant { Name = "extra", Pairs = new[] { "t:layers.lightStride=2" }, FairCost = "g", ClockCost = "g" });
            double withExtra = T09Probe.SoftEstimateS(table);
            Console.WriteLine("with one candidate (cost at the ground in both scenes) " + withExtra.ToString("0") + " s");
            Assert.IsTrue(withExtra > estimate + 90.0, "a variant costs its frames and windows: " + (withExtra - estimate).ToString("0") + " s");
            // A variant that does not run in a view costs nothing there.
            var hashOnly = new List<T09Probe.SoftVariant> { T09Probe.SoftVariants.First(v => v.Name == "softhash") };
            Assert.AreEqual(T09Probe.SoftEstimateViewS(true, 'g', new List<T09Probe.SoftVariant>()) + T09Probe.SoftSwitchS + 3 * T09Probe.SoftFrameReadS, T09Probe.SoftEstimateViewS(true, 'g', hashOnly), 1e-9);
            Assert.AreEqual(T09Probe.SoftEstimateViewS(false, 'g', new List<T09Probe.SoftVariant>()), T09Probe.SoftEstimateViewS(false, 'g', hashOnly), 1e-9);
        }

        [TestMethod]
        public void TheRunnersArgumentsReachTheSoftScenarioAndItsLimit()
        {
            Run r = StartSoft();
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("Probe: series K-T2 start soft59 begins with variants plain")), "the begin line names the series, the start and the fixed variant list");
            Assert.AreEqual("soft59", r.Probe.StartId);
            // The same arguments as the other K-T2 starts but the start id: a start whose id does not begin with soft runs the standard scenario.
            Run other = Start(new[] { "plain" }, null, "1707", "own1707", "K-T2");
            Assert.AreEqual("own1707", other.Probe.StartId);
            SeriesProbe.ForgetRunningProbes();
        }

        // The whole start against the fake world: every scene, view and variant, the frames, the windows, the numbers the pass ran with, the sun colour line.
        [TestMethod]
        public void TheSoftStartReadsEveryViewOfBothScenesAndPasses()
        {
            Run r = StartSoft();
            // The march target of the fake world follows recon.resolution as LayerReconstructor.Record sizes it.
            r.Probe.MarchSizeReader = own =>
            {
                int mw, mh;
                T09Probe.SoftMarchSize(1707, 960, Tuning.Get(OwnCloudOptions.TuneReconResolution, 0.5f), out mw, out mh);
                return new[] { mw, mh };
            };
            SoftDrive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(1, r.Quits);
            Assert.AreEqual("Probe: PASSED", r.Log[r.Log.Count - 1]);
            Assert.IsTrue(r.Rows.All(l => l.Contains("\"series\":\"K-T2\"")), "every row names the series K-T2");
            Assert.IsTrue(r.World.RealS < T09Probe.SoftMaxSeconds, "the scenario ended on its own at " + r.World.RealS.ToString("0") + " s");
            Assert.IsFalse(r.Log.Any(l => l.StartsWith("Probe: check") && l.Contains("FAILED")), "no failed check");

            var active = T09Probe.SoftVariants.Where(v => v.Active).ToList();
            var scenes = new[] { Tuple.Create(true, new[] { "ground", "above" }), Tuple.Create(false, new[] { "ground", "above", "inside", "sky3" }) };
            int windows = 0, frames = 0;
            foreach (var scene in scenes)
            {
                bool fair = scene.Item1;
                string sc = fair ? "fair" : "clock";
                string tag = fair ? "fw_" : "k_";
                foreach (string view in scene.Item2)
                {
                    string pv = view + (fair ? "_fair" : "");
                    string head = "T09s." + sc + "." + view + ".";
                    Assert.AreEqual(1.0, Number(r, head + "placed_ok"), head + "placed");
                    // The baseline: its frame and its cost window.
                    Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-" + pv + "-off_a-")), pv + " baseline frame");
                    Assert.AreEqual(1, r.Log.Count(l => l.StartsWith("T09 view begin: " + view + " " + tag + "off ")), pv + " baseline window");
                    windows++;
                    char vl = view == "ground" ? 'g' : view == "above" ? 'a' : view == "inside" ? 'i' : 's';
                    foreach (T09Probe.SoftVariant v in active)
                    {
                        if (!v.Runs(fair, vl))
                        {
                            Assert.IsFalse(r.World.Saved.Any(n => n.Contains("-" + pv + "-" + v.Name + "_")), pv + " " + v.Name + " does not run here: no frame");
                            Assert.AreEqual(0, r.Log.Count(l => l.StartsWith("T09 view begin: " + view + " " + tag + v.Name + " ")), pv + " " + v.Name + " does not run here: no window");
                            Assert.AreEqual(0, Values(r, head + v.Name + ".grid_points").Count, pv + " " + v.Name + " does not run here: no row");
                            continue;
                        }
                        foreach (string letter in new[] { "a", "b", "c" })
                        {
                            Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-" + pv + "-" + v.Name + "_" + letter + "-")), pv + " " + v.Name + " frame " + letter);
                            frames++;
                        }
                        bool costs = v.Costs(fair, vl);
                        int begins = r.Log.Count(l => l.StartsWith("T09 view begin: " + view + " " + tag + v.Name + " "));
                        Assert.AreEqual(costs ? 1 : 0, begins, pv + " " + v.Name + " cost window");
                        Assert.AreEqual(begins, r.Log.Count(l => l.StartsWith("T09 view end: " + view + " " + tag + v.Name + " ")));
                        if (costs) windows++;
                        string row = head + v.Name + ".";
                        T09Probe.SoftExpectation e = T09Probe.SoftExpect(v);
                        Assert.AreEqual(e.GridPoints, Number(r, row + "grid_points"), row + "grid points");
                        Assert.AreEqual(e.Softness, Number(r, row + "softness"), 1e-6, row + "softness");
                        Assert.AreEqual(e.SoftDensity ? 1.0 : 0.0, Number(r, row + "soft_density"), row + "soft density");
                        Assert.AreEqual(1.0, Number(r, row + "effective_ok"), row + "the numbers are the table's");
                        // The cost candidates' switches and the march target as the table says (the fake reconstructor sizes it from recon.resolution).
                        Assert.AreEqual(e.SpanTight ? 1.0 : 0.0, Number(r, row + "span_tight"), row + "span tight");
                        Assert.AreEqual(e.DropEmpty ? 1.0 : 0.0, Number(r, row + "drop_empty"), row + "drop empty");
                        Assert.AreEqual(e.SkipEmpty ? 1.0 : 0.0, Number(r, row + "skip_empty"), row + "skip empty");
                        Assert.AreEqual(e.DitherSequence, Number(r, row + "dither_sequence"), row + "dither sequence");
                        int ew, eh;
                        T09Probe.SoftMarchSize(1707, 960, e.Resolution, out ew, out eh);
                        Assert.AreEqual(ew, Number(r, row + "march_w"), row + "march target width");
                        Assert.AreEqual(eh, Number(r, row + "march_h"), row + "march target height");
                        Assert.AreEqual(1.0, Number(r, row + "pose_ok"), row + "pose");
                        Assert.IsTrue(Number(r, row + "box_tstd") >= 0.0, row + "box grain");
                        Assert.IsTrue(Number(r, row + "tstd") >= 0.0 && Number(r, row + "mask_n") > 0, row + "mask grain over the pixels the clouds changed");
                        Assert.IsTrue(Number(r, row + "frames_apart") >= 2, row + "three consecutive reads");
                        if (costs) Assert.IsTrue(Number(r, row + "cost_frames") > 100 && Number(r, row + "cost_pose_ok") == 1.0, row + "cost window frames");
                    }
                }
            }
            Assert.AreEqual(windows, (int)Number(r, "T09s.cost_windows"));
            Assert.AreEqual(frames, (int)Number(r, "T09s.frames_read"));
            Assert.AreEqual(6.0, Number(r, "T09s.sections"));
            Assert.AreEqual(0.0, Number(r, "T09s.sections_skipped"));
            Assert.AreEqual(1, r.Log.Count(l => l.StartsWith("T09 soft plan: ") && l.Contains("softnodrop [") && l.Contains("march target 0.33")), "the plan line names the new variants and the march target");
            // The K-T2 clock is chosen after the fair scene from the universal time this start began at: the same hour as the plain start's (K-T2 own1707: five hours on from the base).
            Assert.AreEqual(1000800.0 + 5 * 3600.0, Number(r, "T09.ut"), 1e-6, "the K-T2 clock of the TVC start");
            Assert.AreEqual(0.0, Number(r, "T09s.fair.ut") % 3600.0, 1e-6);
            // The two scenes are told apart by their windows: begin lines, in the order fair then clock.
            int firstClock = r.Log.FindIndex(l => l.StartsWith("T09 view begin:") && l.Contains(" k_"));
            int lastFair = r.Log.FindLastIndex(l => l.StartsWith("T09 view begin:") && l.Contains(" fw_"));
            Assert.IsTrue(lastFair >= 0 && firstClock > lastFair, "the fair scene comes first");
        }

        [TestMethod]
        public void TheSunColourLineSaysWhetherTheReadSeesTheRestoredColourOncePerScene()
        {
            Run r = StartSoft();
            SoftDrive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            List<string> lines = r.Log.Where(l => l.StartsWith("T09 sun colour (")).ToList();
            Assert.AreEqual(2, lines.Count, "one line per scene: " + string.Join(" | ", lines.ToArray()));
            Assert.IsTrue(lines[0].StartsWith("T09 sun colour (fair,") && lines[1].StartsWith("T09 sun colour (clock,"));
            foreach (string line in lines)
            {
                StringAssert.Contains(line, "Scatterer loaded True");
                StringAssert.Contains(line, "air from Scatterer True");
                StringAssert.Contains(line, "read in the render (1.400 1.200 0.970) blue/red 0.693");
                StringAssert.Contains(line, "read in the module's tick (2.000 1.900 1.850) blue/red 0.925");
                StringAssert.Contains(line, "the pass took (1.950 1.860 1.800) blue/red 0.923");
                StringAssert.Contains(line, "the read sees the restored colour: True");
            }
            foreach (string scene in new[] { "fair", "clock" })
            {
                string p = "T09s.sun." + scene + ".";
                Assert.AreEqual(1.0, Number(r, p + "scatterer_loaded"));
                Assert.AreEqual(1.0, Number(r, p + "restored_seen"));
                Assert.AreEqual(0.97 / 1.4, Number(r, p + "scene_blue_over_red"), 1e-3);
                Assert.AreEqual(1.85 / 2.0, Number(r, p + "tick_blue_over_red"), 1e-3);
                Assert.AreEqual(1.8 / 1.85, Number(r, p + "pass_over_tick_blue"), 1e-3);
                Assert.AreEqual(45.0, Number(r, p + "sun_elevation_deg"), 1e-3);
                Assert.AreEqual(200.0, Number(r, p + "viewer_altitude_m"), 1e-3);
            }
        }

        [TestMethod]
        public void ASunColourThatTheModulationNeverChangedIsNotCalledRestored()
        {
            Run r = StartSoft();
            Drive(r, 0.04, 3600.0, x => SoftFrame(x, new Color(2.0f, 1.9f, 1.85f), new Color(2.0f, 1.9f, 1.85f), new Color(2.0f, 1.9f, 1.85f)));
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(0.0, Number(r, "T09s.sun.fair.restored_seen"), "the render's colour equals the tick's: nothing was modulated or nothing was restored");
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 sun colour (fair,") && l.EndsWith("the read sees the restored colour: False")));
        }

        [TestMethod]
        public void AVariantWhoseNumbersDidNotReachThePassFailsTheProbeByName()
        {
            Run r = StartSoft();
            // The module's per-frame numbers are not written: the grid points stay at the frame's own default, whatever the settings say.
            Drive(r, 0.04, 3600.0, x => { });
            Assert.IsFalse(r.Probe.Passed, "the numbers of the variants did not reach the frame");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.Contains("T09s.effective_")), r.Joined);
            AssertNoUnhandled(r);
        }

        [TestMethod]
        public void AMarchTargetThatDidNotTakeTheResolutionFailsTheVariantByName()
        {
            Run r = StartSoft();
            // The target stays at half size whatever recon.resolution says.
            r.Probe.MarchSizeReader = own => new[] { 854, 480 };
            SoftDrive(r);
            Assert.IsFalse(r.Probe.Passed, "softr33's march target is not the one the table asks for");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.Contains("T09s.effective_") && f.Contains("softr33")), r.Joined);
            Assert.IsFalse(r.Probe.Failures.Any(f => f.Contains("T09s.effective_") && f.Contains("softnodrop")), "the variants at half size are fine");
            AssertNoUnhandled(r);
        }

        [TestMethod]
        public void AFieldWithoutADistinctLowCoverFailsTheFairSceneByNameAndStillRunsTheClockScene()
        {
            Run r = StartSoft(x => x.Field.Cover = ut => 0.8f);
            SoftDrive(r);
            Assert.IsFalse(r.Probe.Passed, "the fair scene could not be set up");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.Contains("T09s.fair_clock")), r.Joined);
            AssertNoUnhandled(r);
            Assert.IsFalse(r.Log.Any(l => l.StartsWith("T09 view begin:") && l.Contains(" fw_")), "no fair window");
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 view begin: ground k_soft ")), "the clock scene ran");
            Assert.AreEqual(1, r.Quits);
        }

        [TestMethod]
        public void AViewThatCannotBePlacedFailsTheProbeAndTheRestOfTheStartGoesOn()
        {
            // The vessel never reaches the wanted height of the above view: its placement fails four times and the view is left out.
            Run r = StartSoft(x => x.World.PlaceResult = (call, wanted) => wanted > 5000.0 ? wanted - 400.0 : wanted);
            SoftDrive(r);
            Assert.IsFalse(r.Probe.Passed, "a view that is not placed fails the start");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.Contains("pose_placed_above")), r.Joined);
            AssertNoUnhandled(r);
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 view begin: ground k_soft ")), "the ground view ran");
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 view begin: sky3 k_soft ")), "the views after it ran");
        }

        [TestMethod]
        public void ATableThatTheTimeCannotHoldLeavesTheLastViewsOutAndSaysSo()
        {
            // Every variant runs in every view with a cost window in every view: more than the scenario's time.
            var saved = T09Probe.SoftVariants.Select(v => new { v.FairCost, v.ClockCost, v.FairViews, v.ClockViews }).ToArray();
            try
            {
                foreach (T09Probe.SoftVariant v in T09Probe.SoftVariants)
                {
                    v.FairCost = "ga";
                    v.ClockCost = "gias";
                    v.FairViews = "";
                    v.ClockViews = "";
                }
                Run r = StartSoft();
                SoftDrive(r);
                Assert.IsFalse(r.Probe.Passed, "the plan does not fit the time");
                Assert.IsTrue(r.Probe.Failures.Any(f => f.Contains("T09s.section_")), r.Joined);
                Assert.IsTrue(Number(r, "T09s.sections_skipped") >= 1.0);
                AssertNoUnhandled(r);
                Assert.IsTrue(r.World.RealS < T09Probe.SoftMaxSeconds + 30, "the start ended in its time: " + r.World.RealS.ToString("0"));
            }
            finally
            {
                for (int i = 0; i < T09Probe.SoftVariants.Length; i++)
                {
                    T09Probe.SoftVariants[i].FairCost = saved[i].FairCost;
                    T09Probe.SoftVariants[i].ClockCost = saved[i].ClockCost;
                    T09Probe.SoftVariants[i].FairViews = saved[i].FairViews;
                    T09Probe.SoftVariants[i].ClockViews = saved[i].ClockViews;
                }
            }
        }
    }
}
