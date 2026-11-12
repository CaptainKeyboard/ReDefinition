using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The whole K-T2d scenario of T09Probe (the diagnosis of the black own clouds) against the fake world: the two cover times, the views ground and above, the off baseline,
    // the gains 1, 4, 16 and 64, the aid frame, the numeric readouts, the environment lines, and the faults the readout must name (an empty region, a lost pose, no raw values,
    // a cover that does not differ, a field that does not answer). The fake draws a sky of 0.5, a sea of 0.7 and, when the pass draws all layers, a deck of 0.075 times the sun gain
    // clipped at 1: the readout has to find those numbers again.
    public partial class T09WalkThroughTests
    {
        // Cover by the hour of the day (a Kerbin day has 6): sun band hours (1 and 2) carry 0.31 and 0.72; the hours out of the band carry nearer covers (0.30 and 0.70), so the
        // clock step has to pass them.
        private static float DiagCover(double ut)
        {
            int n = (int)Math.Round(ut / 3600.0);
            switch (((n % 6) + 6) % 6)
            {
                case 0: return 0.30f;
                case 1: return 0.31f;
                case 2: return 0.72f;
                case 3: return 0.70f;
                default: return 0.50f;
            }
        }

        private Run StartDiag(Action<Run> tweak = null, string[] variants = null)
        {
            Run r = Start(variants ?? new[] { "plain" }, x =>
            {
                x.Field.Cover = DiagCover;
                if (tweak != null) tweak(x);
            }, "1707", "diag1707", "K-T2d");
            return r;
        }

        private static double Row(Run r, string metric)
        {
            return Number(r, metric, "plain");
        }

        [TestMethod]
        public void AHealthyDiagStartMeasuresEveryGainAtBothCoversAndPasses()
        {
            Run r = StartDiag();
            Drive(r, 0.02);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(1, r.Quits);
            Assert.IsTrue(r.Rows.All(l => l.Contains("\"series\":\"K-T2d\"")), "every row names the series K-T2d");
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("Probe: series K-T2d start diag1707 begins with variants plain")));

            // The clock: the cover nearest 0.3 with the sun in the band (hour 1: 0.31), the cover nearest 0.7 with the sun in the band (hour 2: 0.72); the others are emitted.
            Assert.AreEqual(0.31, Number(r, "T09d.low.cover"), 1e-6);
            Assert.AreEqual(0.72, Number(r, "T09d.high.cover"), 1e-6);
            Assert.AreEqual(0.0, Number(r, "T09d.cover_off_band"));
            Assert.AreEqual(48.0, Number(r, "T09d.candidates"));
            Assert.AreEqual(48, r.Rows.Count(l => l.Contains("\"metric\":\"T09.cover.")), "one T09.cover row per candidate hour");
            Assert.IsTrue(Number(r, "T09d.low.sun_elev_deg") >= 15 && Number(r, "T09d.high.sun_elev_deg") <= 60);
            Assert.AreNotEqual(Number(r, "T09d.low.ut"), Number(r, "T09d.high.ut"));

            foreach (string view in new[] { "ground", "above" })
            {
                foreach (string cover in new[] { "low", "high" })
                {
                    string p = "T09d." + view + "." + cover + ".";
                    // The baseline: the sky and the sea as the fake draws them; no deck rows.
                    Assert.AreEqual(0.5, Row(r, p + "off.sky_median"), 1e-4, p + "off sky");
                    Assert.AreEqual(0.7, Row(r, p + "off.ground_median"), 1e-4, p + "off sea");
                    Assert.AreEqual(0.7 / 0.5, Row(r, p + "off.ground_over_sky"), 1e-3);
                    Assert.AreEqual(0, Values(r, p + "off.deck_median", "plain").Count, "the baseline has no deck");
                    // The gains: the deck rises linearly with the sun gain until it clips at 1.
                    double[] expected = { 0.075, 0.3, 1.0, 1.0 };
                    string[] tags = { "g1", "g4", "g16", "g64" };
                    for (int i = 0; i < tags.Length; i++)
                    {
                        string q = p + tags[i] + ".";
                        Assert.AreEqual(expected[i], Row(r, q + "deck_median"), 1e-3, q + "deck median");
                        Assert.AreEqual(expected[i] / 0.5, Row(r, q + "deck_over_off_sky"), 2e-3, q + "deck over the off sky");
                        Assert.AreEqual(expected[i] / 0.7, Row(r, q + "deck_over_off_ground"), 2e-3, q + "deck over the off sea");
                        Assert.IsTrue(Row(r, q + "deck_fraction") > 0.5, q + "deck fraction");
                        Assert.IsTrue(Row(r, q + "deck_p10") <= Row(r, q + "deck_median") && Row(r, q + "deck_median") <= Row(r, q + "deck_p90"));
                        Assert.AreEqual(i >= 2 ? 1.0 : 0.0, Row(r, q + "deck_clipped"), 1e-6, q + "clipped share");
                        // The fake's sun colour is (0.8, 0.7, 0.6) times the gain; its grayscale is 0.7185 times the gain.
                        Assert.AreEqual(new[] { 1.0, 4.0, 16.0, 64.0 }[i] * 0.7185, Row(r, q + "sun_luma"), 1e-2, q + "the sun colour the pass was given");
                    }
                    Assert.AreEqual(4.0, Row(r, p + "g4.deck_vs_g1"), 1e-2);
                    Assert.AreEqual(0.0, Values(r, p + "g1.deck_vs_g1", "plain").Count);
                    // The aid frame: the direct light, the taps and the ambient over the deck.
                    Assert.AreEqual(0.3, Row(r, p + "aid4.direct_median"), 1e-4);
                    Assert.AreEqual(0.5, Row(r, p + "aid4.taps_median"), 1e-4);
                    Assert.AreEqual(0.1, Row(r, p + "aid4.ambient_median"), 1e-4);
                    Assert.IsTrue(Row(r, "T09d." + view + "." + cover + ".cover_at_view_height") > 0);
                }
            }

            // The poses are verified at every capture (the rows of the pictures).
            Assert.IsTrue(r.Rows.Count(l => l.Contains("\"metric\":\"T09.pic.") && l.Contains(".alt_m\"")) >= 24, "a pose row for each of the 24 frames");
            // 2 covers x 2 views x (off + 4 gains + aid) pictures, half size.
            Assert.AreEqual(24, r.World.Saved.Count);
            Assert.IsTrue(r.World.SavedFactor.All(f => f == 2));
            Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-ground_low-g16-1707x960-natural-half")), string.Join(",", r.World.Saved.Take(4).ToArray()));
            Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-above_high-aid4-1707x960-aid-direct-half")));
            Assert.AreEqual(0.0, Number(r, "T09.pictures.warnings"));

            // The environment: at the first drawn frame and every 30 s of the scenario, as log lines; the first also as rows.
            int envFirst = r.Log.FindIndex(l => l.StartsWith("T09d env sunLight: "));
            Assert.IsTrue(envFirst >= 0, "the environment at the first drawn frame");
            Assert.IsTrue(r.Log.Count(l => l.StartsWith("T09d env sunLight: ")) >= 3, "and again every 30 s");
            Assert.AreEqual("active Gamma", Values(r, "T09d.env.colourSpace", "plain")[0].Value);
            int firstFrame = r.Log.FindIndex(l => l.StartsWith("Probe: variant plain on"));
            Assert.IsTrue(envFirst > firstFrame);
        }

        [TestMethod]
        public void ADeckThatIsNotThereGivesNoDeckRowsAndNoFailure()
        {
            Run r = StartDiag(x => x.World.DeckCovers = (px, py) => false);
            Drive(r, 0.02);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(0.0, Row(r, "T09d.ground.low.g16.deck_fraction"));
            Assert.AreEqual(0.0, Row(r, "T09d.ground.low.g16.deck_n"));
            Assert.AreEqual(0, Values(r, "T09d.ground.low.g16.deck_median", "plain").Count);
            Assert.AreEqual(0, Values(r, "T09d.ground.low.aid4.direct_median", "plain").Count);
            Assert.AreEqual(1.0, Row(r, "T09d.ground.low.g16.sky_over_off_sky"), 1e-3, "the sky is the sky of the baseline");
        }

        [TestMethod]
        public void AnEmptyReadoutRegionFailsNamed()
        {
            Run r = StartDiag(x => x.World.EmptyHorizon = true);
            Drive(r, 0.02);
            AssertNamedFailure(r, "T09d.region_ground_T09d.ground.low.off");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.Contains("T09d.region_sky_T09d.above.")), "the other view's sky region is empty too: " + r.Joined);
        }

        [TestMethod]
        public void APoseThatLeavesBetweenTheReadsFailsNamedAndGivesNoReadout()
        {
            Run r = StartDiag(x => x.World.AltitudeJumpOnCaptureTake = 400.0);
            Drive(r, 0.02);
            AssertNamedFailure(r, "T09.pose_picture_");
            Assert.AreEqual(0, Values(r, "T09d.ground.low.off.sky_median", "plain").Count, "no readout of a pose that is not the view's");
        }

        [TestMethod]
        public void AReadWithoutRawValuesFailsNamed()
        {
            Run r = StartDiag(x => x.World.NoRawPixels = true);
            Drive(r, 0.02);
            AssertNamedFailure(r, "kept no raw values");
        }

        [TestMethod]
        public void AReadThatCannotStartFailsNamed()
        {
            Run r = StartDiag(x => x.World.ReadFault = n => n == 0 ? "nobegin" : null);
            Drive(r, 0.02);
            AssertNamedFailure(r, "T09d.capture_");
        }

        [TestMethod]
        public void ADiagGainThatDoesNotReachThePassFailsNamed()
        {
            Run r = StartDiag(x => x.World.GainIgnored = true);
            Drive(r, 0.02);
            AssertNamedFailure(r, "reaches the pass");
        }

        [TestMethod]
        public void TwoCoversThatDoNotDifferFailNamedBeforeAnyView()
        {
            Run r = StartDiag(x => x.Field.Cover = ut => 0.5f);
            Drive(r, 0.02);
            AssertNamedFailure(r, "too close to tell apart");
            Assert.IsFalse(r.Log.Any(l => l.StartsWith("T09 view begin")));
            Assert.AreEqual(0, r.World.PlaceCalls, "nothing was placed");
        }

        [TestMethod]
        public void AFieldThatNeverAnswersFailsNamed()
        {
            Run r = StartDiag(x => x.Field.ReadyAtS = 1e9);
            Drive(r, 0.1);
            AssertNamedFailure(r, "did not answer at the site");
        }

        [TestMethod]
        public void OnlyThePlainVariantIsAccepted()
        {
            Run r = StartDiag(null, new[] { "off" });
            Drive(r, 0.1);
            AssertNamedFailure(r, "plain only");
        }

        [TestMethod]
        public void AnEnvironmentThatCannotBeReadIsAWarningNotAFailure()
        {
            Run r = StartDiag(x => x.World.EnvironmentThrows = true);
            Drive(r, 0.02);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.Log.Any(l => l.Contains("Probe: warning environment_failed")), "the fault is named");
        }

        [TestMethod]
        public void TheStandardSeriesStillUsesItsOwnNameAndScenario()
        {
            Run r = Start(new[] { "off" });
            Drive(r);
            Assert.IsTrue(r.Rows.All(l => l.Contains("\"series\":\"K-T2\"")), "the standard series keeps its name");
            Assert.AreEqual(0, r.Rows.Count(l => l.Contains("\"metric\":\"T09d.")), "no diagnosis rows in K-T2");
        }
    }
}
