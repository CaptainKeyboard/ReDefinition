using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.OPT1
{
    // The slew of the layer maps in continuous time (MapSlewField, the CPU twin of MapSlew.cginc) against the old per-frame step (MapSlew.StepMap): the same rule, the same values
    // within one 8-bit level, and no jump where a new goal takes over.
    [TestClass]
    public class MapSlewFieldTests
    {
        private static byte[] RandomMap(Random rng, int n)
        {
            var map = new byte[n];
            rng.NextBytes(map);
            return map;
        }

        // The seconds one step of the old path moves a texel by exactly two levels: the old path's rounding of a step to whole levels is then no difference at all.
        private static float TwoLevelStepS(float rate)
        {
            return 2f / (rate * 255f);
        }

        [TestMethod]
        public void TheAnalyticSlewEqualsTheOldStepWithinOneLevelOverASequenceOfGoals()
        {
            const int n = 4096;
            float rate = MapSlew.DefaultRate, dt = TwoLevelStepS(rate);
            var rng = new Random(11);
            byte[] now = RandomMap(rng, n);
            var from = (byte[])now.Clone();
            byte[] to = RandomMap(rng, n);
            var goal = (byte[])to.Clone();
            double epoch = 0.0, clock = 0.0;
            int worst = 0;
            // Each goal lasts ten steps (about a second), the rhythm of the maps in the game.
            for (int g = 0; g < 8; g++)
            {
                for (int step = 1; step <= 10; step++)
                {
                    MapSlew.StepMap(now, goal, rate, dt);
                    clock += dt;
                    float reach = MapSlewField.Reach01(rate, clock - epoch);
                    for (int i = 0; i < n; i++)
                        worst = Math.Max(worst, Math.Abs(now[i] - MapSlewField.ValueByte(from[i], to[i], reach)));
                }
                // The next goal arrives: the "from" of the pair is the shown value of the moment (what the maps' worker makes), the old path simply goes on.
                var shown = new byte[n];
                MapSlewField.Handover(from, to, MapSlewField.Reach01(rate, clock - epoch), shown);
                from = shown;
                to = RandomMap(rng, n);
                goal = (byte[])to.Clone();
                epoch = clock;
            }
            Assert.IsTrue(worst <= 1, "the analytic value differs from the old step by " + worst + " levels");
        }

        [TestMethod]
        public void TheHandOverKeepsTheShownValue()
        {
            const int n = 4096;
            float rate = MapSlew.DefaultRate;
            var rng = new Random(5);
            byte[] from = RandomMap(rng, n), to = RandomMap(rng, n), shown = new byte[n];
            foreach (double age in new[] { 0.0, 0.3, 2.5, 7.0, 40.0 })
            {
                float reach = MapSlewField.Reach01(rate, age);
                MapSlewField.Handover(from, to, reach, shown);
                for (int i = 0; i < n; i++)
                {
                    // The pair after the hand-over shows `shown` at its age 0, whatever the goal is.
                    Assert.AreEqual(shown[i], MapSlewField.ValueByte(shown[i], (byte)(255 - shown[i]), 0f));
                    Assert.AreEqual(MapSlewField.ValueByte(from[i], to[i], reach), shown[i], "texel " + i + " at age " + age);
                    // And it is the shader's value, within the rounding to a level.
                    float exact = MapSlewField.Value(from[i], to[i], reach) * 255f;
                    Assert.IsTrue(Math.Abs(exact - shown[i]) <= 0.51f, "texel " + i + ": " + exact + " against " + shown[i]);
                }
            }
        }

        [TestMethod]
        public void NoTexelMovesFasterThanTheRateWhateverTheFrames()
        {
            const int n = 4096;
            float rate = MapSlew.DefaultRate;
            var rng = new Random(23);
            byte[] from = RandomMap(rng, n), to = RandomMap(rng, n);
            // Frames of any length (a stall of 0.6 s, 240 fps, 30 fps): the change of the continuous value between two frames never exceeds rate * dt.
            double t = 0.0;
            var frames = new[] { 0.004, 0.033, 0.6, 0.016, 0.0001, 0.2 };
            for (int f = 0; f < 600; f++)
            {
                double dt = frames[f % frames.Length];
                float a = MapSlewField.Reach01(rate, t), b = MapSlewField.Reach01(rate, t + dt);
                for (int i = 0; i < n; i += 37)
                {
                    float va = MapSlewField.Value(from[i], to[i], a), vb = MapSlewField.Value(from[i], to[i], b);
                    Assert.IsTrue(Math.Abs(vb - va) <= rate * dt + 1e-6, "texel " + i + " moved " + Math.Abs(vb - va) + " in " + dt + " s");
                }
                t += dt;
            }
        }

        [TestMethod]
        public void AMapThatIsNotSlewingShowsItsValueWhateverTheReach()
        {
            for (int v = 0; v < 256; v += 5)
                foreach (float reach in new[] { 0f, 0.3f, 1f })
                    Assert.AreEqual(v / 255f, MapSlewField.Value((byte)v, (byte)v, reach), 1e-6f);
        }

        [TestMethod]
        public void TheOldPathAtTheGamesFrameRateMovesFasterThanTheRateAndTheNewOneDoesNot()
        {
            // The old path rounds a step to whole levels but at least one level per frame: at 30 fps (0.68 levels of rate) it moves 1 level a frame, 30 levels a second against
            // the 20.4 the rate allows; at 240 fps it would move 240 levels a second. The continuous value is the rate exactly. This is the difference the report names.
            float rate = MapSlew.DefaultRate;
            byte[] now = { 0 }, goal = { 255 };
            for (int f = 0; f < 30; f++) MapSlew.StepMap(now, goal, rate, 1f / 30f);
            Assert.AreEqual(30, now[0], "old path after one second at 30 fps");
            Assert.AreEqual(rate * 255f, MapSlewField.Reach01(rate, 1.0) * 255f, 1e-3f, "the rate in levels a second");
        }
    }
}
