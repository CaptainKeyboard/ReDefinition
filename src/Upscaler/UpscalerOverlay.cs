using UnityEngine;

namespace ReDefinition.Upscaler
{
    // On the rig's overlay camera: the handlers other mods registered for overlays
    // fill its buffer as it culls (UpscalerRig.FillOverlay).
    public class UpscalerOverlay : MonoBehaviour
    {
        public UpscalerRig Rig;

        private void OnPreCull()
        {
            if (Rig != null) Rig.FillOverlay();
        }
    }
}
