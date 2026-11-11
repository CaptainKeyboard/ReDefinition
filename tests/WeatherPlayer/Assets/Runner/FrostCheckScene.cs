using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // Checks the camera frost's own draws (FrostPass, FrostAtlas and the shader, the classes the mod uses) without the game. The pass
    // is drawn onto an image of the scene's size the way the host hook draws it, then read back.
    //
    // Reference expectations (written before the scene was built):
    //   * the atlas is 4 MB and builds in under 60 ms in this runtime;
    //   * the frost's coverage (the contribution mask, atmoDebug = 1: the pixels whose opacity is at least 0.15) at exposure 0 / 0.4 / 1
    //     at 16:9 is 0 / 10.0 / 32.5 % of the picture, each within 10 %, and equals the CPU arithmetic of FrostShape within 2 %;
    //   * at exposure 1 not one pixel of the contribution mask lies inside the clear centre (r < 0.40), and the pixels of the picture
    //     outside the mask are identical with the frost on and off (A/B);
    //   * a flake of 7 px radius covers 35-120 % of the area of its disc and refracts the picture behind it;
    //   * a drop slides along the gravity direction: its wet trail lies against the way it runs (above the head for gravity down),
    //     and a drop wipes the frost it passes;
    //   * a draw with nothing on the glass records no command;
    //   * an active draw with 40 flakes and 24 drops at exposure 1 costs at most 0.25 ms at 2560 x 1440.
    //
    //   -mode scene -scene frost-check
    [WeatherScene("frost-check")]
    public sealed class FrostCheckScene : WeatherSceneBase
    {
        private const int W = 1280, H = 720;
        private const float Aspect = W / (float)H;
        // The design table at 16:9 (FrostShapeTests.CoverageFollowsTheDesignTable): share of the picture at exposure 0.4 and 1.
        private const float DesignAt04 = 0.100f, DesignAt1 = 0.325f;
        private const float MaskRed = 0.15f * 0.15f;

        private FrostPass pass;
        private Texture2D atlasTexture;
        private byte[] atlas;
        private RenderTexture image;
        private Texture2D pattern;
        private FrostTuning tuning;

        protected override void BuildContent()
        {
            Shader shader = Shader.Find("ReDefinition/AtmospheresFrost");
            if (shader == null) throw new InvalidOperationException("the frost shader is not in the player");
            var watch = Stopwatch.StartNew();
            atlas = FrostAtlas.Build();
            Sink.AtMost("atlas.build_ms", watch.Elapsed.TotalMilliseconds, 60.0);
            Sink.Flag("atlas.bytes_4mb", atlas.Length == 4 * 1024 * 1024);
            atlasTexture = FrostAtlas.MakeTexture(atlas);
            pass = new FrostPass(shader, atlasTexture);
            tuning = FrostTuning.Defaults();
            tuning.FlipY = FrostPass.ResolveFlipY(-1f);
            Sink.Info("target.flip_y", tuning.FlipY ? 1 : 0);
            image = Make(W, H);
            pattern = new Texture2D(W, H, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
            var pixels = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    // Grey with a gradient and thin vertical stripes: a refraction moves the stripes.
                    byte g = (byte)(90 + 60 * x / W + ((x / 6) % 2 == 0 ? 30 : 0));
                    pixels[y * W + x] = new Color32(g, g, g, 255);
                }
            pattern.SetPixels32(pixels);
            pattern.Apply(false);
        }

        private static RenderTexture Make(int w, int h)
        {
            var t = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) { name = "frost check image", filterMode = FilterMode.Bilinear };
            t.Create();
            return t;
        }

        private FrostDraw MakeDraw(float exposure, int flakes, int drops, Vector2 gravity)
        {
            var d = new FrostDraw
            {
                Exposure = exposure,
                Growth = FrostShape.Growth(exposure, ref tuning),
                Aspect = Aspect,
                Light = 0.8f,
                QuadColour = Color.white,
                Flakes = new Vector4[CameraFrost.MaxFlakes],
                Drops = new Vector4[CameraFrost.MaxDrops],
                FlakeCount = flakes,
                DropCount = drops,
                Gravity = gravity,
                Tuning = tuning,
                FlipY = tuning.FlipY
            };
            return d;
        }

        // Clears the image to black or the pattern, runs the host pass over it and returns the pixels (row 0 at the bottom).
        private Color[] Render(FrostDraw draw, bool black, float debug)
        {
            Shader.SetGlobalFloat("_AtmoDebug", debug);
            if (black)
            {
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = image;
                GL.Clear(false, true, Color.black);
                RenderTexture.active = previous;
            }
            else
            {
                Graphics.Blit(pattern, image);
            }
            var cmd = new CommandBuffer { name = "frost-check" };
            pass.RecordHost(cmd, image, W, H, ref draw);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            Shader.SetGlobalFloat("_AtmoDebug", 0f);
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

        // The pixels of a contribution mask run: red is the opacity squared (colour times opacity, blended by the opacity over black).
        private static int Count(Color[] mask, float redAtLeast)
        {
            int n = 0;
            foreach (Color c in mask) if (c.r >= redAtLeast) n++;
            return n;
        }

        private static float Radius(int x, int y)
        {
            return FrostShape.Radius((x + 0.5f) / W, (y + 0.5f) / H, Aspect);
        }

        public override IEnumerator Run()
        {
            yield return Frames(2);

            // --- Coverage against the design table, and against the CPU arithmetic.
            float[] exposures = { 0f, 0.4f, 1f };
            float[] design = { 0f, DesignAt04, DesignAt1 };
            Color[] fullMask = null;
            for (int i = 0; i < exposures.Length; i++)
            {
                float e = exposures[i];
                Color[] mask = Render(MakeDraw(e, 0, 0, new Vector2(0, -1)), true, 1f);
                float gpu = Count(mask, MaskRed) / (float)(W * H);
                float cpu = FrostShape.CoverageOf(atlas, e, 1f, Aspect, ref tuning, 0.15f, W, H);
                string tag = "coverage.e" + e.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                if (i == 0) Sink.AtMost(tag, gpu, 0.002); else Sink.Reference(tag, gpu, design[i], 0.10, 0.0);
                Sink.Info(tag + ".cpu", cpu);
                if (i > 0) Sink.AtMost(tag + ".gpu_vs_cpu", Math.Abs(gpu - cpu) / Math.Max(cpu, 1e-4f), 0.02);
                if (i == 2) fullMask = mask;
            }

            // --- The centre of the view stays readable at full exposure.
            int centre = 0, mid = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float r = Radius(x, y);
                    bool inMask = fullMask[y * W + x].r > 2e-6f;
                    if (r < tuning.Centre && inMask) centre++;
                    if (r < 0.55f && inMask) mid++;
                }
            Sink.AtMost("centre_clear.mask_px_inside_r040", centre, 0);
            Sink.Info("centre_clear.mask_px_inside_r055", mid);

            // --- A/B: frost on against off, identical outside the mask (frost, flakes and drops all present).
            {
                FrostDraw full = MakeDraw(1f, 6, 3, new Vector2(0, -1));
                for (int i = 0; i < 6; i++) full.Flakes[i] = new Vector4(0.15f + 0.12f * i, 0.2f + 0.1f * (i % 3), 0.006f, i * 2 + 0.2f);
                for (int i = 0; i < 3; i++) full.Drops[i] = new Vector4(0.3f + 0.2f * i, 0.5f, 0.012f, 0.06f);
                Color[] on = Render(full, false, 0f);
                Color[] mask = Render(full, true, 1f);
                Color[] off = Render(MakeDraw(0f, 0, 0, new Vector2(0, -1)), false, 0f);
                double worst = 0;
                int outside = 0, inside = 0;
                for (int i = 0; i < on.Length; i++)
                {
                    if (mask[i].r > 2e-6f) { inside++; continue; }
                    outside++;
                    worst = Math.Max(worst, Math.Abs(on[i].r - off[i].r));
                }
                Sink.AtMost("ab.outside_mask_max_diff", worst, 1.0 / 512.0);
                Sink.Info("ab.mask_px", inside);
                Sink.Flag("ab.mask_not_empty", inside > 1000);
                // Inside the mask the picture changed.
                double changed = 0;
                int n = 0;
                for (int i = 0; i < on.Length; i += 7)
                    if (mask[i].r >= MaskRed) { changed += Math.Abs(on[i].r - off[i].r); n++; }
                Sink.AtLeast("ab.mean_change_in_mask", n == 0 ? 0 : changed / n, 0.01);
            }

            // --- A flake: size, share of its disc, and refraction of the picture behind it.
            {
                FrostDraw one = MakeDraw(0f, 1, 0, new Vector2(0, -1));
                float radius = 7f / H;
                one.Flakes[0] = new Vector4(0.5f, 0.5f, radius, 3f + 0.1f);
                Color[] mask = Render(one, true, 1f);
                int px = Count(mask, MaskRed);
                float disc = Mathf.PI * 7f * 7f;
                Sink.Reference("flake.area_vs_disc", px / disc, 0.775, 0.55, 0.0);
                Color[] on = Render(one, false, 0f);
                Color[] off = Render(MakeDraw(0f, 0, 0, new Vector2(0, -1)), false, 0f);
                double inDiff = 0, outDiff = 0;
                int inN = 0, outN = 0;
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        float dx = x + 0.5f - W * 0.5f, dy = y + 0.5f - H * 0.5f;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        float diff = Mathf.Abs(on[y * W + x].r - off[y * W + x].r);
                        if (d < 6f) { inDiff += diff; inN++; }
                        else if (d > 12f) { outDiff += diff; outN++; }
                    }
                Sink.AtLeast("flake.refracts_mean_diff", inDiff / inN, 0.01);
                Sink.AtMost("flake.outside_diff", outDiff / outN, 1e-4);
            }

            // --- A drop slides along gravity: its trail lies against the way it runs.
            foreach (bool down in new[] { true, false })
            {
                FrostDraw one = MakeDraw(0f, 0, 1, down ? new Vector2(0, -1) : new Vector2(1, 0));
                one.Drops[0] = new Vector4(0.5f, 0.5f, 0.02f, 0.12f);
                Color[] mask = Render(one, true, 1f);
                int above = 0, below = 0, left = 0, right = 0;
                float headPx = 0.02f * H;
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        if (mask[y * W + x].r < MaskRed) continue;
                        float dx = x + 0.5f - W * 0.5f, dy = y + 0.5f - H * 0.5f;
                        if (dy > headPx * 1.3f && Mathf.Abs(dx) < headPx) above++;
                        if (dy < -headPx * 1.3f && Mathf.Abs(dx) < headPx) below++;
                        if (dx < -headPx * 1.3f && Mathf.Abs(dy) < headPx) left++;
                        if (dx > headPx * 1.3f && Mathf.Abs(dy) < headPx) right++;
                    }
                if (down)
                {
                    Sink.Info("drop.down.trail_px_above_below", above * 1000 + below);
                    Sink.Flag("drop.down.trail_above_head", above > 20 && below == 0);
                }
                else
                {
                    // Gravity to the right of the picture (a camera rolled a quarter turn): the trail is on the left.
                    Sink.Info("drop.right.trail_px_left_right", left * 1000 + right);
                    Sink.Flag("drop.right.trail_left_of_head", left > 20 && right == 0);
                }
            }

            // --- A drop wipes the frost along its trail.
            {
                FrostDraw frosted = MakeDraw(1f, 0, 0, new Vector2(0, -1));
                Color[] without = Render(frosted, true, 1f);
                FrostDraw wiped = frosted;
                wiped.DropCount = 1;
                // In the lower right corner, where the frost is thick: the head at 0.93 / 0.20, the trail upward from it.
                wiped.Drops[0] = new Vector4(0.93f, 0.20f, 0.015f, 0.25f);
                Color[] with = Render(wiped, true, 1f);
                double sumWithout = 0, sumWith = 0;
                int n = 0;
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        float u = (x + 0.5f) / W, v = (y + 0.5f) / H;
                        if (Mathf.Abs(u - 0.93f) < 0.003f && v > 0.23f && v < 0.31f)
                        {
                            sumWithout += without[y * W + x].r;
                            sumWith += with[y * W + x].r;
                            n++;
                        }
                    }
                // The red of the mask is the opacity squared: the frost alone is there before the drop, the thin wet trail after it.
                Sink.AtLeast("wipe.column_red_without", n == 0 ? 0 : sumWithout / n, 0.1);
                Sink.AtMost("wipe.column_red_with_over_without", sumWithout <= 0 ? 1 : sumWith / sumWithout, 0.6);
            }

            // --- Nothing on the glass records nothing.
            {
                var cmd = new CommandBuffer();
                FrostDraw none = MakeDraw(0f, 0, 0, new Vector2(0, -1));
                pass.RecordHost(cmd, image, W, H, ref none);
                Sink.Flag("inactive.empty_buffer", cmd.sizeInBytes == 0);
                cmd.Release();
            }

            // --- The cost of an active draw at 2560 x 1440: full frost, 40 flakes, 24 drops.
            {
                const int BigW = 2560, BigH = 1440;
                RenderTexture big = Make(BigW, BigH);
                FrostDraw active = MakeDraw(1f, 40, 24, new Vector2(0.2f, -1f));
                active.Aspect = BigW / (float)BigH;
                for (int i = 0; i < 40; i++) active.Flakes[i] = new Vector4(Mathf.Repeat(0.137f * i, 1f), Mathf.Repeat(0.291f * i, 1f), 0.004f + 0.0004f * (i % 8), i % 16 + 0.3f);
                for (int i = 0; i < 24; i++) active.Drops[i] = new Vector4(Mathf.Repeat(0.211f * i, 1f), Mathf.Repeat(0.173f * i + 0.1f, 1f), 0.007f + 0.0003f * (i % 6), 0.05f);
                Graphics.Blit(pattern, big);
                var idle = new CommandBuffer { name = "frost-cost-baseline" };
                idle.SetRenderTarget(big);
                idle.ClearRenderTarget(false, true, Color.black);
                double baseMs = Measure(idle, big, 0.0);
                Sink.Info("cost.baseline_ms_1440p", baseMs);

                // Each variant's buffer is timed in five batches of a hundred runs between read-backs (the read-back waits for the GPU);
                // the best batch minus the empty clear is its cost.
                FrostDraw all = active;
                FrostDraw rimOnly = active;
                rimOnly.FlakeCount = 0;
                rimOnly.DropCount = 0;
                FrostDraw flakesOnly = active;
                flakesOnly.Growth = 0f;
                flakesOnly.DropCount = 0;
                FrostDraw dropsOnly = active;
                dropsOnly.Growth = 0f;
                dropsOnly.FlakeCount = 0;
                var variants = new[] { new KeyValuePair<string, FrostDraw>("all", all), new KeyValuePair<string, FrostDraw>("rim_only", rimOnly),
                                       new KeyValuePair<string, FrostDraw>("flakes_only", flakesOnly), new KeyValuePair<string, FrostDraw>("drops_only", dropsOnly) };
                foreach (KeyValuePair<string, FrostDraw> variant in variants)
                {
                    FrostDraw d = variant.Value;
                    var cmd = new CommandBuffer { name = "frost-cost-" + variant.Key };
                    pass.RecordHost(cmd, big, BigW, BigH, ref d);
                    double ms = Measure(cmd, big, baseMs);
                    cmd.Release();
                    if (variant.Key == "all") Sink.AtMost("cost.active_ms_1440p", ms, 0.25);
                    else Sink.Info("cost." + variant.Key + "_ms_1440p", ms);
                }
                idle.Release();
                big.Release();
            }
        }

        // The best of five batches of a hundred executions, in milliseconds each, less a baseline.
        private static double Measure(CommandBuffer cmd, RenderTexture target, double baseline)
        {
            for (int i = 0; i < 30; i++) Graphics.ExecuteCommandBuffer(cmd);
            Read1(target);
            double best = double.MaxValue;
            for (int batch = 0; batch < 5; batch++)
            {
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < 100; i++) Graphics.ExecuteCommandBuffer(cmd);
                Read1(target);
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds / 100.0);
            }
            return Math.Max(0.0, best - baseline);
        }

        // Waits for the GPU by reading one pixel of the image.
        private static void Read1(RenderTexture rt)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            var t = new Texture2D(1, 1, TextureFormat.RGBAHalf, false, true);
            t.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
            RenderTexture.active = previous;
            UnityEngine.Object.Destroy(t);
        }
    }
}
