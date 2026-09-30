using System;
using System.Runtime.InteropServices;
using System.Text;
using ReDefinition.Bridges;
using ReDefinition.Core;
using ReDefinition.Raytracing;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // DLSS Ray Reconstruction in DLSS Super Resolution's place while the scene camera
    // is raytraced (docs/development/raytracing.md, "Denoising"): the raytraced signals
    // go into the lighting undenoised, and Ray Reconstruction denoises and upscales the
    // finished frame in one step. Its guides come from the raytraced frame of the same
    // index (RtCamera.ReconstructionLease), so it needs no G-buffer. It runs in the
    // proxy through Streamline, where DLSS frame generation has set Streamline up.
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
        private const float ReconstructionGiveUpSeconds = 1f;
        private const float ReconstructionRetrySeconds = 10f;

        private PacketRing reconstructRing;
        private RtNative.ReconstructPacket reconstructPacket;
        private readonly StringBuilder reconstructionWhy = new StringBuilder(256);
        private float nextReconstructionCheck;
        private bool reconstructionReady;
        private float reconstructionGaveUpAt = float.NegativeInfinity;
        private ulong reconstructedSeen;
        private int reconstructedAdvanced = -1;
        private float reconstructedAdvancedAt = -1f;
        private bool reconstructedEver;
        private Matrix4x4 lastReconstructViewProjection;
        private int lastReconstructFrame = -1;

        internal bool Reconstructing { get; private set; }

        // DLSS's turn: Ray Reconstruction where it can take this frame, and whether the
        // proxy has shown one of its frames lately. False where DLSS Super Resolution
        // takes the frame; with its history reset where Ray Reconstruction had it.
        private bool SubmitReconstruction(ref uint flags, out bool shown)
        {
            shown = false;
            RtCamera raytraced = RtCamera.Current;
            bool wanted = raytraced != null && raytraced.isActiveAndEnabled && raytraced.Camera == cam
                          && Time.unscaledTime - reconstructionGaveUpAt > ReconstructionRetrySeconds && ReconstructionReady();
            if (wanted) raytraced.ReconstructionLease = Time.frameCount;
            bool was = Reconstructing;
            Reconstructing = wanted && raytraced.GuidesFrame == Time.frameCount;
            if (Reconstructing != was) flags |= NativeUpscalerLink.FlagReset;
            if (!Reconstructing) return false;

            if (reconstructRing == null)
            {
                reconstructRing = new PacketRing(Marshal.SizeOf(typeof(RtNative.ReconstructPacket)));
                reconstructPacket = RtNative.NewReconstructPacket();
            }
            FillReconstruction(raytraced, flags);
            reconstructRing.Issue(dispatchBuffer, RtNative.RenderEvent, RtNative.ReconstructEvent, ref reconstructPacket);

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
                Debug.Log(Log.Tag + " DLSS Ray Reconstruction " + (reconstructedEver ? "stopped showing frames" : "showed no frame")
                          + "; DLSS Super Resolution for " + ReconstructionRetrySeconds + " s. " + raytraced.Status());
            }
            return true;
        }

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
                    Debug.Log(Log.Tag + " DLSS Ray Reconstruction " + (reconstructionReady ? "ready" : "not available: " + reconstructionWhy)
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
            p.Flags = ((flags & NativeUpscalerLink.FlagReset) != 0 ? RtNative.FlagReset : 0u)
                      | ((flags & NativeUpscalerLink.FlagHdr) != 0 ? 0u : RtNative.ReconstructLdr);
            p.RenderWidth = (uint)renderSize.x;
            p.RenderHeight = (uint)renderSize.y;
            p.OutputWidth = (uint)displaySize.x;
            p.OutputHeight = (uint)displaySize.y;
            // sl::DLSSMode counts from eOff, one above NGX's quality values.
            p.Mode = (uint)(DlssBridge.Quality(QualityMode) + 1);
            p.Preset = 0;   // sl::DLSSDPreset::eDefault
            Array.Copy(raytraced.GuidesClipToWorld, p.ClipToWorld, 16);

            Matrix4x4 projection = CameraRedirect.JitterActive ? cam.nonJitteredProjectionMatrix : cam.projectionMatrix;
            Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(projection, true);
            Matrix4x4 view = cam.worldToCameraMatrix;
            Matrix4x4 viewProjection = gpuProjection * view;
            bool previousFrame = lastReconstructFrame == Time.frameCount - 1 && (flags & NativeUpscalerLink.FlagReset) == 0;
            StreamlineCamera.Fill(gpuProjection, viewProjection, previousFrame ? lastReconstructViewProjection : viewProjection,
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

            p.Colour = lowResHandle;
            p.Depth = depthHandle;
            p.Motion = motionHandle;
            p.GBuffer0 = p.GBuffer1 = p.GBuffer2 = p.Specular = IntPtr.Zero;
            p.Output = dlssSharpener != null ? dlssSharpenInputHandle : upscaledHandle;
            reconstructPacket = p;
        }

        private static void Write(Vector3 v, float[] target)
        {
            target[0] = v.x;
            target[1] = v.y;
            target[2] = v.z;
            target[3] = 0f;
        }
    }
}
