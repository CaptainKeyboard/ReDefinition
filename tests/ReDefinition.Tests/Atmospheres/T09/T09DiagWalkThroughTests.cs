using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The whole K-T2d scenario of T09Probe (the diagnosis of the black own clouds) against the fake world: the two cover times, the views ground and above, the off baseline,
    // the gains 1, 4 and 16, the coverage variants, the aid frame, the numeric readouts, the environment lines, and the faults the readout must name (an empty region, a lost pose, no raw values,
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
                x.World.LayerReport = FourLayers(false);
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
            // (a far candidate at a rounded hour may carry 0.30 or 0.70 with the sun in the band: still the nearest to the targets)
            Assert.AreEqual(0.30, Number(r, "T09d.low.cover"), 0.015);
            Assert.AreEqual(0.71, Number(r, "T09d.high.cover"), 0.015);
            Assert.AreEqual(0.0, Number(r, "T09d.cover_off_band"));
            Assert.AreEqual(158.0, Number(r, "T09d.candidates"), "48 hourly candidates and 110 far ones");
            Assert.AreEqual(110.0, Number(r, "T09d.candidates_far"));
            Assert.AreEqual(158, r.Rows.Count(l => l.Contains("\"metric\":\"T09.cover.")), "one T09.cover row per candidate");
            Assert.AreEqual(1.0, Number(r, "T09d.covers_distinct"));
            Assert.AreEqual(Number(r, "T09d.frames"), Number(r, "T09d.frames_pose_ok"), "every frame had the view's pose");
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
                    double[] expected = { 0.075, 0.3, 1.0 };
                    string[] tags = { "g1", "g4", "g16" };
                    for (int i = 0; i < tags.Length; i++)
                    {
                        string q = p + tags[i] + ".";
                        Assert.AreEqual(expected[i], Row(r, q + "deck_median"), 1e-3, q + "deck median");
                        Assert.AreEqual(expected[i] / 0.5, Row(r, q + "deck_over_off_sky"), 2e-3, q + "deck over the off sky");
                        Assert.AreEqual(expected[i] / 0.7, Row(r, q + "deck_over_off_ground"), 2e-3, q + "deck over the off sea");
                        Assert.IsTrue(Row(r, q + "deck_fraction") > 0.5, q + "deck fraction");
                        Assert.IsTrue(Row(r, q + "deck_p10") <= Row(r, q + "deck_median") && Row(r, q + "deck_median") <= Row(r, q + "deck_p90"));
                        Assert.AreEqual(i >= 2 ? 1.0 : 0.0, Row(r, q + "deck_clipped"), 1e-6, q + "clipped share");
                        Assert.AreEqual(1.0, Row(r, q + "sky_closed_share"), 0.02, q + "the fake's deck replaces the whole sky by one colour: all of it is at the plateau");
                        Assert.AreEqual(1.0, Row(r, q + "sky_deck_share"), 0.02, q + "and every sky sample is a deck sample");
                        // The fake's sun colour is (0.8, 0.7, 0.6) times the gain; its grayscale is 0.7185 times the gain.
                        Assert.AreEqual(new[] { 1.0, 4.0, 16.0 }[i] * 0.7185, Row(r, q + "sun_luma"), 1e-2, q + "the sun colour the pass was given");
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
            Assert.IsTrue(r.Rows.Count(l => l.Contains("\"metric\":\"T09.pic.") && l.Contains(".alt_m\"")) >= 56, "a pose row for each of the 56 frames");
            // 2 covers x 2 views x (off + 3 gains + 3 stage variants + 2 coverage variants + aid) pictures, half size, plus 2 covers x ground x 4 layers x 2 shifts (layer alone).
            Assert.AreEqual(56, r.World.Saved.Count);
            // The coverage variants: a frame each beside p1, with the shift they ran at (the fake world has no layer stack, so no model rows and no reach rows).
            foreach (string tag in new[] { "c42", "c35" })
            {
                Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-above_low-" + tag + "-1707x960-natural-half")), "the coverage variant " + tag + " has its picture");
                Assert.IsTrue(Row(r, "T09d.ground.high." + tag + ".deck_median") > 0 && Row(r, "T09d.ground.high." + tag + ".sky_closed_share") >= 0, "the coverage variant " + tag + " has rows");
            }
            Assert.AreEqual(-0.08, Row(r, "T09d.ground.high.c42.coverage_shift"), 1e-6);
            Assert.AreEqual(-0.15, Row(r, "T09d.above.low.c35.coverage_shift"), 1e-6);
            foreach (string tag in new[] { "u1", "p1", "pe2" })
                Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-ground_high-" + tag + "-1707x960-natural-half")), "the stage variant " + tag + " has its picture");
            Assert.IsTrue(Row(r, "T09d.ground.high.p1.deck_median") > 0 && Row(r, "T09d.above.low.pe2.deck_vs_g1") > 0, "the stage variants have rows");
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

        private static T09LayerReport FourLayers(bool pass1ReadsHigher)
        {
            var report = new T09LayerReport { StackKey = "Kerbin-weather-rain|Kerbin-cumulus|Kerbin-cumulonimbus|Kerbin-cirrus|", Cold = false, CpuLatDeg = -0.1, CpuLonDeg = -74.5, ShaderLatDeg = -0.1, ShaderLonDeg = -74.5 };
            string[] names = { "Kerbin-weather-rain", "Kerbin-cumulus", "Kerbin-cumulonimbus", "Kerbin-cirrus" };
            float[] cpu = { 0.406f, 0.665f, 0.110f, 0.501f };
            for (int k = 0; k < 4; k++)
            {
                var l = new T09LayerInfo
                {
                    Index = k, Name = names[k], Group = k == 0 ? "weather" : "", TypeName = "Fog", Coverage = 0.5f, Softness = 0.2f, NoiseAmount = 0.3f, SigmaPerM = 0.045f, ThicknessM = 2100f,
                    CpuCover = cpu[k], CpuType = 0f, GpuCover = cpu[k], GpuType = 0f, GpuGoalCover = cpu[k], GpuGoalType = 0f, ShaderCover = cpu[k], ShaderType = 0f,
                    EquivalentThicknessM = 119.0, ModelClosed = new[] { 0.34, 0.07, 0.0 }
                };
                // The pass reads layer 1 higher than the CPU readers do.
                if (k == 1 && pass1ReadsHigher) { l.GpuCover = 0.9f; l.ShaderCover = 0.9f; }
                report.Layers.Add(l);
            }
            return report;
        }

        [TestMethod]
        public void EachLayerIsDrawnAloneAtShiftZeroAndMinus015AtTheGroundViewAndItsRowsAreWritten()
        {
            Run r = StartDiag(x =>
            {
                x.World.LayerDraws = k => k == 1;
                x.World.LayerReport = FourLayers(true);
            });
            Drive(r, 0.02);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            foreach (string cover in new[] { "low", "high" })
            {
                for (int k = 0; k < 4; k++)
                {
                    foreach (string tag in new[] { "only" + k, "only" + k + "c35" })
                    {
                        Assert.IsTrue(r.World.Saved.Any(n => n.Contains("-ground_" + cover + "-" + tag + "-1707x960-natural-half")), "the picture of " + tag + " at the " + cover + " cover");
                        string q = "T09d.ground." + cover + "." + tag + ".";
                        // The fake draws a deck only where layer 1 is named: the layer alone is the sheet, the others leave the sky as the baseline has it.
                        Assert.AreEqual(k == 1 ? 1.0 : 0.0, Row(r, q + "sky_closed_share"), 0.02, q + "closed share");
                        Assert.AreEqual(k == 1, Row(r, q + "deck_fraction") > 0.5, q + "deck fraction");
                    }
                    string p = "T09d.ground." + cover + ".L" + k + ".";
                    Assert.AreEqual(new[] { 0.406, 0.665, 0.110, 0.501 }[k], Row(r, p + "cpu_cover"), 1e-5, p + "CPU map");
                    Assert.AreEqual(k == 1 ? 0.9 : new[] { 0.406, 0.665, 0.110, 0.501 }[k], Row(r, p + "gpu_cover"), 1e-5, p + "GPU map");
                    Assert.AreEqual(0.34, Row(r, p + "model_closed_p1"), 1e-9);
                    Assert.AreEqual(0.0, Row(r, p + "model_closed_c35"), 1e-9);
                    Assert.AreEqual(k == 1 ? 0.235 : 0.0, Row(r, p + "gpu_minus_cpu_cover"), 1e-5);
                    Assert.IsTrue(r.Rows.Any(l => l.Contains("\"metric\":\"" + p + "info\"") && l.Contains("name Kerbin-")), p + "info text");
                }
                Assert.AreEqual(0.0, Row(r, "T09d.ground." + cover + ".layers.stack_cold"));
                Assert.IsTrue(Row(r, "T09d.ground." + cover + ".layers.surface_temp_k") > 100);
            }
            Assert.IsFalse(r.World.Saved.Any(n => n.Contains("-above_") && n.Contains("-only")), "the above view has no layer frames");
            Assert.IsTrue(new[] { 1, 2, 4, 8 }.All(m => r.World.LayerMasks.Contains(m)), "each layer alone was named in the mask");
            Assert.AreEqual(0, r.World.LayerMasks.Last(), "the mask ends at all layers");
            Assert.IsTrue(r.Log.Any(l => l.Contains("Probe: warning gpu_map_differs") && l.Contains(".L1.")), "layer 1's GPU map differs and is named");
            Assert.IsFalse(r.Log.Any(l => l.Contains("Probe: warning gpu_map_differs") && l.Contains(".L0.")));
            Assert.AreEqual(56.0, Row(r, "T09d.frames"));
        }

        [TestMethod]
        public void TheLayerFramesNameTheirShiftInTheTag()
        {
            Assert.AreEqual("only0", T09Probe.OnlyTag(0, false));
            Assert.AreEqual("only3c35", T09Probe.OnlyTag(3, true));
        }

        // Start 43: the game applies a placement a few frames late and the read of the centre of mass lags it; the vessel stands at 85.35 m before the placement.
        private Run StartRealistic(Action<Run> tweak = null)
        {
            return StartDiag(x =>
            {
                x.World.RealisticHold = true;
                x.World.TrueAltitude = 85.35;
                x.World.Terrain = 64.0;
                if (tweak != null) tweak(x);
            });
        }

        [TestMethod]
        public void ARealisticPlacementIsHeldAtThePlacedPointNotAtTheOldOne()
        {
            Run r = StartRealistic();
            Drive(r, 0.02);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            string anchor = r.Log.FirstOrDefault(l => l.StartsWith("T09 hold anchor: "));
            Assert.IsNotNull(anchor, "the anchor is logged");
            Assert.IsTrue(r.Log.Count(l => l.StartsWith("T09 hold anchor: ")) >= 4, "once for each of the four placements");
            Assert.IsFalse(r.Log.Any(l => l.StartsWith("T09 hold anchor: ") && l.Contains("85.3")), "no anchor at the old position: " + anchor);
            Assert.AreEqual(4, Values(r, "T09.view.ground.altitude_m", "plain").Count + Values(r, "T09.view.above.altitude_m", "plain").Count, "each view placed at the first attempt");
            Assert.AreEqual(64.0 + 200.0, Number(r, "T09.view.ground.altitude_m", "plain"), 15.0);
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
        public void APoseThatLeavesBetweenTheReadsIsFlaggedAndTheReadoutIsTakenAnyway()
        {
            Run r = StartDiag(x => x.World.AltitudeJumpOnCaptureTake = 400.0);
            Drive(r, 0.02);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(0.0, Row(r, "T09d.ground.low.off.pose_ok"), "the frame is flagged");
            Assert.AreEqual(0.5, Row(r, "T09d.ground.low.off.sky_median"), 1e-4, "and read");
            Assert.IsTrue(Row(r, "T09d.frames_pose_bad") > 0 && Row(r, "T09d.frames") == 56);
            Assert.IsTrue(r.Log.Any(l => l.Contains("Probe: warning pose_mismatch")));
            Assert.AreEqual(56, r.World.Saved.Count, "the pictures are saved");
        }

        [TestMethod]
        public void AVesselThatNeverReachesItsViewIsReadWhereItStandsAndTheStartGoesOn()
        {
            Run r = StartRealistic(x => x.World.PlaceResult = (call, wanted) => 85.35);
            Drive(r, 0.02);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(56.0, Row(r, "T09d.frames"));
            Assert.AreEqual(0.0, Row(r, "T09d.frames_pose_ok"));
            Assert.AreEqual(0.0, Row(r, "T09d.ground.high.placed_ok"));
            Assert.IsTrue(Row(r, "T09d.ground.high.g1.deck_median") > 0, "the readout is there");
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 hold anchor: ") && l.Contains("placing it again")), "the hold placed it again");
            Assert.IsTrue(r.Log.Any(l => l.Contains("Probe: warning pose_placed_ground")), "named");
        }

        [TestMethod]
        public void CoversThatCannotBeFarApartAreTakenAndFlaggedNotFailed()
        {
            Run r = StartDiag(x => x.Field.Cover = ut => (((int)Math.Round(ut / 3600.0) % 6 + 6) % 6) < 2 ? 0.47f : 0.64f);
            Drive(r, 0.02);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(1.0, Number(r, "T09d.cover_off_band"));
            Assert.AreEqual(0.47, Number(r, "T09d.low.cover"), 1e-6);
            Assert.AreEqual(0.64, Number(r, "T09d.high.cover"), 1e-6);
            Assert.AreEqual(1.0, Number(r, "T09d.covers_distinct"));
        }

        [TestMethod]
        public void BothPicksStandInTheNarrowSunBandAndTheLowOneIsTheLowestCoverAvailable()
        {
            // The sun alternates between 20 and 50 degrees by the hour; covers 0.50 to 0.80, none under 0.4: the low pick is the lowest cover whose sun is in 30 to 55.
            Run r = StartDiag(x =>
            {
                x.World.Sun = ut => ((long)Math.Round(ut / 3600.0)) % 2 == 0 ? 20.0 : 50.0;
                x.Field.Cover = ut => 0.5f + 0.075f * (float)(((long)Math.Round(ut / 3600.0) / 2 % 5 + 5) % 5) / 1f;
            });
            Drive(r, 0.02);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(1.0, Number(r, "T09d.sun_band_narrow"), "the narrow band held a pair");
            Assert.AreEqual(30.0, Number(r, "T09d.sun_band_min_deg"));
            Assert.AreEqual(55.0, Number(r, "T09d.sun_band_max_deg"));
            Assert.AreEqual(50.0, Number(r, "T09d.low.sun_elev_deg"), 1e-6);
            Assert.AreEqual(50.0, Number(r, "T09d.high.sun_elev_deg"), 1e-6);
            Assert.AreEqual(0.0, Number(r, "T09d.sun_elev_diff_deg"), 1e-6);
            Assert.AreEqual(0.5, Number(r, "T09d.low.cover"), 1e-6, "the lowest cover available, above the band 0.2 to 0.4");
            Assert.AreEqual(1.0, Number(r, "T09d.cover_off_band"));
            Assert.IsTrue(Number(r, "T09d.high.cover") - Number(r, "T09d.low.cover") >= 0.15 - 1e-6);
            Assert.AreEqual(1.0, Number(r, "T09d.covers_distinct"));
        }

        [TestMethod]
        public void NoPairOfCoversAtLeast015ApartFailsNamedAndAPairJustAboveItIsTaken()
        {
            Run apart = StartDiag(x => x.Field.Cover = ut => (((long)Math.Round(ut / 3600.0)) % 2 == 0) ? 0.5f : 0.62f);
            Drive(apart, 0.02);
            AssertNamedFailure(apart, "the two covers are the same");
            Run ok = StartDiag(x => x.Field.Cover = ut => (((long)Math.Round(ut / 3600.0)) % 2 == 0) ? 0.5f : 0.66f);
            Drive(ok, 0.02);
            Assert.IsTrue(ok.Probe.Passed, Dump(ok));
            Assert.AreEqual(1.0, Number(ok, "T09d.covers_distinct"));
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
        public void TheSameCoverTwiceFailsNamedBeforeAnyView()
        {
            Run r = StartDiag(x => x.Field.Cover = ut => 0.5f);
            Drive(r, 0.02);
            AssertNamedFailure(r, "the two covers are the same");
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
