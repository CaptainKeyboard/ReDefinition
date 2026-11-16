using System;
using System.Globalization;
using System.Reflection;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Viewer
{
    // The cost the viewer shows while a slider moves: the GPU time of the cloud pass and the frame time, smoothed, and what the last change of a key did to them. The GPU time is the
    // segment between the pass's begin and end markers (GpuProfiler: "ReDefinition clouds: begin" and "...: end"), read from the main mod's profiler, which runs only with the game
    // started with -redefinitionGpuProfile; without it only the frame time (the game's own, CPU and GPU together) is shown.
    internal sealed class CostReadout
    {
        // The smoothing: each frame enters with this weight.
        internal const double Smoothing = 0.05;
        // The frames a change is watched for before its effect is stated.
        internal const int SettleFrames = 90;

        private double frameMs = double.NaN, gpuMs = double.NaN;
        private double beforeFrame = double.NaN, beforeGpu = double.NaN;
        private int settle = -1;
        private string changed = "";

        internal double FrameMs { get { return frameMs; } }
        internal double GpuMs { get { return gpuMs; } }
        // The effect of the last watched change: frame and GPU milliseconds after minus before; NaN where one of them is missing.
        internal double DeltaFrameMs { get; private set; } = double.NaN;
        internal double DeltaGpuMs { get; private set; } = double.NaN;
        internal string LastChange { get { return changed; } }
        internal bool Watching { get { return settle >= 0; } }

        // One frame: its time and the pass's GPU time in it (NaN where the profiler gives none).
        internal void Add(double frameMilliseconds, double gpuMilliseconds)
        {
            frameMs = Smooth(frameMs, frameMilliseconds);
            gpuMs = Smooth(gpuMs, gpuMilliseconds);
            if (settle < 0) return;
            if (--settle > 0) return;
            DeltaFrameMs = frameMs - beforeFrame;
            DeltaGpuMs = gpuMs - beforeGpu;
            settle = -1;
        }

        private static double Smooth(double now, double sample)
        {
            if (double.IsNaN(sample) || double.IsInfinity(sample)) return now;
            return double.IsNaN(now) ? sample : now + (sample - now) * Smoothing;
        }

        // A key or setting is about to change: the cost now is noted, and after SettleFrames its effect is stated. A second change before that one has settled replaces the first
        // (the window shows the last key moved; the delta then is that of the whole move since the first).
        internal void Mark(string what)
        {
            if (settle < 0)
            {
                beforeFrame = frameMs;
                beforeGpu = gpuMs;
            }
            changed = what ?? "";
            settle = SettleFrames;
            DeltaFrameMs = DeltaGpuMs = double.NaN;
        }

        internal void Reset()
        {
            frameMs = gpuMs = beforeFrame = beforeGpu = DeltaFrameMs = DeltaGpuMs = double.NaN;
            settle = -1;
            changed = "";
        }

        internal static string Ms(double v)
        {
            return double.IsNaN(v) ? "-" : v.ToString("0.00", CultureInfo.InvariantCulture) + " ms";
        }

        internal static string Signed(double v)
        {
            return double.IsNaN(v) ? "-" : v.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " ms";
        }

        // Line one: the pass's GPU time and the frame time.
        internal string NowLine(bool profilerOn)
        {
            string fps = double.IsNaN(frameMs) || frameMs <= 0.0 ? "-" : (1000.0 / frameMs).ToString("0", CultureInfo.InvariantCulture) + " fps";
            return "Cloud pass GPU " + (profilerOn ? Ms(gpuMs) : "off (start with -redefinitionGpuProfile)") + ", frame " + Ms(frameMs) + " (" + fps + ")";
        }

        // Line two: what the last change did.
        internal string ChangeLine()
        {
            if (changed.Length == 0) return "Move a slider: the change in cost is shown here";
            if (Watching) return "Last change " + changed + ": measuring";
            return "Last change " + changed + ": GPU " + Signed(DeltaGpuMs) + ", frame " + Signed(DeltaFrameMs);
        }

        // The tooltip that says where the numbers come from.
        internal const string SourceNote = "GPU: the segment between the cloud pass's begin and end markers (the dxgi proxy's timestamp queries, read by the GPU profiler, only with -redefinitionGpuProfile); frame: Time.unscaledDeltaTime. Both smoothed over about 20 frames.";
    }

    // The GPU time of the cloud pass from the main mod's profiler. The profiler is another assembly of the mod (ReDefinition), so it is reached by name; where it is missing, off or has no
    // frame yet, the answer is NaN.
    internal static class GpuCostSource
    {
        internal const string ProfilerType = "ReDefinition.Probe.GpuProfiler", Method = "SegmentMs", OnProperty = "On";

        private static Func<string, double> segment;
        private static Func<bool> on;
        private static bool resolved;

        private static void Resolve()
        {
            if (resolved) return;
            Type t = TypeLookup.Find(ProfilerType);
            if (t == null) return;
            resolved = true;
            try
            {
                MethodInfo m = t.GetMethod(Method, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(string) }, null);
                if (m != null) segment = (Func<string, double>)Delegate.CreateDelegate(typeof(Func<string, double>), m);
                PropertyInfo p = t.GetProperty(OnProperty, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                MethodInfo g = p == null ? null : p.GetGetMethod(true);
                if (g != null) on = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), g);
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("cloud viewer", "the GPU profiler cannot be read", e);
                segment = null;
                on = null;
            }
        }

        internal static bool ProfilerOn
        {
            get
            {
                Resolve();
                return on != null && on();
            }
        }

        // The cloud pass's GPU time in the last frame the profiler read; NaN where there is none.
        internal static double CloudPassMs()
        {
            Resolve();
            return segment == null || !ProfilerOn ? double.NaN : segment(CloudBridgeLights.MarkEnd);
        }
    }
}
