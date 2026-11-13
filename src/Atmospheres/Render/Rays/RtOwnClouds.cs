using System;
using System.Diagnostics;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // RtOwnClouds (package T18): the traced rays' view of the own cloud layers. The module calls Tick every frame while the own clouds draw; behind the switch
    // rtOwnClouds (default off) it keeps the ray-side pack of the stack current (RtCloudPackDriver) and the pack's texture on the GPU. It draws nothing and
    // costs the main thread the baking budget while a bake runs and the upload of the finished pack (one texture of Width x Height x layers floats).
    //
    // Hand-over to the main mod's raytracing: the consumer reads Pack and Texture. The seam that carries them over is a proposal (see
    // .local/plans/atmospheres/T18-design.md, section 5): until the main mod and the shared contract take it, nothing reads them and the rays still see only
    // EVE's layer (RtEveClouds). [VERIFY] the consumer.
    //
    // Rendering statement: no motion vectors (a texture the rays read, not a pass); no depth, no mask, no HUD-less effect; no draw order. Exposure: the pack
    // holds optical depths, no radiance; the radiance of a cloud seen in a reflection is the consumer's and goes through the same output stage as the raster
    // (CloudOutputStage).
    internal static class RtOwnClouds
    {
        private static readonly RtCloudPackDriver driver = new RtCloudPackDriver();
        private static Texture2D texture;
        private static int textureLayers;
        private static long uploadedVersion = -1;
        private static bool loggedOn;
        private static LayerStackSource tablesFor;
        private static LayerTypeTable[] tables;

        // What the series probe reads.
        internal static double LastUploadMs, WorstUploadMs;
        internal static int Uploads;

        internal static RtCloudPackDriver.Statistics Stats { get { return driver.Stats; } }

        // The pack that stands (null: none, the switch is off or the first bake has not finished).
        internal static RtCloudPack Pack { get { return driver.Current; } }

        // The pack's texture (R32G32F, Width x Height x layers rows, see RtCloudPackLayout); null while there is no pack.
        internal static Texture Texture { get { return texture; } }

        internal static bool Baking { get { return driver.Baking; } }

        // The settings now: the switch and the layer cap are player settings, the numbers tuning keys.
        internal static RtCloudPackSettings LiveSettings()
        {
            return RtCloudPackSettings.From(AtmospheresSettings.RtOwnClouds, AtmospheresSettings.RtOwnCloudsLayerCap, Tuning.Get);
        }

        // One frame of the module with its stack; `wanted` is RtOwnCloudsPlan.Wanted. Off or not wanted frees everything.
        internal static void Tick(LayerStackSource stack, bool wanted)
        {
            RtCloudPackSettings settings = LiveSettings();
            if (!wanted || stack == null || !settings.Enabled)
            {
                Release();
                return;
            }
            if (!loggedOn)
            {
                loggedOn = true;
                AtmospheresLog.Say("rtOwnClouds: on, " + settings.Width + " x " + settings.Height + " per layer, " + settings.Steps + " steps, budget "
                                   + settings.BudgetMs.ToString("0.00") + " ms");
            }
            // The type tables change only when a layer is added; the array is made again then, not every frame.
            if (!ReferenceEquals(tablesFor, stack) || tables == null || tables.Length != stack.LayerCount)
            {
                tablesFor = stack;
                tables = stack.Tables();
            }
            driver.Tick(stack, tables, settings);
            RtCloudPack pack = driver.Current;
            if (pack == null) return;
            if (pack.Version != uploadedVersion || texture == null) Upload(pack);
        }

        // Frees the pack and the texture (the switch went off, the stack or the body is gone, the scene changed).
        internal static void Release()
        {
            driver.Drop();
            if (texture != null)
            {
                UnityEngine.Object.Destroy(texture);
                texture = null;
            }
            textureLayers = 0;
            uploadedVersion = -1;
            if (loggedOn)
            {
                loggedOn = false;
                AtmospheresLog.Say("rtOwnClouds: off, pack released");
            }
        }

        private static void Upload(RtCloudPack pack)
        {
            long start = Stopwatch.GetTimestamp();
            int rows = pack.Height * pack.LayerCount;
            if (texture == null || texture.width != pack.Width || texture.height != rows || textureLayers != pack.LayerCount)
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
                texture = new Texture2D(pack.Width, rows, TextureFormat.RGFloat, false, true)
                {
                    name = "ReDefinition Atmospheres ray cloud pack",
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave
                };
                textureLayers = pack.LayerCount;
            }
            var bytes = new byte[pack.Data.Length * sizeof(float)];
            Buffer.BlockCopy(pack.Data, 0, bytes, 0, bytes.Length);
            texture.LoadRawTextureData(bytes);
            texture.Apply(false, false);
            uploadedVersion = pack.Version;
            Uploads++;
            LastUploadMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            WorstUploadMs = Math.Max(WorstUploadMs, LastUploadMs);
        }
    }
}
