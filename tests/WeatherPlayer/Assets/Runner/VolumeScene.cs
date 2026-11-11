using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // The scene of the volume pass (package C21): the pass in the fake world with a deep convective cell from the window resampler,
    // checked and measured in one run (-mode scene -scene volume). It writes the metric lines of the series:
    //
    //   determinism and resample    the window is a function of its inputs, the sub-grid cores stay 5 to 25 km, the rebuild's CPU time
    //   views and A/B               the views V1 to V6 of the verification table with the pass on and off; luma_ratio, base_darkness,
    //                               diff_vs_clear, edge_contrast; the share of the on/off difference that lies in the expected region
    //   half-resolution A/B         the same view marched at full, half, quarter and vertical-half size: luma delta, edge contrast
    //                               ratio and structure error against full size
    //   allocations                 managed bytes allocated by VolumeRenderer.Prepare over 300 frames
    //   contact sheet               sheet.png in the output folder
    //
    // The cost table is volume-cost, the temporal runs are the player's turn mode with -volume 1 (tools/weather_metrics/volume.py).
    [WeatherScene("volume")]
    public sealed class VolumeScene : WeatherSceneBase
    {
        internal const float LowSunElevationDeg = 10f, NoonElevationDeg = 62f;

        private VolumeSetup setup;
        private CellSettings cell;
        private CurtainPass curtain;
        private float coreX, coreZ, coreRadius, coreBase, coreDepth;
        private bool curtainReady;
        private readonly Action<CommandBuffer> markStart = cb => WeatherGpuTimer.Mark(cb, WeatherGpuTimer.SlabStart);

        protected override void BuildContent()
        {
            cell = new CellSettings();
            setup = new VolumeSetup();
            if (!setup.Init("convective", VolumeSetup.TierArgument()))
            {
                Sink.Flag("volume_ready", false);
                UnityEngine.Debug.Log("WEATHERPLAYER FAIL  " + setup.Problem);
                return;
            }
            setup.Renderer.Attach(Cam, CameraEvent.AfterSkybox);
            float east, north, radius, baseAlt, depth, updraft;
            setup.Resampler.GetCore(0, out east, out north, out radius, out baseAlt, out depth, out updraft);
            coreX = east;
            coreZ = north;
            coreRadius = radius;
            coreBase = baseAlt;
            coreDepth = depth;
            curtain = new CurtainPass();
            Shader curtainShader = Shader.Find("ReDefinition/AtmospheresStormVolume");
            curtainReady = curtainShader != null && curtain.Init(curtainShader, null, 0);
        }

        // The ground height under a place of the fake world.
        private static float Ground(float x, float z)
        {
            return TerrainHeight(x, z);
        }

        // Records the frame's passes: the volume on or off, the curtain on or off, at the camera's present pose.
        private void PrepareFrame(bool volumeOn, bool curtainOn, Camera camera)
        {
            if (volumeOn)
                setup.Prepare(camera, cell, FrameIndex, ClockSeconds, Vector2.zero, 0, markStart, null, null, false);
            else
                setup.Renderer.Commands.Clear();
            if (curtainReady)
            {
                if (curtainOn)
                {
                    float x = coreX, z = coreZ, ground = Ground(x, z);
                    curtain.SetShare(1f, true);
                    curtain.Place(0, new Vector3(x, ground, z), Vector3.up, Vector3.right, coreBase - ground, coreRadius * CurtainPass.RadiusShare, 0f, 600000.0);
                    curtain.HideFrom(1);
                    curtain.Light(cell.TowardsSun, cell.SunColour * 0.9f, new Color(0.72f, 0.76f, 0.82f, 1f), (float)ClockSeconds, 20f, 80000f, 0);
                    curtain.CameraDepth(camera);
                }
                else
                {
                    curtain.HideAll();
                }
            }
        }

        private IEnumerator Settle(bool volumeOn, bool curtainOn, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                PrepareFrame(volumeOn, curtainOn, Cam);
                yield return Frames(1);
            }
        }

        private IEnumerator Grab(Action<LumaFrame> done)
        {
            yield return new WaitForEndOfFrame();
            done(LumaFrame.Read(Target));
        }

        private void SetSun(float elevationDeg, float azimuthDeg)
        {
            float e = elevationDeg * Mathf.Deg2Rad, a = azimuthDeg * Mathf.Deg2Rad;
            cell.TowardsSun = new Vector3(Mathf.Sin(a) * Mathf.Cos(e), Mathf.Sin(e), Mathf.Cos(a) * Mathf.Cos(e));
            Sun.transform.rotation = Quaternion.LookRotation(-cell.TowardsSun);
        }

        private struct View
        {
            public string Name;
            public CameraPose Pose;
            public float SunElevation, SunAzimuth;
            // The screen region where the cloud is expected.
            public bool Expect;
        }

        // The views of the verification table, relative to the core's place.
        private List<View> Views()
        {
            float cx = coreX, cz = coreZ;
            var views = new List<View>();
            // V1: at the ground (2 m) and at 2 km altitude, looking at the cell from 25 km.
            views.Add(new View { Name = "V1a", Pose = new CameraPose(new Vector3(cx, Ground(cx, cz - 25000f) + 2f, cz - 25000f), 0f, -7f), SunElevation = 38f, SunAzimuth = 200f, Expect = true });
            views.Add(new View { Name = "V1b", Pose = new CameraPose(new Vector3(cx, 2000f, cz - 25000f), 0f, -3f), SunElevation = 38f, SunAzimuth = 200f, Expect = true });
            // V2: a side view from 8 km at 500 m altitude.
            views.Add(new View { Name = "V2", Pose = new CameraPose(new Vector3(cx, 500f, cz - 8000f), 0f, -11f), SunElevation = 38f, SunAzimuth = 200f, Expect = true });
            // V3: inside the cell at 300 m (under its base, in the curtain).
            views.Add(new View { Name = "V3", Pose = new CameraPose(new Vector3(cx, Ground(cx, cz) + 300f, cz), 0f, 0f), SunElevation = 38f, SunAzimuth = 200f, Expect = false });
            // V4: from below the deck looking up: the deck only.
            views.Add(new View { Name = "V4", Pose = new CameraPose(new Vector3(cx, Ground(cx, cz) + 50f, cz), 0f, -75f), SunElevation = 38f, SunAzimuth = 200f, Expect = false });
            // V5: from 10 km altitude looking down at the top.
            views.Add(new View { Name = "V5", Pose = new CameraPose(new Vector3(cx, 10500f, cz - 9000f), 0f, 38f), SunElevation = 38f, SunAzimuth = 200f, Expect = true });
            // V6: the sun at 10 degrees from the side and at noon, from the 25 km view at 2 km.
            views.Add(new View { Name = "V6low", Pose = new CameraPose(new Vector3(cx, 2000f, cz - 25000f), 0f, -3f), SunElevation = LowSunElevationDeg, SunAzimuth = 90f, Expect = true });
            views.Add(new View { Name = "V6noon", Pose = new CameraPose(new Vector3(cx, 2000f, cz - 25000f), 0f, -3f), SunElevation = NoonElevationDeg, SunAzimuth = 90f, Expect = true });
            return views;
        }

        // The cell's expected screen region: the projection of the box of the core and its anvil.
        private ImageStats.Rect2 ExpectedRect(int width, int height)
        {
            float r = coreRadius * setup.Look.AnvilSpread, top = coreBase + coreDepth;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var p = new Vector3(coreX + ((i & 1) == 0 ? -r : r), (i & 2) == 0 ? coreBase : top, coreZ + ((i & 4) == 0 ? -r : r));
                Vector3 s = Cam.WorldToScreenPoint(p);
                // A corner behind the camera: the camera is under or inside the box, which then covers the whole frame.
                if (s.z <= 0f) return new ImageStats.Rect2(0, 0, width, height);
                minX = Math.Min(minX, s.x);
                maxX = Math.Max(maxX, s.x);
                minY = Math.Min(minY, s.y);
                maxY = Math.Max(maxY, s.y);
            }
            if (minX > maxX) return new ImageStats.Rect2(0, 0, 0, 0);
            return new ImageStats.Rect2(Math.Max(0, (int)minX), Math.Max(0, (int)minY), Math.Min(width, (int)maxX), Math.Min(height, (int)maxY));
        }

        private static double MeanAbsDiff(LumaFrame a, LumaFrame b, ImageStats.Rect2 r)
        {
            double sum = 0;
            int n = 0;
            for (int y = r.Y0; y < r.Y1; y++)
                for (int x = r.X0; x < r.X1; x++)
                {
                    sum += Math.Abs(a.Y[y * a.Width + x] - b.Y[y * b.Width + x]);
                    n++;
                }
            return n > 0 ? sum / n : 0;
        }

        // The share of the pixels whose luma differs by more than the threshold between the frames that lie inside the rectangle.
        private static double DifferenceInside(LumaFrame on, LumaFrame off, ImageStats.Rect2 r, float threshold, out int total)
        {
            int inside = 0;
            total = 0;
            for (int y = 0; y < on.Height; y++)
                for (int x = 0; x < on.Width; x++)
                {
                    if (Math.Abs(on.Y[y * on.Width + x] - off.Y[y * off.Width + x]) <= threshold) continue;
                    total++;
                    if (x >= r.X0 && x < r.X1 && y >= r.Y0 && y < r.Y1) inside++;
                }
            return total > 0 ? inside / (double)total : double.NaN;
        }

        // The mean luma of the lowest share of the rectangle's rows (row 0 is the bottom).
        private static ImageStats.Rect2 LowPart(ImageStats.Rect2 r, float share)
        {
            int h = Math.Max(1, (int)((r.Y1 - r.Y0) * share));
            return new ImageStats.Rect2(r.X0, r.Y0, r.X1, r.Y0 + h);
        }

        // The edge: the mean outside the box (to the left and the right of it, as wide as the box) minus the mean inside, over the outside.
        private static double Edge(LumaFrame f, ImageStats.Rect2 r)
        {
            // Across the box's lower edge (row Y0, the cloud's base on the screen): a strip of 6 % of the frame below against one above.
            int strip = Math.Max(2, f.Height / 16);
            var below = new ImageStats.Rect2(r.X0, Math.Max(0, r.Y0 - strip), r.X1, r.Y0);
            var above = new ImageStats.Rect2(r.X0, r.Y0, r.X1, Math.Min(f.Height, r.Y0 + strip));
            if (below.Area <= 0 || above.Area <= 0)
            {
                // The lower edge is off the frame: use the box's left and right edges instead.
                int w = Math.Max(2, f.Width / 16);
                below = new ImageStats.Rect2(Math.Max(0, r.X0 - w), r.Y0, r.X0, r.Y1);
                above = new ImageStats.Rect2(r.X0, r.Y0, Math.Min(f.Width, r.X0 + w), r.Y1);
                if (below.Area <= 0 || above.Area <= 0) return 0.0;
            }
            double outside = ImageStats.MeanLuma(f, below), inside = ImageStats.MeanLuma(f, above);
            return outside > 1e-6 ? Math.Abs(outside - inside) / outside : 0.0;
        }

        // The structure error between two frames over a region: the RMS of the difference of their high-pass parts (frame minus its box
        // blur), over the RMS of the first's high-pass part.
        private static double StructureError(LumaFrame a, LumaFrame b, ImageStats.Rect2 r)
        {
            float[] blurA = ImageStats.BoxBlur(a, 4), blurB = ImageStats.BoxBlur(b, 4);
            double diff = 0, reference = 0;
            int n = 0;
            for (int y = r.Y0; y < r.Y1; y++)
                for (int x = r.X0; x < r.X1; x++)
                {
                    int i = y * a.Width + x;
                    float ha = a.Y[i] - blurA[i], hb = b.Y[i] - blurB[i];
                    diff += (ha - hb) * (ha - hb);
                    reference += ha * ha;
                    n++;
                }
            return n > 0 && reference > 0 ? Math.Sqrt(diff / reference) : 0.0;
        }

        private static string Fixed(double v)
        {
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }

        public override IEnumerator Run()
        {
            if (setup == null || setup.Problem != null)
            {
                Sink.Flag("volume_ready", false);
                yield break;
            }
            Sink.Flag("volume_ready", true);
            Sink.Flag("curtain_ready", curtainReady);

            // --- determinism and the rebuild ---
            byte[] first = (byte[])setup.Resampler.Data.Clone();
            setup.Rebuild(0.0, 1f);
            bool same = true;
            for (int i = 0; i < first.Length; i++) if (first[i] != setup.Resampler.Data[i]) { same = false; break; }
            Sink.Flag("window_deterministic", same);
            Sink.Info("resample_ms_total", setup.ResampleMs);
            Sink.AtMost("resample_ms_per_frame", setup.ResampleMs / setup.Tier.SpreadFrames, 2.0);
            Sink.Info("core_radius_km", coreRadius / 1000.0);
            Sink.AtLeast("core_radius_min_ok", coreRadius / 1000.0, 5.0);
            Sink.AtMost("core_radius_max_ok", coreRadius / 1000.0, 25.0);
            Sink.Info("core_count", setup.Resampler.CoreCount);

            // --- the views, pass on and off ---
            var sheet = new List<Color[]>();
            var sheetNames = new List<string>();
            foreach (View v in Views())
            {
                Pose(v.Pose);
                SetSun(v.SunElevation, v.SunAzimuth);
                LumaFrame off = null, on = null;
                yield return Settle(false, false, 4);
                yield return Grab(f => off = f);
                yield return Settle(true, true, 4);
                yield return Grab(f => on = f);
                ImageStats.Rect2 rect = ExpectedRect(on.Width, on.Height);
                ImageStats.Rect2 region = rect.Area > 0 ? rect : ImageStats.Rect2.Whole(on);
                double clearMean = ImageStats.MeanLuma(off, region), onMean = ImageStats.MeanLuma(on, region);
                string tag = v.Name;
                Sink.Info("luma_ratio." + tag, clearMean > 1e-6 ? onMean / clearMean : double.NaN);
                ImageStats.Rect2 low = LowPart(region, 0.35f);
                double lowClear = ImageStats.MeanLuma(off, low), lowOn = ImageStats.MeanLuma(on, low);
                Sink.Info("base_darkness." + tag, lowClear > 1e-6 ? 1.0 - lowOn / lowClear : double.NaN);
                Sink.Info("diff_vs_clear." + tag, clearMean > 1e-6 ? MeanAbsDiff(on, off, region) / clearMean : double.NaN);
                if (v.Expect) Sink.Info("edge_contrast." + tag, Edge(on, region) - Edge(off, region));
                if (v.Expect)
                {
                    int total;
                    double inside = DifferenceInside(on, off, region, 0.02f, out total);
                    Sink.Info("diff_pixels." + tag, total);
                    // The pass changes pixels, and almost all of the change lies in the cell's region.
                    Sink.AtLeast("contrib_overlap." + tag, inside, 0.8);
                }
                else
                {
                    // Below the deck and inside: the picture is a dark deck, not a bright structure.
                    Sink.Info("mean_luma_on." + tag, ImageStats.MeanLuma(on, ImageStats.Rect2.Whole(on)));
                }
                sheetNames.Add(tag);
                sheet.Add(null);
                yield return CaptureColour(tag, sheet, sheet.Count - 1);
            }

            // --- slab alone against slab and curtain at the inside view ---
            View inside_ = Views()[3];
            Pose(inside_.Pose);
            SetSun(inside_.SunElevation, inside_.SunAzimuth);
            LumaFrame slabOnly = null, both = null, clearInside = null;
            yield return Settle(false, false, 4);
            yield return Grab(f => clearInside = f);
            yield return Settle(true, false, 4);
            yield return Grab(f => slabOnly = f);
            yield return Settle(true, true, 4);
            yield return Grab(f => both = f);
            // The curtain alone against the clear frame at the inside view (the curtain must change the picture).
            LumaFrame curtainOnly = null;
            yield return Settle(false, true, 4);
            yield return Grab(f => curtainOnly = f);
            Sink.Info("curtain_only_diff.V3", MeanAbsDiff(curtainOnly, clearInside, ImageStats.Rect2.Whole(both)) / Math.Max(1e-6, ImageStats.MeanLuma(clearInside, ImageStats.Rect2.Whole(both))));
            Sink.Info("curtain_pieces_shown", curtain.Shown);
            Sink.Info("curtain_steps", curtain.Steps);
            Sink.Info("slab_vs_curtain_diff.V3",MeanAbsDiff(slabOnly, both, ImageStats.Rect2.Whole(both)) / Math.Max(1e-6, ImageStats.MeanLuma(clearInside, ImageStats.Rect2.Whole(both))));

            // --- half-resolution A/B at the 25 km view ---
            View far = Views()[1];
            Pose(far.Pose);
            SetSun(far.SunElevation, far.SunAzimuth);
            LumaFrame full = null;
            setup.SetOverride("volume.resolution", 1f);
            yield return Settle(true, true, 4);
            yield return Grab(f => full = f);
            ImageStats.Rect2 farRect = ExpectedRect(full.Width, full.Height);
            ImageStats.Rect2 farRegion = farRect.Area > 0 ? farRect : ImageStats.Rect2.Whole(full);
            double fullMean = ImageStats.MeanLuma(full, farRegion), fullEdge = Edge(full, farRegion);
            foreach (string variant in new[] { "half", "quarter", "verthalf" })
            {
                setup.SetOverride("volume.resolution", variant == "quarter" ? 0.25f : variant == "half" ? 0.5f : 1f);
                setup.SetOverride("volume.vertHalf", variant == "verthalf" ? 1f : 0f);
                LumaFrame other = null;
                yield return Settle(true, true, 4);
                yield return Grab(f => other = f);
                Sink.Info("res_luma_delta." + variant, Math.Abs(ImageStats.MeanLuma(other, farRegion) - fullMean) / Math.Max(1e-6, fullMean));
                Sink.Info("res_edge_ratio." + variant, fullEdge > 1e-6 ? Edge(other, farRegion) / fullEdge : double.NaN);
                Sink.Info("res_structure_error." + variant, StructureError(full, other, farRegion));
                Sink.Info("res_diff." + variant, MeanAbsDiff(full, other, farRegion) / Math.Max(1e-6, fullMean));
            }
            setup.SetOverride("volume.resolution", 1f);
            setup.SetOverride("volume.vertHalf", 0f);

            // --- allocations of the per-frame preparation ---
            yield return Settle(true, true, 10);
            long before = GC.GetTotalMemory(false);
            for (int i = 0; i < 300; i++) setup.Prepare(Cam, cell, FrameIndex + i, ClockSeconds + i / 60.0, Vector2.zero, i & 7, markStart, null, null, false);
            long after = GC.GetTotalMemory(false);
            Sink.AtMost("prepare_alloc_bytes_per_frame", Math.Max(0L, after - before) / 300.0, 8.0);

            WriteSheet(sheet, sheetNames);
            yield return null;
        }

        // A colour capture of the frame for the contact sheet, downscaled to 640 x 360.
        private IEnumerator CaptureColour(string tag, List<Color[]> sheet, int index)
        {
            yield return new WaitForEndOfFrame();
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = Target;
            var copy = new Texture2D(Target.width, Target.height, TextureFormat.RGBAHalf, false, true);
            copy.ReadPixels(new Rect(0, 0, Target.width, Target.height), 0, 0);
            copy.Apply(false);
            RenderTexture.active = previous;
            Color[] px = copy.GetPixels();
            UnityEngine.Object.Destroy(copy);
            int w = Target.width / 2, h = Target.height / 2;
            var small = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    Color c = (px[(2 * y) * Target.width + 2 * x] + px[(2 * y) * Target.width + 2 * x + 1] + px[(2 * y + 1) * Target.width + 2 * x]
                               + px[(2 * y + 1) * Target.width + 2 * x + 1]) * 0.25f;
                    small[y * w + x] = c;
                }
            sheet[index] = small;
        }

        // The sheet: the views in rows of three, display-encoded.
        private void WriteSheet(List<Color[]> frames, List<string> names)
        {
            int w = Target.width / 2, h = Target.height / 2, columns = 3, rows = (frames.Count + columns - 1) / columns;
            var texture = new Texture2D(w * columns, h * rows, TextureFormat.RGB24, false);
            var pixels = new Color32[w * columns * h * rows];
            for (int f = 0; f < frames.Count; f++)
            {
                int ox = (f % columns) * w, oy = (rows - 1 - f / columns) * h;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        Color c = frames[f][y * w + x];
                        pixels[(oy + y) * w * columns + ox + x] = new Color32(Encode(c.r), Encode(c.g), Encode(c.b), 255);
                    }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false);
            File.WriteAllBytes(Path.Combine(Output, "sheet.png"), texture.EncodeToPNG());
            File.WriteAllText(Path.Combine(Output, "sheet.txt"), string.Join(",", names.ToArray()));
            UnityEngine.Object.Destroy(texture);
        }

        private static byte Encode(float linear)
        {
            float v = Mathf.Clamp01(linear);
            v = v <= 0.0031308f ? v * 12.92f : 1.055f * Mathf.Pow(v, 1f / 2.4f) - 0.055f;
            return (byte)Mathf.RoundToInt(v * 255f);
        }
    }

    // The cost table of the volume pass (-mode scene -scene volume-cost, a timing run: the lock is exclusive and the proxy gives the GPU
    // timestamps): ms of the march and the composite at 1707x960 and 2560x1440, with the march at half and full size and 16, 32 and 64
    // steps, and the curtain inside at 16 steps.
    [WeatherScene("volume-cost")]
    public sealed class VolumeCostScene : WeatherSceneBase
    {
        private VolumeSetup setup;
        private CellSettings cell;
        private CurtainPass curtain;
        private bool curtainReady;
        private float coreX, coreZ, coreRadius, coreBase, coreDepth;
        private CommandBuffer curtainStart, curtainEnd;
        private readonly Action<CommandBuffer> markStart = cb => WeatherGpuTimer.Mark(cb, WeatherGpuTimer.SlabStart),
            markMarched = cb => WeatherGpuTimer.Mark(cb, WeatherGpuTimer.SlabMarched),
            markEnd = cb => WeatherGpuTimer.Mark(cb, WeatherGpuTimer.SlabComposited);

        protected override void BuildContent()
        {
            cell = new CellSettings();
            setup = new VolumeSetup();
            if (!setup.Init("convective", VolumeSetup.TierArgument())) return;
            setup.Renderer.Attach(Cam, CameraEvent.AfterSkybox);
            float east, north, radius, baseAlt, depth, updraft;
            setup.Resampler.GetCore(0, out east, out north, out radius, out baseAlt, out depth, out updraft);
            coreX = east; coreZ = north; coreRadius = radius; coreBase = baseAlt; coreDepth = depth;
            curtain = new CurtainPass();
            Shader curtainShader = Shader.Find("ReDefinition/AtmospheresStormVolume");
            curtainReady = curtainShader != null && curtain.Init(curtainShader, null, 0);
            curtainStart = new CommandBuffer { name = "volume cost curtain start" };
            curtainEnd = new CommandBuffer { name = "volume cost curtain end" };
            WeatherGpuTimer.Mark(curtainStart, WeatherGpuTimer.CurtainStart);
            WeatherGpuTimer.Mark(curtainEnd, WeatherGpuTimer.CurtainEnd);
            Cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, curtainStart);
            Cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, curtainEnd);
        }

        private void PrepareFrame(bool volumeOn, bool curtainOn)
        {
            if (volumeOn) setup.Prepare(Cam, cell, FrameIndex, ClockSeconds, Vector2.zero, 0, markStart, markMarched, markEnd, false);
            else setup.Renderer.Commands.Clear();
            if (!curtainReady) return;
            if (curtainOn)
            {
                float ground = TerrainHeight(coreX, coreZ);
                curtain.SetShare(1f, true);
                curtain.Place(0, new Vector3(coreX, ground, coreZ), Vector3.up, Vector3.right, coreBase - ground, coreRadius * CurtainPass.RadiusShare, 0f, 600000.0);
                curtain.HideFrom(1);
                curtain.Light(cell.TowardsSun, cell.SunColour * 0.9f, new Color(0.72f, 0.76f, 0.82f, 1f), (float)ClockSeconds, 20f, 80000f, 0);
                curtain.CameraDepth(Cam);
            }
            else curtain.HideAll();
        }

        private static string Fixed(double v)
        {
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private IEnumerator Measure(string label, bool volumeOn, bool curtainOn, double[] result)
        {
            var segments = new float[8];
            for (int i = 0; i < 40; i++)
            {
                PrepareFrame(volumeOn, curtainOn);
                yield return Frames(1);
            }
            double frame = 0, march = 0, composite = 0, curtainMs = 0;
            int n = 0, guard = 0;
            while (n < 150 && guard++ < 2000)
            {
                PrepareFrame(volumeOn, curtainOn);
                yield return Frames(1);
                float total;
                int number;
                if (!WeatherGpuTimer.Read(segments, out total, out number)) continue;
                frame += total;
                float m = segments[WeatherGpuTimer.SlabMarched - WeatherGpuTimer.SlabStart], c = segments[WeatherGpuTimer.SlabComposited - WeatherGpuTimer.SlabStart],
                    k = segments[WeatherGpuTimer.CurtainEnd - WeatherGpuTimer.SlabStart];
                if (m >= 0f) march += m;
                if (c >= 0f) composite += c;
                if (k >= 0f) curtainMs += k;
                n++;
            }
            result[0] = n > 0 ? frame / n : double.NaN;
            result[1] = n > 0 ? march / n : double.NaN;
            result[2] = n > 0 ? composite / n : double.NaN;
            result[3] = n > 0 ? curtainMs / n : double.NaN;
            Sink.Info("cost.frame." + label, result[0]);
            if (volumeOn) Sink.Info("cost.march." + label, result[1]);
            if (volumeOn) Sink.Info("cost.composite." + label, result[2]);
            if (curtainOn) Sink.Info("cost.curtain." + label, result[3]);
        }

        public override IEnumerator Run()
        {
            if (setup == null || setup.Problem != null || !WeatherGpuTimer.Start())
            {
                Sink.Flag("cost_timers_available", false);
                yield break;
            }
            Sink.Flag("cost_timers_available", true);
            // The views for the cost: the 8 km side view (the worst of the spike for the slab seen from outside), inside the cloud at 5 km
            // altitude (the slab seen from inside) and inside the cell at 300 m (the curtain).
            var views = new[]
            {
                new KeyValuePair<string, CameraPose>("side8km", new CameraPose(new Vector3(coreX, 500f, coreZ - 8000f), 0f, -11f)),
                new KeyValuePair<string, CameraPose>("inCloud", new CameraPose(new Vector3(coreX - 3000f, 5000f, coreZ - 3000f), 0f, 0f)),
                new KeyValuePair<string, CameraPose>("far25km", new CameraPose(new Vector3(coreX, 2000f, coreZ - 25000f), 0f, -3f)),
            };
            var sizes = new[] { new Vector2Int(1707, 960), new Vector2Int(2560, 1440) };
            var result = new double[4];
            double worstHalf1707 = 0, worstHalf2560 = 0, worstFull1707 = 0, worstCurtain = 0;
            foreach (Vector2Int size in sizes)
            {
                var rt = new RenderTexture(size.x, size.y, 24, RenderTextureFormat.DefaultHDR) { name = "volume cost target" };
                rt.Create();
                Cam.targetTexture = rt;
                string sz = size.x + "x" + size.y;
                foreach (var view in views)
                {
                    Pose(view.Value);
                    // half-res 16, 32, 64 steps; full 32; quarter 32
                    foreach (var config in new[] { new[] { 0.5f, 32f }, new[] { 0.5f, 16f }, new[] { 0.5f, 64f }, new[] { 1f, 32f }, new[] { 0.25f, 32f } })
                    {
                        setup.SetOverride("volume.resolution", config[0]);
                        setup.SetOverride("volume.steps", config[1]);
                        string label = view.Key + ".r" + Fixed(config[0]) + ".s" + Fixed(config[1]) + "." + sz;
                        yield return Measure(label, true, false, result);
                        double ms = result[1] + result[2];
                        Sink.Info("cost.total." + label, ms);
                        if (config[0] == 0.5f && config[1] == 32f) { if (size.x == 1707) worstHalf1707 = Math.Max(worstHalf1707, ms); else worstHalf2560 = Math.Max(worstHalf2560, ms); }
                        if (config[0] == 1f && config[1] == 32f && size.x == 1707) worstFull1707 = Math.Max(worstFull1707, ms);
                    }
                }
                if (curtainReady && size.x == 1707)
                {
                    // The curtain inside the cell at 300 m, 16 steps (the gate share 1).
                    Pose(new CameraPose(new Vector3(coreX, TerrainHeight(coreX, coreZ) + 300f, coreZ), 0f, 0f));
                    yield return Measure("curtain.inside." + sz, false, true, result);
                    double withCurtain = result[0];
                    double marked = result[3];
                    yield return Measure("nocurtain.inside." + sz, false, false, result);
                    // The marked segment where the proxy gave one, else the frame's difference with and without the curtain.
                    worstCurtain = marked > 0.0 ? marked : withCurtain - result[0];
                    Sink.Info("cost_curtain_inside_by_frame_difference_ms", withCurtain - result[0]);
                }
                Cam.targetTexture = Target;
                rt.Release();
            }
            Sink.AtMost("cost_half_32_1707x960_ms", worstHalf1707, 0.6);
            Sink.AtMost("cost_half_32_2560x1440_ms", worstHalf2560, 1.3);
            Sink.Info("cost_full_32_1707x960_ms", worstFull1707);
            Sink.Info("cost_full_rejected", worstFull1707 > 1.5 ? 1 : 0);
            Sink.AtMost("cost_curtain_inside_ms", worstCurtain, 0.7);
        }
    }
}
