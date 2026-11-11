using System;
using System.Collections;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    // The L2 check of the sea ice (package D35): the overlay shader's passes on the player's GPU against the CPU code of the orbit
    // picture (IceHash, IceMask, ShoreGrid, linked as they are), on a synthetic planet. Reference expectation, written before the run:
    //   * the integer cell hashes of 256 lattice cells are identical on the GPU and the CPU (mismatches 0), the Worley distances (F1, F2)
    //     and the cell value agree within 1e-3 and the value noise within 1e-3 (GPU float arithmetic against the CPU's);
    //   * on a planet with one continent (land: 100 to 140 degrees east, shelf of 30 m depth and deep sea around, a coast map made by
    //     ShoreGrid.Build) and a uniform field fraction f of 0.2, 0.5 and 0.8, the shader's coverage pass over 60 degrees S to N and the
    //     CPU's IceMask.Evaluate on the same pixel centres give an ice share that differs by at most 0.01; the pixels where the two
    //     disagree are at most 2 % (bilinear texture weights are 8 bits on the GPU, the coast map is read at the same texels);
    //   * the share of ice the shader draws equals the field's fraction within 0.05 over the sea of the window, and in the coast zone
    //     (shore term at least CoastZoneShore, the same threshold as IceOrbitPicture.CoastZoneShore) within 0.05 too;
    //   * no pixel on land carries ice (the control of the sea test: the coast map's sea flag is read by the shader).
    // What this does not measure: the sheet's depth, its ordering against an ocean, shimmer under TAA, upscaler ghosting, frame
    // generation and RT cost; the player has no Scatterer ocean, a stand-in is no evidence for them (rows of series K-W3b and RECON
    // P10/P11 carry those).
    //
    //   -mode scene -scene ice-check
    [WeatherScene("ice-check")]
    internal sealed class IceCheckScene : WeatherSceneBase
    {
        private const float Radius = 600000f;
        private const float CoastZoneShore = 0.3f;
        private const int Width = 512, Height = 256;
        private const double LatMin = -60.0, LatMax = 60.0, LonMin = -180.0, LonMax = 180.0;

        public override IEnumerator Run()
        {
            yield return Frames(3);
            Shader shader = Shader.Find("ReDefinition/AtmospheresIce");
            Sink.Flag("shader_found", shader != null);
            if (shader == null) yield break;
            var material = new Material(shader);

            int mismatches;
            double worstWorley, worstNoise;
            bool ran = false;
            try
            {
                ran = IceGpu.CompareNoise(material, 256, 12345u, out mismatches, out worstWorley, out worstNoise);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.Log("D35 ice-check: noise passes failed: " + e);
                mismatches = -1;
                worstWorley = double.NaN;
                worstNoise = double.NaN;
            }
            Sink.Flag("hash_pass_ran", ran);
            Sink.AtMost("hash_mismatches", mismatches, 0.0);
            Sink.AtMost("worley_max_error", worstWorley, 0.001);
            Sink.AtMost("noise_max_error", worstNoise, 0.001);
            if (!ran) yield break;

            // The synthetic planet: one continent, a shallow shelf (30 m) in the first half of every 64 columns and deep sea elsewhere.
            var heights = new float[ShoreGrid.Width * ShoreGrid.Height];
            for (int row = 0; row < ShoreGrid.Height; row++)
                for (int col = 0; col < ShoreGrid.Width; col++)
                {
                    double lat = ((row + 0.5) / ShoreGrid.Height - 0.5) * 180.0;
                    double lon = (col + 0.5) / ShoreGrid.Width * 360.0;
                    bool land = lon >= 100.0 && lon < 140.0 && Math.Abs(lat) < 70.0;
                    heights[row * ShoreGrid.Width + col] = land ? 500f : (col % 64 < 32 ? -30f : -4000f);
                }
            byte[] shore = ShoreGrid.Build(heights, 0f, Radius);
            var shoreTex = new Texture2D(ShoreGrid.Width, ShoreGrid.Height, TextureFormat.RGBA32, false, true)
                { filterMode = FilterMode.Bilinear, wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp };
            shoreTex.LoadRawTextureData(shore);
            shoreTex.Apply(false, false);
            var iceMap = new Texture2D(256, 128, TextureFormat.RGBA32, false, true)
                { filterMode = FilterMode.Bilinear, wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp };

            var p = new IceParams { RadiusM = Radius };
            // The frame of the synthetic planet: longitude 0 on +z, east toward +x, the pole on +y (the layout of the coast map).
            material.SetVector("_FrameA", new Vector4(0f, 0f, 1f, 0f));
            material.SetVector("_FrameB", new Vector4(1f, 0f, 0f, 0f));
            material.SetVector("_FrameP", new Vector4(0f, 1f, 0f, 0f));
            material.SetTexture("_ShoreMap", shoreTex);
            material.SetFloat("_Freq", p.Frequency);
            material.SetVector("_Drift", Vector4.zero);
            material.SetFloat("_SeedLo", p.Seed & 0xFFFFu);
            material.SetFloat("_SeedHi", p.Seed >> 16);
            material.SetFloat("_LeadFraction", p.LeadFraction);
            material.SetFloat("_ShoreGain", p.ShoreGain);
            material.SetFloat("_EdgeNoise", p.EdgeNoise);
            material.SetFloat("_Permanent", 0f);
            material.SetFloat("_PermanentThickness", p.PermanentThicknessM);
            material.SetFloat("_Forced", -1f);
            material.SetVector("_Window", new Vector4((float)LatMin, (float)LatMax, (float)LonMin, (float)LonMax));

            foreach (float f in new[] { 0.2f, 0.5f, 0.8f })
            {
                var bytes = new byte[256 * 128 * 4];
                for (int i = 0; i < 256 * 128; i++)
                {
                    bytes[i * 4] = (byte)(f * 255f + 0.5f);
                    bytes[i * 4 + 3] = 255;
                }
                iceMap.LoadRawTextureData(bytes);
                iceMap.Apply(false, false);
                material.SetTexture("_IceMap", iceMap);
                float field = (byte)(f * 255f + 0.5f) / 255f;

                Color[] gpu = IceGpu.Run(material, IceGpu.CoveragePass, Width, Height);
                int sea = 0, gpuIce = 0, cpuIce = 0, differ = 0, landIce = 0, coastSea = 0, coastGpuIce = 0, coastCpuIce = 0;
                for (int y = 0; y < Height; y++)
                {
                    double lat = LatMin + (LatMax - LatMin) * (y + 0.5) / Height;
                    double latRad = lat * Math.PI / 180.0;
                    for (int x = 0; x < Width; x++)
                    {
                        double lon = LonMin + (LonMax - LonMin) * (x + 0.5) / Width;
                        double lonRad = lon * Math.PI / 180.0;
                        double c = Math.Cos(latRad);
                        double dx = c * Math.Sin(lonRad), dy = Math.Sin(latRad), dz = c * Math.Cos(lonRad);
                        double u, v;
                        ShoreGrid.Uv(dx, dy, dz, out u, out v);
                        bool cpuSea = ShoreGrid.Sample(shore, 2, u, v) > 0.5f;
                        Color g = gpu[y * Width + x];
                        bool gpuSea = g.b > 0.5f, gpuIsIce = g.r > 0.5f;
                        if (!cpuSea)
                        {
                            if (gpuIsIce) landIce++;
                            continue;
                        }
                        float shoreTerm = ShoreGrid.Sample(shore, 0, u, v), shoreMean = ShoreGrid.Sample(shore, 1, u, v);
                        IceState s = IceMask.Evaluate(p, (float)dx, (float)dy, (float)dz, field, 0f, shoreTerm, shoreMean, 0f, 0f, 0f);
                        sea++;
                        if (gpuIsIce) gpuIce++;
                        if (s.Ice) cpuIce++;
                        if (gpuIsIce != s.Ice || gpuSea != cpuSea) differ++;
                        if (shoreTerm >= CoastZoneShore)
                        {
                            coastSea++;
                            if (gpuIsIce) coastGpuIce++;
                            if (s.Ice) coastCpuIce++;
                        }
                    }
                }
                string tag = "f" + (int)(f * 100f) + ".";
                Sink.Info(tag + "sea_pixels", sea);
                Sink.AtLeast(tag + "sea_pixels_enough", sea, 20000.0);
                double gpuShare = sea > 0 ? gpuIce / (double)sea : double.NaN;
                double cpuShare = sea > 0 ? cpuIce / (double)sea : double.NaN;
                Sink.Info(tag + "gpu_share", gpuShare);
                Sink.Info(tag + "cpu_share", cpuShare);
                Sink.AtMost(tag + "gpu_cpu_share_difference", Math.Abs(gpuShare - cpuShare), 0.01);
                Sink.AtMost(tag + "differing_pixels", sea > 0 ? differ / (double)sea : double.NaN, 0.02);
                Sink.AtMost(tag + "gpu_field_error", Math.Abs(gpuShare - field), 0.05);
                Sink.AtMost(tag + "ice_pixels_on_land", landIce, 0.0);
                Sink.AtLeast(tag + "coast_pixels_enough", coastSea, 2000.0);
                if (coastSea > 0)
                {
                    Sink.AtMost(tag + "coast_gpu_field_error", Math.Abs(coastGpuIce / (double)coastSea - field), 0.05);
                    Sink.AtMost(tag + "coast_cpu_field_error", Math.Abs(coastCpuIce / (double)coastSea - field), 0.05);
                }
                else Sink.Flag(tag + "coast_zone_present", false);
                yield return Frames(1);
            }
            UnityEngine.Object.Destroy(shoreTex);
            UnityEngine.Object.Destroy(iceMap);
            UnityEngine.Object.Destroy(material);
        }
    }
}
