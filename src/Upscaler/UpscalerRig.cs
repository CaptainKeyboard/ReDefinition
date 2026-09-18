using System.Collections.Generic;
using System.Text;
using System;
using FidelityFX.FSR3;
using FidelityFX;
using ReDefinition.Bridges;
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
    // Here the rig's setup, its frame and its teardown; its other parts in
    // UpscalerRig.*.cs: the HUD-less copy, the upscalers in the proxy, the camera
    // and its cuts, the diagnostics and the preview.
    public partial class UpscalerRig : MonoBehaviour
    {
        public Fsr3Upscaler.QualityMode QualityMode = Fsr3Upscaler.QualityMode.NativeAA;
        public float Sharpness = 0.4f;

        // Which accumulate shader the context was built with (FsrShaderBundle.Load):
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
            GlobalDepthTexture,  // _CameraDepthTexture, filled because of depthTextureMode
            ResolvedDepth,       // BuiltinRenderTextureType.ResolvedDepth
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

        // FSR 3 in Unity, or DLSS or AMD's DLL through the proxy (DlssBridge,
        // AmdUpscalerBridge). Fixed for the life of the rig; the preset is live.
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

        public bool EffectiveHdr { get; private set; }

        public bool Ready
        {
            get { return lowRes != null && !PassThrough && (Bypass || context != null || Backend != UpscalerBackend.Fsr3); }
        }
        public string Status { get; private set; }
        public Vector2Int RenderSize { get { return renderSize; } }
        public Vector2Int DisplaySize { get { return displaySize; } }

        // Set once the rig cannot go on as built; the add-on builds a new one
        // (RequestRebuild). With a reason when the technique stopped for good and
        // FSR 3 is to run in its place (NoteNativeState).
        internal bool RebuildWanted { get; private set; }
        internal string FallbackReason { get; private set; }

        // Whether the proxy called that failure one that stands (state -2).
        internal bool FallbackStable { get; private set; }

        private Camera cam;
        private Fsr3UpscalerContext context;
        private readonly Fsr3Upscaler.DispatchDescription dispatch = new Fsr3Upscaler.DispatchDescription();
        private CommandBuffer dispatchBuffer;

        private RenderTexture lowRes;
        private RenderTexture upscaled;

        // The rig's own copies of depth and motion vectors.
        //
        // BuiltinRenderTextureType.MotionVectors is valid only inside the rendering
        // of the camera that produces it; in the presenter camera, where the
        // dispatch runs, the same identifier names the presenter's own buffers,
        // which do not exist. A CommandBuffer on the producing camera copies them.
        private RenderTexture motionVectors;
        private RenderTexture depthCopy;

        private CommandBuffer captureBuffer;

        private Vector2Int displaySize;
        private Vector2Int renderSize;

        private readonly List<CameraRedirect> redirects = new List<CameraRedirect>();
        private GameObject presenterObject;
        private UpscalerPresenter presenter;

        private readonly KspMipmapBias mipmapBias = new KspMipmapBias();
        private readonly QualityOverrides quality = new QualityOverrides();
        private readonly TufxPostProcessing tufx = new TufxPostProcessing();
        private readonly UpscalerMasks masks = new UpscalerMasks();
        private readonly CloudMotionVectors cloudMotion = new CloudMotionVectors();
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
        internal const float MaximumSharpness = 2f;

        // The rig in place, or null; set by the add-on as it attaches and
        // detaches one (SharedFrame, ScattererCompatibility, UnityMouseEvents).
        internal static UpscalerRig Current { get; set; }

        internal static void NoteSceneLoad()
        {
            sceneLoads++;
        }

        // Every camera that contributes to the 3D image. UI and vector cameras
        // stay out: they draw at full resolution on top of the upscaled image.
        //
        // The list follows KerbalVR, which redirects the same stack into its own
        // RenderTextures:
        //   flight external  GalaxyCamera, Camera ScaledSpace, Camera 00
        //   flight IVA       plus InternalCamera
        //   editor           GalaxyCamera, sceneryCam, Main Camera, markerCam
        //   main menu        GalaxyCamera, Landscape Camera
        //
        // FXCamera and FXDepthCamera draw no part of the scene image and stay out.
        private static readonly string[] SceneCameras =
        {
            "GalaxyCamera", "Camera ScaledSpace", "Camera 01", "Camera 00",
            "InternalCamera", "sceneryCam", "Main Camera", "markerCam",
            "Landscape Camera"
        };

        internal static bool IsSceneCamera(string name)
        {
            return Array.IndexOf(SceneCameras, name) >= 0;
        }

        // What the frame's state tells other mods (SharedFrame): whether this rig
        // upscales -- not its bypass, not frame generation's capture alone, not a rig
        // waiting to be rebuilt --, whether frame generation receives its frames, the
        // jitter the upscaler is told of, and the camera it is built on.
        internal bool UpscalerRuns
        {
            get { return Ready && !Bypass && !RebuildWanted; }
        }

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

        internal Vector2 CurrentJitter
        {
            get { return dispatch.JitterOffset; }
        }

        internal Camera SceneCamera
        {
            get { return cam; }
        }

        public bool Setup(Camera target, Fsr3UpscalerShaders shaders)
        {
            cam = target;

            if (!PassThrough && !SystemInfo.supportsComputeShaders)
            {
                Status = "This graphics API cannot run compute shaders.";
                return false;
            }

            displaySize =new Vector2Int(Screen.width, Screen.height);
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

            lowRes = new RenderTexture(renderSize.x, renderSize.y, 24,
                EffectiveHdr ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default,
                RenderTextureReadWrite.Default);
            lowRes.name = "ReDefinition_LowRes";
            lowRes.filterMode = FilterMode.Bilinear;
            if (!lowRes.Create())
            {
                Status = "RenderTexture " + renderSize.x + "x" + renderSize.y + " could not be created.";
                return false;
            }

            // No output without an upscaler to write it.
            if (!PassThrough)
            {
                upscaled = new RenderTexture(displaySize.x, displaySize.y, 0,
                    RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Default);
                upscaled.name = "ReDefinition_Upscaled";
                upscaled.enableRandomWrite = true;   // FSR writes into it via UAV
                upscaled.filterMode = FilterMode.Bilinear;
                if (!upscaled.Create())
                {
                    Status = "Output texture could not be created.";
                    return false;
                }
            }

            motionVectors = new RenderTexture(renderSize.x, renderSize.y, 0,
                RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear);
            motionVectors.name = "ReDefinition_MotionVectors";
            motionVectors.filterMode = FilterMode.Point;

            depthCopy = new RenderTexture(renderSize.x, renderSize.y, 0,
                RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
            depthCopy.name = "ReDefinition_Depth";
            depthCopy.filterMode = FilterMode.Point;

            if (!motionVectors.Create() || !depthCopy.Create())
            {
                Status = "Depth and motion vector buffers could not be created.";
                return false;
            }

            // In the backbuffer's own format, ARGB32, so frame generation compares
            // like with like.
            hudLessCopy = new RenderTexture(displaySize.x, displaySize.y, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            hudLessCopy.name = "ReDefinition_HudLess";
            hudLessCopy.filterMode = FilterMode.Point;
            if (!hudLessCopy.Create())
            {
                Status = "HUD-less texture could not be created.";
                return false;
            }

            FrameGenerationBridge.RegisterInputs(depthCopy, motionVectors, hudLessCopy);

            upscaledHandle = upscaled != null ? upscaled.GetNativeTexturePtr() : IntPtr.Zero;
            lowResHandle = lowRes.GetNativeTexturePtr();
            depthHandle = depthCopy.GetNativeTexturePtr();
            motionHandle = motionVectors.GetNativeTexturePtr();
            handleFrame = Time.frameCount;

            // On the camera that produces both, where the built-in identifiers are
            // valid, before the image effects.
            captureBuffer = new CommandBuffer { name = "ReDefinition.CaptureInputs" };
            captureBuffer.Blit(BuiltinRenderTextureType.MotionVectors, motionVectors);
            captureBuffer.Blit(DepthIdentifier(Depth), depthCopy);
            cam.AddCommandBuffer(CameraEvent.BeforeImageEffects, captureBuffer);

            if (!RedirectCameras())
            {
                Status = "No 3D cameras found to redirect.";
                return false;
            }

            // Before the first frame renders: TUFX's layers are split from it on.
            tufx.Refresh(redirects, cam, TufxAfterUpscaling && !DebugView && !PassThrough, EffectiveHdr, displaySize);

            nativeLink = Bypass || PassThrough ? null : UpscalerBackends.Link(Backend);
            if (Backend == UpscalerBackend.Dlss && !Bypass && !PassThrough && Sharpening && shaders != null && upscaled != null)
            {
                dlssSharpener = new RcasSharpener(shaders.sharpenPass, upscaled);
                if (dlssSharpener.Input.IsCreated())
                {
                    dlssSharpenInputHandle = dlssSharpener.Input.GetNativeTexturePtr();
                }
                else
                {
                    // Not a rebuild the next frame would fail the same way (TexturesCreated):
                    // DLSS goes on unsharpened.
                    dlssSharpener.Release();
                    dlssSharpener = null;
                    Debug.LogWarning(Log.Tag + " DLSS runs without sharpening: the texture RCAS reads could not be created.");
                }
            }
            if (!Bypass)
            {
                // DLSS and AMD's DLL are started by the proxy on their first frame.
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

        // The scene camera's inputs, recorded anew every frame: the masks follow
        // the renderers in view and their materials. They are recorded earlier in
        // the frame, in a buffer of their own (UpscalerMasks); the reactive one
        // copies the scene's depth into depthCopy for itself, and the capture here
        // copies it again at BeforeImageEffects, so what the upscaler, TUFX and
        // frame generation read does not depend on the masks.
        private void RecordInputs()
        {
            // FSR 3's inputs only: the proxy hands DLSS and AMD's DLL none.
            bool fsr = !Bypass && !PassThrough && Backend == UpscalerBackend.Fsr3;
            masks.Record(cam, TransparencyMask && fsr,
                ReactiveMask == UpscalerMasks.ReactiveSource.Renderers && fsr,
                ReactiveMask == UpscalerMasks.ReactiveSource.Automatic && fsr,
                DepthIdentifier(Depth), depthCopy, lowRes.format, renderSize);
            // EVE's clouds in the motion vectors, for every technique and frame
            // generation (CloudMotionVectors).
            cloudMotion.Record(cam, renderSize);
            captureBuffer.Clear();
            captureBuffer.Blit(DepthIdentifier(Depth), depthCopy);
            cloudMotion.Capture(captureBuffer, motionVectors, depthCopy);
            // Other mods' motion vectors over Unity's and the clouds' follow as the camera
            // culls (OnPreCull), with the frame's state decided.
            motionVectorHooksFrame = -1;
        }

        private static RenderTargetIdentifier DepthIdentifier(DepthSource source)
        {
            switch (source)
            {
                case DepthSource.GlobalDepthTexture:
                    return new RenderTargetIdentifier("_CameraDepthTexture");
                case DepthSource.ResolvedDepth:
                    return new RenderTargetIdentifier(BuiltinRenderTextureType.ResolvedDepth);
                default:
                    return new RenderTargetIdentifier(BuiltinRenderTextureType.Depth);
            }
        }

        private bool RedirectCameras()
        {
            redirects.Clear();
            float lastDepth = float.MinValue;
            string lastName = "none";

            foreach (string wanted in SceneCameras)
            {
                foreach (Camera other in Camera.allCameras)
                {
                    if (other == null || other.name != wanted) continue;

                    // Removed at once rather than reused: Destroy takes effect at the
                    // end of the frame, and a component pending destruction runs its
                    // OnDisable then and undoes the fresh redirect.
                    foreach (CameraRedirect stale in other.gameObject.GetComponents<CameraRedirect>())
                        DestroyImmediate(stale);

                    // Unity's mouse events still reach it: UnityMouseEvents.
                    CameraRedirect redirect = other.gameObject.AddComponent<CameraRedirect>();
                    redirect.Redirect(lowRes);
                    redirects.Add(redirect);

                    if (other.depth > lastDepth) { lastDepth = other.depth; lastName = other.name; }
                }
            }

            if (redirects.Count == 0) return false;

            // The presenter camera, directly behind the 3D stack. It draws nothing
            // (empty culling mask); its OnRenderImage has the frame buffer as its
            // target, at full resolution.
            presenterObject = new GameObject("ReDefinitionPresenter");
            Camera presenterCam = presenterObject.AddComponent<Camera>();
            presenterCam.cullingMask = 0;
            presenterCam.clearFlags = CameraClearFlags.Nothing;
            presenterCam.depth = lastDepth + 0.1f;
            presenterCam.targetTexture = null;
            presenterCam.allowHDR = true;
            presenterCam.allowMSAA = false;
            presenterCam.useOcclusionCulling = false;
            presenterCam.depthTextureMode = DepthTextureMode.None;

            presenter = presenterObject.AddComponent<UpscalerPresenter>();
            presenter.Rig = this;

            Debug.Log(Log.Tag + " " + redirects.Count + " cameras redirected; last in the stack '"
                      + lastName + "' (depth " + lastDepth.ToString("0.##")
                      + "), presenter camera at depth " + presenterCam.depth.ToString("0.##") + ".");

            if (FrameGeneration && FrameGenerationBridge.InputsUsable) AttachHudLessCapture(presenterCam, true);
            return true;
        }

        // For UnityMouseEvents: each redirected camera lends its screen for the
        // length of Unity's mouse event pass; those that did go into lent.
        internal void LendScreens(List<CameraRedirect> lent)
        {
            for (int i = 0; i < redirects.Count; i++)
            {
                CameraRedirect redirect = redirects[i];
                if (redirect != null && redirect.LendScreen()) lent.Add(redirect);
            }
        }

        // Set once Teardown has run: the rig holds nothing any more, whether its
        // add-on or its camera's OnDisable took it down.
        internal bool TornDown { get; private set; }

        public void Teardown()
        {
            TornDown = true;
            ReportCameraMotionIfDue(true);
            ReleaseHooks();
            DetachHudLessCapture();
            tufx.Dispose();
            masks.Dispose();
            cloudMotion.Dispose();
            skinned.Restore();

            // The proxy hears about frame generation only through the dispatch, and
            // with the rig gone there is none: left on, it would keep pacing by
            // VSync with nothing to generate from.
            if (FrameGeneration) FrameGenerationBridge.SetEnabled(false);
            CameraRedirect.JitterActive = false;

            if (presenter != null) { presenter.Rig = null; presenter = null; }
            if (presenterObject != null) { Destroy(presenterObject); presenterObject = null; }

            foreach (CameraRedirect redirect in redirects)
            {
                if (redirect == null) continue;
                redirect.Restore();
                DestroyImmediate(redirect);   // see RedirectCameras: nothing left over until end of frame
            }
            redirects.Clear();

            if (cam != null) cam.depthTextureMode &= ~addedDepthModes;

            if (captureBuffer != null)
            {
                if (cam != null) cam.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, captureBuffer);
                captureBuffer.Release();
                captureBuffer = null;
            }

            if (biasApplied) { mipmapBias.UndoMipmapBias(); biasApplied = false; }
            quality.RestoreAll();
            if (dispatchBuffer != null && nativeLink != null) nativeLink.Release();
            if (context != null) { context.Destroy(); context = null; }
            if (dispatchBuffer != null) { dispatchBuffer.Release(); dispatchBuffer = null; }

            // Released only now: until the cameras are restored they still render
            // into them, and Unity reports "Releasing render texture that is set as
            // Camera.targetTexture!".
            Release(ref lowRes);
            Release(ref upscaled);
            // After the proxy's release of DLSS, whose output it holds.
            if (dlssSharpener != null)
            {
                dlssSharpener.Release();
                dlssSharpener = null;
            }
            Release(ref motionVectors);
            Release(ref depthCopy);
            Release(ref hudLessCopy);

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

        internal static void Release(ref RenderTexture texture)
        {
            if (texture == null) return;
            texture.Release();
            Destroy(texture);
            texture = null;
        }

        private void Awake()
        {
            GameEvents.onFloatingOriginShift.Add(OnFloatingOriginShift);
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
            Teardown();
        }

        // The layers the redirected cameras render: what can end up in the image
        // the upscaler reconstructs.
        private int VisibleLayerMask()
        {
            int mask = 0;
            foreach (CameraRedirect redirect in redirects)
                if (redirect != null && redirect.Camera != null) mask |= redirect.Camera.cullingMask;
            return mask;
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

            Debug.Log(Log.Tag + " Mipmap bias " + bias.ToString("0.00") + " applied to "
                      + mipmapBias.TextureCount + " textures in "
                      + ((Time.realtimeSinceStartup - started) * 1000f).ToString("0") + " ms.");
        }

        // Jitter and dispatch parameters once per frame, before any camera
        // renders. The cameras fetch the value themselves in their OnPreRender,
        // since KSP sets the projection matrices anew every frame.
        private void Update()
        {
            if (lowRes == null) return;

            if (!RebuildWanted && !TexturesCreated()) RequestRebuild("a render texture was lost");

            // Once, a few seconds after the rig was built. See AttachHudLessCapture.
            if (Time.unscaledTime >= hudLessRecheckTime)
                RecheckHudLessCapture();

            // Checked here, not at setup: the editors' switch between VAB and SPH is a
            // load that keeps the rig.
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

            tufx.Refresh(redirects, cam, TufxAfterUpscaling && !DebugView && !PassThrough, EffectiveHdr, displaySize);
            RecordInputs();

            // The HUD-less copy only while frame generation can use it: it is a
            // blit of the whole backbuffer on every carrier camera, every frame.
            bool wantHudLess = FrameGeneration && FrameGenerationBridge.InputsUsable;
            if (wantHudLess != (hudLessBuffer != null) && presenterObject != null)
            {
                if (wantHudLess) AttachHudLessCapture(presenterObject.GetComponent<Camera>(), true);
                else DetachHudLessCapture();
            }
            RefreshOverlay();

            if (Bypass)
            {
                CameraRedirect.JitterActive = false;
                return;
            }

            // With DLSS or AMD's DLL only while the proxy's image is the one shown:
            // before its first frame and after anything stopped it, the rendered
            // image goes out as it is, and jittered it would shake.
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

            SetupDispatch();
        }

        private void SetupDispatch()
        {
            dispatch.Color = new ResourceView(lowRes);
            dispatch.Depth = new ResourceView(depthCopy);
            dispatch.MotionVectors = new ResourceView(motionVectors);
            dispatch.Exposure = ResourceView.Unassigned;
            dispatch.Reactive = ReactiveMask == UpscalerMasks.ReactiveSource.Renderers && masks.ReactiveTexture != null
                ? new ResourceView(masks.ReactiveTexture)
                : ResourceView.Unassigned;
            dispatch.TransparencyAndComposition = TransparencyMask && masks.TransparencyTexture != null
                ? new ResourceView(masks.TransparencyTexture)
                : ResourceView.Unassigned;

            dispatch.Output = upscaled != null ? new ResourceView(upscaled) : ResourceView.Unassigned;
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
                                          && masks.OpaqueOnly != null && masks.PostAlpha != null;
            dispatch.ColorOpaqueOnly = dispatch.EnableAutoReactive ? new ResourceView(masks.OpaqueOnly) : ResourceView.Unassigned;
            dispatch.ColorPostAlpha = dispatch.EnableAutoReactive ? new ResourceView(masks.PostAlpha) : ResourceView.Unassigned;
            // Reset is decided in Present, where the frame is dispatched.

            if (SystemInfo.usesReversedZBuffer)
            {
                float near = dispatch.CameraNear;
                dispatch.CameraNear = dispatch.CameraFar;
                dispatch.CameraFar = near;
            }
        }

        // Every texture whose native pointer went to the proxy at setup. Unity:
        // "render texture contents can become "lost" on certain events, like
        // loading a new level, system going to a screensaver mode, in and out of
        // fullscreen", and they "will become "not yet created" again, you can check
        // for that with IsCreated function" (ScriptReference, RenderTexture, 2019.4).
        // The rig is rebuilt rather than handing on pointers to what is gone.
        private bool TexturesCreated()
        {
            return Created(lowRes) && (PassThrough || Created(upscaled)) && Created(motionVectors) && Created(depthCopy)
                   && Created(hudLessCopy) && (dlssSharpener == null || Created(dlssSharpener.Input));
        }

        private static bool Created(RenderTexture texture)
        {
            return texture != null && texture.IsCreated();
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
            if (lowRes == null) return false;

            // Here as well as in Update: a texture lost later in the frame than the
            // rig's Update would otherwise reach the proxy by its old pointer.
            if (!RebuildWanted && !TexturesCreated()) RequestRebuild("a render texture was lost");
            if (PresentsRendered)
            {
                // Not the proxy's image: Update jitters no more for it.
                nativeShown = false;
                if (!tufx.Render(lowRes, destination, depthCopy, motionVectors))
                    Graphics.Blit(lowRes, destination);
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

            // Wanted only with the HUD-less capture attached, like the packet below.
            FrameGenerationBridge.SetEnabled(FrameGeneration && hudLessBuffer != null);

            dispatchBuffer.Clear();
            RenderTexture result = upscaled;
            // Without the upscaler the image goes out as rendered, at full size;
            // only frame generation's packet follows.
            if (PassThrough)
                result = lowRes;
            else if (Backend == UpscalerBackend.Fsr3)
                context.Dispatch(dispatch, dispatchBuffer);
            else if (!SubmitNative())
                result = lowRes;
            else if (dlssSharpener != null)
                dlssSharpener.Schedule(dispatchBuffer, Sharpness);

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
            if (FrameGeneration && hudLessBuffer != null)
            {
                Transform t = cam.transform;
                Vector3 p = t.position, u = t.up, r = t.right, f = t.forward;

                // Streamline's matrices, for DLSS frame generation only, from the
                // projection Unity drew with and without the jitter: the camera's
                // own while no jitter runs, since nonJitteredProjectionMatrix, once
                // set, is never reset (CameraRedirect). Reprojected into the frame
                // before only when that frame sent a packet and nothing cut in
                // between; otherwise into itself, which reprojects nothing.
                if (FrameGenerationBridge.DlssFrameGenerationRuns)
                {
                    Matrix4x4 projection = CameraRedirect.JitterActive ? cam.nonJitteredProjectionMatrix : cam.projectionMatrix;
                    Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(projection, false);
                    Matrix4x4 viewProjection = gpuProjection * cam.worldToCameraMatrix;
                    bool previousFrame = lastViewProjectionFrame == Time.frameCount - 1 && !dispatch.Reset;
                    StreamlineCamera.Fill(gpuProjection, viewProjection,
                                          previousFrame ? lastViewProjection : viewProjection,
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

            Graphics.ExecuteCommandBuffer(dispatchBuffer);
            RunAfterUpscalingHooks(result);

            if (!tufx.Render(result, destination, depthCopy, motionVectors))
                Graphics.Blit(result, destination);

            // After the frame is out, and fenced: an instrument does not stop a
            // frame. On a failure it says so once and stops.
            if (!instrumentFailed)
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

        private void OnDisable()
        {
            Teardown();
        }
    }
}
