using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.C25
{
    // K-W3b2, C25: the layered window (E43) cost 19.7 ms on the main thread in the game against 5.1 ms before. The field now fills it on a worker
    // (IWindowJobField) into a double buffer. The window must be the one FillWindow makes, bit for bit, whatever thread and whenever the
    // settings change afterwards.
    [TestClass]
    public class WindowJobTests
    {
        private const int Body = FieldFixture.BodyIndex;

        [TestCleanup]
        public void Clear()
        {
            LayerWindOptions.SteeringOverride = null;
            LayerWindOptions.WindShearOverride = LayerWindOptions.VerticalMotionOverride = LayerWindOptions.AnvilTiltOverride = null;
            LayerWindOptions.Couple = true;
            AtmospheresSettings.ResetToDefaults();
        }

        private static WindowJobState WaitDone(IWindowJobField job)
        {
            var watch = Stopwatch.StartNew();
            while (job.WindowState == WindowJobState.Running && watch.ElapsedMilliseconds < 20000) Thread.Sleep(0);
            return job.WindowState;
        }

        private static int Bits(float f)
        {
            return BitConverter.ToInt32(BitConverter.GetBytes(f), 0);
        }

        // A hash over every number of a window (the bits of the floats, the header).
        internal static ulong HashOf(WeatherWindow w)
        {
            ulong h = 0xCBF29CE484222325UL;
            Action<ulong> mix = v => h = (h ^ v) * 0x100000001B3UL;
            mix((ulong)w.Nx); mix((ulong)w.Ny); mix((ulong)w.Nz); mix((ulong)w.BodyIndex);
            mix((ulong)BitConverter.DoubleToInt64Bits(w.CentreLatDeg)); mix((ulong)BitConverter.DoubleToInt64Bits(w.CentreLonDeg));
            mix((ulong)BitConverter.DoubleToInt64Bits(w.RadiusKm)); mix((ulong)BitConverter.DoubleToInt64Bits(w.Ut));
            mix((ulong)Bits(w.SpacingHorizontalM)); mix((ulong)Bits(w.SpacingVerticalM));
            mix((ulong)Bits(w.OriginAltM));
            for (int i = 0; i < w.Count; i++)
            {
                mix((uint)Bits(w.CloudWater[i]));
                mix((uint)Bits(w.Updraft[i]));
                mix((uint)Bits(w.Temperature[i]));
            }
            return h;
        }

        [TestMethod]
        public void TheWorkerWindowIsTheSynchronousWindowBitForBit()
        {
            GeneratorField field = FieldFixture.Field();
            var sync = new WeatherWindow();
            var spare = new WeatherWindow();
            foreach (double lat in new[] { -50.0, -10.0, 20.0, 55.0 })
                foreach (double lon in new[] { -120.0, 15.0, 160.0 })
                {
                    double ut = 3.0e6 + lat * 100.0 + lon;
                    Assert.IsTrue(field.FillWindow(Body, lat, lon, 30.0, ut, sync));
                    Assert.IsTrue(field.BeginWindow(Body, lat, lon, 30.0, ut));
                    Assert.AreEqual(WindowJobState.Ready, WaitDone(field));
                    Assert.AreEqual(WindowJobState.Ready, field.TakeWindow(ref spare));
                    Assert.AreEqual(HashOf(sync), HashOf(spare), "the window at " + lat + ", " + lon);
                    Assert.AreEqual(WindowJobState.Idle, field.WindowState);
                }
        }

        [TestMethod]
        public void TheSettingsOfTheAskAreTheOnesTheWorkerUses()
        {
            GeneratorField field = FieldFixture.Field();
            var layered = new WeatherWindow();
            var plain = new WeatherWindow();
            var spare = new WeatherWindow();
            Assert.IsTrue(field.FillWindow(Body, 40, 10, 30.0, 3.0e6, layered));
            LayerWindOptions.WindShearOverride = LayerWindOptions.VerticalMotionOverride = LayerWindOptions.AnvilTiltOverride = false;
            LayerWindOptions.Couple = false;
            Assert.IsTrue(field.FillWindow(Body, 40, 10, 30.0, 3.0e6, plain));
            Assert.AreNotEqual(HashOf(layered), HashOf(plain), "the two settings make two windows");

            LayerWindOptions.WindShearOverride = LayerWindOptions.VerticalMotionOverride = LayerWindOptions.AnvilTiltOverride = null;
            LayerWindOptions.Couple = true;
            Assert.IsTrue(field.BeginWindow(Body, 40, 10, 30.0, 3.0e6));
            // The switches change at once, while the worker may be running or not.
            LayerWindOptions.WindShearOverride = LayerWindOptions.VerticalMotionOverride = LayerWindOptions.AnvilTiltOverride = false;
            LayerWindOptions.Couple = false;
            Assert.AreEqual(WindowJobState.Ready, WaitDone(field));
            Assert.AreEqual(WindowJobState.Ready, field.TakeWindow(ref spare));
            Assert.AreEqual(HashOf(layered), HashOf(spare));
        }

        [TestMethod]
        public void OneFillAtATimeAndATakenWindowIsNeverWrittenAgain()
        {
            GeneratorField field = FieldFixture.Field();
            var spare = new WeatherWindow();
            Assert.AreEqual(WindowJobState.Idle, field.WindowState);
            Assert.AreEqual(WindowJobState.Idle, field.TakeWindow(ref spare), "nothing to take");
            Assert.IsTrue(field.BeginWindow(Body, 30, 20, 30.0, 3.0e6));
            Assert.IsFalse(field.BeginWindow(Body, 30, 20, 30.0, 3.0e6 + 5.0), "a fill is in the air or not taken");
            Assert.AreEqual(WindowJobState.Ready, WaitDone(field));
            Assert.IsFalse(field.BeginWindow(Body, 30, 20, 30.0, 3.0e6 + 5.0), "the result is not taken yet");
            Assert.AreEqual(WindowJobState.Ready, field.TakeWindow(ref spare));
            WeatherWindow first = spare;
            ulong hash = HashOf(first);
            for (int n = 0; n < 3; n++)
            {
                Assert.IsTrue(field.BeginWindow(Body, 31 + n, 21, 30.0, 3.0e6 + 60.0 * n));
                Assert.AreEqual(WindowJobState.Ready, WaitDone(field));
                WeatherWindow hold = first;
                WeatherWindow next = new WeatherWindow();
                Assert.AreEqual(WindowJobState.Ready, field.TakeWindow(ref next));
                Assert.AreNotSame(hold, next);
                Assert.AreEqual(hash, HashOf(first), "the window taken first is not written by the later fills");
            }
        }

        [TestMethod]
        public void AFieldWithoutCoverageStartsNothingAndAFailedFillResetsTheJob()
        {
            GeneratorField field = FieldFixture.Field();
            var spare = new WeatherWindow();
            Assert.IsFalse(field.BeginWindow(Body + 40, 30, 20, 30.0, 3.0e6), "no state for the body");
            Assert.AreEqual(WindowJobState.Idle, field.WindowState);
            Assert.IsTrue(field.BeginWindow(Body, 30, 20, double.NaN, 3.0e6));
            Assert.AreEqual(WindowJobState.Failed, WaitDone(field));
            Assert.AreEqual(WindowJobState.Failed, field.TakeWindow(ref spare));
            Assert.AreEqual(WindowJobState.Idle, field.WindowState);
        }

        // The numbers the factor to the game's runtime is read from (K-W3b2: window 19.66 ms, sample 4.31 us, map 390.6 ms in the game's
        // Release series; the window before E43 5.13 ms): medians of 31 fills of the layered and the plain window in this process.
        [TestMethod]
        public void TheWindowCostMediansAreStated()
        {
            GeneratorField field = FieldFixture.Field();
            var window = new WeatherWindow();
            field.FillWindow(Body, 50, 10, 30, 3.0e6, window);
            double layered = MedianFillMs(field, window);
            LayerWindOptions.WindShearOverride = LayerWindOptions.VerticalMotionOverride = LayerWindOptions.AnvilTiltOverride = false;
            LayerWindOptions.Couple = false;
            double plain = MedianFillMs(field, window);
            Console.WriteLine("C25 window medians of 31: layered " + layered.ToString("0.00") + " ms, plain " + plain.ToString("0.00") + " ms, ratio "
                              + (layered / plain).ToString("0.00"));
            Assert.IsTrue(layered > 0.0 && plain > 0.0);
        }

        private static double MedianFillMs(GeneratorField field, WeatherWindow window)
        {
            var ms = new double[31];
            for (int i = 0; i < ms.Length; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                field.FillWindow(Body, 50, 10 + i * 0.3, 30, 3.0e6 + i * 600.0, window);
                ms[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            }
            Array.Sort(ms);
            return ms[ms.Length / 2];
        }

        [TestMethod]
        public void TheMainThreadOnlyAsksAndTakes()
        {
            GeneratorField field = FieldFixture.Field();
            var spare = new WeatherWindow();
            var sync = new WeatherWindow();
            field.FillWindow(Body, 50, 10, 30, 3.0e6, sync);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) field.FillWindow(Body, 50, 10, 30, 3.0e6 + i * 7.0, sync);
            double syncMs = watch.Elapsed.TotalMilliseconds / 5.0;

            double worst = 0.0, total = 0.0;
            const int rounds = 8;
            for (int i = 0; i < rounds; i++)
            {
                watch.Restart();
                bool begun = field.BeginWindow(Body, 50, 10, 30, 3.0e6 + i * 7.0);
                double askMs = watch.Elapsed.TotalMilliseconds;
                Assert.IsTrue(begun);
                WaitDone(field);
                watch.Restart();
                field.TakeWindow(ref spare);
                double takeMs = watch.Elapsed.TotalMilliseconds;
                worst = Math.Max(worst, askMs + takeMs);
                total += askMs + takeMs;
            }
            Console.WriteLine("C25 window on the main thread: ask + take " + (total / rounds).ToString("0.000") + " ms mean, " + worst.ToString("0.000")
                              + " ms worst; the synchronous fill " + syncMs.ToString("0.0") + " ms");
            Assert.IsTrue(worst < Math.Max(2.0, 0.5 * syncMs), "ask + take " + worst + " ms against the fill " + syncMs + " ms");
        }
    }
}
