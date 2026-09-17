using System;
using UnityEngine;

namespace ReDefinition
{
    // The rig's part for the upscalers that run in the dxgi.dll proxy, DLSS and
    // AMD's DLL: the textures handed to it, the packets, and what its state means.
    public partial class UpscalerRig
    {
        // The proxy hands the rig's textures to its D3D12 device, which relies on
        // their underlying ID3D11Resource staying the same. Captured once at setup and
        // compared on demand -- GetNativeTexturePtr flushes the render thread,
        // so it must never be called per frame.
        private IntPtr upscaledHandle;
        private IntPtr lowResHandle;
        private IntPtr depthHandle;
        private IntPtr motionHandle;
        private int handleFrame = -1;

        // RCAS after DLSS, which sharpens nothing itself (RcasSharpener): DLSS writes
        // into its input, RCAS from there into the output. Only with sharpening on,
        // which the slider decides at the rig's setup.
        private RcasSharpener dlssSharpener;
        private IntPtr dlssSharpenInputHandle;

        // DLSS (DlssBridge) and AMD's DLL (AmdUpscalerBridge): this frame's packet
        // into the dispatch buffer, where the proxy upscales it on Unity's render
        // thread. Shown is the proxy's output once it has reported an upscaled
        // frame; the image without upscaling before that and after anything
        // stopped it -- never an output nothing has written. The same jitter and
        // motion vector scale as FSR's, in pixels at render size (NVIDIA's DLSS
        // Programming Guide, 3.6.1 and 3.7.3; AMD's ffx_upscale.h). DLSS with auto
        // exposure always: there is no exposure value to hand over (guide 3.9,
        // 3.10). AMD's DLL with FSR's auto exposure as the player set it.
        //
        // The proxy's state outlives a rig: for the first frames it can still be
        // the one of the rig before, or of its release. Packets keep going while it
        // is stopped; the proxy starts again with the next frame that works.
        private const int NativeStateTrustedAfter = 4;

        // How long a technique may stay stopped before FSR 3 takes its place: one
        // that has upscaled frames in this rig, and one that has not. Never at the
        // first -1 -- that can still be the rig's before, until the render thread
        // has run its release.
        private const float NativeStoppedSeconds = 2f;
        private const float NativeNeverShownSeconds = 1f;

        private NativeUpscalerLink nativeLink;
        private int nativeSubmitted;
        private int nativeState;
        private bool nativeShown;
        private bool nativeEverShown;
        private string nativeStopped;
        private float nativeStoppedSince = -1f;
        private float nextNativeCheck;

        private bool SubmitNative()
        {
            int state = nativeSubmitted < NativeStateTrustedAfter ? 0 : nativeLink.State();
            if (state != nativeState || (state < 0 && Time.unscaledTime >= nextNativeCheck))
            {
                nativeState = state;
                nextNativeCheck = Time.unscaledTime + 1f;
                NoteNativeState(state);
            }

            uint flags = (EffectiveHdr ? NativeUpscalerLink.FlagHdr : 0u)
                         | (SystemInfo.usesReversedZBuffer ? NativeUpscalerLink.FlagDepthInverted : 0u)
                         | (Backend == UpscalerBackend.Dlss || AutoExposure ? NativeUpscalerLink.FlagAutoExposure : 0u)
                         | (dispatch.Reset ? NativeUpscalerLink.FlagReset : 0u);
            bool submitted;
            if (Backend == UpscalerBackend.Dlss)
            {
                DlssBridge.DlssPacket packet = new DlssBridge.DlssPacket
                {
                    Colour = lowResHandle,
                    // With sharpening, into RCAS's input, which writes the output.
                    Output = dlssSharpener != null ? dlssSharpenInputHandle : upscaledHandle,
                    Depth = depthHandle,
                    MotionVectors = motionHandle,
                    RenderWidth = (uint)renderSize.x,
                    RenderHeight = (uint)renderSize.y,
                    OutputWidth = (uint)displaySize.x,
                    OutputHeight = (uint)displaySize.y,
                    Quality = DlssBridge.Quality(QualityMode),
                    Preset = (uint)DlssPreset,
                    Flags = flags,
                    JitterX = dispatch.JitterOffset.x,
                    JitterY = dispatch.JitterOffset.y,
                    MotionVectorScaleX = dispatch.MotionVectorScale.x,
                    MotionVectorScaleY = dispatch.MotionVectorScale.y,
                };
                submitted = DlssBridge.Submit(dispatchBuffer, ref packet);
            }
            else
            {
                AmdUpscalerBridge.AmdPacket packet = new AmdUpscalerBridge.AmdPacket
                {
                    Colour = lowResHandle,
                    Output = upscaledHandle,
                    Depth = depthHandle,
                    MotionVectors = motionHandle,
                    RenderWidth = (uint)renderSize.x,
                    RenderHeight = (uint)renderSize.y,
                    OutputWidth = (uint)displaySize.x,
                    OutputHeight = (uint)displaySize.y,
                    Flags = flags,
                    JitterX = dispatch.JitterOffset.x,
                    JitterY = dispatch.JitterOffset.y,
                    MotionVectorScaleX = dispatch.MotionVectorScale.x,
                    MotionVectorScaleY = dispatch.MotionVectorScale.y,
                    // RCAS as with FSR 3; the proxy stops at AMD's maximum of 1.
                    Sharpness = Sharpening ? Sharpness : 0f,
                    FrameTimeDeltaMs = dispatch.FrameTimeDelta * 1000f,
                    CameraNear = dispatch.CameraNear,
                    CameraFar = dispatch.CameraFar,
                    VerticalFovRadians = dispatch.CameraFovAngleVertical,
                };
                submitted = AmdUpscalerBridge.Submit(dispatchBuffer, ref packet);
            }

            if (submitted) nativeSubmitted++;
            nativeShown = submitted && state > 0;
            if (nativeShown) nativeEverShown = true;
            return nativeShown;
        }

        // On every change of the proxy's state, and once a second while it is
        // stopped.
        private void NoteNativeState(int state)
        {
            string name = UpscalerBackends.Name(Backend);

            // DLSS at another size than it asks for: a new rig, which renders at it
            // (Setup). Outside its range DLSS evaluates nothing, and above the size
            // at creation presets L and M reallocate every frame (Dlss.cpp).
            Vector2Int optimal;
            if (state != 0 && Backend == UpscalerBackend.Dlss
                && DlssBridge.RenderSize(displaySize, DlssBridge.Quality(QualityMode), out optimal)
                && optimal != renderSize)
            {
                RequestRebuild("DLSS asks for " + optimal.x + "x" + optimal.y + " in this mode, the rig renders "
                               + renderSize.x + "x" + renderSize.y);
                return;
            }

            if (state < 0)
            {
                string reason = nativeLink.Describe();
                if (nativeStoppedSince < 0f) nativeStoppedSince = Time.unscaledTime;

                // What cannot upscale does not stay on screen: FSR 3 runs in its
                // place (UpscalerAddon, which says when it tries again).
                // A failure that stands (-2) stands for this rig as for the one
                // before: no waiting for it.
                float allowed = state == -2 ? 0f : nativeEverShown ? NativeStoppedSeconds : NativeNeverShownSeconds;
                if (Time.unscaledTime - nativeStoppedSince >= allowed)
                {
                    FallbackReason = reason;
                    FallbackStable = state == -2;
                    RequestRebuild(name + " stopped, FSR 3 runs instead: " + reason);
                    return;
                }

                if (nativeStopped == reason) return;
                nativeStopped = reason;
                Status = name + " stopped: " + reason;
                Debug.LogWarning(UpscalerProbe.Tag + " " + name + " stopped, the image is shown without upscaling: " + reason);
            }
            else
            {
                nativeStoppedSince = -1f;
                if (state > 0 && nativeStopped != null)
                {
                    nativeStopped = null;
                    Status = "active";
                    Debug.Log(UpscalerProbe.Tag + " " + name + " running again.");
                }
            }
        }
    }
}
