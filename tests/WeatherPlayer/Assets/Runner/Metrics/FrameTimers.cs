using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    // The timers of a scene. CPU: the frames' wall time (mean, 95th percentile, worst). GPU: the proxy's timestamp queries
    // (WeatherGpuTimer) averaged over frames, where the proxy answers (dxgi.dll beside the player with enabled=1).
    public sealed class FrameTimers
    {
        private readonly List<float> cpu = new List<float>();
        private readonly float[] segments = new float[8];
        private double gpuTotal;
        private int gpuFrames;

        // Starts the proxy's queries; false where they are not there.
        public bool StartGpu()
        {
            return WeatherGpuTimer.Start();
        }

        public bool GpuAvailable { get { return WeatherGpuTimer.Available; } }

        // Call once per frame, after the frame ended: counts the CPU frame time and, where the proxy has a new frame, its GPU time.
        public void Sample()
        {
            cpu.Add(Time.unscaledDeltaTime * 1000f);
            float total;
            int frame;
            if (WeatherGpuTimer.Read(segments, out total, out frame))
            {
                gpuTotal += total;
                gpuFrames++;
            }
        }

        public void Clear()
        {
            cpu.Clear();
            gpuTotal = 0;
            gpuFrames = 0;
        }

        public int Frames { get { return cpu.Count; } }

        public double CpuMean
        {
            get
            {
                double sum = 0;
                foreach (float v in cpu) sum += v;
                return cpu.Count == 0 ? double.NaN : sum / cpu.Count;
            }
        }

        public double CpuPercentile(double p)
        {
            if (cpu.Count == 0) return double.NaN;
            var sorted = new List<float>(cpu);
            sorted.Sort();
            return sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];
        }

        // The mean GPU ms of the frames the proxy reported; NaN where there were none.
        public double GpuMean { get { return gpuFrames == 0 ? double.NaN : gpuTotal / gpuFrames; } }

        public int GpuFrames { get { return gpuFrames; } }
    }
}
