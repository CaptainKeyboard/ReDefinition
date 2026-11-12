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
        private IEnumerator Natural(WetDraw? draw, Action<Color[]> done)
        {
            cmd.Clear();
            if (draw.HasValue)
            {
                WetDraw d = draw.Value;
                pass.RecordGBuffer(cmd, ref d);
            }
            Cam.AddCommandBuffer(CameraEvent.BeforeLighting, cmd);
            yield return Frames(3);
            yield return new WaitForEndOfFrame();
            Color[] pixels = ReadTexture(Target);
            Cam.RemoveCommandBuffer(CameraEvent.BeforeLighting, cmd);
            done(pixels);
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
            Color[] dry = null, wetDiffuse = null;
            yield return Natural(null, p => dry = p);
            yield return Natural(MakeDraw(1f, 0f, 0f, 288f, 0f, diffuseOnly), p => wetDiffuse = p);
            double lumaDry = MeanLuma(dry, 130, 0, 1150, 250), lumaWet = MeanLuma(wetDiffuse, 130, 0, 1150, 250);
            Sink.Info("ground.luma_dry", lumaDry);
            Sink.Reference("wet.diffuse_ratio", lumaWet / Math.Max(lumaDry, 1e-6), tuning.WetDiffuse, 0.04, 0.0);

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
            Sink.Flag("frozen.darkens_less_than_liquid", frozenRatio > liquidRatio + 0.02);
            Sink.Flag("snow_cover.darkens_less_than_liquid", MeanLuma(snowy, 130, 0, 1150, 250) / lumaDry > liquidRatio + 0.05);

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
            double glintRatio = MeanLuma(glintPuddle, 600, 330, 680, 390) / Math.Max(MeanLuma(glintDry, 600, 330, 680, 390), 1e-6);
            Sink.AtLeast("puddle.sun_glint_ratio", glintRatio, 1.5);
            Color[] glintWet = null;
            yield return Natural(MakeDraw(1f, 0f, 0f, 288f, 0f, tuning), p => glintWet = p);
            Sink.Info("wet.sun_glint_ratio", MeanLuma(glintWet, 600, 330, 680, 390) / Math.Max(MeanLuma(glintDry, 600, 330, 680, 390), 1e-6));

            // --- The pictures: natural and aid, unique names.
            PlaceCamera(0f, 25f);
            Save("t08-wet-natural-dry.png", dry, false);
            Save("t08-wet-natural-wet.png", wet, false);
            Save("t08-wet-natural-puddles-rain.png", puddles, false);
            Save("t08-wet-natural-frozen.png", frozen, false);
            Save("t08-wet-natural-snowcover.png", snowy, false);
            Save("t08-wet-aid-rings-rain-near.png", aidRain, true);
            Save("t08-wet-aid-rings-frozen-near.png", aidFrozen, true);
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
