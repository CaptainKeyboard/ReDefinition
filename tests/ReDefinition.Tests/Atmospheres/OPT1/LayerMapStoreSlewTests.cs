using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.OPT1
{
    // The store's GpuSlew mode with its worker thread and the hand-over of goals, without textures: the uploads go to a hook that records them.
    [TestClass]
    public class LayerMapStoreSlewTests
    {
        private const int W = 64, H = 32, N = W * H;

        private static byte[] Map(int seed)
        {
            var rng = new Random(seed);
            var m = new byte[N];
            rng.NextBytes(m);
            return m;
        }

        private sealed class Rig : IDisposable
        {
            internal readonly LayerMapStore Store;
            internal readonly Dictionary<int, byte[]> Uploaded = new Dictionary<int, byte[]>();
            internal int Uploads;

            internal Rig()
            {
                Store = LayerMapStore.ForTest(W, H, (slot, pairs) =>
                {
                    Uploaded[slot] = (byte[])pairs.Clone();
                    Uploads++;
                });
            }

            // Steps the clock in small frames until nothing is pending; fails after a timeout.
            internal void Settle(float dt, float maxSeconds = 5f)
            {
                var watch = Stopwatch.StartNew();
                float simulated = 0f;
                while (Store.Step(dt, MapSlew.DefaultRate, false) > 0 || simulated < LayerMapStore.SlewLeadS + 2f * dt)
                {
                    simulated += dt;
                    Assert.IsTrue(watch.Elapsed.TotalSeconds < 10.0, "the store did not settle");
                    Thread.Sleep(1);
                    if (simulated > maxSeconds) break;
                }
            }

            public void Dispose()
            {
                Store.Dispose();
            }
        }

        [TestMethod]
        public void TheFirstGoalOfAMapJumpsAndIsUploadedAsEqualPairs()
        {
            using (var rig = new Rig())
            {
                byte[] g = Map(1);
                rig.Store.SetTargetBytes(0, g, null);
                rig.Settle(0.01f);
                byte[] pairs = rig.Uploaded[0];
                for (int i = 0; i < N; i++)
                {
                    Assert.AreEqual(g[i], pairs[2 * i]);
                    Assert.AreEqual(g[i], pairs[2 * i + 1]);
                }
                Assert.AreEqual(0f, rig.Store.ReachVector(0)[0], 0.05f);
            }
        }

        [TestMethod]
        public void TheShownValueIsContinuousThroughHandOversAndNeverFasterThanTheRate()
        {
            using (var rig = new Rig())
            {
                const float dt = 0.01f;
                rig.Store.SetTargetBytes(1, Map(1), Map(2));
                rig.Settle(dt);
                byte[] previous = rig.Store.CoverageBytes(1);
                double allowed = MapSlew.DefaultRate * dt * 255.0 * 2.0 + 1.5;
                double worst = 0.0;
                // A new goal every second (the game's rhythm) for six goals, the frames of 10 ms between.
                for (int goal = 0; goal < 6; goal++)
                {
                    rig.Store.SetTargetBytes(1, Map(10 + goal), Map(20 + goal));
                    for (int frame = 0; frame < 100; frame++)
                    {
                        rig.Store.Step(dt, MapSlew.DefaultRate, false);
                        Thread.Sleep(1);
                        byte[] now = rig.Store.CoverageBytes(1);
                        for (int i = 0; i < N; i++) worst = Math.Max(worst, Math.Abs(now[i] - previous[i]));
                        previous = now;
                    }
                }
                Assert.IsTrue(worst <= allowed, "a texel moved " + worst + " levels in one frame, allowed " + allowed);
            }
        }

        [TestMethod]
        public void TheShownValueReachesTheGoalAndTheCpuReadsAgreeWithTheMapFormula()
        {
            using (var rig = new Rig())
            {
                byte[] first = Map(3), second = Map(4);
                rig.Store.SetTargetBytes(2, first, first);
                rig.Settle(0.02f);
                rig.Store.SetTargetBytes(2, second, second);
                // Halfway: the analytic value at a place lies between the bilinear reads of the two maps and moves at most rate * age from the first.
                for (int f = 0; f < 60; f++)
                {
                    rig.Store.Step(0.02f, MapSlew.DefaultRate, false);
                    Thread.Sleep(2);
                }
                float cov, type, goalCov, goalType;
                Assert.IsTrue(rig.Store.SampleBytes(2, 10.0, 20.0, false, out cov, out type));
                Assert.IsTrue(rig.Store.SampleBytes(2, 10.0, 20.0, true, out goalCov, out goalType));
                byte[] a, b;
                Assert.IsTrue(rig.Store.CoverageBoundPair(2, out a, out b));
                float lo = Math.Min(Bilinear(a, 10.0, 20.0), Bilinear(b, 10.0, 20.0)), hi = Math.Max(Bilinear(a, 10.0, 20.0), Bilinear(b, 10.0, 20.0));
                Assert.IsTrue(cov >= lo - 1e-5f && cov <= hi + 1e-5f, "the shown value " + cov + " lies outside " + lo + " to " + hi);
                Assert.AreEqual(Bilinear(b, 10.0, 20.0), goalCov, 1e-5f);
                // Long after: the goal.
                for (int f = 0; f < 700; f++) rig.Store.Step(0.02f, MapSlew.DefaultRate, false);
                Assert.IsTrue(rig.Store.SampleBytes(2, 10.0, 20.0, false, out cov, out type));
                Assert.AreEqual(goalCov, cov, 1e-5f);
                CollectionAssertEqual(rig.Store.CoverageBytes(2), second);
            }
        }

        [TestMethod]
        public void ASnapStepShowsEveryGoalAtOnce()
        {
            using (var rig = new Rig())
            {
                rig.Store.SetTargetBytes(0, Map(5), Map(6));
                rig.Store.Step(0f, MapSlew.DefaultRate, true);
                rig.Store.SetTargetBytes(0, Map(7), Map(8));
                rig.Store.Step(0f, MapSlew.DefaultRate, true);
                CollectionAssertEqual(rig.Store.CoverageBytes(0), Map(7));
                CollectionAssertEqual(rig.Store.TypeBytes(0), Map(8));
                Assert.AreEqual(0f, rig.Store.ReachVector(0)[0], 1e-6f);
            }
        }

        [TestMethod]
        public void GoalsGivenInQuickSuccessionApplyInOrderWithoutAStall()
        {
            using (var rig = new Rig())
            {
                for (int g = 0; g < 12; g++) rig.Store.SetTargetBytes(3, Map(30 + g), Map(50 + g));
                rig.Settle(0.05f, 60f);
                for (int f = 0; f < 800; f++) rig.Store.Step(0.05f, MapSlew.DefaultRate, false);
                CollectionAssertEqual(rig.Store.CoverageGoalBytes(3), Map(41));
                CollectionAssertEqual(rig.Store.TypeGoalBytes(3), Map(61));
                Assert.AreEqual(0, rig.Store.SlewPending);
            }
        }

        private static float Bilinear(byte[] bytes, double latDeg, double lonDeg)
        {
            double x = (lonDeg + 180.0) / 360.0 * W - 0.5, y = (90.0 - latDeg) / 180.0 * H - 0.5;
            y = Math.Max(0.0, Math.Min(H - 1, y));
            double x0 = Math.Floor(x), fx = x - x0;
            int ix0 = ((int)x0 % W + W) % W, ix1 = (ix0 + 1) % W, iy0 = (int)Math.Floor(y), iy1 = Math.Min(iy0 + 1, H - 1);
            double fy = y - iy0;
            double top = bytes[iy0 * W + ix0] * (1 - fx) + bytes[iy0 * W + ix1] * fx, bottom = bytes[iy1 * W + ix0] * (1 - fx) + bytes[iy1 * W + ix1] * fx;
            return (float)((top * (1 - fy) + bottom * fy) / 255.0);
        }

        private static void CollectionAssertEqual(byte[] a, byte[] b)
        {
            Assert.AreEqual(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) Assert.Fail("texel " + i + ": " + a[i] + " against " + b[i]);
        }
    }
}
