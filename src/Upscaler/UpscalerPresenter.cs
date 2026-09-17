using UnityEngine;

namespace ReDefinition
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
        public UpscalerRig Rig;

        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (Rig == null || !Rig.Present(destination))
                Graphics.Blit(source, destination);
        }
    }
}
