using System;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The probe's hands on the GPU for the sea ice: the shader's coverage, noise and hash passes run by Graphics.Blit into a float target
    // and read back, so that the shader can be compared with the CPU's functions without a camera (rows D35.hash_* and D35.phase.*).
    internal static class IceGpu
    {
        internal const int CoveragePass = 1;
        internal const int NoisePass = 2;
        internal const int HashPass = 3;

        // Runs a pass of the material into a target of that size and returns its pixels (row 0 at the bottom).
        internal static Color[] Run(Material material, int pass, int width, int height)
        {
            RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            rt.filterMode = FilterMode.Point;
            RenderTexture previous = RenderTexture.active;
            Texture2D read = null;
            try
            {
                Graphics.Blit(null, rt, material, pass);
                RenderTexture.active = rt;
                read = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
                read.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                return read.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                if (read != null) UnityEngine.Object.Destroy(read);
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // The table of points of the noise and hash passes: lattice-exact coordinates (multiples of 1/8) over negative and positive ranges.
        internal static Vector3[] Points(int count)
        {
            var points = new Vector3[count];
            for (int i = 0; i < count; i++)
                points[i] = new Vector3((((i * 37) % 401) - 200) / 8f, (((i * 91) % 389) - 190) / 8f, (((i * 53) % 367) - 180) / 8f);
            return points;
        }

        internal static Texture2D PointsTexture(Vector3[] points)
        {
            var tex = new Texture2D(points.Length, 1, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color[points.Length];
            for (int i = 0; i < points.Length; i++) pixels[i] = new Color(points[i].x, points[i].y, points[i].z, 0f);
            tex.SetPixels(pixels);
            tex.Apply(false, false);
            return tex;
        }

        // The worst difference between the shader and the CPU over the points: the integer hashes that differ, the float error of the
        // Worley distances and cell value, and of the value noise. Returns false where the passes could not be run.
        internal static bool CompareNoise(Material material, int count, uint seed, out int hashMismatches, out double worstWorley, out double worstNoise)
        {
            hashMismatches = 0;
            worstWorley = 0.0;
            worstNoise = 0.0;
            Vector3[] points = Points(count);
            Texture2D table = PointsTexture(points);
            try
            {
                material.SetTexture("_Points", table);
                material.SetVector("_PointsInfo", new Vector4(count, seed, 0f, 0f));
                Color[] noise = Run(material, NoisePass, count, 1);
                Color[] hash = Run(material, HashPass, count, 1);
                for (int i = 0; i < count; i++)
                {
                    Vector3 p = points[i];
                    IceHash.Worley w = IceHash.WorleyAt(p.x, p.y, p.z, seed);
                    worstWorley = Math.Max(worstWorley, Math.Max(Math.Abs(w.F1 - noise[i].r), Math.Max(Math.Abs(w.F2 - noise[i].g), Math.Abs(w.CellValue - noise[i].b))));
                    worstNoise = Math.Max(worstNoise, Math.Abs(IceHash.ValueNoise(p.x, p.y, p.z, 777u) - noise[i].a));
                    uint cpu = IceHash.Cell((int)Math.Floor(p.x), (int)Math.Floor(p.y), (int)Math.Floor(p.z), seed);
                    uint gpu = ((uint)Math.Round(hash[i].r) << 16) | (uint)Math.Round(hash[i].g);
                    if (cpu != gpu) hashMismatches++;
                }
                return true;
            }
            finally
            {
                UnityEngine.Object.Destroy(table);
            }
        }

        // The share of sea pixels with ice in a window of the shader's coverage pass, and the number of sea pixels.
        internal static void Coverage(Material material, double latMin, double latMax, double lonMin, double lonMax, int width, int height, out double iceShare, out int seaPixels)
        {
            material.SetVector("_Window", new Vector4((float)latMin, (float)latMax, (float)lonMin, (float)lonMax));
            Color[] pixels = Run(material, CoveragePass, width, height);
            double ice = 0, sea = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].b > 0.5f)
                {
                    sea += 1.0;
                    if (pixels[i].r > 0.5f) ice += 1.0;
                }
            }
            seaPixels = (int)sea;
            iceShare = sea > 0.0 ? ice / sea : 0.0;
        }
    }
}
