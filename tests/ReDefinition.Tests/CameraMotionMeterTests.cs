using System.Globalization;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Upscaler;
using UnityEngine;

namespace ReDefinition.Tests
{
    // The counting of the rig's camera instrument (CameraMotionMeter): turns, moves, cuts and floating origin
    // shifts of sampled frames, and the log line they make, without the game's camera.
    [TestClass]
    public class CameraMotionMeterTests
    {
        // A turn of 90 degrees about the vertical axis: the quaternion of half the angle, sin 45 and cos 45.
        private static readonly Quaternion QuarterTurn = new Quaternion(0f, 0.70710678f, 0f, 0.70710678f);

        private CultureInfo culture;

        [TestInitialize]
        public void UseInvariantCulture()
        {
            culture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        }

        [TestCleanup]
        public void RestoreCulture()
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }

        // The first sample has nothing to compare with: it counts no frame, and a window with no frame reports nothing.
        [TestMethod]
        public void TheFirstSampleCountsNothing()
        {
            CameraMotionMeter meter = new CameraMotionMeter();
            float turn;
            bool shifted;

            bool followed = meter.Accumulate(Quaternion.identity, Vector3.zero, Vector3.zero, null, 0.016f, false, 1,
                                             out turn, out shifted);

            Assert.IsFalse(followed);
            Assert.IsFalse(shifted);
            Assert.IsNull(meter.Describe(10f));
        }

        // An ordinary second frame is a turn of 90 degrees and a move of 3 m against the target, each with its frame time.
        [TestMethod]
        public void AnOrdinaryFrameRecordsTheLargestTurnAndMove()
        {
            CameraMotionMeter meter = new CameraMotionMeter();
            float turn;
            bool shifted;
            meter.Accumulate(Quaternion.identity, Vector3.zero, Vector3.zero, null, 0.016f, false, 1, out turn, out shifted);

            bool followed = meter.Accumulate(QuarterTurn, new Vector3(3f, 0f, 0f), new Vector3(3f, 0f, 0f), null, 0.016f,
                                             false, 2, out turn, out shifted);

            Assert.IsTrue(followed);
            Assert.AreEqual(90f, turn, 1e-3f);
            Assert.AreEqual("[ReDefinition] Camera and origin, last 10.0 s, 1 frames. Ordinary frames: largest turn 90.00 deg"
                            + " in a 16 ms frame, largest move against the target 3.00 m in a 16 ms frame; no detected cut."
                            + " Floating origin: 0 shift(s) in 0 of 1 frames.",
                            meter.Describe(10f));
        }

        // At a cut the turn and the jump in world space are counted, and the ordinary maxima stay at zero.
        [TestMethod]
        public void ACutRecordsTheTurnAndTheJump()
        {
            CameraMotionMeter meter = new CameraMotionMeter();
            float turn;
            bool shifted;
            meter.Accumulate(Quaternion.identity, Vector3.zero, Vector3.zero, null, 0.016f, false, 1, out turn, out shifted);

            bool followed = meter.Accumulate(QuarterTurn, new Vector3(3f, 4f, 0f), new Vector3(3f, 4f, 0f), null, 0.016f,
                                             true, 2, out turn, out shifted);

            Assert.IsFalse(followed);
            Assert.AreEqual("[ReDefinition] Camera and origin, last 10.0 s, 1 frames. Ordinary frames: largest turn 0.00 deg"
                            + " in a 0 ms frame, largest move against the target 0.00 m in a 0 ms frame; 1 detected cut(s),"
                            + " up to 90.00 deg and a 5.0 m jump. Floating origin: 0 shift(s) in 0 of 1 frames.",
                            meter.Describe(10f));
        }

        // A floating origin shift between two samples marks the second one: it counts as a frame with a shift, keeps the
        // largest offset and Krakensbane step, and its frame number is listed while not every frame had one.
        [TestMethod]
        public void AShiftIsCountedInTheFrameThatFollowsIt()
        {
            CameraMotionMeter meter = new CameraMotionMeter();
            float turn;
            bool shifted;
            meter.Accumulate(Quaternion.identity, Vector3.zero, Vector3.zero, null, 0.016f, false, 41, out turn, out shifted);
            meter.OnFloatingOriginShift(new Vector3d(3.0, 4.0, 0.0), new Vector3d(0.0, 0.0, 12.0));

            meter.Accumulate(Quaternion.identity, Vector3.zero, Vector3.zero, null, 0.016f, false, 42, out turn, out shifted);
            Assert.IsTrue(shifted);
            meter.Accumulate(Quaternion.identity, Vector3.zero, Vector3.zero, null, 0.016f, false, 43, out turn, out shifted);
            Assert.IsFalse(shifted);

            StringAssert.Contains(meter.Describe(10f), "Floating origin: 1 shift(s) in 1 of 2 frames, largest offset 5.0 m,"
                                                       + " largest Krakensbane step 12.0 m, in frames 42.");
        }

        // A fast turn asks for the proxy's check, and not again within five seconds of the last request.
        [TestMethod]
        public void AFastTurnAsksForTheCheckOnceInFiveSeconds()
        {
            CameraMotionMeter meter = new CameraMotionMeter();

            Assert.IsFalse(meter.TurnCheckDue(1.5f, 0f));
            Assert.IsTrue(meter.TurnCheckDue(2.5f, 10f));
            Assert.IsFalse(meter.TurnCheckDue(5f, 14f));
            Assert.IsTrue(meter.TurnCheckDue(5f, 15f));
        }

        // Update finds no line to log where no fast turn wrote one.
        [TestMethod]
        public void NoTurnLineIsTakenWhereNoneWasWritten()
        {
            CameraMotionMeter meter = new CameraMotionMeter();

            Assert.IsNull(meter.TakeTurnLine());
        }
    }
}
