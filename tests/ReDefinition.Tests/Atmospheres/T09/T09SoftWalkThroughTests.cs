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
        public void TheSoftTableNamesTheFavouritesAndFourEmptySlotsAndUsesNoRemovedKey()
        {
            var names = T09Probe.SoftVariants.Select(v => v.Name).ToList();
            CollectionAssert.AreEqual(new[] { "soft", "hard", "soft100s24", "softhash", "slot2", "slot3", "slot4" }, names);
            Assert.AreEqual(names.Count, names.Distinct().Count(), "the names are unique");
            Assert.AreEqual(3, T09Probe.SoftVariants.Count(v => !v.Active), "three empty slots");
            Assert.AreEqual(1, T09Probe.SoftVariants.First(v => v.Name == "softhash").Pairs.Length);
            Assert.AreEqual("", T09Probe.SoftVariants.First(v => v.Name == "softhash").FairCost + T09Probe.SoftVariants.First(v => v.Name == "softhash").ClockCost, "softhash has frames and no cost window");
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
        }

        [TestMethod]
        public void TheDefaultTableFitsTheTimeAndEachExtraCandidateIsPriced()
        {
            double estimate = T09Probe.SoftEstimateS(T09Probe.SoftVariants);
            Console.WriteLine("soft59 estimate " + estimate.ToString("0") + " s of " + T09Probe.SoftMaxSeconds.ToString("0") + " s");
            Assert.IsTrue(estimate <= T09Probe.SoftMaxSeconds - 60.0, "the default table needs " + estimate.ToString("0") + " s of " + T09Probe.SoftMaxSeconds.ToString("0"));
            Assert.IsTrue(estimate >= 600.0, "the estimate counts the windows: " + estimate.ToString("0"));
            // A cost candidate with one window in each of the ground views and the above views: the price the lead pays for a slot.
            var table = T09Probe.SoftVariants.Select(v => v).ToList();
            table[4] = new T09Probe.SoftVariant { Name = "slot2",Pairs = new[] { "t:layers.lightStride=2" }, FairCost = "g", ClockCost = "g" };
            double withSlot = T09Probe.SoftEstimateS(table);
            Console.WriteLine("with one candidate (cost at the ground in both scenes) " + withSlot.ToString("0") + " s");
            Assert.IsTrue(withSlot > estimate + 90.0, "a slot costs its frames and windows: " + (withSlot - estimate).ToString("0") + " s");
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
                    foreach (T09Probe.SoftVariant v in active)
                    {
                        foreach (string letter in new[] { "a", "b", "c" })
                        {
                            Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-" + pv + "-" + v.Name + "_" + letter + "-")), pv + " " + v.Name + " frame " + letter);
                            frames++;
                        }
                        bool costs = v.Costs(fair, view == "ground" ? 'g' : view == "above" ? 'a' : view == "inside" ? 'i' : 's');
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
            // The empty slots run nothing.
            Assert.IsFalse(r.World.Saved.Any(n => n.Contains("slot")), "no frame of an empty slot");
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
            // Three slots with cost windows in every view: more than the scenario's time.
            var saved = T09Probe.SoftVariants.Select(v => new { v.Name, v.Pairs, v.FairCost, v.ClockCost }).ToArray();
            try
            {
                for (int i = 4; i < 7; i++)
                {
                    T09Probe.SoftVariants[i].Pairs = new[] { "t:layers.lightStride=" + (i - 2) };
                    T09Probe.SoftVariants[i].FairCost = "ga";
                    T09Probe.SoftVariants[i].ClockCost = "gias";
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
                for (int i = 4; i < 7; i++)
                {
                    T09Probe.SoftVariants[i].Pairs = null;
                    T09Probe.SoftVariants[i].FairCost = saved[i].FairCost;
                    T09Probe.SoftVariants[i].ClockCost = saved[i].ClockCost;
                }
            }
        }
    }
}
