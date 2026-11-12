using System;
using System.Collections.Generic;
using System.Diagnostics;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // Lamp scatter: the beam and glow of a vehicle lamp (a stock light part, a spot light, a mod light, a kerbal's headlamp) in air that
    // is not clear. One effect driven by the local medium density (LampMedium: fog, precipitation, cloud, dust). It is called lamp
    // scatter or beam, never a halo. A module (started by the registry), no game object.
    //
    // Per frame, in Camera.onPreCull of the flight camera: the lamps are found (LampLights, 1 Hz list, 5 Hz scoring, the best N by
    // tier 2 / 4 / 8 through LampSelector with a 0.3 s fade), the medium is evaluated at each lamp and at the camera at 10 Hz and slewed
    // (LampMath.SlewSigma), the brightness is lagged by 0.1 s, and two command buffers are put on the camera: the copy of the depth at
    // BeforeForwardAlpha (RECON P6: valid there with and without Scatterer, the global is overwritten later) and the proxy draws at
    // AfterForwardAlpha. With no lamp within 150 m or a sigma below 1e-4 nothing is attached, nothing is copied: zero cost.
    //
    // Two ways to compose, one look (LampPass and one shader), as the precipitation renderer does:
    //   Scene  the command buffers above, before the upscaler. The smear of the additive beam (no motion vectors) after a turn of 20
    //          deg/s under DLSS, FSR and XeSS is NOT measured yet: the player's scene mode has no upscaler, and no K-W3b run has
    //          happened. The rows D32.smear_frames_scene / D32.smear_frames_host of tests/series/rows/D32.json measure it in the
    //          game; the rule is "Scene while the smear is at most 3 frames, else Host".
    //   Host   after the upscaler through ReDefinition.Api with the host's render-size depth. It is NOT selectable until the exposure
    //          rule below exists: the composite needs Tuning flag lamp.hostAllowed (default false; the series probe sets it for its own
    //          Host measurement, E40 sets it together with lamp.hostProven.<vendor> once the exposure factor is in). The colour is
    //          scaled by lamp.hostGain; the luminance-relative exposure rule of the veil (scale by the same factor as the veil) is not
    //          applied here [VERIFY: needs the image-luminance capture of PrecipPass, which is not a shared service]. weatherCompose
    //          Host and Auto therefore both give Scene today.
    //
    // Motion vectors: none written. Depth: none written; the beam is clipped against a copy of the scene depth. Masks: no reactive mask
    // (DLSS has none), the draw is a command buffer and no scene object. Frame generation: both composites are drawn before the
    // HUD-less copy by their place in the frame (Scene: at AfterForwardAlpha, before the upscaler; Host: in the hook after the upscaler
    // that the host runs before TUFX and the HUD-less copy, master plan 6.1) [VERIFY with a HUD-less capture of the frame-generation
    // replay in E40]. Raytracing: not an input (RT sees the Unity light itself; the beam is only scattering, no double count) [VERIFY
    // with an RT-on start]. Exposure: scene units in Scene mode. Order: transparent queue after EVE's composite.
    //
    // Shared service: Init writes Services.PostUpscale when it still holds the null object (the precipitation renderer does the same);
    // whoever assigns first owns the bridge, and Shutdown unregisters only this module's hook.
    [AtmospheresModule(65)]
    internal sealed class LampScatter : IAtmospheresModule
    {
        internal const string ShaderName = "ReDefinition/AtmospheresLamp";
        internal const string Series = "K-W3b";
        private const int MaxDraws = 8;
        private const int FailureLimit = 5;
        private const double MediumSeconds = 0.1;

        private sealed class LampState
        {
            internal int Id;
            internal float Sigma, SigmaCamera, SigmaTarget, SigmaCameraTarget;
            internal float Scalar;
            internal LampSigma Parts;
            internal bool Seen, Evaluated;
        }

        private static LampScatter instance;

        private LampPass pass;
        private readonly LampSelector selector = new LampSelector();
        private readonly LampLights lights = new LampLights();
        private readonly LampMedium medium = new LampMedium();
        private readonly PrecipCompose compose = new PrecipCompose();
        private readonly Dictionary<int, LampState> states = new Dictionary<int, LampState>();
        private readonly List<LampCandidate> candidates = new List<LampCandidate>(16);
        private readonly List<int> stale = new List<int>(8);
        private readonly LampDraw[] draws = new LampDraw[MaxDraws];
        private int drawCount;
        private LampFrame frame;
        private CommandBuffer depthBuffer, drawBuffer;
        private Camera attachedCamera;
        private PostUpscaleDraw hostDraw;
        private bool hostRegistered, hostFailed;
        private int lastHostCallFrame = -1;
        private int hostRegisterFrame;
        private bool wasDrawing;
        private double idleSince;
        private int lastComposeFrame = -1;
        private bool initialised;
        private int failures;
        private double lastScore = double.NegativeInfinity, lastMedium = double.NegativeInfinity;
        private Vector3 drift;
        private Vector3 windAxisE = Vector3.right, windAxisU = Vector3.up, windAxisN = Vector3.forward;
        private BodyFrame basis;
        private WeatherCompose lastMode = WeatherCompose.Auto;
        private readonly Stopwatch watch = new Stopwatch();
        private bool announced;

        // The state for the diagnostics and the series.
        internal static LampScatter Instance { get { return instance; } }
        internal static bool Drawing { get; private set; }
        internal static int LastDrawCount { get; private set; }
        internal static int LastSelectedCount { get; private set; }
        internal static bool BuffersAttached { get; private set; }
        internal static int DepthCopies { get; private set; }
        internal static LampSigma LastSigma { get; private set; }
        internal static float LastSigmaCamera { get; private set; }
        internal static WeatherCompose ActiveMode { get; private set; }
        internal static double CpuMs { get; private set; }
        internal static int EveLayers { get; private set; }
        internal static int KnownLights { get; private set; }

        // The lights that were on, a lamp by the module's rules (intensity, range) and within reach at the last scoring: what the
        // module could draw, as opposed to KnownLights, which counts the lights of the parts whether they are on or not.
        internal static int LitCandidates { get; private set; }

        // The drawn lamp of the last frame whose axis is the most horizontal (a beam seen from the side shows its length; one that points
        // up or down is seen end-on or leaves the picture): where it is, which way it points, how far it reaches and its half angle
        // (world space), for the probe that puts its sample points on the beam. Valid while LastDrawCount > 0.
        internal static Vector3 SideLampPosition { get; private set; }
        internal static Vector3 SideLampAxis { get; private set; }
        internal static float SideLampRange { get; private set; }
        internal static float SideLampHalfAngleRad { get; private set; }

        // The cloud density the lamp scatter would see at a world position (the probe samples EVE's layers and the volume pass with it).
        internal float ProbeCloudDensity(Vector3 world, CelestialBody body)
        {
            LampTuning t = ReadTuning();
            return medium.CloudDensity(world, body, ref t);
        }

        // The spread of the depth copy over nine places (the probe: a copy of one value everywhere is not a copy of a scene's depth); -1
        // without a copy. Reads back from the GPU.
        internal float ProbeDepthSpread()
        {
            return pass != null ? pass.DepthSpread() : -1f;
        }

        public void Init()
        {
            string reason;
            Shader shader = AtmosphereShaders.Get(ShaderName, out reason);
            if (shader == null)
            {
                AtmospheresLog.WarnOnce("lamp scatter: the shader " + ShaderName + " is not available (" + reason + "); the lamp scatter stays off");
                return;
            }
            pass = new LampPass(shader);
            if (Services.PostUpscale is NullPostUpscaleCompositor) Services.PostUpscale = new ApiBridge();
            hostDraw = HostCompose;
            instance = this;
            Camera.onPreCull += OnPreCull;
            initialised = true;
            AtmospheresLog.Say("lamp scatter: ready (host hook " + (Services.PostUpscale.HostAvailable ? "found" : "absent") + ", depth "
                               + (Services.PostUpscale.DepthAvailable ? "found" : "absent") + ")");
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
            selector.Clear();
            states.Clear();
            lights.Clear();
        }

        public void Tick(float dt)
        {
            if (!initialised) return;
            if (!hostRegistered && !hostFailed && Services.PostUpscale.DepthAvailable)
            {
                hostRegistered = Services.PostUpscale.Register("lamp scatter", hostDraw, true);
                hostRegisterFrame = Time.frameCount;
                if (!hostRegistered) hostFailed = true;
            }
        }

        public void Shutdown()
        {
            Camera.onPreCull -= OnPreCull;
            Detach();
            if (hostRegistered) Services.PostUpscale.Unregister(hostDraw);
            hostRegistered = false;
            if (pass != null) pass.Dispose();
            pass = null;
            initialised = false;
            if (ReferenceEquals(instance, this)) instance = null;
        }

        // The tuning constants, read once a frame (a table lookup each). Keys, default, tuning range for E40:
        //   lamp.fogGain 1 (0-3)  lamp.precipGain 1 (0-3)  lamp.cloudGain 1 (0-3)  lamp.dustGain 1 (0-3)
        //   lamp.cloudSigma 0.05 (0.01-0.2)  lamp.eveDensity 0.5 (0.1-1)  lamp.albedo 0.95 (0.5-1)  lamp.phaseG 0.6 (0-0.9)
        //   lamp.attenScale 0.6 (0.3-1)  lamp.steps 10 (6-16)  lamp.spotInner 0.7 (0.4-0.95)  lamp.nearFadeM 0.3 (0-1)
        //   lamp.gain 6 (1-40)  lamp.minSigma 1e-4 (1e-5-1e-3)  lamp.activeRangeM 150 (50-300)
        //   lamp.sigmaSlew 1 (0.3-3)  lamp.sigmaSlewFloor 0.01 (0.002-0.05)  lamp.brightnessLagS 0.1 (0-0.5)  lamp.fadeS 0.3 (0.1-1)
        //   lamp.rescoreHz 5 (1-10)  lamp.minIntensity 0.1 (0-1)  lamp.minRangeM 2 (0-10)  lamp.glintAmp 0.35 (0-1)
        //   lamp.glintSizeM 0.5 (0.2-2)  lamp.precipExponent 1.3 (1-2)  lamp.maxSigma 0.5 (0.1-1)
        //   lamp.hostGain 1 (0.2-3)  lamp.jitterFrames 0 (0-8)
        //   lamp.forceCloudDensity 0 (0-1, a test floor under the cloud density)
        //   flags lamp.src.fog / .precip / .cloud / .dust true, lamp.depthFlipY false, lamp.rtExplicit true,
        //   lamp.hostAllowed false (Host composite selectable at all), lamp.hostProven.<nvidia|amd|intel|other> false
        // The ranges are the tuning ranges proposed here, not enforced.
        internal static LampTuning ReadTuning()
        {
            LampTuning t = LampTuning.Defaults();
            t.FogGain = Tuning.Get("lamp.fogGain", t.FogGain);
            t.PrecipGain = Tuning.Get("lamp.precipGain", t.PrecipGain);
            t.CloudGain = Tuning.Get("lamp.cloudGain", t.CloudGain);
            t.DustGain = Tuning.Get("lamp.dustGain", t.DustGain);
            t.CloudSigmaFull = Tuning.Get("lamp.cloudSigma", t.CloudSigmaFull);
            t.EveCoverageDensity = Tuning.Get("lamp.eveDensity", t.EveCoverageDensity);
            t.Albedo = Tuning.Get("lamp.albedo", t.Albedo);
            t.PhaseG = Tuning.Get("lamp.phaseG", t.PhaseG);
            t.AttenScale = Tuning.Get("lamp.attenScale", t.AttenScale);
            t.Steps = Tuning.Get("lamp.steps", t.Steps);
            t.SpotInner = Tuning.Get("lamp.spotInner", t.SpotInner);
            t.NearFadeM = Tuning.Get("lamp.nearFadeM", t.NearFadeM);
            t.Gain = Tuning.Get("lamp.gain", t.Gain);
            t.MinSigma = Tuning.Get("lamp.minSigma", t.MinSigma);
            t.ActiveRangeM = Tuning.Get("lamp.activeRangeM", t.ActiveRangeM);
            t.SigmaSlewPerS = Tuning.Get("lamp.sigmaSlew", t.SigmaSlewPerS);
            t.SigmaSlewFloor = Tuning.Get("lamp.sigmaSlewFloor", t.SigmaSlewFloor);
            t.BrightnessLagS = Tuning.Get("lamp.brightnessLagS", t.BrightnessLagS);
            t.FadeS = Tuning.Get("lamp.fadeS", t.FadeS);
            t.RescoreHz = Tuning.Get("lamp.rescoreHz", t.RescoreHz);
            t.MinIntensity = Tuning.Get("lamp.minIntensity", t.MinIntensity);
            t.MinRangeM = Tuning.Get("lamp.minRangeM", t.MinRangeM);
            t.GlintAmp = Tuning.Get("lamp.glintAmp", t.GlintAmp);
            t.GlintSizeM = Tuning.Get("lamp.glintSizeM", t.GlintSizeM);
            t.PrecipLevelExponent = Tuning.Get("lamp.precipExponent", t.PrecipLevelExponent);
            t.MaxSigma = Tuning.Get("lamp.maxSigma", t.MaxSigma);
            return t.Clamped();
        }

        // Which medium sources count: the probe's flags, each on its own.
        internal static LampSources ReadSources()
        {
            return new LampSources
            {
                Fog = Tuning.Flag("lamp.src.fog", true),
                Precip = Tuning.Flag("lamp.src.precip", true),
                Cloud = Tuning.Flag("lamp.src.cloud", true),
                Dust = Tuning.Flag("lamp.src.dust", true)
            };
        }

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
                watch.Reset();
                watch.Start();
                Compose(cam);
                watch.Stop();
                CpuMs += (watch.Elapsed.TotalMilliseconds - CpuMs) * 0.05;
                failures = 0;
            }
            catch (Exception e)
            {
                failures++;
                AtmospheresLog.Failed("lamp-scatter", "composing the lamp scatter failed", e);
                Drawing = false;
                drawCount = 0;
                Detach();
                if (failures >= FailureLimit)
                {
                    AtmospheresLog.WarnOnce("lamp scatter: failed " + FailureLimit + " frames in a row; the lamp scatter is switched off");
                    Shutdown();
                }
            }
        }

        // Whether the camera is inside a vessel: the interior (IVA) and the eyes of a kerbal on foot. The lamps of that vessel are not drawn.
        private static bool CameraInsideActiveVessel()
        {
            CameraManager manager = CameraManager.Instance;
            if (manager == null) return false;
            return manager.currentCameraMode == CameraManager.CameraMode.IVA || manager.currentCameraMode == CameraManager.CameraMode.Internal;
        }

        private void Compose(Camera cam)
        {
            Vessel vessel = GameGlobals.ActiveVessel;
            bool settingsOn = AtmospheresSettings.Enabled && AtmospheresSettings.WeatherEffects && AtmospheresSettings.LightInMedium
                              && AtmospheresSettings.LightInMediumStrength > 0f && AtmospheresSettings.LightCount > 0;
            float dt = Time.timeScale > 0f ? Mathf.Min(Time.unscaledDeltaTime, 0.25f) : 0f;
            if (!settingsOn || vessel == null || vessel.mainBody == null)
            {
                Idle();
                return;
            }

            LampTuning t = ReadTuning();
            LampSources sources = ReadSources();
            double now = Time.realtimeSinceStartup;
            Vector3 camPos = cam.transform.position;
            CelestialBody body = vessel.mainBody;
            WeatherView view = WeatherHooks.View;
            BodyClimate climate = Bodies.Get(body);
            LightMediumRules rules = climate != null ? climate.LightMedium : LightMediumRules.Default;

            // The mode and its cross-fade.
            // The hook counts as answering once it was called, and for the first 120 frames after the registration; a hook that was
            // registered and never called leaves the Scene composite in charge.
            // The Host composite is not selectable without lamp.hostAllowed (the exposure rule is missing, see the header).
            bool hostUsable = Tuning.Flag("lamp.hostAllowed", false) && hostRegistered && !hostFailed && Services.PostUpscale.DepthAvailable
                              && (lastHostCallFrame >= 0 || Time.frameCount - hostRegisterFrame < 120)
                              && !(compose.HostWeight > 0f && lastHostCallFrame >= 0 && Time.frameCount - lastHostCallFrame > 3 && Drawing);
            bool pairProven = Tuning.Flag("lamp.hostProven." + VendorKey(), false);
            WeatherCompose mode = compose.Step(AtmospheresSettings.WeatherCompose, hostUsable, pairProven, Time.unscaledDeltaTime);
            ActiveMode = mode;
            if (mode != lastMode)
            {
                AtmospheresLog.Say("lamp scatter: composing in " + mode + " mode");
                lastMode = mode;
            }

            // The lamps: listed at 1 Hz, scored at the rescoring rate, faded every frame.
            lights.Refresh(camPos, t.ActiveRangeM, now);
            if (now - lastScore >= 1.0 / Math.Max(t.RescoreHz, 0.1f))
            {
                lastScore = now;
                Vessel hide = CameraInsideActiveVessel() ? vessel : null;
                lights.Candidates(camPos, hide, ref t, candidates);
                LitCandidates = candidates.Count;
                QualityTier tier = AtmospheresSettings.QualityTier != QualityTier.Auto ? AtmospheresSettings.QualityTier : WeatherHooks.Caps.Tier;
                selector.Rescore(candidates, Math.Min(LampMath.Cap(AtmospheresSettings.LightCount, tier), MaxDraws));
            }
            selector.Step(dt, t.FadeS);
            LastSelectedCount = selector.Count;
            KnownLights = lights.KnownCount;

            // The medium at each lamp and at the camera, 10 Hz.
            bool mediumDue = now - lastMedium >= MediumSeconds;
            if (mediumDue)
            {
                lastMedium = now;
                UpdateWind(cam, body, view);
            }
            float bodyScale = rules.Density * AtmospheresSettings.LightInMediumStrength;
            float dustSigma = rules.DustScatter;
            foreach (var kv in states) kv.Value.Seen = false;
            LampSigma cameraSigma = default(LampSigma);
            bool cameraDone = false;
            float maxSigma = 0f, nearest = float.MaxValue;
            drawCount = 0;
            // The lamps of the vessel the camera is inside are hidden at once, not at the next rescoring.
            Vessel hideNow = CameraInsideActiveVessel() ? vessel : null;
            LampSigma firstParts = default(LampSigma);
            float firstCamera = 0f;
            bool haveFirst = false;
            for (int i = 0; i < selector.Count && drawCount < MaxDraws; i++)
            {
                int id = selector.IdAt(i);
                Light light = lights.Find(id);
                if (light == null) continue;
                if (hideNow != null && lights.VesselOf(id) == hideNow) continue;
                LampState s;
                if (!states.TryGetValue(id, out s))
                {
                    s = new LampState { Id = id };
                    states[id] = s;
                }
                s.Seen = true;
                Vector3 position = light.transform.position;
                bool first = !s.Evaluated;
                if (mediumDue || first)
                {
                    s.Evaluated = true;
                    LampSigma at = medium.Evaluate(position, body, view, sources, ref t, bodyScale, dustSigma);
                    s.Parts = at;
                    s.SigmaTarget = at.Total;
                    if (!cameraDone)
                    {
                        cameraSigma = medium.Evaluate(camPos, body, view, sources, ref t, bodyScale, dustSigma);
                        cameraDone = true;
                    }
                    s.SigmaCameraTarget = cameraSigma.Total;
                    // A lamp that enters the set fades in; its sigma starts where the air is, not at zero.
                    if (first)
                    {
                        s.Sigma = s.SigmaTarget;
                        s.SigmaCamera = s.SigmaCameraTarget;
                    }
                }
                s.Sigma = LampMath.SlewSigma(s.Sigma, s.SigmaTarget, t.SigmaSlewPerS, t.SigmaSlewFloor, dt);
                s.SigmaCamera = LampMath.SlewSigma(s.SigmaCamera, s.SigmaCameraTarget, t.SigmaSlewPerS, t.SigmaSlewFloor, dt);

                // The brightness: the intensity times the fade (smooth), lagged by 0.1 s.
                float fade = selector.FadeAt(i);
                fade = fade * fade * (3f - 2f * fade);
                float target = light.intensity * fade;
                s.Scalar = LampMath.Lag(s.Scalar, target, t.BrightnessLagS, dt);

                if (!haveFirst)
                {
                    haveFirst = true;
                    firstParts = s.Parts;
                    firstCamera = s.SigmaCamera;
                }
                float distance = (position - camPos).magnitude;
                if (distance < nearest) nearest = distance;
                if (s.Sigma > maxSigma) maxSigma = s.Sigma;
                if (s.Sigma <= 0f || s.Scalar <= 1e-4f) continue;

                Color c = light.color;
                Color tint = rules.ScatterColour;
                float scale = s.Scalar * t.Gain;
                bool spot = light.type == LightType.Spot;
                float halfAngle = spot ? Mathf.Clamp(light.spotAngle, 1f, 170f) * 0.5f * Mathf.Deg2Rad : 0f;
                LampDraw d = default(LampDraw);
                d.Position = position;
                d.Axis = light.transform.forward;
                d.Range = light.range;
                d.CosOuter = spot ? Mathf.Cos(halfAngle) : 0f;
                d.HalfAngleRad = halfAngle;
                d.Colour = new Color(c.r * tint.r * scale, c.g * tint.g * scale, c.b * tint.b * scale, 1f);
                d.Sigma = s.Sigma;
                d.SigmaCamera = s.SigmaCamera;
                d.GlintAmp = LampMath.GlintDepth(t.GlintAmp, view.Level, s.Parts.Precip, s.Parts.Total);
                draws[drawCount++] = d;
            }
            if (drawCount > 0)
            {
                int side = 0;
                float sideVertical = float.MaxValue;
                for (int k = 0; k < drawCount; k++)
                {
                    Vector3 up = (draws[k].Position - (Vector3)body.position).normalized;
                    // A point lamp has no axis, so a spot lamp is preferred whatever its tilt.
                    float vertical = Mathf.Abs(Vector3.Dot(draws[k].Axis.normalized, up)) + (draws[k].HalfAngleRad > 0f ? 0f : 10f);
                    if (vertical < sideVertical)
                    {
                        sideVertical = vertical;
                        side = k;
                    }
                }
                SideLampPosition = draws[side].Position;
                SideLampAxis = draws[side].Axis;
                SideLampRange = draws[side].Range;
                SideLampHalfAngleRad = draws[side].HalfAngleRad;
            }
            // The medium at the first lamp of the set, whether or not a lamp is drawn (the probe reads it every frame).
            LastSigma = firstParts;
            LastSigmaCamera = firstCamera;
            EveLayers = medium.EveLayerCount;

            // States of lamps that left the set are forgotten.
            stale.Clear();
            foreach (var kv in states) if (!kv.Value.Seen) stale.Add(kv.Key);
            for (int k = 0; k < stale.Count; k++) states.Remove(stale[k]);

            // Drift of the flakes the glints ride on: the wind and the fall, in the world.
            drift += (windAxisE * view.WindWorld.x + windAxisN * view.WindWorld.y + windAxisU * (view.WindWorld.z - view.FlakeFallMS)) * dt;
            // The noise table repeats every 256 cells in each axis: wrapping the drift by that period is seamless. A floating-origin
            // or Krakensbane shift moves the world positions the pattern is read at and shows as one jump of the glints [VERIFY].
            float period = 256f * t.GlintSizeM;
            drift = new Vector3(Mathf.Repeat(drift.x, period), Mathf.Repeat(drift.y, period), Mathf.Repeat(drift.z, period));

            frame = new LampFrame
            {
                CameraPosition = camPos,
                CameraForward = cam.transform.forward,
                Near = cam.nearClipPlane,
                Far = cam.farClipPlane,
                UseDepth = true,
                DepthFlipY = Tuning.Flag("lamp.depthFlipY", false),
                ReversedZ = SystemInfo.usesReversedZBuffer,
                JitterOffset = Tuning.Get("lamp.jitterFrames", 0f) > 0f ? (Time.frameCount % (int)Tuning.Get("lamp.jitterFrames", 0f)) : 0f,
                Drift = drift,
                Weight = 1f,
                Tuning = t
            };

            bool worth = drawCount > 0 && LampMath.WorthHolding(wasDrawing, maxSigma, nearest, ref t);
            if (wasDrawing && !worth) idleSince = now;
            wasDrawing = worth;
            Drawing = worth;
            LastDrawCount = worth ? drawCount : 0;
            if (!worth)
            {
                // The buffers go at once; the depth texture stays for 5 s, so that patchy snow does not allocate it again and again.
                Detach(false);
                if (now - idleSince > 5.0 && pass != null) pass.ReleaseDepth();
                return;
            }
            if (!announced)
            {
                announced = true;
                AtmospheresLog.Say("lamp scatter: " + drawCount + " lamp(s) drawn, sigma " + LastSigma.Total.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)
                                   + " (fog " + LastSigma.Fog.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + ", precipitation "
                                   + LastSigma.Precip.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + ", cloud "
                                   + LastSigma.Cloud.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + ", dust "
                                   + LastSigma.Dust.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + ")");
            }

            if (compose.SceneWeight > 0f)
            {
                Attach(cam);
                pass.EnsureDepth(cam.pixelWidth, cam.pixelHeight);
                depthBuffer.Clear();
                depthBuffer.BeginSample("ReDefinition lamp depth");
                pass.RecordDepthCopy(depthBuffer);
                // The copy is a blit into its own target; the camera's target is bound again, here and at the start of the draw, so
                // that neither buffer depends on what Unity does with the target between two camera events (Tuning lamp.rtExplicit,
                // default on; the series probe measures the beam with it off as well) [VERIFY in K-W3b].
                bool explicitTarget = Tuning.Flag("lamp.rtExplicit", true);
                if (explicitTarget) depthBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                depthBuffer.EndSample("ReDefinition lamp depth");
                drawBuffer.Clear();
                drawBuffer.BeginSample("ReDefinition lamp scene");
                if (explicitTarget) drawBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                LampFrame f = frame;
                f.Weight = compose.SceneWeight;
                pass.Record(drawBuffer, draws, drawCount, ref f, pass.DepthCopy);
                drawBuffer.EndSample("ReDefinition lamp scene");
                DepthCopies++;
            }
            else
            {
                Detach();
            }
        }

        // The wind and the up axis in the world frame, for the drift of the flakes (10 Hz).
        private void UpdateWind(Camera cam, CelestialBody body, WeatherView view)
        {
            BodyFrame.Compute(body, cam.transform.position, ref basis);
            windAxisE = (Vector3)basis.East;
            windAxisU = (Vector3)basis.Up;
            windAxisN = (Vector3)basis.North;
        }

        // Nothing to draw (the setting is off, no flight): nothing attached, the state forgotten.
        private void Idle()
        {
            Drawing = false;
            LastDrawCount = 0;
            LitCandidates = 0;
            drawCount = 0;
            Detach();
            selector.Clear();
            states.Clear();
        }

        // The draw the host calls after the upscaler. Records the Host composite from the frame that onPreCull computed.
        private void HostCompose(CommandBuffer cmd, RenderTexture upscaled, RenderTexture depth, Camera camera)
        {
            try
            {
                lastHostCallFrame = Time.frameCount;
                Camera flight = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
                if (pass == null || camera != flight || drawCount <= 0 || !Drawing || compose.HostWeight <= 0f || upscaled == null) return;
                LampFrame f = frame;
                f.Weight = compose.HostWeight * Tuning.Get("lamp.hostGain", 1f);
                f.UseDepth = depth != null;
                cmd.BeginSample("ReDefinition lamp host");
                cmd.SetRenderTarget(upscaled);
                cmd.SetViewProjectionMatrices(camera.worldToCameraMatrix, GL.GetGPUProjectionMatrix(camera.nonJitteredProjectionMatrix, true));
                pass.Record(cmd, draws, drawCount, ref f, depth);
                // [VERIFY] the matrices the host expects after its hook; the camera's own are put back.
                cmd.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
                cmd.EndSample("ReDefinition lamp host");
            }
            catch (Exception e)
            {
                hostFailed = true;
                AtmospheresLog.Failed("lamp-scatter-host", "the host composite failed and is dropped", e);
            }
        }

        private void Attach(Camera cam)
        {
            if (depthBuffer != null && attachedCamera == cam) return;
            Detach(false);
            depthBuffer = new CommandBuffer { name = "ReDefinition Atmospheres lamp depth" };
            drawBuffer = new CommandBuffer { name = "ReDefinition Atmospheres lamp scatter" };
            attachedCamera = cam;
            cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, depthBuffer);
            cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, drawBuffer);
            BuffersAttached = true;
        }

        // Takes the buffers off the camera. The depth texture goes with them unless keepDepth says it stays (the caller frees it later).
        private void Detach(bool releaseDepth = true)
        {
            BuffersAttached = false;
            if (depthBuffer == null) return;
            if (attachedCamera != null)
            {
                attachedCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, depthBuffer);
                attachedCamera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, drawBuffer);
            }
            depthBuffer.Release();
            drawBuffer.Release();
            depthBuffer = null;
            drawBuffer = null;
            attachedCamera = null;
            if (releaseDepth && pass != null) pass.ReleaseDepth();
        }
    }
}
