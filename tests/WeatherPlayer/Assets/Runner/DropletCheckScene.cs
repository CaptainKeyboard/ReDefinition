using System;
using System.Collections;
using System.Globalization;
using System.IO;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // Checks the window droplets' own draws (DropletPass and the shader ReDefinition/AtmospheresDroplet, the classes the mod uses) without
    // the game: the pass is drawn onto an image of the scene's size the way the host hook draws it, then read back; the aid map (discs,
    // trails, beads) is a second draw of the same arithmetic.
    //
    // Reference expectations (written before the scene was built):
    //   * no drop is drawn at wetness 0 and none under the skip level (the image is identical to the source);
    //   * the covered share of the glass (discs and beads in the aid map) follows the configured coverage times wetness^0.85 within
    //     30 %, and it rises with the wetness; at full presence the layers cover the design share 0.28 (measured in the first run:
    //     0.2765; the constant of the shader is that measurement) within 15 %;
    //   * a drop is a lens that shows the picture upside down and compressed: across a drop the picture's horizontal ramp runs backwards,
    //     with a slope of -(2.4 x refraction - 1) = -1.16 times the ramp's (+-30 %);
    //   * trails lie along the sliding direction: the autocorrelation of the trail map at a lag of 6 pixels is at least twice as high
    //     along the sliding direction as across it, for a slide down and for a slide to the right;
    //   * where the interior mask is 1 not a pixel of a drop is drawn, and outside the mask and the drops the picture is untouched
    //     (A/B, at most 1/512);
    //   * a drawn frame at full wetness costs at most 0.25 ms at 1920 x 1080 (droplet-cost, a timing run).
    //
    //   -mode scene -scene droplet-check
    [WeatherScene("droplet-check")]
    public sealed class DropletCheckScene : WeatherSceneBase
    {
        private const int W = 1920, H = 1080;
        private const float Aspect = W / (float)H;

        private DropletPass pass;
        private DropletTuning tuning;
        private RenderTexture image;
        private Texture2D view, ramp, maskTexture;

        protected override void BuildContent()
        {
            Shader shader = Shader.Find(DropletPass.ShaderName);
            if (shader == null) throw new InvalidOperationException("the droplet shader is not in the player");
            pass = new DropletPass(shader);
            tuning = DropletTuning.Defaults();
            image = new RenderTexture(W, H, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) { name = "droplet check image", filterMode = FilterMode.Bilinear };
            image.Create();
            view = MakeView();
            ramp = MakeRamp();
            maskTexture = MakeMask();
        }

        private static Texture2D NewTexture()
        {
            return new Texture2D(W, H, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        }

        // A view out of a window: sky, hills, a post, a sun, a horizontal line of lights.
        private static Texture2D MakeView()
        {
            Texture2D t = NewTexture();
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float v = y / (float)H, u = x / (float)W;
                    float horizon = 0.38f + 0.05f * Mathf.Sin(u * 9f) + 0.03f * Mathf.Sin(u * 23f);
                    Color c;
                    if (v > horizon)
                    {
                        float k = (v - horizon) / (1f - horizon);
                        c = Color.Lerp(new Color(0.78f, 0.82f, 0.88f), new Color(0.28f, 0.45f, 0.72f), k);
                        float dx = (u - 0.72f) * Aspect, dy = v - 0.78f;
                        if (dx * dx + dy * dy < 0.0016f) c = new Color(1f, 0.97f, 0.85f);
                    }
                    else
                    {
                        c = Color.Lerp(new Color(0.25f, 0.33f, 0.18f), new Color(0.45f, 0.38f, 0.26f), v / horizon);
                    }
                    if (Mathf.Abs(u - 0.3f) < 0.006f && v < 0.7f) c = new Color(0.75f, 0.1f, 0.1f);
                    if (Mathf.Abs(v - 0.2f) < 0.004f && (x / 24) % 2 == 0) c = new Color(1f, 0.9f, 0.5f);
                    px[y * W + x] = c;
                }
            t.SetPixels32(px);
            t.Apply(false);
            return t;
        }

        // A horizontal ramp from 0.1 to 0.9.
        private static Texture2D MakeRamp()
        {
            Texture2D t = NewTexture();
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    byte g = (byte)Mathf.RoundToInt(255f * (0.1f + 0.8f * x / (float)W));
                    px[y * W + x] = new Color32(g, g, g, 255);
                }
            t.SetPixels32(px);
            t.Apply(false);
            return t;
        }

        // The interior mask: the left fifth of the picture is a wall (1), the rest is window (0).
        private static Texture2D MakeMask()
        {
            var t = new Texture2D(W / 2, H / 2, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
            var px = new Color32[(W / 2) * (H / 2)];
            for (int y = 0; y < H / 2; y++)
                for (int x = 0; x < W / 2; x++)
                    px[y * (W / 2) + x] = x < W / 10 ? new Color32(255, 0, 0, 255) : new Color32(0, 0, 0, 255);
            t.SetPixels32(px);
            t.Apply(false);
            return t;
        }

        private DropletDraw MakeDraw(float wet, Vector2 slide, float clock, Texture mask, DropletTuning t)
        {
            return new DropletDraw
            {
                Tuning = t,
                Wet01 = wet,
                ClockS = clock,
                Slide = slide,
                Aspect = Aspect,
                FlipY = SystemInfo.graphicsUVStartsAtTop,
                InteriorMask = mask,
                Light = 0.8f,
                QuadColour = Color.white
            };
        }

        private Color[] Natural(DropletDraw draw, Texture source)
        {
            Graphics.Blit(source, image);
            var cmd = new CommandBuffer { name = "droplet-check" };
            pass.RecordHost(cmd, image, W, H, ref draw);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            return Read(image);
        }

        private Color[] Aid(DropletDraw draw)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = image;
            GL.Clear(false, true, Color.black);
            RenderTexture.active = previous;
            var cmd = new CommandBuffer { name = "droplet-check aid" };
            cmd.SetRenderTarget(image);
            pass.RecordMap(cmd, ref draw);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            return Read(image);
        }

        private static Color[] Read(RenderTexture rt)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            var copy = new Texture2D(rt.width, rt.height, TextureFormat.RGBAHalf, false, true);
            copy.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            copy.Apply(false);
            RenderTexture.active = previous;
            Color[] pixels = copy.GetPixels();
            UnityEngine.Object.Destroy(copy);
            return pixels;
        }

        private static byte Channel(float v)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Pow(Mathf.Clamp01(v), 1f / 2.2f) * 255f), 0, 255);
        }

        private static void Save(string folder, string name, Color[] pixels)
        {
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var bytes = new Color32[W * H];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = new Color32(Channel(pixels[i].r), Channel(pixels[i].g), Channel(pixels[i].b), 255);
            tex.SetPixels32(bytes);
            tex.Apply(false);
            File.WriteAllBytes(Path.Combine(folder, name), tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
        }

        // The share of pixels the aid map marks as disc or bead.
        private static double Covered(Color[] aid)
        {
            int n = 0;
            for (int i = 0; i < aid.Length; i++) if (aid[i].r > 0.5f || aid[i].b > 0.5f) n++;
            return n / (double)aid.Length;
        }

        // Autocorrelation of the trail channel at a lag along x and along y.
        private static void TrailCorrelation(Color[] aid, int lag, out double alongX, out double alongY)
        {
            double ax = 0, ay = 0;
            for (int y = 0; y < H - lag; y++)
                for (int x = 0; x < W - lag; x++)
                {
                    float g = aid[y * W + x].g;
                    if (g <= 0f) continue;
                    ax += g * aid[y * W + x + lag].g;
                    ay += g * aid[(y + lag) * W + x].g;
                }
            alongX = ax;
            alongY = ay;
        }

        private static Color[] Source(Texture2D t)
        {
            RenderTexture rt = RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            Graphics.Blit(t, rt);
            Color[] pixels = Read(rt);
            RenderTexture.ReleaseTemporary(rt);
            return pixels;
        }

        public override IEnumerator Run()
        {
            yield return Frames(2);
            Vector2 down = new Vector2(0f, -0.045f);
            float clock = 3.7f;

            // --- Nothing at wetness 0 or under the skip level.
            Color[] viewPixels = Source(view);
            Color[] dry = Natural(MakeDraw(0f, down, clock, null, tuning), view);
            Color[] almost = Natural(MakeDraw(tuning.SkipBelow * 0.5f, down, clock, null, tuning), view);
            double dryDiff = 0, almostDiff = 0;
            for (int i = 0; i < dry.Length; i += 5)
            {
                dryDiff = Math.Max(dryDiff, Math.Abs(dry[i].r - viewPixels[i].r));
                almostDiff = Math.Max(almostDiff, Math.Abs(almost[i].r - viewPixels[i].r));
            }
            Sink.AtMost("wet0.max_diff", dryDiff, 1.0 / 512.0);
            Sink.AtMost("under_skip.max_diff", almostDiff, 1.0 / 512.0);
            Sink.AtMost("wet0.covered", Covered(Aid(MakeDraw(0f, down, clock, null, tuning))), 0.0);

            // --- Coverage: the design share at full presence, then the configured coverage times wetness^0.85.
            DropletTuning full = tuning;
            full.CoverMax = 0.8f;
            double design = Covered(Aid(MakeDraw(1f, down, clock, null, full)));
            Sink.Reference("droplet.design_cover", design, 0.28, 0.15, 0.0);
            double last = -1;
            float[] wets = { 0.25f, 0.5f, 1f };
            bool monotone = true;
            foreach (float w in wets)
            {
                double covered = Covered(Aid(MakeDraw(w, down, clock, null, tuning)));
                double expected = tuning.CoverMax * Math.Pow(w, 0.85);
                Sink.Reference("coverage.w" + w.ToString("0.00", CultureInfo.InvariantCulture), covered, expected, 0.30, 0.0);
                if (covered <= last) monotone = false;
                last = covered;
            }
            Sink.Flag("coverage.rises_with_wetness", monotone);

            // --- A drop is a lens: the ramp runs backwards across it.
            Color[] rampPixels = Source(ramp);
            // No highlight and no darkened rim for this draw (both would swamp the slope of the picture behind the drop), and few
            // drops so that the pixel pairs lie inside one drop; a pair that jumps (two drops touch) is left out.
            DropletTuning lensTuning = tuning;
            lensTuning.Rim = 0f;
            DropletDraw rampDraw = MakeDraw(0.12f, down, clock, null, lensTuning);
            rampDraw.Light = 0f;
            Color[] rampOn = Natural(rampDraw, ramp);
            Color[] rampAid = Aid(rampDraw);
            double slopeSum = 0;
            int slopeN = 0;
            float rampSlope = 0.8f / W;
            for (int y = 2; y < H - 2; y += 1)
                for (int x = 2; x < W - 3; x++)
                {
                    int i = y * W + x;
                    // Inside a drop: both neighbours are disc, away from the rim.
                    if (rampAid[i].r < 0.99f || rampAid[i + 1].r < 0.99f || rampAid[i - 2].r < 0.99f || rampAid[i + 2].r < 0.99f) continue;
                    double step = rampOn[i + 1].r - rampOn[i].r;
                    if (Math.Abs(step) > 8 * 2.4 * rampSlope) continue;
                    slopeSum += step;
                    slopeN++;
                }
            double lensRatio = slopeN == 0 ? double.NaN : slopeSum / slopeN / rampSlope;
            Sink.Info("lens.disc_pixels", slopeN);
            Sink.Reference("lens.ramp_slope_ratio", lensRatio, -(2.4 * tuning.Refraction - 1.0), 0.30, 0.0);

            // --- Trails lie along the sliding direction.
            foreach (bool downward in new[] { true, false })
            {
                Vector2 slide = downward ? down : new Vector2(0.045f, 0f);
                Color[] aid = Aid(MakeDraw(1f, slide, clock, null, tuning));
                double ax, ay;
                TrailCorrelation(aid, 6, out ax, out ay);
                double along = downward ? ay : ax, across = downward ? ax : ay;
                Sink.Info((downward ? "trail.down" : "trail.right") + ".corr_along", along);
                Sink.Info((downward ? "trail.down" : "trail.right") + ".corr_across", across);
                Sink.AtLeast((downward ? "trail.down" : "trail.right") + ".anisotropy", along / Math.Max(across, 1.0), 2.0);
            }

            // --- The interior mask keeps the wall clear, and the picture outside the drops is untouched.
            DropletDraw masked = MakeDraw(1f, down, clock, maskTexture, tuning);
            Color[] onMasked = Natural(masked, view);
            Color[] aidMasked = Aid(masked);
            int wallDrops = 0, windowDrops = 0;
            double outsideDiff = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    bool wall = x < W / 10;
                    // The thresholds of the draw itself: it keeps a pixel whose alpha (disc, bead x 0.9, trail x 0.35) is at least 0.003.
                    bool drop = aidMasked[i].r > 0.003f || aidMasked[i].b * 0.9f > 0.003f || aidMasked[i].g * tuning.TrailAlpha > 0.003f;
                    if (wall && Math.Abs(onMasked[i].r - viewPixels[i].r) > 1.0 / 512.0) wallDrops++;
                    if (!wall && drop) windowDrops++;
                    if (!drop) outsideDiff = Math.Max(outsideDiff, Math.Abs(onMasked[i].r - viewPixels[i].r));
                }
            Sink.AtMost("mask.wall_pixels_changed", wallDrops, 0);
            Sink.AtLeast("mask.window_pixels_with_drops", windowDrops, 20000);
            Sink.AtMost("ab.outside_drops_max_diff", outsideDiff, 1.0 / 512.0);

            // --- The pictures: natural and aid for the same wetness, unique names.
            string o = Output;
            float[] sheet = { 0.15f, 0.5f, 1f };
            foreach (float w in sheet)
            {
                string tag = ((int)(w * 100)).ToString("000", CultureInfo.InvariantCulture);
                Save(o, "t08-droplet-natural-w" + tag + ".png", Natural(MakeDraw(w, down, clock, null, tuning), view));
                Save(o, "t08-droplet-aid-w" + tag + ".png", Aid(MakeDraw(w, down, clock, null, tuning)));
            }
            Save(o, "t08-droplet-natural-fast-airflow.png", Natural(MakeDraw(1f, new Vector2(0.4f, -0.04f), clock, null, tuning), view));
            Save(o, "t08-droplet-natural-masked.png", onMasked);
            Save(o, "t08-droplet-natural-ramp.png", rampOn);
            Save(o, "t08-droplet-natural-dry-source.png", viewPixels);
        }
    }
}
