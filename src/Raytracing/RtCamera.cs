using System;
using System.Collections.Generic;
using System.Text;
using ReDefinition.Core;
using ReDefinition.Proxy;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Raytracing
{
    // What the raytracing of a camera does, as the settings choose it.
    internal struct RtSettings
    {
        // The passes: the sun's raytraced shadow, the occlusion (set with Diffuse, read nowhere on its own), the
        // diffuse light and the reflections; the denoiser, where Ray Reconstruction does not denoise instead.
        public bool Shadows;
        public bool Occlusion;
        public bool Diffuse;
        public bool Reflections;
        public bool Denoise;
        // The diffuse light's bounces, the occlusion's reach in metres, and the luminance a single sample is clamped
        // to (RtCommon.hlsli, BOUNCES, AO_DISTANCE, SAMPLE_CLAMP; 0 for no clamp).
        public int Bounces;
        public float OcclusionDistance;
        public float Clamp;

        // Everything on, two bounces, the occlusion to 2 m, samples clamped at 20.
        internal static RtSettings Full()
        {
            return new RtSettings
            {
                Shadows = true, Occlusion = true, Diffuse = true, Reflections = true, Denoise = true,
                Bounces = 2, OcclusionDistance = 2f, Clamp = 20f,
            };
        }
    }

    // The raytracing of one camera (docs/development/flow-raytracing.md). On the scene camera,
    // OnPreCull hands over the scene's instances of this frame.
    // In the game (TraceBeforeLighting) the depth and G-buffer are copied in the camera's own
    // command stream before its lighting, and the raytracing starts on the proxy there.
    // This frame's lighting reads its results. The test players can trace after the
    // frame instead (RtLateTrace), its results carried over to the next frame's lighting.
    //
    // What depends on KSP comes in through the fields below: the sources of the scene,
    // the sun, the sky, how far the world moved between two frames, and the projection
    // the frame was rendered with.
    internal sealed partial class RtCamera : MonoBehaviour
    {
        // The camera's raytracing scene, filled each frame from its sources, and the passes it runs.
        internal readonly RtScene Scene = new RtScene();
        internal RtSettings Settings = RtSettings.Full();

        // The shaders: the capture, the screen-space shadows, the deferred reflections;
        // the compute shader that gathers the GPU's instances.
        internal Func<string, Shader> ShaderSource = Shader.Find;
        internal Func<string, ComputeShader> ComputeSource = name => Resources.Load<ComputeShader>(name);
        // Towards the sun, and its angular radius in w; its colour; the sky's.
        internal Func<Vector4> Sun = () => new Vector4(0.3f, 0.8f, 0.5f, 0.0047f);
        internal Func<Color> SunColor = () => Color.white;
        // The sun's light, whose screen-space mask alone takes the raytraced visibility;
        // null for every directional light's.
        internal Func<Light> SunLight;
        internal Func<Color> SkyColor = () => RenderSettings.ambientSkyColor;
        // How much of its colour the sky's light loses (RtCommon.hlsli, SkyLight): 0 keeps it.
        internal Func<float> SkyNeutral = () => 0f;
        // -redefinitionProbeParity: every five seconds the primary check runs beside the frame, and the log says
        // where the rays' scene and the picture disagree (RtParity), with a picture of it in ReDefinitionCaptures.
        private static readonly bool ParityProbe = CommandLine.Has("-redefinitionProbeParity");
        private RenderTexture parity;
        private RenderTexture parityRays;
        private RenderTexture parityRaster;
        private IntPtr parityHandle;
        private IntPtr parityRaysHandle;
        private IntPtr parityRasterHandle;
        private float parityAt;
        private bool parityAsked;
        private bool parityPending;
        // The name of the pose a report was asked for (ParityNow), logged before the report.
        private string parityLabel;
        private uint parityFlag;
        // -redefinitionProbeCloudShadow: the sun's visibility is the cloud layer's transmittance
        // alone, to compare with EVE's own cloud shadow (the probe's shadow mask dump).
        private static readonly bool CloudShadowCheck =
            CommandLine.Has("-redefinitionProbeCloudShadow");
        // The main body's cloud layer: fills the packet's cloud constants (RtFramePacket.clouds)
        // and gives its map's native texture (RT_INPUT_CLOUDS); zero for none.
        internal Func<float[], IntPtr> Clouds = clouds =>
        {
            for (int i = 0; i < clouds.Length; i++) clouds[i] = 0f;
            return IntPtr.Zero;
        };
        // The sky as a cube map, or null for the sky's colour.
        internal Func<Texture> Environment = () => null;
        // Where a point of this frame's world was in the previous frame's: previous = this +
        // shift, for what is near the camera, and for the bodies and what stands on them,
        // which KSP moves apart under Krakensbane.
        internal Func<RtPosition> WorldShift = () => new RtPosition(0, 0, 0);
        internal Func<RtPosition> WorldShiftFar;
        // The projection this frame was rendered with, jitter and all; the same without the
        // jitter, and the jitter in pixels.
        internal Func<Matrix4x4> RenderedProjection;
        internal Func<Matrix4x4> UnjitteredProjection;
        internal Func<Vector2> Jitter = () => Vector2.zero;
        // The main body's centre in the world and its radius, which the sun's rays test
        // as a sphere; radius 0 for none.
        internal Func<Vector4> Body = () => Vector4.zero;
        // The sea's frame fixed to the main body, for the waves (RtCommon.hlsli,
        // MakeSeaFrame). It gives a point on the sea relative to the camera, and its east and north,
        // world space. It returns false for none, where the waves take the plane under the camera.
        internal SeaFrameSource Sea = (out Vector3 origin, out Vector3 east, out Vector3 north) =>
        {
            origin = east = north = Vector3.zero;
            return false;
        };
        // The type of Sea: a delegate, as a Func cannot have out parameters.
        internal delegate bool SeaFrameSource(out Vector3 origin, out Vector3 east, out Vector3 north);
        // For the light probe's check that the waves stay on the water: the water's trace
        // gives its normals, the waves' time held, and the sea's frame left out (the plane
        // under the camera) where wanted.
        internal static bool WaterNormalsShown;
        internal static float? WaveTimeHeld;
        internal static bool SeaFrameLeftOut;
        // For the light probe: the G-buffer's smoothness as Deferred wrote it, without
        // LegacySmoothness, to compare.
        internal static bool DeferredSmoothness;
        // For the light probe: the surfaces' shine as Deferred's G-buffer gives it, to compare
        // with the shine their materials mean (SurfaceIntent).
        internal static bool SurfacesAsDeferred;
        // The surfaces' shine as a coloured overlay (the diagnostics window, Capture & log):
        // ReDefinitionDeferredReflections.shader, _ReDefinitionSurfaceView.
        internal static bool SurfaceView;
        // For the light probe: without the discs added to Scatterer's ocean, or without
        // the reflections handed to it through ssrOutput, to compare.
        internal static bool OceanDiscsHeldOff;
        internal static bool OceanHandoffHeldOff;

        // The light probe's switches above off, as on a scene's start: a probe's coroutine
        // left by a scene change does not get to switch them back.
        internal static void ResetProbeSwitches()
        {
            WaterNormalsShown = false;
            WaveTimeHeld = null;
            SeaFrameLeftOut = false;
            DeferredSmoothness = false;
            SurfacesAsDeferred = false;
            OceanDiscsHeldOff = false;
            OceanHandoffHeldOff = false;
        }
        // Whether the G-buffer's smoothness is Deferred's from KSP's legacy part shaders,
        // which the raytracing takes as their highlight (RtCommon.hlsli, LegacySmoothness):
        // the game's; a test's G-buffer is Unity's own.
        internal Func<bool> LegacySmoothness = () => false;
        // The bodies the sun lights, as lights (RtReflectors in the game): fills the
        // packet's lights -- towards each, its angular radius, its radiance linear and how
        // far its shadow ray goes, to the body's surface -- and says how many.
        // None by default.
        internal Func<float[], float[], int> Lights = (lights, surfaces) => 0;
        // How much of Unity's ambient light stays as the least light a raytraced point
        // gets, 0 to 1 (KSP's night light: the player's Night light). All by default.
        internal Func<float> NightLight = () => 1f;
        // The main body's albedo, linear: rays that miss the scene and meet its sphere
        // (Body) take the sun's and the lights' light on it times this.
        internal Func<Color> PlanetAlbedo = () => new Color(0.12f, 0.12f, 0.12f);
        // Whether Scatterer's ocean takes its reflections from the raytracing this frame:
        // where Deferred's screen-space reflections give way to the raytraced ones, the
        // ocean, which reads theirs (ssrOutput), would show whatever they last held.
        internal Func<bool> OceanReflections = () => false;
        // How strongly water mirrors the sun's disc, 1 as it is (RtCommon.hlsli, SUN_MIRROR).
        internal Func<float> SunMirror = () => 1f;
        // Whether the main body's sea is KSP's own, which the reflections take as water by
        // its level (RtCommon.hlsli, STOCK_WATER; ReDefinitionDeferredReflections.shader).
        internal Func<bool> StockWater = () => false;
        // Unity's ambient light, which its shading adds by itself: the raytraced light
        // added to the frame is this much less. Handed over facing up, sideways and down.
        // The probe Unity shades with: RenderSettings.ambientProbe holds only a baked one.
        internal Func<Vector3, SphericalHarmonicsL2> AmbientProbe = at =>
        {
            SphericalHarmonicsL2 probe;
            LightProbes.GetInterpolatedProbe(at, null, out probe);
            return probe;
        };
        private static readonly Vector3[] AmbientDirections = { Vector3.up, Vector3.right, Vector3.down };
        private readonly Color[] ambientValues = new Color[3];
        // Unity's frame count at which the upscaler last asked for DLSS Ray
        // Reconstruction's frames. Until the frame after it, they go undenoised, as Ray
        // Reconstruction denoises the finished frame, and make its guides from their
        // surfaces (UpscalerRig.Reconstruction.cs). It is a lease, so that nothing holds it on
        // once the upscaler stops asking.
        internal int ReconstructionLease = -1;
        private bool reconstructedBefore;
        // The last frame that made the guides: Unity's frame count, the packet's index,
        // its camera-relative clip-to-world.
        internal int GuidesFrame { get; private set; } = -1;
        // The packet index of the frame that made the guides, which Ray Reconstruction's packet names.
        internal uint GuidesIndex { get; private set; }
        // That frame's camera-relative clip-to-world as its packet had it, for Ray Reconstruction's packet.
        internal readonly float[] GuidesClipToWorld = new float[16];
        // The raytraced camera, while one is on.
        internal static RtCamera Current { get; private set; }
        // The Unity camera this traces for, the component's own.
        internal Camera Camera { get { return cam; } }
        // Whether this frame is a cut: the camera jumped, the history is not its.
        internal Func<bool> Cut = () => false;
        // The camera's motion vectors, where they are rendered and correct.
        internal bool MotionVectors;
        // Whether to trace each frame after its G-buffer (deferred) or depth texture (forward) and
        // before its lighting. Then the lighting reads this frame's signals: nothing is
        // carried from the frame before, and nothing is missing where the frame before did
        // not see. Otherwise the trace runs after the frame, for the next one (OnRenderImage).
        internal bool TraceBeforeLighting;
        // Called before the trace reads the camera's projection in OnPreRender: whatever
        // jitters it (the upscaler's CameraRedirect) applies its offset now, as Unity
        // orders the components' OnPreRender as it likes.
        internal Action BeforeTrace;
        private CameraEvent traceEvent;
        private bool traceAttached;

        // Why the raytracing does not run on this camera (the proxy not ready, the shaders missing); null where it runs.
        internal string Problem { get; private set; }
        // The passes the last packet asked for, as RtNative's Signal constants.
        internal uint LastSignals { get; private set; }
        // Each pass's GPU time in milliseconds as the proxy last measured it, filled by Status (sun, occlusion,
        // diffuse, reflections, ..., denoiser at 6).
        internal float[] PassTimings = new float[8];

        // The camera this component raytraces for.
        private Camera cam;
        // The capture material with ReDefinitionRtCapture.shader's passes.
        private Material capture;
        // The screen-space shadows and deferred reflections put in Unity's place (RtLighting).
        private Shader shadows;
        private Shader reflections;
        // The trace's command buffer, the G-buffer's copy after it is filled, the forward light.
        private CommandBuffer command;
        private CommandBuffer gbufferCopy;
        private CommandBuffer forwardLight;
        // The sun mask's flag on and off around the sun's screen-space mask, and the light they are on.
        private CommandBuffer maskOn, maskOff;
        private Light maskedLight;
        // The pass of ReDefinitionRtCapture.shader (fragShadowFade) that writes how Unity fades the sun's shadow out
        // towards its shadow distance in forward rendering, which only the GPU knows. It runs after the sun's
        // screen-space shadows. The result is read back a frame or two later, and kept in the camera's own frame,
        // where Unity's fade does not move with it.
        private const int ShadowFadePass = 15;
        // The light a meadow's blades keep from its ground. Measured at the launch pad on the RTX A4500, 2026-10-25,
        // with the blades letting through half the sun (rtFoliageTransmission 0.5). The terrain past Parallax's grass
        // about the vessel is as bright as the terrain in it: 0.99 of it by day and 0.94 in the evening. Medians of
        // eleven views from 150 m to 4 km, single views 0.81 to 1.10. When the rays saw the grass's
        // shadow only near the camera (RtCommon.hlsli, RayOrigin), it was 0.77. It goes into the ground's
        // albedo for sun and sky alike.
        private const float MeadowShade = 0.82f;
        // Where the packet keeps what it carries beside the name of its array: Params (RtNative.FramePacket.Params), the
        // sun's shadow strength in the sun colour's w, the waves' time in the planet's w, and the meadow's range and the
        // range of the grass about the vessel in the shadow fade's slots 6 and 11.
        private const int ParamOcclusion = 0, ParamBounces = 1, ParamDiffuseReach = 2, ParamSpecularReach = 3, ParamClamp = 4,
                          ParamEnvironment = 5, ParamTextures = 6, ParamGBuffer = 7;
        private const int SunShadowStrength = 3, PlanetWaveTime = 3, ShadowFadeMeadowRange = 6, ShadowFadeVesselRange = 11;
        // How far the diffuse and the specular rays go, in metres.
        private const float RayReach = 100000f;
        private static readonly float MeadowShadeProbe = CommandLine.After("-redefinitionProbeMeadowShade") != null
            && float.TryParse(CommandLine.After("-redefinitionProbeMeadowShade"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float shadeProbe)
            ? shadeProbe : 0f;
        private static readonly float MeadowFrom = CommandLine.After("-redefinitionProbeMeadowFrom") != null
            && float.TryParse(CommandLine.After("-redefinitionProbeMeadowFrom"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float from)
            ? from : 0f;
        private CommandBuffer fadeGrab;
        private RenderTexture fadeTexture;
        private Action<AsyncGPUReadbackRequest> fadeDone;
        private bool fadeGrabbed, fadeReading;
        private Matrix4x4 fadeGrabbedView, fadeReadView;
        private Vector4 fadeCentre, fadeScale;
        // The sun's colour while this camera draws: Scatterer changes it for the local
        // cameras only, by the eclipse and the atmosphere's extinction, and sets it back
        // after them (SunlightModulator).
        private Color drawnSunColour;
        private int drawnSunFrame = -1;
        private CameraEvent forwardEvent;
        private Material forwardMaterial;
        private Mesh screenQuad;
        private CommandBuffer flagOn;
        private CommandBuffer flagOff;
        // One for every camera raytracing attaches to, in turn: a ring is never freed
        // (PacketRing), so one per attach would be left behind with each.
        private static PacketRing ring;
        private RtNative.FramePacket packet;
        private RenderTexture depth, gbuffer0, gbuffer1, gbuffer2, motion, environment;
        // The G-buffer 0 and 1 as the G-buffer pass left them, before a mod's later pass changes them.
        private RenderTexture gbuffer0Early, gbuffer1Early;
        private RenderTexture sun, diffuse, specular, forward, forwardRatio;
        // The surfaces as their materials mean them, and how each was found (SurfaceIntent).
        private RenderTexture surface, surfaceMode;
        private IntPtr surfaceHandle, surfaceModeHandle;
        // Scatterer's ocean as a surface, its raytraced reflections, and those as the
        // ocean reads them.
        private RenderTexture oceanDepth, oceanSurface1, oceanSurface2, oceanTraced, oceanReflections;
        private IntPtr oceanDepthHandle, oceanSurface1Handle, oceanSurface2Handle, oceanTracedHandle;
        private CommandBuffer oceanCommand;
        private bool oceanAttached;
        // Whose sea takes the raytraced reflections: 0 none, 1 KSP's, 2 Scatterer's.
        private int oceanMode;
        // The sun's and the moons' discs added to Scatterer's ocean once it is drawn
        // (OceanDiscsEvent; ReDefinitionRtCapture.shader, fragOceanDiscs).
        private CommandBuffer oceanDiscs;
        private bool oceanDiscsAttached;
        private RtNative.FramePacket oceanPacket;
        // The targets' native textures, taken once as they are made: GetNativeTexturePtr
        // waits for the render thread.
        private IntPtr depthHandle, gbuffer0Handle, gbuffer1Handle, gbuffer2Handle, motionHandle, environmentHandle;
        private IntPtr sunHandle, diffuseHandle, specularHandle, forwardHandle, forwardRatioHandle;
        private int width, height;
        private uint frameIndex;
        private bool haveHistory;
        private bool gbufferReady;
        private Matrix4x4 previousWorldToClip;     // camera-relative, of the previous frame
        private RtPosition previousPosition;
        private Matrix4x4 renderedProjection;
        private RtPosition position;
        private readonly StringBuilder statusText = new StringBuilder(512);
        private int generation = -1;
        private int statusFrame;

        // The lighting shaders' flags: on around this camera's rendering, the sun's mask taking the raytraced
        // visibility, and which raytraced light to use (x diffuse, y reflections, z the night light kept).
        private static readonly int RtOn = Shader.PropertyToID("_ReDefinitionRtOn");
        private static readonly int RtSunMask = Shader.PropertyToID("_ReDefinitionRtSunMask");
        private static readonly int RtLight = Shader.PropertyToID("_ReDefinitionRtLight");
        // The raytraced diffuse light's colour (ReDefinitionDeferredReflections.shader). At 0 it is its
        // own. Up to 1, it takes that much of Deferred's limit on its ambient light's tint (its
        // ambientTint). From 1 to 2, it is more and more in the colour of Deferred's own ambient
        // light, its strength kept. From 2 to 3, it is more and more grey.
        // -redefinitionProbeRtAmbientTint <value> sets it for a probe's run.
        // It is the player's choice (RtKsp, RaytracingLightColour).
        internal Func<float> AmbientColour = () => 0f;
        private static readonly float probeAmbientTint = ProbeAmbientTint();

        // The light colour in effect: the probe's where given, otherwise the player's, 0 to 3.
        private float AmbientTintLimit
        {
            get { return probeAmbientTint >= 0f ? probeAmbientTint : Mathf.Clamp(AmbientColour(), 0f, 3f); }
        }

        // -redefinitionProbeRtAmbientTint's value, clamped to 0 to 3; -1 where it is not given.
        private static float ProbeAmbientTint()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            int at = System.Array.IndexOf(args, "-redefinitionProbeRtAmbientTint");
            float share;
            return at >= 0 && at + 1 < args.Length && float.TryParse(args[at + 1], System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out share) ? Mathf.Clamp(share, 0f, 3f) : -1f;
        }
        // The ocean's traced reflections for the deferred reflections, Scatterer's ocean's input of screen-space
        // reflections, and KSP's sea as a sphere (centre and radius; radius 0 where the sea is not KSP's).
        private static readonly int RtOcean = Shader.PropertyToID("_ReDefinitionRtOcean");
        private static readonly int SsrOutput = Shader.PropertyToID("ssrOutput");
        private static readonly int WaterSphereId = Shader.PropertyToID("_ReDefinitionWaterSphere");
        // Scatterer's ocean draws its G-buffer after the forward opaque pass and is shaded
        // after the opaque image effects (OceanCommandBuffer); Deferred's screen-space
        // reflections read that G-buffer before them, where this goes too.
        private const CameraEvent OceanEvent = CameraEvent.BeforeImageEffectsOpaque;
        // Scatterer's ocean is shaded after the opaque image effects, its atmosphere over it
        // after the transparent pass (OceanRenderingHook): between the two.
        private const CameraEvent OceanDiscsEvent = CameraEvent.BeforeForwardAlpha;
        private const int OceanDiscsPass = 11;
        // The surfaces written back into the G-buffer (ReDefinitionRtCapture.shader, fragSurface).
        private const int SurfacePass = 12;
        // The mark of a surface a mod made shinier after the G-buffer pass, into the alpha of the G-buffer 2's copy (RtParity's
        // sibling in the shader: ReDefinitionRtCapture.shader, fragWetFlag).
        private const int WetFlagPass = 17;
        private static readonly int G1NowId = Shader.PropertyToID("_ReDefinitionRtG1Now");
        private static readonly int G1EarlyId = Shader.PropertyToID("_ReDefinitionRtG1Early");
        private static readonly int G0NowId = Shader.PropertyToID("_ReDefinitionRtG0Now");
        private static readonly int G0EarlyId = Shader.PropertyToID("_ReDefinitionRtG0Early");
        private static readonly int WetAllId = Shader.PropertyToID("_ReDefinitionRtWetAll");
        // -redefinitionProbeWetAll: every surface marked as made shinier, to see what the rays make of it.
        private static readonly bool WetAll = CommandLine.Has("-redefinitionProbeWetAll");
        private const int MeadowPass = 16;
        private const int SurfaceViewPass = 13;
        // KSP's flat ambient light taken out of the light buffer where a surface's own
        // G-buffer pass put it (ReDefinitionRtCapture.shader, fragUnbakeAmbient).
        private const int UnbakeAmbientPass = 14;
        private CommandBuffer unbakeAmbient;
        private static readonly int FlatAmbientId = Shader.PropertyToID("_ReDefinitionFlatAmbient");
        private CommandBuffer surfaceView;
        private bool surfaceViewAttached;
        private static readonly int SurfaceId = Shader.PropertyToID("_ReDefinitionRtSurface");
        private static readonly int SurfaceModeId = Shader.PropertyToID("_ReDefinitionRtSurfaceMode");
        private static readonly int OceanFrameId = Shader.PropertyToID("_ReDefinitionOceanFrame");
        private static readonly int OceanScreenToWorldId = Shader.PropertyToID("_ReDefinitionOceanScreenToWorld");
        private static readonly int RtPrevViewProj = Shader.PropertyToID("_ReDefinitionRtPrevViewProj");
        private static readonly int RtTexel = Shader.PropertyToID("_ReDefinitionRtTexel");
        private static readonly int RtOffsetNear = Shader.PropertyToID("_ReDefinitionRtOffsetNear");
        private static readonly int RtOffsetFar = Shader.PropertyToID("_ReDefinitionRtOffsetFar");
        private static readonly int RtSun = Shader.PropertyToID("_ReDefinitionRtSun");
        private static readonly int RtDiffuse = Shader.PropertyToID("_ReDefinitionRtDiffuse");
        private static readonly int RtSpecular = Shader.PropertyToID("_ReDefinitionRtSpecular");
        private static readonly int RtCube = Shader.PropertyToID("_ReDefinitionRtCube");
        private static readonly int RtForward = Shader.PropertyToID("_ReDefinitionRtForward");
        private static readonly int RtForwardRatio = Shader.PropertyToID("_ReDefinitionRtForwardRatio");
        private static readonly int RtFrameCopy = Shader.PropertyToID("_ReDefinitionRtFrame");
        // ReDefinitionRtForwardLight.shader's pass that relights the frame by its own colour.
        private const int RelightPass = 4;
        private static readonly int RtCubeHdr = Shader.PropertyToID("_ReDefinitionRtCube_HDR");
        // The gather pass of the GPU's instances (GatherGpuInstances).
        private static readonly int GatherEye = Shader.PropertyToID("_Eye");
        private static readonly int GatherOut = Shader.PropertyToID("_Out");
        private static readonly int GatherCounts = Shader.PropertyToID("_Counts");
        private static readonly int GatherSource = Shader.PropertyToID("_Source");
        private static readonly int GatherCountIndex = Shader.PropertyToID("_CountIndex");
        private static readonly int GatherCapacity = Shader.PropertyToID("_Capacity");
        private static readonly int GatherFirst = Shader.PropertyToID("_First");
        private static readonly int GatherReach = Shader.PropertyToID("_Reach");

        // The camera and the frame packets; the packet ring, shared by every raytraced camera, made once.
        private void Awake()
        {
            cam = GetComponent<Camera>();
            packet = RtNative.NewFramePacket();
            oceanPacket = RtNative.NewFramePacket();
            if (ring == null)
                // Two packets a frame where the ocean's reflections are traced too.
                ring = new PacketRing(System.Runtime.InteropServices.Marshal.SizeOf(typeof(RtNative.FramePacket)), 16);
        }

        // Makes the materials, command buffers and flags and attaches them to the camera; where the proxy is not ready
        // or the shaders are missing, says why (Problem) and turns itself off again.
        private void OnEnable()
        {
            string why;
            if (!RtNative.Ready(out why))
            {
                Problem = "raytracing does not run: " + why;
                enabled = false;
                return;
            }
            Shader captureShader = ShaderSource("Hidden/ReDefinition/RtCapture");
            shadows = ShaderSource("Hidden/ReDefinition/ScreenSpaceShadows");
            reflections = ShaderSource("ReDefinition/Internal-DeferredReflections");
            if (captureShader == null || shadows == null)
            {
                Problem = "raytracing's shaders are missing from the shader bundle";
                enabled = false;
                return;
            }
            capture = new Material(captureShader) { hideFlags = HideFlags.HideAndDontSave };
            Scene.TextureReader = capture;
            command = new CommandBuffer { name = "ReDefinition raytracing" };
            oceanCommand = new CommandBuffer { name = "ReDefinition raytracing ocean reflections" };
            oceanDiscs = new CommandBuffer { name = "ReDefinition raytracing ocean discs" };

            // The flag the lighting shaders read: on around this camera's rendering,
            // off for every other camera. Forward rendering collects the sun's shadow
            // after the depth texture, deferred rendering after the G-buffer.
            flagOn = new CommandBuffer { name = "ReDefinition raytracing on" };
            flagOn.SetGlobalFloat(RtOn, 1f);
            flagOff = new CommandBuffer { name = "ReDefinition raytracing off" };
            flagOff.SetGlobalFloat(RtOn, 0f);
            cam.AddCommandBuffer(CameraEvent.BeforeDepthTexture, flagOn);
            cam.AddCommandBuffer(CameraEvent.BeforeGBuffer, flagOn);
            cam.AddCommandBuffer(CameraEvent.AfterEverything, flagOff);
            // The G-buffer is gone by OnRenderImage: copied as soon as it is filled, in the
            // camera's own command stream, where the copy belongs to this frame.
            gbufferCopy = new CommandBuffer { name = "ReDefinition raytracing G-buffer" };
            cam.AddCommandBuffer(CameraEvent.AfterGBuffer, gbufferCopy);
            unbakeAmbient = new CommandBuffer { name = "ReDefinition raytracing ambient taken out" };
            cam.AddCommandBuffer(CameraEvent.AfterGBuffer, unbakeAmbient);
            // Without Deferred's reflections pass to take it, the raytraced light goes into
            // the frame after the opaque geometry's light, before the transparent and the
            // atmosphere: after the forward opaque pass, or after Unity's deferred lighting,
            // where the forward opaque pass is left out when nothing is drawn forward.
            screenQuad = RtScreenQuad.Make("ReDefinition raytracing screen");
            Shader forwardShader = ShaderSource("Hidden/ReDefinition/RtForwardLight");
            if (forwardShader != null)
            {
                forwardMaterial = new Material(forwardShader) { hideFlags = HideFlags.HideAndDontSave };
                forwardLight = new CommandBuffer { name = "ReDefinition raytracing forward light" };
                forwardEvent = CameraEvent.AfterForwardOpaque;
                cam.AddCommandBuffer(forwardEvent, forwardLight);
            }
            maskOn = new CommandBuffer { name = "ReDefinition raytracing sun mask on" };
            maskOn.SetGlobalFloat(RtSunMask, 1f);
            maskOff = new CommandBuffer { name = "ReDefinition raytracing sun mask off" };
            maskOff.SetGlobalFloat(RtSunMask, 0f);
            fadeGrab = new CommandBuffer { name = "ReDefinition raytracing shadow fade" };
            fadeTexture = new RenderTexture(2, 1, 0, RenderTextureFormat.ARGBFloat)
            {
                name = "ReDefinition raytracing shadow fade",
                filterMode = FilterMode.Point,
            };
            fadeTexture.Create();
            fadeDone = ShadowFadeRead;
            fadeGrabbed = fadeReading = false;
            fadeScale = Vector4.zero;
            cam.depthTextureMode |= DepthTextureMode.Depth;
            haveHistory = false;
            Problem = null;
            Current = this;
        }

        // Takes everything off the camera, sets Unity's lighting shaders, the sun's shadow and the reflections back,
        // and has the proxy let go of what it held for this camera.
        private void OnDisable()
        {
            if (lateTrace != null) Destroy(lateTrace);
            lateTrace = null;
            HoldReflections(false);
            if (Current == this) Current = null;
            ReconstructionLease = -1;
            if (cam != null)
            {
                if (flagOn != null)
                {
                    cam.RemoveCommandBuffer(CameraEvent.BeforeDepthTexture, flagOn);
                    cam.RemoveCommandBuffer(CameraEvent.BeforeGBuffer, flagOn);
                }
                if (flagOff != null) cam.RemoveCommandBuffer(CameraEvent.AfterEverything, flagOff);
                if (gbufferCopy != null) cam.RemoveCommandBuffer(CameraEvent.AfterGBuffer, gbufferCopy);
                if (unbakeAmbient != null) cam.RemoveCommandBuffer(CameraEvent.AfterGBuffer, unbakeAmbient);
                DetachTrace();
                if (forwardLight != null) cam.RemoveCommandBuffer(forwardEvent, forwardLight);
            }
            MaskSun(null);
            maskOn?.Release();
            maskOff?.Release();
            maskOn = maskOff = null;
            fadeGrab?.Release();
            fadeGrab = null;
            if (fadeTexture != null)
            {
                fadeTexture.Release();
                Destroy(fadeTexture);
            }
            fadeTexture = null;
            Shader.SetGlobalFloat(RtOn, 0f);
            Shader.SetGlobalFloat(RtSunMask, 0f);
            RtLighting.Restore();
            Scene.Clear();
            // The proxy lets go of the denoiser's textures, the shared copies and the meshes
            // just released on the render thread, after the last frame sent
            // (Raytracing::OnRelease); without frames it would keep them.
            if (RtNative.RenderEvent != IntPtr.Zero)
            {
                var release = new CommandBuffer { name = "ReDefinition raytracing release" };
                release.IssuePluginEventAndData(RtNative.RenderEvent, RtNative.ReleaseEvent, IntPtr.Zero);
                Graphics.ExecuteCommandBuffer(release);
                release.Release();
            }
            ReleaseTargets();
            ReleaseOceanTargets();
            ReleaseGpuInstances();
            if (capture != null) Destroy(capture);
            capture = null;
            command?.Release();
            oceanCommand?.Release();
            oceanCommand = null;
            if (surfaceViewAttached && cam != null) cam.RemoveCommandBuffer(CameraEvent.AfterImageEffects, surfaceView);
            surfaceViewAttached = false;
            surfaceView?.Release();
            surfaceView = null;
            oceanDiscs?.Release();
            oceanDiscs = null;
            gbufferCopy?.Release();
            unbakeAmbient?.Release();
            unbakeAmbient = null;
            forwardLight?.Release();
            forwardLight = null;
            if (forwardMaterial != null) Destroy(forwardMaterial);
            if (screenQuad != null) Destroy(screenQuad);
            forwardMaterial = null;
            screenQuad = null;
            flagOn?.Release();
            flagOff?.Release();
            command = gbufferCopy = flagOn = flagOff = null;
        }

        // Runs before the camera culls. It puts the lighting shaders and the sun's mask in place and hands over the
        // scene's instances of this frame. It sets up the forward light and the G-buffer's copy, and carries the
        // previous frame's results to this frame's world for the lighting.
        private void OnPreCull()
        {
            KeepLateTrace();
            if (capture == null) return;
            RtLighting.Keep(Settings.Shadows ? shadows : null, Settings.Diffuse || Settings.Reflections ? reflections : null);
            if (SunLight != null) MaskSun(SunLight());
            else Shader.SetGlobalFloat(RtSunMask, 1f);
            GrabShadowFade();
            HoldReflections(!RtLighting.ReflectionsReplaced && Settings.Reflections);

            // The service's line now and then; its generation, which changes with its
            // device, every frame, and handed back as the one this frame's meshes are for.
            if (generation < 0 || ++statusFrame >= 30)
            {
                statusFrame = 0;
                statusText.Length = 0;
                RtNative.KspRtStatus(statusText, statusText.Capacity);
            }
            generation = (int)RtNative.KspRtGeneration();
            RtNative.KspRtHandOverFor((uint)generation);
            position = RtPosition.From(transform.position);
            Scene.Update(position, generation, frameIndex);

            // The raytraced light into the frame, where Deferred's reflections pass does not
            // take it.
            if (forwardLight != null)
            {
                CameraEvent wanted = cam.actualRenderingPath == RenderingPath.DeferredShading
                    ? CameraEvent.AfterLighting : CameraEvent.AfterForwardOpaque;
                if (wanted != forwardEvent)
                {
                    cam.RemoveCommandBuffer(forwardEvent, forwardLight);
                    forwardEvent = wanted;
                    cam.AddCommandBuffer(forwardEvent, forwardLight);
                }
                forwardLight.Clear();
                bool forwardFrame = !RtLighting.ReflectionsReplaced;
                // Traced before the lighting, the light is this frame's own, a cut's too
                // (traced afresh); traced after the frame, the frame before's, which a cut
                // leaves behind.
                bool traced = TraceBeforeLighting || haveHistory && !Cut();
                if (forwardFrame && traced && forward != null && (Settings.Diffuse || Settings.Reflections))
                {
                    Shader.SetGlobalTexture(RtForward, forward);
                    if (forwardEvent == CameraEvent.AfterForwardOpaque)
                    {
                        // Without a G-buffer: the frame relit by its own colour, which a pass
                        // reads from a copy (Raytracing.hlsl, ForwardLight).
                        Shader.SetGlobalTexture(RtForwardRatio, forwardRatio);
                        forwardLight.GetTemporaryRT(RtFrameCopy, -1, -1, 0, FilterMode.Point,
                                                    cam.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default);
                        forwardLight.Blit(BuiltinRenderTextureType.CameraTarget, RtFrameCopy);
                        forwardLight.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                        forwardLight.DrawMesh(screenQuad, Matrix4x4.identity, forwardMaterial, 0, RelightPass);
                        forwardLight.ReleaseTemporaryRT(RtFrameCopy);
                    }
                    else
                    {
                        int first = forwardEvent == CameraEvent.AfterLighting && !cam.allowHDR ? 2 : 0;
                        forwardLight.DrawMesh(screenQuad, Matrix4x4.identity, forwardMaterial, 0, first);
                        forwardLight.DrawMesh(screenQuad, Matrix4x4.identity, forwardMaterial, 0, first + 1);
                    }
                }
            }

            // The G-buffer's copy at this frame's size, which the previous frame's
            // OnRenderImage found; at a new size, from the next frame on. Only for the trace
            // after the frame: the trace in the frame copies it itself (TraceThisFrame).
            gbufferCopy.Clear();
            // The trace in the frame clears this buffer and records the copies itself (TraceThisFrame).
            if (!TraceBeforeLighting) RecordEarlyGBuffer1();
            if (!TraceBeforeLighting && gbuffer0 != null && cam.actualRenderingPath == RenderingPath.DeferredShading)
            {
                gbufferCopy.Blit(BuiltinRenderTextureType.GBuffer0, gbuffer0);
                gbufferCopy.Blit(BuiltinRenderTextureType.GBuffer1, gbuffer1);
                gbufferCopy.Blit(BuiltinRenderTextureType.GBuffer2, gbuffer2);
            }

            // Where the raytraced diffuse light replaces the ambient light: the flat ambient
            // light a surface's own G-buffer pass added taken out first, so that it does not
            // keep KSP's light of the day at night. The light buffer holds linear values only
            // in HDR; in LDR Unity keeps it encoded.
            unbakeAmbient.Clear();
            if (Settings.Diffuse && cam.allowHDR && cam.actualRenderingPath == RenderingPath.DeferredShading)
            {
                unbakeAmbient.GetTemporaryRT(RtFrameCopy, -1, -1, 0, FilterMode.Point, RenderTextureFormat.DefaultHDR);
                unbakeAmbient.Blit(BuiltinRenderTextureType.CameraTarget, RtFrameCopy);
                unbakeAmbient.SetGlobalColor(FlatAmbientId, RenderSettings.ambientLight);
                unbakeAmbient.Blit(RtFrameCopy, BuiltinRenderTextureType.CameraTarget, capture, UnbakeAmbientPass);
                unbakeAmbient.ReleaseTemporaryRT(RtFrameCopy);
            }

            // The previous frame's results, carried to this frame's world.
            bool valid = haveHistory && !Cut();
            Shader.SetGlobalVector(RtLight, new Vector4(valid && Settings.Diffuse ? 1f : 0f,
                                                        valid && Settings.Reflections ? 1f : 0f, Mathf.Clamp01(NightLight()),
                                                        AmbientTintLimit));
            if (valid)
            {
                RtPosition near = WorldShift();
                RtPosition far = WorldShiftFar != null ? WorldShiftFar() : near;
                Shader.SetGlobalMatrix(RtPrevViewProj, previousWorldToClip);
                Shader.SetGlobalVector(RtOffsetNear, new Vector4((float)(near.x - previousPosition.x),
                                                                 (float)(near.y - previousPosition.y),
                                                                 (float)(near.z - previousPosition.z), 0f));
                Shader.SetGlobalVector(RtOffsetFar, new Vector4((float)(far.x - previousPosition.x),
                                                                (float)(far.y - previousPosition.y),
                                                                (float)(far.z - previousPosition.z), 0f));
                if (sun != null)
                {
                    Shader.SetGlobalTexture(RtSun, sun);
                    Shader.SetGlobalVector(RtTexel, new Vector4(1f / sun.width, 1f / sun.height, sun.width, sun.height));
                }
                if (diffuse != null) Shader.SetGlobalTexture(RtDiffuse, diffuse);
                if (specular != null) Shader.SetGlobalTexture(RtSpecular, specular);
            }
            else
            {
                // Nothing to carry: Unity's own lighting until the first raytraced frame.
                Shader.SetGlobalMatrix(RtPrevViewProj, Matrix4x4.zero);
            }
        }

        // The projection the frame renders with, after BeforeTrace jittered it, and the trace in the frame where it is
        // wanted; otherwise the trace taken off the camera.
        private void OnPreRender()
        {
            renderedProjection = cam.projectionMatrix;
            if (TraceBeforeLighting && capture != null)
            {
                if (BeforeTrace != null)
                {
                    BeforeTrace();
                    renderedProjection = cam.projectionMatrix;
                }
                TraceThisFrame();
            }
            else DetachTrace();
        }

        // Takes the trace in the frame, and the ocean's, off the camera.
        private void DetachTrace()
        {
            TraceOcean(false, false);
            if (!traceAttached) return;
            cam.RemoveCommandBuffer(traceEvent, command);
            traceAttached = false;
        }

        // The G-buffer 0 and 1 as the G-buffer pass leaves them, copied then, before a mod's later pass changes
        // them (the wet flag, WetFlagPass).
        private void RecordEarlyGBuffer1()
        {
            if (gbuffer1Early == null || cam.actualRenderingPath != RenderingPath.DeferredShading) return;
            gbufferCopy.Blit(BuiltinRenderTextureType.GBuffer0, gbuffer0Early);
            gbufferCopy.Blit(BuiltinRenderTextureType.GBuffer1, gbuffer1Early);
        }

        // The trace in the camera's own command stream, between its G-buffer or depth
        // texture and its lighting, with the jitter it renders with; the lighting shaders
        // then read its results where they are, for this frame.
        private void TraceThisFrame()
        {
            bool deferredPath = cam.actualRenderingPath == RenderingPath.DeferredShading;
            // Deferred: before the reflections pass, through which the diffuse light and the
            // reflections enter (RtLighting), which Unity runs before the lighting.
            CameraEvent wanted = deferredPath ? CameraEvent.BeforeReflections : CameraEvent.AfterDepthTexture;
            if (!traceAttached || wanted != traceEvent)
            {
                DetachTrace();
                traceEvent = wanted;
                traceAttached = true;
            }
            // The last at its event, each frame: the G-buffer it copies is then what the mods before it left, as
            // EVE's wet effect at the reflections' event leaves it, and its surfaces are written back over that.
            cam.RemoveCommandBuffer(traceEvent, command);
            cam.AddCommandBuffer(traceEvent, command);
            gbufferCopy.Clear();
            EnsureTargets(cam.pixelWidth, cam.pixelHeight);
            RecordEarlyGBuffer1();

            command.Clear();
            RenderTargetIdentifier source = BuiltinRenderTextureType.CameraTarget;
            if (deferredPath)
            {
                command.Blit(BuiltinRenderTextureType.GBuffer0, gbuffer0);
                command.Blit(BuiltinRenderTextureType.GBuffer1, gbuffer1);
                command.Blit(BuiltinRenderTextureType.GBuffer2, gbuffer2);
                // The pixels a mod made shinier since the G-buffer pass, marked in the alpha of the normals' copy.
                command.SetGlobalTexture(G1NowId, gbuffer1);
                command.SetGlobalTexture(G1EarlyId, gbuffer1Early);
                command.SetGlobalTexture(G0NowId, gbuffer0);
                command.SetGlobalTexture(G0EarlyId, gbuffer0Early);
                command.SetGlobalFloat(WetAllId, WetAll ? 1f : 0f);
                command.SetRenderTarget(gbuffer2);
                command.DrawMesh(screenQuad, Matrix4x4.identity, capture, 0, WetFlagPass);
            }
            command.Blit(source, depth, capture, 0);
            Probe.GpuProfiler.Mark(command, "ReDefinition raytracing: G-buffer and depth copies");
            // Unity draws the motion vectors after the lighting: without them here, the
            // denoiser follows the camera alone.
            Texture cube = Environment();
            if (cube != null)
            {
                command.SetGlobalTexture(RtCube, cube);
                command.SetGlobalVector(RtCubeHdr, new Vector4(1f, 1f, 0f, 0f));
                command.Blit(source, environment, capture, 5);
            }
            bool withGpu = GatherGpuInstances();
            Matrix4x4 worldToClip = FillPacket(deferredPath, false, cube != null);
            if (withGpu) packet.Inputs[RtNative.InputInstances] = gpuInstancesHandle;
            ring.Issue(command, RtNative.RenderEvent, RtNative.FrameEvent, ref packet);
            Probe.GpuProfiler.Mark(command, "ReDefinition raytracing: trace (inputs to D3D12, results back)");
            // The surfaces as their materials mean them into the G-buffer, before the
            // reflections and the lighting read it.
            bool intent = (packet.Flags & RtNative.FlagSurfaceIntent) != 0;
            if (intent)
            {
                command.SetGlobalTexture(SurfaceId, surface);
                command.SetGlobalTexture(SurfaceModeId, surfaceMode);
                command.SetRenderTarget(BuiltinRenderTextureType.GBuffer1);
                command.DrawMesh(screenQuad, Matrix4x4.identity, capture, 0, SurfacePass);
                // The terrain past Parallax's grass in a meadow's shade, and the space centre's lawn lifted
                // to the terrain's albedo, into their albedo.
                if (packet.ShadowFade[ShadowFadeMeadowRange] != 0f || packet.ShadowFade[ShadowFadeVesselRange] != 0f
                    || RtMaterialIntent.LawnAsTerrain)
                {
                    command.SetRenderTarget(BuiltinRenderTextureType.GBuffer0);
                    command.DrawMesh(screenQuad, Matrix4x4.identity, capture, 0, MeadowPass);
                }
                Probe.GpuProfiler.Mark(command, "ReDefinition raytracing: surface intent");
            }
            ShowSurfaces(intent && SurfaceView);
            // KSP's sea where the reflections' shader is ours, which composes it, or without a
            // G-buffer, where the forward light's relight pass does
            // (ReDefinitionRtForwardLight.shader).
            bool stockSea = StockWater() && (RtLighting.ReflectionsReplaced || !deferredPath);
            TraceOcean(Settings.Reflections && (deferredPath ? OceanReflections() || stockSea : stockSea), stockSea, command);

            // This frame's results for this frame's lighting: the same pixel. The matrix is
            // relative to the camera, the shaders' positions are Unity's world.
            var toCamera = new Vector4((float)-position.x, (float)-position.y, (float)-position.z, 0f);
            Shader.SetGlobalMatrix(RtPrevViewProj, worldToClip);
            Shader.SetGlobalVector(RtOffsetNear, toCamera);
            Shader.SetGlobalVector(RtOffsetFar, toCamera);
            Shader.SetGlobalVector(RtLight, new Vector4(Settings.Diffuse ? 1f : 0f, Settings.Reflections ? 1f : 0f,
                                                        Mathf.Clamp01(NightLight()), AmbientTintLimit));
            if (sun != null)
            {
                Shader.SetGlobalTexture(RtSun, sun);
                Shader.SetGlobalVector(RtTexel, new Vector4(1f / sun.width, 1f / sun.height, sun.width, sun.height));
            }
            if (diffuse != null) Shader.SetGlobalTexture(RtDiffuse, diffuse);
            if (specular != null) Shader.SetGlobalTexture(RtSpecular, specular);

            previousWorldToClip = worldToClip;
            previousPosition = position;
            haveHistory = true;
            frameIndex++;
        }

        // The surfaces' overlay over the finished picture, after the image effects
        // (ReDefinitionRtCapture.shader, fragSurfaceView).
        private void ShowSurfaces(bool wanted)
        {
            if (wanted == surfaceViewAttached) return;
            surfaceViewAttached = wanted;
            if (surfaceView == null)
            {
                surfaceView = new CommandBuffer { name = "ReDefinition raytracing surfaces' overlay" };
                surfaceView.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                surfaceView.DrawMesh(screenQuad, Matrix4x4.identity, capture, 0, SurfaceViewPass);
            }
            if (wanted) cam.AddCommandBuffer(CameraEvent.AfterImageEffects, surfaceView);
            else cam.RemoveCommandBuffer(CameraEvent.AfterImageEffects, surfaceView);
        }

        // After this camera's scene, before its OnPostRender, where the sun's colour is
        // still the one it drew with.
        private void OnRenderObject()
        {
            if (Camera.current != cam) return;
            drawnSunColour = SunColor();
            drawnSunFrame = Time.frameCount;
        }

        // The projection as the GPU used it, into a render texture, times the camera's
        // rotation: camera-relative world to clip.
        private Matrix4x4 WorldToClipRelative()
        {
            Matrix4x4 view = cam.worldToCameraMatrix;
            view.m03 = 0f;
            view.m13 = 0f;
            view.m23 = 0f;
            return GL.GetGPUProjectionMatrix(renderedProjection, true) * view;
        }

        // The camera: this frame's matrices and the previous ones, the camera for AMD's denoiser, the jitter and the
        // depth range. It returns the frame's camera-relative world to clip.
        private Matrix4x4 FillView()
        {
            Matrix4x4 worldToClip = WorldToClipRelative();
            Write(worldToClip, packet.WorldToClip);
            Write(StreamlineCamera.Invert(worldToClip), packet.ClipToWorld);
            if (haveHistory)
            {
                RtPosition shift = WorldShift();
                Matrix4x4 toPrevious = Translation(position.x + shift.x - previousPosition.x,
                                                   position.y + shift.y - previousPosition.y,
                                                   position.z + shift.z - previousPosition.z);
                Write(previousWorldToClip * toPrevious, packet.PrevWorldToClip);
                // What the bodies carry moved by their shift (offset + nonFrame under
                // Krakensbane), the camera and the vessels by the near one.
                RtPosition far = WorldShiftFar != null ? WorldShiftFar() : shift;
                Matrix4x4 toPreviousFar = Translation(position.x + far.x - previousPosition.x,
                                                      position.y + far.y - previousPosition.y,
                                                      position.z + far.z - previousPosition.z);
                Write(previousWorldToClip * toPreviousFar, packet.PrevWorldToClipFar);
            }
            else
            {
                Write(worldToClip, packet.PrevWorldToClip);
                Write(worldToClip, packet.PrevWorldToClipFar);
            }

            // The camera for AMD's denoiser: its view looking down +z, as StreamlineCamera
            // has it.
            Matrix4x4 view = cam.worldToCameraMatrix;
            view.m03 = 0f;
            view.m13 = 0f;
            view.m23 = 0f;
            StreamlineCamera.WriteRowMajor(StreamlineCamera.FlipZ() * view, packet.WorldToView);
            Matrix4x4 unjittered = UnjitteredProjection != null ? UnjitteredProjection() : cam.projectionMatrix;
            StreamlineCamera.WriteRowMajor(GL.GetGPUProjectionMatrix(unjittered, true) * StreamlineCamera.FlipZ(), packet.ViewToClip);
            if (haveHistory)
            {
                RtPosition moved = WorldShift();
                packet.CameraDelta[0] = (float)(previousPosition.x - position.x - moved.x);
                packet.CameraDelta[1] = (float)(previousPosition.y - position.y - moved.y);
                packet.CameraDelta[2] = (float)(previousPosition.z - position.z - moved.z);
            }
            else
                packet.CameraDelta[0] = packet.CameraDelta[1] = packet.CameraDelta[2] = 0f;
            Vector2 jitter = Jitter();
            packet.Jitter[0] = jitter.x;
            packet.Jitter[1] = jitter.y;
            packet.DepthRange[0] = cam.nearClipPlane;
            packet.DepthRange[1] = cam.farClipPlane;
            return worldToClip;
        }

        // The sun: its direction, its colour, how much of it Unity's shadows take and how they fade out.
        private void FillSun(bool deferred)
        {
            Vector4 towards = Sun();
            Vector3 direction = new Vector3(towards.x, towards.y, towards.z).normalized;
            packet.SunDirection[0] = direction.x;
            packet.SunDirection[1] = direction.y;
            packet.SunDirection[2] = direction.z;
            packet.SunDirection[3] = towards.w;
            Color sunColour = drawnSunFrame == Time.frameCount ? drawnSunColour : SunColor();
            TracedSunColour = sunColour;
            packet.SunColor[0] = sunColour.r;
            packet.SunColor[1] = sunColour.g;
            packet.SunColor[2] = sunColour.b;
            // How much of the sun Unity's shadows take: its light's shadow strength (KSP's sun
            // 0.75), for the forward light's estimate of what Unity shaded with.
            Light sunLight = SunLight != null ? SunLight() : null;
            packet.SunColor[SunShadowStrength] = sunLight != null && sunLight.shadows != LightShadows.None ? sunLight.shadowStrength : 1f;
            // And how it fades them out towards the shadow distance, its centre turned with
            // the camera.
            bool fades = !deferred && fadeScale.x > 0f;
            Vector3 fadeAt = fades ? cam.cameraToWorldMatrix.MultiplyVector(fadeCentre) : Vector3.zero;
            packet.ShadowFade[0] = fadeAt.x;
            packet.ShadowFade[1] = fadeAt.y;
            packet.ShadowFade[2] = fadeAt.z;
            packet.ShadowFade[3] = fades ? fadeCentre.w : 0f;
            packet.ShadowFade[4] = fades ? fadeScale.x : 0f;
            packet.ShadowFade[5] = fades ? fadeScale.y : 0f;
        }

        // The frame packet for this frame. It holds the view and the previous one, the inputs, the sun, sky, lights,
        // clouds, sea and settings, and which passes to run. The argument `deferred` is set where the G-buffer was
        // copied, the others where motion vectors and the sky's cube were. It returns the frame's camera-relative
        // world to clip.
        private Matrix4x4 FillPacket(bool deferred, bool withMotion, bool withEnvironment)
        {
            Matrix4x4 worldToClip = FillView();
            FillSun(deferred);
            // The meadow's shade on the ground past Parallax's grass (RtCommon.hlsli, MeadowShade): how far
            // the grass is drawn, and how much light a meadow's own blades keep from it.
            float grassRange = RtKspMods.ParallaxGrassRange(out float vesselGrassRange);
            // Where Parallax draws grass about the vessel, that is the meadow, and the grass about the camera
            // not: Kerbin's tall grass, 400 m about the camera, stands too sparse to shade the ground (the sun's
            // visibility 0.98 on the terrain it stands on past the main grass, 2026-10-25), which kept the
            // terrain near the camera brighter than further off, and the more the nearer the camera came.
            // Its blades the camera sees as such end where the farthest level of detail starts: the shade takes the
            // ground from there, as it does past the range.
            if (vesselGrassRange > 0f) grassRange = RtKspMods.ParallaxGrassLodStart;
            // -redefinitionProbeMeadowFrom <metres>: the meadow's shade from there on instead of the grass's range.
            packet.ShadowFade[6] = (grassRange > 0f || vesselGrassRange > 0f) && MeadowFrom != 0f ? MeadowFrom : grassRange;
            // -redefinitionProbeMeadowShade <0..1>: another shade, to see where it lies.
            packet.ShadowFade[7] = MeadowShadeProbe > 0f ? MeadowShadeProbe : MeadowShade;
            // The grass Parallax draws about the vessel: the vessel relative to the camera, and how far.
            Vector3 vessel = vesselGrassRange > 0f ? RtKspMods.ParallaxVessel(transform.position) - transform.position : Vector3.zero;
            packet.ShadowFade[8] = vessel.x;
            packet.ShadowFade[9] = vessel.y;
            packet.ShadowFade[10] = vessel.z;
            packet.ShadowFade[11] = MeadowFrom != 0f ? 0f : vesselGrassRange;
            Color sky = SkyColor();
            packet.SkyColor[0] = sky.r;
            packet.SkyColor[1] = sky.g;
            packet.SkyColor[2] = sky.b;
            packet.SkyColor[3] = Mathf.Clamp01(SkyNeutral());
            bool composite = !RtLighting.ReflectionsReplaced;
            FillAmbient(composite);
            FillParams(deferred, withEnvironment);
            bool stockWater = FillLights();
            FillBody(stockWater);
            uint signals = FillSignals(deferred, composite);
            FillFlags(deferred, withMotion);
            FillInputs(deferred, withMotion, withEnvironment, signals);
            return worldToClip;
        }

        // Unity's ambient light, as it shades by itself; none where the raytraced light replaces it.
        private void FillAmbient(bool composite)
        {
            for (int i = 0; i < 12; i++) packet.Ambient[i] = 0f;
            if (composite)
            {
                AmbientProbe(transform.position).Evaluate(AmbientDirections, ambientValues);
                for (int i = 0; i < 3; i++)
                {
                    packet.Ambient[i * 4] = ambientValues[i].r;
                    packet.Ambient[i * 4 + 1] = ambientValues[i].g;
                    packet.Ambient[i * 4 + 2] = ambientValues[i].b;
                }
                packet.Ambient[3] = QualitySettings.activeColorSpace == ColorSpace.Gamma ? 1f : 0f;
            }
        }

        // The rays' settings: the occlusion, the bounces, how far the rays go, the clamp and which inputs there are.
        private void FillParams(bool deferred, bool withEnvironment)
        {
            packet.Params[ParamOcclusion] = Settings.OcclusionDistance;
            packet.Params[ParamBounces] = Settings.Bounces;
            packet.Params[ParamDiffuseReach] = RayReach;
            packet.Params[ParamSpecularReach] = RayReach;
            packet.Params[ParamClamp] = Settings.Clamp;
            packet.Params[ParamEnvironment] = withEnvironment ? 1f : 0f;
            packet.Params[ParamTextures] = 1f;
            packet.Params[ParamGBuffer] = deferred ? 1f : 0f;
        }

        // The bodies lit by the sun as lights, and the least light a surface gets. It returns whether the sea is KSP's own.
        private bool FillLights()
        {
            for (int i = 0; i < 16; i++) packet.Lights[i] = 0f;
            for (int i = 0; i < 8; i++) packet.LightSurface[i] = 0f;
            packet.LightParams[0] = Mathf.Clamp(Lights(packet.Lights, packet.LightSurface), 0, 2);
            // Unity's colours are gamma-space values in gamma space; the light transport
            // takes them to linear (RtCommon.hlsli, InputColor).
            packet.LightParams[1] = QualitySettings.activeColorSpace == ColorSpace.Gamma ? 1f : 0f;
            packet.LightParams[2] = Mathf.Max(SunMirror(), 0f);
            bool stockWater = StockWater();
            packet.LightParams[3] = stockWater ? 1f : 0f;
            // KSP's flat ambient light, as Deferred takes it (RefreshLegacyAmbient).
            Color night = RenderSettings.ambientLight * Mathf.Clamp01(NightLight());
            packet.NightLight[0] = night.r;
            packet.NightLight[1] = night.g;
            packet.NightLight[2] = night.b;
            return stockWater;
        }

        // The main body: its albedo, the time of its sea's waves, its sphere and the sea's frame.
        private void FillBody(bool stockWater)
        {
            Color planet = PlanetAlbedo();
            packet.Planet[0] = planet.r;
            packet.Planet[1] = planet.g;
            packet.Planet[2] = planet.b;
            // The time for the waves of KSP's own sea (RtCommon.hlsli, StockWaves), within a
            // day: the waves jump where it starts again, and a float still counts 8 ms by
            // its end, a ripple's move of millimetres.
            packet.Planet[PlanetWaveTime] = WaveTimeHeld ?? Time.time % 86400f;
            Vector4 body = Body();
            // The sea's sphere for the reflections' shader, in Unity's world: none but for
            // KSP's own sea.
            Shader.SetGlobalVector(WaterSphereId, stockWater ? body : Vector4.zero);
            packet.BodySphere[0] = (float)(body.x - position.x);
            packet.BodySphere[1] = (float)(body.y - position.y);
            packet.BodySphere[2] = (float)(body.z - position.z);
            packet.BodySphere[3] = body.w;
            for (int i = 0; i < 12; i++) packet.Sea[i] = 0f;
            Vector3 seaOrigin, seaEast, seaNorth;
            if (!SeaFrameLeftOut && Sea(out seaOrigin, out seaEast, out seaNorth))
            {
                packet.Sea[0] = seaOrigin.x;
                packet.Sea[1] = seaOrigin.y;
                packet.Sea[2] = seaOrigin.z;
                packet.Sea[4] = seaEast.x;
                packet.Sea[5] = seaEast.y;
                packet.Sea[6] = seaEast.z;
                packet.Sea[8] = seaNorth.x;
                packet.Sea[9] = seaNorth.y;
                packet.Sea[10] = seaNorth.z;
            }
        }

        // The passes to trace this frame; the parity check's, where it runs, among them. It returns the signals.
        private uint FillSignals(bool deferred, bool composite)
        {
            // The sun's pass always: its output keeps the view depth the next frame finds
            // its pixels by.
            uint signals = RtNative.SignalSun;
            if (Settings.Diffuse) signals |= RtNative.SignalDiffuse;
            if (Settings.Reflections) signals |= RtNative.SignalSpecular;
            // Where Deferred's reflections pass does not take it -- forward rendering, or
            // Unity's own deferred lighting --, the light to add to the frame; it reads
            // the diffuse light, which replaces the flat ambient light.
            if (composite && (Settings.Diffuse || Settings.Reflections))
                signals |= RtNative.SignalForwardLight | RtNative.SignalDiffuse;
            parityFlag = 0u;
            if (ParityProbe && deferred && parityHandle != IntPtr.Zero)
            {
                if (parityAsked)
                {
                    // The frame that traced it is done: its output is in the texture.
                    parityAsked = false;
                    parityPending = true;
                    AsyncGPUReadback.Request(parity, 0, TextureFormat.RGBAFloat, ParityRead);
                }
                else if (!parityPending && Time.realtimeSinceStartup - parityAt > 5f)
                {
                    parityAt = Time.realtimeSinceStartup;
                    parityAsked = true;
                    signals |= RtNative.SignalPrimaryCheck;
                    parityFlag = RtNative.FlagAlphaCheck;
                }
            }
            packet.Signals = signals;
            LastSignals = signals;
            return signals;
        }

        // The frame's index and size and the flags, and the guides Ray Reconstruction asks for.
        private void FillFlags(bool deferred, bool withMotion)
        {
            packet.FrameIndex = frameIndex;
            packet.Width = (uint)width;
            packet.Height = (uint)height;
            // The denoiser's history stood still while Ray Reconstruction ran: afresh when
            // it comes back.
            bool reconstruct = ReconstructionLease >= Time.frameCount - 1;
            bool switched = reconstruct != reconstructedBefore;
            reconstructedBefore = reconstruct;
            packet.Flags = RtNative.FlagSameFrame | (Settings.Denoise && !reconstruct ? RtNative.FlagDenoise : 0u)
                           | (!haveHistory || Cut() || switched ? RtNative.FlagReset : 0u)
                           | (withMotion ? RtNative.FlagMotionVectors : 0u)
                           | (reconstruct ? RtNative.FlagGuides : 0u)
                           | (LegacySmoothness() && !DeferredSmoothness ? RtNative.FlagLegacySmoothness : 0u)
                           | (SurfaceIntent(deferred) ? RtNative.FlagSurfaceIntent : 0u)
                           | (CloudShadowCheck ? RtNative.FlagCloudShadow : 0u) | parityFlag;
            if (reconstruct)
            {
                GuidesFrame = Time.frameCount;
                GuidesIndex = frameIndex;
                Array.Copy(packet.ClipToWorld, GuidesClipToWorld, 16);
            }
        }

        // The textures the proxy reads and writes this frame.
        private void FillInputs(bool deferred, bool withMotion, bool withEnvironment, uint signals)
        {
            for (int i = 0; i < 8; i++)
            {
                packet.Inputs[i] = IntPtr.Zero;
                packet.Outputs[i] = IntPtr.Zero;
            }
            packet.Inputs[RtNative.InputDepth] = depthHandle;
            if (deferred)
            {
                // Without a G-buffer the proxy takes each pixel's surface from its rays.
                packet.Inputs[RtNative.InputGBuffer0] = gbuffer0Handle;
                packet.Inputs[RtNative.InputGBuffer1] = gbuffer1Handle;
                packet.Inputs[RtNative.InputGBuffer2] = gbuffer2Handle;
            }
            if (withMotion) packet.Inputs[RtNative.InputMotion] = motionHandle;
            if (withEnvironment) packet.Inputs[RtNative.InputEnvironment] = environmentHandle;
            IntPtr clouds = Clouds(packet.Clouds);
            if (clouds != IntPtr.Zero) packet.Inputs[RtNative.InputClouds] = clouds;
            else for (int i = 0; i < packet.Clouds.Length; i++) packet.Clouds[i] = 0f;
            if ((signals & RtNative.SignalSun) != 0) packet.Outputs[RtNative.OutputSun] = sunHandle;
            if ((signals & RtNative.SignalDiffuse) != 0) packet.Outputs[RtNative.OutputDiffuse] = diffuseHandle;
            if ((signals & RtNative.SignalSpecular) != 0)
                packet.Outputs[RtNative.OutputSpecular] = specularHandle;
            if ((signals & RtNative.SignalForwardLight) != 0)
            {
                packet.Outputs[RtNative.OutputForwardLight] = forwardHandle;
                if (!deferred) packet.Outputs[RtNative.OutputForwardRatio] = forwardRatioHandle;
            }
            if ((packet.Flags & RtNative.FlagSurfaceIntent) != 0)
            {
                packet.Outputs[RtNative.OutputSurface] = surfaceHandle;
                packet.Outputs[RtNative.OutputSurfaceMode] = surfaceModeHandle;
            }
            if ((signals & RtNative.SignalPrimaryCheck) != 0)
            {
                // The parity frame: its own textures take the check's output, and the surfaces' slots with it.
                packet.Outputs[RtNative.OutputPrimaryCheck] = parityHandle;
                packet.Outputs[RtNative.OutputSurface] = parityRaysHandle;
                packet.Outputs[RtNative.OutputSurfaceMode] = parityRasterHandle;
            }
        }

        // The surfaces as their materials mean them (RtMaterialIntent; RtCommon.hlsli,
        // SurfaceIntent): in the game, with a G-buffer and the sun's pass, which finds them.
        private bool SurfaceIntent(bool deferred)
        {
            return deferred && TraceBeforeLighting && LegacySmoothness() && Settings.Shadows && !SurfacesAsDeferred;
        }

        // A translation by x, y, z, in single precision.
        private static Matrix4x4 Translation(double x, double y, double z)
        {
            Matrix4x4 m = Matrix4x4.identity;
            m.m03 = (float)x;
            m.m13 = (float)y;
            m.m23 = (float)z;
            return m;
        }

        // Rows first, as the proxy takes them: Unity's [row, column].
        internal static void Write(Matrix4x4 m, float[] target)
        {
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                    target[row * 4 + column] = m[row, column];
        }

        // The frame's targets at the given size, with their native textures; made anew, and the history dropped, where
        // the size changed or Unity let one go.
        private void EnsureTargets(int newWidth, int newHeight)
        {
            // Made again where Unity let a texture go, as its native handle goes with it.
            if (depth != null && width == newWidth && height == newHeight && depth.IsCreated() && sun.IsCreated()) return;
            ReleaseTargets();
            width = newWidth;
            height = newHeight;
            depth = Target(RenderTextureFormat.RFloat);
            gbuffer0 = Target(RenderTextureFormat.ARGB32);
            gbuffer1 = Target(RenderTextureFormat.ARGB32);
            gbuffer0Early = Target(RenderTextureFormat.ARGB32);
            gbuffer1Early = Target(RenderTextureFormat.ARGB32);
            gbuffer2 = Target(RenderTextureFormat.ARGB2101010);
            motion = Target(RenderTextureFormat.RGHalf);
            environment = new RenderTexture(256, 128, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = "ReDefinition raytracing sky",
                wrapMode = TextureWrapMode.Repeat,
            };
            environment.Create();
            // Its w keeps the view depth, past half precision's 65 504 m in orbit.
            sun = Target(RenderTextureFormat.ARGBFloat);
            diffuse = Target(RenderTextureFormat.ARGBHalf);
            specular = Target(RenderTextureFormat.ARGBHalf);
            forward = Target(RenderTextureFormat.ARGBHalf);
            forwardRatio = Target(RenderTextureFormat.ARGBHalf);
            surface = Target(RenderTextureFormat.ARGBHalf);
            surfaceMode = Target(RenderTextureFormat.ARGBHalf);
            parity = ParityProbe ? Target(RenderTextureFormat.ARGBFloat) : null;
            parityRays = ParityProbe ? Target(RenderTextureFormat.ARGBFloat) : null;
            parityRaster = ParityProbe ? Target(RenderTextureFormat.ARGBFloat) : null;
            parityHandle = parity != null ? parity.GetNativeTexturePtr() : IntPtr.Zero;
            parityRaysHandle = parityRays != null ? parityRays.GetNativeTexturePtr() : IntPtr.Zero;
            parityRasterHandle = parityRaster != null ? parityRaster.GetNativeTexturePtr() : IntPtr.Zero;
            depthHandle = depth.GetNativeTexturePtr();
            gbuffer0Handle = gbuffer0.GetNativeTexturePtr();
            gbuffer1Handle = gbuffer1.GetNativeTexturePtr();
            gbuffer2Handle = gbuffer2.GetNativeTexturePtr();
            motionHandle = motion.GetNativeTexturePtr();
            environmentHandle = environment.GetNativeTexturePtr();
            sunHandle = sun.GetNativeTexturePtr();
            diffuseHandle = diffuse.GetNativeTexturePtr();
            specularHandle = specular.GetNativeTexturePtr();
            forwardHandle = forward.GetNativeTexturePtr();
            forwardRatioHandle = forwardRatio.GetNativeTexturePtr();
            surfaceHandle = surface.GetNativeTexturePtr();
            surfaceModeHandle = surfaceMode.GetNativeTexturePtr();
            // OnPreCull bound the texture let go above for this frame's forward light,
            // which runs after this frame's trace into the new one.
            Shader.SetGlobalTexture(RtForward, forward);
            Shader.SetGlobalTexture(RtForwardRatio, forwardRatio);
            haveHistory = false;
            gbufferReady = false;
        }

        // A linear, bilinear, clamped target of the frame's size, created.
        private RenderTexture Target(RenderTextureFormat format)
        {
            var texture = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear)
            {
                name = "ReDefinition raytracing",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.Create();
            return texture;
        }

        // Releases the frame's targets and forgets their native textures.
        private void ReleaseTargets()
        {
            foreach (RenderTexture texture in new[] { depth, gbuffer0, gbuffer1, gbuffer0Early, gbuffer1Early, gbuffer2, motion, environment, sun, diffuse,
                                                      specular, forward, forwardRatio, surface, surfaceMode, parity, parityRays, parityRaster })
                if (texture != null)
                {
                    texture.Release();
                    Destroy(texture);
                }
            depth = gbuffer0 = gbuffer1 = gbuffer0Early = gbuffer1Early = gbuffer2 = motion = environment = sun = diffuse = specular = forward = forwardRatio = null;
            surface = surfaceMode = parity = parityRays = parityRaster = null;
            surfaceHandle = surfaceModeHandle = parityHandle = parityRaysHandle = parityRasterHandle = IntPtr.Zero;
            depthHandle = gbuffer0Handle = gbuffer1Handle = gbuffer2Handle = motionHandle = environmentHandle = IntPtr.Zero;
            sunHandle = diffuseHandle = specularHandle = forwardHandle = forwardRatioHandle = IntPtr.Zero;
        }

        // The textures of the last frame, for the test player's and the diagnostics' look.
        internal RenderTexture DepthTexture { get { return depth; } }
        // The sun's raytraced visibility, its w the view depth.
        internal RenderTexture SunTexture { get { return sun; } }
        // The raytraced diffuse light, denoised where the denoiser runs.
        internal RenderTexture DiffuseTexture { get { return diffuse; } }
        // The raytraced reflections, the specular light.
        internal RenderTexture SpecularTexture { get { return specular; } }
        // The G-buffer's copy of the normals (GBuffer2).
        internal RenderTexture NormalTexture { get { return gbuffer2; } }
        // The G-buffer's copy of the specular colour and smoothness (GBuffer1).
        internal RenderTexture SpecularSmoothnessTexture { get { return gbuffer1; } }
        // The surfaces as their materials mean them, and how each was found (SurfaceIntent).
        internal RenderTexture SurfaceTexture { get { return surface; } }
        // How each surface was found, beside SurfaceTexture (RT_SURFACE_*, RT_REGION_*).
        internal RenderTexture SurfaceModeTexture { get { return surfaceMode; } }
        // The sky's cube map unwrapped into 256 by 128 texels, as the proxy reads it.
        internal RenderTexture EnvironmentTexture { get { return environment; } }
        // The raytraced light the forward light adds to the frame.
        internal RenderTexture ForwardTexture { get { return forward; } }
        // Without a G-buffer: the ratio the forward light relights the frame's colour by.
        internal RenderTexture ForwardRatioTexture { get { return forwardRatio; } }
        // The light whose shadows the raytraced sun visibility replaces.
        internal Light SunLightNow { get { return maskedLight; } }
        // The ocean's raytraced reflections as Scatterer's ocean reads them (ssrOutput).
        internal RenderTexture OceanReflectionsTexture { get { return oceanReflections; } }
        // Whether KSP's own sea took the raytraced reflections, with the sun's disc, in the
        // last frame (TraceOcean). The sea is traced from Unity's G-buffer as the frame's trace took it, its
        // reflections whole and undenoised for the reflections' shader (_ReDefinitionRtOcean,
        // ReDefinitionDeferredReflections.shader). TraceOcean traces it in the frame's own command buffer (`into`),
        // right after the frame, before the reflections pass reads it.
        internal static bool StockSeaTraced { get; private set; }
        // The sun's colour the last frame's trace took (SunColor as the frame drew it).
        internal Color TracedSunColour { get; private set; }
        // The ocean's raytraced reflections as the trace wrote them.
        internal RenderTexture OceanTracedTexture { get { return oceanTraced; } }
        // Scatterer's ocean's depth, copied from its G-buffer.
        internal RenderTexture OceanDepthTexture { get { return oceanDepth; } }
        // The ocean's normals in the shape of Unity's G-buffer 2 (fragOceanSurface2).
        internal RenderTexture OceanSurfaceTexture { get { return oceanSurface2; } }
        // The ocean's specular colour and smoothness in the shape of Unity's G-buffer 1 (fragOceanSurface1).
        internal RenderTexture OceanRoughnessTexture { get { return oceanSurface1; } }

        // The service's line and each pass's time, for the diagnostics.
        internal string Status()
        {
            statusText.Length = 0;
            RtNative.KspRtStatus(statusText, statusText.Capacity);
            RtNative.KspRtPassTimings(PassTimings, PassTimings.Length);
            return statusText.ToString();
        }
    }
}
