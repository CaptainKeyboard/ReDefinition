using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.B11
{
    // The cost counters: windows of one second, the line every 10 seconds, the allocation per frame from the heap's deltas, and the
    // memory from the known texture sizes where there is no live reading. Clock and heap are stepped by hand.
    [TestClass]
    public class PerfCountersTests
    {
        private long clockTicks, heap;
        private readonly List<string> lines = new List<string>();

        // 1000 ticks a second, so a tick is a millisecond.
        private PerfCounters Make(MemorySampler memory = null)
        {
            return new PerfCounters(lines.Add, memory, () => heap, () => clockTicks, 1000.0);
        }

        // A frame at time `now` in which the system `volume` took `ms` and the heap grew by `grow` bytes.
        private void Frame(PerfCounters c, double now, int ms, long grow)
        {
            c.Begin("volume");
            clockTicks += ms;
            c.End("volume");
            heap += grow;
            c.EndFrame(now);
        }

        [TestMethod]
        public void Windows_close_after_one_second_with_the_mean_and_worst_time_per_frame()
        {
            PerfCounters c = Make();
            c.EndFrame(0.0);
            for (int i = 1; i <= 10; i++) Frame(c, i * 0.1, i == 5 ? 9 : 1, 100);
            Assert.AreEqual(1, c.WindowsClosed);
            PerfWindow w = c.Last;
            Assert.AreEqual(10, w.Frames);
            Assert.AreEqual((9 + 9 * 1) / 10.0, w.MeanMs["volume"], 1e-9);
            Assert.AreEqual(9.0, w.MaxMs["volume"], 1e-9);
            Assert.AreEqual(1.0, w.Seconds, 1e-9);
        }

        [TestMethod]
        public void A_system_that_runs_twice_in_a_frame_adds_up()
        {
            PerfCounters c = Make();
            c.EndFrame(0.0);
            for (int i = 1; i <= 10; i++)
            {
                c.Begin("precip");
                clockTicks += 2;
                c.End("precip");
                using (c.Measure("precip")) clockTicks += 3;
                c.EndFrame(i * 0.1);
            }
            Assert.AreEqual(5.0, c.Last.MeanMs["precip"], 1e-9);
        }

        [TestMethod]
        public void Allocation_per_frame_is_the_heap_delta_and_a_collection_is_not_a_negative_allocation()
        {
            PerfCounters c = Make();
            heap = 1000000;
            c.EndFrame(0.0);
            for (int i = 1; i <= 10; i++)
            {
                if (i == 4) heap -= 500000;
                Frame(c, i * 0.1, 1, i == 4 ? 0 : 2000);
            }
            Assert.AreEqual(1, c.Last.Collections);
            Assert.AreEqual(9 * 2000 / 10.0, c.Last.AllocBytesPerFrame, 1e-9);
        }

        [TestMethod]
        public void A_perf_line_is_written_every_ten_seconds_and_names_each_system()
        {
            PerfCounters c = Make();
            c.EndFrame(0.0);
            for (int i = 1; i <= 100; i++) Frame(c, i * 0.1 - 0.0001 * 0, 2, 1000);
            Assert.AreEqual(1, lines.Count, string.Join("|", lines));
            StringAssert.StartsWith(lines[0], "perf:");
            StringAssert.Contains(lines[0], "volume=2ms(max 2)");
            StringAssert.Contains(lines[0], "alloc=1000B/frame");
            StringAssert.Contains(lines[0], "gc=0");
            for (int i = 101; i <= 150; i++) Frame(c, i * 0.1, 2, 1000);
            Assert.AreEqual(1, lines.Count);
        }

        [TestMethod]
        public void Memory_falls_back_to_the_known_texture_sizes_when_there_is_no_live_reading()
        {
            var failing = new MemorySampler(() => { throw new InvalidOperationException("no profiler"); });
            failing.Register("volume target", 1024, 512, 8);
            failing.Register("map", 256, 128, 4, 1, true);
            bool estimated;
            long bytes = failing.Sample(out estimated);
            long map = 256L * 128 * 4;
            Assert.IsTrue(estimated);
            Assert.AreEqual(1024L * 512 * 8 + map + map / 3, bytes);
            failing.Unregister("map");
            Assert.AreEqual(1024L * 512 * 8, failing.Sample(out estimated));

            var negative = new MemorySampler(() => -1);
            negative.Register("x", 10, 10, 4);
            Assert.AreEqual(400L, negative.Sample(out estimated));
            Assert.IsTrue(estimated);

            var live = new MemorySampler(() => 123456789);
            live.Register("x", 10, 10, 4);
            Assert.AreEqual(123456789L, live.Sample(out estimated));
            Assert.IsFalse(estimated);
        }

        [TestMethod]
        public void The_window_carries_the_memory_and_says_when_it_is_an_estimate()
        {
            var memory = new MemorySampler(null);
            memory.Register("a", 100, 100, 4);
            PerfCounters c = Make(memory);
            c.EndFrame(0.0);
            for (int i = 1; i <= 10; i++) Frame(c, i * 0.1, 1, 0);
            Assert.IsTrue(c.Last.MemoryEstimated);
            Assert.AreEqual(40000L, c.Last.MemoryBytes);
        }
    }
}
