using System;
using System.Globalization;
using System.Threading;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // Water drops on the windows of a cabin (IVA) and the visor of a kerbal (EVA first person): a camera effect like the camera frost
    // (package D37) and drawn the same two ways, after the upscaler through the host hook or as a quad where the main mod is not there.
    // A module of the atmospheres, setting weatherEffects (the T10 package adds its own switch). The state machine (DropletModel) is
    // stepped here from the weather view; the look is DropletPass and its shader.
    //
    // The camera classes, the host policy and the interior mask are the frost's (FrostCameraClass, FrostDrawPolicy, FrostInteriorMask,
    // the mask passes of the frost shader through a FrostPass of this module's own): the drops go only on the windows, the props of the
    // cabin stay clear, and they are drawn under the same rules (host composite when the hook answers and, in a cabin, the mask is
    // valid; the quad only where no upscaler would smear it).
    //
    // Motion vectors: none, fixed to the screen. Depth: none. Masks: the draw is after the upscaler. Frame generation: part of the image
    // before the HUD-less copy. Raytracing: not an input. Order: after the upscaler and the frost, before TUFX's effects (host hook
    // registration order). Exposure: colours relative to the image. Zero cost inactive: below drop.skipBelow wetness no command is
    // recorded and no quad is queued; with the frost over the same pane, drops and frost both draw (frost forms below the frost limit,
    // the drops need a glass above freezing, so they hardly meet).
    [AtmospheresModule(64)]
    internal sealed class DropletsRenderer : IAtmospheresModule
    {
        private const int FailureLimit = 5;
        private const float PauseAfterFailuresS = 5f;
        private const int HostAliveFrames = 3;
        private const int HostWaitFrames = 30;
        private const float SampleEveryS = 0.25f;
        private const float ShelterEveryS = 0.2f;
        private const float IvaMaskHoldS = 3f;
        private const string InternalCameraName = "InternalCamera";
        private const float StaleGraceS = 3f;
        // Air temperature the glass is pulled towards in a cabin, and the pane's share of the way (the frost's defaults).
        private const float CabinK = 293f;
        private const float IvaInsulation = 0.7f;

        private DropletPass pass;
        private FrostPass maskPass;
        private Texture2D dummyAtlas;
        private readonly FrostInteriorMask interior = new FrostInteriorMask();
        private readonly DropletModel state = new DropletModel();
        private DropletDraw draw;
        private bool drawReady;
        private PostUpscaleDraw hostDraw;
        private bool hostRegistered, hostFailed, initialised;
        private int lastHostCallFrame = -100;
        private int hostPendingFrames;
        private bool ivaHostDead, evaHostDead;
        private Camera lastHostCameraRef;
        private bool lastHostCameraInternal;
        private readonly StandDown standDown = new StandDown(FailureLimit, PauseAfterFailuresS);
        private Guid lastVessel;
        private int lastBody = -1;
        private float sampleAge = 1e9f, shelterAge = 1e9f, staleS, internalLookAge = 1e9f;
        private float temperatureK = float.NaN;
        private bool sheltered, abOff, ivaMaskNeeded;
        private float ivaMaskUntil = -1f;
        private Camera internalCamera;
        private FrostView lastView = FrostView.Off;
        private string lastModeLogged = "";
        private BodyFrame basis;

        internal static DropletsRenderer Current { get; private set; }
        internal static FrostView ActiveView { get; private set; }
        internal static string ActiveMode { get; private set; }
        internal static float LastWet { get; private set; }
        internal static float LastCoverage { get; private set; }
        internal static float LastSlideX { get; private set; }
        internal static float LastSlideY { get; private set; }
        internal static int DrawnFrames { get; private set; }
        internal static bool IvaMaskValid { get; private set; }

        public void Init()
        {
            Current = this;
            string reason;
            Shader shader = AtmosphereShaders.Get(DropletPass.ShaderName, out reason);
            Shader frostShader = AtmosphereShaders.Get(CameraFrostRenderer.ShaderName, out reason);
            if (shader == null)
            {
                AtmospheresLog.WarnOnce("droplets: the shader " + DropletPass.ShaderName + " is not available (" + reason + "); the droplets stay off");
                return;
            }
            pass = new DropletPass(shader);
            if (frostShader != null)
            {
                dummyAtlas = new Texture2D(1, 1, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave };
                maskPass = new FrostPass(frostShader, dummyAtlas);
            }
            if (Services.PostUpscale is NullPostUpscaleCompositor) Services.PostUpscale = new ApiBridge();
            hostDraw = HostCompose;
            initialised = true;
            ActiveMode = "none";
            AtmospheresLog.Say("droplets: ready (host hook " + (Services.PostUpscale.HostAvailable ? "found" : "absent") + ")");
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
            if (maskPass != null) maskPass.Dispose();
            maskPass = null;
            if (dummyAtlas != null) UnityEngine.Object.Destroy(dummyAtlas);
            dummyAtlas = null;
            if (pass != null) pass.Dispose();
            pass = null;
            initialised = false;
            drawReady = false;
            ActiveMode = "none";
            if (Current == this) Current = null;
        }

        internal static DropletTuning ReadTuning()
        {
            DropletTuning t = DropletTuning.Defaults();
            t.FillS = Tuning.Get("drop.fillS", t.FillS);
            t.DryStillS = Tuning.Get("drop.dryStillS", t.DryStillS);
            t.BlowRefMs = Tuning.Get("drop.blowRefMs", t.BlowRefMs);
            t.BlowS = Tuning.Get("drop.blowS", t.BlowS);
            t.CatchRefMs = Tuning.Get("drop.catchRefMs", t.CatchRefMs);
            t.SnowMelt = Tuning.Get("drop.snowMelt", t.SnowMelt);
            t.SlideG = Tuning.Get("drop.slideG", t.SlideG);
            t.SlideAir = Tuning.Get("drop.slideAir", t.SlideAir);
            t.LagS = Tuning.Get("drop.lagS", t.LagS);
            t.CoverMax = Tuning.Get("drop.coverMax", t.CoverMax);
            t.Refraction = Tuning.Get("drop.refraction", t.Refraction);
            t.SkipBelow = Tuning.Get("drop.skipBelow", t.SkipBelow);
            t.QuadAlpha = Tuning.Get("drop.quadAlpha", t.QuadAlpha);
            t.SizePx = Tuning.Get("drop.sizePx", t.SizePx);
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
            standDown.Elapse(dt);
            if (standDown.Paused) return;
            try
            {
                Step();
                standDown.Succeeded();
            }
            catch (Exception e)
            {
                bool pause = standDown.Failed();
                AtmospheresLog.Failed("droplets", "stepping the droplets failed", e);
                drawReady = false;
                if (pause)
                {
                    AtmospheresLog.WarnOnce("droplets: failed " + FailureLimit + " frames in a row; they stand down for " + PauseAfterFailuresS + " s");
                    Idle();
                    if (hostRegistered)
                    {
                        Services.PostUpscale.Unregister(hostDraw);
                        hostRegistered = false;
                    }
                }
            }
        }

        private void Step()
        {
            Vessel vessel = HighLogic.LoadedSceneIsFlight ? GameGlobals.ActiveVessel : null;
            Camera flight = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            bool flightScene = HighLogic.LoadedSceneIsFlight && vessel != null && vessel.mainBody != null && flight != null;
            bool settingsOn = AtmospheresSettings.Enabled && AtmospheresSettings.WeatherEffects && Tuning.Flag("drop.enabled", true);
            drawReady = false;
            if (!flightScene || !settingsOn)
            {
                if (!flightScene) state.Reset();
                interior.Update(false, maskPass, -1);
                EnsureHook(false);
                Idle();
                LastWet = 0f;
                return;
            }

            CelestialBody body = vessel.mainBody;
            if (vessel.id != lastVessel || body.flightGlobalsIndex != lastBody)
            {
                state.Reset();
                temperatureK = float.NaN;
                sampleAge = 1e9f;
                lastVessel = vessel.id;
                lastBody = body.flightGlobalsIndex;
            }
            DropletTuning t = ReadTuning();
            WeatherView view = WeatherHooks.View;
            bool live = view.Active && !view.IsStale(Time.frameCount);
            staleS = live ? 0f : staleS + Time.unscaledDeltaTime;
            bool hold = !live && staleS < StaleGraceS;
            bool eva = vessel.isEVA;
            float toVessel = (float)(((Vector3d)flight.transform.position) - vessel.GetWorldPos3D()).magnitude;
            FrostView where = FrostCameraClass.Classify(CameraMode(), MapView.MapIsEnabled, eva, toVessel, FrostCameraClass.FirstPersonM);
            if (where != lastView)
            {
                AtmospheresLog.Say("droplets: view " + lastView + " -> " + where);
                lastView = where;
            }

            sampleAge += Time.unscaledDeltaTime;
            if (sampleAge >= SampleEveryS)
            {
                sampleAge = 0f;
                Vector3d position = flight.transform.position;
                WeatherSample sample;
                temperatureK = WeatherHub.Sample(body.flightGlobalsIndex, body.GetLatitude(position), body.GetLongitude(position), body.GetAltitude(position),
                    Planetarium.GetUniversalTime(), out sample) ? sample.TemperatureK : float.NaN;
            }

            // The camera the picture is taken from: the internal camera in the cabin when it is found, else the flight camera.
            Camera look = flight;
            if (where == FrostView.Iva)
            {
                LookForInternalCamera();
                if (internalCamera != null && internalCamera.enabled) look = internalCamera;
            }
            Vector3 down = (Vector3)(body.position - (Vector3d)look.transform.position).normalized;
            float gx = Vector3.Dot(down, look.transform.right), gy = Vector3.Dot(down, look.transform.up);

            Vector3d camWorld = flight.transform.position;
            BodyFrame.Compute(body, camWorld, ref basis);
            Vector3d windWorld = live ? basis.East * view.WindWorld.x + basis.North * view.WindWorld.y + basis.Up * view.WindWorld.z : Vector3d.zero;
            // The air moves over the vessel with the opposite of the vessel's velocity through the air; a drop is pushed along it.
            Vector3d airOver = windWorld - vessel.srf_velocity;
            float airflow = (float)airOver.magnitude;
            float ax = (float)Vector3d.Dot(airOver, look.transform.right), ay = (float)Vector3d.Dot(airOver, look.transform.up);
            float inPlane = Mathf.Sqrt(ax * ax + ay * ay);
            float airTotal = Mathf.Max((float)airOver.magnitude, 1e-3f);

            shelterAge += Time.unscaledDeltaTime;
            if (shelterAge >= ShelterEveryS)
            {
                shelterAge = 0f;
                sheltered = where == FrostView.Eva && Roofed(flight.transform.position, (Vector3)basis.Up, 30f, vessel);
            }

            // A pane that is above freezing wets with snow too: the glass is the air's (visor) or between the air and the cabin's.
            float glassK = where == FrostView.Iva ? temperatureK + IvaInsulation * (CabinK - temperatureK) : temperatureK;
            float warm = float.IsNaN(glassK) ? 1f : Mathf.Clamp01((glassK - 272.15f) / 3f);
            var input = new DropletInput
            {
                Dt = Time.timeScale > 0f && !hold ? Mathf.Min(Time.unscaledDeltaTime, 0.25f) : 0f,
                Level = live ? view.Level : 0f,
                Type = live ? view.Type : PrecipType.None,
                WarmGlass = warm,
                AirflowMs = airflow,
                Sheltered = !live || sheltered,
                GravityX = gx,
                GravityY = gy,
                GravityInPlane = Mathf.Sqrt(gx * gx + gy * gy),
                AirX = inPlane > 1e-3f ? ax / inPlane : 0f,
                AirY = inPlane > 1e-3f ? ay / inPlane : 0f,
                AirInPlane = Mathf.Clamp01(inPlane / airTotal)
            };
            // Wetness and the camera's views other than a window or visor: the model runs in any view (the pane stays wet when the
            // camera goes outside and comes back), the draw only in a view with glass.
            state.Step(ref input, ref t);

            LastWet = state.Wet01;
            LastCoverage = DropletModel.Coverage(state.Wet01, ref t);
            LastSlideX = state.SlideX;
            LastSlideY = state.SlideY;
            ActiveView = where;

            bool wantDraw = where != FrostView.Off && state.NeedsDraw(ref t);
            ivaMaskNeeded = where == FrostView.Iva && InternalCameraInImage(flight);
            bool ivaWanted = ivaMaskNeeded && wantDraw && Tuning.Flag("frost.ivaMask", true) && Services.PostUpscale.HostAvailable && maskPass != null;
            if (ivaWanted) ivaMaskUntil = Time.unscaledTime + IvaMaskHoldS;
            interior.Update(maskPass != null && where == FrostView.Iva && Time.unscaledTime < ivaMaskUntil, maskPass, (int)Tuning.Get("frost.ivaMaskWay", -1f));
            IvaMaskValid = interior.Valid;
            if (!wantDraw)
            {
                EnsureHook(true);
                Idle();
                return;
            }

            CelestialBody sunBody = GameGlobals.Sun;
            Vector3d sun = sunBody != null ? (sunBody.position - vessel.GetWorldPos3D()).normalized : vessel.upAxis;
            float elevation = Mathf.Asin(Mathf.Clamp((float)Vector3d.Dot(sun, vessel.upAxis), -1f, 1f));
            float light = Mathf.Clamp01(0.25f + 0.75f * Mathf.Sin(Mathf.Max(elevation, 0f))) * (live ? view.SunTransmission : 1f);
            draw = new DropletDraw
            {
                Tuning = t,
                Wet01 = state.Wet01,
                ClockS = state.Time,
                Slide = new Vector2(state.SlideX, state.SlideY),
                Aspect = look.aspect,
                FlipY = SystemInfo.graphicsUVStartsAtTop,
                InteriorMask = null,
                Light = light,
                QuadColour = new Color(0.85f * (0.2f + 0.6f * light), 0.9f * (0.2f + 0.6f * light), (0.2f + 0.6f * light), 1f)
            };
            drawReady = true;

            bool hostAlive = hostRegistered && !hostFailed && Time.frameCount - lastHostCallFrame <= HostAliveFrames;
            bool dead = where == FrostView.Iva ? ivaHostDead : evaHostDead;
            bool hostUsable = hostAlive && !dead && (where == FrostView.Eva || !ivaMaskNeeded || IvaMaskValid || !Tuning.Flag("frost.ivaMaskRequired", true))
                              && !Tuning.Flag("drop.forceQuad", false);
            if (hostUsable)
            {
                hostPendingFrames++;
                if (hostPendingFrames > HostWaitFrames)
                {
                    if (where == FrostView.Iva) ivaHostDead = true; else evaHostDead = true;
                    AtmospheresLog.WarnOnce("droplets: the host hook drew nothing in " + HostWaitFrames + " frames in the " + where + " view; the quad draws them");
                    hostUsable = false;
                }
            }
            else
            {
                hostPendingFrames = 0;
            }
            bool upscaled = Services.PostUpscale.HostAvailable && WeatherHooks.Caps.Upscaler != UpscalerKind.None;
            bool quadAllowed = FrostDrawPolicy.QuadAllowed(upscaled, Tuning.Flag("drop.quadWithUpscaler", false));
            abOff = Tuning.Flag("drop.abOff", false);
            string mode = FrostDrawPolicy.Mode(hostUsable, quadAllowed);
            if (mode != lastModeLogged)
            {
                AtmospheresLog.Say("droplets: drawing in " + mode + " mode (" + where + ", wetness " + state.Wet01.ToString("0.00", CultureInfo.InvariantCulture) + ")");
                lastModeLogged = mode;
            }
            ActiveMode = mode;
            if (mode == "quad" && !abOff)
            {
                pass.DrawQuad(flight, ref draw);
                DrawnFrames++;
            }
            EnsureHook(true);
        }

        private void LookForInternalCamera()
        {
            internalLookAge += Time.unscaledDeltaTime;
            if (internalCamera == null && internalLookAge >= 1f)
            {
                internalLookAge = 0f;
                Camera[] all = Camera.allCameras;
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null && all[i].name == InternalCameraName) { internalCamera = all[i]; break; }
            }
        }

        private bool InternalCameraInImage(Camera flight)
        {
            LookForInternalCamera();
            if (flight == null) return false;
            if (internalCamera == null) return true;
            RenderTexture target = flight.targetTexture;
            return target != null && internalCamera.targetTexture == target;
        }

        private void Idle()
        {
            drawReady = false;
            ActiveMode = "none";
            hostPendingFrames = 0;
        }

        private static bool Roofed(Vector3 camera, Vector3 up, float reach, Vessel vessel)
        {
            return FrostRoof.Roofed(camera, up, reach, (1 << 0) | (1 << 15), vessel);
        }

        private void EnsureHook(bool wanted)
        {
            if (wanted && !hostRegistered && !hostFailed && initialised && Services.PostUpscale.HostAvailable)
            {
                hostRegistered = Services.PostUpscale.Register("droplets", hostDraw, false);
                if (!hostRegistered) hostFailed = true;
            }
            else if (!wanted && hostRegistered)
            {
                Services.PostUpscale.Unregister(hostDraw);
                hostRegistered = false;
            }
        }

        private void HostCompose(CommandBuffer cmd, RenderTexture upscaled, RenderTexture depth, Camera camera)
        {
            try
            {
                lastHostCallFrame = Time.frameCount;
                if (camera != lastHostCameraRef)
                {
                    lastHostCameraRef = camera;
                    lastHostCameraInternal = camera != null && camera.name == InternalCameraName;
                }
                Camera flight = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
                bool ours = camera == flight || (ActiveView == FrostView.Iva && lastHostCameraInternal);
                if (pass == null || !ours || !drawReady || ActiveMode != "host" || upscaled == null || abOff) return;
                hostPendingFrames = 0;
                DropletDraw d = draw;
                d.Aspect = (float)upscaled.width / Mathf.Max(upscaled.height, 1);
                d.InteriorMask = ActiveView == FrostView.Iva && ivaMaskNeeded ? interior.Mask : null;
                cmd.BeginSample("ReDefinition droplets host");
                pass.RecordHost(cmd, upscaled, upscaled.width, upscaled.height, ref d);
                cmd.EndSample("ReDefinition droplets host");
                DrawnFrames++;
            }
            catch (Exception e)
            {
                hostFailed = true;
                AtmospheresLog.Failed("droplets-host", "the host composite failed and is dropped", e);
            }
        }
    }
}
