using System.Text;
using ReDefinition.Proxy;
using ReDefinition.Upscaler;
using UnityEngine;

namespace ReDefinition.Diagnostics
{
    // The frame rates the window compares and shows: with and without the upscaler, rendered and
    // presented, the load behind them, and every frame's time for the strip's graph. The add-on
    // feeds it once per frame (Sample, Update).
    internal sealed class FrameRateReadout
    {
        // The frame times with the upscaler running, and with it switched off, for Comparison.
        private readonly FrameRateMeter meterOn = new FrameRateMeter();
        private readonly FrameRateMeter meterOff = new FrameRateMeter();

        // The last 240 frame times (ms), for the strip's graph.
        private readonly FrameTimeRing frameTimes = new FrameTimeRing(240);

        // The frame rates the window's strip shows, measured once a second (Update): the
        // frames the game renders, and the frames the swapchain presents, which are more while
        // frame generation generates; negative until the first second is out.
        private float renderedFps = -1f;
        private float shownFps = -1f;
        // Presented per rendered frame over the last second, 0 while the proxy counts none.
        private float presentedPerRendered;
        private int fpsFrames;
        private float fpsWindowStart = -1f;
        private uint countedRendered;
        private uint countedPresented;
        private bool counted;
        private string loadText = "Load: --";

        // The last frame times, newest last.
        public FrameTimeRing FrameTimes
        {
            get { return frameTimes; }
        }

        // The frames the game rendered per second over the last second; negative before the first.
        public float RenderedFps
        {
            get { return renderedFps; }
        }

        // The frames shown per second: the presented ones where frame generation generates, else the rendered ones.
        public float ShownFps
        {
            get { return shownFps; }
        }

        // Presented per rendered frame over the last second, 0 while the proxy counts none.
        public float PresentedPerRendered
        {
            get { return presentedPerRendered; }
        }

        // The load line: the main thread's, the render thread's and the GPU's share of the frame, or why there is none.
        public string LoadText
        {
            get { return loadText; }
        }

        // Adds this frame's time to the mean of the upscaler on or off. A rig for frame
        // generation alone counts as neither, so the caller leaves it out.
        public void Sample(bool upscalerOn)
        {
            (upscalerOn ? meterOn : meterOff).Sample(Time.unscaledDeltaTime);
        }

        // Starts the mean with the upscaler on afresh, as after a new rig.
        public void ResetOn()
        {
            meterOn.Reset();
        }

        // Starts the mean with the upscaler off afresh, as after the rig went.
        public void ResetOff()
        {
            meterOff.Reset();
        }

        // The window's frame rates: the frames the game renders, counted
        // here, and the frames the swapchain presented while frame generation generates. The
        // presented frames include generated ones and come from the proxy's running
        // totals (KspFgCounters), read at the start and the end of the same second.
        // Taken once a second, so the number can be read; and every frame's time
        // for the strip's graph. Nothing is measured, and the proxy not asked, while the window is closed;
        // the readings start over when it opens.
        public void Update(bool windowVisible)
        {
            if (!windowVisible)
            {
                if (fpsWindowStart >= 0f)
                {
                    fpsWindowStart = -1f;
                    renderedFps = -1f;
                    shownFps = -1f;
                    presentedPerRendered = 0f;
                    frameTimes.Clear();
                    loadText = "Load: measuring ...";
                }
                return;
            }
            frameTimes.Add(Time.unscaledDeltaTime * 1000f);
            float now = Time.unscaledTime;
            if (fpsWindowStart < 0f)
            {
                fpsWindowStart = now;
                fpsFrames = 0;
                counted = FrameGenerationBridge.TryGetCounters(out countedRendered, out countedPresented);
                return;
            }

            fpsFrames++;
            float elapsed = now - fpsWindowStart;
            if (elapsed < 1f) return;

            renderedFps = fpsFrames / elapsed;
            shownFps = renderedFps;
            presentedPerRendered = 0f;
            fpsWindowStart = now;
            fpsFrames = 0;

            uint rendered;
            uint presented;
            bool countedNow = FrameGenerationBridge.TryGetCounters(out rendered, out presented);
            if (countedNow && counted)
            {
                uint renderedDelta = unchecked(rendered - countedRendered);
                uint presentedDelta = unchecked(presented - countedPresented);
                if (renderedDelta > 0)
                {
                    presentedPerRendered = (float)presentedDelta / renderedDelta;
                    if (presentedDelta >= 1.5f * renderedDelta) shownFps = presentedDelta / elapsed;
                }
            }

            counted = countedNow;
            countedRendered = rendered;
            countedPresented = presented;

            // Where the frame's time goes, from the proxy (LoadMonitor): whether
            // a smaller render size can buy anything at all. Being read makes the
            // proxy collect Windows' GPU counters every second.
            loadText = DescribeLoad();
        }

        // The load line: the main thread's, the render thread's and the GPU's share of the frame, or why there is none.
        private static string DescribeLoad()
        {
            float mainThread;
            float renderThread;
            float gpu;
            float gpuThisProcess;
            switch (FrameGenerationBridge.ReadLoad(out mainThread, out renderThread, out gpu, out gpuThisProcess))
            {
                case FrameGenerationBridge.LoadState.Measured:
                    return "Load: main " + Percent(mainThread) + ", render " + Percent(renderThread)
                           + ", GPU " + Percent(gpu);
                case FrameGenerationBridge.LoadState.NotYet:
                    return "Load: measuring ...";
                case FrameGenerationBridge.LoadState.SwitchedOff:
                    return "Load: not measured, the proxy is off in its ini";
                default:
                    return "Load: not measured, needs the dxgi.dll proxy";
            }
        }

        // A share as a whole percentage; "n/a" where the proxy could not read it.
        private static string Percent(float value)
        {
            return value < 0f ? "n/a" : value.ToString("0") + " %";
        }

        // The frame rate with the upscaler on against off (FrameRateMeter).
        public string Comparison()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("fps on ").Append(meterOn.Describe())
              .Append("   off ").Append(meterOff.Describe());
            if (meterOn.HasData && meterOff.HasData)
            {
                float gain = (meterOn.AverageFps / meterOff.AverageFps - 1f) * 100f;
                sb.Append("   (").Append(gain >= 0f ? "+" : "").Append(gain.ToString("0.0")).Append(" %)");
            }
            return sb.ToString();
        }
    }
}
