using System.Collections.Generic;
using ReDefinition.Proxy;
using ReDefinition.Modules;
using UnityEngine;

namespace ReDefinition.Diagnostics
{
    // The diagnostics window's live strip: what runs, the frame rates, the frame time graph,
    // latency and load, and the search, the "Changed" filter and the reset. The texts are
    // made four times a second while the window is open (RefreshLiveStrip); OnGUI only reads
    // them.
    internal sealed partial class DiagnosticsWindow
    {
        // The frame time graph's size, and how many bars it draws.
        private const float SparklineWidth = 190f;
        private const float SparklineHeight = 44f;
        private const int SparklineBars = 120;

        // The graph's layout options, made once.
        private static readonly GUILayoutOption SparklineWidthOption = GUILayout.Width(SparklineWidth);
        private static readonly GUILayoutOption SparklineHeightOption = GUILayout.Height(SparklineHeight);

        // When the strip is next due, and the graph's top and median, set at its refresh.
        private float nextStripRefresh;
        private float sparklineTop = 33.3f;
        private float sparklineMedian;

        // The texts the refresh makes for the strip and the pages: what runs, the frame rates, the frame times, the
        // latency, frame generation's technique, the low-latency mode, the presented-per-rendered ratio, the recording.
        private string runningText = "";
        private string fpsText = "-- fps";
        private string frameTimeText = "";
        private string latencyText = "Latency: --";
        private string fgTechniqueText = "";
        private string lowLatencyDescription = "";
        private string rateRatioText = "--";
        private string recordingState = "";
        // The frame rate with the upscaler on against off (FrameRateReadout.Comparison), made with the rest.
        private string frameRateText = "";

        // "FSR 3 + XeSS-FG 2x + XeLL": the upscaler that runs, the frame generation that
        // generates, what lowers the latency.
        private string RunningText()
        {
            List<string> parts = new List<string>();
            if (addon.rig != null && addon.rig.Ready)
                parts.Add(OurModules.TechniqueLabel(addon.rig.Backend) + (addon.rig.Backend != addon.backend && addon.fallbackReason != null ? " (instead of "
                          + UpscalerBackends.Name(addon.backend) + ")" : "")
                          // Upscaler off, frame generation on: native antialiasing (NativeForFrameGeneration).
                          + (!addon.wantEnabled ? " native AA" : ""));
            else if (addon.rig != null && addon.rig.PassThrough) parts.Add("Upscaler off");
            else parts.Add(addon.wantEnabled ? "Upscaler setting up" : "Upscaler off");

            if (addon.frameGeneration)
            {
                string technique = FrameGenerationBridge.TechniqueName();
                parts.Add(technique == null ? "frame generation idle" : FrameGenerationShort(technique));
            }

            string lowLatency = ReflexFrames.LowLatencyName();
            if (lowLatency != null) parts.Add(lowLatency);
            return string.Join(" + ", parts.ToArray());
        }

        // KspFgTechnique's label as the strip names it: "DLSS 2x" is DLSS-G 2x, "XeSS 2x" XeSS-FG 2x.
        internal static string FrameGenerationShort(string technique)
        {
            if (technique.StartsWith("DLSS")) return "DLSS-G" + technique.Substring(4);
            if (technique.StartsWith("XeSS")) return "XeSS-FG" + technique.Substring(4);
            return technique + " FG";
        }

        // Makes the strip's texts afresh, at most four times a second.
        public void RefreshLiveStrip()
        {
            if (Time.unscaledTime < nextStripRefresh) return;
            nextStripRefresh = Time.unscaledTime + 0.25f;

            runningText = RunningText();
            float renderedFps = rates.RenderedFps;
            float shownFps = rates.ShownFps;
            fpsText = renderedFps < 0f ? "-- fps"
                : shownFps > renderedFps ? renderedFps.ToString("0") + " -> " + shownFps.ToString("0") + " fps"
                : renderedFps.ToString("0") + " fps";

            FrameTimeRing frameTimes = rates.FrameTimes;
            if (frameTimes.Count > 0)
            {
                sparklineMedian = frameTimes.Percentile(0.5f);
                float p95 = frameTimes.Percentile(0.95f);
                sparklineTop = Mathf.Clamp(p95 * 1.6f, 16.7f, 120f);
                frameTimeText = "Frame time " + sparklineMedian.ToString("0.0") + " ms, 95 % under "
                                + p95.ToString("0.0") + ", worst " + frameTimes.Max.ToString("0.0");
            }

            double median;
            double percentile90;
            latencyText = ReflexFrames.LatencyMeasure(out median, out percentile90) > 0
                ? "Latency " + median.ToString("0.0") + " ms, 90 % under " + percentile90.ToString("0.0")
                : "Latency: not measured";

            string technique = FrameGenerationBridge.TechniqueName();
            fgTechniqueText = !addon.frameGeneration ? "off" : technique ?? "not generating";
            lowLatencyDescription = ReflexFrames.LowLatencyText() ?? "no proxy, or it does not say";
            float presentedPerRendered = rates.PresentedPerRendered;
            rateRatioText = presentedPerRendered > 0f ? presentedPerRendered.ToString("0.00") + " presented per rendered frame" : "--";
            recordingState = ScreenRecordingBridge.State();
            frameRateText = rates.Comparison();
        }

        // The strip: what runs and the frame rate, then the graph, the frame time, latency and load lines, and the filter
        // controls.
        private void DrawLiveStrip()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(runningText, styles.Strip, DiagnosticsLayout.Fill);
            GUILayout.Label(fpsText, styles.Fps, DiagnosticsLayout.FpsColumn);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            Rect graph = GUILayoutUtility.GetRect(SparklineWidth, SparklineHeight, SparklineWidthOption,
                                                  SparklineHeightOption);
            DrawSparkline(graph);
            GUILayout.Space(8f);

            GUILayout.BeginVertical(DiagnosticsLayout.FrameTimeColumn);
            GUILayout.Label(frameTimeText, styles.Small);
            GUILayout.Label(latencyText, styles.Small);
            GUILayout.Label(rates.LoadText, styles.Small);
            GUILayout.EndVertical();

            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical(DiagnosticsLayout.FilterColumn);
            DrawFilterControls();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        // The search field, the "Changed" chip and the reset.
        private void DrawFilterControls()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", styles.Small, DiagnosticsLayout.SearchLabel);
            GUI.SetNextControlName(SearchControl);
            typedQuery = GUILayout.TextField(typedQuery, 40, styles.Field, DiagnosticsLayout.Fill);
            if (Event.current.type == EventType.Repaint) searchRect = GUILayoutUtility.GetLastRect();
            // Shown by the search in force, not by the text being typed: the controls of a
            // layout pass and of the event after it must be the same.
            if (query.Length > 0 && GUILayout.Button(content.ClearSearch, styles.Segment, DiagnosticsLayout.ClearButton))
            {
                typedQuery = "";
                GUI.FocusControl("");
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(changedOnly, content.ChangedChip(changedCount), styles.Segment)
                != changedOnly)
                pendingChangedToggle = true;
            GUILayout.Space(4f);
            bool outer = GUI.enabled;
            GUI.enabled = outer && changedCount > 0;
            if (GUILayout.Button(content.ResetSwitches, styles.Segment))
                pendingReset = true;
            GUI.enabled = outer;
            GUILayout.EndHorizontal();
        }

        // The last frame times as bars, newest at the right; a frame well over the usual one is
        // drawn in the warning colour. Two frames to a bar (the longer of them).
        private void DrawSparkline(Rect area)
        {
            if (Event.current.type != EventType.Repaint) return;
            Texture2D white = Texture2D.whiteTexture;
            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(area, white);

            FrameTimeRing frameTimes = rates.FrameTimes;
            int count = frameTimes.Count;
            if (count >= 2)
            {
                float barWidth = area.width / SparklineBars;
                int perBar = Mathf.Max(1, frameTimes.Capacity / SparklineBars);
                for (int bar = 0; bar < SparklineBars; bar++)
                {
                    int last = count - 1 - (SparklineBars - 1 - bar) * perBar;
                    float value = 0f;
                    for (int k = 0; k < perBar; k++)
                    {
                        int at = last - k;
                        if (at >= 0) value = Mathf.Max(value, frameTimes[at]);
                    }
                    if (value <= 0f) continue;
                    float height = Mathf.Clamp01(value / sparklineTop) * (area.height - 2f);
                    GUI.color = value > sparklineMedian * 1.5f ? DiagnosticsStyles.Warning : new Color(0.45f, 0.8f, 1f);
                    GUI.DrawTexture(new Rect(area.x + bar * barWidth, area.yMax - 1f - height, Mathf.Max(1f, barWidth - 0.4f), height), white);
                }
            }
            GUI.color = previous;
        }
    }
}
