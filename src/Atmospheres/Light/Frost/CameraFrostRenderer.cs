using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The camera frost: frost growing in from the edges, flakes stuck on gust peaks and sliding drops on the glass of a kerbal's visor
    // (EVA first person: the stock first-person view or ThroughTheEyes) and of the windows of a cabin (IVA). A module of the atmospheres,
    // setting cameraFrost. The state machine (CameraFrost) is stepped here from the weather view; the look is FrostPass and its shader.
    //
    // Two ways to draw, one look:
    //   Host  after the upscaler through ReDefinition.Api (IPostUpscaleCompositor, the plain after-upscaling hook, no depth): the frost
    //         is not in the picture the upscaler sees, so it cannot ghost, and it refracts and scatters a half-size copy of the
    //         upscaled image. Needs the main mod. In the IVA it needs the interior mask (FrostInteriorMask), because the rig upscales
    //         the internal camera together with the flight camera and the props must stay clear.
    //   Quad  Graphics.DrawMesh at queue 3500 on the flight camera, a plain alpha overlay without refraction. Works alone; in the IVA
    //         it sits under the interior (the internal camera draws after the flight camera).
    // The host composite is used when the hook answers (it was called in the last three frames) and, in the IVA, the mask is valid.
    //
    // Motion vectors: none, the overlay is fixed to the screen. Depth: none. Masks: the draw is after the upscaler, so the upscaler's
    // masks do not apply; the Quad fallback is in the scene and relies on the frost being slow. Frame generation: the host composite is
    // part of the image before the HUD-less copy, so it is interpolated with the scene. Raytracing: not an input. Order: after the
    // upscaler, before TUFX's effects after it. Exposure: the colours are relative to the image's (scene side); the Quad fallback uses a
    // colour in scene units. Zero cost inactive: below frost.skipBelow exposure with no flake or drop, no command is recorded.
    [AtmospheresModule(62)]
    internal sealed class CameraFrostRenderer : IAtmospheresModule
    {
        internal const string ShaderName = "ReDefinition/AtmospheresFrost";
        private const int FailureLimit = 5;
        private const int HostAliveFrames = 3;
        private const float SampleEveryS = 0.25f;

        private FrostPass pass;
        private Shader shader;
        private Texture2D atlas;
        private byte[] builtAtlas;
        private double atlasBuildMs = -1;
        private readonly CameraFrost state = new CameraFrost();
        private readonly FrostInteriorMask interior = new FrostInteriorMask();
        private FrostDraw draw;
        private bool drawReady;
        private PostUpscaleDraw hostDraw;
        private bool hostRegistered;
        private bool hostFailed;
        private int lastHostCallFrame = -100;
        private bool initialised;
        private int failures;
        private Guid lastVessel;
        private int lastBody = -1;
        private float sampleAge = 1e9f;
        private float temperatureK = float.NaN;
        private FrostView lastView = FrostView.Off;
        private string lastModeLogged = "";

        // The state for diagnostics and the series.
        internal static FrostView ActiveView { get; private set; }
        internal static string ActiveMode { get; private set; }
        internal static float LastExposure { get; private set; }
        internal static float LastGrowth { get; private set; }
        internal static int LastFlakes { get; private set; }
        internal static int LastDrops { get; private set; }
        // Frames in which a command was recorded or a quad was queued; stays put while the frost is inactive.
        internal static int DrawnFrames { get; private set; }
        internal static bool HostComposed { get; private set; }
        internal static bool IvaMaskValid { get; private set; }
        internal static double AtlasBuildMs { get; private set; }

        public void Init()
        {
            string reason;
            shader = AtmosphereShaders.Get(ShaderName, out reason);
            if (shader == null)
            {
                AtmospheresLog.WarnOnce("camera frost: the shader " + ShaderName + " is not available (" + reason + "); the frost stays off");
                return;
            }
            if (Services.PostUpscale is NullPostUpscaleCompositor) Services.PostUpscale = new ApiBridge();
            hostDraw = HostCompose;
            // The atlas is painted on a worker thread (pure maths), uploaded by Tick.
            ThreadPool.QueueUserWorkItem(delegate
            {
                var watch = Stopwatch.StartNew();
                byte[] pixels = FrostAtlas.Build();
                double ms = watch.Elapsed.TotalMilliseconds;
                atlasBuildMs = ms;
                Volatile.Write(ref builtAtlas, pixels);
            });
            initialised = true;
            ActiveMode = "none";
            AtmospheresLog.Say("camera frost: ready (host hook " + (Services.PostUpscale.HostAvailable ? "found" : "absent") + ")");
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
            state.Reset();
            temperatureK = float.NaN;
            sampleAge = 1e9f;
            drawReady = false;
        }

        public void Shutdown()
        {
            if (hostRegistered) Services.PostUpscale.Unregister(hostDraw);
            hostRegistered = false;
            interior.Dispose();
            if (pass != null) pass.Dispose();
            pass = null;
            if (atlas != null) UnityEngine.Object.Destroy(atlas);
            atlas = null;
            initialised = false;
            drawReady = false;
            ActiveMode = "none";
        }

        // The tuning constants, read once a frame. Keys, defaults and ranges are in FrostTuning.
        internal static FrostTuning ReadTuning()
        {
            FrostTuning t = FrostTuning.Defaults();
            t.BuildS = Tuning.Get("frost.buildS", t.BuildS);
            t.ClearS = Tuning.Get("frost.clearS", t.ClearS);
            t.MaxGrowth = Tuning.Get("frost.maxGrowth", t.MaxGrowth);
            t.GrowthPow = Tuning.Get("frost.growthPow", t.GrowthPow);
            t.Gain = Tuning.Get("frost.gain", t.Gain);
            t.WindRefMs = Tuning.Get("frost.windRefMs", t.WindRefMs);
            t.ColdWidthK = Tuning.Get("frost.coldWidthK", t.ColdWidthK);
            t.CabinK = Tuning.Get("frost.cabinK", t.CabinK);
            t.IvaInsulation = Tuning.Get("frost.ivaInsulation", t.IvaInsulation);
            t.IvaLagS = Tuning.Get("frost.ivaLagS", t.IvaLagS);
            t.EvaLagS = Tuning.Get("frost.evaLagS", t.EvaLagS);
            t.FlakeMax = (int)Tuning.Get("frost.flakeMax", t.FlakeMax);
            t.FlakePxMin = Tuning.Get("frost.flakePxMin", t.FlakePxMin);
            t.FlakePxMax = Tuning.Get("frost.flakePxMax", t.FlakePxMax);
            t.FlakeLifeMin = Tuning.Get("frost.flakeLifeMin", t.FlakeLifeMin);
            t.FlakeLifeMax = Tuning.Get("frost.flakeLifeMax", t.FlakeLifeMax);
            t.FlakeGust = Tuning.Get("frost.flakeGust", t.FlakeGust);
            t.FlakePerGust = Tuning.Get("frost.flakePerGust", t.FlakePerGust);
            t.DropMax = (int)Tuning.Get("frost.dropMax", t.DropMax);
            t.DropRate = Tuning.Get("frost.dropRate", t.DropRate);
            t.DropPx = Tuning.Get("frost.dropPx", t.DropPx);
            t.DropPxMax = Tuning.Get("frost.dropPxMax", t.DropPxMax);
            t.DropSpeed = Tuning.Get("frost.dropSpeed", t.DropSpeed);
            t.DropTrail = Tuning.Get("frost.dropTrail", t.DropTrail);
            t.SkipBelow = Tuning.Get("frost.skipBelow", t.SkipBelow);
            t.Jag = Tuning.Get("frost.jag", t.Jag);
            t.Centre = Tuning.Get("frost.centre", t.Centre);
            t.Opacity = Tuning.Get("frost.opacity", t.Opacity);
            t.AtlasScale = Tuning.Get("frost.atlasScale", t.AtlasScale);
            t.Refraction = Tuning.Get("frost.refraction", t.Refraction);
            t.Blur = Tuning.Get("frost.blur", t.Blur);
            t.Whiten = Tuning.Get("frost.whiten", t.Whiten);
            t.SunShare = Tuning.Get("frost.sunShare", t.SunShare);
            t.Ambient = Tuning.Get("frost.ambient", t.Ambient);
            t.TintR = Tuning.Get("frost.tintR", t.TintR);
            t.TintG = Tuning.Get("frost.tintG", t.TintG);
            t.TintB = Tuning.Get("frost.tintB", t.TintB);
            t.DropGain = Tuning.Get("frost.dropGain", t.DropGain);
            t.QuadAlpha = Tuning.Get("frost.quadAlpha", t.QuadAlpha);
            t.FlipY = Tuning.Flag("frost.flipY", false);
            return t.Clamped();
        }

        private static FrostCameraMode CameraMode()
        {
            CameraManager manager = CameraManager.Instance;
            if (manager == null) return FrostCameraMode.Other;
            switch (manager.currentCameraMode)
            {
                case CameraManager.CameraMode.Flight: return FrostCameraMode.Flight;
                case CameraManager.CameraMode.IVA: return FrostCameraMode.Iva;
                case CameraManager.CameraMode.Internal: return FrostCameraMode.Internal;
                case CameraManager.CameraMode.Map: return FrostCameraMode.Map;
                default: return FrostCameraMode.Other;
            }
        }

        public void Tick(float dt)
        {
            if (!initialised) return;
            try
            {
                Step(dt);
                failures = 0;
            }
            catch (Exception e)
            {
                failures++;
                AtmospheresLog.Failed("camera-frost", "stepping the camera frost failed", e);
                drawReady = false;
                if (failures >= FailureLimit)
                {
                    AtmospheresLog.WarnOnce("camera frost: failed " + FailureLimit + " frames in a row; the frost is switched off");
                    Shutdown();
                }
            }
        }

        private void EnsurePass()
        {
            if (pass != null || shader == null) return;
            byte[] pixels = Volatile.Read(ref builtAtlas);
            if (pixels == null) return;
            atlas = FrostAtlas.MakeTexture(pixels);
            pass = new FrostPass(shader, atlas);
            AtlasBuildMs = atlasBuildMs;
            AtmospheresLog.Say("camera frost: atlas " + FrostAtlas.Size + " x " + FrostAtlas.Size + " built in "
                               + atlasBuildMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
        }

        private void Step(float dt)
        {
            EnsurePass();
            Vessel vessel = FlightGlobals.ActiveVessel;
            Camera flight = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            bool flightScene = HighLogic.LoadedSceneIsFlight && vessel != null && vessel.mainBody != null && flight != null;
            bool settingsOn = AtmospheresSettings.Enabled && AtmospheresSettings.WeatherEffects && AtmospheresSettings.CameraFrost;
            drawReady = false;
            if (!flightScene || !settingsOn || pass == null)
            {
                if (!flightScene) state.Reset();
                interior.Update(false, pass);
                Idle();
                return;
            }

            CelestialBody body = vessel.mainBody;
            BodyClimate climate = Bodies.Get(body);
            FrostRules rules = climate != null ? climate.Frost : FrostRules.Default;
            if (vessel.id != lastVessel || body.flightGlobalsIndex != lastBody)
            {
                state.Reset();
                temperatureK = float.NaN;
                sampleAge = 1e9f;
                lastVessel = vessel.id;
                lastBody = body.flightGlobalsIndex;
            }

            FrostTuning t = ReadTuning();
            WeatherView view = WeatherHooks.View;
            bool live = view.Active && !view.IsStale(Time.frameCount);
            bool eva = vessel.isEVA;
            float toVessel = (float)(((Vector3d)flight.transform.position) - vessel.GetWorldPos3D()).magnitude;
            FrostView where = FrostCameraClass.Classify(CameraMode(), MapView.MapIsEnabled, eva, toVessel, FrostCameraClass.FirstPersonM);
            if (where != lastView)
            {
                AtmospheresLog.Say("camera frost: view " + lastView + " -> " + where);
                lastView = where;
            }

            // The air temperature from the field, a few times a second.
            sampleAge += Time.unscaledDeltaTime;
            if (sampleAge >= SampleEveryS)
            {
                sampleAge = 0f;
                Vector3d position = flight.transform.position;
                WeatherSample sample;
                if (WeatherHub.Sample(body.flightGlobalsIndex, body.GetLatitude(position), body.GetLongitude(position), body.GetAltitude(position),
                        Planetarium.GetUniversalTime(), out sample))
                    temperatureK = sample.TemperatureK;
                else
                    temperatureK = float.NaN;
            }

            // Down the picture: the direction of gravity in the plane of the camera, so that a rolled camera slides the drops down.
            Vector3 down = (body.position - (Vector3d)flight.transform.position).normalized;
            float gx = Vector3.Dot(down, flight.transform.right), gy = Vector3.Dot(down, flight.transform.up);

            var input = new FrostInput
            {
                Dt = Time.timeScale > 0f ? Mathf.Min(Time.unscaledDeltaTime, 0.25f) : 0f,
                View = where,
                Level = live ? view.Level : 0f,
                Type = live ? view.Type : PrecipType.None,
                TemperatureK = temperatureK,
                FrostLimitK = rules.MaxTemperatureK,
                Strength = rules.Enabled ? rules.Strength : 0f,
                AirflowMs = (float)vessel.srfSpeed + (live ? new Vector2(view.WindWorld.x, view.WindWorld.y).magnitude : 0f),
                GustPhase = live ? view.GustPhase : 0f,
                Sheltered = !live || view.Level < CameraFrost.WeatherFrom,
                GravityX = gx,
                GravityY = gy,
                GravityInPlane = Mathf.Sqrt(gx * gx + gy * gy),
                Seed = live ? view.FlakeSeed : 0
            };
            state.Step(ref input, ref t);

            LastExposure = state.Exposure;
            LastGrowth = state.Growth;
            LastFlakes = state.FlakeCount;
            LastDrops = state.DropCount;
            ActiveView = where;

            bool wantDraw = where != FrostView.Off && state.NeedsDraw(ref t);
            bool ivaWanted = where == FrostView.Iva && wantDraw && Tuning.Flag("frost.ivaMask", true) && Services.PostUpscale.HostAvailable;
            interior.Update(ivaWanted, pass);
            IvaMaskValid = ivaWanted && interior.Mask != null && interior.BuiltFrame >= 0 && Time.frameCount - interior.BuiltFrame > 2;
            if (!wantDraw)
            {
                Idle();
                return;
            }

            // The draw: the numbers the shader reads.
            if (draw.Flakes == null) draw.Flakes = new Vector4[CameraFrost.MaxFlakes];
            if (draw.Drops == null) draw.Drops = new Vector4[CameraFrost.MaxDrops];
            int nf = 0;
            FrostFlake[] flakes = state.Flakes;
            for (int i = 0; i < flakes.Length; i++)
            {
                if (!flakes[i].Alive) continue;
                float age01 = Mathf.Min(flakes[i].Age / flakes[i].Life, 0.999f);
                draw.Flakes[nf++] = new Vector4(flakes[i].X, flakes[i].Y, flakes[i].Size, flakes[i].Variant + age01);
            }
            int nd = 0;
            FrostDrop[] drops = state.Drops;
            for (int i = 0; i < drops.Length; i++)
            {
                if (!drops[i].Alive) continue;
                draw.Drops[nd++] = new Vector4(drops[i].X, drops[i].Y, drops[i].Size, drops[i].Trail);
            }
            draw.FlakeCount = nf;
            draw.DropCount = nd;
            draw.Growth = state.Growth * Mathf.Min(rules.Strength, 1f);
            draw.Exposure = state.Exposure;
            draw.Gravity = new Vector2(gx, gy);
            draw.Tuning = t;
            draw.FlipY = t.FlipY;
            Vector3d sun = FlightGlobals.Bodies != null && FlightGlobals.Bodies.Count > 0 && FlightGlobals.Bodies[0] != null
                ? (FlightGlobals.Bodies[0].position - vessel.GetWorldPos3D()).normalized
                : vessel.upAxis;
            float elevation = Mathf.Asin(Mathf.Clamp((float)Vector3d.Dot(sun, vessel.upAxis), -1f, 1f));
            draw.Light = FrostLight.Factor(elevation, live ? view.SunTransmission : 1f, ref t);
            float c = 0.15f + 0.7f * draw.Light;
            draw.QuadColour = new Color(t.TintR * c, t.TintG * c, t.TintB * c, 1f);
            draw.InteriorMask = null;
            draw.Aspect = flight.aspect;
            drawReady = true;

            bool hostAlive = hostRegistered && !hostFailed && Time.frameCount - lastHostCallFrame <= HostAliveFrames;
            bool hostUsable = hostAlive && (where == FrostView.Eva || IvaMaskValid);
            string mode = hostUsable ? "host" : "quad";
            if (mode != lastModeLogged)
            {
                AtmospheresLog.Say("camera frost: drawing in " + mode + " mode (" + where + ", exposure "
                                   + state.Exposure.ToString("0.00", CultureInfo.InvariantCulture) + ")");
                lastModeLogged = mode;
            }
            ActiveMode = mode;
            HostComposed = hostUsable;
            if (!hostUsable)
            {
                pass.DrawQuad(flight, ref draw);
                DrawnFrames++;
            }

            if (!hostRegistered && !hostFailed && Services.PostUpscale.HostAvailable)
            {
                hostRegistered = Services.PostUpscale.Register("camera-frost", hostDraw, false);
                if (!hostRegistered) hostFailed = true;
            }
        }

        private void Idle()
        {
            drawReady = false;
            HostComposed = false;
            ActiveMode = "none";
            IvaMaskValid = false;
            if (!hostRegistered && !hostFailed && initialised && Services.PostUpscale.HostAvailable)
            {
                hostRegistered = Services.PostUpscale.Register("camera-frost", hostDraw, false);
                if (!hostRegistered) hostFailed = true;
            }
        }

        // The draw the host calls after the upscaler. Records the composite from the frame Tick prepared.
        private void HostCompose(CommandBuffer cmd, RenderTexture upscaled, RenderTexture depth, Camera camera)
        {
            try
            {
                lastHostCallFrame = Time.frameCount;
                Camera flight = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
                if (pass == null || camera != flight || !drawReady || ActiveMode != "host" || upscaled == null) return;
                FrostDraw d = draw;
                d.Aspect = (float)upscaled.width / Mathf.Max(upscaled.height, 1);
                d.InteriorMask = ActiveView == FrostView.Iva ? interior.Mask : null;
                cmd.BeginSample("ReDefinition frost host");
                pass.RecordHost(cmd, upscaled, upscaled.width, upscaled.height, ref d);
                cmd.EndSample("ReDefinition frost host");
                DrawnFrames++;
            }
            catch (Exception e)
            {
                hostFailed = true;
                AtmospheresLog.Failed("camera-frost-host", "the host composite failed and is dropped", e);
            }
        }
    }
}
