using UnityEngine;

namespace ReDefinition.Upscaler
{
    // Writes the finished image into the frame buffer, from a camera of its own.
    //
    // In the OnPostRender of the last 3D camera its RenderTexture and its viewport
    // are still the active target: a Graphics.Blit there lands in a rectangle at
    // render resolution in the bottom left.
    //
    // A dedicated camera without a targetTexture gets the frame buffer at full
    // size as its target from Unity. It draws nothing itself (empty culling
    // mask), it exists only so that OnRenderImage is called at the right point in
    // the render order with the right target: after all 3D cameras, before the UI
    // cameras.
    public class UpscalerPresenter : MonoBehaviour
    {
        // The integration this camera belongs to, set as the integration makes the camera.
        public UpscalerIntegration Integration;

        // The integration presents its image into the frame buffer; where it does not, the camera's own image passes
        // through unchanged.
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (Integration == null || !Integration.Present(destination))
                Graphics.Blit(source, destination);
        }
    }
}
