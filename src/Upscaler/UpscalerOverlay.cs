using UnityEngine;

namespace ReDefinition.Upscaler
{
    // On the rig's overlay camera: the handlers other mods registered for overlays
    // fill its buffer as it culls (UpscalerRig.FillOverlay).
    public class UpscalerOverlay : MonoBehaviour
    {
        // The rig this camera belongs to, set as the rig makes the camera.
        public UpscalerRig Rig;

        // Has the rig fill the overlay buffer before this camera draws.
        private void OnPreCull()
        {
            if (Rig != null) Rig.FillOverlay();
        }
    }
}
