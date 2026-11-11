using System;
using System.Collections;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // Checks the precipitation renderer's own draws (PrecipPass and its shader, the same classes the mod uses) without the game:
    //   * per type at level 0.6 over a flat grey: the luminance shift has the sign of the type's colour, enough pixels change, the
    //     pattern moves down (gravity), and with a wind from the west it moves to the east (every type, grit included);
    //   * below level 0.04 nothing is drawn: the frame equals the frame without the renderer;
    //   * Host against Scene composite of the same frame with veil and flakes at levels 0.3, 0.6, 1.0: mean luminance within 5 %;
    //   * a cross-fade between the two (weights 0.5 / 0.5) covers the veil as one veil, not two.
    //
    //   -mode scene -scene precip-check
    [WeatherScene("precip-check")]
    public sealed class PrecipCheckScene : WeatherSceneBase
    {
        private const float Grey = 0.45f;
        // The veil's colours in scene units: both at the luminance of the fake world's image (about 0.42), as the frame driver's colours
        // are meant to be, so that the luminance-relative Host colour and the absolute Scene colour can agree.
        private static readonly Color VeilGround = new Color(0.40f, 0.42f, 0.45f), VeilSky = new Color(0.39f, 0.42f, 0.46f);
        private PrecipPass pass;
        private PrecipState state;
        private PrecipTuning tuning;
        private CommandBuffer sceneBuffer;
        private RenderTexture depthCopy;
        private Texture2D depthDummy;

        protected override void BuildContent()
        {
            Shader shader = Shader.Find("ReDefinition/AtmospheresPrecip");
            if (shader == null) throw new InvalidOperationException("the precipitation shader is not in the player");
            pass = new PrecipPass(shader);
            state = new PrecipState();
            tuning = PrecipTuning.Defaults();
            depthCopy = new RenderTexture(Width, Height, 0, RenderTextureFormat.RFloat) { filterMode = FilterMode.Point };
            depthCopy.Create();
            Cam.transform.position = new Vector3(2000f, 120f, -800f);
            Cam.transform.rotation = Quaternion.Euler(4f, 0f, 0f);
        }

        private PrecipInput Input(PrecipType type, float level, float windE, bool veil)
        {
            return new PrecipInput
            {
                Active = true, Type = type, Level = level, VisibilityM = Mathf.Lerp(8000f, 60f, level), WindE = windE, GustNoise = 0.5f, GustDirNoise = 0.5f,
                FlakeFallMs = 1.1f, FlakeBrightness = 1f, Seed = 11,
                Near = new FlakeLayer { Width = 7f, Share = 0.06f, SizeMin = 0.10f, SizeMax = 0.26f, Opacity = 0.5f },
                Mid = new FlakeLayer { Width = 18f, Share = 0.34f, SizeMin = 0.03f, SizeMax = 0.08f, Opacity = 0.95f },
                Far = new FlakeLayer { Width = 36f, Share = 0.60f, SizeMin = 0.02f, SizeMax = 0.05f, Opacity = 1f },
                VeilGroundAlpha = veil ? 0.6f * level : 0f, VeilSkyAlpha = veil ? 0.7f * level : 0f,
                VeilGroundColour = VeilGround, VeilSkyColour = VeilSky, SkyGreyStart = 0f, SkyGreyFull = 30f,
                Density = 1f, TypeEnabled = true, FovYRad = Cam.fieldOfView * Mathf.Deg2Rad, HeightPx = Height, Dt = 1f / 60f, GroundDistM = 1e6f
            };
        }

        private PrecipDraw Draw(bool host, float weight, double camE)
        {
            var d = new PrecipDraw
            {
                Host = host, Weight = weight, AxisE = Vector3.right, AxisU = Vector3.up, AxisN = Vector3.forward, TargetWidth = Width, TargetHeight = Height,
                CamE = camE, TimeMod60 = (float)(state.Time % 60.0), GroundDistance = 1e5f, Seed = 11,
                VeilGroundColour = VeilGround, VeilSkyColour = VeilSky, SkyGreyStartDeg = 0f, SkyGreyFullDeg = 30f,
                Grade = 0.55f, LumaGain = 1f, Dt = 1f / 60f, Tuning = tuning, View = Cam.worldToCameraMatrix,
                Projection = GL.GetGPUProjectionMatrix(Cam.projectionMatrix, true)
            };
            return d;
        }

        private void Attach(PrecipFrame frame, PrecipDraw draw)
        {
            Detach();
            sceneBuffer = new CommandBuffer { name = "precip-check" };
            pass.Record(sceneBuffer, ref frame, ref draw);
            Cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, sceneBuffer);
        }

        private void Detach()
        {
            if (sceneBuffer == null) return;
            Cam.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, sceneBuffer);
            sceneBuffer.Release();
            sceneBuffer = null;
        }

        private IEnumerator Grab(Action<LumaFrame> done)
        {
            yield return new WaitForEndOfFrame();
            done(LumaFrame.Read(Target));
        }

        // The Host composite on top of the finished frame, the way the mod's hook draws it.
        private IEnumerator GrabHost(PrecipFrame frame, PrecipDraw draw, Action<LumaFrame> done)
        {
            yield return new WaitForEndOfFrame();
            Texture depth = Shader.GetGlobalTexture("_CameraDepthTexture");
            if (depth != null) Graphics.Blit(depth, depthCopy);
            draw.Depth = depth != null ? depthCopy : null;
            // The mod smooths the image's luminance over 0.5 s; here 150 captures of the same finished frame stand for the seconds of
            // frames that precede a real one.
            for (int i = 0; i < 150; i++)
            {
                var warm = new CommandBuffer { name = "precip-check luma" };
                pass.RecordLuma(warm, Target, 1f / 60f, tuning.LumaTauS);
                Graphics.ExecuteCommandBuffer(warm);
                warm.Release();
            }
            var cmd = new CommandBuffer { name = "precip-check host" };
            if (frame.VeilGround > 0.001f || frame.VeilSky > 0.001f) pass.RecordLuma(cmd, Target, 1f / 60f, tuning.LumaTauS);
            cmd.SetRenderTarget(Target);
            pass.Record(cmd, ref frame, ref draw);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            done(LumaFrame.Read(Target));
        }

        private static float Shift(float[] a, float[] b, int width, int height, bool vertical, int range, int other)
        {
            double best = double.MinValue;
            int bestShift = 0;
            for (int s = -range; s <= range; s++)
            {
                double sum = 0;
                for (int y = 8; y < height - 8; y += 1)
                    for (int x = 8; x < width - 8; x += 1)
                    {
                        // other is the shift already known along the second axis (the fall, when the drift is searched).
                        int x2 = vertical ? x + other : x + s, y2 = vertical ? y + s : y + other;
                        if (x2 < 0 || x2 >= width || y2 < 0 || y2 >= height) continue;
                        sum += a[y * width + x] * b[y2 * width + x2];
                    }
                if (sum > best)
                {
                    best = sum;
                    bestShift = s;
                }
            }
            return bestShift;
        }

        // The best correlation to the east (shifts 1..range) over the best to the west, at the known vertical shift. Above 1 the pattern
        // moved east; the flakes sit at many depths and so move by many amounts, which is why the sides are compared and no one shift
        // is read.
        private static float EastBias(float[] a, float[] b, int width, int height, int range, int vertical)
        {
            double east = double.MinValue, west = double.MinValue;
            for (int s = -range; s <= range; s++)
            {
                if (s == 0) continue;
                double sum = 0;
                for (int y = 8; y < height - 8; y++)
                    for (int x = 8; x < width - 8; x++)
                    {
                        int x2 = x + s, y2 = y + vertical;
                        if (x2 < 0 || x2 >= width || y2 < 0 || y2 >= height) continue;
                        sum += a[y * width + x] * b[y2 * width + x2];
                    }
                if (s > 0) east = Math.Max(east, sum); else west = Math.Max(west, sum);
            }
            return (float)(east / Math.Max(west, 1e-9));
        }

        private static float[] Deviation(LumaFrame f)
        {
            var d = new float[f.Y.Length];
            for (int i = 0; i < d.Length; i++) d[i] = Mathf.Abs(f.Y[i] - Grey) > 0.02f ? Mathf.Abs(f.Y[i] - Grey) : 0f;
            return d;
        }

        private static int Count(float[] d)
        {
            int n = 0;
            foreach (float v in d) if (v > 0f) n++;
            return n;
        }

        private IEnumerator Settle(PrecipInput input, int frames)
        {
            PrecipFrame f;
            for (int i = 0; i < frames; i++)
            {
                PrecipLookParams.Compute(ref input, ref tuning, state, out f);
                yield return null;
            }
        }

        public override IEnumerator Run()
        {
            // --- Per type over a flat grey: nothing but the renderer is drawn.
            Cam.clearFlags = CameraClearFlags.SolidColor;
            Cam.backgroundColor = new Color(Grey, Grey, Grey);
            int savedMask = Cam.cullingMask;
            Cam.cullingMask = 0;
            Detach();
            LumaFrame none = null;
            yield return Grab(r => none = r);
            double meanNone = ImageStats.MeanLuma(none, ImageStats.Rect2.Whole(none));
            foreach (PrecipType type in PrecipTypeTable.Types)
            {
                string n = type.ToString();
                int knownGap = 1, knownFall = 0;
                foreach (bool windy in new[] { false, true })
                {
                    state = new PrecipState();
                    PrecipInput input = Input(type, 0.6f, windy ? 8f : 0f, false);
                    PrecipFrame f1 = default(PrecipFrame), f2 = default(PrecipFrame);
                    for (int i = 0; i < 30; i++) PrecipLookParams.Compute(ref input, ref tuning, state, out f1);
                    LumaFrame a = null, b = null;
                    PrecipDraw draw = Draw(false, 1f, 0.0);
                    Attach(f1, draw);
                    yield return Grab(r => a = r);
                    float speed = Mathf.Max(Mathf.Abs(f1.VelU), Mathf.Abs(f1.VelE));
                    int gap = windy ? 1 : Mathf.Clamp(Mathf.RoundToInt(8f / (Mathf.Max(speed, 0.05f) * 69f / 60f)), 1, 30);
                    if (!windy) knownGap = gap;
                    for (int i = 0; i < gap; i++) PrecipLookParams.Compute(ref input, ref tuning, state, out f2);
                    draw = Draw(false, 1f, 0.0);
                    draw.TimeMod60 = (float)(state.Time % 60.0);
                    Attach(f2, draw);
                    yield return Grab(r => b = r);
                    Detach();
                    float[] da = Deviation(a), db = Deviation(b);
                    if (!windy)
                    {
                        double mean = ImageStats.MeanLuma(a, ImageStats.Rect2.Whole(a));
                        int expected = PrecipTypeTable.LumaSign(type);
                        Sink.Flag(n + ".luma_sign", Math.Sign(mean - meanNone) == expected);
                        Sink.AtLeast(n + ".pixels", Count(da), 200);
                        float shift = Shift(da, db, a.Width, a.Height, true, 60, 0);
                        knownFall = (int)shift;
                        // Rows count upwards from the bottom of the picture: a falling pattern moves to lower rows.
                        Sink.Flag(n + ".falls_down", shift < 0f);
                        Sink.Info(n + ".fall_shift_px", shift);
                    }
                    else
                    {
                        float bias = EastBias(da, db, a.Width, a.Height, 60, Mathf.RoundToInt(knownFall / (float)knownGap));
                        Sink.AtLeast(n + ".drift_east_bias", bias, 1.02);
                    }
                }
            }

            // --- Below the lowest level nothing is drawn.
            {
                state = new PrecipState();
                PrecipInput input = Input(PrecipType.Snow, 0.039f, 0f, true);
                PrecipFrame f;
                PrecipLookParams.Compute(ref input, ref tuning, state, out f);
                Sink.Flag("below_0_04.no_draw", !f.Draw);
                var cmd = new CommandBuffer();
                PrecipDraw draw = Draw(false, 1f, 0.0);
                pass.Record(cmd, ref f, ref draw);
                Sink.Flag("below_0_04.empty_buffer", cmd.sizeInBytes == 0);
                cmd.Release();
            }
            Cam.cullingMask = savedMask;
            Cam.clearFlags = CameraClearFlags.Skybox;

            // --- Host against Scene, and the cross-fade, in the fake world.
            double worst = 0;
            foreach (float level in new[] { 0.3f, 0.6f, 1.0f })
            {
                state = new PrecipState();
                PrecipInput input = Input(PrecipType.Snow, level, 2f, true);
                PrecipFrame f = default(PrecipFrame);
                for (int i = 0; i < 30; i++) PrecipLookParams.Compute(ref input, ref tuning, state, out f);
                LumaFrame clear = null, scene = null, host = null;
                yield return Grab(r => clear = r);
                Attach(f, Draw(false, 1f, 0.0));
                yield return Grab(r => scene = r);
                Detach();
                yield return GrabHost(f, Draw(true, 1f, 0.0), r => host = r);
                double ms = ImageStats.MeanLuma(scene, ImageStats.Rect2.Whole(scene)), mh = ImageStats.MeanLuma(host, ImageStats.Rect2.Whole(host));
                double mc = ImageStats.MeanLuma(clear, ImageStats.Rect2.Whole(clear));
                double ratio = Math.Abs(mh - ms) / Math.Max(ms, 1e-4);
                worst = Math.Max(worst, ratio);
                Sink.AtMost("parity.level_" + level.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ".host_vs_scene", ratio, 0.05);
                Sink.Info("parity.level_" + level.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ".luma_clear_scene_host", mc * 1000 + ms * 1.0 + mh * 0.001);
            }
            Sink.AtMost("parity.worst", worst, 0.05);

            {
                state = new PrecipState();
                PrecipInput input = Input(PrecipType.Snow, 0.8f, 0f, true);
                input.Near.Width = input.Mid.Width = input.Far.Width = 0f;
                PrecipFrame f = default(PrecipFrame);
                for (int i = 0; i < 30; i++) PrecipLookParams.Compute(ref input, ref tuning, state, out f);
                f.Draw = true;
                LumaFrame pure = null, mixed = null;
                Attach(f, Draw(false, 1f, 0.0));
                yield return Grab(r => pure = r);
                Detach();
                Attach(f, Draw(false, 0.5f, 0.0));
                LumaFrame sceneHalf = null;
                yield return Grab(r => sceneHalf = r);
                Detach();
                // Half the Scene veil, then half the Host veil on top of that frame is what a cross-fade at 0.5 / 0.5 draws.
                Attach(f, Draw(false, 0.5f, 0.0));
                yield return GrabHost(f, Draw(true, 0.5f, 0.0), r => mixed = r);
                Detach();
                double mp = ImageStats.MeanLuma(pure, ImageStats.Rect2.Whole(pure)), mm = ImageStats.MeanLuma(mixed, ImageStats.Rect2.Whole(mixed));
                Sink.AtMost("switch.veil_ratio_dev", Math.Abs(mm - mp) / Math.Max(mp, 1e-4), 0.05);
            }
        }
    }
}
