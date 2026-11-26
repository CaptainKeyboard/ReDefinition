using System;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // Measurement (master plan 46.4, PRUEFPROTOKOLL 347): what the scene depth the pass limits its march with holds, by quarter of the picture. At 300 km, after
    // views from far, the clouds were gone in one planet tile and came back with the depth limit off; the picture shows nothing drawn there. A probe sets
    // ProbeDepthStats; the next frame's depth target is read once (no stall: AsyncGPUReadback) and one log line says, for each quarter, how many pixels hold a depth
    // and its least, middle and greatest value in m. Nothing runs unless a probe asks.
    internal sealed partial class OwnCloudModule
    {
        // The property id lives in a nested class: it is made at its first use in the game, not when a test touches the module's type outside the engine.
        private static class DepthIds
        {
            internal static readonly int MaxM = Shader.PropertyToID("_LayerDepthMaxM");
        }

        internal static bool ProbeDepthStats;
        // What the read is for, in the log line (the probe's view and variant).
        internal static string ProbeDepthStatsTag = "";

        private void DepthStatsIfAsked(double cameraAltitudeM)
        {
            if (!ProbeDepthStats || depthTarget == null || !depthTarget.IsCreated()) return;
            ProbeDepthStats = false;
            string tag = ProbeDepthStatsTag;
            int w = depthTarget.width, h = depthTarget.height;
            AsyncGPUReadback.Request(depthTarget, 0, TextureFormat.RFloat, request =>
            {
                try
                {
                    if (request.hasError)
                    {
                        AtmospheresLog.Say("own clouds: scene depth read (" + tag + ") failed");
                        return;
                    }
                    float[] data = request.GetData<float>().ToArray();
                    AtmospheresLog.Say("own clouds: scene depth (" + tag + "), camera " + cameraAltitudeM.ToString("0", CultureInfo.InvariantCulture) + " m up, " + w + "x" + h + ": "
                                       + DepthStatsText(data, w, h));
                }
                catch (Exception e)
                {
                    AtmospheresLog.Say("own clouds: scene depth read (" + tag + ") failed: " + e.Message);
                }
            });
        }

        // "upper left: n px of N, least / middle / greatest m; ..." for the four quarters (the texture's row 0 is the picture's bottom). A depth of 0 is the sky.
        internal static string DepthStatsText(float[] data, int w, int h)
        {
            var text = new StringBuilder(256);
            string[] names = { "lower left", "lower right", "upper left", "upper right" };
            for (int q = 0; q < 4; q++)
            {
                int x0 = (q & 1) == 0 ? 0 : w / 2, x1 = (q & 1) == 0 ? w / 2 : w, y0 = q < 2 ? 0 : h / 2, y1 = q < 2 ? h / 2 : h;
                var values = new System.Collections.Generic.List<float>();
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        float v = data[y * w + x];
                        if (v > 0f && v < 1e9f) values.Add(v);
                    }
                values.Sort();
                if (q > 0) text.Append("; ");
                text.Append(names[q]).Append(": ").Append(values.Count).Append(" px of ").Append((x1 - x0) * (y1 - y0));
                if (values.Count > 0)
                    text.Append(", ").Append(values[0].ToString("0", CultureInfo.InvariantCulture)).Append(" / ")
                        .Append(values[values.Count / 2].ToString("0", CultureInfo.InvariantCulture)).Append(" / ")
                        .Append(values[values.Count - 1].ToString("0", CultureInfo.InvariantCulture)).Append(" m");
            }
            return text.ToString();
        }
    }
}
