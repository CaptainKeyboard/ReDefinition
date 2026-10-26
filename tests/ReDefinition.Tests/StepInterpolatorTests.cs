using System;
using ReDefinition.Upscaler;
using UnityEngine;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReDefinition.Tests
{
    // RenderInterpolation's arithmetic against Unity's loop as KSP runs it: physics steps
    // of 0.02 s, frames of 1/59 s, a body in steady motion.
    [TestClass]
    public class StepInterpolatorTests
    {
        // Unity's loop as KSP runs it: the physics step and a frame at 59 fps, in seconds, and the body's
        // steady velocity in metres per second.
        private const float FixedDelta = 0.02f;
        private const float FrameDelta = 1f / 59f;
        private static readonly Vector3 Velocity = new Vector3(100f, 0f, 0f);

        // With the interpolator's offset a body in steady motion moves by the same distance every frame, where
        // the raw physics positions stand still in about one frame in six.
        [TestMethod]
        public void EveryFrameMovesOnWherePhysicsStandsStill()
        {
            StepInterpolator interpolator = new StepInterpolator();
            float time = 0f, fixedTime = 0f;
            Vector3 body = Vector3.zero;
            Vector3? lastDrawn = null, lastRaw = null;
            int stillRaw = 0, frames = 0;
            double worst = 0;
            for (int frame = 0; frame < 600; frame++)
            {
                time += FrameDelta;
                // Unity runs the steps whose start lies before the frame's time; each
                // moves the state from fixedTime to fixedTime + FixedDelta.
                while (fixedTime + FixedDelta <= time)
                {
                    fixedTime += FixedDelta;
                    body = Velocity * (fixedTime + FixedDelta);
                }
                Vector3 drawn = body + interpolator.Advance(body, time, fixedTime, FixedDelta);
                if (lastRaw.HasValue && body == lastRaw.Value) stillRaw++;
                if (lastDrawn.HasValue && frame > 2)
                {
                    float moved = (drawn - lastDrawn.Value).x;
                    worst = Math.Max(worst, Math.Abs(moved - Velocity.x * FrameDelta));
                    frames++;
                }
                lastDrawn = drawn;
                lastRaw = body;
            }
            // Without it one frame in six stands still, as measured in the game.
            Assert.IsTrue(stillRaw >= 80 && stillRaw <= 120, "frames without a step: " + stillRaw);
            // With it every frame moves by the same distance, to a thousandth of a metre.
            Assert.IsTrue(worst < 1e-3, "largest deviation " + worst + " m");
            Assert.IsTrue(frames > 500);
        }

        // A rebase by the shift the positions are given in keeps the offset an interpolator would give without
        // it: a floating origin shift must not make the vessel jump.
        [TestMethod]
        public void ARebaseKeepsTheOffsets()
        {
            StepInterpolator plain = new StepInterpolator(), rebased = new StepInterpolator();
            Vector3 shift = new Vector3(1000f, 0f, 0f);
            plain.Advance(Vector3.zero, 0.02f, 0.02f, FixedDelta);
            rebased.Advance(Vector3.zero, 0.02f, 0.02f, FixedDelta);
            plain.Advance(new Vector3(2f, 0f, 0f), 0.05f, 0.04f, FixedDelta);
            rebased.Advance(new Vector3(2f, 0f, 0f), 0.05f, 0.04f, FixedDelta);
            rebased.Rebase(shift);
            Vector3 a = plain.Advance(new Vector3(4f, 0f, 0f), 0.07f, 0.06f, FixedDelta);
            Vector3 b = rebased.Advance(new Vector3(4f, 0f, 0f) - shift, 0.07f, 0.06f, FixedDelta);
            Assert.AreEqual(a.x, b.x, 1e-3f);
        }

        // A stage dropped between two frames goes on from the vessel's steps. There it is drawn
        // where the vessel is, while a fresh history would draw it ahead by the part of a step
        // not yet drawn, half the step's 2 m here.
        [TestMethod]
        public void ADroppedStageGoesOnFromTheVesselsSteps()
        {
            StepInterpolator vessel = new StepInterpolator();
            vessel.Advance(Vector3.zero, 0.02f, 0.02f, FixedDelta);
            vessel.Advance(new Vector3(2f, 0f, 0f), 0.05f, 0.04f, FixedDelta);
            StepInterpolator stage = new StepInterpolator(), fresh = new StepInterpolator();
            stage.CopyFrom(vessel);
            Vector3 now = new Vector3(4f, 0f, 0f);
            Vector3 a = vessel.Advance(now, 0.07f, 0.06f, FixedDelta);
            Vector3 b = stage.Advance(now, 0.07f, 0.06f, FixedDelta);
            Vector3 c = fresh.Advance(now, 0.07f, 0.06f, FixedDelta);
            Assert.AreEqual(a.x, b.x, 1e-5f);
            Assert.AreEqual(1, stage.Steps);
            Assert.AreEqual(0f, c.x, 1e-5f);
            Assert.AreEqual(-1f, a.x, 1e-4f);
        }

        // Two steps run in one frame: the offset draws the body where the second step began, the first step's
        // 2 m back, and the interpolator counts both.
        [TestMethod]
        public void TwoStepsInOneFrameAreSpreadOverTheFrame()
        {
            StepInterpolator interpolator = new StepInterpolator();
            interpolator.Advance(Vector3.zero, 0.02f, 0.02f, FixedDelta);
            Vector3 offset = interpolator.Advance(new Vector3(4f, 0f, 0f), 0.06f, 0.06f, FixedDelta);
            Assert.AreEqual(2, interpolator.Steps);
            // At the start of a step: drawn where the step before left it, 2 m back.
            Assert.AreEqual(-2f, offset.x, 1e-4f);
        }
    }
}
