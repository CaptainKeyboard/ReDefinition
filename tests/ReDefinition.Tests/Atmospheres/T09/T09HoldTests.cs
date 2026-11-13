using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The hold against what start 44 showed the game does: the centre of mass sits 9.35 m under the transform SetPosition moves (each placement attempt then sank by that
    // much: the stale read of the old hold was inside its 60 m radius), the vessel is released with gravity on, the surface turns under it (175 m/s, 3.5 m a physics step)
    // and the game hitches for 0.4 to 1 s. The fake world models each (FakeWorld.RealisticHold, ComOffsetM, GravityMS2, ResidualEastMS, HitchEveryS).
    [TestClass]
    public class T09HoldTests
    {
        private const double Target = 264.8, Frame = 1.0 / 60.0;

        private static readonly List<string> Lines = new List<string>();

        private static FakeWorld NewWorld(Action<FakeWorld> tweak = null)
        {
            Lines.Clear();
            AtmospheresLog.SayLine = line => Lines.Add(line);
            var w = new FakeWorld { RealisticHold = true, FlightReadyAtS = 0.0, TrueAltitude = 70.0 };
            if (tweak != null) tweak(w);
            return w;
        }

        // Places the vessel the way the probe does (PlaceVessel, StartHold) and runs the frames; returns the worst distance of the true centre of mass from the target over
        // the frames from the moment the hold reports itself stable.
        private static double PlaceAndRun(FakeWorld w, int frames, out int stableAt, out double finalM)
        {
            return PlaceAndRun(w, frames, out stableAt, Frame, out finalM);
        }

        private static double PlaceAndRun(FakeWorld w, int frames, out int stableAt, double frameSeconds, out double finalM)
        {
            w.PlaceVessel(w.SiteLat, w.SiteLon, Target);
            w.StartHold();
            double worst = 0.0;
            stableAt = -1;
            for (int i = 0; i < frames; i++)
            {
                w.Advance(frameSeconds);
                if (stableAt < 0 && w.HoldStable) stableAt = i;
                if (stableAt >= 0) worst = Math.Max(worst, w.TrueDistanceM(Target));
            }
            finalM = w.TrueDistanceM(Target);
            return worst;
        }

        [TestMethod]
        public void FourPlacementsInARowEachLandTheCentreOfMassOnTheTargetAndNeverSink()
        {
            var log = new List<string>();
            FakeWorld w = NewWorld();
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int stableAt;
                double finalM;
                double worst = PlaceAndRun(w, 150, out stableAt, out finalM);
                Assert.IsTrue(w.HoldReached && w.HoldStable, "attempt " + attempt + ": the hold is stable (" + w.HoldStatus + ")");
                Assert.IsTrue(stableAt >= 0 && stableAt < 120, "attempt " + attempt + ": stable after " + stableAt + " frames");
                Assert.IsTrue(finalM <= 5.0, "attempt " + attempt + ": the centre of mass is " + finalM.ToString("0.0") + " m from the target (altitude " + w.TrueAltitude.ToString("0.0") + ")");
                Assert.IsTrue(worst <= 6.0, "attempt " + attempt + ": worst distance once stable " + worst.ToString("0.0") + " m");
                Assert.AreEqual(Target, w.TrueAltitude, 5.0, "attempt " + attempt + ": no sinking");
            }
        }

        [TestMethod]
        public void AHitchOfAboutASecondDoesNotCarryTheVesselAway()
        {
            FakeWorld w = NewWorld(x => { x.HitchEveryS = 2.0; x.HitchS = 1.0; });
            int stableAt;
            double finalM;
            double worst = PlaceAndRun(w, 60 * 12, out stableAt, out finalM);
            Assert.IsTrue(stableAt >= 0, "the hold became stable");
            Assert.IsTrue(w.PhysicsSteps > 12 * 50, "hitches ran their physics steps: " + w.PhysicsSteps);
            Assert.IsTrue(worst <= 8.0, "worst distance over a dwell with hitches: " + worst.ToString("0.0") + " m (start 44: 209 to 541 m)");
        }

        [TestMethod]
        public void TheAnchorIsNeverAcceptedFromTheOldPositionThatHappensToBeNear()
        {
            // The vessel stands 10 m under the target (the previous attempt's end), well inside the old 60 m radius: the old read must not become the anchor.
            FakeWorld w = NewWorld(x => x.TrueAltitude = Target - 10.0);
            var log = new List<string>();
            int stableAt;
            double finalM;
            PlaceAndRun(w, 150, out stableAt, out finalM);
            Assert.IsTrue(finalM <= 5.0, "ends on the target: " + finalM.ToString("0.0") + " m");
            Assert.IsTrue(w.HoldReached);
        }

        [TestMethod]
        public void AVesselThatNeverMovesIsReportedUnreachedAndTheHoldKeepsTrying()
        {
            FakeWorld w = NewWorld(x => x.PlaceResult = (call, wanted) => 85.35);
            int stableAt;
            double finalM;
            PlaceAndRun(w, 400, out stableAt, out finalM);
            Assert.IsFalse(w.HoldReached, "not reached: " + w.HoldStatus);
            Assert.IsFalse(w.HoldStable);
            Assert.IsTrue(w.HoldStatus.Contains("not reached"), w.HoldStatus);
        }

        [TestMethod]
        public void SlowFramesWithTwoPhysicsStepsEachKeepTheVesselOnTheTarget()
        {
            // K-T2 start 46: the windows with the own pass ran at 45 to 52 fps, a frame about as long as a physics step (20 ms) or two of them.
            foreach (double frame in new[] { 0.0222, 0.03, 0.04 })
            {
                FakeWorld w = NewWorld(x => { x.ReadLagFrames = 1; x.PlaceDelayFrames = 1; });
                int stableAt;
                double finalM;
                double worst = PlaceAndRun(w, 600, out stableAt, frame, out finalM);
                Assert.IsTrue(stableAt >= 0, "frame " + frame + ": stable");
                Assert.IsTrue(worst <= 6.0 && finalM <= 6.0, "frame " + frame + ": worst " + worst.ToString("0.0") + " m, final " + finalM.ToString("0.0") + " m");
            }
        }

        [TestMethod]
        public void AReadThatStopsFollowingTheVesselDoesNotWindTheCommandUpOffTheTarget()
        {
            // K-T2 start 46, above, plain: the read stood within 2 m of the point for 470 frames while corrections moved the command to 278 m off the target and the vessel
            // went with it. The fake freezes the read 12 m off for 400 frames: the hold may try, then must not go on adding to the command beyond its limit (plus the centre of mass's 9 m).
            FakeWorld w = NewWorld(x => { x.ReadLagFrames = 1; x.PlaceDelayFrames = 1; x.ReadFreezeAtFrame = 150; x.ReadFreezeFrames = 400; });
            w.PlaceVessel(w.SiteLat, w.SiteLon, Target);
            w.StartHold();
            double worst = 0.0;
            for (int i = 0; i < 700; i++)
            {
                w.Advance(0.0222);
                if (i > 150) worst = Math.Max(worst, w.TrueDistanceM(Target));
            }
            Assert.IsTrue(worst <= T09Hold.CommandLimitM + 15.0, "the vessel left the target by " + worst.ToString("0.0") + " m while the read stood still; corrections " + w.HoldCorrections);
            Assert.IsTrue(w.TrueDistanceM(Target) <= 6.0, "and is back on it after the read follows again: " + w.TrueDistanceM(Target).ToString("0.0") + " m");
        }

        [TestMethod]
        public void TheHoldWithoutItsFixedStepCommandLosesTheVesselInAHitch()
        {
            // The model check: with the per-step command switched off the same hitch carries the vessel off by the surface's turning (175 m/s over the hitch).
            FakeWorld w = NewWorld(x => { x.HitchEveryS = 2.0; x.HitchS = 1.0; });
            w.PlaceVessel(w.SiteLat, w.SiteLon, Target);
            w.StartHold();
            for (int i = 0; i < 200; i++) w.Advance(Frame);
            double before = w.TrueDistanceM(Target);
            Assert.IsTrue(before <= 6.0, "settled before the hitch: " + before.ToString("0.0"));
            w.Held = false; // the per-frame and per-step ticks are off
            double worst = 0.0;
            for (int i = 0; i < 180; i++)
            {
                w.Advance(Frame);
                worst = Math.Max(worst, w.TrueDistanceM(Target));
            }
            Assert.IsTrue(worst > 100.0, "an unheld vessel is carried away: " + worst.ToString("0") + " m");
        }
    }
}
