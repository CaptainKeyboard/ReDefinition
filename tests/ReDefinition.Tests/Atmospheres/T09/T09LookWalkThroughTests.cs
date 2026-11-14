using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The look starts of K-T2d (T09ProbeLook: -atmoLook A for start 51, B for start 52) against the fake world: the frames each one takes, the expectation rows that come before a frame,
    // the map profile along the view, the set selection by the command line, and the failures it must name. The fake draws a deck for every frame whose mask includes a layer.
    public partial class T09WalkThroughTests
    {
        private Run StartLook(string mode, string sets = null, string win = null, Action<Run> tweak = null)
        {
            return StartDiag(x =>
            {
                var args = x.Probe.CommandLineOverride.ToList();
                args.Add("-atmoLook");
                args.Add(mode);
                if (sets != null)
                {
                    args.Add("-atmoLookSets");
                    args.Add(sets);
                }
                if (win != null)
                {
                    args.Add("-atmoLookWin");
                    args.Add(win);
                }
                x.Probe.CommandLineOverride = args.ToArray();
                if (tweak != null) tweak(x);
            });
        }

        // Start gt57 (-atmoLook H, T09ProbeGt.cs): both views of the low cover, three frames of each grain variant, then the four cost windows of each view.
        [TestMethod]
        public void LookHReadsTheGrainVariantsAndTheCostWindowsOfBothViews()
        {
            Run r = StartLook("H");
            Drive(r, 0.02);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            foreach (string view in new[] { "ground", "above" })
            {
                foreach (string tag in new[] { "gbefore", "gnew", "gnew10" })
                    foreach (string letter in new[] { "a", "b", "c" })
                        Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-" + view + "_low-" + tag + letter + "-")), view + " " + tag + letter + " was written");
                foreach (string variant in new[] { "off", "before", "plain" })
                {
                    Assert.AreEqual(1, r.Log.Count(l => l.StartsWith("T09 view begin: " + view + " " + variant + " ")), view + " " + variant + " has a cost window");
                    Assert.AreEqual(1, r.Log.Count(l => l.StartsWith("T09 view end: " + view + " " + variant + " ")), view + " " + variant + " ends its cost window");
                }
            }
            Assert.IsFalse(r.World.Saved.Any(n => n.Contains("_high-")), "the high cover is not read");
        }

        [TestMethod]
        public void LookAReadsTheSetsAtTwoShiftsTheForcedMapAndTheLayersAlone()
        {
            Run r = StartLook("A", "C0,C4,C5");
            Drive(r, 0.02);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(1, r.Quits);
            Assert.AreEqual(Number(r, "T09d.frames"), Number(r, "T09d.frames_pose_ok"), "every frame had the view's pose");
            foreach (string view in new[] { "ground.low", "ground.high", "above.low" })
            {
                string p = "T09d." + view + ".";
                Assert.AreEqual(0.5, Row(r, p + "off.sky_median"), 1e-4, p + "baseline");
                foreach (string frame in new[] { "kC0", "kC0m8", "kC4", "kC4m8", "kC5", "kC5m8", "kovr" })
                {
                    string q = p + frame + ".";
                    Assert.IsTrue(Row(r, q + "deck_median") > 0, q + "has a deck row");
                    double mean = Row(r, q + "expect_closed_mean");
                    Assert.IsTrue(mean >= 0 && mean <= 1, q + "expectation " + mean);
                    Assert.IsTrue(Row(r, q + "expect_closed_p10") <= mean + 1e-9 && mean <= Row(r, q + "expect_closed_p90") + 1e-9, q + "the spread brackets the mean");
                    Assert.AreEqual(8.0, Row(r, q + "expect_sites"));
                }
                // The expectation row is written before the frame's own rows: its line comes before the readout of the same frame in the log of rows.
                int expectAt = r.Rows.FindIndex(l => l.Contains("\"metric\":\"" + p + "kC4.expect_closed_mean\""));
                int readAt = r.Rows.FindIndex(l => l.Contains("\"metric\":\"" + p + "kC4.deck_median\""));
                Assert.IsTrue(expectAt >= 0 && readAt > expectAt, p + "the expectation is written first");
                // The map profile along the view: the layers' maps at 0, 5, 10, 20, 40 and 80 km.
                foreach (int l in new[] { 0, 1, 2, 3 })
                    foreach (string km in new[] { "0", "5", "10", "20", "40", "80" })
                        Assert.IsTrue(Row(r, p + "L" + l + ".map_at_" + km + "_km") >= 0, p + "map profile L" + l + " " + km);
                Assert.AreEqual("Kerbin-cumulus", Values(r, p + "sheet_layer", "plain")[0].Value, "the sheet layer is the highest map outside the rain group");
                Assert.AreEqual(0.665, Row(r, p + "kovr.forced_map"), 1e-6);
            }
            // The forced map reaches the tuning table (the key of the sheet layer) while its frame is taken, and the keys of a set while its frames are.
            Assert.IsTrue(r.Log.Count > 0);
            // The ground view: the layers alone under the winning set (the last selected: C5), and the rain deck's two coverages.
            foreach (string cover in new[] { "low", "high" })
            {
                for (int k = 0; k < 4; k++) Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-ground_" + cover + "-wC5o" + k + "-1707x960-natural-half")), "layer " + k + " alone, " + cover);
                foreach (string q in new[] { "q50", "q42" }) Assert.IsTrue(Row(r, "T09d.ground." + cover + "." + q + ".deck_median") >= 0, q + " rows, " + cover);
            }
            // The above view at the high cover is not read by look A, except the rain deck alone (P3), like the inside view.
            Assert.IsFalse(r.World.Saved.Any(n => n.Contains("-above_high-kC0-")), "no set frames above the high cover");
            Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-inside_high-l0in-")) && r.World.Saved.Any(n => n.Contains("-above_high-l0up-")), "the rain deck alone from inside and from above");
            Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-inside_high-off-")) && r.World.Saved.Any(n => n.Contains("-above_high-off-")));
            // 3 pairs x (off + 6 set frames + forced map) + ground x 2 covers x (4 layers + 2 rain frames) + the rain deck from inside and above (2 x 2)
            // + the aid frames of the optical depth: 3 pairs x 2 sets (the current one and the winning one at shift 0) + the two rain deck frames.
            // + the order pair (start 52): 2 poses x 2 frames; the rain deck alone from 2.3 km (off, l0mid, aid); the aid frames of the layers alone (L0 and L1) at both covers of the ground view.
            Assert.AreEqual(3 * 8 + 2 * 6 + 4 + 3 * 2 + 2 + 4 + 3 + 4, r.World.Saved.Count, string.Join(",", r.World.Saved.Take(6).ToArray()));
            foreach (string pose in new[] { "ground.low", "above.low" })
            {
                string o = "T09d." + pose + ".";
                foreach (string frame in new[] { "ord1", "ord0" })
                    Assert.IsTrue(Row(r, o + frame + ".deck_median") > 0, o + frame + " has a deck row");
                Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-" + pose.Replace('.', '_') + "-ord0-")) && r.World.Saved.Any(n => n.Contains("-" + pose.Replace('.', '_') + "-ord1-")), pose + " order pair pictures");
            }
            Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-deck_high-l0mid-")) && r.World.Saved.Any(n => n.Contains("-deck_high-off-")), "the rain deck alone from 2.3 km");
            Assert.IsTrue(Row(r, "T09d.deck.high.aid1l0mid.aidtau_n") > 0, "aid frame of the rain deck from 2.3 km");
            Assert.IsTrue(Row(r, "T09d.deck.high.l0mid.expect_tau_zenith_mean") >= 0, "the oracle's zenith tau for the deck alone");
            foreach (string cover in new[] { "low", "high" })
                foreach (int k in new[] { 0, 1 })
                {
                    Assert.IsTrue(Row(r, "T09d.ground." + cover + ".aid1wC5o" + k + ".aidtau_n") > 0, "aid frame of layer " + k + " alone, " + cover);
                    Assert.IsTrue(Row(r, "T09d.ground." + cover + ".wC5o" + k + ".expect_tau_zenith_mean") >= 0, "zenith expectation of layer " + k + " alone, " + cover);
                }
            Assert.IsTrue(r.Log.Any(l => l.Contains("no_order_state")), "without the pass the order pair names that it could not read the order");
            foreach (string view in new[] { "ground.low", "ground.high", "above.low" })
            {
                string p = "T09d." + view + ".";
                // The zenith's optical depth the oracle expects is written with the other expectation rows, before the frame is read; the aid frame follows its natural frame.
                foreach (string frame in new[] { "kC0", "kC5" })
                {
                    Assert.IsTrue(Row(r, p + frame + ".expect_tau_zenith_mean") >= 0, p + frame + " zenith tau expectation");
                    Assert.IsTrue(Row(r, p + frame + ".expect_tau_zenith_p10") <= Row(r, p + frame + ".expect_tau_zenith_p90") + 1e-9, p + frame + " spread");
                    double closed = Row(r, p + frame + ".expect_zenith_closed");
                    Assert.IsTrue(closed >= 0 && closed <= 1, p + frame + " zenith closed share");
                    Assert.IsTrue(Row(r, p + "aid1" + frame + ".aidtau_n") > 0, p + frame + " aid tau samples");
                    int natural = r.Rows.FindIndex(l => l.Contains("\"metric\":\"" + p + frame + ".deck_median\""));
                    int aidAt = r.Rows.FindIndex(l => l.Contains("\"metric\":\"" + p + "aid1" + frame + ".aidtau_n\""));
                    Assert.IsTrue(natural >= 0 && aidAt > natural, p + frame + " the aid frame follows the natural frame");
                }
            }
            Assert.IsTrue(Row(r, "T09d.inside.high.aid1l0in.aidtau_n") > 0 && Row(r, "T09d.above.high.aid1l0up.aidtau_n") > 0, "aid frames of the rain deck from inside and above");
            // The only warnings are the four frames of the order pair, which name that the fake has no pass that read the camera's buffer order.
            Assert.AreEqual(4.0, Number(r, "T09.pictures.warnings"));
        }

        [TestMethod]
        public void LookAWaitsForTheMapsAfterEveryChangeOfTheForcedKeysThatTouchesMapsOrTypes()
        {
            Run r = StartLook("A", "C0,C1,C2");
            Drive(r, 0.02);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            List<double> since = r.World.LookReadSinceChangeS;
            Assert.IsTrue(since.Count >= 8, "reads " + since.Count);
            // Reads of the first pair: 0 the input, then kC0, kC0m8, kC1, kC1m8, kC2, kC2m8, kovr. The types move with C1 (the humilis type value) and back with C2's clear; the forced
            // sheet map is a map change: each of these frames reads after the maps have snapped or slewed (the module snaps on a look-knob change; the wait covers its round).
            foreach (int i in new[] { 3, 4, 5, 7 })
                Assert.IsTrue(since[i] >= 2.0, "read " + i + " came " + since[i].ToString("0.00") + " s after the last change of the keys");
        }

        [TestMethod]
        public void LookATakesEverySetByDefaultAndOnlyTheNamedOnesWhenAsked()
        {
            Run all = StartLook("A");
            Drive(all, 0.02);
            Assert.IsTrue(all.Probe.Passed, Dump(all));
            foreach (string t in new[] { "kC0", "kC1m8", "kC2", "kC3m8", "kC4", "kC5m8" })
                Assert.IsTrue(all.World.Saved.Any(n => n.Contains("-ground_low-" + t + "-")), "set frame " + t);
            // The winning set defaults to the last selected one: C5.
            Assert.IsTrue(all.World.Saved.Any(n => n.Contains("-ground_low-wC5o2-")));
            Run one = StartLook("A", "C4", "C0");
            Drive(one, 0.02);
            Assert.IsTrue(one.Probe.Passed, Dump(one));
            Assert.IsTrue(one.World.Saved.Any(n => n.Contains("-ground_low-kC4m8-")));
            Assert.IsFalse(one.World.Saved.Any(n => n.Contains("-ground_low-kC5-")), "an unnamed set is not read");
            Assert.IsTrue(one.World.Saved.Any(n => n.Contains("-ground_low-wC0o1-")), "-atmoLookWin names the set of the layer-alone frames");
        }

        [TestMethod]
        public void LookAWithoutAStackWarnsAndStillReadsTheFrames()
        {
            Run r = StartLook("A", "C0", null, x => x.World.LookInputOff = true);
            Drive(r, 0.02);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("Probe: warning no_look_input")), "the missing stack is named");
            Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-ground_low-kC0-")), "the frames are still taken");
            Assert.AreEqual(0, r.Rows.Count(l => l.Contains(".expect_closed_mean")), "no expectation without a stack");
        }

        [TestMethod]
        public void LookBReadsTheLightVariantsTheAidFrameAndTheHorizon()
        {
            bool shadowsBefore = AtmospheresSettings.CloudShadows;
            // A larger raw picture than the fake's default: a row profile band is 2 percent of the height, under one row of 36.
            Run r = StartLook("B", null, null, x => { x.World.RawWidth = 160; x.World.RawHeight = 120; });
            Drive(r, 0.02);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            foreach (string view in new[] { "ground.low", "ground.high", "above.low" })
            {
                string p = "T09d." + view + ".";
                foreach (string frame in new[] { "t0", "e14", "air1", "t0b" })
                {
                    Assert.IsTrue(Row(r, p + frame + ".deck_median") > 0, p + frame);
                    Assert.IsTrue(Row(r, p + frame + ".sun_r") > 0 && Row(r, p + frame + ".sun_g") > 0 && Row(r, p + frame + ".sun_b") > 0, p + frame + " sun colour");
                }
                Assert.AreEqual(0.3, Row(r, p + "aid4.direct_median"), 1e-4);
                Assert.IsTrue(Row(r, p + "off2.sky_n") > 0 || Row(r, p + "off2.ground_n") > 0, p + "the second baseline frame (the noise floor) is read");
                Assert.IsTrue(Row(r, p + "off.prof_b0_n") >= 0, p + "the baseline's row profile");
            }
            // The horizon pose at the low cover: eight frames (fade 0 / 20 / 40 / 80 km, haze off and on), their repeat, the baseline and its repeat, the order frames 1, 0, 0, 1.
            foreach (string frame in new[] { "hz0h0", "hz0h1", "hz20h0", "hz20h1", "hz40h0", "hz40h1", "hz80h0", "hz80h1", "hz0h0b" })
            {
                Assert.IsTrue(Row(r, "T09d.horizon.low." + frame + ".deck_median") >= 0, frame);
                Assert.IsTrue(Row(r, "T09d.horizon.low." + frame + ".expect_closed_mean") >= 0, frame + " expectation");
            }
            foreach (string frame in new[] { "ord1", "ord0", "ord0b", "ord1b" })
            {
                Assert.IsTrue(Row(r, "T09d.horizon.low." + frame + ".deck_median") >= 0, frame);
                Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-horizon_low-" + frame + "-")), frame + " picture");
                // The row profile of the deck: five bands below the horizon row and five above it, each with its sample count.
                Assert.IsTrue(Row(r, "T09d.horizon.low." + frame + ".prof_b0_n") > 0, frame + " row profile");
            }
            Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-horizon_low-off2-")), "the baseline's repeat at the horizon pose");
            // The view from above at the low cover: the order frames 1, 0, 0, 1 and the pass's own aerial perspective at the strength 1 and 2.
            foreach (string frame in new[] { "ord1", "ord0", "ord0b", "ord1b", "hh1", "hh2" })
            {
                Assert.IsTrue(Row(r, "T09d.above.low." + frame + ".deck_median") >= 0, "above low " + frame);
                Assert.IsTrue(Row(r, "T09d.above.low." + frame + ".prof_b0_n") > 0, "above low " + frame + " row profile");
            }
            Assert.IsFalse(r.World.Saved.Any(n => n.Contains("-ground_low-ord1-") || n.Contains("-ground_high-hh1-")), "the order and haze frames are read from above at the low cover and at the horizon only");
            Assert.IsFalse(r.Rows.Any(l => l.Contains(".t0.prof_")), "the row profile is written for the horizon pose, the order frames and the baselines only");
            Assert.AreEqual(1.0, Row(r, "T09d.horizon.low.placed_ok"));
            // The rain deck's own light at the high cover, from 8.3 km and 2.3 km: the deck, with the shadows off, with every layer, the aid frames of the tau and of the light.
            foreach (string pose in new[] { "above.high.l0up", "deck.high.l0mid" })
            {
                string view = pose.Substring(0, pose.LastIndexOf('.')), frame = pose.Substring(pose.LastIndexOf('.') + 1);
                string q = "T09d." + view + ".";
                Assert.IsTrue(Row(r, q + frame + ".deck_median") >= 0 && Row(r, q + frame + "sh0.deck_median") >= 0 && Row(r, q + frame + "all.deck_median") >= 0, pose + " frames");
                Assert.IsTrue(Row(r, q + "aid1" + frame + ".aidtau_n") > 0, pose + " aid tau");
                Assert.IsTrue(Row(r, q + "aid4" + frame + ".direct_median") > 0, pose + " aid light");
                Assert.IsTrue(Row(r, q + "aid4" + frame + ".direct_r_p10") <= Row(r, q + "aid4" + frame + ".direct_r_p90"), pose + " the direct light's spread over the deck");
                Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-" + view.Replace('.', '_') + "-" + frame + "sh0-")), pose + " shadows-off picture");
            }
            // The shadow setting is put back after the shadows-off frames.
            Assert.AreEqual(shadowsBefore, AtmospheresSettings.CloudShadows, "cloudShadows is back as it was");
            // 3 pairs x (off + off2 + 4 variants + aid) + the horizon (off, off2, 4 order frames, 8 fade frames, the repeat) + the high cover's ground view again (counted in the 3 pairs)
            // + 2 deck-light views x (off, deck, aid tau, aid light, shadows off, all layers) + from above at the low cover the order frames (4) and the haze frames (2).
            Assert.AreEqual(3 * 7 + 15 + 2 * 6 + 4 + 2, r.World.Saved.Count);
        }

        [TestMethod]
        public void WithoutALookSwitchTheOrdinaryScenarioRuns()
        {
            Run r = StartDiag();
            Drive(r, 0.02);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(56, r.World.Saved.Count);
            Assert.AreEqual(0, r.Rows.Count(l => l.Contains(".expect_closed_mean")), "no look rows");
            Assert.IsFalse(r.World.Saved.Any(n => n.Contains("-kC0-")));
        }
    }
}
