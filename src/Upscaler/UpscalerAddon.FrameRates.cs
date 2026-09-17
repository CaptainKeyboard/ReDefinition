using System.Text;
using UnityEngine;

namespace ReDefinition
{
    // The add-on's part for the frame rates the window compares: with and without
    // the upscaler, rendered and presented, and the load behind them.
    public partial class UpscalerAddon
    {
        private readonly FrameRateMeter meterOn = new FrameRateMeter();
        private readonly FrameRateMeter meterOff = new FrameRateMeter();

        // The window's frame rate line, built once a second (UpdateFrameRates).
        private string frameRateText = "-- fps";
        private int fpsFrames;
        private float fpsWindowStart = -1f;
        private uint countedRendered;
        private uint countedPresented;
        private bool counted;
        private string loadText = "Load: --";

        // The window's frame rate line: the frames the game renders, counted
        // here, and -- while frame generation generates -- in brackets the
        // frames the swapchain presented, generated ones included, from the
        // proxy's running totals (KspFgCounters) read at the start and the end
        // of the same second. Built once a second, so the number can be read
        // and OnGUI does not rebuild the string on every event.
        private void UpdateFrameRates()
        {
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

            string text = (fpsFrames / elapsed).ToString("0") + " fps";
            fpsWindowStart = now;
            fpsFrames = 0;

            uint rendered;
            uint presented;
            bool countedNow = FrameGenerationBridge.TryGetCounters(out rendered, out presented);
            if (countedNow && counted)
            {
                uint renderedDelta = unchecked(rendered - countedRendered);
                uint presentedDelta = unchecked(presented - countedPresented);
                if (renderedDelta > 0 && presentedDelta >= 1.5f * renderedDelta)
                    text += "  (" + (presentedDelta / elapsed).ToString("0") + " with frame generation)";
            }

            counted = countedNow;
            countedRendered = rendered;
            countedPresented = presented;
            frameRateText = text;

            // Where the frame's time goes, from the proxy (LoadMonitor): whether
            // a smaller render size can buy anything at all. Asked only while
            // the Debug tab shows it: being read makes the proxy collect
            // Windows' GPU counters every second.
            // Outside the tab the line starts over, so figures from an earlier
            // visit cannot pass for current ones.
            loadText = windowVisible && tab == Tab.Debug ? DescribeLoad() : "Load: measuring ...";
        }

        private static string DescribeLoad()
        {
            float mainThread;
            float renderThread;
            float gpu;
            float gpuThisProcess;
            switch (FrameGenerationBridge.ReadLoad(out mainThread, out renderThread, out gpu, out gpuThisProcess))
            {
                case FrameGenerationBridge.LoadState.Measured:
                    return "Load: main thread " + Percent(mainThread) + ", render thread " + Percent(renderThread)
                           + ", GPU " + Percent(gpu);
                case FrameGenerationBridge.LoadState.NotYet:
                    return "Load: measuring ...";
                case FrameGenerationBridge.LoadState.SwitchedOff:
                    return "Load: not measured -- the proxy is switched off in its ini";
                default:
                    return "Load: not measured -- needs the dxgi.dll proxy";
            }
        }

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
                float gain = (meterOn.Average / meterOff.Average - 1f) * 100f;
                sb.Append("   (").Append(gain >= 0f ? "+" : "").Append(gain.ToString("0.0")).Append(" %)");
            }
            return sb.ToString();
        }
    }
}
