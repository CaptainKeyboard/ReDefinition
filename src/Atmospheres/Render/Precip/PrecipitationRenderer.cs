using System;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The Atmospheres' own precipitation: the veil and the flakes of any type, drawn from the weather view with a path that the
    // upscalers and frame generation leave alone. A module (started by the registry), no game object.
    //
    // Two ways to compose, one look (PrecipPass and one shader):
    //   Host   after the upscaler through ReDefinition.Api (IPostUpscaleCompositor), with the render-size depth: nothing the
    //          upscaler could smear, because the flakes are not in the picture it sees. Needs the main mod.
    //   Scene  a command buffer on the flight camera before the upscaler, hardware depth, a size floor of 3 px and a low contrast for
    //          the floored flakes. Works alone.
    // weatherCompose Auto takes Host only where the vendor's player metrics are recorded (Tuning precip.hostProven.<vendor>) and the
    // hook answers; the hook disappearing hands over to Scene within PrecipCompose.FadeSeconds.
    //
    // Motion vectors: none written for the flakes. Depth: the host's render-size copy (nearest of 3x3 taps), or the scene's. Masks: no
    // scene object exists, so nothing is added to the upscaler's mask lists, and the mask (UpscalerMasks collects scene renderers
    // with FindObjectsOfType) does not see the command-buffer draw of the Scene mode: the Scene flakes rely on the size floor and the
    // low contrast alone. Whether they need a mask contribution is E40's decision [VERIFY]. Frame generation: the composite is part of the image
    // before the HUD-less copy, so the shadow layer of the vessel (applied to the copy) comes after it. Raytracing: not traced.
    // Exposure: the veil's colour is relative to the image's luminance in Host mode (the exposure was applied before the upscaler)
    // [VERIFY against TUFX in the game]. Order: after the upscaler, before the post-processing of TUFX.
    [AtmospheresModule(60)]
    internal sealed class PrecipitationRenderer : IAtmospheresModule
    {
        internal const string ShaderName = "ReDefinition/AtmospheresPrecip";
        private const int FailureLimit = 5;

        private PrecipPass pass;
        private readonly PrecipState state = new PrecipState();
        private readonly PrecipCompose compose = new PrecipCompose();
        private readonly PrecipCameraTrack track = new PrecipCameraTrack();
        private CommandBuffer sceneBuffer;
        private Camera sceneCamera;
        private PostUpscaleDraw hostDraw;
        private bool hostRegistered;
        private bool hostFailed;
        private int lastHostCallFrame = -1;
        private int lastComposeFrame = -1;
        private bool suppressing;
        private bool initialised;
        private int failures;
        private PrecipFrame frame;
        private PrecipDraw draw;
        private bool haveFrame;
        private WeatherCompose lastMode = WeatherCompose.Auto;
        private PrecipType loggedType = PrecipType.None;
        private BodyFrame basis;

        // The state for diagnostics and the series.
        internal static bool Drawing { get; private set; }
        internal static int LastTotalCount { get; private set; }
        internal static WeatherCompose ActiveMode { get; private set; }
        internal static float HostWeight { get; private set; }
        internal static float SceneWeight { get; private set; }

        public void Init()
        {
            string reason;
            Shader shader = AtmosphereShaders.Get(ShaderName, out reason);
            if (shader == null)
            {
                AtmospheresLog.WarnOnce("precipitation: the shader " + ShaderName + " is not available (" + reason + "); the renderer stays off");
                return;
            }
            pass = new PrecipPass(shader);
            if (Services.PostUpscale is NullPostUpscaleCompositor) Services.PostUpscale = new ApiBridge();
            hostDraw = HostCompose;
            Camera.onPreCull += OnPreCull;
            initialised = true;
            AtmospheresLog.Say("precipitation: renderer ready (host hook " + (Services.PostUpscale.HostAvailable ? "found" : "absent")
                               + ", depth " + (Services.PostUpscale.DepthAvailable ? "found" : "absent") + ")");
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
            state.Reset();
            track.Reset();
        }

        public void Tick(float dt)
        {
            if (!initialised) return;
            if (!hostRegistered && !hostFailed && Services.PostUpscale.DepthAvailable)
            {
                hostRegistered = Services.PostUpscale.Register("precipitation", hostDraw, true);
                if (!hostRegistered) hostFailed = true;
            }
        }

        public void Shutdown()
        {
            Camera.onPreCull -= OnPreCull;
            DetachScene();
            if (hostRegistered) Services.PostUpscale.Unregister(hostDraw);
            hostRegistered = false;
            SetSuppressed(false, "shutdown");
            if (pass != null) pass.Dispose();
            pass = null;
            initialised = false;
        }

        // The tuning constants, read once a frame (a table lookup each). Keys, default, tuning range for E40:
        //   precip.maxPerLayer 8000 (500-20000)   precip.maxTotal 20000 (2000-40000)   precip.fill 0.35 (0.1-0.8)
        //   precip.floorPx 3 (1-6)   precip.sceneMinContrast 0.45 (0.2-0.8)   precip.hostMinContrast 0.85 (0.5-1)
        //   precip.edgeShare 0.12 (0-0.4)   precip.gustScale 1 (0-2)   precip.hostFlakeGain 1 (0.5-2)
        //   precip.splashMax 1500 (0-4000)   precip.splashRangeM 40 (10-100)   precip.splashMaxGroundM 200 (50-500)
        //   precip.veilLumaFloor 0.02 (0-0.1)   precip.lumaTauS 0.5 (0.2-2)   precip.veilLumaGain 1 (0.5-1.5)
        //   precip.grade 0.55 (0-1)   flags precip.depthFlipY false, precip.hostProven.<nvidia|amd|intel|other> false
        //   fog.gustAmp 0.1 (0-0.12)   fog.gustPeriodS 6 (3-20)   fog.hertz 3 (0.5-3)
        // The ranges are the tuning ranges proposed here, not enforced; fog.gustAmp and fog.hertz are capped by FogRamp (12 %, 3 Hz).
        internal static PrecipTuning ReadTuning()
        {
            PrecipTuning t = PrecipTuning.Defaults();
            t.MaxPerLayer = Tuning.Get("precip.maxPerLayer", t.MaxPerLayer);
            t.Fill = Tuning.Get("precip.fill", t.Fill);
            t.FloorPx = Tuning.Get("precip.floorPx", t.FloorPx);
            t.SceneMinContrast = Tuning.Get("precip.sceneMinContrast", t.SceneMinContrast);
            t.HostMinContrast = Tuning.Get("precip.hostMinContrast", t.HostMinContrast);
            t.EdgeShare = Tuning.Get("precip.edgeShare", t.EdgeShare);
            t.GustScale = Tuning.Get("precip.gustScale", t.GustScale);
            t.MaxTotal = Tuning.Get("precip.maxTotal", t.MaxTotal);
            t.SplashMax = Tuning.Get("precip.splashMax", t.SplashMax);
            t.SplashRangeM = Tuning.Get("precip.splashRangeM", t.SplashRangeM);
            t.SplashMaxGroundM = Tuning.Get("precip.splashMaxGroundM", t.SplashMaxGroundM);
            t.HostFlakeGain = Tuning.Get("precip.hostFlakeGain", t.HostFlakeGain);
            t.VeilLumaFloor = Tuning.Get("precip.veilLumaFloor", t.VeilLumaFloor);
            t.LumaTauS = Tuning.Get("precip.lumaTauS", t.LumaTauS);
            t.VeilLumaGain = Tuning.Get("precip.veilLumaGain", t.VeilLumaGain);
            return t;
        }

        // Whether EVE's particle fields are suppressed (CONTRACT section 5): where the precipitation is ours (Ours, None) always; with
        // Eve or Both only while precipDensity is on.
        internal static bool SuppressEveFields(CloudSourceKind source, float precipDensity)
        {
            if (source == CloudSourceKind.Ours || source == CloudSourceKind.None) return true;
            return precipDensity > 0f;
        }

        private static bool TypeEnabled(PrecipType type)
        {
            switch (type)
            {
                case PrecipType.Snow: return AtmospheresSettings.PrecipSnow;
                case PrecipType.Rain:
                case PrecipType.FreezingRain: return AtmospheresSettings.PrecipRain;
                case PrecipType.Sleet: return AtmospheresSettings.PrecipSleet;
                case PrecipType.Dust: return AtmospheresSettings.PrecipDust;
                case PrecipType.Sand: return AtmospheresSettings.PrecipSand;
                case PrecipType.Ash: return AtmospheresSettings.PrecipAsh;
                case PrecipType.Methane: return AtmospheresSettings.PrecipMethane;
                default: return false;
            }
        }

        // The vendor key of the proven-pair table (precip.hostProven.<vendor>).
        private static string VendorKey()
        {
            string v = (SystemInfo.graphicsDeviceVendor ?? "").ToLowerInvariant();
            if (v.Contains("nvidia")) return "nvidia";
            if (v.Contains("amd") || v.Contains("ati")) return "amd";
            if (v.Contains("intel")) return "intel";
            return "other";
        }

        private void OnPreCull(Camera cam)
        {
            if (!initialised || pass == null) return;
            Camera flight = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (flight == null || cam != flight) return;
            if (lastComposeFrame == Time.frameCount) return;
            lastComposeFrame = Time.frameCount;
            try
            {
                Compose(cam);
                failures = 0;
            }
            catch (Exception e)
            {
                failures++;
                AtmospheresLog.Failed("precipitation", "composing the precipitation failed", e);
                Drawing = false;
                haveFrame = false;
                DetachScene();
                if (failures >= FailureLimit)
                {
                    AtmospheresLog.WarnOnce("precipitation: failed " + FailureLimit + " frames in a row; the renderer is switched off");
                    Shutdown();
                }
            }
        }

        private void Compose(Camera cam)
        {
            WeatherView view = WeatherHooks.View;
            Vessel vessel = FlightGlobals.ActiveVessel;
            bool settingsOn = AtmospheresSettings.Enabled && AtmospheresSettings.WeatherEffects;
            bool live = settingsOn && vessel != null && vessel.mainBody != null && !view.IsStale(Time.frameCount);

            // The mode and its cross-fade.
            bool hostUsable = hostRegistered && !hostFailed && Services.PostUpscale.DepthAvailable
                              && !(compose.HostWeight > 0f && lastHostCallFrame >= 0 && Time.frameCount - lastHostCallFrame > 3 && haveFrame && Drawing);
            bool pairProven = Tuning.Flag("precip.hostProven." + VendorKey(), false);
            float dt = Time.timeScale > 0f ? Mathf.Min(Time.unscaledDeltaTime, 0.25f) : 0f;
            WeatherCompose mode = compose.Step(AtmospheresSettings.WeatherCompose, hostUsable, pairProven, Time.unscaledDeltaTime);
            ActiveMode = mode;
            HostWeight = compose.HostWeight;
            SceneWeight = compose.SceneWeight;
            if (mode != lastMode)
            {
                AtmospheresLog.Say("precipitation: composing in " + mode + " mode (setting " + AtmospheresSettings.WeatherCompose + ", host hook "
                                   + (hostUsable ? "answering" : "not available") + ")");
                lastMode = mode;
            }
            if (compose.TakeLostNotice()) AtmospheresLog.WarnOnce("precipitation: the host hook stopped answering; the Scene composite takes over");

            if (!live)
            {
                Drawing = false;
                haveFrame = false;
                state.GustFactor = PrecipMath.Slew(state.GustFactor, 1f, PrecipMath.GustSlewPerS, dt);
                DetachScene();
                SetSuppressed(false, "no weather");
                return;
            }

            CelestialBody body = vessel.mainBody;
            PrecipTuning tuning = ReadTuning();

            // The frame of the box: east, up and north at the camera, in the world and in the body's own coordinates.
            Vector3d camWorld = cam.transform.position;
            BodyFrame.Compute(body, camWorld, ref basis);

            // Where the camera is in the frame, from its movement (free of floating-origin shifts).
            Vector3d local = body.transform.InverseTransformPoint(camWorld);
            Vector3d eL = body.transform.InverseTransformDirection(basis.East), uL = body.transform.InverseTransformDirection(basis.Up);
            Vector3d nL = body.transform.InverseTransformDirection(basis.North);
            track.Update(local.x, local.y, local.z, eL.x, eL.y, eL.z, uL.x, uL.y, uL.z, nL.x, nL.y, nL.z);

            var input = new PrecipInput
            {
                Active = view.Active,
                Type = view.Type,
                Level = view.Level,
                VisibilityM = view.VisibilityM,
                WindE = view.WindWorld.x,
                WindU = view.WindWorld.z,
                WindN = view.WindWorld.y,
                GustNoise = view.GustPhase,
                GustDirNoise = 0.5f + 0.5f * Mathf.Sin(6.2831853f * view.GustPhase * 1.618f),
                FlakeFallMs = view.FlakeFallMS,
                FlakeBrightness = view.FlakeBrightness,
                Seed = view.FlakeSeed,
                Near = view.FlakeLayers[0],
                Mid = view.FlakeLayers[1],
                Far = view.FlakeLayers[2],
                VeilGroundAlpha = view.VeilGroundAlpha,
                VeilSkyAlpha = view.VeilSkyAlpha,
                VeilGroundColour = view.VeilGroundColour,
                VeilSkyColour = view.VeilSkyColour,
                SkyGreyStart = view.SkyGreyStart,
                SkyGreyFull = view.SkyGreyFull,
                Density = AtmospheresSettings.PrecipDensity,
                TypeEnabled = TypeEnabled(view.Type),
                FovYRad = cam.fieldOfView * Mathf.Deg2Rad,
                HeightPx = cam.pixelHeight,
                Dt = dt,
                GroundDistM = vessel.heightFromTerrain >= 0f ? vessel.heightFromTerrain : 1e6f
            };
            PrecipLookParams.Compute(ref input, ref tuning, state, out frame);

            // Fog gust is the fog writer's; the renderer reports the gust it used.
            haveFrame = frame.Draw;
            Drawing = frame.Draw;
            LastTotalCount = frame.TotalCount;

            draw = new PrecipDraw
            {
                Weight = 1f,
                AxisE = basis.East,
                AxisU = basis.Up,
                AxisN = basis.North,
                TargetWidth = cam.pixelWidth,
                TargetHeight = cam.pixelHeight,
                CamE = track.E,
                CamU = track.U,
                CamN = track.N,
                TimeMod60 = (float)(state.Time % 60.0),
                GroundDistance = Mathf.Min(input.GroundDistM, 1e5f),
                Seed = view.FlakeSeed,
                VeilGroundColour = view.VeilGroundColour,
                VeilSkyColour = view.VeilSkyColour,
                SkyGreyStartDeg = view.SkyGreyStart,
                SkyGreyFullDeg = Mathf.Max(view.SkyGreyFull, view.SkyGreyStart + 1f),
                Grade = Tuning.Get("precip.grade", 0.55f),
                LumaGain = AtmospheresSettings.FogBrightness,
                Dt = dt,
                DepthFlipY = Tuning.Flag("precip.depthFlipY", false),
                Tuning = tuning
            };

            if (frame.Draw && view.Type != loggedType)
            {
                loggedType = view.Type;
                AtmospheresLog.Say("precipitation: " + view.Type + " at level " + view.Level.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                                   + ", " + frame.TotalCount + " billboards");
            }
            if (!frame.Draw) loggedType = PrecipType.None;

            // EVE's own particle fields follow the cloudSource rule of CONTRACT section 5, not what is drawn this frame.
            BodyClimate climate = Bodies.Get(body);
            CloudSourceKind source = EveRenderCompat.EffectiveSource((climate != null ? climate.Clouds.Source : null) ?? AtmospheresSettings.CloudSource);
            SetSuppressed(SuppressEveFields(source, AtmospheresSettings.PrecipDensity), "cloudSource " + source + ", precipDensity "
                          + AtmospheresSettings.PrecipDensity.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));

            // The Scene composite: a command buffer on the flight camera, only while it has a share of the picture to draw.
            if (frame.Draw && compose.SceneWeight > 0f)
            {
                AttachScene(cam);
                sceneBuffer.Clear();
                draw.Host = false;
                draw.Weight = compose.SceneWeight;
                sceneBuffer.BeginSample("ReDefinition precip scene");
                pass.Record(sceneBuffer, ref frame, ref draw);
                sceneBuffer.EndSample("ReDefinition precip scene");
            }
            else
            {
                DetachScene();
            }
        }

        // The draw the host calls after the upscaler. Records the Host composite from the frame that onPreCull computed.
        private void HostCompose(CommandBuffer cmd, RenderTexture upscaled, RenderTexture depth, Camera camera)
        {
            try
            {
                lastHostCallFrame = Time.frameCount;
                Camera flight = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
                if (pass == null || camera != flight || !haveFrame || !frame.Draw || compose.HostWeight <= 0f || upscaled == null) return;
                PrecipDraw d = draw;
                d.Host = true;
                d.Weight = compose.HostWeight;
                d.View = camera.worldToCameraMatrix;
                d.Projection = GL.GetGPUProjectionMatrix(camera.nonJitteredProjectionMatrix, true);
                d.TargetWidth = upscaled.width;
                d.TargetHeight = upscaled.height;
                d.Depth = depth;
                cmd.BeginSample("ReDefinition precip host");
                bool veil = frame.VeilGround > 0.001f || frame.VeilSky > 0.001f;
                if (veil) pass.RecordLuma(cmd, upscaled, d.Dt, d.Tuning.LumaTauS);
                cmd.SetRenderTarget(upscaled);
                pass.Record(cmd, ref frame, ref d);
                cmd.EndSample("ReDefinition precip host");
            }
            catch (Exception e)
            {
                hostFailed = true;
                AtmospheresLog.Failed("precipitation-host", "the host composite failed and is dropped", e);
            }
        }

        private void AttachScene(Camera cam)
        {
            if (sceneBuffer != null && sceneCamera == cam) return;
            DetachScene();
            sceneBuffer = new CommandBuffer { name = "ReDefinition Atmospheres precipitation" };
            sceneCamera = cam;
            cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, sceneBuffer);
        }

        private void DetachScene()
        {
            if (sceneBuffer == null) return;
            if (sceneCamera != null) sceneCamera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, sceneBuffer);
            sceneBuffer.Release();
            sceneBuffer = null;
            sceneCamera = null;
        }

        private void SetSuppressed(bool suppress, string reason)
        {
            if (suppress == suppressing) return;
            suppressing = suppress;
            Services.EveFieldSuppressor.SetSuppressed(suppress, reason);
        }
    }

    // East, up and north at a place, as world directions. East and north are found from the body's own latitude and longitude, so that
    // the handedness of the game's frames cannot turn them round.
    internal struct BodyFrame
    {
        internal Vector3d East, Up, North;

        internal static void Compute(CelestialBody body, Vector3d worldPos, ref BodyFrame frame)
        {
            Vector3d up = (worldPos - body.position).normalized;
            Vector3d pole = body.transform.up;
            Vector3d t1 = Vector3d.Cross(up, pole);
            if (t1.sqrMagnitude < 1e-8) t1 = Vector3d.Cross(up, Vector3d.right);
            t1 = t1.normalized;
            Vector3d t2 = Vector3d.Cross(up, t1).normalized;
            const double Step = 10.0;
            double lat0 = body.GetLatitude(worldPos);
            // The one of the two horizontal axes along which the latitude rises most is north.
            double d1 = body.GetLatitude(worldPos + t1 * Step) - lat0;
            double d2 = body.GetLatitude(worldPos + t2 * Step) - lat0;
            Vector3d north = Math.Abs(d1) >= Math.Abs(d2) ? t1 * Math.Sign(d1 == 0.0 ? 1.0 : d1) : t2 * Math.Sign(d2 == 0.0 ? 1.0 : d2);
            Vector3d east = Vector3d.Cross(north, up).normalized;
            // The longitude must rise to the east; if not, the cross product ran the other way.
            double lon0 = body.GetLongitude(worldPos);
            double dLon = body.GetLongitude(worldPos + east * Step) - lon0;
            dLon = ((dLon + 540.0) % 360.0) - 180.0;
            if (dLon < 0.0) east = -east;
            frame.East = east;
            frame.Up = up;
            frame.North = north.normalized;
        }
    }
}
