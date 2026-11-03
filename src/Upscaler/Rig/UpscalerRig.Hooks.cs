using ReDefinition.Api.Internal;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The rig's part for the hooks other mods register (ReDefinition.Api.Hooks,
    // SharedFrame): the camera's two Unity messages that feed the capture, and the overlay camera's call (RigHooks).
    public partial class UpscalerRig
    {
        // The hooks the rig runs for other mods.
        private readonly RigHooks hooks = new RigHooks();

        // The frame the motion vector hooks were recorded for; -1 once RecordInputs has
        // recorded the capture anew.
        private int motionVectorHooksFrame = -1;

        // Unity calls it on the components of a camera's object before that camera culls:
        // the rig sits on the scene camera. By then the frame's state is decided
        // (SharedFrame), which a handler reads, and the distant planets and the active
        // vessel are placed (ScaledSpaceMotion, VesselMotionVectors).
        private void OnPreCull()
        {
            if (textures.LowRes == null || inputs.Capture == null) return;
            // After LateUpdate, where KSP places the scaled bodies and the flight camera.
            inputs.ScaledMotion.Sample();
            inputs.CloudMotion.Sample(cam);
            if (motionVectorHooksFrame == Time.frameCount) return;
            motionVectorHooksFrame = Time.frameCount;
            SharedFrame.EnsureBegun();
            // The vessel's own motion vectors before the hooks, so a mod's for a part
            // are drawn over them.
            vesselMotion.Begin(cam, renderSize);
            Probe.GpuProfiler.Mark(inputs.Capture, "ReDefinition inputs: capture before the vessel's depth");
            vesselMotion.RecordDepth(inputs.Capture, textures.DepthCopy);
            Probe.GpuProfiler.Mark(inputs.Capture, "ReDefinition inputs: the vessel's depth");
            vesselMotion.RecordRepair(inputs.Capture, textures.MotionVectors, textures.DepthCopy);
            Probe.GpuProfiler.Mark(inputs.Capture, "ReDefinition inputs: the vessel's motion vectors");
            hooks.RecordMotionVectors(inputs.Capture, textures.MotionVectors, textures.DepthCopy, cam);
            Probe.GpuProfiler.Mark(inputs.Capture, "ReDefinition inputs: other mods' motion vector hooks");
            // After the hooks: they read the motion vectors as the upscaler gets them.
            // Diagnostic measurements only (ReDefinitionAddon.DebugMode).
            if (ReDefinitionAddon.DebugMode)
            {
                motionAudit.Record(inputs.Capture, cam, textures.MotionVectors, textures.DepthCopy, renderSize);
                vesselMotion.RecordAudit(inputs.Capture, textures.MotionVectors, textures.DepthCopy);
            }
            vesselMotion.End();
            // The camera's matrices for the vessel's shadow, without the jitter.
            if (hudLess.Attached && hudLess.WithShadow)
            {
                vesselShadow.Begin(cam, renderSize);
                hudLess.RefreshComposition(textures.HudLess, vesselShadow, renderSize);
            }
        }

        // After every OnPreCull, where Unity has built the shadow cascades from the sun's
        // direction as it stands now (CameraRedirect): the vessel's shadow for frame
        // generation, appended to the capture.
        private int vesselShadowFrame = -1;

        // The scene camera about to render: once a frame, the vessel's shadow into the capture while the
        // HUD-less capture was recorded with it.
        private void OnPreRender()
        {
            if (textures.LowRes == null || inputs.Capture == null || vesselShadowFrame == Time.frameCount) return;
            vesselShadowFrame = Time.frameCount;
            if (hudLess.Attached && hudLess.WithShadow)
                vesselShadow.Record(inputs.Capture, textures.DepthCopy, textures.MotionVectors);
        }

        // From Update: the overlay camera made, placed and taken down as handlers come
        // and go.
        private void RefreshOverlay()
        {
            hooks.RefreshOverlay(this, cameras.Presenter);
        }

        // From the overlay camera's OnPreCull: every scene camera of the frame has
        // rendered, and the handlers see the frame's final camera.
        internal void FillOverlay()
        {
            hooks.FillOverlay(cam);
        }
    }
}
