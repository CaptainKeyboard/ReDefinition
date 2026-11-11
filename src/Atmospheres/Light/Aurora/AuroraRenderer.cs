using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // Package D38. The aurora: a camera-centred dome (AuroraPass and the shader ReDefinition/AtmospheresAurora) drawn on the flight
    // camera from a command buffer at BeforeForwardAlpha. A module (started by the registry), no game object.
    //
    // Draw order: after the opaque queue, before the transparent range (queue above 2500). Scatterer's sky sphere (render queue 2998,
    // multiplicative extinction then soft-additive in-scatter, ZWrite off) and EVE's 2D cloud layers (queue 2999, set by Scatterer)
    // draw after this buffer, so the sky extincts the aurora, which lies above the atmosphere, and the clouds cover it. That follows
    // from Scatterer's source (SkyNode.cs, EVEReflectionHandler.cs); the installed build and the result in the frame are checked by
    // the series K-W3b (start aurora, rows D38.order.*) [VERIFY]. RECON P6 found no depth at AfterForwardOpaque with Scatterer and a
    // depth at BeforeForwardAlpha that is not the camera's projection (an empty sky view reads 0.314), so BeforeForwardAlpha is the
    // earliest event with a depth and the event stays; the control AfterForwardAlpha (tuning aurora.event 1) is in the series.
    // Where the dome may draw: the tuning aurora.skyTest picks the test (AuroraSkyTest). The default is the camera's hardware depth
    // buffer (the GPU's depth test against the buffer the opaque scene wrote, no copy); the modes that test a copy of
    // BuiltinRenderTextureType.Depth, ResolvedDepth or the global texture against the far value stay available, and the series
    // measures every one in the real sky and decides the default [VERIFY]. Motion vectors: none; the aurora is slow emissive light and
    // only the camera moves it on the screen. Masks: none added; the upscaler's reactive mask is not used (DLSS has none). HUD-less
    // frame generation: the dome is part of the scene before the upscaler, so the HUD-less copy contains it [VERIFY in a proxy run].
    // RT input: none; the dome is a command-buffer draw, not a scene renderer, so the ray-traced scene does not see it
    // (CloudLayerKinds only knows EVE layers). Exposure: the emission is added before the post-processing, so TUFX's auto exposure
    // scales it like the rest of the scene; the extra scale is Tuning aurora.exposureScale (1) [VERIFY against TUFX in the game].
    // Order: before the upscaler.
    //
    // Not built here: the Volumetric variant (an EVE layer through the C23 binding). The setting Volumetric draws the shell and says so
    // once in the log.
    [AtmospheresModule(64)]
    internal sealed class AuroraRenderer : IAtmospheresModule
    {
        internal const string ShaderName = "ReDefinition/AtmospheresAurora";
        private const int FailureLimit = 5;
        private const double ForeignScanSeconds = 5.0;

        private AuroraPass pass;
        private CommandBuffer buffer;
        private Camera bufferCamera;
        private CameraEvent bufferEvent;
        private bool initialised;
        private int failures;
        private int lastFrame = -1;
        private string foreignName;
        private string foreignBody = "";
        private double foreignAt = double.NegativeInfinity;
        private string loggedState = "";
        private string loggedMode = "";

        // The state for diagnostics and the series.
        internal static bool Ready { get; private set; }
        internal static bool Drawing { get; private set; }
        internal static float LastIntensity { get; private set; }
        internal static float LastActivity { get; private set; }
        internal static float LastRingLatDeg { get; private set; }
        internal static bool ForeignDetected { get; private set; }
        // The sky test and the event of the last draw (the series reads them to know which variant it measured).
        internal static AuroraSkyTest LastSkyTest { get; private set; }
        internal static AuroraEvent LastEvent { get; private set; }
        // The frame number of the last recorded draw: the probe waits for it to move after it changes a tuning value.
        internal static int LastDrawFrame { get; private set; }
        // The share of the snow albedo the aurora's ambient light would add (0 unless Tuning aurora.ambientTint is on); the ambient owner
        // reads it, nothing here writes the ambient.
        internal static float AmbientShare { get; private set; }

        public void Init()
        {
            string reason;
            Shader shader = AtmosphereShaders.Get(ShaderName, out reason);
            if (shader == null)
            {
                AtmospheresLog.WarnOnce("aurora: the shader " + ShaderName + " is not available (" + reason + "); the aurora stays off");
                return;
            }
            pass = new AuroraPass(shader);
            Camera.onPreCull += OnPreCull;
            initialised = true;
            Ready = true;
            AtmospheresLog.Say("aurora: renderer ready");
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
            foreignAt = double.NegativeInfinity;
            foreignName = null;
            loggedState = "";
        }

        public void Tick(float dt)
        {
        }

        public void Shutdown()
        {
            Camera.onPreCull -= OnPreCull;
            Detach();
            if (pass != null) pass.Dispose();
            pass = null;
            initialised = false;
            Ready = false;
            Drawing = false;
        }

        // The tuning constants, read once a frame. Keys, default, tuning range for E40:
        //   aurora.gain 0.9 (0.3-2)   aurora.exposureScale 1 (0.5-3)   aurora.foldPhase 0.55 (0-1.2)   aurora.redStart 0.55 (0.3-0.9)
        //   aurora.rayTileM 400000 (100000-800000)   aurora.rayContrast 0.5 (0-1)   aurora.stepM 6000 (3000-12000)
        //   aurora.maxSteps 14 (6-24)   aurora.horizonStartDeg 3 (2-6)   aurora.horizonFullDeg 10 (6-20)
        //   aurora.driftPerS 0.004 (0-0.02, texture units a second)   aurora.pathCap 3 (1-6)   aurora.baseShare 0.786 (0.5-0.95)
        //   flag aurora.ambientTint false (the faint green ambient share, at most 2 % of the snow albedo, scaled by auroraAmbientTint)
        //   aurora.skyTest 0 (0-4: 0 hardware depth buffer, 1 copy of Depth, 2 copy of ResolvedDepth, 3 copy of the global texture, 4 none)
        //   aurora.event 0 (0 BeforeForwardAlpha, 1 AfterForwardAlpha)   aurora.ridgeRepeat 1 (0 or 1; 0 keeps one ridge, for the taste sheet)
        //   aurora.skyDepthRef 0 and aurora.skyDepthTol 0 (copy modes: a tolerance above 0 tests |copy - ref| instead of the far value)
        //   aurora.forceActivity -1 (series and probes: 0-1 forces the activity, -1 is the model's own)
        //   aurora.forceEvent 0 (with a forced activity: 0-1 the strong event's share, which pushes the oval towards the equator)
        // The ranges are proposed, not enforced.
        internal static AuroraLook ReadLook()
        {
            AuroraLook l = AuroraLook.Defaults();
            l.Gain = Tuning.Get("aurora.gain", l.Gain);
            l.ExposureScale = Tuning.Get("aurora.exposureScale", l.ExposureScale);
            l.FoldPhase = Tuning.Get("aurora.foldPhase", l.FoldPhase);
            l.RedStart = Tuning.Get("aurora.redStart", l.RedStart);
            l.RayTileM = Tuning.Get("aurora.rayTileM", l.RayTileM);
            l.RayContrast = Tuning.Get("aurora.rayContrast", l.RayContrast);
            l.StepM = Tuning.Get("aurora.stepM", l.StepM);
            l.MaxSteps = Tuning.Get("aurora.maxSteps", l.MaxSteps);
            l.HorizonStartDeg = Tuning.Get("aurora.horizonStartDeg", l.HorizonStartDeg);
            l.HorizonFullDeg = Tuning.Get("aurora.horizonFullDeg", l.HorizonFullDeg);
            l.DriftPerS = Tuning.Get("aurora.driftPerS", l.DriftPerS);
            l.PathCap = Tuning.Get("aurora.pathCap", l.PathCap);
            l.BaseShare = Tuning.Get("aurora.baseShare", l.BaseShare);
            l.RidgeRepeat = Tuning.Get("aurora.ridgeRepeat", l.RidgeRepeat);
            l.SkyDepthRef = Tuning.Get("aurora.skyDepthRef", l.SkyDepthRef);
            l.SkyDepthTol = Tuning.Get("aurora.skyDepthTol", l.SkyDepthTol);
            return l;
        }

        internal static AuroraSkyTest ReadSkyTest()
        {
            return AuroraSkyTests.FromNumber(Tuning.Get("aurora.skyTest", (float)AuroraSkyTest.ZBuffer));
        }

        internal static AuroraEvent ReadEvent()
        {
            return AuroraSkyTests.EventFromNumber(Tuning.Get("aurora.event", (float)AuroraEvent.BeforeForwardAlpha));
        }

        private void OnPreCull(Camera cam)
        {
            if (!initialised || pass == null) return;
            if (!HighLogic.LoadedSceneIsFlight || FlightCamera.fetch == null)
            {
                if (Drawing || buffer != null) Hide();
                return;
            }
            Camera flight = FlightCamera.fetch.mainCamera;
            if (flight == null || cam != flight) return;
            if (lastFrame == Time.frameCount) return;
            lastFrame = Time.frameCount;
            try
            {
                Compose(cam);
                failures = 0;
            }
            catch (Exception e)
            {
                failures++;
                AtmospheresLog.Failed("aurora", "composing the aurora failed", e);
                Drawing = false;
                Detach();
                if (failures >= FailureLimit)
                {
                    AtmospheresLog.WarnOnce("aurora: failed " + FailureLimit + " frames in a row; the renderer is switched off");
                    Shutdown();
                }
            }
        }

        private void Compose(Camera cam)
        {
            CelestialBody body = FlightGlobals.currentMainBody;
            bool settingsOn = AtmospheresSettings.Enabled && AtmospheresSettings.WeatherEffects && AtmospheresSettings.Aurora != AuroraMode.Off;
            if (!settingsOn || body == null || !body.atmosphere)
            {
                Hide();
                return;
            }
            BodyClimate climate = Bodies.Get(body);
            AuroraRules rules = climate != null ? climate.Aurora : AuroraRules.Default;
            if (rules == null || !rules.Enabled)
            {
                Hide();
                return;
            }

            // A foreign aurora layer of this body hands Auto over to it (logged once per state).
            double now = Time.unscaledTime;
            if (body.bodyName != foreignBody || now - foreignAt > ForeignScanSeconds)
            {
                foreignBody = body.bodyName;
                foreignAt = now;
                foreignName = AuroraForeign.Find(body.bodyName);
            }
            ForeignDetected = foreignName != null;
            bool volumetricFallback;
            bool wanted = AuroraPolicy.ShellWanted(AtmospheresSettings.Aurora, ForeignDetected, out volumetricFallback);
            string state = (wanted ? "shell" : "off") + (ForeignDetected ? " foreign " + foreignName : "") + (volumetricFallback ? " volumetric-fallback" : "");
            if (state != loggedState)
            {
                loggedState = state;
                if (ForeignDetected && !wanted)
                    AtmospheresLog.Say("aurora: " + body.bodyName + " already has an aurora layer (" + foreignName + "); setting Auto leaves it to that mod");
                else if (volumetricFallback)
                    AtmospheresLog.Say("aurora: the volumetric variant is not built; drawing the shell on " + body.bodyName);
                else if (wanted)
                    AtmospheresLog.Say("aurora: drawing the shell on " + body.bodyName);
            }
            if (!wanted)
            {
                Hide();
                return;
            }

            // The model: the sun's elevation at the camera, the universal time.
            Vector3d camWorld = cam.transform.position;
            Vector3d up = (camWorld - body.position).normalized;
            CelestialBody sunBody = GameGlobals.Sun;
            if (sunBody == null)
            {
                Hide();
                return;
            }
            Vector3d toSun = (sunBody.position - camWorld).normalized;
            float sunElevation = AuroraModel.SunElevationDeg(Vector3d.Dot(up, toSun));
            double ut = Planetarium.GetUniversalTime();
            AuroraFrame frame = AuroraModel.Compute(ut, sunElevation, true, rules.MinLatDeg, rules.Strength);
            // A forced activity (series and probes only; default -1 is the model's own): the oval, width and brightness follow it.
            float forcedActivity = Tuning.Get("aurora.forceActivity", -1f);
            if (forcedActivity >= 0f)
            {
                float forcedEvent = Mathf.Clamp01(Tuning.Get("aurora.forceEvent", 0f));
                frame = AuroraModel.FromActivity(Mathf.Clamp01(forcedActivity), forcedEvent, sunElevation, rules.MinLatDeg, rules.Strength);
            }
            LastIntensity = frame.Intensity;
            LastActivity = frame.Activity;
            LastRingLatDeg = frame.RingLatDeg;
            AmbientShare = AuroraPolicy.AmbientShare(Tuning.Flag("aurora.ambientTint", false), AtmospheresSettings.AuroraAmbientTint, frame.Intensity);
            if (!frame.Draw)
            {
                Hide();
                return;
            }

            AuroraLook look = ReadLook();
            double depth = body.atmosphereDepth;
            if (depth <= 0.0)
            {
                Hide();
                return;
            }
            AuroraSkyTest skyTest = ReadSkyTest();
            AuroraEvent evt = ReadEvent();
            // The pole axis and the longitude reference are the body transform's up and right, so the pattern turns with the body
            // [VERIFY on a tilted body: the series row D38.pole_axis_agrees compares up with the body's rotation axis].
            Vector3 pole = body.transform.up.normalized;
            Vector3 refA = Vector3.ProjectOnPlane(body.transform.right, pole).normalized;
            var draw = new AuroraDraw
            {
                CameraPosition = cam.transform.position,
                PlanetCentre = (Vector3)(body.position - camWorld),
                PlanetRadius = (float)body.Radius,
                Pole = pole,
                RefA = refA,
                BaseAltitude = (float)(depth * look.BaseShare),
                TopAltitude = (float)depth,
                Frame = frame,
                Look = look,
                Colour = rules.Colour,
                Seconds = ut,
                FovYRad = cam.fieldOfView * Mathf.Deg2Rad,
                WidthPx = cam.pixelWidth,
                HeightPx = cam.pixelHeight,
                SkyTest = skyTest
            };
            Attach(cam, CameraEventOf(evt), AuroraSkyTests.NeedsDepthTexture(skyTest));
            buffer.Clear();
            buffer.BeginSample("ReDefinition aurora");
            pass.Record(buffer, ref draw);
            buffer.EndSample("ReDefinition aurora");
            Drawing = true;
            LastSkyTest = skyTest;
            LastEvent = evt;
            LastDrawFrame = Time.frameCount;
            string mode = AuroraSkyTests.Name(skyTest) + "/" + evt;
            if (mode != loggedMode)
            {
                loggedMode = mode;
                AtmospheresLog.Say("aurora: sky test " + AuroraSkyTests.Name(skyTest) + ", event " + evt);
            }
        }

        private static CameraEvent CameraEventOf(AuroraEvent evt)
        {
            return evt == AuroraEvent.AfterForwardAlpha ? CameraEvent.AfterForwardAlpha : CameraEvent.BeforeForwardAlpha;
        }

        private void Hide()
        {
            Drawing = false;
            LastIntensity = 0f;
            AmbientShare = 0f;
            Detach();
        }

        private void Attach(Camera cam, CameraEvent evt, bool needsDepthTexture)
        {
            if (buffer != null && bufferCamera == cam && bufferEvent == evt)
            {
                if (needsDepthTexture) cam.depthTextureMode |= DepthTextureMode.Depth;
                return;
            }
            Detach();
            buffer = new CommandBuffer { name = "ReDefinition Atmospheres aurora" };
            bufferCamera = cam;
            bufferEvent = evt;
            cam.AddCommandBuffer(evt, buffer);
            if (needsDepthTexture) cam.depthTextureMode |= DepthTextureMode.Depth;
        }

        private void Detach()
        {
            if (buffer == null) return;
            if (bufferCamera != null) bufferCamera.RemoveCommandBuffer(bufferEvent, buffer);
            buffer.Release();
            buffer = null;
            bufferCamera = null;
        }

        // The numbers as one log line for the series and the probes.
        internal static string Describe()
        {
            return "aurora: drawing " + Drawing + ", intensity " + LastIntensity.ToString("0.000", CultureInfo.InvariantCulture)
                   + ", activity " + LastActivity.ToString("0.000", CultureInfo.InvariantCulture)
                   + ", oval " + LastRingLatDeg.ToString("0.0", CultureInfo.InvariantCulture) + " degrees, foreign " + ForeignDetected;
        }
    }
}
