using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.D37
{
    // The offline parts of the IVA path and the fallback policy: the judge of the interior mask, the A/B figures the series reads out
    // of the game's picture, the draw policy, the camera-chain check, and the agreement of the probe's rows with its rows file.
    [TestClass]
    public class FrostIvaPremiseTests
    {
        // ---- FrostMaskJudge

        [TestMethod]
        public void PlausibleReadingsMakeTheFirstWayValid()
        {
            var judge = new FrostMaskJudge();
            Assert.IsFalse(judge.Valid);
            Assert.IsFalse(judge.Report(0.30f));
            Assert.IsFalse(judge.Valid, "one reading is not enough");
            Assert.IsFalse(judge.Report(0.32f));
            Assert.IsTrue(judge.Valid);
            Assert.AreEqual(0, judge.Current);
        }

        [TestMethod]
        public void EmptyOrFullReadingsMoveToTheNextWayAfterThreeStrikes()
        {
            var judge = new FrostMaskJudge();
            Assert.IsFalse(judge.Report(0f));
            Assert.IsFalse(judge.Report(1f));
            Assert.IsTrue(judge.Report(0.001f), "the third implausible reading changes the way");
            Assert.AreEqual(1, judge.Current);
            Assert.IsFalse(judge.Valid);
            judge.Report(0.4f);
            judge.Report(0.4f);
            Assert.IsTrue(judge.Valid);
            Assert.AreEqual(1, judge.Current);
        }

        [TestMethod]
        public void AGoodReadingInBetweenResetsTheStrikes()
        {
            var judge = new FrostMaskJudge();
            judge.Report(0f);
            judge.Report(0f);
            judge.Report(0.3f);
            judge.Report(0f);
            judge.Report(0f);
            Assert.AreEqual(0, judge.Current, "two plus two strikes do not add up");
        }

        [TestMethod]
        public void WhenEveryWayFailsTheJudgeIsExhaustedAndNeverValid()
        {
            var judge = new FrostMaskJudge();
            for (int i = 0; i < FrostMaskJudge.Ways.Length * FrostMaskJudge.Strikes + 5; i++) judge.Report(1f);
            Assert.IsTrue(judge.Exhausted);
            Assert.IsFalse(judge.Valid);
            Assert.AreEqual(FrostMaskJudge.Ways.Length - 1, judge.Current);
            judge.Report(0.3f);
            judge.Report(0.3f);
            Assert.IsFalse(judge.Valid, "exhausted stays exhausted until a new IVA entry");
            judge.Reset();
            Assert.IsFalse(judge.Exhausted);
            Assert.AreEqual(0, judge.Current);
        }

        [TestMethod]
        public void PinningPutsAWayByHandAndClamps()
        {
            var judge = new FrostMaskJudge();
            judge.Pin(2);
            Assert.AreEqual(2, judge.Current);
            judge.Pin(99);
            Assert.AreEqual(FrostMaskJudge.Ways.Length - 1, judge.Current);
            judge.Pin(-5);
            Assert.AreEqual(0, judge.Current);
        }

        [TestMethod]
        public void TheWaysStartWithThePictureDifferenceAndKeepTheReconEventBehindIt()
        {
            // The repeat run read a full mask (1.000) in all four depth ways, so the way that reads no depth goes first. RECON P6: the
            // depth is valid from BeforeForwardAlpha on; that event with the depth texture asked for is the first depth way.
            Assert.IsTrue(FrostMaskJudge.Ways[0].Diff);
            Assert.IsFalse(FrostMaskJudge.Ways[0].ForceDepth);
            Assert.AreEqual("BeforeForwardAlpha", FrostMaskJudge.Ways[1].Event);
            Assert.IsTrue(FrostMaskJudge.Ways[1].ForceDepth);
            Assert.IsFalse(FrostMaskJudge.Ways[1].Diff);
            Assert.IsTrue(FrostMaskJudge.Ways.Length >= 4);
            Assert.IsTrue(FrostMaskJudge.Plausible(0.2f));
            Assert.IsFalse(FrostMaskJudge.Plausible(0f));
            Assert.IsFalse(FrostMaskJudge.Plausible(1f));
        }

        // ---- FrostDrawPolicy

        [TestMethod]
        public void TheQuadIsNotDrawnUnderAnUpscalerUnlessAllowed()
        {
            Assert.IsTrue(FrostDrawPolicy.QuadAllowed(false, false), "no upscaler: nothing to smear it");
            Assert.IsFalse(FrostDrawPolicy.QuadAllowed(true, false), "an upscaler smears a screen-fixed scene overlay");
            Assert.IsTrue(FrostDrawPolicy.QuadAllowed(true, true), "frost.quadWithUpscaler");
            Assert.AreEqual(FrostDrawPolicy.Host, FrostDrawPolicy.Mode(true, false));
            Assert.AreEqual(FrostDrawPolicy.Host, FrostDrawPolicy.Mode(true, true));
            Assert.AreEqual(FrostDrawPolicy.Quad, FrostDrawPolicy.Mode(false, true));
            Assert.AreEqual(FrostDrawPolicy.Suppressed, FrostDrawPolicy.Mode(false, false));
        }

        // ---- FrostAbMetrics

        [TestMethod]
        public void AChangeCountsWhenItPassesTenPercentOfTheBrighterImage()
        {
            Assert.IsFalse(FrostAbMetrics.Changed(1.00f, 1.05f));
            Assert.IsTrue(FrostAbMetrics.Changed(1.00f, 1.20f));
            Assert.IsFalse(FrostAbMetrics.Changed(0.001f, 0.004f), "a dark pixel does not flip on noise");
            Assert.IsTrue(FrostAbMetrics.Changed(0.0f, 0.2f));
        }

        [TestMethod]
        public void ChangedShareInSeparatesTheInsideFromTheOutsideOfTheMask()
        {
            const int w = 8, h = 8;
            var clear = new float[w * h];
            var frosted = new float[w * h];
            for (int i = 0; i < clear.Length; i++) clear[i] = 0.5f;
            Array.Copy(clear, frosted, clear.Length);
            // The mask covers the left half; the frost changes the right half only.
            var mask = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    mask[y * w + x] = x < w / 2 ? 1f : 0f;
                    if (x >= w / 2) frosted[y * w + x] = 0.9f;
                }
            Assert.AreEqual(0.5f, FrostAbMetrics.ChangedShare(frosted, clear), 1e-6f);
            Assert.AreEqual(0f, FrostAbMetrics.ChangedShareIn(frosted, clear, w, h, mask, w, h, true), 1e-6f);
            Assert.AreEqual(1f, FrostAbMetrics.ChangedShareIn(frosted, clear, w, h, mask, w, h, false), 1e-6f);
            // A mask of another size is read at the image pixel's place.
            var coarse = new float[] { 1f, 0f, 1f, 0f };
            Assert.AreEqual(0f, FrostAbMetrics.ChangedShareIn(frosted, clear, w, h, coarse, 2, 2, true), 1e-6f);
            Assert.IsTrue(float.IsNaN(FrostAbMetrics.ChangedShareIn(frosted, clear, w, h, new float[] { 1f }, 1, 1, false)));
        }

        [TestMethod]
        public void AControlOfTwoEqualImagesChangesNothing()
        {
            var a = new float[100];
            for (int i = 0; i < a.Length; i++) a[i] = 0.1f + 0.01f * i;
            Assert.AreEqual(0f, FrostAbMetrics.ChangedShare(a, (float[])a.Clone()), 1e-6f);
            Assert.IsTrue(float.IsNaN(FrostAbMetrics.ChangedShare(new float[0], new float[0])));
        }

        [TestMethod]
        public void OrientationReadsTheFrostTheRightWayUp()
        {
            // The predicted opacity map of a real state; a picture whose change follows it reads the right way up, its mirror image
            // reads upside down.
            const int w = 160, h = 90;
            FrostTuning t = FrostTuning.Defaults();
            byte[] atlas = FrostAtlas.Build();
            float[] predicted = FrostShape.AlphaMap(atlas, FrostShape.Growth(0.8f, ref t), 0.8f, 16f / 9f, ref t, w, h);
            var clear = new float[w * h];
            var frosted = new float[w * h];
            for (int i = 0; i < clear.Length; i++)
            {
                clear[i] = 0.5f;
                frosted[i] = 0.5f + 0.4f * predicted[i];
            }
            float normal, flipped;
            FrostAbMetrics.Orientation(frosted, clear, predicted, w, h, out normal, out flipped);
            Assert.IsTrue(normal > 0.9f, "normal " + normal);
            Assert.IsTrue(normal > flipped + 0.02f, "normal " + normal + " flipped " + flipped);

            var upside = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    upside[y * w + x] = frosted[(h - 1 - y) * w + x];
            FrostAbMetrics.Orientation(upside, clear, predicted, w, h, out normal, out flipped);
            Assert.IsTrue(flipped > normal + 0.02f, "a picture turned over reads as flipped: normal " + normal + " flipped " + flipped);
        }

        [TestMethod]
        public void TheAlphaMapAgreesWithTheCoverageOfTheShapeMaths()
        {
            FrostTuning t = FrostTuning.Defaults();
            byte[] atlas = FrostAtlas.Build();
            const int w = 128, h = 72;
            float growth = FrostShape.Growth(0.4f, ref t);
            float[] map = FrostShape.AlphaMap(atlas, growth, 0.4f, 16f / 9f, ref t, w, h);
            int hit = 0;
            foreach (float a in map) if (a >= 0.15f) hit++;
            float viaMap = hit / (float)map.Length;
            float viaCoverage = FrostShape.CoverageOf(atlas, 0.4f, 1f, 16f / 9f, ref t, 0.15f, w, h);
            Assert.AreEqual(viaCoverage, viaMap, 1e-6f);
        }

        // ---- The camera chain

        private static List<KeyValuePair<float, string>> Chain(params object[] pairs)
        {
            var list = new List<KeyValuePair<float, string>>();
            for (int i = 0; i < pairs.Length; i += 2) list.Add(new KeyValuePair<float, string>(Convert.ToSingle(pairs[i + 1]), (string)pairs[i]));
            return list;
        }

        [TestMethod]
        public void TheChainCheckNeedsThePresenterWhereTheRigRuns()
        {
            // The chain measured in the game with the rig running (K-W3b frost, 2026-11-12).
            List<KeyValuePair<float, string>> measured = Chain("GalaxyCamera", -4, "Camera ScaledSpace", -3, "Camera 00", 0, "markerCam", 1, "ReDefinitionPresenter", 1.1,
                "UIMainCamera", 90, "UIVectorCamera", 91, "Camera", 100);
            // The chain as the game had it before the rig built its stack: what the first run read and judged against the presenter.
            List<KeyValuePair<float, string>> tooEarly = Chain("GalaxyCamera", -4, "Camera ScaledSpace", -3, "Camera 00", 0, "Canvas Camera ", 0, "UIVectorCamera", 91, "Camera", 100);
            List<KeyValuePair<float, string>> bare = Chain("Camera 00", 0);
            Assert.IsTrue(CameraFrostProbe.ChainMatchesRecon(measured, true));
            Assert.IsTrue(CameraFrostProbe.ChainMatchesRecon(measured, false));
            Assert.IsFalse(CameraFrostProbe.ChainMatchesRecon(tooEarly, true), "a chain read before the rig built its stack fails while the rig runs");
            Assert.IsTrue(CameraFrostProbe.ChainMatchesRecon(tooEarly, false));
            Assert.IsFalse(CameraFrostProbe.ChainMatchesRecon(bare, true), "no presenter while the rig runs says nothing about the rig order");
            Assert.IsTrue(CameraFrostProbe.ChainMatchesRecon(bare, false), "without the rig the main camera alone is the chain");
            Assert.IsFalse(CameraFrostProbe.ChainMatchesRecon(Chain("Camera ScaledSpace", 1, "Camera 00", 0, "ReDefinitionPresenter", 0.1), true));
            Assert.IsFalse(CameraFrostProbe.ChainMatchesRecon(Chain("Camera 00", 0, "markerCam", 1.2, "ReDefinitionPresenter", 1.1), true), "a redirected camera behind the presenter");
            Assert.IsFalse(CameraFrostProbe.ChainMatchesRecon(Chain("Camera 00", 0, "ReDefinitionPresenter", 1.1, "UIMainCamera", 1.0), true), "the UI cameras draw after the presenter");
        }

        [TestMethod]
        public void TheDriverRaisesTheLevelTooSlowlyForTheProbesCeilingWindow()
        {
            // The first game run set the field to level 1 and read the exposure after 14 s: the frame driver moves the level at most
            // 1/90 per game second, so the frost saw 0.16, not 1. The probe forces the level (AtmospheresSettings.ForceLevel jumps it).
            float level = 0f;
            for (int i = 0; i < 14 * 30; i++) level = LevelLimiter.Step(level, 1f, 1.0 / 30.0, false);
            Assert.AreEqual(14f / 90f, level, 1e-3f);
            Assert.AreEqual(1f, LevelLimiter.Step(0f, 1f, 1.0 / 30.0, true), "a forced level jumps");
        }

        [TestMethod]
        public void TheReconDumpHoldsTheChainTheProbeChecks()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string dump = Path.Combine(root, "tests", "data", "recon", "dumps", "run2-cameras-flight.json");
            Assert.IsTrue(File.Exists(dump), dump);
            string text = File.ReadAllText(dump);
            foreach (string name in new[] { "Camera 00", "ReDefinitionPresenter", "markerCam" })
                StringAssert.Contains(text, name);
        }

        // ---- The state machine's ceilings (review R5, R7)

        private static FrostInput Snow(FrostView view, float airK, float wind)
        {
            return new FrostInput
            {
                Dt = 1f / 30f, View = view, Level = 1f, Type = ReDefinition.Core.PrecipType.Snow, TemperatureK = airK, FrostLimitK = 268f, Strength = 1f,
                AirflowMs = wind, GustPhase = 0.2f, Sheltered = false, GravityX = 0f, GravityY = -1f, GravityInPlane = 1f, Seed = 5
            };
        }

        private static float Settled(FrostView view, float airK, float wind)
        {
            FrostTuning t = FrostTuning.Defaults();
            var frost = new CameraFrost();
            FrostInput input = Snow(view, airK, wind);
            for (int i = 0; i < 30 * 400; i++) frost.Step(ref input, ref t);
            return frost.Exposure;
        }

        [TestMethod]
        public void ExposureOneNeedsAirflowOverTheGlassAndColdBelowTheLimit()
        {
            // The ceiling is level * cold * (0.55 + 0.45 * airflow / 20 m/s) * 1.25: 0.6875 without wind, 1 from about 11.2 m/s on.
            Assert.IsTrue(Settled(FrostView.Eva, 255f, 20f) > 0.99f, "level 1, -18 C, 20 m/s");
            Assert.IsTrue(Settled(FrostView.Eva, 255f, 12f) > 0.99f, "12 m/s of airflow is enough for exposure 1");
            Assert.AreEqual(0.6875f, Settled(FrostView.Eva, 255f, 0f), 0.01f, "the wind-free ceiling");
            Assert.IsTrue(Settled(FrostView.Eva, 255f, 8f) < 0.95f, "8 m/s stays under 1");
            Assert.IsTrue(Settled(FrostView.Eva, 255f, 5f) < Settled(FrostView.Eva, 255f, 20f));
            Assert.IsTrue(Settled(FrostView.Eva, 266f, 20f) < 0.5f, "a glass just under the limit frosts weakly");
        }

        [TestMethod]
        public void TheCeilingTheProbeJudgesAgainstIsWhereTheExposureSettles()
        {
            // The calm row compares the settled exposure with CameraFrost.Ceiling for the airflow actually over the glass (a vessel that
            // moves is not calm): ratio 1 for any airflow, and the ceiling follows the airflow.
            foreach (float airflow in new[] { 0f, 5f, 17f, 40f })
            {
                FrostTuning t = FrostTuning.Defaults();
                var frost = new CameraFrost();
                FrostInput input = Snow(FrostView.Eva, 255f, airflow);
                for (int i = 0; i < 30 * 400; i++) frost.Step(ref input, ref t);
                Assert.IsTrue(frost.Ceiling > 0.5f, "airflow " + airflow);
                Assert.AreEqual(frost.Ceiling, frost.Exposure, 0.005f, "airflow " + airflow);
            }
            var calm = new CameraFrost();
            var moving = new CameraFrost();
            FrostTuning tuning = FrostTuning.Defaults();
            FrostInput a = Snow(FrostView.Eva, 255f, 0f), b = Snow(FrostView.Eva, 255f, 17f);
            calm.Step(ref a, ref tuning);
            moving.Step(ref b, ref tuning);
            Assert.IsTrue(moving.Ceiling > calm.Ceiling + 0.1f, "17 m/s of vessel speed raises the ceiling above the wind-free one");
        }

        [TestMethod]
        public void ThePictureDifferencePassIsInTheShader()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string shader = File.ReadAllText(Path.Combine(root, "unity", "Assets", "ReDefinitionAtmospheres", "Shaders", "ReDefinitionAtmospheresFrost.shader"));
            StringAssert.Contains(shader, "Name \"FrostInteriorDiff\"");
            StringAssert.Contains(shader, "sampler2D _FrostBefore;");
            Assert.AreEqual(4, FrostPass.PassMaskDiff, "the pass index the renderer draws is the fifth pass of the shader");
        }

        [TestMethod]
        public void TheIvaGlassStartsToFrostBelowMinusSixtyThreeCelsiusOfAirAndFullyBelowMinusNinetySeven()
        {
            // The owner decision of review R5 in numbers: with the shipped insulation 0.7 the cabin glass is air + 0.7 * (20 C - air); frost
            // starts where the glass is under the limit (-5 C) and is full 10 K further down. No Kerbin air is that cold.
            FrostTuning t = FrostTuning.Defaults();
            float airAtLimit = (268f - t.IvaInsulation * t.CabinK) / (1f - t.IvaInsulation);
            float airFull = (258f - t.IvaInsulation * t.CabinK) / (1f - t.IvaInsulation);
            Assert.AreEqual(209.7f, airAtLimit, 0.5f, "air at which the cabin glass reaches the frost limit (-63 C)");
            Assert.AreEqual(176.3f, airFull, 0.5f, "air at which the cold is full (-97 C)");
            Assert.IsTrue(Settled(FrostView.Iva, 245f, 20f) < 0.02f, "-28 C outside a warm cabin stays clear");
            Assert.IsTrue(Settled(FrostView.Iva, 150f, 20f) > 0.3f, "-123 C outside frosts the glass");
        }

        // ---- The probe against its rows file

        [TestMethod]
        public void EveryMetricTheProbeEmitsHasARow()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string probe = File.ReadAllText(Path.Combine(root, "src", "Atmospheres", "Light", "Frost", "Probe", "CameraFrostProbe.cs"));
            string rows = File.ReadAllText(Path.Combine(root, "tests", "series", "rows", "D37.json"));
            var emitted = new SortedSet<string>();
            // The numeric and flag metrics (Emit); the text metrics (EmitText) are record lines of the log and have no threshold.
            foreach (Match m in Regex.Matches(probe, "\\bEmit\\(\"(D37\\.[A-Za-z0-9_.]+)\","))
                emitted.Add(m.Groups[1].Value);
            // Names built from a loop variable.
            foreach (string phase in new[] { "host", "quad" })
                foreach (string suffix in new[] { "_control_changed", "_changed" })
                    emitted.Add("D37.iva_ab_" + phase + suffix);
            for (int way = 0; way < FrostMaskJudge.Ways.Length; way++) emitted.Add("D37.iva_mask_cov_way" + way);
            Assert.IsTrue(emitted.Count > 40, "the probe's metrics were found: " + emitted.Count);
            var missing = new List<string>();
            foreach (string name in emitted)
                if (!rows.Contains("\"metric\": \"" + name + "\"")) missing.Add(name);
            Assert.AreEqual(0, missing.Count, "metrics without a row: " + string.Join(", ", missing.ToArray()));
        }

        [TestMethod]
        public void TheRowsTheReviewMakesRequiredAreRequired()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string rows = File.ReadAllText(Path.Combine(root, "tests", "series", "rows", "D37.json"));
            foreach (string name in new[] { "D37.iva_mask_ok", "D37.iva_hook_calls", "D37.iva_hook_camera_known", "D37.iva_hook_calls_drawn",
                                            "D37.iva_ab_host_control_changed", "D37.iva_ab_props_clear", "D37.iva_ab_orientation_ok", "D37.iva_policy_ok",
                                            "D37.exposure_ceiling_20ms", "D37.exposure_ceiling_calm_ratio" })
            {
                Match line = Regex.Match(rows, "\\{[^{}]*\"metric\": \"" + Regex.Escape(name) + "\"[^{}]*\\}");
                Assert.IsTrue(line.Success, name + " has no row");
                StringAssert.DoesNotMatch(line.Value, new Regex("\"required\": false"), name + " must be required");
            }
        }
    }
}
