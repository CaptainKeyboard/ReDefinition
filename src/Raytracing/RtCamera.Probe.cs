using System;
using System.Collections.Generic;
using ReDefinition.Core;
using ReDefinition.Proxy;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Raytracing
{
    // The part of RtCamera that the probes use: the parity check's read-back and the surface images they write.
    internal sealed partial class RtCamera
    {
        // A render texture's pixels as four floats each, read at once (the probe's); null where it cannot be.
        private static float[] ReadFloats(RenderTexture texture)
        {
            if (texture == null || !texture.IsCreated()) return null;
            RenderTexture before = RenderTexture.active;
            var read = new Texture2D(texture.width, texture.height, TextureFormat.RGBAFloat, false, true);
            try
            {
                RenderTexture.active = texture;
                read.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
                return read.GetRawTextureData<float>().ToArray();
            }
            finally
            {
                RenderTexture.active = before;
                Destroy(read);
            }
        }

        // The primary check at the next frame, for the pose `label` (the views probe): the report follows in the log.
        internal void ParityNow(string label)
        {
            parityLabel = label;
            parityAt = float.NegativeInfinity;
        }

        // The primary check's output read back (-redefinitionProbeParity): the log says where the rays' scene and
        // the picture disagree, and the picture of it goes into ReDefinitionCaptures.
        private void ParityRead(AsyncGPUReadbackRequest request)
        {
            parityPending = false;
            if (request.hasError || parity == null) return;
            int w = parity.width, h = parity.height;
            float[] data = request.GetData<float>().ToArray();
            RtParity.Result result = RtParity.Analyse(data, w, h, Scene.IsTerrain);
            if (parityLabel != null)
            {
                Light sun = SunLight != null ? SunLight() : null;
                Debug.Log(Core.Log.Tag + " Raytracing parity of view " + parityLabel + ": the sun's light " + (sun != null ? sun.color + " x " + sun.intensity : "none")
                          + ", the rays' sun " + SunColor() + " (drawn " + drawnSunColour + " in frame " + drawnSunFrame + " of " + Time.frameCount + "), the rays' sky " + SkyColor() + ", Unity's ambient sky " + RenderSettings.ambientSkyColor + " equator "
                          + RenderSettings.ambientEquatorColor + " ground " + RenderSettings.ambientGroundColor + " flat " + RenderSettings.ambientLight + ".");
            }
            parityLabel = null;
            Debug.Log(Core.Log.Tag + " " + RtParity.Report(result, Scene.Describe, 12));
            float[] rays = ReadFloats(parityRays), raster = ReadFloats(parityRaster);
            if (rays != null && raster != null)
                Debug.Log(Core.Log.Tag + " " + RtParity.LookReport(RtParity.Looks(data, rays, raster, w, h), Scene.Describe, 12));
            string folder = System.IO.Path.Combine(Core.CaptureFolder.Root, "parity");
            System.IO.Directory.CreateDirectory(folder);
            var picture = new Texture2D(w, h, TextureFormat.RGBA32, false);
            picture.LoadRawTextureData(RtParity.Image(data, w, h, Scene.IsTerrain));
            picture.Apply();
            string file = System.IO.Path.Combine(folder, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + HighLogic.LoadedScene + ".png");
            System.IO.File.WriteAllBytes(file, picture.EncodeToPNG());
            Destroy(picture);
            Debug.Log(Core.Log.Tag + " Raytracing parity picture: " + file);
        }

        // For the scene probe (-redefinitionProbeSurfaces). It writes this frame's G-buffer copies,
        // the surfaces as their materials mean them with their modes and what the camera's
        // ray met (RT_REGION_*), the raytraced diffuse and reflected light, and the sun's
        // visibility. Each goes into <prefix>-<name>.bin in folder. A file holds the width and height as two
        // little-endian ints, then four floats a pixel, rows from the bottom. Argument `step` keeps every
        // step-th pixel of every step-th row. Argument `only` names the images to write, or all. It returns the names
        // written (tools/surface_sheet.py reads them).
        internal string WriteSurfaceImages(string folder, string prefix, int step = 1, string[] only = null)
        {
            step = Math.Max(1, step);
            var written = new System.Text.StringBuilder();
            var named = new[]
            {
                new KeyValuePair<string, RenderTexture>("gbuffer0", gbuffer0), new KeyValuePair<string, RenderTexture>("gbuffer1", gbuffer1),
                new KeyValuePair<string, RenderTexture>("surface", surface), new KeyValuePair<string, RenderTexture>("surfacemode", surfaceMode),
                new KeyValuePair<string, RenderTexture>("diffuse", diffuse), new KeyValuePair<string, RenderTexture>("sun", sun),
                new KeyValuePair<string, RenderTexture>("specular", specular), new KeyValuePair<string, RenderTexture>("gbuffer2", gbuffer2),
            };
            RenderTexture before = RenderTexture.active;
            foreach (KeyValuePair<string, RenderTexture> pair in named)
            {
                RenderTexture texture = pair.Value;
                if (texture == null || !texture.IsCreated()) continue;
                if (only != null && Array.IndexOf(only, pair.Key) < 0) continue;
                var read = new Texture2D(texture.width, texture.height, TextureFormat.RGBAFloat, false, true);
                try
                {
                    RenderTexture.active = texture;
                    read.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
                    Color[] pixels = read.GetPixels();
                    int width = (texture.width + step - 1) / step, height = (texture.height + step - 1) / step;
                    using (var file = new System.IO.BinaryWriter(System.IO.File.Create(System.IO.Path.Combine(folder, prefix + "-" + pair.Key + ".bin"))))
                    {
                        file.Write(width);
                        file.Write(height);
                        for (int y = 0; y < texture.height; y += step)
                            for (int x = 0; x < texture.width; x += step)
                            {
                                Color c = pixels[y * texture.width + x];
                                file.Write(c.r);
                                file.Write(c.g);
                                file.Write(c.b);
                                file.Write(c.a);
                            }
                    }
                    written.Append(pair.Key).Append(' ');
                }
                finally
                {
                    RenderTexture.active = before;
                    Destroy(read);
                }
            }
            return written.ToString().TrimEnd();
        }
    }
}
