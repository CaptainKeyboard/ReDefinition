using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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
    //  - motion vectors (E40 MVCLOUD): the pass writes no depth, so the host's vectors carry the sky's rotation-only motion over a cloud. As the reconstruction it draws the
    //    cloud's own vector over the host's through Hooks.RegisterMotionVectors (OnMotionVectors, DrawMotion; cloud.motionVectors): each pixel's place in the frame before on the
    //    layers' middle spheres, blended by the layers' share of the pixel (LayerMotionModel), weighted by the cloud's alpha where the cloud is in front of the scene. The same place is
    //    the history's lookup (recon.layerMotion). The pass draws into the camera target before the image effects; the upscaler sees the clouds as part
    //    of the colour. With the reconstruction (off by default) the cloud keeps its own history: the block levels of cloudResolution a full-size one, the
    //    half-size march of level 1 one of its own size behind recon.accumulate (E40 grain; default 0.2, 0 = none). The upscaler's jitter: with recon.hostJitter the march's rays
    //    follow the jittered projection the scene's depth was rendered with (each ray is tested against the depth texel rendered along it), while the history's reprojection
    //    and the motion vectors keep the unjittered camera; without it the rays are the unjittered ones.
    //  - depth: the camera's depth texture, made linear by ReDefinitionAtmospheresLayerDepth.shader, limits the march and the composite
    //    (depth-aware); the clouds write no depth. [VERIFY] that _CameraDepthTexture is readable at the chosen event under the Deferred mod.
    //  - masks: no reactive mask for DLSS. HUD-less frame generation: the pass is part of the scene colour before the HUD. Raytracing: the
    //    rays see the layers through RtOwnClouds (T18): behind rtOwnClouds (default off) this module ticks the ray-side pack of its stack; the main mod's
    //    raytracing reads it once the seam of the T18 design note lands (nothing reads it today).
    //  - draw order: after the lit opaque scene and before the transparents (CameraEvent.BeforeForwardAlpha, tuning cloud.cameraEvent). Where Scatterer's
    //    screen-space scattering stands behind the pass at that event, the pass's buffers are moved behind it before the camera renders (OnPreRender,
    //    cloud.afterScatterer, default on): the scattering writes the air of the path to the scene's depth and would give a cloud the haze of the sea behind it.
    //    Behind the scattering Scatterer's air no longer reaches the cloud; the pass's aerial perspective (T06, cloud.farHaze 2, CloudHazePlan) replaces it with the
    //    coefficients of Scatterer's atmosphere node of the body (ScattererAirReader), the same optical depth along the cloud's mean distance.
    //  - exposure: the pass is given pi times the scene's sun colour (it is radiometric, Unity's legacy lights have no 1/pi; cloud.sunUnit) and its radiance
    //    is encoded relative to the sun's display value at the final composite (CloudOutputStage: cloud.exposure, cloud.outputGamma 2.2), so the clouds
    //    sit in the display units of the Gamma-space target that holds the sky and the sea; TUFX and the auto exposure then tone-map all of it together.
    [AtmospheresModule(61)]
    internal sealed partial class OwnCloudModule : IAtmospheresModule
    {
        // The tuning keys of this module (ATMO_TUNING { key = value }, or a matrix row atmo.<key>).
        internal const string TuneCameraEvent = "cloud.cameraEvent", TuneSunGain = "cloud.sunGain", TuneSunAirWithScatterer = "cloud.sunAirWithScatterer",
            TuneMoonGain = "cloud.moonGain", TuneSunUnit = "cloud.sunUnit", TuneExposure = "cloud.exposure", TuneOutputGamma = "cloud.outputGamma",
            TuneAfterScatterer = "cloud.afterScatterer",
            // E40 B3: the white point of the output stage follows the sun at the clouds (CloudOutputStage.WhiteTint): the strength 0 to 1 (default 1, 0 = the grey white as before) and
            // the altitude the sun's air is taken at (default -1: the lower quarter of the stack's lowest layer, CloudOutputStage.NominalCloudAltitudeM; a value of 0 or more is that altitude in m).
            TuneWhitePoint = "cloud.whitePoint", TuneWhiteAltitudeM = "cloud.whiteAltitudeM";
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
            // What the main thread pays: the worst single frame of the height build (begin, pump, apply) and of the map fetch (one step), first call
            // and the forced rounds of a new stack left out. The rows above are the build's own time on the worker (height) and a fetch round's sum (maps).
            public double WorstHeightMainMs, WorstMapMainMs;
            public int BufferAttaches;
            // E40 MVCLOUD: the frames in which the cloud's motion vectors were drawn into the host's capture, and why they were not in the last frame the host asked for them in ("" while they are).
            public int MotionFrames;
            public string MotionNote = "not asked";
            // The frames in which the buffers were moved behind Scatterer's screen-space scattering before the camera rendered (cloud.afterScatterer).
            public int OrderMoves;
            // Whether the light cache is filled and read in the frame just recorded (cloudLightCache on).
            public bool LightCacheOn;
            // Whether the scene's white balance took the picture in the frame just recorded (sceneWhiteBalance, the host's hook alive).
            public bool SceneWhiteBalance;
            // The sun's tint the frame was balanced by (the scene or the clouds alone; white where nothing was balanced).
            public Color SceneTint = Color.white;
            // E40 C2: the extra lights' luminance over the sun's in the frame just recorded, and whether one was left out as too faint (light.moonMinShare); what a game start reads of why the pass costs what it does.
            public float MoonOverSun;
            public bool MoonDropped;
            // The size of the camera the pass draws into (its depth target): the render size, which the upscaler sets below the window size.
            public int RenderWidth, RenderHeight;
            public string Mode = "off", Why = "not started";
            public int Layers;
            public bool NearMarchActive;
            // Whether the cloud shadow's rig is switched on this frame (ownClouds, cloudShadows and a layer) and whether its renderer exists.
            public bool ShadowOn, ShadowReady;
        }

        internal static OwnCloudModule Current { get; private set; }

        // Whether the layer pass has its buffers on the camera this frame, i.e. draws the clouds. The storm slab (VolumePass) asks: two cloud
        // renderers must not draw the same clouds (OwnCloudPlan.SlabDraws).
        internal static bool Drawing
        {
            get
            {
                OwnCloudModule m = Current;
                return m != null && m.buffersOn;
            }
        }
        internal readonly Statistics Stats = new Statistics();

        private readonly LayerFrame frame = new LayerFrame();
        private readonly ReconSettings recon = new ReconSettings();
        private readonly HeightTuning heightTuning = new HeightTuning();
        private readonly WeatherMap coverageMap = new WeatherMap(), typeMap = new WeatherMap(), layerMap = new WeatherMap();
        private readonly StackMapMirror stackMaps = new StackMapMirror();
        // The look knobs of the maps (CloudLookTuning): per-layer type bytes and maps, and the held layer maps of a combined fetch.
        private byte[] typeScratch, altoScratch;
        // The layer maps at the mid-level sheet's height and at the fair-weather type's height (E40 L2 D1, CloudLookTuning.ApplyAlto).
        private readonly WeatherMap altoMidMap = new WeatherMap(), altoLowMap = new WeatherMap();
        // The weather deck rule's inputs (FetchFogInputs) and its per-layer results (WeatherDeckRule.Apply).
        private readonly WeatherMap humidityMap = new WeatherMap(), temperatureMap = new WeatherMap(), windMap = new WeatherMap();
        private float[] spreadScratch, fogSpreadK, fogWind;
        // E40 L3 (CloudMesoStage): the inputs of the types picked from their climate, and the stage's per-layer results.
        private byte[] climateHumidity, climateSurface;
        private readonly WeatherMap surfaceMap = new WeatherMap();
        private readonly CloudMesoInputs mesoInputs = new CloudMesoInputs();
        private readonly byte[][] mesoCover = new byte[CloudLayerDef.MaxLayers][], mesoType = new byte[CloudLayerDef.MaxLayers][];
        private readonly WeatherMap[] mesoCoverMaps = new WeatherMap[CloudLayerDef.MaxLayers];
        private readonly byte[][] deckCover = new byte[CloudLayerDef.MaxLayers][], deckType = new byte[CloudLayerDef.MaxLayers][];
        // E40 CB: the anvils drawn into a layer's maps (AnvilMap), the inputs taken at the layer's fetch (the body's radius, the shear between the anvil and the steering level), and
        // the CPU copies of the maps with them.
        private readonly AnvilInputs[] anvilInputs = new AnvilInputs[CloudLayerDef.MaxLayers];
        private readonly byte[][] anvilCover = new byte[CloudLayerDef.MaxLayers][], anvilType = new byte[CloudLayerDef.MaxLayers][];
        private readonly WeatherMap[] anvilCoverMaps = new WeatherMap[CloudLayerDef.MaxLayers], anvilTypeMaps = new WeatherMap[CloudLayerDef.MaxLayers];
        // CELLS: a layer with cfg key cellList draws its towers from the generator's list of convective cells (AnvilMap.PaintCells / ApplyCells): the cells of the last fetch, the maps painted from
        // them and the painter's buffers; the list is null where the layer's last fetch did not use it (no generator, no list yet, a probe's override of the maps).
        private readonly List<AtmoSim.Core.ConvectiveCell>[] cellLists = new List<AtmoSim.Core.ConvectiveCell>[CloudLayerDef.MaxLayers];
        private readonly bool[] cellLayerNow = new bool[CloudLayerDef.MaxLayers];
        private readonly byte[][] cellCover = new byte[CloudLayerDef.MaxLayers][], cellType = new byte[CloudLayerDef.MaxLayers][];
        private readonly WeatherMap[] cellCoverMaps = new WeatherMap[CloudLayerDef.MaxLayers];
        private readonly AnvilMap.CellScratch cellScratch = new AnvilMap.CellScratch();

        private void TakeAnvilInputs(CelestialBody b, GeneratorField gf, int l)
        {
            CloudLayerDef d = stack.LayerAt(l);
            double shear = 0.0;
            if (gf != null && d.AnvilType >= 0f)
            {
                double mid = CloudBridgeStack.MidHeightM(d);
                shear = gf.CloudDriftRadS(b.flightGlobalsIndex, mid + 0.5 * d.ThicknessM) - gf.CloudDriftRadS(b.flightGlobalsIndex, mid);
            }
            anvilInputs[l] = new AnvilInputs { Width = MapWidth, Height = MapHeight, RadiusM = b.Radius, ShearRadS = shear, UpdraftMs = gf != null ? gf.CellUpdraftMsOf(b.flightGlobalsIndex) : 0.0 };
        }

        // Draws the layer's anvils into copies of its maps; true (and the copies and their CPU maps in the arguments) where it changed them.
        private bool DrawAnvil(int l, ref byte[] cover, ref byte[] typeBytes, out WeatherMap coverCpu, out WeatherMap typeCpu)
        {
            coverCpu = typeCpu = null;
            CloudLayerDef d = stack.LayerAt(l);
            AnvilInputs inp = anvilInputs[l];
            if (AnvilMap.AnvilTypeIndex(d) < 0 || inp.Width * inp.Height != cover.Length || typeBytes == null || typeBytes.Length != cover.Length) return false;
            byte[] c = anvilCover[l] = CopyInto(anvilCover[l], cover);
            byte[] t = anvilType[l] = CopyInto(anvilType[l], typeBytes);
            if (!(cellLayerNow[l] && cellLists[l] != null ? AnvilMap.ApplyCells(d, cellLists[l], c, t, inp) : AnvilMap.Apply(d, c, t, inp))) return false;
            coverCpu = anvilCoverMaps[l] ?? (anvilCoverMaps[l] = new WeatherMap());
            typeCpu = anvilTypeMaps[l] ?? (anvilTypeMaps[l] = new WeatherMap());
            coverCpu.Resize(inp.Width, inp.Height);
            typeCpu.Resize(inp.Width, inp.Height);
            for (int i = 0; i < c.Length; i++)
            {
                coverCpu.Values[i] = c[i] / 255f;
                typeCpu.Values[i] = t[i] / 255f;
            }
            cover = c;
            typeBytes = t;
            return true;
        }
        private readonly WeatherMap[] deckCoverMaps = new WeatherMap[CloudLayerDef.MaxLayers];
        private long lookStampSeen = -1;
        private readonly byte[][] forcedBytes = new byte[CloudLayerDef.MaxLayers][];
        // E40 start 60: a layer without its own coverage map silently took the field's shared cover (every layer closed as far as the weather), which can turn a half
        // sky into overcast. Each change between the own map and the fallback is now said per body and layer, with the reason and the time (the worker's first map comes
        // a moment after the start, so a once-only line would be used up there).
        private readonly Dictionary<string, bool> sharedCoverState = new Dictionary<string, bool>();
        private readonly WeatherMap[] forcedMaps = new WeatherMap[CloudLayerDef.MaxLayers];
        private readonly WeatherMap[] layerTypeMaps = new WeatherMap[CloudLayerDef.MaxLayers], combinedMaps = new WeatherMap[CloudLayerDef.MaxLayers],
            pendingTypeMap = new WeatherMap[CloudLayerDef.MaxLayers];
        private readonly byte[][] pendingCover = new byte[CloudLayerDef.MaxLayers][], pendingType = new byte[CloudLayerDef.MaxLayers][];
        private readonly WeatherDeckRule[] pendingDeck = new WeatherDeckRule[CloudLayerDef.MaxLayers];
        private bool combineRound;
        private LayerRenderer renderer;
        private LayerReconstructDriver driver;
        // E40 MVCLOUD: the cloud's motion vectors for the upscalers and frame generation. The handler is registered with the host (Hooks.RegisterMotionVectors, through the bridge); the
        // host calls it as the scene camera culls, before or after this module's pre-cull handler, so the draw waits for both: the host's capture buffer and targets of the frame are
        // kept, and the draw is recorded into the buffer once the frame's pass is recorded (DrawMotion). lastDt is the interval the layers' drift is taken over.
        private ApiBridge motionBridge;
        private Action<CommandBuffer, RenderTexture, RenderTexture, Camera> motionHandler;
        private bool motionRegistered, motionRefused, motionCameraLogged;
        private CommandBuffer motionCommands;
        private RenderTexture motionVectors, motionDepth;
        private Camera motionCamera;
        private int motionFrame = -1, recordedFrame = -1, motionDrawnFrame = -1;
        private CloudMotionAudit motionAudit;
        private float lastDt;
        private LightCache lightCache;
        private readonly SceneWhiteBalance sceneWhiteBalance = new SceneWhiteBalance();
        private CloudShadowRenderer shadowRenderer;
        private CloudShadowRig shadowRig;
        // The pass's shape noise for the CPU readers of the stack (the hook's field: sun dimming, the weather driver): updated every frame in FillShadow.
        private readonly CloudShadowShapeNoise shadowNoise = new CloudShadowShapeNoise();
        private readonly float[] worldToMapRows = new float[9];
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
        // The order at the pass's event as the camera renders it (read in the camera's pre-render, where the buffers other mods add per frame stand):
        // looked at once a second; while a scattering buffer stands behind the pass the buffers are moved behind it every frame.
        private bool preRenderSubscribed, moveBehindScattering;
        private float nextOrderCheck;
        private readonly List<string> orderNames = new List<string>();
        private string renderOrder = "not read yet";
        // The inputs of the stack that stands, so that the once-a-second check allocates nothing when they have not changed.
        private IReadOnlyList<CloudLayerSpec> stackSpecs;
        private int stackCap = -1, stackPick = -1;
        private float nextMaps, nextHeights, nextStats, nextWind, nextStackCheck;
        // The height scale's switch and factor the body configuration was last read with (CloudHeightScale.Signature); null before the first check.
        private string heightScaleSignature;
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
        // The body's height numbers the heightTuning holds now (reference compared: the rules of a body are one object until the cfg is read again).
        private CloudHeightRules heightRules;
        // The height maps are built off the main thread; the main thread pumps it with this budget (ms) a frame and applies what it finishes.
        private readonly HeightJob heightJob = new HeightJob();
        private const double HeightPumpBudgetMs = 0.8;
        private bool heightMainSeen, heightFailedLogged;
        // A map fetch round is spread over frames: -1 none, 0 shared coverage, 1 type, 2 + l the layer l. A round for a new stack runs in one frame.
        private int fetchStep = -1;
        private bool fetchHaveShared, fetchHaveType, fetchAll;
        private double fetchRoundMs;
        private double lastUt = double.NaN;
        private CloudBridgeAir.Air air;
        // The sun's light as Unity has it outside the render, before Scatterer's modulation (read in the tick: Scatterer applies its colour in the far camera's pre-render and
        // restores it after the near camera); the sun colour of the pass never exceeds it.
        private Color sunUnmodulated;
        private bool haveSunUnmodulated;
        private int airBody = -1;
        private bool airFromScatterer;

        // The layer stack that stands (null while the pass is off): the CPU consumers read it.
        internal ICloudLayerSource Stack { get { return stack; } }
        // Diagnosis (K-T2d): the names of the layers the stack was built from ("a|b|"), and whether the group members were picked by the cold branch (the snow deck).
        internal string StackKeyNow { get { return stackKey; } }
        internal bool GroupsCold { get { return cold; } }
        internal LayerMapStore GpuMaps { get { return renderer == null ? null : renderer.Maps; } }
        internal LayerRenderer Renderer { get { return renderer; } }
        // The camera and the event the pass's buffers stand on (the K-T2d probe logs them; null / BeforeForwardAlpha before the first frame).
        internal Camera AttachedCamera { get { return attachedCamera; } }
        internal CameraEvent AttachedEvent { get { return attachedEvent; } }
        internal LayerReconstructDriver Driver { get { return driver; } }
        internal LayerFrame Frame { get { return frame; } }
        // The cloud shadow's numbers of the last frame (the perf starts' proof rows); null without the shadow rig.
        internal CloudShadowFrame ShadowFrame { get { return shadowRig != null ? shadowRig.Frame : null; } }
        // The origin of the frame number (OwnCloudPlan.FrameNumber): 0 by default; the pinned capture (T09ProbePin.cs, -t09Pin) sets it so that the step dither, the light
        // cone's rotation and, with a history cut, the rotation's block index start from a known state.
        internal int FrameOrigin;

        // The sun colour of the last frame the pass filled, as the probe logs it (E40 soft density start 59, T09ProbeSoft.cs): the colour read in the render (Scatterer's, modulated), the
        // one read in the tick (restored, the cap), the one the pass took, and what the air division used. FrameNumber 0: no frame filled yet.
        internal struct SunColourRead
        {
            internal int FrameNumber;
            internal Color Scene, Unmodulated, Pass;
            internal bool HaveUnmodulated, ScattererLoaded, AirFromScatterer, Divided;
            internal float SunAirHeightM;
            internal double SinElevation, ViewerAltitudeM;
        }

        internal SunColourRead SunRead { get { return sunRead; } }
        private SunColourRead sunRead;
        // The buffers at the pass's event in the order the camera renders them, as last read in the pre-render (the K-T2d probe logs it), and
        // whether the pass is being moved behind Scatterer's screen-space scattering.
        internal string RenderOrder { get { return renderOrder; } }
        internal bool MovesBehindScattering { get { return moveBehindScattering; } }

        public void Init()
        {
            Current = this;
            // LV1: a new lay of the light volume (cloud.lightVolume) is logged.
            LightVolume.Say = line => AtmospheresLog.Say("own clouds: " + line);
            // The GPU profiler's sub-markers are wired only while the profiler runs, so no label is put together without it.
            if (HostSeams.GpuProfileOn())
            {
                LayerReconstructor.SubMark = CloudBridgeLights.Mark;
                CloudShadowRenderer.SubMark = CloudBridgeLights.Mark;
            }
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
            lastDt = dt;
            bool flight = HighLogic.LoadedSceneIsFlight && FlightCamera.fetch != null && FlightGlobals.currentMainBody != null;
            CelestialBody b = flight ? FlightGlobals.currentMainBody : null;
            BodyClimate c = b != null ? Bodies.Get(b) : null;
            CloudSourceKind source = c != null ? EveRenderCompat.EffectiveSource(c.Clouds.Source ?? AtmospheresSettings.CloudSource) : CloudSourceKind.None;
            int specs = c != null ? c.Clouds.Layers.Count : 0;
            bool want = OwnCloudPlan.Wanted(AtmospheresSettings.Enabled, flight, b != null && b.atmosphere, c != null && c.Status.WeatherRuns,
                                            OwnCloudOptions.Active(source), specs, MapView.MapIsEnabled);
            // (the settings half of this gate is CloudGates.OwnPassSwitches; the audit of K-T2 evaluates it with a start's effective settings)
            if (!want)
            {
                Stats.Why = !AtmospheresSettings.Enabled ? "mod off" : !flight ? "no flight scene" : c == null ? "no body config" :
                    !OwnCloudOptions.Active(source) ? "ownClouds off or the source is not Ours" : specs == 0 ? "no layers for the body (" + c.Clouds.LayerSet + ")" : "map view or no weather";
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
            long mapStart = Stopwatch.GetTimestamp();
            FetchMaps(b, field, ut, now, dt);
            ReportMapTiming((Stopwatch.GetTimestamp() - mapStart) * 1000.0 / Stopwatch.Frequency, now);
            BuildHeights(c, b, field, ut, now);
            // The upload of a new stack and the height slices (one a frame) are timed: the worst frame is a hitch of the pass (the first call, with Mono's
            // JIT in it, is left out of the worst).
            long uploadStart = Stopwatch.GetTimestamp();
            renderer.Upload(stack);
            renderer.StepHeights();
            double uploadMs = (Stopwatch.GetTimestamp() - uploadStart) * 1000.0 / Stopwatch.Frequency;
            if (uploadSeen) Stats.WorstUploadMs = Math.Max(Stats.WorstUploadMs, uploadMs);
            uploadSeen = true;
            // The ray-side pack of the layers (T18, behind rtOwnClouds, default off): baked a few texels a frame, never in the pass's own time.
            RtOwnClouds.Tick(stack, RtOwnCloudsPlan.Wanted(HostGate.Open, AtmospheresSettings.RtOwnClouds, CloudShadowSwitches.WhoCasts(source, false), mapsPrimed,
                                                           stack.LayerCount));
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
            // cloud.motionScale (CloudMotion, default 1: unchanged) scales the clouds' drift for the cloud viewer; 0 holds them still.
            renderer.Noise.Advance(dt * CloudMotion.Scale);
            if (!mapsPrimed)
            {
                Stats.Why = "waiting for the first maps";
                Hide(false);
                return;
            }
            Light sunLight = Sun.Instance != null ? Sun.Instance.sunLight : null;
            haveSunUnmodulated = sunLight != null;
            if (haveSunUnmodulated) sunUnmodulated = sunLight.color * sunLight.intensity;
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
            if (!preRenderSubscribed)
            {
                Camera.onPreRender += OnPreRender;
                preRenderSubscribed = true;
            }
        }

        // After the camera's cull and before it renders: the buffers Scatterer adds while the camera culls stand on the camera now. Once a second the
        // order at the pass's event is read (it allocates); while Scatterer's screen-space scattering stands behind the pass, the pass's two buffers
        // are taken off and added again every frame, which puts them at the end of the event's list (the depth first, the pass after it).
        private void OnPreRender(Camera cam)
        {
            if (broken || !buffersOn || renderer == null || depthCommands == null || !ReferenceEquals(cam, attachedCamera)) return;
            try
            {
                float now = Time.realtimeSinceStartup;
                bool look = now >= nextOrderCheck;
                if (look)
                {
                    nextOrderCheck = now + 1f;
                    ReadOrder(cam);
                    moveBehindScattering = OwnCloudPlan.MoveBehindScattering(Tuning.Flag(TuneAfterScatterer, true), orderNames, renderer.Commands.name);
                }
                if (!moveBehindScattering) return;
                cam.RemoveCommandBuffer(attachedEvent, depthCommands);
                cam.AddCommandBuffer(attachedEvent, depthCommands);
                renderer.Attach(cam, attachedEvent);
                Stats.OrderMoves++;
                if (look) ReadOrder(cam);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private void ReadOrder(Camera cam)
        {
            orderNames.Clear();
            foreach (CommandBuffer cb in cam.GetCommandBuffers(attachedEvent)) orderNames.Add(cb.name);
            renderOrder = attachedEvent + ": " + (orderNames.Count == 0 ? "none" : string.Join(", ", orderNames.ToArray()));
        }

        private void OnPreCull(Camera cam)
        {
            if (broken || pendingCamera == null || !ReferenceEquals(cam, pendingCamera) || renderer == null || stack == null) return;
            long cpuStart = ModuleCpuTiming.Begin();
            try
            {
                FillFrame(cam, pendingBody, pendingClimate, pendingField, pendingUt, pendingNow);
                ModuleCpuTiming.End("cloudPass.fill", cpuStart);
                long cpuFilled = ModuleCpuTiming.Begin();
                FillShadow(pendingBody, frame);
                Record(cam, pendingBody);
                if (FrameOrigin != 0) PinLogFrame(cam);
                recordedFrame = Time.frameCount;
                if (motionFrame == recordedFrame) DrawMotion();
                PublishStats(pendingNow);
                ModuleCpuTiming.End("cloudPass.shadowRecordMotion", cpuFilled);
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
            r.SpreadReach = true;
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
            s.Noise = renderer.Noise;
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
            // The CPU readers of the stack (the hook's field) take the pass's shape noise whether or not the shadow is drawn; the rotation is the frame's.
            for (int r = 0; r < 3; r++)
                for (int k = 0; k < 3; k++) worldToMapRows[r * 3 + k] = f.WorldToMap[r, k];
            shadowNoise.Update(renderer.Noise, stack, worldToMapRows, OwnCloudOptions.ShadowShapeNoise);
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
            // OPT2: the shadow leaps over the layer pass's empty reach where the pass does (a body with a surface, the maps built for this frame's numbers).
            s.EmptyReachOn = f.EmptyReach && f.HasSurface;
            s.EmptyReach = renderer.Reach.Texture(f.EmptyReach);
            OwnCloudOptions.Apply(s, OwnCloudOptions.LiveSettings);
            shadowRenderer.Upload(stack, renderer.Maps);
        }

        private void EnsureStack(BodyClimate c, CelestialBody b, IWeatherField field, double ut, float now)
        {
            // Checked once a second (a build allocates): the temperature at the camera picks the member of a group, the layer cap limits the count.
            if (stack != null && now < nextStackCheck) return;
            nextStackCheck = now + 1f;
            // The height scale (CloudHeightScale): a change of its switch or factor re-reads the body configuration; the next check builds the stack from the new layers.
            string heightScale = CloudHeightScale.Signature;
            if (heightScaleSignature != null && heightScale != heightScaleSignature)
            {
                var provider = Bodies.Provider as BodyConfigProvider;
                if (provider != null) provider.Reset();
                AtmospheresLog.Say("own clouds: the height scale changed (" + heightScaleSignature + " -> " + heightScale + "); the body configuration is read again");
            }
            heightScaleSignature = heightScale;
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
            EnsureRoundIdle();
            fetchStep = -1;
            heightJob.Cancel();
            renderer.Maps.Ensure(MapWidth, MapHeight);
            renderer.Noise.Switches.SphereScale = c.Clouds.SphereScale > 0f ? c.Clouds.SphereScale : 1f;
            OwnCloudOpticalDepth.Bind(new CloudShadowField(stack, stack.Tables()) { StepPolicy = OwnCloudOptions.ShadowStepPolicy, ShapeNoise = shadowNoise });
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
            heightJob.Cancel();
            EnsureRoundIdle();
            fetchStep = -1;
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
            // The scene's white balance follows the pass: with no cloud frame there is no tint of this frame to balance by.
            sceneWhiteBalance.Clear();
            Stats.SceneWhiteBalance = false;
            pendingCamera = null;
            SetShell(false);
            Detach();
            if (dropStack) RtOwnClouds.Release();
            if (dropStack && stack != null) DropStack("the pass is off: " + Stats.Why);
        }

        private void Teardown()
        {
            if (preCullSubscribed)
            {
                Camera.onPreCull -= OnPreCull;
                preCullSubscribed = false;
            }
            if (preRenderSubscribed)
            {
                Camera.onPreRender -= OnPreRender;
                preRenderSubscribed = false;
            }
            heightJob.Cancel();
            Hide();
            if (motionBridge != null) motionBridge.UnregisterAll();
            motionRegistered = false;
            motionCommands = null;
            motionVectors = motionDepth = null;
            motionCamera = null;
            DropStack("shutdown");
            if (shadowRig != null) shadowRig.Stop();
            shadowRig = null;
            if (shadowRenderer != null) shadowRenderer.Dispose();
            shadowRenderer = null;
            if (driver != null) driver.Dispose();
            driver = null;
            frame.Light.Cache = null;
            if (lightCache != null) lightCache.Dispose();
            sceneWhiteBalance.Dispose();
            lightCache = null;
            if (renderer != null) renderer.Dispose();
            renderer = null;
            if (depthCommands != null) depthCommands.Release();
            depthCommands = null;
            if (depthTarget != null) { depthTarget.Release(); UnityEngine.Object.Destroy(depthTarget); }
            depthTarget = null;
            PinSceneRelease();
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
            // A round (the shared coverage, the type, then each layer's coverage) takes one step a frame, each a copy of 0.5 MB and a packing to
            // bytes, so that no frame pays for all of them; a round for a new stack, which snaps the maps, runs in the frame it starts.
            // A change of a look knob that acts on the maps (type per layer, cover combination) starts a round of its own, snapping the maps.
            long lookStamp = CloudLookTuning.Stamp;
            if (lookStamp != lookStampSeen)
            {
                if (lookStampSeen >= 0) { forceMaps = true; snapMaps = true; }
                lookStampSeen = lookStamp;
            }
            if (roundOnWorker && Volatile.Read(ref roundFinished) == 1) CompleteWorkerRound();
            if (!roundOnWorker && fetchStep < 0 && OwnCloudPlan.Due(now, nextMaps, OwnCloudPlan.MapIntervalS, forceMaps))
            {
                nextMaps = now + OwnCloudPlan.MapIntervalS;
                roundAfterSwap = WeatherRebuild.Phase == RebuildPhase.Fetching;
                fetchStep = 0;
                fetchHaveShared = fetchHaveType = false;
                fetchAll = forceMaps || !mapsPrimed;
                fetchRoundMs = 0.0;
                // cloud.mapWorker (default 1): a steady-state round (no snap, maps primed) runs on a worker thread; the main thread starts it and takes its result (the maps slew on the GPU).
                if (!fetchAll && !snapMaps && field.ThreadSafe && Tuning.Get(TuneMapWorker, 1f) > 0.5f) StartWorkerRound(b, field, ut);
                forceMaps = false;
            }
            if (fetchStep >= 0 && !roundOnWorker)
            {
                long stepStart = Stopwatch.GetTimestamp();
                do
                {
                    FetchStep(b, field, ut);
                } while (fetchStep >= 0 && fetchAll);
                double stepMs = (Stopwatch.GetTimestamp() - stepStart) * 1000.0 / Stopwatch.Frequency;
                fetchRoundMs += stepMs;
                if (!fetchAll && Stats.MapFetches > 0) Stats.WorstMapMainMs = Math.Max(Stats.WorstMapMainMs, stepMs);
                if (fetchStep < 0)
                {
                    Stats.LastMapMs = fetchRoundMs;
                    if (Stats.MapFetches <= 1 && Stats.FirstMapMs == 0.0) Stats.FirstMapMs = Stats.LastMapMs;
                    else Stats.WorstMapMs = Math.Max(Stats.WorstMapMs, Stats.LastMapMs);
                }
            }
            // The store's clock runs every frame (the shader's slew is measured against it, LayerMapStore.GpuSlew.cs).
            int open = renderer.Maps.Step(dt, MapSlew.DefaultRate, stepMaps && snapMaps);
            if (stepMaps)
            {
                snapMaps = false;
                mapsPrimed = true;
                if (open == 0) stepMaps = false;
            }
        }

        internal const string TuneMapWorker = "cloud.mapWorker";

        // The main-thread time of the map pipeline (FetchMaps: the round's steps, the store's step and its uploads), one line a second, only with -redefinitionGpuProfile.
        private static bool? mapTimingOn;
        private static bool MapTimingOn { get { return (mapTimingOn ?? (mapTimingOn = CommandLine.Has("-redefinitionGpuProfile"))).Value; } }
        private int mapTimeFrames;
        private double mapTimeSum, mapTimeMax;
        private float mapTimeSince = -1f;

        private void ReportMapTiming(double ms, float now)
        {
            if (!MapTimingOn) return;
            if (mapTimeSince < 0f) mapTimeSince = now;
            mapTimeFrames++;
            mapTimeSum += ms;
            if (ms > mapTimeMax) mapTimeMax = ms;
            if (now - mapTimeSince < 1f) return;
            AtmospheresLog.Say("own clouds: map pipeline main thread (OPT1), last second: " + mapTimeFrames + " frames, mean " + (mapTimeSum / mapTimeFrames).ToString("0.000")
                               + " ms, max " + mapTimeMax.ToString("0.000") + " ms" + (roundOnWorker ? ", round on the worker" : "")
                               + ", pending uploads " + renderer.Maps.SlewPending + ", rounds " + Stats.MapFetches);
            mapTimeFrames = 0;
            mapTimeSum = mapTimeMax = 0.0;
            mapTimeSince = now;
        }

        // --- The map round on a worker thread ------------------------------------------------------------------------------------------

        // A steady-state round (FetchStep from the shared coverage to the last layer's hand-over) runs on one worker thread, started by the main thread when a round is due; every call
        // in it is the thread-safe field's (IWeatherField.ThreadSafe) or pure byte work on buffers only the round touches while it runs (the main thread reads none of them until it
        // has taken the round: roundFinished). The round hands each layer's goal to the store (LayerMapStore.SetTargetBytes, GpuSlew: a copy and a queue entry, any thread) and copies
        // the CPU mirror's coverage and type map into back buffers (StackMapMirror.CopyBack); the main thread then swaps the stack's maps for them (StackMapMirror.AttachBack: no reader
        // ever sees a map being written) and ends the round. The stack, the
        // store's size and the round's buffers do not change under a round: whatever would change them waits for it first (EnsureRoundIdle).
        private bool roundOnWorker;
        private int roundFinished;
        private bool roundDelivered;
        private Exception roundError;
        private double roundWorkerMs;
        private CelestialBody roundBody;
        private IWeatherField roundField;
        private double roundUt;
        private Thread roundThread;
        private readonly object roundGate = new object();
        private bool roundRequested;
        private readonly WeatherMap[] attachType = new WeatherMap[CloudLayerDef.MaxLayers];
        private readonly ManualResetEvent roundIdle = new ManualResetEvent(true);

        private void StartWorkerRound(CelestialBody b, IWeatherField field, double ut)
        {
            stackMaps.OwnTypeMaps(stack);
            roundOnWorker = true;
            Volatile.Write(ref roundFinished, 0);
            roundDelivered = false;
            roundError = null;
            roundBody = b;
            roundField = field;
            roundUt = ut;
            Array.Clear(attachType, 0, attachType.Length);
            roundIdle.Reset();
            lock (roundGate)
            {
                roundRequested = true;
                if (roundThread == null)
                {
                    roundThread = new Thread(RoundWorker) { IsBackground = true, Name = "ReDefinition cloud map round" };
                    roundThread.Start();
                }
                Monitor.PulseAll(roundGate);
            }
        }

        private void RoundWorker()
        {
            while (true)
            {
                lock (roundGate)
                {
                    while (!roundRequested) Monitor.Wait(roundGate);
                    roundRequested = false;
                }
                long start = Stopwatch.GetTimestamp();
                try
                {
                    do
                    {
                        FetchStep(roundBody, roundField, roundUt);
                    } while (fetchStep >= 0);
                }
                catch (Exception e)
                {
                    roundError = e;
                    fetchStep = -1;
                }
                roundWorkerMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                Volatile.Write(ref roundFinished, 1);
                roundIdle.Set();
            }
        }

        // Blocks until a round in flight has ended and drops its result (a stack change or a mode change is about to replace what it was working on).
        private void EnsureRoundIdle()
        {
            if (!roundOnWorker) return;
            roundIdle.WaitOne();
            roundOnWorker = false;
            roundDelivered = false;
            fetchStep = -1;
        }

        // Main thread, the frame after the worker finished: attaches the round's type maps to the stack and ends the round as the main-thread path does.
        private void CompleteWorkerRound()
        {
            roundOnWorker = false;
            fetchRoundMs = roundWorkerMs;
            Stats.LastMapMs = roundWorkerMs;
            if (Stats.MapFetches <= 1 && Stats.FirstMapMs == 0.0) Stats.FirstMapMs = Stats.LastMapMs;
            else Stats.WorstMapMs = Math.Max(Stats.WorstMapMs, Stats.LastMapMs);
            if (roundError != null)
            {
                AtmospheresLog.Say("own clouds: a map round on the worker failed: " + roundError.Message);
                fetchStep = -1;
                return;
            }
            if (!roundDelivered) return;
            for (int l = 0; l < stack.LayerCount; l++)
                if (attachType[l] != null) stackMaps.AttachBack(stack, l);
            stepMaps = true;
            Stats.MapFetches++;
            if (roundAfterSwap) WeatherRebuild.MapsFetched();
        }

        // Where a layer's goal goes at the end of its step: the store and the CPU mirror. On the worker the mirror's coverage is copied there and the type map attached by the main
        // thread afterwards.
        private void PublishLayer(int l, byte[] cover, byte[] typeBytes, WeatherMap coverSource, WeatherMap typeForCpu)
        {
            renderer.Maps.SetTargetBytes(l, cover, typeBytes);
            if (roundOnWorker)
            {
                stackMaps.CopyBack(l, coverSource, typeForCpu);
                attachType[l] = typeForCpu;
            }
            else
            {
                stackMaps.Set(stack, l, coverSource, typeForCpu);
            }
        }

        // The end of a round that delivered goals: the main-thread path finishes it here, the worker's round is finished by CompleteWorkerRound.
        private void EndRound()
        {
            fetchStep = -1;
            // The map sky: which sky the round that ends here published (the pinned capture's proof, OwnCloudModule.MapSky.cs).
            MapSkyRoundEnded();
            if (roundOnWorker)
            {
                roundDelivered = true;
                return;
            }
            stepMaps = true;
            Stats.MapFetches++;
            if (roundAfterSwap) WeatherRebuild.MapsFetched();
        }

        // One step of a fetch round (see FetchMaps). The packing to bytes happens once per map; the store copies the bytes into the layers.
        private void FetchStep(CelestialBody b, IWeatherField field, double ut)
        {
            int step = fetchStep++;
            if (step == 0)
            {
                fetchHaveShared = field.GetMap(b.flightGlobalsIndex, WeatherMapKind.Coverage, ut, coverageMap) && coverageMap.Width == MapWidth && coverageMap.Height == MapHeight;
                if (fetchHaveShared) coverageMap.PackBytes(0f, 1f);
                return;
            }
            if (step == 1)
            {
                fetchHaveType = field.GetMap(b.flightGlobalsIndex, WeatherMapKind.Precipitation, ut, typeMap) && typeMap.Width == MapWidth && typeMap.Height == MapHeight;
                if (fetchHaveType) typeMap.PackBytes(0f, 1f);
                if (!(fetchHaveShared && fetchHaveType)) fetchStep = -1;
                return;
            }
            if (step == 2)
            {
                FetchFogInputs(b, field, ut);
                // The map sky: the package B maps and the layers' parts of this round (OwnCloudModule.MapSky.cs).
                FetchMapSky(b, field, ut);
                return;
            }
            int l = step - 3;
            if (l >= stack.LayerCount)
            {
                CombineSlice(l - stack.LayerCount);
                return;
            }
            // Whether this round combines the layer maps (decided once, at its first layer: the key may change between the frames of a round).
            if (l == 0) combineRound = CloudLookTuning.CoverCombine > 0f && fetchHaveShared && !mapSkyRound;
            GeneratorField gf = GeneratorModule.Current;
            TakeAnvilInputs(b, gf, l);
            byte[] cover = coverageMap.Bytes;
            WeatherMap coverSource = coverageMap;
            bool layerFetched = gf != null && gf.GetLayerCoverage(b.flightGlobalsIndex, ut, CloudBridgeStack.MidHeightM(stack.LayerAt(l)), layerMap);
            bool ownMap = layerFetched && layerMap.Width == MapWidth && layerMap.Height == MapHeight;
            if (ownMap)
            {
                layerMap.PackBytes(0f, 1f);
                cover = layerMap.Bytes;
                coverSource = layerMap;
            }
            string sharedKey = b.bodyName + "/" + stack.LayerAt(l).Name;
            bool wasShared;
            if (!sharedCoverState.TryGetValue(sharedKey, out wasShared) ? !ownMap : wasShared == ownMap)
            {
                string why = ownMap ? "its own map again" : gf == null ? "the field's shared cover: no generator field"
                    : !layerFetched ? "the field's shared cover: the generator returned no layer coverage"
                    : "the field's shared cover: the layer map is " + layerMap.Width + "x" + layerMap.Height + ", not " + MapWidth + "x" + MapHeight;
                AtmospheresLog.Say("own clouds: layer " + stack.LayerAt(l).Name + " of " + b.bodyName + " uses " + why + " (ut " + ut.ToString("0") + ")");
            }
            sharedCoverState[sharedKey] = !ownMap;
            // The look knobs (CloudLookTuning): a layer's own type value from the precipitation (cloud.typeFair.<name>, cloud.typeOverride.<name>), and the layer maps scaled so
            // that their union meets the field's cover (cloud.coverCombine). All at their defaults: the shared type bytes and the maps as fetched, as before.
            string layerName = stack.LayerAt(l).Name;
            // cloud.mapOverride.<name> (default -1: the field's map): a constant map value everywhere, the probe's way of telling a map that varies over the view from the pass.
            float forcedCover = CloudLookTuning.MapOverrideOf(layerName);
            if (forcedCover >= 0f)
            {
                byte forcedByte = (byte)Math.Min(255.0, Math.Max(0.0, Math.Round(forcedCover * 255.0, MidpointRounding.ToEven)));
                byte[] filled = forcedBytes[l] ?? (forcedBytes[l] = new byte[cover.Length]);
                if (filled.Length != cover.Length) forcedBytes[l] = filled = new byte[cover.Length];
                WeatherMap forcedMap = forcedMaps[l] ?? (forcedMaps[l] = new WeatherMap());
                forcedMap.Resize(coverSource.Width, coverSource.Height);
                // cloud.cellKm above 0 (E40 SOLO): the forced value is the strength of ONE cell at cloud.cellLat / cloud.cellLon instead of a constant (CloudSoloCell).
                if (CloudLookTuning.CellKm > 0f && filled.Length == forcedMap.Width * forcedMap.Height)
                    CloudSoloCell.Fill(filled, forcedMap.Values, forcedMap.Width, forcedMap.Height, b.Radius, CloudLookTuning.CellLat, CloudLookTuning.CellLon, CloudLookTuning.CellKm, forcedCover);
                else
                {
                    for (int i = 0; i < filled.Length; i++) filled[i] = forcedByte;
                    for (int i = 0; i < forcedMap.Values.Length; i++) forcedMap.Values[i] = forcedByte / 255f;
                }
                cover = filled;
                coverSource = forcedMap;
            }
            // The weather deck rule (WeatherDeckRule): the deck draws only where precipitation falls or the air is fog air; the coverage and the type bytes are the rule's, and
            // the CPU copy of the maps (StackMapMirror, the sun dimming, the cloud shadow, the oracle) gets the same ones.
            float fairType = CloudLookTuning.FairType(CloudLookTuning.TypeFairOf(layerName), stack.LayerAt(l).FairType);
            byte[] typeBytes;
            bool haveAltoMaps = false;
            // CELLS: a layer with the cell list draws its towers from the generator's cells and not from the point-sampled layer map: no deck rule, no meso stage (not under a probe's override
            // of the maps or the types, and only once the worker has made a list).
            CloudLayerDef cellDef = stack.LayerAt(l);
            // The map sky: the tall layer draws its towers from the cell list (the map holds a cell's tower in the single texel at its centre; the list paints each cell as one tower).
            bool mapSkyCells = mapSkyRound && WeatherMapSky.RoleOf(cellDef) == WeatherMapSky.Role.Tall;
            bool cellLayer = (cellDef.CellList || mapSkyCells) && AnvilMap.AnvilTypeIndex(cellDef) >= 0 && gf != null && forcedCover < 0f && CloudLookTuning.TypeOverrideOf(layerName) < 0f;
            if (cellLayer)
            {
                double cellsUt;
                List<AtmoSim.Core.ConvectiveCell> list = cellLists[l] ?? (cellLists[l] = new List<AtmoSim.Core.ConvectiveCell>(2048));
                cellLayer = gf.GetCells(b.flightGlobalsIndex, ut, list, out cellsUt);
            }
            cellLayerNow[l] = cellLayer;
            WeatherDeckRule deck = cellLayer ? null : CloudLookTuning.DeckRuleOf(layerName, stack.LayerAt(l).Deck);
            byte[] rawCover = cover;
            if (cellLayer)
            {
                int n = cover.Length;
                byte[] paintedCover = cellCover[l] ?? (cellCover[l] = new byte[n]);
                byte[] paintedType = cellType[l] ?? (cellType[l] = new byte[n]);
                if (paintedCover.Length != n) cellCover[l] = paintedCover = new byte[n];
                if (paintedType.Length != n) cellType[l] = paintedType = new byte[n];
                AnvilMap.PaintCells(cellDef, cellLists[l], anvilInputs[l], cellScratch, paintedCover, paintedType);
                WeatherMap paintedMap = cellCoverMaps[l] ?? (cellCoverMaps[l] = new WeatherMap());
                paintedMap.Resize(coverSource.Width, coverSource.Height);
                for (int i = 0; i < n; i++) paintedMap.Values[i] = paintedCover[i] / 255f;
                cover = paintedCover;
                coverSource = paintedMap;
                typeBytes = paintedType;
            }
            else if (deck != null)
            {
                int n = cover.Length;
                byte[] ruleCover = deckCover[l] ?? (deckCover[l] = new byte[n]);
                byte[] ruleType = deckType[l] ?? (deckType[l] = new byte[n]);
                if (ruleCover.Length != n) deckCover[l] = ruleCover = new byte[n];
                if (ruleType.Length != n) deckType[l] = ruleType = new byte[n];
                deck.Apply(cover, typeMap.Values, fogSpreadK, fogWind, ruleCover, ruleType, CloudLookTuning.DeckPrecipOf(layerName), CloudLookTuning.DeckFogOf(layerName));
                // E40 CB: a deck layer whose table goes on past its rain types (the cumulonimbus layer's anvil sheet) ends its precipitation axis at rainType, as the other layers do.
                float deckRain = stack.LayerAt(l).RainType;
                if (deckRain < 1f)
                    for (int i = 0; i < n; i++) ruleType[i] = (byte)Math.Round(ruleType[i] * deckRain);
                float typeOverride = CloudLookTuning.TypeOverrideOf(layerName);
                if (typeOverride >= 0f)
                {
                    byte forced = (byte)Math.Min(255.0, Math.Round(typeOverride * 255.0, MidpointRounding.ToEven));
                    for (int i = 0; i < n; i++) ruleType[i] = forced;
                }
                WeatherMap ruleCoverMap = deckCoverMaps[l] ?? (deckCoverMaps[l] = new WeatherMap());
                ruleCoverMap.Resize(coverSource.Width, coverSource.Height);
                for (int i = 0; i < n; i++) ruleCoverMap.Values[i] = ruleCover[i] / 255f;
                cover = ruleCover;
                coverSource = ruleCoverMap;
                typeBytes = ruleType;
            }
            else
            {
                // The type at no precipitation: the tuning key where it is set, else the layer's own (cfg key fairType; -1: the precipitation level itself).
                typeBytes = CloudLookTuning.TypeBytes(typeMap.Bytes, fairType, CloudLookTuning.TypeOverrideOf(layerName), ref typeScratch, stack.LayerAt(l).RainType);
                // The mid-level sheet's rule (cfg altoType): where the generator makes more cloud at the sheet's height than at the fair-weather type's height, the type map shows the sheet.
                CloudLayerDef layerDef = stack.LayerAt(l);
                if (layerDef.AltoType >= 0f && gf != null && CloudLookTuning.TypeOverrideOf(layerName) < 0f
                    && gf.GetLayerCoverage(b.flightGlobalsIndex, ut, layerDef.TypeMidHeightM(layerDef.AltoType), altoMidMap)
                    && gf.GetLayerCoverage(b.flightGlobalsIndex, ut, layerDef.TypeMidHeightM(fairType >= 0f ? fairType : 0f), altoLowMap)
                    && altoMidMap.Width == MapWidth && altoMidMap.Height == MapHeight && altoLowMap.Width == MapWidth && altoLowMap.Height == MapHeight)
                {
                    altoMidMap.PackBytes(0f, 1f);
                    altoLowMap.PackBytes(0f, 1f);
                    haveAltoMaps = true;
                    // A layer that picks its types from their climate takes the sheet as a share of its own (CloudMesoStage); the others blend the type value towards it.
                    if (!layerDef.HasClimate) typeBytes = CloudLookTuning.ApplyAlto(typeBytes, typeMap.Bytes, altoMidMap.Bytes, altoLowMap.Bytes, layerDef.AltoType, layerDef.AltoFrom, layerDef.AltoFull, ref altoScratch);
                }
            }
            // The map sky: a layer the weather map drives takes its cover and type from the round's parts; no meso stage, no combination (OwnCloudModule.MapSky.cs).
            bool mapSkyLayer = forcedCover < 0f && CloudLookTuning.TypeOverrideOf(layerName) < 0f
                               && MapSkyLayer(l, stack.LayerAt(l), deck ?? stack.LayerAt(l).Deck, rawCover, typeBytes, ref cover, ref typeBytes, ref coverSource);
            if (mapSkyLayer) cellLayerNow[l] = false;
            // E40 L3: the meso-scale cover pattern and the types from their climate, from the layer's own cfg data (not under the probe's overrides of the maps and types).
            CloudLayerDef mesoDef = stack.LayerAt(l);
            if (!mapSkyLayer && !cellLayer && forcedCover < 0f && CloudLookTuning.TypeOverrideOf(layerName) < 0f && ((mesoDef.Meso != null && mesoDef.Meso.On) || (mesoDef.HasClimate && deck == null)) && gf != null)
            {
                CloudMesoInputs mi = mesoInputs;
                mi.Width = MapWidth;
                mi.Height = MapHeight;
                mi.Precip = typeMap.Bytes;
                mi.Humidity = climateHumidity;
                mi.Surface = climateSurface;
                mi.AltoMid = haveAltoMaps ? altoMidMap.Bytes : null;
                mi.AltoLow = haveAltoMaps ? altoLowMap.Bytes : null;
                mi.Ut = ut;
                mi.DriftRadS = gf.CloudDriftRadS(b.flightGlobalsIndex, CloudBridgeStack.MidHeightM(mesoDef));
                mi.Seed = gf.SeedOf(b.flightGlobalsIndex);
                // The body's own scales (radius, troposphere depth, circulation-cell edges); without a generator state the radius and the default share of the atmosphere's depth.
                if (!gf.CloudScalesOf(b.flightGlobalsIndex, mi))
                {
                    mi.RadiusKm = b.Radius * 0.001;
                    mi.DepthKm = CloudMesoInputs.DefaultTropopauseShare * b.atmosphereDepth * 0.001;
                    mi.Edge1Rad = mi.Edge2Rad = double.NaN;
                    mi.SystemKm = 0.0;
                }
                // The height scale (CloudHeightScale): the meso depth keys follow the troposphere the layers were read against.
                BodyClimate scaleClimate = Bodies.Get(b);
                if (scaleClimate != null && scaleClimate.Clouds.ScaledTroposphereM > 0) mi.DepthKm = scaleClimate.Clouds.ScaledTroposphereM * 0.001;
                byte[] mc = mesoCover[l], mt = mesoType[l];
                CloudMesoStage.Apply(mesoDef, mi, cover, typeBytes, ref mc, ref mt);
                mesoCover[l] = mc;
                mesoType[l] = mt;
                if (!ReferenceEquals(mc, cover))
                {
                    WeatherMap mm = mesoCoverMaps[l] ?? (mesoCoverMaps[l] = new WeatherMap());
                    mm.Resize(coverSource.Width, coverSource.Height);
                    for (int i = 0; i < mc.Length; i++) mm.Values[i] = mc[i] / 255f;
                    cover = mc;
                    coverSource = mm;
                }
                typeBytes = mt;
            }
            WeatherMap typeForCpu = typeMap;
            if (!ReferenceEquals(typeBytes, typeMap.Bytes))
            {
                typeForCpu = layerTypeMaps[l] ?? (layerTypeMaps[l] = new WeatherMap());
                typeForCpu.Resize(typeMap.Width, typeMap.Height);
                for (int i = 0; i < typeBytes.Length; i++) typeForCpu.Values[i] = typeBytes[i] / 255f;
            }
            if (combineRound)
            {
                // Held until every layer has been fetched: the union needs all of them. The combination itself follows in CombineSlices steps of its own (CombineSlice).
                pendingCover[l] = CopyInto(pendingCover[l], cover);
                pendingType[l] = CopyInto(pendingType[l], typeBytes);
                pendingTypeMap[l] = typeForCpu;
                pendingDeck[l] = deck;
                return;
            }
            else
            {
                WeatherMap anvilCoverCpu, anvilTypeCpu;
                if (DrawAnvil(l, ref cover, ref typeBytes, out anvilCoverCpu, out anvilTypeCpu)) { coverSource = anvilCoverCpu; typeForCpu = anvilTypeCpu; }
                PublishLayer(l, cover, typeBytes, coverSource, typeForCpu);
            }
            if (l + 1 < stack.LayerCount) return;
            EndRound();
        }

        // The slices a round's combination of the layer maps to the field's cover is cut into, a slice a frame (the whole map is 100 ms of CPU).
        private const int CombineSlices = 8;

        private static byte[] CopyInto(byte[] spare, byte[] source)
        {
            if (spare == null || spare.Length != source.Length) spare = new byte[source.Length];
            Buffer.BlockCopy(source, 0, spare, 0, source.Length);
            return spare;
        }

        // One slice of the combination (E40 F1, CloudLookTuning.MeetTheField) of the held layer maps; the last one stores them.
        private void CombineSlice(int slice)
        {
            int n = coverageMap.Bytes.Length;
            int from = (int)((long)n * slice / CombineSlices), to = (int)((long)n * (slice + 1) / CombineSlices);
            CloudLookTuning.MeetTheFieldRange(pendingCover, stack.LayerCount, coverageMap.Bytes, CloudLookTuning.CoverCombine, pendingDeck, typeMap.Values, from, to);
            if (slice + 1 < CombineSlices) return;
            for (int k = 0; k < stack.LayerCount; k++)
            {
                byte[] kc = pendingCover[k], kt = pendingType[k];
                WeatherMap anvilCoverCpu, anvilTypeCpu;
                bool anvil = DrawAnvil(k, ref kc, ref kt, out anvilCoverCpu, out anvilTypeCpu);
                WeatherMap combined = combinedMaps[k] ?? (combinedMaps[k] = new WeatherMap());
                combined.Resize(coverageMap.Width, coverageMap.Height);
                for (int i = 0; i < kc.Length; i++) combined.Values[i] = kc[i] / 255f;
                PublishLayer(k, kc, kt, combined, anvil ? anvilTypeCpu : pendingTypeMap[k]);
            }
            EndRound();
        }

        // Whether the round in flight began after the generator's states were swapped in (a rebuild waits for such a round: one begun before it holds the old weather).
        private bool roundAfterSwap;

        // The cloud viewer's Rebuild Weather, once the generator's new state stands: the next frame fetches every map again (not at the map interval) and the maps snap to the new
        // values instead of sliding there at the slew limit, the height maps are made again and the history is dropped. The maps worker makes the new state's maps first, so the
        // round that finds none ends at once and the next one is asked for by the caller (CloudViewer) until one has come.
        internal void RequestWeatherRebuild()
        {
            PokeMapFetch();
            forceHeights = true;
            CloudBridgeLights.RequestHistoryReset("cloud viewer rebuild weather");
        }

        // Starts a map round at the next frame, snapping the maps (no history reset, no height rebuild): asked for each frame until a round has found the new maps.
        internal void PokeMapFetch()
        {
            forceMaps = snapMaps = true;
        }

        // The number of finished map rounds (Stats.MapFetches), for the viewer's wait for the first round after a rebuild.
        internal int MapRounds
        {
            get { return Stats.MapFetches; }
        }

        // The main-thread milliseconds of the last map round and the worst single frame of a round (the cost the viewer shows next to a rebuild).
        internal double LastMapRoundMs
        {
            get { return Stats.LastMapMs; }
        }

        // The inputs of the weather deck rule (WeatherDeckRule) once per round, for every layer that has one: the dew-point spread of the surface air (from the humidity and
        // temperature maps) and the wind. A map the field cannot give leaves its input null: no fog from the spread, a calm wind.
        private void FetchFogInputs(CelestialBody b, IWeatherField field, double ut)
        {
            bool needed = false;
            for (int k = 0; k < stack.LayerCount && !needed; k++) needed = CloudLookTuning.DeckRuleOf(stack.LayerAt(k).Name, stack.LayerAt(k).Deck) != null;
            fogSpreadK = null;
            fogWind = null;
            // E40 L3: the humidity and the surface as bytes for the types picked from their climate (CloudMesoStage); a map the field cannot give leaves the input null (neutral).
            climateHumidity = climateSurface = null;
            bool climate = false;
            for (int k = 0; k < stack.LayerCount && !climate; k++) climate = stack.LayerAt(k).HasClimate;
            if (climate)
            {
                if (field.GetMap(b.flightGlobalsIndex, WeatherMapKind.Humidity, ut, humidityMap) && humidityMap.Width == MapWidth && humidityMap.Height == MapHeight)
                {
                    humidityMap.PackBytes(0f, 1f);
                    climateHumidity = humidityMap.Bytes;
                }
                if (field.GetMap(b.flightGlobalsIndex, WeatherMapKind.Surface, ut, surfaceMap) && surfaceMap.Width == MapWidth && surfaceMap.Height == MapHeight)
                {
                    surfaceMap.PackBytes(0f, 1f);
                    climateSurface = surfaceMap.Bytes;
                }
            }
            if (!needed) return;
            bool haveH = field.GetMap(b.flightGlobalsIndex, WeatherMapKind.Humidity, ut, humidityMap) && humidityMap.Width == MapWidth && humidityMap.Height == MapHeight;
            bool haveT = haveH && field.GetMap(b.flightGlobalsIndex, WeatherMapKind.SurfaceTemperature, ut, temperatureMap) && temperatureMap.Width == MapWidth && temperatureMap.Height == MapHeight;
            if (haveT)
            {
                int n = humidityMap.Values.Length;
                float[] spread = spreadScratch;
                if (spread == null || spread.Length != n) spreadScratch = spread = new float[n];
                for (int i = 0; i < n; i++) spread[i] = WeatherDeckRule.SpreadK(temperatureMap.Values[i], humidityMap.Values[i]);
                fogSpreadK = spread;
            }
            if (field.GetMap(b.flightGlobalsIndex, WeatherMapKind.WindSpeed, ut, windMap) && windMap.Width == MapWidth && windMap.Height == MapHeight) fogWind = windMap.Values;
        }

        // The height maps of the layers (T13): at a slow rhythm, after a switch changed and after the stack was built. The build runs on a worker
        // (HeightJob) from a copy of what it reads; the main thread starts it, reads a field that is not thread safe in slices of a budget, and
        // applies a finished result to the stack it was planned for.
        private void BuildHeights(BodyClimate c, CelestialBody b, IWeatherField field, double ut, float now)
        {
            long start = Stopwatch.GetTimestamp();
            OwnCloudOptions.Apply(heightTuning);
            // The numbers are the body's (cfg keys height*); a body whose rules were read again is one more reason for a build.
            bool numbersChanged = !ReferenceEquals(c.Clouds.Height, heightRules);
            if (numbersChanged)
            {
                heightTuning.TakeNumbers(c.Clouds.Height);
                heightRules = c.Clouds.Height;
            }
            int switches = (heightTuning.BaseVariation ? 1 : 0) | (heightTuning.BasePhysics ? 2 : 0) | (heightTuning.TopFromUpdraft ? 4 : 0) | (heightTuning.BaseRagged ? 8 : 0);
            // The map sky: the bases come from the map's parts, so the heights are built again when the parts first come or go.
            bool skyNow = mapSkyPartsForHeights != null;
            bool changed = switches != heightSwitches || numbersChanged || skyNow != heightsHadMapSky;
            heightSwitches = switches;
            HeightResult done;
            HeightTake take = heightJob.TryTake(out done);
            if (take == HeightTake.Ready && ReferenceEquals(done.Source, stack))
            {
                done.ApplyTo(stack);
                heightsAppliedMapSky = heightsBuildingMapSky;
                lastHeightPlanUt = done.PlanUt;
                lastHeightMapsUt = done.MapsUt;
                lastHeightMapsRead = done.MapsRead;
                Stats.HeightBuilds++;
                Stats.LastHeightMs = done.ElapsedMs;
                if (Stats.HeightBuilds <= 1 && Stats.FirstHeightMs == 0.0) Stats.FirstHeightMs = Stats.LastHeightMs;
                else Stats.WorstHeightMs = Math.Max(Stats.WorstHeightMs, Stats.LastHeightMs);
            }
            else if (take == HeightTake.Failed && !heightFailedLogged)
            {
                heightFailedLogged = true;
                AtmospheresLog.Say("own clouds: a height build failed on the worker (" + heightJob.LastError + "); the next one is tried at the next interval");
            }
            // A build in the air began before a switch changed or the stack was replaced: its maps are not the ones wanted.
            if (heightJob.Busy && (changed || forceHeights)) heightJob.Cancel();
            if (heightJob.Busy) heightJob.Pump(HeightPumpBudgetMs);
            else if (OwnCloudPlan.Due(now, nextHeights, OwnCloudPlan.HeightInterval(TimeWarp.CurrentRate), forceHeights || changed))
            {
                nextHeights = now + OwnCloudPlan.HeightInterval(TimeWarp.CurrentRate);
                forceHeights = false;
                // The field hands its maps at the generator's size (512 x 256); the builder asks for the same, a smaller size would read nothing.
                HeightPlan plan = HeightPlan.Make(stack, heightTuning, ut, (uint)(b.flightGlobalsIndex + 1) * 0x9E3779B1u, MapWidth, MapHeight);
                heightsHadMapSky = heightsBuildingMapSky = skyNow;
                if (skyNow)
                {
                    plan.RootOverrideM = new float[plan.Count][];
                    plan.RootOverrideRangeM = new float[plan.Count];
                    for (int l = 0; l < plan.Count; l++)
                        if (plan.Tunings[l] != null) plan.RootOverrideM[l] = MapSkyRoots(l, stack.LayerAt(l), out plan.RootOverrideRangeM[l]);
                }
                heightJob.Begin(plan, field, stack);
                heightJob.Pump(HeightPumpBudgetMs);
            }
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (heightMainSeen) Stats.WorstHeightMainMs = Math.Max(Stats.WorstHeightMainMs, ms);
            heightMainSeen = true;
        }

        // --- The frame's numbers ------------------------------------------------------------------------------------------------------------

        private void FillFrame(Camera cam, CelestialBody b, BodyClimate c, IWeatherField field, double ut, float now)
        {
            LayerFrame f = frame;
            OwnCloudOptions.Apply(f);
            // E40 SOLO: cloud.soloLayer draws one layer alone (a mask a probe set, Frame.LayerMask, wins).
            f.SoloMask = CloudLookTuning.SoloMask();
            // The light cache (cloudLightCache, default off): made when the switch is first on, filled in the pass's buffer every frame, handed to the
            // light settings so that the shader reads it only while Enabled (OwnCloudOptions.Apply sets that from the key).
            if (lightCache == null && AtmospheresSettings.CloudLightCache)
            {
                lightCache = new LightCache();
                AtmospheresLog.Say("own clouds: light cache made (" + lightCache.GridN + " x " + lightCache.GridN + " x " + lightCache.GridNz + " voxels over " + lightCache.SizeM + " m)");
            }
            f.Light.Cache = lightCache;
            // The light volume refreshes faster under time warp (cloud.lightVolume.warp times this rate) and is off above cloud.lightVolume.warpLimit.
            LightVolume.TimeWarpRate = TimeWarp.fetch != null ? Mathf.Max(TimeWarp.CurrentRate, 1f) : 1f;
            OwnCloudOptions.Apply(f.Light);
            OwnCloudOptions.Apply(f.Far);
            // ProbeNoSurface / ProbeNoSceneDepth: the pinned capture's distance-limit look (T09ProbePin -t09PinLimits); false unless a probe sets them.
            f.HasSurface = b.hasSolidSurface && !ProbeNoSurface;
            f.FrameNumber = OwnCloudPlan.FrameNumber(Time.frameCount, FrameOrigin);
            f.UseSceneDepth = !ProbeNoSceneDepth;
            f.PlanetCenter = (Vector3)b.position;
            f.WorldToMap = MapFrame(b);

            // The light: the sun's direction and colour, the scene's ambient light, the moons, the lightning.
            Vector3 camPos = cam.transform.position;
            CelestialBody sunBody = GameGlobals.Sun;
            Vector3 toSun = sunBody != null ? (Vector3)sunBody.position - camPos : ((Vector3)b.position - camPos) + Vector3.up * 1e9f;
            f.TowardsSun = toSun.sqrMagnitude > 1f ? toSun.normalized : Vector3.up;
            Light sun = Sun.Instance != null ? Sun.Instance.sunLight : null;
            float gain = Tuning.Get(TuneSunGain, SunGainDefault);
            // The scene's sun colour is the display value of a white card facing the sun (the output stage's white); the pass is radiometric and gets pi times it.
            Color sceneSun = sun != null ? sun.color * sun.intensity : Color.white;
            float sunUnit = Tuning.Get(TuneSunUnit, CloudOutputStage.SunUnitDefault);
            // E40 sun colour: where Scatterer filters the sun (it multiplies the light by the air between the active vessel and the sun), the pass takes the viewer's air out of the
            // colour and the shader puts the air above each sample back, so a cloud gets the sun through the air above the cloud, not above the camera. With Scatterer's own air
            // only (ResolveAir); otherwise the colour stays as given and the shader's air is off, as before. OutputWhite below stays the viewer's value (the exposure's reference).
            ResolveAir(b);
            Color passSun = sceneSun;
            bool divided = false;
            double readSin = double.NaN, readAltitude = double.NaN;
            if (scattererLoaded && !Tuning.Flag(TuneSunAirWithScatterer, false))
            {
                if (air.Valid && air.FromScatterer && f.Light.SunAirHeightM > 0f)
                {
                    f.Light.UseAir(air.RayleighPerM, air.RayleighHeightM, air.MiePerM, air.MieHeightM);
                    Vector3 viewer = GameGlobals.ActiveVessel != null ? GameGlobals.ActiveVessel.transform.position : camPos;
                    Vector3 fromCentre = viewer - f.PlanetCenter;
                    double distance = fromCentre.magnitude;
                    double sinElevation = distance > 1.0 ? Vector3.Dot(fromCentre / (float)distance, f.TowardsSun) : 1.0;
                    passSun = CloudSunAir.OutsideAir(sceneSun, haveSunUnmodulated ? sunUnmodulated : (Color?)null, sinElevation, distance - b.Radius, f.Light);
                    divided = true;
                    readSin = sinElevation;
                    readAltitude = distance - b.Radius;
                }
                else f.Light.SunAirHeightM = 0f;
            }
            sunRead = new SunColourRead
            {
                FrameNumber = Time.frameCount, Scene = sceneSun, Unmodulated = sunUnmodulated, Pass = passSun, HaveUnmodulated = haveSunUnmodulated, ScattererLoaded = scattererLoaded,
                AirFromScatterer = air.FromScatterer, Divided = divided, SunAirHeightM = f.Light.SunAirHeightM, SinElevation = readSin, ViewerAltitudeM = readAltitude
            };
            Color sunColour = passSun * (sunUnit * gain);
            f.SunColour = sunColour;
            f.OutputExposure = Tuning.Get(TuneExposure, CloudOutputStage.ExposureDefault);
            f.OutputGamma = Tuning.Get(TuneOutputGamma, CloudOutputStage.GammaDefault);
            f.OutputWhite = Math.Max(0.01f, sceneSun.grayscale);
            float hazeLayerAltitude = 1500f;
            {
                // The white point's colour: the sun a cloud at the nominal altitude gets (the pass's sun times the shader's air above it), over its luminance, by the sun's height.
                Vector3 whiteViewer = GameGlobals.ActiveVessel != null ? GameGlobals.ActiveVessel.transform.position : camPos;
                Vector3 whiteFromCentre = whiteViewer - f.PlanetCenter;
                double whiteDistance = whiteFromCentre.magnitude;
                double whiteSin = whiteDistance > 1.0 ? Vector3.Dot(whiteFromCentre / (float)whiteDistance, f.TowardsSun) : 1.0;
                float whiteAltitude = Tuning.Get(TuneWhiteAltitudeM, -1f);
                if (whiteAltitude < 0f)
                {
                    // The lowest layer of the stack decides what the sky shows first: the sun at the lower quarter of it.
                    whiteAltitude = float.MaxValue;
                    if (stack != null)
                        for (int i = 0; i < stack.LayerCount; i++)
                        {
                            CloudLayerDef lowest = stack.LayerAt(i);
                            whiteAltitude = Math.Min(whiteAltitude, CloudOutputStage.NominalCloudAltitudeM(lowest.InnerAltitudeM, lowest.ThicknessM));
                        }
                    if (whiteAltitude == float.MaxValue) whiteAltitude = 1500f;
                }
                hazeLayerAltitude = whiteAltitude;
                Color cloudSun = CloudSunAir.AtAltitude(passSun, whiteSin, whiteAltitude, f.Light);
                f.OutputWhiteTint = CloudOutputStage.WhiteTint(cloudSun, CloudOutputStage.WhiteShare(whiteSin, Tuning.Get(TuneWhitePoint, 1f)));
            }
            // The scene's white balance (sceneWhiteBalance, audit A2): where the host's hook balances the whole picture by the same tint this frame, the clouds and
            // their haze leave it out (the haze's exemption is then the identity); otherwise the clouds keep their own white point.
            // The slider's strength s raises the tint to s (0: none, 1: the full tint); without the host's hook the clouds take that partial tint alone, as before.
            float strength = Mathf.Clamp01(AtmospheresSettings.SceneWhiteBalance);
            Color partial = SceneWhiteBalance.Partial(f.OutputWhiteTint, strength);
            bool sceneWanted = strength > 0.001f;
            bool sceneTakes = sceneWhiteBalance.Takes(sceneWanted);
            sceneWhiteBalance.Update(sceneWanted, partial, sceneTakes);
            Stats.SceneWhiteBalance = sceneTakes;
            Stats.SceneTint = f.OutputWhiteTint;
            f.OutputWhiteTint = sceneTakes ? Color.white : partial;
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

            Color moon = Color.black;
            float sunLuminance = Math.Max(0.01f, sunColour.grayscale);
            for (int i = 0; i < CloudLightSettings.MaxLights; i++) f.Light.Lights[i] = new CloudLight { Towards = Vector3.up, Colour = Color.black };
            CloudBridgeLights.MoonLight provider = CloudBridgeLights.Moon;
            Vector3 towards = Vector3.up;
            Color moonColour;
            if (provider != null && provider(b, (Vector3d)camPos, out towards, out moonColour))
            {
                moon = moonColour * (sunLuminance * Tuning.Get(TuneMoonGain, 1f));
                f.Light.Lights[0] = new CloudLight { Towards = towards, Colour = moon };
            }
            OwnCloudOptions.LimitMoonLights(f.Light, OwnCloudOptions.LiveSettings);
            // E40 C2: an extra light below light.moonMinShare of the sun (any moon in a daytime sky) is not marched to; the haze's in-scatter below still reads the moon as it was.
            float moonShare = Tuning.Get(OwnCloudOptions.TuneMoonMinShare, OwnCloudOptions.MoonMinShareDefault);
            float moonLuma = Math.Max(f.Light.Lights[0].Colour.grayscale, f.Light.Lights[1].Colour.grayscale);
            Stats.MoonOverSun = moonLuma / sunLuminance;
            Stats.MoonDropped = false;
            for (int i = 0; i < CloudLightSettings.MaxLights; i++)
            {
                if (f.Light.Lights[i].Colour.grayscale <= 0f || !OwnCloudOptions.MoonTooFaint(f.Light.Lights[i].Colour.grayscale, sunLuminance, moonShare)) continue;
                f.Light.Lights[i].Colour = Color.black;
                Stats.MoonDropped = true;
            }
            LightningModule lightning = LightningModule.Current;
            if (lightning != null) lightning.FillBolts(f.Light, b, OwnCloudPlan.FlashPeakScale(sunLuminance));
            else f.Light.BoltCount = 0;

            // The far view: the body's own air (ResolveAir, read before the sun colour), the in-scatter that follows the sky, the march policy by altitude.
            CloudBridgeAir.Apply(f.Far, air);
            // The aerial perspective behind Scatterer's scattering (cloud.farHaze 2, or AutoByDefault): on while the pass's buffers stand behind it.
            f.Far.Haze = CloudHazePlan.Active(f.Far.Haze, f.Far.HazeAuto, moveBehindScattering);
            Color hazeDisplay = new Color(Tuning.Get(CloudHazePlan.TuneR, CloudHazePlan.DefaultDisplayColour.r), Tuning.Get(CloudHazePlan.TuneG, CloudHazePlan.DefaultDisplayColour.g),
                                          Tuning.Get(CloudHazePlan.TuneB, CloudHazePlan.DefaultDisplayColour.b), 1f);
            // The colour a far cloud fades into is the radiance of the body's air along the cloud's own view ray, the near colour what a short path in-scatters (E40 H1 haze, CloudHorizonSky;
            // cloud.hazeSky 0: the law of before, hazeBlue / hazeNearBlue of the body and its share of the sun); both leave the white point's tint out (CloudHazePlan.ExemptFromWhitePoint).
            int hazeColourSource = (int)Tuning.Get(CloudHazePlan.TuneColour, 0f);
            float hazeShare = Tuning.Get(CloudHazePlan.TuneShare, CloudBridgeAir.HorizonShare);
            double altitude = ((Vector3d)camPos - b.position).magnitude - b.Radius;
            Color farRadiance, nearRadiance;
            if (hazeColourSource == 0 && Tuning.Get(CloudHazePlan.TuneSky, 1f) > 0.5f
                && SkyHazeColours(cam, f, b, altitude, hazeLayerAltitude, moon, sunLuminance, towards, ambientTop, out farRadiance, out nearRadiance))
            {
                farRadiance = CloudHazePlan.WithTint(farRadiance, c.Clouds.HazeTint);
            }
            else
            {
                farRadiance = CloudHazePlan.WithTint(CloudHazePlan.Inscatter(hazeColourSource, sunColour, moon, ambientTop, f.Far.RayleighPerM, hazeShare, hazeDisplay, f.OutputWhite, f.OutputExposure, f.OutputGamma, c.Clouds.HazeBlue),
                                                     hazeColourSource == 1 ? Vector3.one : c.Clouds.HazeTint);   // a display colour the key asks for is not tinted
                nearRadiance = CloudHazePlan.Inscatter(hazeColourSource, sunColour, moon, ambientTop, f.Far.RayleighPerM, hazeShare, hazeDisplay, f.OutputWhite, f.OutputExposure, f.OutputGamma, c.Clouds.HazeNearBlue);
            }
            f.Far.Inscatter = CloudHazePlan.ExemptFromWhitePoint(farRadiance, f.OutputWhiteTint, f.OutputGamma);
            f.Far.InscatterNear = CloudHazePlan.ExemptFromWhitePoint(nearRadiance, f.OutputWhiteTint, f.OutputGamma);
            f.Far.ApplyTo(f, altitude, b.Radius);

            if (now >= nextWind)
            {
                nextWind = now + 1f;
                UpdateMotion(cam, b, ut);
            }
        }

        // The evaluations of the air's horizon colour for the sun and for the moon (they keep the higher orders between frames, CloudHorizonSky.Evaluator) and the time of the last log line.
        private readonly CloudHorizonSky.Evaluator skySun = new CloudHorizonSky.Evaluator(), skyMoon = new CloudHorizonSky.Evaluator();
        private float nextSkyLog;

        // The colours a far cloud fades into and a short path in-scatters, from the body's air along the view ray of the camera that stands on the lowest layer (E40 H1 haze). False where
        // there is no air to work from.
        private bool SkyHazeColours(Camera cam, LayerFrame f, CelestialBody b, double altitude, float layerAltitudeM, Color moon, float sunLuminance, Vector3 moonTowards, Color ambient, out Color far, out Color near)
        {
            far = near = ambient;
            if (!air.Valid) return false;
            Vector3 up = (cam.transform.position - f.PlanetCenter).normalized, forward = cam.transform.forward;
            var v = new CloudHazePlan.SkyView { HeightM = altitude, LayerAltitudeM = layerAltitudeM };
            CloudHorizonSky.ViewAngles(up, forward, f.TowardsSun, out v.SunElevation, out v.SunAzimuth);
            if (moon.grayscale > 0f)
            {
                CloudHorizonSky.ViewAngles(up, forward, moonTowards, out v.MoonElevation, out v.MoonAzimuth);
                v.MoonOverSun = moon * (1f / sunLuminance);
            }
            if (!CloudHazePlan.SkyInscatter(skySun, skyMoon, air, b.Radius, v, ambient, f.OutputWhite, f.OutputExposure, f.OutputGamma, out far, out near)) return false;
            if (Time.realtimeSinceStartup >= nextSkyLog)
            {
                nextSkyLog = Time.realtimeSinceStartup + 10f;
                AtmospheresLog.Say("own clouds: haze colours of " + b.name + "'s air (display) far " + CloudOutputStage.Encode(far.r, f.OutputWhite, f.OutputExposure, f.OutputGamma).ToString("0.000") + " "
                                   + CloudOutputStage.Encode(far.g, f.OutputWhite, f.OutputExposure, f.OutputGamma).ToString("0.000") + " " + CloudOutputStage.Encode(far.b, f.OutputWhite, f.OutputExposure, f.OutputGamma).ToString("0.000")
                                   + ", near " + CloudOutputStage.Encode(near.r, f.OutputWhite, f.OutputExposure, f.OutputGamma).ToString("0.000") + " " + CloudOutputStage.Encode(near.g, f.OutputWhite, f.OutputExposure, f.OutputGamma).ToString("0.000")
                                   + " " + CloudOutputStage.Encode(near.b, f.OutputWhite, f.OutputExposure, f.OutputGamma).ToString("0.000") + "; sun " + (v.SunElevation * 180.0 / Math.PI).ToString("0.0") + " deg up, view "
                                   + (v.SunAzimuth * 180.0 / Math.PI).ToString("0") + " deg from it, camera " + altitude.ToString("0") + " m, sky exposure " + air.SkyExposureAt(altitude).ToString("0.00")
                                   + ", higher orders worked out " + skySun.Recomputed + " times");
            }
            return true;
        }

        // The air of the body: Scatterer's atmosphere node where Scatterer is loaded and the node reads (cloud.airSource), the body's own data otherwise. It is read before the
        // sun colour (FillFrame): the colour Scatterer hands over is divided by this air, which is only right where it is the air that made the colour.
        private void ResolveAir(CelestialBody b)
        {
            bool useScattererAir = CloudHazePlan.UseScattererAir(CloudHazePlan.SourceFrom(Tuning.Get(CloudHazePlan.TuneAirSource, -1f)), scattererLoaded);
            if (airBody == b.flightGlobalsIndex && airFromScatterer == useScattererAir) return;
            airBody = b.flightGlobalsIndex;
            airFromScatterer = useScattererAir;
            string origin = "";
            air = new CloudBridgeAir.Air();
            if (useScattererAir)
            {
                string why;
                air = ScattererAirReader.Read(b.name, out why);
                origin = air.Valid ? " (Scatterer's node)" : " (Scatterer's node not used: " + why + ")";
            }
            if (!air.Valid)
            {
                double h1 = Math.Max(1000.0, b.atmosphereDepth * 0.1);
                air = CloudBridgeAir.From(b.atmospherePressureSeaLevel * 1000.0, b.atmosphereTemperatureSeaLevel, b.atmosphereMolarMass, b.GetPressure(h1) * 1000.0, h1);
            }
            AtmospheresLog.Say("own clouds: air of " + b.name + origin + ": " + (air.Valid ? "Rayleigh " + air.RayleighPerM.x.ToString("0.0e+0") + " / " + air.RayleighPerM.z.ToString("0.0e+0")
                               + " per m, scale height " + air.RayleighHeightM.ToString("0") + " m" : "no usable data, the defaults of Earth's air stay"));
            AtmospheresLog.Say("own clouds: sun colour " + (scattererLoaded ? (air.FromScatterer ? "has the camera's air divided out with Scatterer's air" : "is Scatterer's colour as given (its node of " + b.name + " is not read)") : "is the scene's colour, the cloud's own air reddens it"));
        }

        // The rotation from world space into the map frame (x towards longitude 0, y towards 90 east, z north), measured from the body's surface
        // points so that its rotation and KSP's handedness are in it.
        internal static Matrix4x4 MapFrame(CelestialBody b)
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
            // look at the camera once a second does not find them (a camera that was rebuilt).
            bool readd = false;
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
            LayerReconstructor.Sub(depthCommands, "before the scene depth copy");
            depthCommands.Blit(BuiltinRenderTextureType.None, depthTarget, depthMaterial);
            LayerReconstructor.Sub(depthCommands, "scene depth copy");
            // The pinned capture's scene before the clouds (OwnCloudModule.Pin.cs); nothing unless a probe armed it for this frame.
            PinSceneCopy(depthCommands, true);
            if (!buffersOn) cam.AddCommandBuffer(attachedEvent, depthCommands);

            renderer.Commands.Clear();
            var depth = new RenderTargetIdentifier(depthTarget);
            bool reconstruct = false;
            if (driver != null)
            {
                OwnCloudOptions.Apply(recon);
                // The history is carried by the wind over the frame's time, scaled as the noise's drift is (cloud.motionScale, default 1: the frame's time as before; 0 holds the
                // pattern still, and the history then stays where the pattern stands instead of following a wind that moves nothing).
                recon.DriftSeconds = lastDt * CloudMotion.Scale;
                // The jitter the scene and its depth render with in this frame (the host decides it before the first scene camera culls, ahead of this camera's cull).
                Vector4 hostJitter = Shader.GetGlobalVector(HostJitter);
                recon.HostJitterNdc = new Vector2(hostJitter.z, hostJitter.w);
                reconstruct = driver.Configure(Tuning.Get, recon);
            }
            if (reconstruct)
            {
                driver.PlayerCamera = cam;
                int scene = (int)HighLogic.LoadedScene * 100000 + b.flightGlobalsIndex;
                if (CloudBridgeLights.Mark != null) CloudBridgeLights.Mark(renderer.Commands, CloudBridgeLights.MarkBegin);
                // The light cache is filled by the reconstruction's record, at the head of the march (LayerReconstructor.Record).
                Stats.LightCacheOn = lightCache != null && lightCache.Enabled;
                driver.Record(renderer.Commands, renderer, frame, cam, depth, BuiltinRenderTextureType.CameraTarget, OwnCloudPlan.FrameNumber(Time.frameCount, FrameOrigin), scene,
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
                    begin = cb =>
                    {
                        CloudBridgeLights.Mark(cb, CloudBridgeLights.MarkBegin);
                    };
                    end = cb => CloudBridgeLights.Mark(cb, CloudBridgeLights.MarkEnd);
                }
                // The plain path does not fill the light cache: the march marches the whole way to the sun.
                if (lightCache != null) lightCache.Enabled = false;
                Stats.LightCacheOn = false;
                renderer.Prepare(cam, frame, depth, begin, null, end, driver != null && recon.HostJitter ? ReconSettings.RayJitter(recon.HostJitterNdc) : Vector2.zero);
                Stats.PlainFrames++;
                Stats.Mode = "plain";
            }
            // The pinned capture's scene after the clouds' composite, at the end of the pass's own buffer (OwnCloudModule.Pin.cs).
            PinSceneCopy(renderer.Commands, false);
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
            EnsureMotionHook();
            if (!depthSeenLogged)
            {
                depthSeenLogged = true;
                AtmospheresLog.Say("own clouds: drawing at " + attachedEvent + " on " + cam.name + ", depth target " + depthTarget.width + "x" + depthTarget.height
                                   + ", camera depth mode " + cam.depthTextureMode + ", rendering path " + cam.actualRenderingPath);
            }
        }

        // --- The cloud's motion vectors (E40 MVCLOUD) ------------------------------------------------------------------------------------------

        // The handler is on the host's hook while cloud.motionVectors or cloud.mvAudit is on (the audit reads the host's vectors whether or not the pass draws into them). A host without
        // the hook is noted once and not asked again.
        private void EnsureMotionHook()
        {
            bool want = !motionRefused && (Tuning.Flag(OwnCloudOptions.TuneMotionVectors, OwnCloudOptions.MotionVectorsDefault)
                                           || Tuning.Flag(OwnCloudOptions.TuneMotionAudit, OwnCloudOptions.MotionAuditDefault));
            if (want == motionRegistered) return;
            if (motionBridge == null) motionBridge = new ApiBridge();
            if (motionHandler == null) motionHandler = OnMotionVectors;
            if (want)
            {
                motionRegistered = motionBridge.RegisterMotionVectors("own clouds", motionHandler);
                if (!motionRegistered)
                {
                    motionRefused = true;
                    Stats.MotionNote = "the host has no motion vector hook";
                    AtmospheresLog.Say("own clouds: the host has no motion vector hook (or refused it); the clouds keep the host's vectors");
                }
            }
            else
            {
                motionBridge.UnregisterMotionVectors(motionHandler);
                motionRegistered = false;
            }
        }

        // The host's call as the scene camera culls, with the capture buffer and the targets it records into. The draw is recorded now when this frame's pass is, else in OnPreCull once it is.
        private void OnMotionVectors(CommandBuffer commands, RenderTexture vectors, RenderTexture depth, Camera camera)
        {
            motionCommands = commands;
            motionVectors = vectors;
            motionDepth = depth;
            motionCamera = camera;
            motionFrame = Time.frameCount;
            if (recordedFrame == motionFrame) DrawMotion();
        }

        // Once a frame: the cloud's vectors over the host's, and the audit's readbacks behind them. The pass has to have recorded the frame as the reconstruction, from the camera the host's
        // vectors belong to; otherwise the host's vectors stand and Stats.MotionNote says why.
        private void DrawMotion()
        {
            if (motionDrawnFrame == Time.frameCount) return;
            motionDrawnFrame = Time.frameCount;
            bool drawn = false;
            RenderTexture cloud = null;
            try
            {
                string why = null;
                if (broken || !buffersOn || driver == null || driver.Player == null || renderer == null || Stats.Mode != "reconstruct") why = "the pass is not drawing as the reconstruction";
                else if (!ReferenceEquals(motionCamera, attachedCamera)) why = "the host's scene camera is not the cloud camera";
                if (why != null)
                {
                    Stats.MotionNote = why;
                    if (!motionCameraLogged && why.StartsWith("the host's"))
                    {
                        motionCameraLogged = true;
                        AtmospheresLog.Say("own clouds: motion vectors not drawn: " + why + " (" + (motionCamera != null ? motionCamera.name : "none") + " against "
                                           + (attachedCamera != null ? attachedCamera.name : "none") + ")");
                    }
                    return;
                }
                cloud = driver.Player.Output;
                if (Tuning.Flag(OwnCloudOptions.TuneMotionVectors, OwnCloudOptions.MotionVectorsDefault))
                {
                    drawn = driver.Player.RecordMotion(motionCommands, motionVectors, motionDepth);
                    if (drawn) Stats.MotionFrames++;
                    Stats.MotionNote = drawn ? "" : "no frame before this one";
                }
                else
                {
                    Stats.MotionNote = "switched off (cloud.motionVectors)";
                }
                if (Tuning.Flag(OwnCloudOptions.TuneMotionAudit, OwnCloudOptions.MotionAuditDefault))
                {
                    if (motionAudit == null) motionAudit = new CloudMotionAudit();
                    motionAudit.Frame(motionCommands, motionVectors, motionDepth, cloud, driver.Player, renderer, lastDt, drawn);
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private void EnsureDepthTarget(Camera cam)
        {
            int w = cam.pixelWidth, h = cam.pixelHeight;
            Stats.RenderWidth = w;
            Stats.RenderHeight = h;
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

        // E40 C2: the maps the layer pass marches this frame (the coverage and type bytes of each layer as uploaded, and the humidity of the time) as .u8 files in a folder of the series pictures,
        // so that the weather player can draw the very sky of a game start (tools/weather_metrics/e40c1.py --prepare-game): until then the player's own reproduction of a scene differed from
        // the game's (cloud share inside 0.34 against 0.71, in sky3 0.87 against 0.67 at the K-T2 clock). Once per tag.
        private readonly HashSet<string> dumpedMaps = new HashSet<string>();

        internal void DumpMaps(string tag)
        {
            if (renderer == null || stack == null || !dumpedMaps.Add(tag)) return;
            WriteDump(System.IO.Path.Combine(System.IO.Path.Combine(SeriesPictures.Folder, "maps"), tag), tag);
        }

        // The maps and pass.json into a folder (DumpMaps; the pinned capture writes them into its own folder in the frame of its readbacks). False where it failed.
        private bool WriteDump(string dir, string tag)
        {
            if (renderer == null || stack == null) return false;
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                for (int l = 0; l < stack.LayerCount; l++)
                {
                    byte[] cover = renderer.Maps.CoverageBytes(l), type = renderer.Maps.TypeBytes(l);
                    string name = stack.LayerAt(l).Name;
                    if (cover != null) System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name + "-cover.u8"), cover);
                    if (type != null) System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name + "-type.u8"), type);
                    // E40 PP: the targets the slew moves the maps toward, and the height maps the GPU holds (three bytes a texel: root, top, variation; ranges in pass.json).
                    byte[] coverGoal = renderer.Maps.CoverageGoalBytes(l), typeGoal = renderer.Maps.TypeGoalBytes(l);
                    if (coverGoal != null) System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name + "-cover-goal.u8"), coverGoal);
                    if (typeGoal != null) System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name + "-type-goal.u8"), typeGoal);
                    HeightMaps heights = renderer.Heights.AppliedMaps(l);
                    if (heights != null) System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name + "-height.u8"), heights.Bytes);
                }
                if (climateHumidity != null) System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "humidity.u8"), climateHumidity);
                // The map sky: the package B maps and the layers' parts the round drew (nothing where the round had no weather data).
                WriteMapSkyDump(dir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "pass.json"), PassJson(tag));
                AtmospheresLog.Say("own clouds: dumped the layer maps of " + stack.LayerCount + " layers and the pass's numbers (pass.json) to " + dir);
                return true;
            }
            catch (Exception e)
            {
                AtmospheresLog.Say("own clouds: the map dump of " + tag + " failed: " + e.Message);
                return false;
            }
        }

        // E40 PP: the pass's numbers of the frame the maps are dumped in (.local/plans/atmospheres/E40-player-parity.md): the camera relative to the body's centre in world
        // axes (the noise is looked up in world axes, so the replay keeps them and takes the world-to-map rotation), the clock, the sun's direction and its colours (the scene's,
        // Scatterer's restored one, the pass's), the display stage, the noise's drift and wind, the reconstruction, the height maps' ranges, which layers carry drawn anvils, and
        // the uniforms the material holds (LayerPassReadout). Positions in m, directions unit vectors.
        private string PassJson(string tag)
        {
            var s = new System.Text.StringBuilder(32768);
            LayerFrame f = frame;
            Camera cam = attachedCamera;
            Vector3 centre = f.PlanetCenter;
            s.Append("{\"tag\": ").Append(LayerPassReadout.Str(tag));
            s.Append(",\n \"ut\": ").Append(LayerPassReadout.Num(Planetarium.GetUniversalTime())).Append(", \"frame\": ").Append(Time.frameCount);
            s.Append(", \"body\": ").Append(LayerPassReadout.Str(body != null ? body.name : null)).Append(", \"planetRadius\": ").Append(LayerPassReadout.Num(body != null ? body.Radius : 0.0));
            if (cam != null)
            {
                Transform t = cam.transform;
                s.Append(",\n \"camera\": {\"position\": ");
                Json3(s, t.position - centre);
                s.Append(", \"forward\": ");
                Json3(s, t.forward);
                s.Append(", \"right\": ");
                Json3(s, t.right);
                s.Append(", \"up\": ");
                Json3(s, t.up);
                s.Append(", \"fov\": ").Append(LayerPassReadout.Num(cam.fieldOfView)).Append(", \"width\": ").Append(cam.pixelWidth).Append(", \"height\": ").Append(cam.pixelHeight).Append('}');
            }
            s.Append(",\n \"worldToMap\": [");
            for (int r = 0; r < 3; r++)
            {
                if (r > 0) s.Append(", ");
                Vector4 row = f.WorldToMap.GetRow(r);
                Json3(s, new Vector3(row.x, row.y, row.z));
            }
            s.Append("],\n \"sun\": {\"towards\": ");
            Json3(s, f.TowardsSun);
            s.Append(", \"colour\": ");
            JsonColour(s, f.SunColour);
            s.Append(", \"scene\": ");
            JsonColour(s, sunRead.Scene);
            s.Append(", \"unmodulated\": ");
            JsonColour(s, sunRead.Unmodulated);
            s.Append(", \"pass\": ");
            JsonColour(s, sunRead.Pass);
            s.Append(", \"haveUnmodulated\": ").Append(sunRead.HaveUnmodulated ? 1 : 0).Append(", \"divided\": ").Append(sunRead.Divided ? 1 : 0)
             .Append(", \"sinElevation\": ").Append(LayerPassReadout.Num(sunRead.SinElevation)).Append(", \"sunAirHeightM\": ").Append(LayerPassReadout.Num(sunRead.SunAirHeightM)).Append('}');
            s.Append(",\n \"output\": {\"exposure\": ").Append(LayerPassReadout.Num(f.OutputExposure)).Append(", \"gamma\": ").Append(LayerPassReadout.Num(f.OutputGamma))
             .Append(", \"white\": ").Append(LayerPassReadout.Num(f.OutputWhite)).Append(", \"whiteTint\": ");
            JsonColour(s, f.OutputWhiteTint);
            s.Append("},\n \"ambientTop\": ");
            JsonColour(s, f.AmbientTop);
            s.Append(", \"ambientBottom\": ");
            JsonColour(s, f.AmbientBottom);
            s.Append(",\n \"recon\": {\"mode\": ").Append(LayerPassReadout.Str(Stats.Mode)).Append(", \"setting\": ").Append(LayerPassReadout.Str(recon.Mode.ToString()))
             .Append(", \"resolution\": ").Append(LayerPassReadout.Num(recon.Resolution)).Append(", \"accumulate\": ").Append(LayerPassReadout.Num(recon.Accumulate))
             .Append(", \"frameResolution\": ").Append(LayerPassReadout.Num(f.Resolution)).Append(", \"maxSteps\": ").Append(f.MaxSteps).Append(", \"dither\": ").Append(f.DitherSequence)
             .Append(", \"historyCuts\": ").Append(driver != null ? driver.CutCount : 0).Append('}');
            LayerNoise noise = renderer.Noise;
            s.Append(",\n \"noise\": {\"seed\": ").Append(noise.Switches.Seed).Append(", \"flowTime\": ").Append(LayerPassReadout.Num(noise.FlowTimeSeconds)).Append(", \"layers\": [");
            var drift = new double[3];
            for (int l = 0; l < stack.LayerCount; l++)
            {
                double rise;
                Vector3 wind;
                noise.StateOf(l, drift, out rise, out wind);
                if (l > 0) s.Append(", ");
                s.Append("{\"drift\": [").Append(LayerPassReadout.Num(drift[0])).Append(", ").Append(LayerPassReadout.Num(drift[1])).Append(", ").Append(LayerPassReadout.Num(drift[2]))
                 .Append("], \"rise\": ").Append(LayerPassReadout.Num(rise)).Append(", \"wind\": ");
                Json3(s, wind);
                s.Append(", \"axis\": ");
                Json3(s, noise.AxisOf(l));
                s.Append('}');
            }
            s.Append("]},\n \"layers\": [");
            for (int l = 0; l < stack.LayerCount; l++)
            {
                CloudLayerDef d = stack.LayerAt(l);
                HeightMaps h = renderer.Heights.AppliedMaps(l);
                if (l > 0) s.Append(",\n  ");
                s.Append("{\"name\": ").Append(LayerPassReadout.Str(d.Name)).Append(", \"anvilDrawn\": ").Append(AnvilMap.AnvilTypeIndex(d) >= 0 ? 1 : 0)
                 .Append(", \"heightSeed\": ").Append(stack.Heights.SeedOf(l));
                if (h != null)
                    s.Append(", \"height\": {\"width\": ").Append(h.Width).Append(", \"height\": ").Append(h.Height).Append(", \"root\": ").Append(LayerPassReadout.Num(h.RootRangeM))
                     .Append(", \"top\": ").Append(LayerPassReadout.Num(h.TopRangeM)).Append(", \"variation\": ").Append(LayerPassReadout.Num(h.VariationRangeM)).Append('}');
                s.Append('}');
            }
            s.Append(']');
            // The pinned capture's numbers (OwnCloudModule.Pin.cs): the frame numbers and the history, the tuning keys in force, the march and temporal settings, the type table's hash.
            s.Append(PinJsonNow());
            // LV3: the light volume of the player's view at this frame (its lays, the slices filled and stamped) and whether its Record left the march's keyword on; the
            // uniforms' "keywords" below are the material's at the frame's end (another camera's Record may have changed them since).
            LayerReconstructor player = driver != null ? driver.Player : null;
            LightVolume lv = player != null ? player.CurrentLightVolume : null;
            s.Append(",\n \"lightVolume\": {\"keywordAtRecord\": ").Append(player == null ? "null" : player.LightVolumeKeywordAtRecord ? "true" : "false")
             .Append(", \"state\": ").Append(lv != null ? lv.StateJson() : "null").Append('}');
            // The map sky: which sky the last map round published and whether the applied heights carry the map's bases.
            s.Append(MapSkyJson());
            s.Append(",\n \"uniforms\": ").Append(LayerPassReadout.Uniforms(renderer.Material)).Append("}\n");
            return s.ToString();
        }

        private static void Json3(System.Text.StringBuilder s, Vector3 v)
        {
            s.Append('[').Append(LayerPassReadout.Num(v.x)).Append(", ").Append(LayerPassReadout.Num(v.y)).Append(", ").Append(LayerPassReadout.Num(v.z)).Append(']');
        }

        private static void JsonColour(System.Text.StringBuilder s, Color c)
        {
            s.Append('[').Append(LayerPassReadout.Num(c.r)).Append(", ").Append(LayerPassReadout.Num(c.g)).Append(", ").Append(LayerPassReadout.Num(c.b)).Append(']');
        }

        private void PublishStats(float now)
        {
            if (now < nextStats) return;
            nextStats = now + 30f;
            AtmospheresLog.Say("own clouds: " + Stats.Mode + ", " + Stats.Layers + " layers, frames " + Stats.FramesDrawn + " (plain " + Stats.PlainFrames + ", reconstruct "
                               + Stats.ReconFrames + "), map fetches " + Stats.MapFetches + " (last " + Stats.LastMapMs.ToString("0.0") + " ms, worst "
                               + Stats.WorstMapMs.ToString("0.0") + " ms, main step worst " + Stats.WorstMapMainMs.ToString("0.0") + " ms), height builds " + Stats.HeightBuilds
                               + " (last " + Stats.LastHeightMs.ToString("0.0") + " ms on the worker, worst " + Stats.WorstHeightMs.ToString("0.0") + " ms, first "
                               + Stats.FirstHeightMs.ToString("0.0") + " ms, main thread worst " + Stats.WorstHeightMainMs.ToString("0.0") + " ms), height upload worst "
                               + Stats.WorstUploadMs.ToString("0.0") + " ms, motion vector frames " + Stats.MotionFrames
                               + (string.IsNullOrEmpty(Stats.MotionNote) ? "" : " (" + Stats.MotionNote + ")") + ", buffer attaches " + Stats.BufferAttaches + ", moves behind Scatterer's scattering " + Stats.OrderMoves + ", moon light " + (Stats.MoonOverSun * 100f).ToString("0.00") + " percent of the sun"
                               + (Stats.MoonOverSun > 0f ? (Stats.MoonDropped ? " (not marched to: too faint)" : " (marched to at every cloud sample)") : "") + ", history cuts " + (driver != null ? driver.CutCount : 0)
                               + (driver != null && driver.LastCutReason != null ? " (last: " + driver.LastCutReason + ")" : ""));
        }
    }
}
