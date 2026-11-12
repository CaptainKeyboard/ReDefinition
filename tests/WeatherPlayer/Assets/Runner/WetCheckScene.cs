using System;
using System.Collections;
using System.Globalization;
using System.IO;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // Checks the wet-surface overlay (WetSurfacesPass and the shader ReDefinition/AtmospheresWet, the classes the mod uses) on Unity's
    // deferred path without the game: the overlay is recorded at BeforeLighting into the G-buffer of a camera that sees the player's
    // terrain, the lit picture is read back, and the aid map (wetness, puddles, up-facing, ripples) is read from a second draw of the
    // same arithmetic. The installed Deferred mod's lighting shaders are not in the player [VERIFY in the game].
    //
    // Reference expectations (written before the scene was built):
    //   * the mean luma of level ground lit by sun and flat ambient (all linear) falls by the diffuse factor of the look: wet / dry
    //     equals WetnessTuning.WetDiffuse within 4 % when the smoothness is not changed (a purely diffuse change scales the lit
    //     picture), and frozen water darkens less than liquid;
    //   * the share of level ground that the aid map marks as puddle equals the asked share (0.2, 0.4, 0.55) within 0.08 (the depression
    //     field's threshold comes from the normal approximation of the field, FieldThreshold);
    //   * rain rings exist on the puddles while it rains and not on frozen ones;
    //   * the sky and everything the overlay does not see are identical with the overlay on and off (A/B, at most 1/512), and a draw
    //     with strength 0 records nothing;
    //   * a puddle (smoothness 0.95, flat normal) at the mirror direction of the sun is brighter than dry ground there by a factor of
    //     at least 1.5 although it is darker (a wet surface is a mirror for the sun);
    //   * the overlay costs at most 0.3 ms at 1280 x 720 (wet-cost, a timing run).
    //
    //   -mode scene -scene wet-check
    [WeatherScene("wet-check")]
    public sealed class WetCheckScene : WeatherSceneBase
    {
        private const int W = 1280, H = 720;

        private WetSurfacesPass pass;
        private CommandBuffer cmd;
        private RenderTexture mapRt;

        protected override void BuildContent()
        {
            Shader shader = Shader.Find(WetSurfacesPass.ShaderName);
            if (shader == null) throw new InvalidOperationException("the wet shader is not in the player");
            pass = new WetSurfacesPass(shader);
            cmd = new CommandBuffer { name = "wet-check" };
            mapRt = new RenderTexture(W, H, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { name = "wet aid map", filterMode = FilterMode.Point };
            mapRt.Create();
        }

        // The camera: on the beach dunes looking over level ground; the sun (azimuth 20, elevation 38 degrees) is ahead and above.
        private void PlaceCamera(float yaw, float pitch)
        {
            const float X = 3000f, Z = -800f;
            Pose(new CameraPose(new Vector3(X, TerrainHeight(X, Z) + 2.5f, Z), yaw, pitch));
        }

        // High above the dunes, looking steeply down: several hundred metres of level ground, many cells of the 32 m depression field.
        private void PlaceHigh()
        {
            const float X = 3000f, Z = -800f;
            Pose(new CameraPose(new Vector3(X, TerrainHeight(X, Z) + 400f, Z), 0f, 55f));
        }

        // Near the ground, looking steeply down: a few square metres, the rain rings are several pixels wide.
        private void PlaceNear()
        {
            const float X = 3000f, Z = -800f;
            Pose(new CameraPose(new Vector3(X, TerrainHeight(X, Z) + 1.8f, Z), 0f, 60f));
        }

        private WetDraw MakeDraw(float saturation, float standingMm, float rainLevel, float temperatureK, float snow01, WetnessTuning t, float strength = 1f)
        {
            WetLook look = WetnessLookMath.Eval(saturation, standingMm, rainLevel, temperatureK, 273.15f, snow01, ref t);
            Vector3 cam = Cam.transform.position;
            return new WetDraw
            {
                Look = look,
                Tuning = t,
                // A body of 6000 km radius under the camera: up is +y here.
                CentreRel = new Vector3(0f, -6.0e6f, 0f),
                East = Vector3.right,
                North = Vector3.forward,
                EastM = Mathf.Repeat(cam.x, WetSurfacesPass.WrapM),
                NorthM = Mathf.Repeat(cam.z, WetSurfacesPass.WrapM),
                ClockS = 1.3f,
                Strength = strength
            };
        }

        private static Color[] ReadTexture(RenderTexture rt)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            var copy = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true);
            copy.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            copy.Apply(false);
            RenderTexture.active = previous;
            Color[] pixels = copy.GetPixels();
            UnityEngine.Object.Destroy(copy);
            return pixels;
        }

        // The lit picture with the overlay recorded from the draw (or without any).
        private IEnumerator Natural(WetDraw? draw, Action<Color[]> done, CameraEvent at = CameraEvent.BeforeLighting)
        {
            cmd.Clear();
            if (draw.HasValue)
            {
                WetDraw d = draw.Value;
                pass.RecordGBuffer(cmd, ref d);
            }
            Cam.AddCommandBuffer(at, cmd);
            yield return Frames(3);
            yield return new WaitForEndOfFrame();
            Color[] pixels = ReadTexture(Target);
            Cam.RemoveCommandBuffer(at, cmd);
            done(pixels);
        }

        // The mean luma of the G-buffer's diffuse target over the ground rectangle after the overlay (a second buffer at the same event
        // copies the target once the overlay's buffer has run): the diffuse change itself, without the lighting.
        private IEnumerator Albedo(WetDraw? draw, Action<double> done)
        {
            cmd.Clear();
            if (draw.HasValue)
            {
                WetDraw d = draw.Value;
                pass.RecordGBuffer(cmd, ref d);
            }
            var probe = new CommandBuffer { name = "wet-check albedo" };
            probe.Blit(BuiltinRenderTextureType.GBuffer0, mapRt);
            Cam.AddCommandBuffer(CameraEvent.BeforeLighting, cmd);
            Cam.AddCommandBuffer(CameraEvent.BeforeLighting, probe);
            yield return Frames(3);
            yield return new WaitForEndOfFrame();
            Color[] pixels = ReadTexture(mapRt);
            Cam.RemoveCommandBuffer(CameraEvent.BeforeLighting, probe);
            Cam.RemoveCommandBuffer(CameraEvent.BeforeLighting, cmd);
            probe.Release();
            done(MeanLuma(pixels, 130, 0, 1150, 250));
        }

        // The aid map of the same draw.
        private IEnumerator Aid(WetDraw draw, Action<Color[]> done)
        {
            cmd.Clear();
            cmd.SetRenderTarget(mapRt);
            cmd.ClearRenderTarget(false, true, Color.clear);
            pass.RecordMap(cmd, ref draw);
            Cam.AddCommandBuffer(CameraEvent.BeforeLighting, cmd);
            yield return Frames(3);
            yield return new WaitForEndOfFrame();
            Color[] pixels = ReadTexture(mapRt);
            Cam.RemoveCommandBuffer(CameraEvent.BeforeLighting, cmd);
            done(pixels);
        }

        private static double Luma(Color c)
        {
            return 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b;
        }

        private static double MeanLuma(Color[] p, int x0, int y0, int x1, int y1)
        {
            double sum = 0;
            int n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++) { sum += Luma(p[y * W + x]); n++; }
            return n == 0 ? 0 : sum / n;
        }

        private static double MaxLuma(Color[] p)
        {
            double max = 0;
            for (int y = 0; y < H * 2 / 3; y++)
                for (int x = 0; x < W; x++) max = Math.Max(max, Luma(p[y * W + x]));
            return max;
        }

        private static byte Channel(float v)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Pow(Mathf.Clamp01(v), 1f / 2.2f) * 255f), 0, 255);
        }

        private void Save(string name, Color[] pixels, bool aid)
        {
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var bytes = new Color32[W * H];
            for (int i = 0; i < bytes.Length; i++)
            {
                Color c = pixels[i];
                bytes[i] = aid ? new Color32(Channel(c.r), Channel(c.g), Channel(c.b), 255) : new Color32(Channel(c.r), Channel(c.g), Channel(c.b), 255);
            }
            tex.SetPixels32(bytes);
            tex.Apply(false);
            File.WriteAllBytes(Path.Combine(Output, name), tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
        }

        private void SaveAlpha(string name, Color[] pixels)
        {
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var bytes = new Color32[W * H];
            for (int i = 0; i < bytes.Length; i++)
            {
                byte g = Channel(pixels[i].a);
                bytes[i] = new Color32(g, g, g, 255);
            }
            tex.SetPixels32(bytes);
            tex.Apply(false);
            File.WriteAllBytes(Path.Combine(Output, name), tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
        }

        // The share of level ground pixels (aid map: wet and up-facing) that are puddle, and the count of ring pixels.
        private static void Share(Color[] aid, out double puddleShare, out int ripplePixels, out int ground)
        {
            int level = 0, puddle = 0;
            ripplePixels = 0;
            ground = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    Color c = aid[y * W + x];
                    if (c.r < 0.3f) continue;
                    ground++;
                    if (c.b < 0.95f) continue;
                    level++;
                    if (c.g > 0.5f) puddle++;
                    if (c.a > 0.1f) ripplePixels++;
                }
            puddleShare = level == 0 ? 0 : puddle / (double)level;
        }

        public override IEnumerator Run()
        {
            yield return Frames(2);
            Cam.renderingPath = RenderingPath.DeferredShading;
            yield return Frames(4);
            Sink.Flag("deferred.active", Cam.actualRenderingPath == RenderingPath.DeferredShading);
            var tuning = WetnessTuning.Defaults();
            Sink.Info("tuning.wet_diffuse", tuning.WetDiffuse);

            // --- A purely diffuse change: no smoothness added, no puddles.
            PlaceCamera(0f, 25f);
            WetnessTuning diffuseOnly = tuning;
            diffuseOnly.WetSmooth = 0f;
            diffuseOnly.PuddleMax = 0f;
            // The sky is not reflected in this part: the ground's reflection of the sky (a share of the picture that no diffuse factor
            // touches) would hide the diffuse change the row is about.
            var blackSky = new Cubemap(16, TextureFormat.RGBA32, false);
            var blackPixels = new Color[16 * 16];
            for (int i = 0; i < blackPixels.Length; i++) blackPixels[i] = Color.black;
            for (int face = 0; face < 6; face++) blackSky.SetPixels(blackPixels, (CubemapFace)face);
            blackSky.Apply();
            RenderSettings.defaultReflectionMode = UnityEngine.Rendering.DefaultReflectionMode.Custom;
            RenderSettings.customReflection = blackSky;
            yield return Frames(6);
            Color[] dry = null, wetDiffuse = null;
            yield return Natural(null, p => dry = p);
            yield return Natural(MakeDraw(1f, 0f, 0f, 288f, 0f, diffuseOnly), p => wetDiffuse = p);
            double lumaDry = MeanLuma(dry, 130, 0, 1150, 250), lumaWet = MeanLuma(wetDiffuse, 130, 0, 1150, 250);
            Sink.Info("ground.luma_dry", lumaDry);

            // --- The diffuse target itself: wet / dry albedo is the configured factor (no lighting, no specular in it).
            double albedoDry = 0, albedoWet = 0;
            yield return Albedo(null, v => albedoDry = v);
            yield return Albedo(MakeDraw(1f, 0f, 0f, 288f, 0f, diffuseOnly), v => albedoWet = v);
            Sink.Info("albedo.dry_luma", albedoDry);
            Sink.Reference("wet.albedo_ratio", albedoWet / Math.Max(albedoDry, 1e-6), tuning.WetDiffuse, 0.03, 0.0);

            // --- Which G-buffer target carries which part of the picture (the diagnosis): the same diffuse change written into one
            // target, into the other, into none, and a factor of 0 into both (what is left is not the geometry pass's diffuse light).
            string[] names = { "both", "albedo_only", "ambient_only", "none" };
            Vector4[] skips = { Vector4.zero, new Vector4(0f, 1f, 1f, 1f), new Vector4(1f, 1f, 1f, 0f), new Vector4(1f, 1f, 1f, 1f) };
            for (int i = 0; i < names.Length; i++)
            {
                WetDraw dd = MakeDraw(1f, 0f, 0f, 288f, 0f, diffuseOnly);
                dd.SkipTargets = skips[i];
                Color[] shot = null;
                yield return Natural(dd, p => shot = p);
                double targetRatio = MeanLuma(shot, 130, 0, 1150, 250) / Math.Max(lumaDry, 1e-6);
                Sink.Info("diag.target." + names[i] + ".ratio", targetRatio);
                // Each of the two targets that hold diffuse light must change the picture by itself.
                if (i == 1 || i == 2) Sink.AtMost("target." + names[i] + ".darkens", targetRatio, 0.95);
            }
            // Which event and which fourth target reach the ambient light: the ambient-only write at each combination.
            foreach (CameraEvent ev in new[] { CameraEvent.BeforeLighting, CameraEvent.AfterGBuffer })
                foreach (bool cameraTarget in new[] { false, true })
                {
                    WetDraw dd = MakeDraw(1f, 0f, 0f, 288f, 0f, diffuseOnly);
                    dd.SkipTargets = new Vector4(1f, 1f, 1f, 0f);
                    dd.EmissionIsGBuffer3 = !cameraTarget;
                    dd.Look.DiffuseFactor = 0f;
                    Color[] shot = null;
                    yield return Natural(dd, p => shot = p, ev);
                    Sink.Info("diag.ambient_factor0." + ev + (cameraTarget ? ".cameratarget" : ".gbuffer3") + ".ratio", MeanLuma(shot, 130, 0, 1150, 250) / Math.Max(lumaDry, 1e-6));
                }
            {
                WetDraw black = MakeDraw(1f, 0f, 0f, 288f, 0f, diffuseOnly);
                black.Look.DiffuseFactor = 0f;
                Color[] shot = null;
                yield return Natural(black, p => shot = p);
                double ratioFactor0 = MeanLuma(shot, 130, 0, 1150, 250) / Math.Max(lumaDry, 1e-6);
                Sink.Info("diag.factor0.ratio", ratioFactor0);
                // What is left with a diffuse factor of 0 is the picture's light that is not diffuse (the sun's highlight on the ground);
                // the diffuse share S of the ground's luma is the rest. A diffuse factor f then gives 1 - (1 - f) S: the overlay scales
                // the diffuse light and nothing else, in both targets that hold it.
                double share = 1.0 - ratioFactor0;
                Sink.AtLeast("wet.diffuse_share_of_ground_luma", share, 0.6);
                Sink.Reference("wet.diffuse_ratio", lumaWet / Math.Max(lumaDry, 1e-6), 1.0 - (1.0 - tuning.WetDiffuse) * share, 0.03, 0.0);
            }
            Cam.renderingPath = RenderingPath.Forward;
            yield return Frames(4);
            Color[] forwardDry = null;
            yield return Natural(null, p => forwardDry = p);
            Sink.Info("diag.forward_dry.ratio_to_deferred_dry", MeanLuma(forwardDry, 130, 0, 1150, 250) / Math.Max(lumaDry, 1e-6));
            Cam.renderingPath = RenderingPath.DeferredShading;
            yield return Frames(4);

            // The sky is reflected again from here on; the dry picture and its luma are taken again in that state.
            RenderSettings.defaultReflectionMode = UnityEngine.Rendering.DefaultReflectionMode.Skybox;
            RenderSettings.customReflection = null;
            yield return Frames(6);
            yield return Natural(null, p => dry = p);
            lumaDry = MeanLuma(dry, 130, 0, 1150, 250);
            Sink.Info("ground.luma_dry_with_sky_reflection", lumaDry);

            // --- The sky and the far field are untouched; a draw with strength 0 is the dry picture.
            double skyDiff = 0;
            // Rows from the bottom; with pitch 25 and a 60 degree field the horizon is near row 650, dunes lift it by about 10 rows.
            for (int y = (int)(H * 0.95f); y < H; y++)
                for (int x = 0; x < W; x++) skyDiff = Math.Max(skyDiff, Math.Abs(Luma(wetDiffuse[y * W + x]) - Luma(dry[y * W + x])));
            Sink.AtMost("ab.sky_max_diff", skyDiff, 1.0 / 512.0);
            Color[] off = null;
            yield return Natural(MakeDraw(1f, 6f, 0.3f, 288f, 0f, tuning, 0f), p => off = p);
            double offDiff = 0;
            for (int i = 0; i < off.Length; i += 3) offDiff = Math.Max(offDiff, Math.Abs(Luma(off[i]) - Luma(dry[i])));
            Sink.AtMost("ab.strength0_max_diff", offDiff, 1.0 / 512.0);

            // --- Wet ground with its gloss, puddles and rings in rain; frozen; under snow cover.
            Color[] wet = null, puddles = null, frozen = null, snowy = null;
            yield return Natural(MakeDraw(1f, 0f, 0f, 288f, 0f, tuning), p => wet = p);
            WetDraw puddleDraw = MakeDraw(1f, 6f, 0.3f, 288f, 0f, tuning);
            yield return Natural(puddleDraw, p => puddles = p);
            WetDraw frozenDraw = MakeDraw(1f, 6f, 0.3f, 265f, 0f, tuning);
            yield return Natural(frozenDraw, p => frozen = p);
            WetDraw snowDraw = MakeDraw(1f, 6f, 0.0f, 285f, 1f, tuning);
            yield return Natural(snowDraw, p => snowy = p);
            Sink.Info("ground.luma_wet_gloss", MeanLuma(wet, 130, 0, 1150, 250) / lumaDry);
            Sink.Info("ground.luma_puddles", MeanLuma(puddles, 130, 0, 1150, 250) / lumaDry);
            double frozenRatio = MeanLuma(frozen, 130, 0, 1150, 250) / lumaDry, liquidRatio = MeanLuma(puddles, 130, 0, 1150, 250) / lumaDry;
            Sink.Info("ground.luma_frozen", frozenRatio);
            Sink.Info("ground.luma_snow_cover", MeanLuma(snowy, 130, 0, 1150, 250) / lumaDry);

            // The liquid, frozen and snow-covered comparison over the whole of a high view (many cells of the puddle field; a patch of a
            // few square metres is inside a puddle or not by chance).
            PlaceHigh();
            Color[] dryHigh = null, puddleHigh = null, frozenHigh = null, snowHigh = null;
            yield return Natural(null, p => dryHigh = p);
            yield return Natural(MakeDraw(1f, 6f, 0.3f, 288f, 0f, tuning), p => puddleHigh = p);
            yield return Natural(MakeDraw(1f, 6f, 0.3f, 265f, 0f, tuning), p => frozenHigh = p);
            yield return Natural(MakeDraw(1f, 6f, 0f, 285f, 1f, tuning), p => snowHigh = p);
            double lumaHigh = MeanLuma(dryHigh, 0, 0, W, H);
            double liquidHigh = MeanLuma(puddleHigh, 0, 0, W, H) / lumaHigh, frozenHighRatio = MeanLuma(frozenHigh, 0, 0, W, H) / lumaHigh, snowHighRatio = MeanLuma(snowHigh, 0, 0, W, H) / lumaHigh;
            Sink.Info("high.luma_liquid", liquidHigh);
            Sink.Info("high.luma_frozen", frozenHighRatio);
            Sink.Info("high.luma_snow_cover", snowHighRatio);
            Sink.Flag("frozen.darkens_less_than_liquid", frozenHighRatio > liquidHigh + 0.02);
            Sink.Flag("snow_cover.darkens_less_than_liquid", snowHighRatio > liquidHigh + 0.05);
            Save("t08-wet-natural-high-dry.png", dryHigh, false);
            Save("t08-wet-natural-high-frozen.png", frozenHigh, false);
            Save("t08-wet-natural-high-snowcover.png", snowHigh, false);
            PlaceCamera(0f, 25f);

            // --- The aid maps, the share of puddles against the asked share, the rings.
            PlaceHigh();
            double[] covers = { 0.2, 0.4, 0.55 };
            for (int i = 0; i < covers.Length; i++)
            {
                WetDraw d = MakeDraw(1f, 6f, 0.3f, 288f, 0f, tuning);
                d.Look.PuddleCover = (float)covers[i];
                Color[] aid = null;
                yield return Aid(d, p => aid = p);
                double share; int rings, ground;
                Share(aid, out share, out rings, out ground);
                string tag = "puddle.share_vs_cover.c" + covers[i].ToString("0.00", CultureInfo.InvariantCulture);
                Sink.Reference(tag, share, covers[i], 0.0, 0.08);
                Sink.Info(tag + ".ground_px", ground);
            }
            // The rain rings, close to the ground where every puddle pixel is several pixels wide: the whole ground a puddle (cover 1).
            PlaceNear();
            WetDraw ringsRainDraw = MakeDraw(1f, 6f, 0.3f, 288f, 0f, tuning);
            ringsRainDraw.Look.PuddleCover = 1f;
            WetDraw ringsFrozenDraw = MakeDraw(1f, 6f, 0.3f, 265f, 0f, tuning);
            ringsFrozenDraw.Look.PuddleCover = 1f;
            WetDraw ringsDryDraw = MakeDraw(0f, 0f, 0f, 288f, 0f, tuning);
            Color[] aidRain = null, aidFrozen = null, aidDry = null;
            yield return Aid(ringsRainDraw, p => aidRain = p);
            yield return Aid(ringsFrozenDraw, p => aidFrozen = p);
            yield return Aid(ringsDryDraw, p => aidDry = p);
            double s1; int ringsRain, ringsFrozen, ringsDry, g1;
            Share(aidRain, out s1, out ringsRain, out g1);
            Share(aidFrozen, out s1, out ringsFrozen, out g1);
            Share(aidDry, out s1, out ringsDry, out g1);
            Sink.Info("ripples.ground_px", g1);
            Sink.AtLeast("ripples.rain_pixels", ringsRain, 500);
            Sink.AtMost("ripples.frozen_pixels", ringsFrozen, 0);
            Sink.AtMost("ripples.dry_pixels", ringsDry, 0);

            // --- A puddle mirrors the sun: camera at the mirror direction of the sun (38 degrees below the horizon, towards its azimuth).
            PlaceCamera(20f, 38f);
            Color[] glintDry = null, glintPuddle = null;
            yield return Natural(null, p => glintDry = p);
            WetnessTuning allPuddles = tuning;
            allPuddles.PuddleMax = 1f;
            WetDraw mirror = MakeDraw(1f, tuning.PuddleFullMm, 0f, 288f, 0f, allPuddles);
            yield return Natural(mirror, p => glintPuddle = p);
            // The sun's mirror image in a puddle is a small spot: the brightest pixel of the view, not the mean of a patch.
            Sink.Info("puddle.sun_glint_patch_ratio", MeanLuma(glintPuddle, 600, 330, 680, 390) / Math.Max(MeanLuma(glintDry, 600, 330, 680, 390), 1e-6));
            Sink.AtLeast("puddle.sun_glint_peak_ratio", MaxLuma(glintPuddle) / Math.Max(MaxLuma(glintDry), 1e-6), 1.5);
            // The diagnosis: the brightest pixel of the lower two thirds with nothing, with the smoothness alone and with the puddle.
            WetDraw smoothOnly = mirror;
            smoothOnly.SkipTargets = new Vector4(1f, 0f, 1f, 1f);
            Color[] glintSmooth = null;
            yield return Natural(smoothOnly, p => glintSmooth = p);
            Sink.Info("diag.glint_max.dry", MaxLuma(glintDry));
            Sink.Info("diag.glint_max.smooth_only", MaxLuma(glintSmooth));
            Sink.Info("diag.glint_max.puddle", MaxLuma(glintPuddle));
            Color[] glintWet = null;
            yield return Natural(MakeDraw(1f, 0f, 0f, 288f, 0f, tuning), p => glintWet = p);
            Sink.Info("wet.sun_glint_peak_ratio", MaxLuma(glintWet) / Math.Max(MaxLuma(glintDry), 1e-6));
            Save("t08-wet-natural-glint-wet.png", glintWet, false);

            // --- The pictures: natural and aid, unique names.
            PlaceCamera(0f, 25f);
            Save("t08-wet-natural-dry.png", dry, false);
            Save("t08-wet-natural-wet.png", wet, false);
            Save("t08-wet-natural-puddles-rain.png", puddles, false);
            Save("t08-wet-natural-frozen.png", frozen, false);
            Save("t08-wet-natural-snowcover.png", snowy, false);
            // The rings' slope (the aid map's alpha) as a grey picture.
            SaveAlpha("t08-wet-aid-rings-rain-near.png", aidRain);
            SaveAlpha("t08-wet-aid-rings-frozen-near.png", aidFrozen);
            Color[] naturalRings = null;
            PlaceNear();
            yield return Natural(ringsRainDraw, p => naturalRings = p);
            PlaceCamera(0f, 25f);
            Save("t08-wet-natural-rings-rain-near.png", naturalRings, false);
            PlaceHigh();
            Color[] aidHigh = null, naturalHigh = null;
            WetDraw highDraw = MakeDraw(1f, 6f, 0.3f, 288f, 0f, tuning);
            yield return Aid(highDraw, p => aidHigh = p);
            yield return Natural(highDraw, p => naturalHigh = p);
            Save("t08-wet-aid-puddles-high.png", aidHigh, true);
            Save("t08-wet-natural-puddles-high.png", naturalHigh, false);
            PlaceCamera(0f, 25f);
            Save("t08-wet-natural-glint-dry.png", glintDry, false);
            Save("t08-wet-natural-glint-puddle.png", glintPuddle, false);
        }
    }
}
