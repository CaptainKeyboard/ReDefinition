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

        // What the shaders' SceneEyeDepth makes of the camera's projection (ReDefinitionAtmospheresSceneDepth.cginc reads unity_CameraProjection; this is the
        // same arithmetic on the matrix the camera holds as it culls), for the measurement's log line.
        private static string DepthProjectionText(Camera cam)
        {
            Matrix4x4 p = cam.projectionMatrix;
            float a, b;
            SceneDepthPlan.Terms(p.m22, p.m23, out a, out b);
            bool usable = SceneDepthPlan.Usable(p.m32, p.m33, a, b);
            CultureInfo inv = CultureInfo.InvariantCulture;
            return "depth by " + (usable ? "the projection" : "the planes (the projection is not of the perspective kind)") + " (a " + a.ToString("R", inv) + ", b "
                   + b.ToString("R", inv) + ", its far plane " + SceneDepthPlan.ImpliedFar(a, b).ToString("0", inv) + " m)";
        }

        internal static bool ProbeDepthStats;
        // What the read is for, in the log line (the probe's view and variant).
        internal static string ProbeDepthStatsTag = "";

        // The distance along a ray (unit direction d from the point o, planet-relative) to the sphere of the radius r, or -1 where the ray misses it or starts inside.
        internal static double SphereAlongRay(Vector3d o, Vector3d d, double r)
        {
            double b = Vector3d.Dot(o, d), c = Vector3d.Dot(o, o) - r * r;
            double disc = b * b - c;
            if (disc <= 0.0 || c <= 0.0) return -1.0;
            double t = -b - Math.Sqrt(disc);
            return t > 0.0 ? t : -1.0;
        }

        // The places of the picture the depth is compared at (viewport x, y from the lower left; pairs): left and right of the vessel at the middle height, and below it.
        internal static readonly float[] DepthStatPoints = { 0.25f, 0.5f, 0.75f, 0.5f, 0.5f, 0.2f };

        // seaForwardM: for each place of DepthStatPoints the distance of the sea level's sphere along the camera's forward axis (the depth there if the sea is what the
        // camera sees; -1: the ray misses the body). PRUEFPROTOKOLL 374, 375: from 130 km the decks were not drawn while their shadows lay on the sea.
        private void DepthStatsIfAsked(double cameraAltitudeM, double[] seaForwardM, string cameraInfo)
        {
            if (!ProbeDepthStats || depthTarget == null || !depthTarget.IsCreated()) return;
            ProbeDepthStats = false;
            string tag = ProbeDepthStatsTag + ", " + cameraInfo;
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
                                       + DepthStatsText(data, w, h) + DepthPointsText(data, w, h, DepthStatPoints, seaForwardM));
                }
                catch (Exception e)
                {
                    AtmospheresLog.Say("own clouds: scene depth read (" + tag + ") failed: " + e.Message);
                }
            });
        }

        // "; at (0.25, 0.50): D m, the sea's sphere G m (D / G); ...": the depth at places of the picture (viewport x, y from the lower left, pairs) against what the
        // sea level's sphere gives there along the camera's forward axis. Over the open sea the two are the same; over land the depth is shorter by what the land
        // stands above the sea. The middle of the 3 x 3 pixels round the place is taken (one pixel may be the vessel's or a tree's).
        internal static string DepthPointsText(float[] data, int w, int h, float[] points, double[] seaForwardM)
        {
            if (data == null || w <= 0 || h <= 0 || data.Length < w * h || points == null || seaForwardM == null) return "";
            CultureInfo inv = CultureInfo.InvariantCulture;
            var text = new StringBuilder(160);
            var near = new System.Collections.Generic.List<float>(9);
            for (int k = 0; k + 1 < points.Length && k / 2 < seaForwardM.Length; k += 2)
            {
                int px = Mathf.Clamp((int)(points[k] * w), 1, w - 2), py = Mathf.Clamp((int)(points[k + 1] * h), 1, h - 2);
                near.Clear();
                for (int y = py - 1; y <= py + 1; y++)
                    for (int x = px - 1; x <= px + 1; x++) near.Add(data[y * w + x]);
                near.Sort();
                float d = near[4];
                double sea = seaForwardM[k / 2];
                text.Append("; at (").Append(points[k].ToString("0.00", inv)).Append(", ").Append(points[k + 1].ToString("0.00", inv)).Append("): ")
                    .Append(d > 0f && d < 1e9f ? d.ToString("0", inv) + " m" : "no depth").Append(", the sea's sphere ")
                    .Append(sea > 0.0 ? sea.ToString("0", inv) + " m" : "not met");
                if (d > 0f && d < 1e9f && sea > 0.0) text.Append(" (").Append((d / sea).ToString("0.0000", inv)).Append(")");
            }
            return text.ToString();
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
