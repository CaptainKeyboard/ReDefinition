using System;
using System.Diagnostics;
using ReDefinition.Core;
using Unity.Collections;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // RtOwnClouds (package T18): the traced rays' view of the own cloud layers. The module calls Tick every frame while the own clouds draw; behind the switch
    // rtOwnClouds and while the main mod's raytracing traces (RayCloudSeam.Asked) it keeps the ray-side pack of the stack current (RtCloudPackDriver), the
    // pack's texture on the GPU and the view the raytracing reads (RayCloudSeam.View). It draws nothing and costs the main thread the baking budget while a
    // bake runs and the upload of the finished pack (one texture of Width x (Height x layers + 1) texels of four floats, filled a layer slice a frame once it exists).
    //
    // Hand-over to the main mod's raytracing: RayCloudSeam.View (ReDefinition.Common) holds the texture, the map frame and the shell; the main mod's RtKsp
    // fills the packet's cloud block from it instead of EVE's layer (never both), and RtCommon.hlsli's OwnCloud* functions walk the layers by the law of
    // RtCloudRayMath. The texture's last row holds the entries' numbers (RtCloudPackLayout.WriteHeader); when only they change (the wind's map shift), that
    // row is copied in on the GPU, not the whole texture uploaded.
    //
    // Rendering statement: no motion vectors (a texture the rays read, not a pass); no depth, no mask, no HUD-less effect; no draw order. Exposure: the pack
    // holds optical depths, no radiance; the colour of a cloud seen in a reflection is the ray shader's (CloudOverSky's lit cloud).
    internal static class RtOwnClouds
    {
        private static readonly RtCloudPackDriver driver = new RtCloudPackDriver();
        private static Texture2D texture, headerTexture;
        private static int textureLayers;
        private static long uploadedVersion = -1, uploadedLiveVersion = -1;
        private static bool loggedOn, loggedPublished;
        private static LayerStackSource tablesFor;
        private static LayerTypeTable[] tables;
        private static byte[] headerBytes;
        private static float[] header, headerExpanded;

        // What the series probe reads.
        // The main-thread time of the last upload step (the whole texture, or one slice), the first (whole) upload, and the worst step since the start.
        internal static double LastUploadMs, WorstUploadMs, FirstUploadMs, WorstSliceMs;
        internal static int Uploads, HeaderCopies;
        // The series probe's switch: keep the pack but do not publish the view, so that the traced rays run without the own layers (T18Probe's cost windows).
        internal static bool HideFromRays;

        internal static RtCloudPackDriver.Statistics Stats { get { return driver.Stats; } }

        // The pack that stands (null: none, the switch is off or the first bake has not finished).
        internal static RtCloudPack Pack { get { return driver.Current; } }

        // The pack's texture (R32G32B32A32F, Width x (Height x layers + 1) rows, see RtCloudPackLayout and RayCloudLayout); null while there is no pack.
        internal static Texture Texture { get { return texture; } }

        internal static bool Baking { get { return driver.Baking; } }

        // The settings now: the switch and the layer cap are player settings, the numbers tuning keys.
        internal static RtCloudPackSettings LiveSettings()
        {
            return RtCloudPackSettings.From(AtmospheresSettings.RtOwnClouds, AtmospheresSettings.RtOwnCloudsLayerCap, Tuning.Get);
        }

        // One frame of the module with its stack; `wanted` is RtOwnCloudsPlan.Wanted. Off, not wanted or no raytracing asking frees everything.
        internal static void Tick(LayerStackSource stack, bool wanted)
        {
            RtCloudPackSettings settings = LiveSettings();
            if (!wanted || stack == null || !settings.Enabled || !RayCloudSeam.Asked(Time.frameCount))
            {
                Release();
                return;
            }
            if (!loggedOn)
            {
                loggedOn = true;
                AtmospheresLog.Say("rtOwnClouds: on, " + settings.Width + " x " + settings.Height + " per layer, " + settings.Steps + " steps, budget "
                                   + settings.BudgetMs.ToString("0.00") + " ms, " + settings.RayTaps + " ray taps");
            }
            // The type tables change only when a layer is added; the array is made again then, not every frame.
            if (!ReferenceEquals(tablesFor, stack) || tables == null || tables.Length != stack.LayerCount)
            {
                tablesFor = stack;
                tables = stack.Tables();
            }
            driver.Tick(stack, tables, settings);
            RtCloudPack pack = driver.Current;
            if (pack == null)
            {
                RayCloudSeam.Clear();
                return;
            }
            if (pack.Version != uploadedVersion || texture == null) Upload(pack);
            else if (pack.LiveVersion != uploadedLiveVersion) CopyHeader(pack);
            // The probe's cost measurement: the pack stands and stays current but the rays are not shown it (the same traced scene without the own layers).
            if (HideFromRays)
            {
                RayCloudSeam.Clear();
                return;
            }
            Publish(pack, settings);
        }

        // Frees the pack and the texture (the switch went off, the stack or the body is gone, the scene changed, the raytracing stopped).
        internal static void Release()
        {
            driver.Drop();
            RayCloudSeam.Clear();
            if (texture != null)
            {
                UnityEngine.Object.Destroy(texture);
                texture = null;
            }
            if (headerTexture != null)
            {
                UnityEngine.Object.Destroy(headerTexture);
                headerTexture = null;
            }
            if (sliceTexture != null)
            {
                UnityEngine.Object.Destroy(sliceTexture);
                sliceTexture = null;
            }
            slicePack = null;
            textureLayers = 0;
            uploadedVersion = -1;
            uploadedLiveVersion = -1;
            loggedPublished = false;
            if (loggedOn)
            {
                loggedOn = false;
                AtmospheresLog.Say("rtOwnClouds: off, pack released");
            }
        }

        // The view the raytracing reads, every frame: the frame moves with the body's rotation.
        private static void Publish(RtCloudPack pack, RtCloudPackSettings settings)
        {
            var bodies = GameGlobals.Bodies;
            if (bodies == null || pack.BodyIndex < 0 || pack.BodyIndex >= bodies.Count)
            {
                RayCloudSeam.Clear();
                return;
            }
            CelestialBody b = bodies[pack.BodyIndex];
            RayCloudView v = RayCloudSeam.View;
            double inner = double.MaxValue, outer = 0.0;
            for (int i = 0; i < pack.LayerCount; i++)
            {
                inner = Math.Min(inner, pack.Entries[i].InnerRadiusM);
                outer = Math.Max(outer, pack.Entries[i].OuterRadiusM);
            }
            v.BodyIndex = pack.BodyIndex;
            v.Texture = texture.GetNativeTexturePtr();
            v.Width = pack.Width;
            v.SliceHeight = pack.Height;
            v.LayerCount = pack.LayerCount;
            v.InnerRadiusM = (float)inner;
            v.OuterRadiusM = (float)outer;
            v.WorldToMap = OwnCloudModule.MapFrame(b);
            v.Taps = settings.RayTaps;
            v.Frame = Time.frameCount;
            if (!loggedPublished)
            {
                loggedPublished = true;
                AtmospheresLog.Say("rtOwnClouds: " + pack.LayerCount + " layers of " + b.name + " handed to the rays, shell " + (inner - pack.BodyRadiusM).ToString("0") + " to "
                                   + (outer - pack.BodyRadiusM).ToString("0") + " m");
            }
        }

        // One frame of getting `pack` into the texture. The pack's data is the texture's data (R32G32B32A32F), so it is one memory copy, not a loop over the
        // texels (the old two-floats-to-four expansion through the NativeArray indexer was most of an 11.9 ms hitch). A texture that exists (a refresh) is filled
        // one layer slice a frame through a staging texture and a GPU copy, so that no frame uploads more than a quarter of the pack; the traced rays read the
        // old and the new slices side by side for those few frames, which is a sky a few seconds apart. The first upload (no texture, or another size) is whole.
        private static void Upload(RtCloudPack pack)
        {
            long start = Stopwatch.GetTimestamp();
            int rows = pack.Height * pack.LayerCount + RayCloudLayout.HeaderRows;
            bool same = texture != null && texture.width == pack.Width && texture.height == rows && textureLayers == pack.LayerCount;
            if (same && SystemInfo.copyTextureSupport != UnityEngine.Rendering.CopyTextureSupport.None)
            {
                UploadSlice(pack);
                LastUploadMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                WorstUploadMs = Math.Max(WorstUploadMs, LastUploadMs);
                WorstSliceMs = Math.Max(WorstSliceMs, LastUploadMs);
                return;
            }
            if (texture != null) UnityEngine.Object.Destroy(texture);
            texture = new Texture2D(pack.Width, rows, TextureFormat.RGBAFloat, false, true)
            {
                name = "ReDefinition Atmospheres ray cloud pack",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
            textureLayers = pack.LayerCount;
            slicePack = null;
            int rowFloats = pack.Width * RtCloudPackLayout.HeaderChannels;
            if (header == null || header.Length != rowFloats) header = new float[rowFloats];
            RtCloudPackLayout.WriteHeader(pack, header);
            // Written into the texture's own CPU copy: no managed staging arrays (a copy of 8 MB for a four-layer pack) between the pack and the upload.
            NativeArray<float> raw = texture.GetRawTextureData<float>();
            NativeArray<float>.Copy(pack.Data, 0, raw, 0, pack.Data.Length);
            for (int i = 0; i < pack.Width * RayCloudLayout.HeaderRows; i++)
            {
                int t = pack.Data.Length + i * 4;
                raw[t] = header[i * 2];
                raw[t + 1] = header[i * 2 + 1];
                raw[t + 2] = 0f;
                raw[t + 3] = 0f;
            }
            texture.Apply(false, false);
            uploadedVersion = pack.Version;
            uploadedLiveVersion = pack.LiveVersion;
            Uploads++;
            LastUploadMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            FirstUploadMs = LastUploadMs;
            WorstUploadMs = Math.Max(WorstUploadMs, LastUploadMs);
        }

        // The pack being copied into the standing texture, and the slice it is at.
        private static RtCloudPack slicePack;
        private static int nextSlice;
        private static Texture2D sliceTexture;

        private static void UploadSlice(RtCloudPack pack)
        {
            if (!ReferenceEquals(slicePack, pack))
            {
                slicePack = pack;
                nextSlice = 0;
            }
            int floats = pack.Width * pack.Height * RtCloudPackLayout.Channels;
            if (sliceTexture == null || sliceTexture.width != pack.Width || sliceTexture.height != pack.Height)
            {
                if (sliceTexture != null) UnityEngine.Object.Destroy(sliceTexture);
                sliceTexture = new Texture2D(pack.Width, pack.Height, TextureFormat.RGBAFloat, false, true)
                {
                    name = "ReDefinition Atmospheres ray cloud slice",
                    filterMode = FilterMode.Point,
                    hideFlags = HideFlags.HideAndDontSave
                };
            }
            NativeArray<float>.Copy(pack.Data, nextSlice * floats, sliceTexture.GetRawTextureData<float>(), 0, floats);
            sliceTexture.Apply(false, false);
            Graphics.CopyTexture(sliceTexture, 0, 0, 0, 0, pack.Width, pack.Height, texture, 0, 0, 0, nextSlice * pack.Height);
            nextSlice++;
            if (nextSlice < pack.LayerCount) return;
            // The last slice is in: the header row of this pack (the live numbers with it), then the pack stands.
            slicePack = null;
            uploadedVersion = pack.Version;
            CopyHeader(pack);
            Uploads++;
        }

        // The header row alone, after the live fields moved: a one-row texture uploaded and copied into the pack's last row on the GPU. Without the
        // GPU copy the whole texture is uploaded again.
        private static void CopyHeader(RtCloudPack pack)
        {
            if (SystemInfo.copyTextureSupport == UnityEngine.Rendering.CopyTextureSupport.None)
            {
                Upload(pack);
                return;
            }
            int rowFloats = pack.Width * RtCloudPackLayout.HeaderChannels;
            if (header == null || header.Length != rowFloats) header = new float[rowFloats];
            RtCloudPackLayout.WriteHeader(pack, header);
            if (headerTexture == null || headerTexture.width != pack.Width)
            {
                if (headerTexture != null) UnityEngine.Object.Destroy(headerTexture);
                headerTexture = new Texture2D(pack.Width, RayCloudLayout.HeaderRows, TextureFormat.RGBAFloat, false, true)
                {
                    name = "ReDefinition Atmospheres ray cloud header",
                    filterMode = FilterMode.Point,
                    hideFlags = HideFlags.HideAndDontSave
                };
            }
            if (headerExpanded == null || headerExpanded.Length != rowFloats * 2) headerExpanded = new float[rowFloats * 2];
            Expand(header, 0, headerExpanded, 0, pack.Width * RayCloudLayout.HeaderRows);
            if (headerBytes == null || headerBytes.Length != headerExpanded.Length * sizeof(float)) headerBytes = new byte[headerExpanded.Length * sizeof(float)];
            Buffer.BlockCopy(headerExpanded, 0, headerBytes, 0, headerBytes.Length);
            headerTexture.LoadRawTextureData(headerBytes);
            headerTexture.Apply(false, false);
            Graphics.CopyTexture(headerTexture, 0, 0, 0, 0, pack.Width, RayCloudLayout.HeaderRows, texture, 0, 0, 0, pack.Height * pack.LayerCount);
            uploadedLiveVersion = pack.LiveVersion;
            HeaderCopies++;
        }

        // Two floats a texel into four (r, g, 0, 0): the raytracing shares its inputs between Direct3D 11 and 12, and the driver does not share
        // R32G32_FLOAT (the proxy's log: "input 7: format 16 cannot be shared", which drops the whole traced frame), R32G32B32A32_FLOAT it does.
        private static void Expand(float[] from, int fromIndex, float[] to, int toIndex, int texels)
        {
            for (int i = 0; i < texels; i++)
            {
                to[toIndex + i * 4] = from[fromIndex + i * 2];
                to[toIndex + i * 4 + 1] = from[fromIndex + i * 2 + 1];
                to[toIndex + i * 4 + 2] = 0f;
                to[toIndex + i * 4 + 3] = 0f;
            }
        }
    }
}
