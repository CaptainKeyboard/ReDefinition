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
    // The quad is a scene-side overlay: an upscaler sees it as part of the moving scene. So with the main mod present and an upscaler
    // running, a view the host composite cannot serve is left without frost (the log says why) unless the flag frost.quadWithUpscaler
    // is on; the quad is drawn when there is no upscaler to smear it. [VERIFY by the player rows ghost.*.quad_*: they measure the quad
    // under each upscaler, and the default follows them.]
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
        private const float PauseAfterFailuresS = 5f;
        private const int HostAliveFrames = 3;
        private const float SampleEveryS = 0.25f;
        private const float ShelterEveryS = 0.2f;
        private const float IvaMaskHoldS = 3f;
        private const int HostWaitFrames = 30;
        private const string InternalCameraName = "InternalCamera";
        // A stale weather view this long freezes the glass instead of clearing it (a field rebuild is not dry weather).
        private const float StaleGraceS = 3f;

        private FrostPass pass;
        private Shader shader;
        private Texture2D atlas;
        private byte[] builtAtlas;
        private string atlasError;
        private double atlasBuildMs = -1;
        private int hostPendingFrames;
        private bool ivaHostDead, evaHostDead;
        private float ivaMaskUntil = -1f;
        private float shelterAge = 1e9f;
        private bool sheltered;
        private Camera lastHostCameraRef;
        private bool lastHostCameraInternal;
        private BodyFrame basis;
        private readonly CameraFrost state = new CameraFrost();
        private readonly FrostInteriorMask interior = new FrostInteriorMask();
        private FrostDraw draw;
        private bool drawReady;
        private PostUpscaleDraw hostDraw;
        private bool hostRegistered;
        private bool hostFailed;
        private int lastHostCallFrame = -100;
        private bool initialised;
        private readonly StandDown standDown = new StandDown(FailureLimit, PauseAfterFailuresS);
        private bool stackLogged;
        private Guid lastVessel;
        private int lastBody = -1;
        private float sampleAge = 1e9f;
        private float temperatureK = float.NaN;
        private float staleS;
        private bool abOff;
        private bool ivaMaskNeeded;
        private Camera internalCamera;
        private float internalLookAge = 1e9f;
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
        // The name of the camera the host hook was last called for.
        internal static string LastHostCamera { get; private set; }
        // The IVA premises as the series reads them: how often the hook was called while the view was the IVA, for which camera (the
        // flight camera, the internal camera), the size of the image it gave and of the camera's own target, and the interior mask.
        // Whether the internal camera draws into the flight camera's render target (the picture the upscaler gets), and so needs the mask.
        internal static bool InternalInRigImage { get; private set; }
        internal static bool IvaMaskNeeded { get; private set; }
        internal static int HostCallsIva { get; private set; }
        internal static int HostCallsIvaFlightCamera { get; private set; }
        internal static int HostCallsIvaInternalCamera { get; private set; }
        internal static int HostCallsIvaDrawn { get; private set; }
        internal static int HostImageW { get; private set; }
        internal static int HostImageH { get; private set; }
        internal static int HostCameraPixelW { get; private set; }
        internal static int HostCameraPixelH { get; private set; }
        internal static float IvaMaskCoverage { get; private set; }
        internal static int IvaMaskWay { get; private set; }
        internal static bool IvaMaskExhausted { get; private set; }
        // Frames in which the view wanted frost and none was drawn because the host composite could not serve it and the quad would be
        // upscaled (see the class comment), and frames the quad was queued.
        internal static int SuppressedFrames { get; private set; }
        internal static int QuadFrames { get; private set; }
        // Why the last frame that wanted frost used the mode it used ("host", "quad", "suppressed: ...").
        internal static string ModeReason { get; private set; }
        // The running module, or null; the series probe reads it.
        internal static CameraFrostRenderer Current { get; private set; }

        // Whether the shader is loaded and the atlas is uploaded: the pass exists.
        internal bool PassReady
        {
            get { return pass != null; }
        }

        // The share of the interior mask that is covered, read back from the GPU (-1: no mask). The series probe's diagnostic.
        internal float ReadInteriorCoverage()
        {
            return interior.ReadCoverage();
        }

        // The interior mask as 0 / 1 values (see FrostInteriorMask.ReadMask), or null. The series probe's diagnostic.
        internal float[] ReadInteriorMask(out int width, out int height)
        {
            return interior.ReadMask(out width, out height);
        }

        // The camera the interior mask is attached to, or null.
        internal Camera InteriorCamera
        {
            get { return interior.Camera; }
        }

        // The state of the glass the draw was prepared from (for the series probe's prediction of the picture).
        internal float CurrentGrowth
        {
            get { return state.Growth; }
        }

        // Empties the glass (the series probe starts each step from a clear pane).
        internal void ResetState()
        {
            state.Reset();
            LastExposure = 0f;
        }

        public void Init()
        {
            Current = this;
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
                try
                {
                    var watch = Stopwatch.StartNew();
                    byte[] pixels = FrostAtlas.Build();
                    atlasBuildMs = watch.Elapsed.TotalMilliseconds;
                    Volatile.Write(ref builtAtlas, pixels);
                }
                catch (Exception e)
                {
                    // Reported by Tick on the main thread.
                    Volatile.Write(ref atlasError, e.GetType().Name + " " + e.Message);
                }
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
            if (Current == this) Current = null;
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
            t.FlipY = FrostPass.ResolveFlipY(Tuning.Get("frost.flipY", -1f));
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
                Step(dt);
                standDown.Succeeded();
            }
            catch (Exception e)
            {
                bool pause = standDown.Failed();
                AtmospheresLog.Failed("camera-frost", "stepping the camera frost failed", e);
                if (!stackLogged)
                {
                    stackLogged = true;
                    AtmospheresLog.WarnOnce("camera frost: first failure at " + e);
                }
                drawReady = false;
                if (pause)
                {
                    // A scene change or a menu frame must not end the frost for the session: stand down for a while, then try again.
                    AtmospheresLog.WarnOnce("camera frost: failed " + FailureLimit + " frames in a row; it stands down for " + PauseAfterFailuresS + " s");
                    Idle();
                    ResetReadings();
                    if (hostRegistered)
                    {
                        Services.PostUpscale.Unregister(hostDraw);
                        hostRegistered = false;
                    }
                }
            }
        }

        private void EnsurePass()
        {
            if (pass != null || shader == null) return;
            string error = Volatile.Read(ref atlasError);
            if (error != null)
            {
                AtmospheresLog.WarnOnce("camera frost: the atlas could not be built (" + error + "); the frost stays off");
                shader = null;
                return;
            }
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
            // Outside a flight there is no FlightGlobals object to ask (the main menu, a scene load): no vessel, no frost.
            Vessel vessel = HighLogic.LoadedSceneIsFlight ? GameGlobals.ActiveVessel : null;
            Camera flight = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            bool flightScene = HighLogic.LoadedSceneIsFlight && vessel != null && vessel.mainBody != null && flight != null;
            bool settingsOn = AtmospheresSettings.Enabled && AtmospheresSettings.WeatherEffects && AtmospheresSettings.CameraFrost;
            drawReady = false;
            if (!flightScene || !settingsOn || pass == null)
            {
                if (!flightScene) state.Reset();
                interior.Update(false, pass, -1);
                EnsureHook(flightScene && settingsOn);
                Idle();
                ResetReadings();
                return;
            }

            CelestialBody body = vessel.mainBody;
            BodyClimate climate = Bodies.Get(body);
            FrostRules rules = climate != null ? climate.Frost : FrostRules.Default;
            if (!rules.Enabled)
            {
                // The body's configuration switches the frost off.
                state.Reset();
                interior.Update(false, pass, -1);
                EnsureHook(false);
                Idle();
                ResetReadings();
                return;
            }
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
            // A weather view that is stale for a moment (the field rebuilds) freezes the glass; only a long gap counts as dry weather.
            staleS = live ? 0f : staleS + Time.unscaledDeltaTime;
            bool holdGlass = !live && staleS < StaleGraceS;
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
                if (live && view.Level >= CameraFrost.WeatherFrom && float.IsNaN(temperatureK))
                    AtmospheresLog.WarnOnce("camera frost: the weather field gives no temperature at the camera; the glass does not freeze without one");
            }

            // Down the picture: the direction of gravity in the plane of the camera, so that a rolled camera slides the drops down.
            Vector3 down = (body.position - (Vector3d)flight.transform.position).normalized;
            float gx = Vector3.Dot(down, flight.transform.right), gy = Vector3.Dot(down, flight.transform.up);

            // The air over the glass: the vessel's velocity through the air, the wind taken from the body-fixed frame (east, north, up)
            // into the world.
            Vector3d camWorld = flight.transform.position;
            BodyFrame.Compute(body, camWorld, ref basis);
            Vector3d windWorld = live ? basis.East * view.WindWorld.x + basis.North * view.WindWorld.y + basis.Up * view.WindWorld.z : Vector3d.zero;
            float airflow = (float)(vessel.srf_velocity - windWorld).magnitude;

            // Under a roof the weather does not reach the glass (an EVA camera only; a cabin's pane faces the weather whatever is above).
            shelterAge += Time.unscaledDeltaTime;
            if (shelterAge >= ShelterEveryS)
            {
                shelterAge = 0f;
                sheltered = where == FrostView.Eva && t.ShelterM > 0f && Roofed(flight.transform.position, (Vector3)basis.Up, t.ShelterM, vessel);
            }

            var input = new FrostInput
            {
                Dt = Time.timeScale > 0f && !holdGlass ? Mathf.Min(Time.unscaledDeltaTime, 0.25f) : 0f,
                View = where,
                Level = live ? view.Level : 0f,
                Type = live ? view.Type : PrecipType.None,
                TemperatureK = temperatureK,
                FrostLimitK = rules.MaxTemperatureK,
                Strength = rules.Strength,
                AirflowMs = airflow,
                GustPhase = live ? view.GustPhase : 0f,
                Sheltered = !live || view.Level < CameraFrost.WeatherFrom || sheltered,
                GravityX = gx,
                GravityY = gy,
                GravityInPlane = Mathf.Sqrt(gx * gx + gy * gy),
                Aspect = flight.aspect,
                Seed = live ? view.FlakeSeed : 0
            };
            state.Step(ref input, ref t);

            LastExposure = state.Exposure;
            LastGrowth = state.Growth;
            LastFlakes = state.FlakeCount;
            LastDrops = state.DropCount;
            ActiveView = where;

            bool wantDraw = where != FrostView.Off && state.NeedsDraw(ref t);
            // The interior mask stays on for IvaMaskHoldS after the last frame the glass needed it, so that a pane that flickers around
            // the skip threshold does not rebuild it.
            // The mask is needed where the internal camera is part of the picture the upscaler gets (it draws into the same target as the
            // flight camera); where it draws past the upscaler the interior covers the composite by itself.
            ivaMaskNeeded = where == FrostView.Iva && InternalCameraInImage(flight);
            bool ivaWanted = ivaMaskNeeded && wantDraw && Tuning.Flag("frost.ivaMask", true) && Services.PostUpscale.HostAvailable;
            if (ivaWanted) ivaMaskUntil = Time.unscaledTime + IvaMaskHoldS;
            interior.Update(where == FrostView.Iva && Time.unscaledTime < ivaMaskUntil, pass, (int)Tuning.Get("frost.ivaMaskWay", -1f));
            IvaMaskValid = interior.Valid;
            IvaMaskCoverage = interior.Coverage;
            IvaMaskWay = interior.Way;
            IvaMaskExhausted = interior.Exhausted;
            IvaMaskNeeded = ivaMaskNeeded;
            if (!wantDraw)
            {
                EnsureHook(true);
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
            draw.Growth = state.Growth;
            draw.Exposure = state.Exposure;
            draw.Gravity = new Vector2(gx, gy);
            draw.Tuning = t;
            draw.FlipY = t.FlipY;
            CelestialBody sunBody = GameGlobals.Sun;
            Vector3d sun = sunBody != null ? (sunBody.position - vessel.GetWorldPos3D()).normalized : vessel.upAxis;
            float elevation = Mathf.Asin(Mathf.Clamp((float)Vector3d.Dot(sun, vessel.upAxis), -1f, 1f));
            draw.Light = FrostLight.Factor(elevation, live ? view.SunTransmission : 1f, ref t);
            float c = t.QuadBase + t.QuadLight * draw.Light;
            draw.QuadColour = new Color(t.TintR * c, t.TintG * c, t.TintB * c, 1f);
            draw.InteriorMask = null;
            draw.Aspect = flight.aspect;
            drawReady = true;

            // Host composite when the hook answers and, for a cabin, the interior mask is there. A host that was selected for a view and
            // did not draw in HostWaitFrames frames (the hook is called for another camera than the one assumed, or not at all) is
            // dropped for that view and the quad draws instead.
            bool hostAlive = hostRegistered && !hostFailed && Time.frameCount - lastHostCallFrame <= HostAliveFrames;
            bool dead = where == FrostView.Iva ? ivaHostDead : evaHostDead;
            // frost.forceQuad and frost.ivaMaskRequired = 0 are for the series (it measures the quad and the unmasked composite).
            bool hostUsable = hostAlive && !dead && (where == FrostView.Eva || !ivaMaskNeeded || IvaMaskValid || !Tuning.Flag("frost.ivaMaskRequired", true))
                              && !Tuning.Flag("frost.forceQuad", false);
            if (hostUsable)
            {
                hostPendingFrames++;
                if (hostPendingFrames > HostWaitFrames)
                {
                    if (where == FrostView.Iva) ivaHostDead = true; else evaHostDead = true;
                    AtmospheresLog.WarnOnce("camera frost: the host hook drew nothing in " + HostWaitFrames + " frames in the " + where
                                            + " view (called for '" + (LastHostCamera ?? "no camera") + "'); the quad fallback draws it");
                    hostUsable = false;
                }
            }
            else
            {
                hostPendingFrames = 0;
            }
            // The quad is a scene-side overlay with no motion vectors: an upscaler running on the picture smears it. With the host present
            // and an upscaler on, a view the host composite cannot serve gets no frost (and a log line) rather than a smeared quad.
            bool upscaled = Services.PostUpscale.HostAvailable && WeatherHooks.Caps.Upscaler != UpscalerKind.None;
            bool quadAllowed = FrostDrawPolicy.QuadAllowed(upscaled, Tuning.Flag("frost.quadWithUpscaler", false));
            abOff = Tuning.Flag("frost.abOff", false);
            string reason = hostUsable ? "host" : Tuning.Flag("frost.forceQuad", false) ? "forced" : !hostAlive ? "the hook did not answer" : dead ? "the hook drew nothing in this view"
                : !IvaMaskValid ? (IvaMaskExhausted ? "no way of taking the interior mask gave a plausible one" : "the interior mask is not valid yet") : "host not usable";
            string mode = FrostDrawPolicy.Mode(hostUsable, quadAllowed);
            if (mode != lastModeLogged)
            {
                AtmospheresLog.Say("camera frost: drawing in " + mode + " mode (" + where + ", exposure "
                                   + state.Exposure.ToString("0.00", CultureInfo.InvariantCulture) + ")"
                                   + (hostUsable ? "" : ": " + reason + (quadAllowed ? "" : "; the quad would be upscaled with the picture")));
                lastModeLogged = mode;
            }
            ActiveMode = mode;
            ModeReason = mode == "host" ? "host" : mode + ": " + reason;
            HostComposed = hostUsable;
            if (mode == "quad" && !abOff)
            {
                pass.DrawQuad(flight, ref draw);
                DrawnFrames++;
                QuadFrames++;
            }
            else if (mode == "suppressed")
            {
                SuppressedFrames++;
            }

            EnsureHook(true);
        }

        // Whether the internal camera draws into the same render target as the flight camera, i.e. into the picture the upscaler gets
        // (the rig redirects the cameras of the stack into one render-size texture). Looked up about once a second.
        private bool InternalCameraInImage(Camera flight)
        {
            internalLookAge += Time.unscaledDeltaTime;
            if (internalCamera == null && internalLookAge >= 1f)
            {
                internalLookAge = 0f;
                Camera[] all = Camera.allCameras;
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null && all[i].name == InternalCameraName) { internalCamera = all[i]; break; }
            }
            if (flight == null) return false;
            // No internal camera found: the mask is assumed needed (and cannot be built, so the composite is not used).
            if (internalCamera == null) return true;
            RenderTexture target = flight.targetTexture;
            InternalInRigImage = target != null && internalCamera.targetTexture == target;
            return InternalInRigImage;
        }

        private void Idle()
        {
            drawReady = false;
            HostComposed = false;
            ActiveMode = "none";
            hostPendingFrames = 0;
        }

        // The readings of the series when there is no frost to read: the pane, the view and the counts.
        private static void ResetReadings()
        {
            ActiveView = FrostView.Off;
            LastExposure = 0f;
            LastGrowth = 0f;
            LastFlakes = 0;
            LastDrops = 0;
            IvaMaskValid = false;
            IvaMaskCoverage = -1f;
            IvaMaskExhausted = false;
        }

        // Whether something solid lies above the point within reach, ignoring the vessel itself: the EVA camera is under a roof, a wing
        // or a rock. Layers 0 (parts) and 15 (scenery and terrain).
        private static bool Roofed(Vector3 camera, Vector3 up, float reach, Vessel vessel)
        {
            return FrostRoof.Roofed(camera, up, reach, (1 << 0) | (1 << 15), vessel);
        }

        // Keeps the host hook registered while the frost is in use (a flight, the setting on) and takes it away otherwise, so that a
        // switched-off frost leaves no handler for the host to call.
        private void EnsureHook(bool wanted)
        {
            if (wanted && !hostRegistered && !hostFailed && initialised && Services.PostUpscale.HostAvailable)
            {
                hostRegistered = Services.PostUpscale.Register("camera-frost", hostDraw, false);
                if (!hostRegistered) hostFailed = true;
            }
            else if (!wanted && hostRegistered)
            {
                Services.PostUpscale.Unregister(hostDraw);
                hostRegistered = false;
            }
        }

        // The draw the host calls after the upscaler. Records the composite from the frame Tick prepared.
        private void HostCompose(CommandBuffer cmd, RenderTexture upscaled, RenderTexture depth, Camera camera)
        {
            try
            {
                lastHostCallFrame = Time.frameCount;
                if (camera != lastHostCameraRef)
                {
                    // The name is read once per camera, not once per call (the property allocates a string).
                    lastHostCameraRef = camera;
                    lastHostCameraInternal = camera != null && camera.name == InternalCameraName;
                    LastHostCamera = camera != null ? camera.name : null;
                }
                Camera flight = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
                // The hook is called with the camera the rig was built on (RigLifecycle.CameraPreference: Camera 00 in flight, in the
                // IVA too, as the rig redirects InternalCamera into the same image but is built on Camera 00); the internal camera is
                // accepted as well [VERIFY: the rows D37.iva_hook_calls_flight_camera / _internal_camera count which one the game gives].
                bool ours = camera == flight || (ActiveView == FrostView.Iva && lastHostCameraInternal);
                if (ActiveView == FrostView.Iva && upscaled != null)
                {
                    HostCallsIva++;
                    if (camera == flight) HostCallsIvaFlightCamera++;
                    else if (lastHostCameraInternal) HostCallsIvaInternalCamera++;
                    HostImageW = upscaled.width;
                    HostImageH = upscaled.height;
                    HostCameraPixelW = camera != null ? camera.pixelWidth : 0;
                    HostCameraPixelH = camera != null ? camera.pixelHeight : 0;
                }
                // frost.abOff leaves the frost undrawn with every other state unchanged: the series' A/B pictures.
                if (pass == null || !ours || !drawReady || ActiveMode != "host" || upscaled == null || abOff) return;
                hostPendingFrames = 0;
                if (ActiveView == FrostView.Iva) HostCallsIvaDrawn++;
                FrostDraw d = draw;
                d.Aspect = (float)upscaled.width / Mathf.Max(upscaled.height, 1);
                d.InteriorMask = ActiveView == FrostView.Iva && ivaMaskNeeded ? interior.Mask : null;
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
