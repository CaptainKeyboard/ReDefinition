using System;
using System.Runtime.InteropServices;
using System.Text;
using ReDefinition.Proxy;
using ReDefinition.Core;
using ReDefinition.Raytracing;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Upscaler
{
    // DLSS Ray Reconstruction in DLSS Super Resolution's place while the scene camera
    // is raytraced (docs/development/raytracing.md, "Denoising"): the raytraced signals
    // go into the lighting undenoised, and Ray Reconstruction denoises and upscales the
    // finished frame in one step. Its guides come from the raytraced frame of the same
    // index (RtCamera.ReconstructionLease), so it needs no G-buffer. It runs in the
    // proxy: through Streamline where Streamline's swapchain presents -- DLSS frame
    // generation, or frame generation off -- and through NGX directly beside FSR's or
    // XeSS's frame generation (rrPath in ReDefinitionProxy.ini).
    //
    // A frame without guides of this frame -- the first after switching over -- goes
    // through DLSS Super Resolution. Where Ray Reconstruction shows no new frame for a
    // second, Super Resolution takes over for ten seconds, and the log says why. Each
    // switch starts the history afresh.
    //
    // The raytraced light in frame N's colour was traced for frame N-1 and carried over
    // (RtCamera); the guides, the reflections' hit distance with them, are frame N's.
    public partial class UpscalerRig
    {
        // How long Ray Reconstruction may show no new frame before Super Resolution takes over, and for how long.
        private const float ReconstructionGiveUpSeconds = 1f;
        private const float ReconstructionRetrySeconds = 10f;

        // Shared by every rig: a ring is never freed (PacketRing), so one per rig would be
        // left behind with each rebuild.
        private static PacketRing reconstructRing;
        private RtNative.ReconstructPacket reconstructPacket;
        private bool reconstructPacketMade;
        // Whether frames went to Ray Reconstruction since its resources were last freed.
        private bool reconstructionSent;
        private readonly StringBuilder reconstructionWhy = new StringBuilder(256);
        private float nextReconstructionCheck;
        private bool reconstructionReady;
        private float reconstructionGaveUpAt = float.NegativeInfinity;
        private ulong reconstructedSeen;
        private int reconstructedAdvanced = -1;
        private float reconstructedAdvancedAt = -1f;
        private bool reconstructedEver;
        // Ray Reconstruction gave up in the last frame: Super Resolution starts its history
        // afresh in this one, which the give-up frame itself could not tell it.
        private bool handedBack;
        private bool loggedLdr;
        private Matrix4x4 lastReconstructViewProjection;
        private int lastReconstructFrame = -1;

        // Whether Ray Reconstruction takes this frame, in DLSS Super Resolution's place.
        internal bool Reconstructing { get; private set; }

        // sl::DLSSDPreset: 0 eDefault; -redefinitionProbeRrPreset <4 to 6> (D to F) for a
        // probe's run.
        private static readonly uint ReconstructionPreset = ProbeReconstructionPreset();

        // The preset after -redefinitionProbeRrPreset on the command line, or 0 (eDefault).
        private static uint ProbeReconstructionPreset()
        {
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "-redefinitionProbeRrPreset");
            uint preset;
            return at >= 0 && at + 1 < args.Length && uint.TryParse(args[at + 1], out preset) ? preset : 0u;
        }

        // Whether the raytraced frame may go to Ray Reconstruction at all; without it the
        // raytracing's own denoiser and DLSS Super Resolution take it. Off with
        // -redefinitionProbeNoRayReconstruction, for a probe's run; the player's "Ray
        // Reconstruction" (OwnSettings.RayReconstruction) is asked beside it (SubmitReconstruction).
        internal static bool UseRayReconstruction =
            !CommandLine.Has("-redefinitionProbeNoRayReconstruction");

        // DLSS's turn: Ray Reconstruction where it can take this frame, and whether the
        // proxy has shown one of its frames lately. False where DLSS Super Resolution
        // takes the frame; with its history reset where Ray Reconstruction had it.
        private bool SubmitReconstruction(ref uint flags, out bool shown)
        {
            shown = false;
            if (handedBack)
            {
                handedBack = false;
                flags |= NativeUpscalerLink.FlagReset;
            }
            RtCamera raytraced = RtCamera.Current;
            // HDR only: "DLSS-RR only support HDR inputs ... colorBuffersHDR **must** be set
            // to eTrue" (ProgrammingGuideDLSS_RR.md). An LDR camera -- KSP's editors --
            // keeps DLSS Super Resolution.
            bool raytracedHere = raytraced != null && raytraced.isActiveAndEnabled && raytraced.Camera == cam;
            if (raytracedHere && !EffectiveHdr && !loggedLdr)
            {
                loggedLdr = true;
                Debug.Log(Log.Tag + " DLSS Ray Reconstruction takes HDR frames only; this camera renders LDR: DLSS Super"
                          + " Resolution.");
            }
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            // rayReconstructionFrom: in the least raytracing mode the player chose and above.
            bool wanted = raytracedHere && EffectiveHdr && UseRayReconstruction
                          && (addon == null || (addon.Live.RayReconstruction && addon.Live.Raytracing >= addon.Live.RayReconstructionFrom))
                          && Time.unscaledTime - reconstructionGaveUpAt > ReconstructionRetrySeconds && ReconstructionReady();
            if (wanted) raytraced.ReconstructionLease = Time.frameCount;
            bool was = Reconstructing;
            Reconstructing = wanted && raytraced.GuidesFrame == Time.frameCount;
            if (Reconstructing != was) flags |= NativeUpscalerLink.FlagReset;
            if (!Reconstructing)
            {
                ReleaseReconstruction(dispatchBuffer);
                return false;
            }

            if (reconstructRing == null)
                reconstructRing = new PacketRing(Marshal.SizeOf(typeof(RtNative.ReconstructPacket)));
            if (!reconstructPacketMade)
            {
                reconstructPacket = RtNative.NewReconstructPacket();
                reconstructPacketMade = true;
            }
            FillReconstruction(raytraced, flags);
            reconstructRing.Issue(dispatchBuffer, RtNative.RenderEvent, RtNative.ReconstructEvent, ref reconstructPacket);
            reconstructionSent = true;

            // Reconstructed frames are counted on the render thread, a frame or two late.
            if (!was)
            {
                reconstructedAdvancedAt = Time.unscaledTime;
                Debug.Log(Log.Tag + " DLSS Ray Reconstruction takes the frames, " + renderSize.x + "x" + renderSize.y + " to "
                          + displaySize.x + "x" + displaySize.y + ".");
            }
            ulong count = RtNative.KspRtReconstructedFrames();
            if (count != reconstructedSeen)
            {
                reconstructedSeen = count;
                reconstructedAdvanced = Time.frameCount;
                reconstructedAdvancedAt = Time.unscaledTime;
                reconstructedEver = true;
            }
            shown = reconstructedAdvanced >= 0 && Time.frameCount - reconstructedAdvanced <= 3;
            if (!shown && Time.unscaledTime - reconstructedAdvancedAt > ReconstructionGiveUpSeconds)
            {
                reconstructionGaveUpAt = Time.unscaledTime;
                Reconstructing = false;
                handedBack = true;
                Debug.Log(Log.Tag + " DLSS Ray Reconstruction " + (reconstructedEver ? "stopped showing frames" : "showed no frame")
                          + "; DLSS Super Resolution for " + ReconstructionRetrySeconds + " s. " + raytraced.Status());
                ReleaseReconstruction(dispatchBuffer);
            }
            return true;
        }

        // Streamline keeps Ray Reconstruction's resources when its frames stop: "this
        // does NOT release any resources, for that please use slFreeResources"
        // (ProgrammingGuideDLSS_RR.md 5.0), and NGX keeps its feature until it is
        // released. The proxy frees them on the render thread,
        // after the last frame sent (Raytracing::OnReconstructRelease): into buffer
        // behind that frame, or on its own where the rig goes.
        private void ReleaseReconstruction(CommandBuffer buffer)
        {
            if (!reconstructionSent) return;
            reconstructionSent = false;
            if (buffer != null)
            {
                buffer.IssuePluginEventAndData(RtNative.RenderEvent, RtNative.ReconstructReleaseEvent, IntPtr.Zero);
                return;
            }
            var own = new CommandBuffer { name = "ReDefinition.RayReconstruction release" };
            own.IssuePluginEventAndData(RtNative.RenderEvent, RtNative.ReconstructReleaseEvent, IntPtr.Zero);
            Graphics.ExecuteCommandBuffer(own);
            own.Release();
        }

        // Whether the proxy offers Ray Reconstruction with a packet of this layout, asked once a second; the log
        // says when the answer changes, and why.
        private bool ReconstructionReady()
        {
            if (Time.unscaledTime >= nextReconstructionCheck)
            {
                nextReconstructionCheck = Time.unscaledTime + 1f;
                bool was = reconstructionReady;
                try
                {
                    reconstructionWhy.Length = 0;
                    reconstructionReady = RtNative.KspRtReconstructPacketSize() == Marshal.SizeOf(typeof(RtNative.ReconstructPacket))
                                          && RtNative.KspRtReconstructionAvailable(reconstructionWhy, reconstructionWhy.Capacity) == 1;
                }
                catch (Exception e)
                {
                    reconstructionReady = false;
                    reconstructionWhy.Length = 0;
                    reconstructionWhy.Append(e.GetType().Name);
                }
                if (was != reconstructionReady)
                    Debug.Log(Log.Tag + " DLSS Ray Reconstruction " + (reconstructionReady ? reconstructionWhy.ToString() : "not available: " + reconstructionWhy)
                              + ".");
            }
            return reconstructionReady;
        }

        // The packet as Raytracing.h lays it out: the matrices as Streamline takes them
        // (StreamlineCamera), from the projection without the jitter, as the render
        // textures hold the frame (GL.GetGPUProjectionMatrix(..., true)), like the
        // raytracing's own; the jitter and the motion vectors' scale as DLSS Super
        // Resolution gets them, the scale to Streamline's normalised units.
        private void FillReconstruction(RtCamera raytraced, uint flags)
        {
            RtNative.ReconstructPacket p = reconstructPacket;
            p.FrameIndex = raytraced.GuidesIndex;
            // The G-buffer's smoothness as the raytraced frame took it, for guides from it.
            p.Flags = ((flags & NativeUpscalerLink.FlagReset) != 0 ? RtNative.FlagReset : 0u)
                      | (raytraced.LegacySmoothness() && !RtCamera.DeferredSmoothness ? RtNative.FlagLegacySmoothness : 0u);
            p.RenderWidth = (uint)renderSize.x;
            p.RenderHeight = (uint)renderSize.y;
            p.OutputWidth = (uint)displaySize.x;
            p.OutputHeight = (uint)displaySize.y;
            // sl::DLSSMode counts from eOff, one above NGX's quality values.
            p.Mode = (uint)(DlssBridge.Quality(QualityMode) + 1);
            p.Preset = ReconstructionPreset;
            Array.Copy(raytraced.GuidesClipToWorld, p.ClipToWorld, 16);

            Matrix4x4 projection = CameraRedirect.JitterActive ? cam.nonJitteredProjectionMatrix : cam.projectionMatrix;
            Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(projection, true);
            Matrix4x4 view = cam.worldToCameraMatrix;
            Matrix4x4 viewProjection = gpuProjection * view;
            bool previousFrame = lastReconstructFrame == Time.frameCount - 1 && (flags & NativeUpscalerLink.FlagReset) == 0;
            StreamlineCamera.Fill(gpuProjection, viewProjection, previousFrame ? AcrossOriginShift(lastReconstructViewProjection) : viewProjection,
                                  p.ViewToClip, p.ClipToView, p.ClipToPrevClip, p.PrevClipToClip);
            lastReconstructViewProjection = viewProjection;
            lastReconstructFrame = Time.frameCount;
            // Streamline's view looks down +z (StreamlineCamera).
            Matrix4x4 viewPlusZ = StreamlineCamera.FlipZ() * view;
            StreamlineCamera.WriteRowMajor(viewPlusZ, p.WorldToView);
            StreamlineCamera.WriteRowMajor(StreamlineCamera.Invert(viewPlusZ), p.ViewToWorld);

            p.Jitter[0] = dispatch.JitterOffset.x;
            p.Jitter[1] = dispatch.JitterOffset.y;
            p.MotionScale[0] = dispatch.MotionVectorScale.x / renderSize.x;
            p.MotionScale[1] = dispatch.MotionVectorScale.y / renderSize.y;
            Transform t = cam.transform;
            Write(t.up, p.Up);
            Write(t.right, p.Right);
            Write(t.forward, p.Forward);
            p.NearPlane = cam.nearClipPlane;
            p.FarPlane = cam.farClipPlane;
            p.VerticalFov = dispatch.CameraFovAngleVertical;

            p.Colour = textures.LowResHandle;
            p.Depth = textures.DepthHandle;
            p.Motion = textures.MotionHandle;
            p.GBuffer0 = p.GBuffer1 = p.GBuffer2 = p.Specular = IntPtr.Zero;
            p.Output = nativeSharpener != null ? nativeSharpenInputHandle : textures.UpscaledHandle;
            reconstructPacket = p;
        }

        // A direction into the packet's four floats, w zero.
        private static void Write(Vector3 v, float[] target)
        {
            target[0] = v.x;
            target[1] = v.y;
            target[2] = v.z;
            target[3] = 0f;
        }
    }
}
