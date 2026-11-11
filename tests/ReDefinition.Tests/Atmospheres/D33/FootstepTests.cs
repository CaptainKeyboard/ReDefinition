using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres.Audio;

namespace ReDefinition.Tests.Atmospheres.D33
{
    // The stride integrator, the gating of the steps and the shaping of a step.
    [TestClass]
    public class FootstepTests
    {
        private static int Walk(StrideIntegrator s, double metres, double speed, double dt, List<StepEvent> events = null)
        {
            int steps = 0;
            double done = 0;
            while (done < metres)
            {
                float d = (float)(speed * dt);
                StepEvent ev;
                if (s.Feed(d, (float)speed, true, (float)dt, out ev))
                {
                    steps++;
                    if (events != null) events.Add(ev);
                }
                done += d;
            }
            return steps;
        }

        [TestMethod]
        public void NStepsForNMetres()
        {
            foreach (double speed in new[] { 1.0, 2.0, 2.5 })
            {
                foreach (double dt in new[] { 1.0 / 30, 1.0 / 60, 1.0 / 144 })
                {
                    var s = new StrideIntegrator();
                    double metres = 30;
                    int steps = Walk(s, metres, speed, dt);
                    double expected = metres / StrideIntegrator.WalkStrideM;
                    Assert.IsTrue(steps >= expected * 0.9 && steps <= expected * 1.1, "speed " + speed + " dt " + dt + ": " + steps + " steps for " + metres + " m, expected " + expected);
                }
            }
        }

        [TestMethod]
        public void ARunUsesTheLongerStrideAndIsFlaggedAsRun()
        {
            var s = new StrideIntegrator();
            var events = new List<StepEvent>();
            int steps = Walk(s, 30, 5.0, 1.0 / 60, events);
            double expected = 30 / StrideIntegrator.RunStrideM;
            Assert.IsTrue(steps >= expected * 0.9 && steps <= expected * 1.1, steps + " vs " + expected);
            foreach (StepEvent e in events) Assert.IsTrue(e.Run);
        }

        [TestMethod]
        public void TheFeetAlternateAndTheFirstStepAfterStandingIsSkipped()
        {
            var s = new StrideIntegrator();
            var events = new List<StepEvent>();
            Walk(s, 10, 2.0, 1.0 / 60, events);
            Assert.IsTrue(events.Count > 6);
            for (int i = 1; i < events.Count; i++) Assert.AreNotEqual(events[i - 1].LeftFoot, events[i].LeftFoot);
            // Stand for a second, walk 0.8 m: one stride is covered, but it is the first step after standing.
            StepEvent ev;
            for (int i = 0; i < 60; i++) s.Feed(0f, 0f, true, 1f / 60f, out ev);
            int again = Walk(s, 0.75, 2.0, 1.0 / 60);
            Assert.AreEqual(0, again, "the first step after standing is skipped");
            Assert.AreEqual(1, Walk(s, 0.75, 2.0, 1.0 / 60), "the next one sounds");
        }

        [TestMethod]
        public void ABriefStopDoesNotMakeANewFirstStep()
        {
            var s = new StrideIntegrator();
            Walk(s, 5, 2.0, 1.0 / 60);
            StepEvent ev;
            for (int i = 0; i < 6; i++) s.Feed(0f, 0f, true, 1f / 60f, out ev);
            Assert.IsTrue(Walk(s, 1.5, 2.0, 1.0 / 60) >= 1);
        }

        [TestMethod]
        public void NothingIsFoundOffTheGroundOrWhileStanding()
        {
            var s = new StrideIntegrator();
            StepEvent ev;
            for (int i = 0; i < 600; i++) Assert.IsFalse(s.Feed(0.03f, 2f, false, 1f / 60f, out ev));
            for (int i = 0; i < 600; i++) Assert.IsFalse(s.Feed(0f, 0.05f, true, 1f / 60f, out ev));
        }

        [TestMethod]
        public void TheGateByCoverWaterAndIce()
        {
            // cover, water, ice, custom clip, surface setting -> surface
            object[][] rows =
            {
                new object[] { 0.0f, false, false, false, true, StepSurface.None },
                new object[] { 0.05f, false, false, false, true, StepSurface.None },
                new object[] { 0.12f, false, false, false, true, StepSurface.Snow },
                new object[] { 0.9f, false, false, false, false, StepSurface.Snow },
                new object[] { 0.9f, true, false, false, true, StepSurface.None },
                new object[] { 0.9f, false, true, false, true, StepSurface.None },
                new object[] { 0.0f, false, false, true, true, StepSurface.Custom },
                new object[] { 0.0f, false, false, true, false, StepSurface.None },
                new object[] { 0.0f, true, false, true, true, StepSurface.None },
                new object[] { 0.5f, false, false, true, true, StepSurface.Snow }
            };
            foreach (object[] r in rows)
            {
                var q = new SurfaceQuery { Cover01 = (float)r[0], Water = (bool)r[1], IceRink = (bool)r[2], HasCustomClip = (bool)r[3] };
                Assert.AreEqual((StepSurface)r[5], FootstepGate.Decide(q, (bool)r[4]), string.Join(" ", Array.ConvertAll(r, o => o.ToString())));
            }
        }

        [TestMethod]
        public void TheFootCoverIsCachedForHalfASecond()
        {
            var t = new TimedValue();
            Assert.IsTrue(t.NeedsUpdate(10f));
            t.Set(0.7f, 10f, 0.5f);
            Assert.IsFalse(t.NeedsUpdate(10.3f));
            Assert.AreEqual(0.7f, t.Value);
            Assert.IsTrue(t.NeedsUpdate(10.5f));
            t.Reset();
            Assert.IsTrue(t.NeedsUpdate(10.1f));
        }

        [TestMethod]
        public void TheStepClassFollowsSpeedAndDepthAndTheThumpFollowsMass()
        {
            var walk = new StepEvent { Run = false, Speed = 1.5f };
            var run = new StepEvent { Run = true, Speed = 5f };
            Assert.AreEqual(0, StepShaper.Shape(walk, 0.3f, 0.094f, 0.5f, 0.5f).Class, "thin cover, walk: soft");
            Assert.AreEqual(1, StepShaper.Shape(walk, 0.8f, 0.094f, 0.5f, 0.5f).Class, "deep, walk: medium");
            Assert.AreEqual(2, StepShaper.Shape(run, 0.3f, 0.094f, 0.5f, 0.5f).Class, "run: hard");
            StepVoice deep = StepShaper.Shape(walk, 0.8f, 0.094f, 0.5f, 0.5f);
            StepVoice thin = StepShaper.Shape(walk, 0.3f, 0.094f, 0.5f, 0.5f);
            Assert.IsTrue(deep.Pitch < thin.Pitch, "deep snow is duller");
            StepVoice heavy = StepShaper.Shape(walk, 0.5f, 0.2f, 0.5f, 0.5f);
            StepVoice light = StepShaper.Shape(walk, 0.5f, 0.05f, 0.5f, 0.5f);
            Assert.IsTrue(heavy.ThumpGain > light.ThumpGain && heavy.ThumpPitch < light.ThumpPitch);
            StepVoice fast = StepShaper.Shape(new StepEvent { Run = true, Speed = 6f }, 0.5f, 0.094f, 0.5f, 0.5f);
            Assert.IsTrue(fast.Gain > thin.Gain);
            Assert.IsTrue(thin.Gain > 0f && thin.Gain < 1.2f);
        }
    }
}
