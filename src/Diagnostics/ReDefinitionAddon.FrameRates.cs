using System.Text;
using ReDefinition.Proxy;
using ReDefinition.Upscaler;
using UnityEngine;

namespace ReDefinition
{
    // The add-on's part for the frame rates the window compares: with and without
    // the upscaler, rendered and presented, and the load behind them.
    public partial class ReDefinitionAddon
    {
        // The frame times with the upscaler running, and with it switched off, for FrameRateComparison.
        private readonly FrameRateMeter meterOn = new FrameRateMeter();
        private readonly FrameRateMeter meterOff = new FrameRateMeter();

        // The frame rates the window's strip shows, measured once a second
        // (UpdateFrameRates): the frames the game renders, and the frames the swapchain
        // presents, which are more while frame generation generates; negative until the
        // first second is out.
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

        // The window's frame rates: the frames the game renders, counted
        // here, and the frames the swapchain presented while frame generation generates. The
        // presented frames include generated ones and come from the proxy's running
        // totals (KspFgCounters), read at the start and the end of the same second.
        // Taken once a second, so the number can be read; and every frame's time
        // for the strip's graph.
        private void UpdateFrameRates()
        {
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
            // a smaller render size can buy anything at all. Asked only while
            // the window shows it: being read makes the proxy collect
            // Windows' GPU counters every second.
            // While it is closed the line starts over, so figures from an earlier
            // visit cannot pass for current ones.
            loadText = windowVisible ? DescribeLoad() : "Load: measuring ...";
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

        // The frame rate with the upscaler on against off (FrameRateMeter). A rig
        // for frame generation alone counts as neither.
        private string FrameRateComparison()
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
