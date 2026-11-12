using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Audio;
using UnityEngine;

namespace ReDefinition.Tests
{
    // The flash envelope, the bolt geometry, the thunder policy and the thunder sound (package T07).
    [TestClass]
    public class LightningShapeTests
    {
        private static FlashEvent Event(uint seed, int bumps, FlashKind kind)
        {
            return new FlashEvent { Seed = seed, Bumps = bumps, Kind = kind, Ut = 100.0, TopM = 9000f, StartHeightM = 5000f, CloudLightHeightM = 4000f };
        }

        // ---- the envelope ----

        [TestMethod]
        public void TheFirstStrokeIsTheBrightestAndTheLaterOnesAreWeaker()
        {
            for (uint seed = 1; seed < 40; seed++)
            {
                var e = Event(seed, 3, FlashKind.Cloud);
                double a0 = FlashEnvelope.BumpAmplitude(ref e, 0), a1 = FlashEnvelope.BumpAmplitude(ref e, 1), a2 = FlashEnvelope.BumpAmplitude(ref e, 2);
                Assert.IsTrue(a0 > a1 && a1 > a2, "seed " + seed + ": " + a0 + " " + a1 + " " + a2);
            }
        }

        [TestMethod]
        public void TheLightRisesInMillisecondsAndFallsAwayWithinHalfASecond()
        {
            var e = Event(5, 2, FlashKind.Ground);
            Assert.AreEqual(0.0, FlashEnvelope.Level(ref e, -0.01), 0.0);
            double peak = 0.0, tPeak = 0.0;
            for (double t = 0.0; t < 1.0; t += 0.001)
            {
                double l = FlashEnvelope.Level(ref e, t);
                Assert.IsTrue(l >= 0.0 && l <= 1.25 && !double.IsNaN(l));
                if (l > peak) { peak = l; tPeak = t; }
            }
            Assert.IsTrue(peak > 0.85, "peak " + peak);
            Assert.IsTrue(tPeak < 0.02, "the peak at " + tPeak);
            Assert.IsTrue(FlashEnvelope.DurationS(ref e) < 0.8, "duration " + FlashEnvelope.DurationS(ref e));
            Assert.IsTrue(FlashEnvelope.Level(ref e, FlashEnvelope.DurationS(ref e)) < 0.05);
        }

        [TestMethod]
        public void StrokesFollowEachOtherNoCloserThanSixtyMilliseconds()
        {
            for (uint seed = 1; seed < 60; seed++)
            {
                var e = Event(seed, 3, FlashKind.Ground);
                double t1 = FlashEnvelope.BumpTime(ref e, 1), t2 = FlashEnvelope.BumpTime(ref e, 2);
                Assert.IsTrue(t1 >= 0.06 && t2 - t1 >= 0.06 && t2 < 0.4, "seed " + seed + ": " + t1 + " " + t2);
            }
        }

        [TestMethod]
        public void AGroundFlashKeepsAGlowAfterItsLastStroke()
        {
            var ground = Event(8, 1, FlashKind.Ground);
            var cloud = Event(8, 1, FlashKind.Cloud);
            Assert.IsTrue(FlashEnvelope.Level(ref ground, 0.2) > FlashEnvelope.Level(ref cloud, 0.2) + 0.03);
        }

        // ---- the bolt ----

        [TestMethod]
        public void ABoltRunsFromTheChargeRegionToTheGroundAndNeverUnderIt()
        {
            var bp = new BoltParams();
            for (uint seed = 1; seed <= 40; seed++)
            {
                var strands = BoltGeometry.Build(seed, 5000f, new Vector2(300f, -200f), bp);
                Assert.IsTrue(strands.Count >= 1 && strands.Count <= bp.MaxStrands);
                var main = strands[0];
                Assert.AreEqual(5000f, main.Points[0].y, 1e-3f);
                Assert.AreEqual(0f, main.Points[main.Points.Count - 1].magnitude, 1e-3f);
                Assert.AreEqual((1 << bp.MainLevels) + 1, main.Points.Count);
                foreach (var s in strands)
                    foreach (var p in s.Points)
                    {
                        Assert.IsTrue(p.y >= 0f, "seed " + seed + " point below the ground " + p);
                        Assert.IsFalse(float.IsNaN(p.x + p.y + p.z));
                    }
            }
        }

        [TestMethod]
        public void TheSameSeedGivesTheSameBoltAndEverySeedBranches()
        {
            var bp = new BoltParams();
            var a = BoltGeometry.Build(77u, 4000f, Vector2.zero, bp);
            var b = BoltGeometry.Build(77u, 4000f, Vector2.zero, bp);
            Assert.AreEqual(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
                for (int k = 0; k < a[i].Points.Count; k++)
                    Assert.AreEqual(a[i].Points[k], b[i].Points[k]);
            int branched = 0;
            for (uint seed = 1; seed <= 100; seed++)
                if (BoltGeometry.Build(seed, 4000f, Vector2.zero, bp).Count > 1) branched++;
            Assert.IsTrue(branched >= 85, "bolts with a branch: " + branched + " of 100");
        }

        [TestMethod]
        public void TheMainChannelIsJaggedButKeepsToTheDirection()
        {
            var bp = new BoltParams();
            var ratios = new List<float>();
            for (uint seed = 1; seed <= 60; seed++)
            {
                var main = BoltGeometry.Build(seed, 5000f, Vector2.zero, bp)[0];
                ratios.Add(BoltGeometry.ChannelLengthM(main) / 5000f);
            }
            Assert.IsTrue(ratios.Min() > 1.01f, "min " + ratios.Min());
            Assert.IsTrue(ratios.Max() < 1.6f, "max " + ratios.Max());
            Assert.IsTrue(ratios.Average() > 1.04f && ratios.Average() < 1.3f, "mean " + ratios.Average());
        }

        [TestMethod]
        public void BranchesAreDimmerAndThinnerThanTheMainChannelAndStartOnIt()
        {
            var bp = new BoltParams();
            var strands = BoltGeometry.Build(3u, 6000f, Vector2.zero, bp);
            for (int i = 1; i < strands.Count; i++)
            {
                var s = strands[i];
                Assert.IsTrue(s.Brightness < strands[0].Brightness && s.WidthM < strands[0].WidthM);
                Assert.IsTrue(s.Parent >= 0 && s.Parent < i);
                Assert.AreEqual(strands[s.Parent].Points[s.ParentIndex], s.Points[0]);
            }
        }

        // ---- the thunder policy ----

        [TestMethod]
        public void TheDelayIsTheDistanceOverTheSpeedOfSoundWithinFivePercent()
        {
            var s = new ThunderSettings();
            foreach (double d in new[] { 100.0, 343.2, 1000.0, 3432.0, 10000.0, 25000.0 })
            {
                double delay = ThunderPolicy.DelayGameS(d, s);
                Assert.AreEqual(d / 343.0, delay, d / 343.0 * 0.05, "distance " + d);
            }
            var plan = ThunderPolicy.Plan(3432.0, default(ListenerState), true, 1f, 1f, 0, s);
            Assert.IsTrue(plan.Play);
            Assert.AreEqual(10.0, plan.DelayS, 0.5);
        }

        [TestMethod]
        public void WithoutTheRealisticDelayTheThunderFollowsAtOnce()
        {
            var s = new ThunderSettings { RealisticDelay = false };
            Assert.AreEqual(0.0, ThunderPolicy.DelayGameS(20000.0, s));
            Assert.AreEqual(0.0, ThunderPolicy.Plan(20000.0, default(ListenerState), true, 1f, 1f, 0, s).DelayS);
        }

        [TestMethod]
        public void TimeWarpShortensTheDelayInRealSeconds()
        {
            var s = new ThunderSettings();
            double one = ThunderPolicy.Plan(6000.0, default(ListenerState), true, 1f, 1f, 0, s).DelayS;
            double four = ThunderPolicy.Plan(6000.0, default(ListenerState), true, 4f, 1f, 0, s).DelayS;
            Assert.AreEqual(one / 4.0, four, 1e-9);
        }

        [TestMethod]
        public void FartherThunderIsQuieterDarkerAndRollsLonger()
        {
            var s = new ThunderSettings();
            float gain = float.MaxValue, cutoff = float.MaxValue, duration = 0f, crack = float.MaxValue;
            foreach (double d in new[] { 200.0, 800.0, 2000.0, 5000.0, 10000.0, 18000.0 })
            {
                var plan = ThunderPolicy.Plan(d, default(ListenerState), true, 1f, 1f, 0, s);
                Assert.IsTrue(plan.Play, "distance " + d);
                Assert.IsTrue(plan.Gain < gain && plan.CutoffHz < cutoff && plan.DurationS > duration && plan.CrackGain < crack, "distance " + d);
                gain = plan.Gain; cutoff = plan.CutoffHz; duration = plan.DurationS; crack = plan.CrackGain;
            }
            Assert.IsTrue(ThunderPolicy.DurationS(1000.0) < 4f && ThunderPolicy.DurationS(20000.0) > 8f);
        }

        [TestMethod]
        public void ThunderIsSilentInAVacuumOnTheMapAtTooGreatADistanceAndWhenAllVoicesAreBusy()
        {
            var s = new ThunderSettings();
            Assert.IsFalse(ThunderPolicy.Plan(2000.0, default(ListenerState), false, 1f, 1f, 0, s).Play);
            Assert.IsFalse(ThunderPolicy.Plan(2000.0, new ListenerState { MapView = true }, true, 1f, 1f, 0, s).Play);
            Assert.IsFalse(ThunderPolicy.Plan(s.MaxAudibleM + 1.0, default(ListenerState), true, 1f, 1f, 0, s).Play);
            Assert.IsFalse(ThunderPolicy.Plan(2000.0, default(ListenerState), true, 1f, 1f, s.MaxVoices, s).Play);
            Assert.IsFalse(ThunderPolicy.Plan(2000.0, default(ListenerState), true, 1f, 0f, 0, s).Play);
        }

        [TestMethod]
        public void TheCabinTakesTheTopOffAndSixDecibels()
        {
            var s = new ThunderSettings();
            var open = ThunderPolicy.Plan(1500.0, default(ListenerState), true, 1f, 1f, 0, s);
            var cabin = ThunderPolicy.Plan(1500.0, new ListenerState { Cabin = true }, true, 1f, 1f, 0, s);
            Assert.AreEqual(open.Gain * 0.5f, cabin.Gain, 1e-6f);
            Assert.IsTrue(cabin.CutoffHz <= 700f && cabin.CutoffHz < open.CutoffHz);
            Assert.IsTrue(cabin.CrackGain < open.CrackGain);
        }

        [TestMethod]
        public void ThunderFadesOutBetweenTheFadeAndTheAudibleDistance()
        {
            var s = new ThunderSettings();
            float near = ThunderPolicy.Loudness(s.FadeStartM - 10.0, s);
            float mid = ThunderPolicy.Loudness((s.FadeStartM + s.MaxAudibleM) * 0.5, s);
            Assert.IsTrue(near > mid && mid > 0f);
            Assert.AreEqual(0f, ThunderPolicy.Loudness(s.MaxAudibleM, s), 1e-6f);
        }

        // ---- the thunder sound ----

        private static ThunderPlan PlanAt(double d)
        {
            return ThunderPolicy.Plan(d, default(ListenerState), true, 1f, 1f, 0, new ThunderSettings());
        }

        private static double Rms(float[] x, int from, int to)
        {
            double s = 0.0;
            for (int i = from; i < to; i++) s += x[i] * x[i];
            return Math.Sqrt(s / Math.Max(1, to - from));
        }

        // The share of the energy in the sample-to-sample change: grows with the high-frequency content.
        private static double Brightness(float[] x)
        {
            double d = 0.0, t = 1e-12;
            for (int i = 1; i < x.Length; i++) { d += (x[i] - x[i - 1]) * (x[i] - x[i - 1]); t += x[i] * x[i]; }
            return Math.Sqrt(d / t);
        }

        [TestMethod]
        public void TheThunderIsAPureFunctionOfTheSeedAndThePlan()
        {
            var plan = PlanAt(4000.0);
            var a = ThunderSynth.Render(99u, plan);
            var b = ThunderSynth.Render(99u, plan);
            var c = ThunderSynth.Render(100u, plan);
            Assert.AreEqual(ThunderSynth.LengthOf(plan), a.Length);
            CollectionAssert.AreEqual(a, b);
            Assert.IsFalse(a.SequenceEqual(c));
        }

        [TestMethod]
        public void TheThunderIsFiniteWithinRangeAndEndsInSilence()
        {
            foreach (double d in new[] { 150.0, 1200.0, 6000.0, 20000.0 })
            {
                var plan = PlanAt(d);
                var x = ThunderSynth.Render(5u, plan);
                float peak = 0f;
                foreach (float v in x)
                {
                    Assert.IsFalse(float.IsNaN(v) || float.IsInfinity(v));
                    peak = Math.Max(peak, Math.Abs(v));
                }
                Assert.IsTrue(peak > 0.5f && peak <= 0.98f, "distance " + d + " peak " + peak);
                Assert.AreEqual(0f, x[x.Length - 1], 1e-4f);
                Assert.IsTrue(Rms(x, x.Length - 2000, x.Length) < 0.02, "the end is not quiet at " + d);
            }
        }

        [TestMethod]
        public void NearThunderIsBrighterAndOpensWithACrackThatFarThunderLacks()
        {
            var near = ThunderSynth.Render(7u, PlanAt(250.0));
            var far = ThunderSynth.Render(7u, PlanAt(16000.0));
            Assert.IsTrue(Brightness(near) > 2.0 * Brightness(far), "near " + Brightness(near) + " far " + Brightness(far));
            int sr = ThunderSynth.SampleRate;
            double nearCrack = Rms(near, 0, (int)(0.04 * sr)), nearLater = Rms(near, (int)(0.5 * sr), (int)(0.7 * sr));
            Assert.IsTrue(nearCrack > nearLater * 0.8, "crack " + nearCrack + " later " + nearLater);
            // The far clip's first 40 ms hold the thump's rise only: no broadband burst.
            Assert.IsTrue(Brightness(far) < 0.12, "far brightness " + Brightness(far));
        }

        [TestMethod]
        public void FarThunderRollsLongerThanNearThunder()
        {
            var near = ThunderSynth.Render(3u, PlanAt(500.0));
            var far = ThunderSynth.Render(3u, PlanAt(18000.0));
            Assert.IsTrue(far.Length > near.Length * 2);
            int sr = ThunderSynth.SampleRate;
            // Time at which the rolling envelope (50 ms rms) last exceeds a tenth of its maximum.
            Func<float[], double> tail = x =>
            {
                int win = sr / 20;
                double max = 0.0;
                var rms = new List<double>();
                for (int i = 0; i + win < x.Length; i += win) { double r = Rms(x, i, i + win); rms.Add(r); max = Math.Max(max, r); }
                int last = 0;
                for (int i = 0; i < rms.Count; i++) if (rms[i] > 0.1 * max) last = i;
                return last * 0.05;
            };
            Assert.IsTrue(tail(far) > tail(near) + 3.0, "near " + tail(near) + " far " + tail(far));
        }

        [TestMethod]
        public void RenderingAClipIsFastEnoughForAWorkerThread()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ThunderSynth.Render(1u, PlanAt(20000.0));
            Assert.IsTrue(watch.ElapsedMilliseconds < 400, "ms " + watch.ElapsedMilliseconds);
        }

        // ---- the sources of sound ----

        [TestMethod]
        public void TheLightningCodeLoadsNoRecordedSound()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string[] files =
            {
                Path.Combine(root, "src", "Atmospheres", "Audio", "Runtime", "ThunderPolicy.cs"),
                Path.Combine(root, "src", "Atmospheres", "Render", "Lightning", "ThunderSynth.cs"),
                Path.Combine(root, "src", "Atmospheres", "Render", "Lightning", "LightningModule.cs")
            };
            foreach (string path in files)
            {
                if (!File.Exists(path)) continue;
                string text = File.ReadAllText(path);
                foreach (string forbidden in new[] { "GetAudioClip", "ExistsAudioClip", "LoadAudioClip", ".ogg\"", ".wav\"", ".mp3\"", "Resources.Load", "UnityWebRequest" })
                    Assert.IsFalse(text.Contains(forbidden), Path.GetFileName(path) + " names " + forbidden);
            }
        }
    }
}
