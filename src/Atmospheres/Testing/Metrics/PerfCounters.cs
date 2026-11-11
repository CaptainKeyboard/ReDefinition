using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ReDefinition.Atmospheres
{
    // The figures of one 1 second window: per system the frames it ran in, the mean and worst milliseconds per frame, and over all
    // frames the allocation per frame and the memory at its end.
    internal sealed class PerfWindow
    {
        internal double Seconds;
        internal int Frames;
        internal readonly Dictionary<string, double> MeanMs = new Dictionary<string, double>();
        internal readonly Dictionary<string, double> MaxMs = new Dictionary<string, double>();
        internal double AllocBytesPerFrame;
        internal int Collections;
        internal long MemoryBytes;
        internal bool MemoryEstimated;
    }

    // Where the memory comes from: a live reading where there is one (the Unity profiler), else the sum of the textures and buffers we
    // know we hold. A reading below zero or an exception counts as no reading.
    internal sealed class MemorySampler
    {
        private readonly Func<long> live;
        private readonly Dictionary<string, long> known = new Dictionary<string, long>();

        internal MemorySampler(Func<long> live)
        {
            this.live = live;
        }

        // Registers a texture of ours: its bytes are width x height x bytes per pixel x depth, with a third more for the mip chain.
        internal void Register(string name, int width, int height, int bytesPerPixel, int depth = 1, bool mips = false)
        {
            long bytes = (long)width * height * bytesPerPixel * depth;
            known[name] = mips ? bytes + bytes / 3 : bytes;
        }

        internal void Unregister(string name)
        {
            known.Remove(name);
        }

        internal long KnownBytes
        {
            get
            {
                long sum = 0;
                foreach (long v in known.Values) sum += v;
                return sum;
            }
        }

        // The memory in bytes and whether it is an estimate from the known sizes.
        internal long Sample(out bool estimated)
        {
            estimated = false;
            if (live != null)
            {
                try
                {
                    long v = live();
                    if (v >= 0) return v;
                }
                catch (Exception)
                {
                    // no reading: the known sizes stand in
                }
            }
            estimated = true;
            return KnownBytes;
        }
    }

    // The cost counters of the atmospheres: a stopwatch per system, windows of one second, a line "perf:" every 10 seconds, and the
    // allocation per frame from the deltas of the managed heap. The clock and the heap reading are injected, so that a test steps
    // time and memory by hand. Use: Begin("volume") ... End("volume") around a system (or the using form), EndFrame once per frame.
    internal sealed class PerfCounters
    {
        internal const double WindowSeconds = 1.0;
        internal const double ReportSeconds = 10.0;

        private readonly Func<long> ticks;
        private readonly double ticksPerSecond;
        private readonly Func<long> heap;
        private readonly Action<string> sink;
        private readonly MemorySampler memory;
        private readonly Dictionary<string, long> started = new Dictionary<string, long>();
        private readonly Dictionary<string, double> frameMs = new Dictionary<string, double>();
        private readonly Dictionary<string, double> sumMs = new Dictionary<string, double>();
        private readonly Dictionary<string, double> maxMs = new Dictionary<string, double>();
        private readonly List<PerfWindow> reportWindows = new List<PerfWindow>();
        private double windowStart = double.NaN, lastReport = double.NaN;
        private int frames, collections;
        private double allocBytes;
        private long lastHeap = -1;

        // ticks and ticksPerSecond default to the Stopwatch; heap to GC.GetTotalMemory(false); sink to nothing (the game passes the log).
        internal PerfCounters(Action<string> sink, MemorySampler memory = null, Func<long> heap = null, Func<long> ticks = null, double ticksPerSecond = 0)
        {
            this.sink = sink;
            this.memory = memory ?? new MemorySampler(null);
            this.heap = heap ?? (() => GC.GetTotalMemory(false));
            this.ticks = ticks ?? Stopwatch.GetTimestamp;
            this.ticksPerSecond = ticks == null ? Stopwatch.Frequency : (ticksPerSecond > 0 ? ticksPerSecond : 1000.0);
        }

        // The last closed window, or null before the first.
        internal PerfWindow Last { get; private set; }
        internal int WindowsClosed { get; private set; }
        internal int ReportsWritten { get; private set; }

        internal void Begin(string system)
        {
            started[system] = ticks();
        }

        // Ends a system's stopwatch; the time adds to this frame's time of the system (a system may run more than once a frame).
        internal void End(string system)
        {
            long t;
            if (!started.TryGetValue(system, out t)) return;
            started.Remove(system);
            double ms = (ticks() - t) * 1000.0 / ticksPerSecond;
            double sum;
            frameMs.TryGetValue(system, out sum);
            frameMs[system] = sum + ms;
        }

        // A scope for a using block.
        internal struct Scope : IDisposable
        {
            private readonly PerfCounters owner;
            private readonly string system;

            internal Scope(PerfCounters owner, string system)
            {
                this.owner = owner;
                this.system = system;
                owner.Begin(system);
            }

            public void Dispose()
            {
                owner.End(system);
            }
        }

        internal Scope Measure(string system)
        {
            return new Scope(this, system);
        }

        // Closes the frame at the time `now` (seconds on the caller's clock): the allocation since the last frame, the systems' times of
        // this frame into the window, and the window when a second is over; the report every 10 seconds.
        internal void EndFrame(double now)
        {
            if (double.IsNaN(windowStart))
            {
                windowStart = lastReport = now;
                lastHeap = heap();
                frameMs.Clear();
                return;
            }
            long h = heap();
            long delta = h - lastHeap;
            // A fall of the heap is a collection: it counts as a collection, not as a negative allocation.
            if (delta < 0) collections++;
            else allocBytes += delta;
            lastHeap = h;
            frames++;
            foreach (KeyValuePair<string, double> kv in frameMs)
            {
                double s;
                sumMs.TryGetValue(kv.Key, out s);
                sumMs[kv.Key] = s + kv.Value;
                double m;
                maxMs.TryGetValue(kv.Key, out m);
                if (kv.Value > m) maxMs[kv.Key] = kv.Value;
            }
            frameMs.Clear();
            if (now - windowStart >= WindowSeconds) CloseWindow(now);
            if (now - lastReport >= ReportSeconds) Report(now);
        }

        private void CloseWindow(double now)
        {
            var w = new PerfWindow { Seconds = now - windowStart, Frames = frames, Collections = collections };
            foreach (KeyValuePair<string, double> kv in sumMs) w.MeanMs[kv.Key] = frames == 0 ? 0 : kv.Value / frames;
            foreach (KeyValuePair<string, double> kv in maxMs) w.MaxMs[kv.Key] = kv.Value;
            w.AllocBytesPerFrame = frames == 0 ? 0 : allocBytes / frames;
            w.MemoryBytes = memory.Sample(out w.MemoryEstimated);
            Last = w;
            WindowsClosed++;
            reportWindows.Add(w);
            sumMs.Clear();
            maxMs.Clear();
            frames = 0;
            collections = 0;
            allocBytes = 0;
            windowStart = now;
        }

        // One line for the last 10 seconds: the mean of each system's window means, the worst, the allocation per frame, the memory.
        private void Report(double now)
        {
            lastReport = now;
            if (reportWindows.Count == 0) return;
            var names = new SortedDictionary<string, double[]>(StringComparer.Ordinal);
            double alloc = 0;
            int collected = 0;
            foreach (PerfWindow w in reportWindows)
            {
                alloc += w.AllocBytesPerFrame;
                collected += w.Collections;
                foreach (KeyValuePair<string, double> kv in w.MeanMs)
                {
                    double[] a;
                    if (!names.TryGetValue(kv.Key, out a)) names[kv.Key] = a = new double[2];
                    a[0] += kv.Value;
                    double worst;
                    w.MaxMs.TryGetValue(kv.Key, out worst);
                    if (worst > a[1]) a[1] = worst;
                }
            }
            var sb = new StringBuilder("perf:");
            foreach (KeyValuePair<string, double[]> kv in names)
                sb.Append(' ').Append(kv.Key).Append('=').Append((kv.Value[0] / reportWindows.Count).ToString("0.###", CultureInfo.InvariantCulture))
                  .Append("ms(max ").Append(kv.Value[1].ToString("0.###", CultureInfo.InvariantCulture)).Append(')');
            PerfWindow lastWindow = reportWindows[reportWindows.Count - 1];
            sb.Append(" alloc=").Append((alloc / reportWindows.Count).ToString("0", CultureInfo.InvariantCulture)).Append("B/frame");
            sb.Append(" gc=").Append(collected);
            sb.Append(" mem=").Append((lastWindow.MemoryBytes / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture)).Append("MB");
            if (lastWindow.MemoryEstimated) sb.Append("(known sizes)");
            reportWindows.Clear();
            ReportsWritten++;
            if (sink != null) sink(sb.ToString());
        }
    }
}
