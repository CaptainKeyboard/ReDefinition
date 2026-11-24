using UnityEngine;

namespace ReDefinition.Upscaler
{
    // On the integration's overlay camera: the handlers other mods registered for overlays
    // fill its buffer as it culls (UpscalerIntegration.FillOverlay).
    public class UpscalerOverlay : MonoBehaviour
    {
        // The integration this camera belongs to, set as the integration makes the camera.
        public UpscalerIntegration Integration;

        // Has the integration fill the overlay buffer before this camera draws.
        private void OnPreCull()
        {
            if (Integration != null) Integration.FillOverlay();
        }
    }
}
