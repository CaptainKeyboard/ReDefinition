using System.Collections.Generic;
using System;
using FidelityFX.FSR3;
using FidelityFX;
using ReDefinition.Proxy;
using ReDefinition.Core;
using ReDefinition.Shared;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The upscaler on one scene.
    //
    // KSP stacks several cameras that paint into the same buffer one after the
    // other: GalaxyCamera the starfield, Camera ScaledSpace the distant planets,
    // Camera 00 the near scene, then the FX cameras. A viewport rectangle on one
    // camera cannot scale that stack (CameraRedirect), so the whole 3D stack
    // renders into one shared RenderTexture at render resolution, and a presenter
    // camera behind the stack writes the result into the frame buffer before the
    // UI cameras draw on top.
    //
    // Here the rig's setup, its frame and its teardown, and FSR 3's dispatch; the
    // cameras in UpscalerCameras, the textures in UpscalerTextures, depth, motion
    // vectors and masks in UpscalerInputs; its other parts in UpscalerRig.*.cs: the
    // HUD-less copy, the upscalers in the proxy, DLSS Ray Reconstruction, the hooks
    // other mods register, the camera and its cuts, the diagnostics and the preview.
    public partial class UpscalerRig : MonoBehaviour
    {
        // The quality mode, which sets the render size (and DLSS's own mode), and the RCAS strength after FSR 3,
        // DLSS and XeSS, up to MaximumSharpness.
        public Fsr3Upscaler.QualityMode QualityMode = Fsr3Upscaler.QualityMode.NativeAA;
        public float Sharpness = 0.4f;

        // Which accumulate shader the context was built with (ShaderBundle.Load):
        // with sharpening it writes for the RCAS pass, without it straight into the
        // output. Fixed for the life of the rig.
        public bool Sharpening = true;

        // FSR's own exposure, from the image's luminance, for its internal
        // tonemapping. AMD: "it is recommended that FFX_UPSCALE_ENABLE_AUTO_EXPOSURE
        // is used by the application, unless there is a particular reason not to"
        // (super-resolution-upscaler.md); ReDefinition has no exposure of its own to
        // hand over. Fixed for the life of the rig.
        public bool AutoExposure = true;

        // The negative mipmap bias: finer mip levels than the render resolution
        // picks, log2(render/display) - 1 -- so -1 at AA only (ApplyMipmapBias).
        // Applied only to textures the redirected cameras see (KspMipmapBias).
        public bool EnableMipmapBias = true;

        // Diagnostic mode: the rendered image scaled up bilinearly, without an
        // upscaler. A correct but soft image here means the camera wiring is
        // right and a fault lies in the upscaler.
        public bool Bypass;

        // The upscaler off and frame generation on: the 3D stack is still
        // redirected, at full size, so that frame generation gets depth, motion
        // vectors and the HUD-less copy -- without jitter, masks, mipmap bias,
        // quality overrides or any upscaler. Fixed for the life of the rig.
        internal bool PassThrough;

        // Jitter off separates the jitter from the camera redirect.
        public bool EnableJitter = true;

        // Skinned renderers -- kerbals, flags -- made to draw their own motion
        // (SkinnedMotionVectors). Live.
        internal bool ForceSkinnedMotionVectors = true;

        // Where the depth copy is taken from. In KSP's deferred path
        // BuiltinRenderTextureType.Depth stays empty and KSP logs "built-in render
        // texture type 3 not found"; RenderTargetIdentifier has no
        // RenderTextureSubElement overload in Unity 2019.4, so the depth
        // sub-element of the camera target is not available either.
        public enum DepthSource
        {
            // The two sources, each named on its line: a global texture, or Unity's built-in resolved depth.
            GlobalDepthTexture,  // _CameraDepthTexture, filled because of depthTextureMode
            ResolvedDepth,       // Unity's BuiltinRenderTextureType.ResolvedDepth, the depth buffer resolved from deferred
        }

        // ResolvedDepth: in KSP's deferred path the only source that holds depth;
        // _CameraDepthTexture stays empty despite depthTextureMode
        // (docs/development/upscaler.md).
        public DepthSource Depth = DepthSource.ResolvedDepth;

        // FSR's own debug view: the output replaced with panels of the dilated mask
        // channels -- disocclusion, reactive and shading change -- as green
        // intensity. The add-on sets it in development builds only, where FSR's
        // debug view pass is compiled in (DEVELOPMENT_BUILD).
        public bool DebugView;

        // Frame generation, handled by the native dxgi.dll proxy. Without that
        // proxy this does nothing and the upscaler is unaffected.
        public bool FrameGeneration;

        // FSR 3 in Unity, or DLSS, AMD's DLL or XeSS through the proxy (DlssBridge,
        // AmdUpscalerBridge, XessUpscalerBridge). Fixed for the life of the rig; the preset is live.
        internal UpscalerBackend Backend = UpscalerBackend.Fsr3;
        internal DlssPreset DlssPreset = DlssPreset.Default;

        // Game-wide quality settings, each switchable on its own
        // (QualityOverrides).
        public bool CompensateLodBias = true;
        public bool DisableMsaa = true;
        public bool ForceAnisotropic = true;

        // TUFX's image effects that belong after the upscaler -- bloom, tonemapping,
        // film grain and the like -- drawn over the upscaler's output instead of
        // into the image it receives (TufxPostProcessing). Live.
        public bool TufxAfterUpscaling = true;

        // The masks FSR 3 takes beside colour, depth and motion vectors
        // (UpscalerMasks). Off by default. Live.
        public bool TransparencyMask;
        internal UpscalerMasks.ReactiveSource ReactiveMask = UpscalerMasks.ReactiveSource.Off;

        // Whether the rig's textures and FSR 3 run in HDR: the scene camera's allowHDR at Setup.
        public bool EffectiveHdr { get; private set; }

        // Whether the rig is set up as an upscaler: its textures made, not pass-through, and FSR 3's context
        // made unless it bypasses or upscales in the proxy.
        public bool Ready
        {
            get { return textures.LowRes != null && !PassThrough && (Bypass || context != null || Backend != UpscalerBackend.Fsr3); }
        }
        // What the rig is doing, or why Setup failed, in words for the log and the window.
        public string Status { get; private set; }
        // The size the 3D stack renders at: the quality mode's, or the one DLSS asks for.
        public Vector2Int RenderSize { get { return renderSize; } }
        // The screen's size the rig was built for, which the upscaler writes.
        public Vector2Int DisplaySize { get { return displaySize; } }

        // Set once the rig cannot go on as built; the add-on builds a new one
        // (RequestRebuild). With a reason when the technique stopped for good and
        // FSR 3 is to run in its place (NoteNativeState).
        internal bool RebuildWanted { get; private set; }
        // Why the technique in the proxy stopped for good, which the add-on keeps to run FSR 3 instead; else null.
        internal string FallbackReason { get; private set; }

        // Whether the proxy called that failure one that stands (state -2).
        internal bool FallbackStable { get; private set; }

        // The scene camera the rig is built on; FSR 3's context (null for the proxy's upscalers, a bypass or
        // pass-through), its per-frame dispatch description, and the buffer each frame's dispatch and packet go in.
        private Camera cam;
        private Fsr3UpscalerContext context;
        private readonly Fsr3Upscaler.DispatchDescription dispatch = new Fsr3Upscaler.DispatchDescription();
        private CommandBuffer dispatchBuffer;

        // The rig's parts: its render targets, the inputs beside the colour, and the redirected cameras.
        private readonly UpscalerTextures textures = new UpscalerTextures();
        private readonly UpscalerInputs inputs = new UpscalerInputs();
        private readonly UpscalerCameras cameras = new UpscalerCameras();

        // The screen's size the rig was built for, and the size the 3D stack renders at.
        private Vector2Int displaySize;
        private Vector2Int renderSize;

        // The helpers the rig drives each frame: mipmap bias, game-wide quality settings, TUFX after the
        // upscaler, the motion vector audit, the vessel's own motion vectors and its shadow layer.
        private readonly KspMipmapBias mipmapBias = new KspMipmapBias();
        private readonly QualityOverrides quality = new QualityOverrides();
        private readonly TufxPostProcessing tufx = new TufxPostProcessing();
        private readonly MotionVectorAudit motionAudit = new MotionVectorAudit();
        private readonly VesselMotionVectors vesselMotion = new VesselMotionVectors();
        private readonly VesselShadowLayer vesselShadow = new VesselShadowLayer();
        // VesselShadowLayer.Enabled as the HUD-less capture was recorded with it.
        private bool hudLessWithShadow;
        private readonly SkinnedMotionVectors skinned = new SkinnedMotionVectors();
        private int skinnedSeenLoads = -1;
        private static bool loggedPassThroughMsaa;
        private bool biasApplied;
        private bool resetHistory = true;

        // Once per scene load, a few seconds after the rig first sees it: which
        // skinned renderers draw their own motion vectors. Static: a rig rebuilt in
        // the same scene -- a quality change, a camera mode change -- is not another
        // scene to report on.
        private static float skinnedReportTime = float.MaxValue;
        private static int skinnedReportedLoad = -1;

        // DLSS frame generation's camera matrices for the packet (StreamlineCamera),
        // reused every frame, and the view-projection of the frame that sent the
        // last packet, which the next one reprojects into.
        private readonly float[] viewToClip = new float[16];
        private readonly float[] clipToView = new float[16];
        private readonly float[] clipToPrevClip = new float[16];
        private readonly float[] prevClipToClip = new float[16];
        private Matrix4x4 lastViewProjection;
        private int lastViewProjectionFrame = -1;

        // The frame before's view-projection in this frame's world: KSP's floating
        // origin moved the camera and the vessel by minus OriginShift, so a point now
        // at p lay at p + OriginShift (docs/development/shared-foundation.md).
        private static Matrix4x4 AcrossOriginShift(Matrix4x4 previous)
        {
            return SharedFrame.Shifted ? previous * Matrix4x4.Translate((Vector3)SharedFrame.OriginShift) : previous;
        }

        // The scene camera's depthTextureMode before Setup, and the bits Setup added to it, which Teardown clears.
        private DepthTextureMode originalDepthMode;
        private DepthTextureMode addedDepthModes;

        // Every scene load Unity reports, counted by the add-on -- flight to flight
        // too, a revert or a quickload, and the editors' switch between VAB and SPH,
        // which loads the building's scenery additively and keeps the rig
        // (EditorDriver, decompiled). HighLogic.LoadScene loads through a
        // loading-buffer scene, so the count may rise twice per change.
        private static int sceneLoads;

        // Up to 2: values above FidelityFX's range of 1 stay numerically sound;
        // above about 1.2 artefacts appear.
        internal const float MaximumSharpness = 1.2f;

        // The rig in place, or null; set by the add-on as it attaches and
        // detaches one (SharedFrame, ScattererCompatibility, UnityMouseEvents).
        internal static UpscalerRig Current { get; set; }

        // The projection a camera of the rig rendered this frame with, its jitter in it,
        // for the raytracing (src/Raytracing/Ksp/RtKsp.cs); false where the rig did not
        // jitter it.
        internal bool RenderedProjection(Camera camera, out Matrix4x4 projection)
        {
            return cameras.RenderedProjection(camera, out projection);
        }

        // Counts a scene load; the add-on calls it on each one Unity reports (sceneLoads).
        internal static void NoteSceneLoad()
        {
            sceneLoads++;
        }

        // Every camera that contributes to the 3D image (UpscalerCameras).
        internal static bool IsSceneCamera(string name)
        {
            return UpscalerCameras.IsSceneCamera(name);
        }

        // What the frame's state tells other mods (SharedFrame): whether this rig
        // upscales -- not its bypass, not frame generation's capture alone, not a rig
        // waiting to be rebuilt, not an upscaler in the proxy while it shows none of
        // their frames --, whether frame generation receives its frames, the
        // jitter the upscaler is told of, and the camera it is built on.
        internal bool UpscalerRuns
        {
            get { return Ready && !Bypass && !RebuildWanted && (Backend == UpscalerBackend.Fsr3 || nativeShown); }
        }

        // Whether frame generation is on and gets this rig's frames: the HUD-less capture attached and the
        // frame not shown as rendered.
        internal bool FrameGenerationReceives
        {
            get { return FrameGeneration && hudLessBuffer != null && !PresentsRendered; }
        }

        // Whether Present shows the rendered image as it is, sending neither the
        // upscaler nor frame generation anything.
        private bool PresentsRendered
        {
            get { return Bypass || RebuildWanted || (context == null && Backend == UpscalerBackend.Fsr3 && !PassThrough); }
        }

        // Whether the rig hands its frames to an upscaler or to frame generation: what the
        // helpers that exist for them wait for (RenderInterpolation, UnityMotionRepair).
        internal static bool Feeding
        {
            get
            {
                UpscalerRig rig = Current;
                return rig != null && !rig.TornDown && rig.textures.LowRes != null && !rig.PresentsRendered
                       && (!rig.PassThrough || rig.hudLessBuffer != null);
            }
        }

        // Whether the motion vectors of camera are drawn because the rig asked for them,
        // not another mod.
        internal static bool AskedForMotionVectors(Camera camera)
        {
            UpscalerRig rig = Current;
            return rig != null && !rig.TornDown && rig.cam == camera
                   && (rig.addedDepthModes & DepthTextureMode.MotionVectors) != 0;
        }

        // This frame's jitter in render pixels, as the upscaler is told of it; zero while none runs.
        internal Vector2 CurrentJitter
        {
            get { return dispatch.JitterOffset; }
        }

        // Builds the rig on the scene camera target: render size, textures, the redirected stack, the presenter,
        // the upscaler and the overrides. False with the reason in Status; the caller then destroys the rig, which tears it down.
        public bool Setup(Camera target, Fsr3UpscalerShaders shaders)
        {
            cam = target;

            if (!PassThrough && !SystemInfo.supportsComputeShaders)
            {
                Status = "This graphics API cannot run compute shaders.";
                return false;
            }

            // FSR 3's shaders are built with INVERTED_DEPTH (Fsr3UpscalerPass.VerifyBakedFlags):
            // on a graphics API without reversed depth they would give a wrong image.
            if (Backend == UpscalerBackend.Fsr3 && !Bypass && !PassThrough && !SystemInfo.usesReversedZBuffer)
            {
                Status = "FSR 3 needs reversed depth, which " + SystemInfo.graphicsDeviceType + " does not use.";
                return false;
            }

            displaySize = new Vector2Int(Screen.width, Screen.height);
            if (displaySize.x <= 0 || displaySize.y <= 0)
            {
                Status = "Screen reports " + displaySize.x + "x" + displaySize.y + ".";
                return false;
            }

            int renderWidth, renderHeight;
            Fsr3Upscaler.GetRenderResolutionFromQualityMode(out renderWidth, out renderHeight, displaySize.x,
                displaySize.y, PassThrough ? Fsr3Upscaler.QualityMode.NativeAA : QualityMode);
            renderSize = new Vector2Int(renderWidth, renderHeight);

            // DLSS at the size it asks for, once the proxy has made a feature for
            // this mode and output (DlssBridge.RenderSize); before that at FSR's,
            // until DLSS answers and the rig is rebuilt (NoteNativeState).
            Vector2Int optimal;
            if (Backend == UpscalerBackend.Dlss && !Bypass && !PassThrough
                && DlssBridge.RenderSize(displaySize, DlssBridge.Quality(QualityMode), out optimal))
                renderSize = optimal;

            originalDepthMode = cam.depthTextureMode;

            // HDR as KSP renders: without it in the editor, with it in flight. A
            // floating point buffer where KSP renders without HDR turns the water at
            // the horizon transparent.
            EffectiveHdr = cam.allowHDR;
            // Only the bits added here are cleared on teardown: KSP or another mod
            // may set bits of its own in the meantime.
            DepthTextureMode wanted = DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
            addedDepthModes = wanted & ~originalDepthMode;
            cam.depthTextureMode |= wanted;

            string failed = textures.Make(renderSize, displaySize, EffectiveHdr, PassThrough);
            if (failed != null)
            {
                Status = failed;
                return false;
            }

            FrameGenerationBridge.RegisterInputs(textures.DepthCopy, textures.MotionVectors, textures.HudLess);
            textures.CaptureHandles();
            inputs.AttachCapture(cam, textures, Depth);

            if (!cameras.Redirect(textures.LowRes, this))
            {
                Status = "No 3D cameras found to redirect.";
                return false;
            }
            if (FrameGeneration && FrameGenerationBridge.InputsUsable) AttachHudLessCapture(cameras.Presenter, true);
            inputs.ScaledMotion.Attach(cameras.Redirects, renderSize);

            // Before the first frame renders: TUFX's layers are split from it on.
            tufx.Refresh(cameras.Redirects, cam, TufxAfterUpscaling && !DebugView && !PassThrough, EffectiveHdr, displaySize);

            StartUpscaler(shaders);

            ApplyMipmapBias();
            RefreshQualityOverrides();

            // The presenter camera renders in the frame the rig is created in,
            // before the rig's first Update: without parameters here the dispatch
            // would get an image size of 0x0 ("Thread group size must be above
            // zero").
            if (!Bypass) SetupDispatch();

            Status = Bypass ? "bypass, without FSR"
                : PassThrough ? "upscaler off, frame generation's inputs only"
                : "active";

            // The capture keeps the single-sample target the upscaler uses, so KSP's
            // MSAA does not reach the 3D scene; said once a session.
            if (PassThrough && QualitySettings.antiAliasing > 1 && !loggedPassThroughMsaa)
            {
                loggedPassThroughMsaa = true;
                Debug.LogWarning(Log.Tag + " Frame generation without the upscaler captures the scene without KSP's "
                                 + QualitySettings.antiAliasing + "x MSAA; the upscaler at AA only antialiases it.");
            }
            resetHistory = true;
            return true;
        }

        // The link to the upscaler in the proxy, the sharpening after DLSS and XeSS,
        // and FSR 3's context.
        private void StartUpscaler(Fsr3UpscalerShaders shaders)
        {
            nativeLink = Bypass || PassThrough ? null : UpscalerBackends.Link(Backend);
            if ((Backend == UpscalerBackend.Dlss || Backend == UpscalerBackend.Xess) && !Bypass && !PassThrough && Sharpening
                && shaders != null && textures.Upscaled != null)
            {
                nativeSharpener = new RcasSharpener(shaders.sharpenPass, textures.Upscaled);
                if (nativeSharpener.Input.IsCreated())
                {
                    nativeSharpenInputHandle = nativeSharpener.Input.GetNativeTexturePtr();
                }
                else
                {
                    // Not a rebuild the next frame would fail the same way (TexturesCreated):
                    // the upscaler goes on unsharpened.
                    nativeSharpener.Release();
                    nativeSharpener = null;
                    Debug.LogWarning(Log.Tag + " " + UpscalerBackends.Name(Backend)
                                     + " runs without sharpening: the texture RCAS reads could not be created.");
                }
            }
            if (!Bypass)
            {
                // DLSS, AMD's DLL and XeSS are started by the proxy on their first frame.
                if (Backend == UpscalerBackend.Fsr3 && !PassThrough)
                {
                    // HDR as the camera renders, which picked the shader set (TryAttach).
                    Fsr3Upscaler.InitializationFlags flags = EffectiveHdr
                        ? Fsr3Upscaler.InitializationFlags.EnableHighDynamicRange
                        : 0;
                    if (AutoExposure) flags |= Fsr3Upscaler.InitializationFlags.EnableAutoExposure;
                    context = Fsr3Upscaler.CreateContext(displaySize, renderSize, shaders, flags);
                }
                dispatchBuffer = new CommandBuffer { name = "ReDefinition.UpscalerDispatch" };
            }
        }

        // The scene camera's inputs, recorded anew every frame (UpscalerInputs). Other
        // mods' motion vectors over Unity's and the clouds' follow as the camera culls
        // (OnPreCull), with the frame's state decided.
        private void RecordInputs()
        {
            // The masks for FSR 3 and AMD's DLL, which take them; DLSS and XeSS have none. FSR's
            // automatic reactive mask is FSR 3's own pass.
            bool fsr = !Bypass && !PassThrough && Backend == UpscalerBackend.Fsr3;
            bool amd = !Bypass && !PassThrough && Backend == UpscalerBackend.Amd;
            inputs.Record(cam, textures, Depth, renderSize, TransparencyMask && (fsr || amd),
                ReactiveMask == UpscalerMasks.ReactiveSource.Renderers && (fsr || amd),
                ReactiveMask == UpscalerMasks.ReactiveSource.Automatic && fsr);
            motionVectorHooksFrame = -1;
        }

        // For UnityMouseEvents: each redirected camera lends its screen for the
        // length of Unity's mouse event pass; those that did go into lent.
        internal void LendScreens(List<CameraRedirect> lent)
        {
            cameras.LendScreens(lent);
        }

        // Set once Teardown has run: the rig holds nothing any more, whether its
        // add-on or its camera's OnDisable took it down.
        internal bool TornDown { get; private set; }

        // Undoes everything Setup did and releases what it made, in an order no camera of the stack renders
        // into a released texture.
        public void Teardown()
        {
            // Once: Detach, OnDisable and OnDestroy each call it.
            if (TornDown) return;
            TornDown = true;
            ReportCameraMotionIfDue(true);
            ReleaseHooks();
            DetachHudLessCapture();
            tufx.Dispose();
            inputs.DisposeParts();
            skinned.Restore();
            // The light keeps the shadow's mask buffer until taken off.
            vesselMotion.Disable();
            vesselShadow.Disable();

            // The proxy hears about frame generation only through the dispatch, and
            // with the rig gone there is none: left on, it would keep pacing by
            // VSync with nothing to generate from.
            if (FrameGeneration) FrameGenerationBridge.SetEnabled(false);
            CameraRedirect.JitterActive = false;

            cameras.Release();

            if (cam != null) cam.depthTextureMode &= ~addedDepthModes;

            inputs.ReleaseCapture();

            if (biasApplied) { mipmapBias.UndoMipmapBias(); biasApplied = false; }
            quality.RestoreAll();
            ReleaseReconstruction(null);
            if (dispatchBuffer != null && nativeLink != null) nativeLink.Release();
            if (context != null) { context.Destroy(); context = null; }
            if (dispatchBuffer != null) { dispatchBuffer.Release(); dispatchBuffer = null; }

            // Released only now: until the cameras are restored they still render
            // into them, and Unity reports "Releasing render texture that is set as
            // Camera.targetTexture!".
            textures.ReleaseImages();
            // After the proxy's release of DLSS or XeSS, whose output it holds.
            if (nativeSharpener != null)
            {
                nativeSharpener.Release();
                nativeSharpener = null;
            }
            textures.ReleaseInputs();

            if (probePixel != null) { Destroy(probePixel); probePixel = null; }
            if (readback != null) { Destroy(readback); readback = null; }
            if (preview != null) { Destroy(preview); preview = null; }

            Status = "off";
        }

        // Live, without rebuilding the rig: a rebuild resets the upscaler's history.
        public void RefreshQualityOverrides()
        {
            // The upscaler's, not frame generation's.
            if (PassThrough) return;
            float ratio = renderSize.y > 0 ? (float)displaySize.y / renderSize.y : 1f;
            quality.SetLodBias(CompensateLodBias, ratio);
            quality.SetAntiAliasing(DisableMsaa);
            quality.SetAnisotropic(ForceAnisotropic);
        }

        // After KSP applied its own settings; see QualityOverrides.Reassert.
        public void ReassertQualityOverrides()
        {
            quality.Reassert();
        }

        // KSP's events the rig follows: the floating origin's shift, vessels loaded and the editors' ship changed.
        private void Awake()
        {
            GameEvents.onFloatingOriginShift.Add(OnFloatingOriginShift);
            GameEvents.onVesselLoaded.Add(OnVesselLoaded);
            GameEvents.onEditorShipModified.Add(OnEditorShipModified);
        }

        // Counts KSP's physics steps for VesselMotionVectors.
        private void FixedUpdate()
        {
            VesselMotionVectors.CountPhysicsStep();
        }

        // A rig can go without Teardown: destroyed with its camera when a scene
        // ends (the editor's Main Camera on launch), or by DestroyImmediate after
        // a failed Setup. Teardown is safe to run twice, so it runs here too;
        // otherwise the mipmap bias, the quality overrides and frame generation
        // stay as the rig left them, and the next rig takes them for the
        // player's own.
        private void OnDestroy()
        {
            GameEvents.onFloatingOriginShift.Remove(OnFloatingOriginShift);
            GameEvents.onVesselLoaded.Remove(OnVesselLoaded);
            GameEvents.onEditorShipModified.Remove(OnEditorShipModified);
            vesselMotion.Disable();
            vesselShadow.Disable();
            Teardown();
        }

        // The layers the redirected cameras render (UpscalerCameras).
        private int VisibleLayerMask()
        {
            return cameras.VisibleLayerMask();
        }

        // FSR's formula is log2(render/display) - 1.0: at NativeAA, where render and
        // display resolution are equal, the bias is -1.0. That constant is AMD's
        // recommendation for temporal reconstruction in general -- the accumulation
        // resolves detail a single sample cannot, so the sampler reaches for a
        // sharper mip level.
        private void ApplyMipmapBias()
        {
            if (PassThrough || !EnableMipmapBias || biasApplied) return;

            float bias = Fsr3Upscaler.GetMipmapBiasOffset(renderSize.x, displaySize.x);
            if (float.IsNaN(bias) || float.IsInfinity(bias)) return;

            // Only layers one of the redirected cameras sees.
            mipmapBias.SetVisibleLayers(VisibleLayerMask());

            float started = Time.realtimeSinceStartup;
            mipmapBias.ApplyMipmapBias(bias);
            biasApplied = true;
            biasSeenLoads = sceneLoads;

            Debug.Log(Log.Tag + " Mipmap bias " + bias.ToString("0.00") + " applied to "
                      + mipmapBias.TextureCount + " textures in "
                      + ((Time.realtimeSinceStartup - started) * 1000f).ToString("0") + " ms.");
        }

        // The mipmap bias for what came after ApplyMipmapBias: the parts of a vessel
        // loaded or of the ship in the editors, taken in the frame after, when they
        // are in place; and a few seconds after a load that keeps the rig -- the
        // editors' switch between VAB and SPH -- the whole scene again.
        private readonly List<Part> biasParts = new List<Part>();
        private readonly List<Renderer> biasRenderers = new List<Renderer>();
        private int biasPartsFrame = -1;
        private int biasSeenLoads = -1;
        private float biasSceneTime = float.MaxValue;

        // A vessel loaded in flight: its parts get the mipmap bias in the next frame.
        private void OnVesselLoaded(Vessel vessel)
        {
            if (vessel != null) NoteBiasParts(vessel.parts);
        }

        // The ship in the editors changed: its parts get the mipmap bias in the next frame.
        private void OnEditorShipModified(ShipConstruct ship)
        {
            if (ship != null) NoteBiasParts(ship.parts);
        }

        // Queues parts for ExtendMipmapBias, once the bias is applied at all.
        private void NoteBiasParts(List<Part> parts)
        {
            if (!biasApplied || parts == null) return;
            biasParts.AddRange(parts);
            biasPartsFrame = Time.frameCount;
        }

        // Each frame: the queued parts, the whole scene a few seconds after a load that kept the rig, and
        // Parallax's scatter every two seconds get the bias already applied.
        private void ExtendMipmapBias()
        {
            if (!biasApplied) return;
            if (sceneLoads != biasSeenLoads)
            {
                biasSeenLoads = sceneLoads;
                biasSceneTime = Time.unscaledTime + 3f;
            }
            int added = 0;
            if (Time.unscaledTime >= biasSceneTime)
            {
                biasSceneTime = float.MaxValue;
                biasParts.Clear();
                added = mipmapBias.Extend(FindObjectsOfType<Renderer>());
            }
            else if (biasParts.Count > 0 && Time.frameCount > biasPartsFrame)
            {
                biasRenderers.Clear();
                foreach (Part part in biasParts)
                    if (part != null) biasRenderers.AddRange(part.GetComponentsInChildren<Renderer>(true));
                biasParts.Clear();
                added = mipmapBias.Extend(biasRenderers);
                biasRenderers.Clear();
            }
            // Parallax's scatter, drawn without renderers: its renderers come some seconds
            // into a scene and as the terrain around the vessel is built.
            if (Time.unscaledTime >= biasScatterTime)
            {
                biasScatterTime = Time.unscaledTime + 2f;
                biasMaterials.Clear();
                Raytracing.RtKspMods.ParallaxScatterMaterials(biasMaterials);
                int scatter = biasMaterials.Count > 0 ? mipmapBias.Extend(biasMaterials) : 0;
                biasMaterials.Clear();
                if (scatter > 0)
                    Debug.Log(Log.Tag + " Mipmap bias applied to " + scatter + " textures of Parallax's scatter.");
            }
            if (added > 0)
                Debug.Log(Log.Tag + " Mipmap bias applied to " + added + " more textures, " + mipmapBias.TextureCount + " in all.");
        }

        // Parallax's scatter materials, gathered anew at each look, and when the next look is due.
        private readonly List<Material> biasMaterials = new List<Material>();
        private float biasScatterTime;

        // Jitter and dispatch parameters once per frame, before any camera
        // renders. The cameras fetch the value themselves in their OnPreRender,
        // since KSP sets the projection matrices anew every frame.
        private void Update()
        {
            if (textures.LowRes == null) return;

            if (!RebuildWanted && !TexturesCreated()) RequestRebuild("a render texture was lost");
            // A window resized or a resolution chosen: every target, the upscaler's
            // context and the packets are sized for the screen the rig was built for.
            if (!RebuildWanted && (Screen.width != displaySize.x || Screen.height != displaySize.y)
                && Screen.width > 0 && Screen.height > 0)
                RequestRebuild("the screen is now " + Screen.width + "x" + Screen.height + ", the rig was built for "
                               + displaySize.x + "x" + displaySize.y);
            // HDR switched on the camera since: TUFX sets allowHDR with each profile it
            // applies (TexturesUnlimitedFXLoader.ApplyProfile), its HDR switch and a
            // profile changed in the middle of a scene included. The textures and the
            // upscaler's shaders follow the camera's, as at setup.
            if (!RebuildWanted && cam != null && cam.allowHDR != EffectiveHdr)
                RequestRebuild("the camera's HDR is now " + (cam.allowHDR ? "on" : "off"));

            // Once, a few seconds after the rig was built. See AttachHudLessCapture.
            if (Time.unscaledTime >= hudLessRecheckTime)
                RecheckHudLessCapture();

            ReportSkinnedIfDue();

            ReportCameraMotionIfDue(false);
            if (pendingTurnLine != null)
            {
                Debug.Log(pendingTurnLine);
                pendingTurnLine = null;
            }
            // A scene load that keeps the rig -- the editors' switch between VAB
            // and SPH -- brings skinned renderers without any of the sweep's events.
            if (sceneLoads != skinnedSeenLoads)
            {
                skinnedSeenLoads = sceneLoads;
                skinned.MarkDirty();
            }
            skinned.Refresh(ForceSkinnedMotionVectors);
            ExtendMipmapBias();

            tufx.Refresh(cameras.Redirects, cam, TufxAfterUpscaling && !DebugView && !PassThrough, EffectiveHdr, displaySize);
            RecordInputs();

            // The HUD-less copy only while frame generation can use it: it is a
            // blit of the whole backbuffer on every carrier camera, every frame.
            bool wantHudLess = FrameGeneration && FrameGenerationBridge.InputsUsable;
            if (wantHudLess != (hudLessBuffer != null) && cameras.Presenter != null)
            {
                if (wantHudLess) AttachHudLessCapture(cameras.Presenter, true);
                else DetachHudLessCapture();
            }
            // The vessel's shadow switched in or out of the HUD-less copy (VesselShadowLayer).
            else if (hudLessBuffer != null && hudLessWithShadow != VesselShadowLayer.Enabled && cameras.Presenter != null)
                AttachHudLessCapture(cameras.Presenter, false);
            RefreshOverlay();

            if (Bypass)
            {
                CameraRedirect.JitterActive = false;
                return;
            }

            ChooseJitter();
            SetupDispatch();
        }

        // Checked here, not at setup: the editors' switch between VAB and SPH is a
        // load that keeps the rig.
        private void ReportSkinnedIfDue()
        {
            if (sceneLoads != skinnedReportedLoad && skinnedReportTime == float.MaxValue)
                skinnedReportTime = Time.unscaledTime + 5f;
            if (Time.unscaledTime >= skinnedReportTime)
            {
                skinnedReportTime = float.MaxValue;
                skinnedReportedLoad = sceneLoads;
                // It walks the scene's renderers and their materials: a shader
                // unloaded with the scene must not take this frame's inputs with it.
                try
                {
                    Debug.Log(Log.Tag + " Skinned renderers: " + skinned.Describe(VisibleLayerMask()) + ".");
                }
                catch (Exception e)
                {
                    Debug.LogWarning(Log.Tag + " The skinned renderers could not be reported: " + e);
                }
            }
        }

        // With an upscaler in the proxy only while its image is the one shown:
        // before its first frame and after anything stopped it, the rendered
        // image goes out as it is, and jittered it would shake.
        private void ChooseJitter()
        {
            if (EnableJitter && !PassThrough && !RebuildWanted && (Backend == UpscalerBackend.Fsr3 || nativeShown))
            {
                int phaseCount = Fsr3Upscaler.GetJitterPhaseCount(renderSize.x, displaySize.x);
                float jitterX, jitterY;
                Fsr3Upscaler.GetJitterOffset(out jitterX, out jitterY, Time.frameCount, phaseCount);

                dispatch.JitterOffset = new Vector2(jitterX, jitterY);
                CameraRedirect.JitterNdc = new Vector2(2.0f * jitterX / renderSize.x,
                                                       2.0f * jitterY / renderSize.y);
                CameraRedirect.JitterActive = true;
            }
            else
            {
                // The upscaler is told that nothing was offset.
                dispatch.JitterOffset = Vector2.zero;
                CameraRedirect.JitterActive = false;
            }
        }

        // FSR 3's dispatch description from the rig's textures, masks and camera; the proxy's upscalers and the
        // frame packet read their sizes, scale, planes and frame time from it too.
        private void SetupDispatch()
        {
            dispatch.Color = new ResourceView(textures.LowRes);
            dispatch.Depth = new ResourceView(textures.DepthCopy);
            dispatch.MotionVectors = new ResourceView(textures.MotionVectors);
            dispatch.Exposure = ResourceView.Unassigned;
            dispatch.Reactive = ReactiveMask == UpscalerMasks.ReactiveSource.Renderers && inputs.Masks.ReactiveTexture != null
                ? new ResourceView(inputs.Masks.ReactiveTexture)
                : ResourceView.Unassigned;
            dispatch.TransparencyAndComposition = TransparencyMask && inputs.Masks.TransparencyTexture != null
                ? new ResourceView(inputs.Masks.TransparencyTexture)
                : ResourceView.Unassigned;

            dispatch.Output = textures.Upscaled != null ? new ResourceView(textures.Upscaled) : ResourceView.Unassigned;
            dispatch.PreExposure = 1.0f;
            // JitterOffset is set in Update.

            // Matches the accumulate shader the context was built with: the sharpen
            // variant writes its result for the RCAS pass instead of straight into
            // the output, and without RCAS the output would stay empty.
            dispatch.EnableSharpening = Sharpening;
            dispatch.Sharpness = Sharpness;

            dispatch.MotionVectorScale = new Vector2(-renderSize.x, -renderSize.y);
            dispatch.RenderSize = renderSize;
            dispatch.UpscaleSize = displaySize;
            dispatch.FrameTimeDelta = Time.unscaledDeltaTime;
            dispatch.CameraNear = cam.nearClipPlane;
            dispatch.CameraFar = cam.farClipPlane;
            dispatch.CameraFovAngleVertical = cam.fieldOfView * Mathf.Deg2Rad;
            dispatch.ViewSpaceToMetersFactor = 1.0f;    // in KSP one unit is one metre
            // FSR 3.1.4's four tuning constants keep DispatchDescription's
            // defaults, which are AMD's.
            dispatch.VelocityFactor = 1.0f;
            dispatch.Flags = DebugView ? Fsr3Upscaler.DispatchFlags.DrawDebugView : 0;
            // FSR's own estimate replaces both masks; a transparency mask given is
            // kept in it as the larger of the two (ffx_fsr3upscaler_tcr_autogen_pass.hlsl).
            dispatch.EnableAutoReactive = ReactiveMask == UpscalerMasks.ReactiveSource.Automatic
                                          && inputs.Masks.OpaqueOnly != null && inputs.Masks.PostAlpha != null;
            dispatch.ColorOpaqueOnly = dispatch.EnableAutoReactive ? new ResourceView(inputs.Masks.OpaqueOnly) : ResourceView.Unassigned;
            dispatch.ColorPostAlpha = dispatch.EnableAutoReactive ? new ResourceView(inputs.Masks.PostAlpha) : ResourceView.Unassigned;
            // Reset is decided in Present, where the frame is dispatched.

            if (SystemInfo.usesReversedZBuffer)
            {
                float near = dispatch.CameraNear;
                dispatch.CameraNear = dispatch.CameraFar;
                dispatch.CameraFar = near;
            }
        }

        // Back in focus, after alt-tab or a full-screen switch: the textures' native
        // handles are asked for once more.
        private void OnApplicationFocus(bool focus)
        {
            if (focus) textures.WantNativeCheck();
        }

        // Whether every texture whose native pointer went to the proxy at setup still exists (UpscalerTextures),
        // the RCAS input after DLSS and XeSS included; false asks for a rebuild.
        private bool TexturesCreated()
        {
            return textures.Created(PassThrough, nativeSharpener != null ? nativeSharpener.Input : null);
        }

        // The add-on builds the new rig in its next Update; until then the frame
        // goes out without upscaling and the proxy is sent nothing.
        private void RequestRebuild(string reason)
        {
            if (RebuildWanted) return;
            RebuildWanted = true;
            Debug.Log(Log.Tag + " Rebuilding the upscaler: " + reason + ".");
        }

        // Called by the presenter camera. Returning false means: did nothing,
        // the caller should pass through.
        public bool Present(RenderTexture destination)
        {
            if (textures.LowRes == null) return false;

            // Here as well as in Update: a texture lost later in the frame than the
            // rig's Update would otherwise reach the proxy by its old pointer.
            if (!RebuildWanted && !TexturesCreated()) RequestRebuild("a render texture was lost");
            if (PresentsRendered)
            {
                // Not the proxy's image: Update jitters no more for it.
                nativeShown = false;
                if (!tufx.Render(textures.LowRes, destination, textures.DepthCopy, textures.MotionVectors))
                    Graphics.Blit(textures.LowRes, destination);
                return true;
            }

            // Guard against a dispatch with empty parameters.
            if (dispatch.RenderSize.x <= 0 || dispatch.RenderSize.y <= 0)
                return false;

            // What the frame decided when it began (SharedFrame), and a cut or a mod's
            // request since: decided here, not in Update, a cut after the rig's Update
            // in the same frame would reset the frame after it, and frame generation,
            // which takes the same flag, would interpolate across the cut.
            SharedFrame.EnsureBegun();
            string late = SharedFrame.LateReset();
            if (late != null)
                Debug.Log(Log.Tag + " History reset while the frame rendered: " + late + ".");
            bool cut = SharedFrame.RigResetReason != null || late != null;
            dispatch.Reset = resetHistory || cut;
            resetHistory = false;

            // Wanted only with the HUD-less capture attached, like the packet below; off
            // while KSP's pause menu is open: "DLSS-G is turned off ... when the game is
            // paused, loading, in menus, or otherwise not rendering game frames"
            // (ProgrammingGuideDLSS_G.md, checklist). The frames sent then are a frozen
            // scene under a menu, nothing to interpolate.
            bool paused = PauseMenu.exists && PauseMenu.isOpen;
            FrameGenerationBridge.SetEnabled(FrameGeneration && hudLessBuffer != null && !paused);

            dispatchBuffer.Clear();
            RenderTexture result = textures.Upscaled;
            // Without the upscaler the image goes out as rendered, at full size;
            // only frame generation's packet follows.
            if (PassThrough)
                result = textures.LowRes;
            else if (Backend == UpscalerBackend.Fsr3)
                context.Dispatch(dispatch, dispatchBuffer);
            else if (!SubmitNative())
                result = textures.LowRes;
            else if (nativeSharpener != null)
                nativeSharpener.Schedule(dispatchBuffer, Sharpness);
            if (Probe.GpuProfiler.On)
                Probe.GpuProfiler.Mark(dispatchBuffer, "ReDefinition upscaler: " + (PassThrough ? "pass-through" : Backend.ToString())
                                                       + (Reconstructing ? " with Ray Reconstruction" : ""));

            // The same camera state the upscaler is dispatched with, so
            // interpolation and reconstruction agree about the frame, sent through
            // the same CommandBuffer so it arrives with the frame.
            //
            // The camera's own planes. Which way the depth runs is told to FSR by
            // the DEPTH_INVERTED flag at context creation, on both the upscaler and
            // the frame generation side; FSR takes min and max of the planes itself,
            // so their order carries nothing.
            //
            // KSP's floating origin shifts the world around the camera, so the
            // position is no fixed point in space; it is consistent from one frame
            // to the next, which is what interpolation reasons about.
            //
            // Only with the HUD-less capture attached, which Update does before this
            // frame's cameras draw: the proxy generates from frames with a packet,
            // and so never from a copy nobody wrote -- in the frame's own order,
            // which SetEnabled, taking effect at once, is not.
            if (FrameGeneration && hudLessBuffer != null) SubmitFramePacket();
            Probe.GpuProfiler.Mark(dispatchBuffer, "ReDefinition upscaler: frame generation packet");

            Graphics.ExecuteCommandBuffer(dispatchBuffer);
            StageDump.AfterDispatch(cam, CameraRedirect.JitterActive ? cam.nonJitteredProjectionMatrix : cam.projectionMatrix,
                                    dispatch.JitterOffset, Reconstructing, textures.LowRes, result);
            RunAfterUpscalingHooks(result);
            Probe.GpuProfiler.MarkNow("ReDefinition upscaler: after-upscaling hooks");

            if (!tufx.Render(result, destination, textures.DepthCopy, textures.MotionVectors))
                Graphics.Blit(result, destination);
            Probe.GpuProfiler.MarkNow("ReDefinition upscaler: TUFX after upscaling, or the copy out");

            // After the frame is out, and fenced: an instrument does not stop a
            // frame. On a failure it says so once and stops. With the diagnostic
            // measurements, or for the Debug switch for fast turns, which it drives.
            if (!instrumentFailed && (ReDefinitionAddon.DebugMode || RecordFastTurns))
            {
                try
                {
                    SampleCameraMotion(cut);
                }
                catch (System.Exception e)
                {
                    instrumentFailed = true;
                    Debug.LogWarning(Log.Tag + " Camera instrument stopped: " + e.Message);
                }
            }
            return true;
        }

        // Frame generation's packet for this frame, into the dispatch buffer.
        private void SubmitFramePacket()
        {
            // The camera where KSP's physics step put it, without the render
            // interpolation's shift (RenderInterpolation.CameraShift). In orbit
            // Krakensbane keeps the vessel at the origin and moves the world; the
            // interpolation moves the vessel and the camera on it back and forth by
            // up to 43 m a step. Frame generation's camera motion then disagreed with
            // the motion vectors by 490 to 1165 px, and its frames showed the vessel
            // three times (FlipProbe, 2026-10-17); the motion vectors hold the drawn
            // motion either way.
            Vector3 shift = RenderInterpolation.CameraShift(cam);
            Transform t = cam.transform;
            Vector3 p = t.position - shift, u = t.up, r = t.right, f = t.forward;

            // Streamline's matrices, for DLSS and XeSS frame generation only --
            // XeSS-FG takes the projection and builds its view from the camera's
            // basis -- from the projection Unity drew with and without the jitter:
            // the camera's own while no jitter runs, since
            // nonJitteredProjectionMatrix, once set, is never reset
            // (CameraRedirect). Reprojected into the frame before only when that
            // frame sent a packet and nothing cut in between; otherwise into
            // itself, which reprojects nothing.
            if (FrameGenerationBridge.CameraMatricesNeeded)
            {
                Matrix4x4 projection = CameraRedirect.JitterActive ? cam.nonJitteredProjectionMatrix : cam.projectionMatrix;
                Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(projection, false);
                Matrix4x4 viewProjection = gpuProjection * cam.worldToCameraMatrix * Matrix4x4.Translate(shift);
                bool previousFrame = lastViewProjectionFrame == Time.frameCount - 1 && !dispatch.Reset;
                StreamlineCamera.Fill(gpuProjection, viewProjection,
                                      previousFrame ? AcrossOriginShift(lastViewProjection) : viewProjection,
                                      viewToClip, clipToView, clipToPrevClip, prevClipToClip);
                lastViewProjection = viewProjection;
                lastViewProjectionFrame = Time.frameCount;
            }

            FrameGenerationBridge.FramePacket packet = new FrameGenerationBridge.FramePacket
            {
                FrameIndex = (uint)Time.frameCount,
                RenderWidth = (uint)renderSize.x,
                RenderHeight = (uint)renderSize.y,
                Reset = dispatch.Reset ? 1u : 0u,
                JitterX = dispatch.JitterOffset.x,
                JitterY = dispatch.JitterOffset.y,
                MotionVectorScaleX = dispatch.MotionVectorScale.x,
                MotionVectorScaleY = dispatch.MotionVectorScale.y,
                NearPlane = cam.nearClipPlane,
                FarPlane = cam.farClipPlane,
                VerticalFovRadians = dispatch.CameraFovAngleVertical,
                FrameTimeDeltaMs = dispatch.FrameTimeDelta * 1000f,
                PositionX = p.x, PositionY = p.y, PositionZ = p.z,
                UpX = u.x, UpY = u.y, UpZ = u.z,
                RightX = r.x, RightY = r.y, RightZ = r.z,
                ForwardX = f.x, ForwardY = f.y, ForwardZ = f.z,
                ViewToClip = viewToClip,
                ClipToView = clipToView,
                ClipToPrevClip = clipToPrevClip,
                PrevClipToClip = prevClipToClip,
            };
            FrameGenerationBridge.SubmitFrame(dispatchBuffer, ref packet);
        }

        // The camera's object deactivated or the rig switched off: everything goes back as with Detach.
        private void OnDisable()
        {
            Teardown();
        }
    }
}
