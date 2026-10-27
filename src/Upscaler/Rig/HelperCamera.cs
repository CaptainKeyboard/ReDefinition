using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The cameras the rig and the TUFX split put on objects of their own: the presenter
    // (UpscalerCameras), the overlay (UpscalerRig.RefreshOverlay) and the host of TUFX's
    // layer (TufxPostProcessing). None draws a scene object; each exists for its events,
    // OnRenderImage or command buffers, and for the place it takes in the camera order.
    internal static class HelperCamera
    {
        // A camera on that object which culls everything, clears nothing, renders to the frame
        // buffer and does no occlusion culling, depth texture or MSAA. allowHdr says whether it
        // works in HDR; the depth and any target are the caller's.
        internal static Camera AddTo(GameObject owner, bool allowHdr)
        {
            Camera camera = owner.AddComponent<Camera>();
            camera.cullingMask = 0;
            camera.clearFlags = CameraClearFlags.Nothing;
            camera.targetTexture = null;
            camera.allowHDR = allowHdr;
            camera.allowMSAA = false;
            camera.useOcclusionCulling = false;
            camera.depthTextureMode = DepthTextureMode.None;
            return camera;
        }
    }
}
