using System;
using System.Collections.Generic;
using System.Diagnostics;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The own cloud pass in the game (package T09): the module that owns the layer stack of the body the flight camera is at and records the
    // layer pass into a command buffer on that camera every frame. Everything the earlier packages built but nothing called is called here:
    // LayerRenderer (T01), the noise model (T02), the light with the moons and the lightning bolts (T03, T07), the temporal reconstruction
    // with the edge mode (T05, T12; LayerReconstructDriver Configure and Record), the far view with the aerial perspective of the body's own
    // air (T06; CloudBridgeAir) and the hand-over of the 2D shell (OwnShell.NearMarchActive), the height maps (T13; HeightDriver.Update), the
    // optical depth hook of the cloud shadow (OwnCloudHook). The settings reach the structs through OwnCloudOptions.
    //
    // When it draws: the mod and the own clouds are on, the cloud source in force is Ours (OwnCloudOptions.Active), the scene is a flight scene
    // on a body with an air, a weather field and a set of layers (the LAYER nodes of the body's CLOUDS node), and the map view is not showing.
    // Otherwise the buffers are off the camera and nothing is allocated per frame.
    //
    // Rendering statement (owner rule: every visual feature states it):
    //  - motion vectors: none of its own. The pass draws into the camera target before the image effects; the upscaler sees the clouds as part
    //    of the colour. With the reconstruction (off by default) the cloud keeps its own history; the upscaler's jitter is not applied to the rays.
    //  - depth: the camera's depth texture, made linear by ReDefinitionAtmospheresLayerDepth.shader, limits the march and the composite
    //    (depth-aware); the clouds write no depth. [VERIFY] that _CameraDepthTexture is readable at the chosen event under the Deferred mod.
    //  - masks: no reactive mask for DLSS. HUD-less frame generation: the pass is part of the scene colour before the HUD. Raytracing: the
    //    rays see the layers through RtOwnClouds (a separate package of the raytracing pack); this module does not feed them.
    //  - draw order: after the lit opaque scene and before the transparents (CameraEvent.BeforeForwardAlpha, tuning cloud.cameraEvent).
    //  - exposure: the clouds are written in the scene's radiance (the sun light's colour times its intensity times cloud.sunGain), so TUFX
    //    and the auto exposure tone-map clouds and sky together. [VERIFY] the sun gain against the sky in the first integrated series.
    [AtmospheresModule(61)]
    internal sealed class OwnCloudModule : IAtmospheresModule
    {
        // The tuning keys of this module (ATMO_TUNING { key = value }, or a matrix row atmo.<key>).
        internal const string TuneCameraEvent = "cloud.cameraEvent", TuneSunGain = "cloud.sunGain", TuneSunAirWithScatterer = "cloud.sunAirWithScatterer",
            TuneMoonGain = "cloud.moonGain", TuneStatsS = "cloud.statsS", TuneReaddEveryFrame = "cloud.readdEveryFrame";
        private const int MapWidth = GeneratorMaps.Width, MapHeight = GeneratorMaps.Height;
        private const float SunGainDefault = 1f;
        // The most layers of the field's coverage cache; the type value of a layer is the precipitation level, 0 (fair) to 1 (storm).
        private static readonly CameraEvent[] Events = { CameraEvent.BeforeForwardAlpha, CameraEvent.AfterForwardAlpha, CameraEvent.BeforeImageEffects };

        // What the series probe and the log read.
        internal sealed class Statistics
        {
            public int FramesDrawn, StackBuilds, MapFetches, HeightBuilds, Cuts, ReconFrames, PlainFrames;
            // The worst times leave the first call out (Mono's JIT and the first catch-up of the generator are in it; FirstHeightMs and
            // FirstMapMs keep it). The upload is the height slices written to the GPU, one a frame (LayerRenderer.StepHeights).
            public double LastHeightMs, WorstHeightMs, LastMapMs, WorstMapMs, FirstHeightMs, FirstMapMs, WorstUploadMs;
            public int BufferAttaches;
            public string Mode = "off", Why = "not started";
            public int Layers;
            public bool NearMarchActive;
            // Whether the cloud shadow's rig is switched on this frame (ownClouds, cloudShadows and a layer) and whether its renderer exists.
            public bool ShadowOn, ShadowReady;
        }

        internal static OwnCloudModule Current { get; private set; }
        internal readonly Statistics Stats = new Statistics();

        private readonly LayerFrame frame = new LayerFrame();
        private readonly ReconSettings recon = new ReconSettings();
        private readonly HeightTuning heightTuning = new HeightTuning();
        private readonly WeatherMap coverageMap = new WeatherMap(), typeMap = new WeatherMap(), layerMap = new WeatherMap();
        private LayerRenderer renderer;
        private LayerReconstructDriver driver;
        private CloudShadowRenderer shadowRenderer;
        private CloudShadowRig shadowRig;
        private Material depthMaterial;
        private CommandBuffer depthCommands;
        private RenderTexture depthTarget;
        private Camera attachedCamera;
        private CameraEvent attachedEvent;
        private LayerStackSource stack;
        private string stackKey = "";
        private CelestialBody body;
        private int structureVersion, weatherCuts;
        private bool broken, shadersFailed, snapMaps, cold, haveCold, noiseWaitLogged, depthSeenLogged, uploadSeen;
        // The buffers stand on the camera (they are added when absent, not every frame); the next time they are looked for on the camera.
        private bool buffersOn;
        private float nextBufferCheck;
        // The inputs of the stack that stands, so that the once-a-second check allocates nothing when they have not changed.
        private IReadOnlyList<CloudLayerSpec> stackSpecs;
        private int stackCap = -1, stackPick = -1;
        private float nextMaps, nextHeights, nextStats, nextWind, nextStackCheck;
        private bool forceMaps, forceHeights, stepMaps, scattererLoaded, mapsPrimed, preCullSubscribed;
        private const int MaxFailures = 3;
        private int failures;
        private Camera pendingCamera;
        private CelestialBody pendingBody;
        private BodyClimate pendingClimate;
        private IWeatherField pendingField;
        private double pendingUt;
        private float pendingNow;
        private int heightSwitches = -1;
        private double lastUt = double.NaN;
        private CloudBridgeAir.Air air;
        private int airBody = -1;

        // The layer stack that stands (null while the pass is off): the CPU consumers read it.
        internal ICloudLayerSource Stack { get { return stack; } }
        internal LayerRenderer Renderer { get { return renderer; } }
        internal LayerReconstructDriver Driver { get { return driver; } }
        internal LayerFrame Frame { get { return frame; } }

        public void Init()
        {
            Current = this;
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
            DropStack("body changed");
            failures = 0;
            CloudBridgeLights.RequestHistoryReset("body changed");
        }

        public void Shutdown()
        {
            Teardown();
            if (ReferenceEquals(Current, this)) Current = null;
        }

        public void Tick(float dt)
        {
            if (broken) return;
            try
            {
                Run(dt);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // --- The frame -----------------------------------------------------------------------------------------------------------------

        private void Run(float dt)
        {
            bool flight = HighLogic.LoadedSceneIsFlight && FlightCamera.fetch != null && FlightGlobals.currentMainBody != null;
            CelestialBody b = flight ? FlightGlobals.currentMainBody : null;
            BodyClimate c = b != null ? Bodies.Get(b) : null;
            CloudSourceKind source = c != null ? EveRenderCompat.EffectiveSource(c.Clouds.Source ?? AtmospheresSettings.CloudSource) : CloudSourceKind.None;
            int specs = c != null ? c.Clouds.Layers.Count : 0;
            bool want = OwnCloudPlan.Wanted(AtmospheresSettings.Enabled, flight, b != null && b.atmosphere, c != null && c.Status.WeatherRuns,
                                            OwnCloudOptions.Active(source), specs, MapView.MapIsEnabled);
            if (!want)
            {
                Stats.Why = !AtmospheresSettings.Enabled ? "mod off" : !flight ? "no flight scene" : c == null ? "no body config" :
                    !OwnCloudOptions.Active(source) ? "ownClouds off or the source is not Ours" : specs == 0 ? "no layers for the body" : "map view or no weather";
                Hide();
                return;
            }
            Camera cam = FlightCamera.fetch.mainCamera;
            if (cam == null)
            {
                Hide();
                return;
            }
            if (!EnsureBuilt()) return;
            if (!ReferenceEquals(b, body)) DropStack("body changed");
            body = b;
            IWeatherField field = WeatherHub.Field;
            if (field is NullField)
            {
                Stats.Why = "no weather field";
                Hide();
                return;
            }
            double ut = Planetarium.GetUniversalTime();
            float now = Time.realtimeSinceStartup;
            if (!double.IsNaN(lastUt) && OwnCloudPlan.WeatherCut(ut, lastUt, dt, TimeWarp.CurrentRate)) weatherCuts++;
            lastUt = ut;

            EnsureStack(c, b, field, ut, now);
            if (stack == null || stack.LayerCount == 0)
            {
                Hide();
                return;
            }
            FetchMaps(b, field, ut, now, dt);
            BuildHeights(b, field, ut, now);
            // The upload of a new stack and the height slices (one a frame) are timed: the worst frame is a hitch of the pass (the first call, with Mono's
            // JIT in it, is left out of the worst).
            long uploadStart = Stopwatch.GetTimestamp();
            renderer.Upload(stack);
            renderer.StepHeights();
            double uploadMs = (Stopwatch.GetTimestamp() - uploadStart) * 1000.0 / Stopwatch.Frequency;
            if (uploadSeen) Stats.WorstUploadMs = Math.Max(Stats.WorstUploadMs, uploadMs);
            uploadSeen = true;
            // The noise volumes are made on a worker (a change of the size level makes them again); until they are up nothing is drawn.
            OwnCloudOptions.Apply(renderer.Noise.Switches);
            // A generation that failed is not started again every frame (Problem stays until the size level or the seed changes it).
            if (renderer.Noise.Problem == null) renderer.Noise.StartGeneration();
            if (!renderer.Noise.Poll())
            {
                if (renderer.Noise.Problem != null) Stats.Why = renderer.Noise.Problem;
                else if (!noiseWaitLogged) { noiseWaitLogged = true; AtmospheresLog.Say("own clouds: waiting for the noise volumes"); }
                Hide(false);
                return;
            }
            renderer.Noise.Advance(dt);
            if (!mapsPrimed)
            {
                Stats.Why = "waiting for the first maps";
                Hide(false);
                return;
            }
            // The camera's pose and the floating origin move after the modules tick (FlightCamera's LateUpdate): the numbers that hold the pose are
            // written and the buffer recorded in the camera's pre-cull, with the state of this tick.
            pendingCamera = cam;
            pendingBody = b;
            pendingClimate = c;
            pendingField = field;
            pendingUt = ut;
            pendingNow = now;
            if (!preCullSubscribed)
            {
                Camera.onPreCull += OnPreCull;
                preCullSubscribed = true;
                // After this module's handler, so the rig's handler of the same cull reads the frame written just before it.
                if (shadowRig != null) shadowRig.Start();
            }
        }

        private void OnPreCull(Camera cam)
        {
            if (broken || pendingCamera == null || !ReferenceEquals(cam, pendingCamera) || renderer == null || stack == null) return;
            try
            {
                FillFrame(cam, pendingBody, pendingClimate, pendingField, pendingUt, pendingNow);
                FillShadow(pendingBody, frame);
                Record(cam, pendingBody);
                PublishStats(pendingNow);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private void Fail(Exception e)
        {
            failures++;
            AtmospheresLog.Failed("own-clouds", "the own cloud pass failed (" + failures + " of " + MaxFailures + ")", e);
            Hide(true);
            if (failures >= MaxFailures)
            {
                broken = true;
                Teardown();
            }
        }

        // --- Build and teardown --------------------------------------------------------------------------------------------------------

        private bool EnsureBuilt()
        {
            if (renderer != null) return true;
            if (shadersFailed) return false;
            string error;
            Shader layers = AtmosphereShaders.Get(LayerRenderer.ShaderName, out error);
            Shader depth = layers != null ? AtmosphereShaders.Get("ReDefinition/AtmospheresLayerDepth", out error) : null;
            if (layers == null || depth == null)
            {
                shadersFailed = true;
                Stats.Why = "shader missing";
                AtmospheresLog.WarnOnce("own clouds: " + error + "; the own clouds are not drawn");
                return false;
            }
            // Everything is made in locals and handed to the fields at the end: a failure or an exception on the way disposes what was made and
            // leaves the module without a half-built renderer (which would throw on every frame until the module switched itself off).
            var r = new LayerRenderer();
            Material depthMat = null;
            CommandBuffer depthCb = null;
            LayerReconstructDriver d = null;
            bool done = false;
            string reconError, edgeError;
            Shader edgeShader;
            try
            {
                if (!r.Init(layers))
                {
                    shadersFailed = true;
                    AtmospheresLog.WarnOnce("own clouds: " + r.Problem + "; the own clouds are not drawn");
                    return false;
                }
                depthMat = new Material(depth) { hideFlags = HideFlags.HideAndDontSave };
                depthCb = new CommandBuffer { name = "ReDefinition layer depth" };
                // The reconstruction and its edge mode need their shaders; without them the plain pass draws (the setting stays off in effect).
                Shader reconShader = AtmosphereShaders.Get(LayerReconstructor.ShaderName, out reconError);
                edgeShader = AtmosphereShaders.Get(LayerReconstructor.EdgeShaderName, out edgeError);
                d = new LayerReconstructDriver();
                if (reconShader == null || !d.Init(reconShader, edgeShader))
                {
                    AtmospheresLog.Say("own clouds: the reconstruction is not available (" + (reconError ?? "it does not compile") + "); the plain pass draws");
                    d.Dispose();
                    d = null;
                }
                done = true;
            }
            finally
            {
                if (!done)
                {
                    if (d != null) d.Dispose();
                    if (depthCb != null) depthCb.Release();
                    if (depthMat != null) UnityEngine.Object.Destroy(depthMat);
                    r.Dispose();
                }
            }
            r.SpreadHeights = true;
            renderer = r;
            depthMaterial = depthMat;
            depthCommands = depthCb;
            driver = d;
            scattererLoaded = EveRenderCompat.ScattererLoaded;
            if (edgeShader == null) AtmospheresLog.Say("own clouds: the edge mode shader is missing (" + edgeError + ")");
            BuildShadow();
            AtmospheresLog.Say("own clouds: ready, shaders loaded (layers, depth" + (driver != null ? ", reconstruct" : "") + (edgeShader != null ? ", edge" : "")
                               + (shadowRenderer != null ? ", cloud shadow" : "") + ")");
            return true;
        }

        // The cloud shadow's renderer (T04), made with the pass; the rig that attaches its buffers starts after this module's pre-cull handler
        // is subscribed (Run), so that the rig reads the frame the handler has just written. Without the shader the clouds draw without shadow.
        private void BuildShadow()
        {
            string error;
            Shader shader = AtmosphereShaders.Get(CloudShadowRenderer.ShaderName, out error);
            var s = new CloudShadowRenderer();
            if (shader == null || !s.Init(shader))
            {
                AtmospheresLog.Say("own clouds: the cloud shadow is not available (" + (error ?? s.Problem) + ")");
                return;
            }
            shadowRenderer = s;
            Stats.ShadowReady = true;
            shadowRig = new CloudShadowRig(s, ShadowRole, () => Sun.Instance != null ? Sun.Instance.sunLight : null);
        }

        // The scene's cameras take the shadow; the scaled-space camera's disc is not wired (its frame is in scaled-space units, open in T09).
        internal static CloudShadowRole ShadowRole(Camera camera)
        {
            CloudShadowRole role = CloudShadowRig.ClassifyByName(camera);
            return role == CloudShadowRole.Scene ? role : CloudShadowRole.None;
        }

        // The shadow's frame from the pass's: the same planet, map frame and sun. On only where the switches say so (CloudShadowSwitches.Active).
        private void FillShadow(CelestialBody b, LayerFrame f)
        {
            if (shadowRig == null) return;
            bool on = OwnCloudOptions.ShadowActive(OwnCloudOptions.LiveSettings,CloudSourceKind.Ours, stack != null ? stack.LayerCount : 0);
            shadowRig.Enabled = on;
            Stats.ShadowOn = on;
            if (!on) return;
            CloudShadowFrame s = shadowRig.Frame;
            s.PlanetCenter = f.PlanetCenter;
            s.RadiusUnits = b.Radius;
            s.MetresPerUnit = 1f;
            s.WorldToMap = f.WorldToMap;
            s.TowardsSun = f.TowardsSun;
            OwnCloudOptions.Apply(s, OwnCloudOptions.LiveSettings);
            shadowRenderer.Upload(stack, renderer.Maps);
        }

        private void EnsureStack(BodyClimate c, CelestialBody b, IWeatherField field, double ut, float now)
        {
            // Checked once a second (a build allocates): the temperature at the camera picks the member of a group, the layer cap limits the count.
            if (stack != null && now < nextStackCheck) return;
            nextStackCheck = now + 1f;
            // Read again each second: the first look of EveRenderCompat may come after this module's first build.
            scattererLoaded = EveRenderCompat.ScattererLoaded;
            WeatherSample sample;
            Vector3d at = FlightCamera.fetch.mainCamera.transform.position;
            double temperature = 288.0;
            if (WeatherHub.Sample(b.flightGlobalsIndex, b.GetLatitude(at), b.GetLongitude(at), 0.0, ut, out sample) && sample.TemperatureK > 100f)
                temperature = sample.TemperatureK;
            CloudBridgeStack.PickByTemperature(temperature, haveCold && cold, OwnCloudPlan.FreezingBandK, out cold);
            haveCold = true;
            int pickIndex = cold ? 1 : 0;
            int draw = OwnCloudOptions.LayersToDraw(OwnCloudOptions.LiveSettings, c.Clouds.Layers.Count);
            // The stack is a function of the specs, the cap and the pick: the same inputs give the same stack, so nothing is built to compare keys.
            if (stack != null && ReferenceEquals(stackSpecs, c.Clouds.Layers) && stackCap == draw && stackPick == pickIndex
                && stack.BodyIndex == b.flightGlobalsIndex) return;
            string key;
            LayerStackSource candidate = BuildStack(c, b, draw, pickIndex, out key);
            stackSpecs = c.Clouds.Layers;
            stackCap = draw;
            stackPick = pickIndex;
            if (stack != null && key == stackKey && stack.BodyIndex == b.flightGlobalsIndex) return;
            stack = candidate;
            stackKey = key;
            structureVersion++;
            Stats.StackBuilds++;
            Stats.Layers = stack.LayerCount;
            forceMaps = forceHeights = snapMaps = true;
            mapsPrimed = false;
            renderer.Maps.Ensure(MapWidth, MapHeight);
            renderer.Noise.Switches.SphereScale = c.Clouds.SphereScale > 0f ? c.Clouds.SphereScale : 1f;
            OwnCloudOpticalDepth.Bind(new CloudShadowField(stack, stack.Tables()));
            OwnCloudHook.Attach();
            airBody = -1;
            AtmospheresLog.Say("own clouds: stack on " + b.name + ": " + stack.LayerCount + " layers [" + key.TrimEnd('|').Replace("|", ", ") + "], "
                               + (cold ? "cold" : "warm") + " group members");
        }

        // In its own method so that the closure over the pick lives only on a build, not in the once-a-second check.
        private static LayerStackSource BuildStack(BodyClimate c, CelestialBody b, int draw, int pickIndex, out string key)
        {
            return CloudBridgeStack.Build(c.Clouds.Layers, draw, g => pickIndex, b.flightGlobalsIndex, b.Radius, b.hasSolidSurface, out key);
        }

        private void DropStack(string reason)
        {
            if (stack == null) return;
            stack = null;
            stackKey = "";
            stackSpecs = null;
            OwnCloudOpticalDepth.Bind(null);
            OwnCloudHook.Detach();
            Stats.Layers = 0;
            AtmospheresLog.Say("own clouds: stack dropped (" + reason + ")");
        }

        // Takes the pass off the camera. dropStack: the pass is off for a reason that lasts (the switch, the source, the scene), so the stack and the
        // shadow hook go too; false for a wait (noise volumes, first maps), which keeps them.
        private void Hide(bool dropStack = true)
        {
            Stats.Mode = "off";
            Stats.NearMarchActive = false;
            Stats.ShadowOn = false;
            if (shadowRig != null) shadowRig.Enabled = false;
            pendingCamera = null;
            SetShell(false);
            Detach();
            if (dropStack && stack != null) DropStack("the pass is off: " + Stats.Why);
        }

        private void Teardown()
        {
            if (preCullSubscribed)
            {
                Camera.onPreCull -= OnPreCull;
                preCullSubscribed = false;
            }
            Hide();
            DropStack("shutdown");
            if (shadowRig != null) shadowRig.Stop();
            shadowRig = null;
            if (shadowRenderer != null) shadowRenderer.Dispose();
            shadowRenderer = null;
            if (driver != null) driver.Dispose();
            driver = null;
            if (renderer != null) renderer.Dispose();
            renderer = null;
            if (depthCommands != null) depthCommands.Release();
            depthCommands = null;
            if (depthTarget != null) { depthTarget.Release(); UnityEngine.Object.Destroy(depthTarget); }
            depthTarget = null;
            if (depthMaterial != null) UnityEngine.Object.Destroy(depthMaterial);
            depthMaterial = null;
        }

        private void Detach()
        {
            if (attachedCamera != null && depthCommands != null) attachedCamera.RemoveCommandBuffer(attachedEvent, depthCommands);
            if (renderer != null) { renderer.Detach(); renderer.Commands.Clear(); }
            if (depthCommands != null) depthCommands.Clear();
            attachedCamera = null;
            buffersOn = false;
        }

        // Whether the camera holds a buffer of that name at the event (GetCommandBuffers hands out new wrapper objects, so the names are compared).
        private static bool CameraHolds(Camera cam, CameraEvent at, string name)
        {
            foreach (CommandBuffer cb in cam.GetCommandBuffers(at))
            {
                if (cb.name == name) return true;
            }
            return false;
        }

        private static void SetShell(bool nearMarchActive)
        {
            EveCloudModule eve = EveCloudModule.Current;
            OwnShell shell = eve != null ? eve.Shell : null;
            if (shell != null) shell.NearMarchActive = nearMarchActive;
        }

        // --- Maps and heights ------------------------------------------------------------------------------------------------------------

        // The coverage of each layer from the field (the layer map of its height where the generator has one, else the shared coverage map)
        // and the type value from the precipitation level; the maps move to their targets with the slew limit (LayerMapStore.Step).
        private void FetchMaps(CelestialBody b, IWeatherField field, double ut, float now, float dt)
        {
            bool due = OwnCloudPlan.Due(now, nextMaps, OwnCloudPlan.MapIntervalS, forceMaps);
            if (due)
            {
                var watch = Stopwatch.StartNew();
                nextMaps = now + OwnCloudPlan.MapIntervalS;
                bool haveShared = field.GetMap(b.flightGlobalsIndex, WeatherMapKind.Coverage, ut, coverageMap) && coverageMap.Width == MapWidth && coverageMap.Height == MapHeight;
                bool haveType = field.GetMap(b.flightGlobalsIndex, WeatherMapKind.Precipitation, ut, typeMap) && typeMap.Width == MapWidth && typeMap.Height == MapHeight;
                GeneratorField gf = GeneratorModule.Current;
                if (haveShared && haveType)
                {
                    for (int l = 0; l < stack.LayerCount; l++)
                    {
                        WeatherMap cover = coverageMap;
                        if (gf != null && gf.GetLayerCoverage(b.flightGlobalsIndex, ut, CloudBridgeStack.MidHeightM(stack.LayerAt(l)), layerMap)
                            && layerMap.Width == MapWidth && layerMap.Height == MapHeight) cover = layerMap;
                        renderer.Maps.SetTargets(l, cover, typeMap);
                        // The target bytes are copied into the store; the shared scratch map can serve the next layer.
                    }
                    stepMaps = true;
                    Stats.MapFetches++;
                }
                Stats.LastMapMs = watch.Elapsed.TotalMilliseconds;
                if (Stats.MapFetches <= 1 && Stats.FirstMapMs == 0.0) Stats.FirstMapMs = Stats.LastMapMs;
                else Stats.WorstMapMs = Math.Max(Stats.WorstMapMs, Stats.LastMapMs);
                forceMaps = false;
            }
            if (stepMaps)
            {
                int open = renderer.Maps.Step(dt, MapSlew.DefaultRate, snapMaps);
                snapMaps = false;
                mapsPrimed = true;
                if (open == 0) stepMaps = false;
            }
        }

        // The height maps of the layers (T13): at a slow rhythm, after a switch changed and after the stack was built.
        private void BuildHeights(CelestialBody b, IWeatherField field, double ut, float now)
        {
            OwnCloudOptions.Apply(heightTuning);
            int switches = (heightTuning.BaseVariation ? 1 : 0) | (heightTuning.BasePhysics ? 2 : 0) | (heightTuning.TopFromUpdraft ? 4 : 0) | (heightTuning.BaseRagged ? 8 : 0);
            bool changed = switches != heightSwitches;
            heightSwitches = switches;
            if (!OwnCloudPlan.Due(now, nextHeights, OwnCloudPlan.HeightInterval(TimeWarp.CurrentRate), forceHeights || changed)) return;
            nextHeights = now + OwnCloudPlan.HeightInterval(TimeWarp.CurrentRate);
            forceHeights = false;
            var watch = Stopwatch.StartNew();
            // The field hands its maps at the generator's size (512 x 256); the builder asks for the same, a smaller size would read nothing.
            HeightDriver.Update(stack, field, heightTuning, ut, (uint)(b.flightGlobalsIndex + 1) * 0x9E3779B1u, MapWidth, MapHeight);
            Stats.HeightBuilds++;
            Stats.LastHeightMs = watch.Elapsed.TotalMilliseconds;
            if (Stats.HeightBuilds <= 1 && Stats.FirstHeightMs == 0.0) Stats.FirstHeightMs = Stats.LastHeightMs;
            else Stats.WorstHeightMs = Math.Max(Stats.WorstHeightMs, Stats.LastHeightMs);
        }

        // --- The frame's numbers ------------------------------------------------------------------------------------------------------------

        private void FillFrame(Camera cam, CelestialBody b, BodyClimate c, IWeatherField field, double ut, float now)
        {
            LayerFrame f = frame;
            OwnCloudOptions.Apply(f);
            OwnCloudOptions.Apply(f.Light);
            OwnCloudOptions.Apply(f.Far);
            f.HasSurface = b.hasSolidSurface;
            f.FrameNumber = Time.frameCount;
            f.UseSceneDepth = true;
            f.PlanetCenter = (Vector3)b.position;
            f.WorldToMap = MapFrame(b);

            // The light: the sun's direction and colour, the scene's ambient light, the moons, the lightning.
            Vector3 camPos = cam.transform.position;
            CelestialBody sunBody = GameGlobals.Sun;
            Vector3 toSun = sunBody != null ? (Vector3)sunBody.position - camPos : ((Vector3)b.position - camPos) + Vector3.up * 1e9f;
            f.TowardsSun = toSun.sqrMagnitude > 1f ? toSun.normalized : Vector3.up;
            Light sun = Sun.Instance != null ? Sun.Instance.sunLight : null;
            float gain = Tuning.Get(TuneSunGain, SunGainDefault);
            Color sunColour = sun != null ? sun.color * (sun.intensity * gain) : Color.white * gain;
            f.SunColour = sunColour;
            Color ambientTop, ambientBottom;
            if (RenderSettings.ambientMode == AmbientMode.Trilight)
            {
                ambientTop = RenderSettings.ambientSkyColor * RenderSettings.ambientIntensity;
                ambientBottom = RenderSettings.ambientGroundColor * RenderSettings.ambientIntensity;
            }
            else
            {
                ambientTop = ambientBottom = RenderSettings.ambientLight * RenderSettings.ambientIntensity;
                ambientBottom *= 0.5f;
            }
            f.AmbientTop = ambientTop;
            f.AmbientBottom = ambientBottom;
            // Where Scatterer draws the air its sunlight colour already carries the air path: the cloud light does not redden it a second time.
            if (scattererLoaded && !Tuning.Flag(TuneSunAirWithScatterer, false)) f.Light.SunAirHeightM = 0f;

            Color moon = Color.black;
            float sunLuminance = Math.Max(0.01f, sunColour.grayscale);
            for (int i = 0; i < CloudLightSettings.MaxLights; i++) f.Light.Lights[i] = new CloudLight { Towards = Vector3.up, Colour = Color.black };
            CloudBridgeLights.MoonLight provider = CloudBridgeLights.Moon;
            Vector3 towards;
            Color moonColour;
            if (provider != null && provider(b, (Vector3d)camPos, out towards, out moonColour))
            {
                moon = moonColour * (sunLuminance * Tuning.Get(TuneMoonGain, 1f));
                f.Light.Lights[0] = new CloudLight { Towards = towards, Colour = moon };
            }
            OwnCloudOptions.LimitMoonLights(f.Light, OwnCloudOptions.LiveSettings);
            LightningModule lightning = LightningModule.Current;
            if (lightning != null) lightning.FillBolts(f.Light, b, OwnCloudPlan.FlashPeakScale(sunLuminance));
            else f.Light.BoltCount = 0;

            // The far view: the body's own air, the in-scatter that follows the sky, the march policy by altitude.
            if (airBody != b.flightGlobalsIndex)
            {
                airBody = b.flightGlobalsIndex;
                double h1 = Math.Max(1000.0, b.atmosphereDepth * 0.1);
                air = CloudBridgeAir.From(b.atmospherePressureSeaLevel * 1000.0, b.atmosphereTemperatureSeaLevel, b.atmosphereMolarMass, b.GetPressure(h1) * 1000.0, h1);
                AtmospheresLog.Say("own clouds: air of " + b.name + ": " + (air.Valid ? "Rayleigh " + air.RayleighPerM.x.ToString("0.0e+0") + " / " + air.RayleighPerM.z.ToString("0.0e+0")
                                   + " per m, scale height " + air.RayleighHeightM.ToString("0") + " m" : "no usable data, the defaults of Earth's air stay"));
            }
            CloudBridgeAir.Apply(f.Far, air);
            f.Far.Inscatter = CloudBridgeAir.Inscatter(sunColour, moon, ambientTop, f.Far.RayleighPerM);
            double altitude = ((Vector3d)camPos - b.position).magnitude - b.Radius;
            f.Far.ApplyTo(f, altitude, b.Radius);

            if (now >= nextWind)
            {
                nextWind = now + 1f;
                UpdateMotion(cam, b, ut);
            }
        }

        // The rotation from world space into the map frame (x towards longitude 0, y towards 90 east, z north), measured from the body's surface
        // points so that its rotation and KSP's handedness are in it.
        private static Matrix4x4 MapFrame(CelestialBody b)
        {
            Vector3 centre = (Vector3)b.position;
            Vector3 x = ((Vector3)b.GetWorldSurfacePosition(0.0, 0.0, 0.0) - centre).normalized;
            Vector3 y = ((Vector3)b.GetWorldSurfacePosition(0.0, 90.0, 0.0) - centre).normalized;
            Vector3 z = ((Vector3)b.GetWorldSurfacePosition(90.0, 0.0, 0.0) - centre).normalized;
            var m = Matrix4x4.identity;
            m.SetRow(0, new Vector4(x.x, x.y, x.z, 0f));
            m.SetRow(1, new Vector4(y.x, y.y, y.z, 0f));
            m.SetRow(2, new Vector4(z.x, z.y, z.z, 0f));
            return m;
        }

        // The wind of each layer's height at the camera as the drift of the noise (m/s in world space).
        private void UpdateMotion(Camera cam, CelestialBody b, double ut)
        {
            Vector3d at = cam.transform.position;
            double lat = b.GetLatitude(at), lon = b.GetLongitude(at);
            Vector3 up = (Vector3)b.GetSurfaceNVector(lat, lon);
            Vector3 centre = (Vector3)b.GetWorldSurfacePosition(lat, lon, 0.0);
            Vector3 east = (Vector3)b.GetWorldSurfacePosition(lat, lon + 0.02, 0.0) - centre;
            Vector3 north = (Vector3)b.GetWorldSurfacePosition(lat + 0.02, lon, 0.0) - centre;
            east = (east - up * Vector3.Dot(east, up)).normalized;
            north = (north - up * Vector3.Dot(north, up)).normalized;
            for (int l = 0; l < stack.LayerCount; l++)
            {
                WeatherSample s;
                if (!WeatherHub.Sample(b.flightGlobalsIndex, lat, lon, CloudBridgeStack.MidHeightM(stack.LayerAt(l)), ut, out s)) continue;
                renderer.Noise.SetMotion(l, new LayerNoiseMotion { DriftMs = east * s.WindWorld.x + north * s.WindWorld.y, RiseMs = Mathf.Clamp(s.WindWorld.z, -5f, 5f) });
            }
        }

        // --- Recording -----------------------------------------------------------------------------------------------------------------------

        private void Record(Camera cam, CelestialBody b)
        {
            EnsureDepthTarget(cam);
            CameraEvent when = Events[OwnCloudPlan.CameraEventIndex(Tuning.Get(TuneCameraEvent, 0f))];
            cam.depthTextureMode |= DepthTextureMode.Depth;
            // The buffers are added once and stay on the camera; their contents are recorded anew every frame. Taking them off and adding them
            // back every frame is not needed for the order (the depth buffer is added first, the layer buffer after it, and a buffer another mod adds
            // later runs after both) and the GPU profiler records that doing so between its markers darkens the frame (GpuProfiler.LayOutCamera), which
            // the profiled K-T2 starts would run into. They are added again when the camera or the event changes, after the pass was hidden, and when a
            // look at the camera once a second does not find them (a camera that was rebuilt). cloud.readdEveryFrame = 1 gives the old behaviour.
            bool readd = Tuning.Flag(TuneReaddEveryFrame, false);
            if (!ReferenceEquals(attachedCamera, cam) || attachedEvent != when)
            {
                Detach();
                attachedCamera = cam;
                attachedEvent = when;
            }
            else if (buffersOn && Time.realtimeSinceStartup >= nextBufferCheck)
            {
                nextBufferCheck = Time.realtimeSinceStartup + 1f;
                if (!CameraHolds(cam, attachedEvent, depthCommands.name) || !CameraHolds(cam, attachedEvent, renderer.Commands.name)) readd = true;
            }
            if (readd && buffersOn)
            {
                cam.RemoveCommandBuffer(attachedEvent, depthCommands);
                renderer.Detach();
                buffersOn = false;
            }
            depthCommands.Clear();
            depthCommands.Blit(BuiltinRenderTextureType.None, depthTarget, depthMaterial);
            if (!buffersOn) cam.AddCommandBuffer(attachedEvent, depthCommands);

            renderer.Commands.Clear();
            var depth = new RenderTargetIdentifier(depthTarget);
            bool reconstruct = false;
            if (driver != null)
            {
                OwnCloudOptions.Apply(recon);
                reconstruct = driver.Configure(Tuning.Get, recon);
            }
            if (reconstruct)
            {
                driver.PlayerCamera = cam;
                int scene = (int)HighLogic.LoadedScene * 100000 + b.flightGlobalsIndex;
                if (CloudBridgeLights.Mark != null) CloudBridgeLights.Mark(renderer.Commands, CloudBridgeLights.MarkBegin);
                driver.Record(renderer.Commands, renderer, frame, cam, depth, BuiltinRenderTextureType.CameraTarget, Time.frameCount, scene,
                              CloudBridgeLights.ResetRequests, weatherCuts, structureVersion);
                if (CloudBridgeLights.Mark != null) CloudBridgeLights.Mark(renderer.Commands, CloudBridgeLights.MarkEnd);
                Stats.ReconFrames++;
                Stats.Mode = "reconstruct";
            }
            else
            {
                Action<CommandBuffer> begin = null, end = null;
                if (CloudBridgeLights.Mark != null)
                {
                    begin = cb => CloudBridgeLights.Mark(cb, CloudBridgeLights.MarkBegin);
                    end = cb => CloudBridgeLights.Mark(cb, CloudBridgeLights.MarkEnd);
                }
                renderer.Prepare(cam, frame, depth, begin, null, end);
                Stats.PlainFrames++;
                Stats.Mode = "plain";
            }
            if (!buffersOn)
            {
                renderer.Attach(cam, attachedEvent);
                buffersOn = true;
                nextBufferCheck = Time.realtimeSinceStartup + 1f;
                Stats.BufferAttaches++;
            }
            Stats.FramesDrawn++;
            Stats.NearMarchActive = true;
            Stats.Why = "drawing";
            SetShell(true);
            if (!depthSeenLogged)
            {
                depthSeenLogged = true;
                AtmospheresLog.Say("own clouds: drawing at " + attachedEvent + " on " + cam.name + ", depth target " + depthTarget.width + "x" + depthTarget.height
                                   + ", camera depth mode " + cam.depthTextureMode + ", rendering path " + cam.actualRenderingPath);
            }
        }

        private void EnsureDepthTarget(Camera cam)
        {
            int w = cam.pixelWidth, h = cam.pixelHeight;
            if (depthTarget != null && depthTarget.width == w && depthTarget.height == h) return;
            if (depthTarget != null) { depthTarget.Release(); UnityEngine.Object.Destroy(depthTarget); }
            depthTarget = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
            {
                name = "layer scene depth",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            depthTarget.Create();
        }

        private void PublishStats(float now)
        {
            if (now < nextStats) return;
            nextStats = now + Math.Max(5f, Tuning.Get(TuneStatsS, 30f));
            AtmospheresLog.Say("own clouds: " + Stats.Mode + ", " + Stats.Layers + " layers, frames " + Stats.FramesDrawn + " (plain " + Stats.PlainFrames + ", reconstruct "
                               + Stats.ReconFrames + "), map fetches " + Stats.MapFetches + " (last " + Stats.LastMapMs.ToString("0.0") + " ms, worst "
                               + Stats.WorstMapMs.ToString("0.0") + " ms), height builds " + Stats.HeightBuilds + " (last " + Stats.LastHeightMs.ToString("0.0")
                               + " ms, worst " + Stats.WorstHeightMs.ToString("0.0") + " ms, first " + Stats.FirstHeightMs.ToString("0.0") + " ms), height upload worst "
                               + Stats.WorstUploadMs.ToString("0.0") + " ms, buffer attaches " + Stats.BufferAttaches + ", history cuts " + (driver != null ? driver.CutCount : 0)
                               + (driver != null && driver.LastCutReason != null ? " (last: " + driver.LastCutReason + ")" : ""));
        }
    }
}
