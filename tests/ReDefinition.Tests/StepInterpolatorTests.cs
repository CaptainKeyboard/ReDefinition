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
        private const float FixedDelta = 0.02f;
        private const float FrameDelta = 1f / 59f;
        private static readonly Vector3 Velocity = new Vector3(100f, 0f, 0f);

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
