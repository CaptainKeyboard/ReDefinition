using ReDefinition.Proxy;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The rig's part for frame generation's HUD-less copy: when the capture is attached, detached and attached
    // again (HudLessCapture).
    public partial class UpscalerRig
    {
        // Frame generation's HUD-less copy of the backbuffer.
        private readonly HudLessCapture hudLess = new HudLessCapture();

        // Attaches the capture to the presenter and the effect cameras behind it.
        private void AttachHudLess(bool recheckLater)
        {
            hudLess.Attach(cameras.Presenter, recheckLater, textures.HudLess, vesselShadow, renderSize);
        }

        // Once, a few seconds after the capture was attached: the one look at the cameras.
        private void RecheckHudLess()
        {
            hudLess.Recheck(cameras.Presenter, textures.HudLess, vesselShadow, renderSize);
        }

        // From Update. The HUD-less copy only while frame generation can use it: it is a
        // blit of the whole backbuffer on every carrier camera, every frame.
        private void SyncHudLess()
        {
            bool wantHudLess = FrameGeneration && FrameGenerationBridge.InputsUsable;
            if (wantHudLess != hudLess.Attached && cameras.Presenter != null)
            {
                if (wantHudLess) AttachHudLess(true);
                else hudLess.Detach();
            }
            // The vessel's shadow switched in or out of the HUD-less copy (VesselShadowLayer).
            else if (hudLess.Attached && hudLess.WithShadow != VesselShadowLayer.Enabled && cameras.Presenter != null)
                AttachHudLess(false);
        }
    }
}
