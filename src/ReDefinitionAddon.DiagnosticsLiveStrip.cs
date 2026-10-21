using System.Collections.Generic;
using ReDefinition.Bridges;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition
{
    // The diagnostics window's live strip: what runs, the frame rates, the frame time graph,
    // latency and load, and the search, the "Changed" filter and the reset. The texts are
    // made four times a second while the window is open (RefreshLiveStrip); OnGUI only reads
    // them.
    public partial class ReDefinitionAddon
    {
        private const float SparklineWidth = 190f;
        private const float SparklineHeight = 44f;
        private const int SparklineBars = 120;

        private readonly FrameTimeRing frameTimes = new FrameTimeRing(240);
        private float nextStripRefresh;
        private float sparklineTop = 33.3f;
        private float sparklineMedian;

        private string runningText = "";
        private string fpsText = "-- fps";
        private string frameTimeText = "";
        private string latencyText = "Latency: --";
        private string fgTechniqueText = "";
        private string lowLatencyDescription = "";
        private string rateRatioText = "--";
        private string recordingState = "";

        // "FSR 3 + XeSS-FG 2x + XeLL": the upscaler that runs, the frame generation that
        // generates, what lowers the latency.
        private string RunningText()
        {
            List<string> parts = new List<string>();
            if (rig != null && rig.Ready)
                parts.Add(OurModules.TechniqueLabel(rig.Backend) + (rig.Backend != backend && fallbackReason != null ? " (instead of "
                          + UpscalerBackends.Name(backend) + ")" : ""));
            else if (rig != null && rig.PassThrough) parts.Add("Upscaler off");
            else parts.Add(wantEnabled ? "Upscaler setting up" : "Upscaler off");

            if (frameGeneration)
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

        private void RefreshLiveStrip()
        {
            if (Time.unscaledTime < nextStripRefresh) return;
            nextStripRefresh = Time.unscaledTime + 0.25f;

            runningText = RunningText();
            fpsText = renderedFps < 0f ? "-- fps"
                : shownFps > renderedFps ? renderedFps.ToString("0") + " -> " + shownFps.ToString("0") + " fps"
                : renderedFps.ToString("0") + " fps";

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
            fgTechniqueText = !frameGeneration ? "off" : technique ?? "not generating";
            lowLatencyDescription = ReflexFrames.LowLatencyText() ?? "no proxy, or it does not say";
            rateRatioText = presentedPerRendered > 0f ? presentedPerRendered.ToString("0.00") + " presented per rendered frame" : "--";
            recordingState = ScreenRecordingBridge.State();
        }

        private void DrawLiveStrip()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(runningText, styles.Strip, GUILayout.ExpandWidth(true));
            GUILayout.Label(fpsText, styles.Fps, GUILayout.Width(200f));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            Rect graph = GUILayoutUtility.GetRect(SparklineWidth, SparklineHeight, GUILayout.Width(SparklineWidth),
                                                  GUILayout.Height(SparklineHeight));
            DrawSparkline(graph);
            GUILayout.Space(8f);

            GUILayout.BeginVertical(GUILayout.Width(268f));
            GUILayout.Label(frameTimeText, styles.Small);
            GUILayout.Label(latencyText, styles.Small);
            GUILayout.Label(loadText, styles.Small);
            GUILayout.EndVertical();

            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical(GUILayout.Width(220f));
            DrawFilterControls();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        // The search field, the "Changed" chip and the reset.
        private void DrawFilterControls()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", styles.Small, GUILayout.Width(44f));
            GUI.SetNextControlName(SearchControl);
            typedQuery = GUILayout.TextField(typedQuery, 40, styles.Field, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) searchRect = GUILayoutUtility.GetLastRect();
            // Shown by the search in force, not by the text being typed: the controls of a
            // layout pass and of the event after it must be the same.
            if (query.Length > 0 && GUILayout.Button(content.ClearSearch, styles.Segment, GUILayout.Width(24f)))
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
