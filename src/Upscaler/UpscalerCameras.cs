using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ReDefinition.Upscaler
{
    // The rig's cameras: the 3D stack redirected into the render-size texture
    // (CameraRedirect), and the presenter camera behind it that writes the result into
    // the frame buffer before the UI cameras draw on top (UpscalerPresenter).
    internal sealed class UpscalerCameras
    {
        // Every camera that contributes to the 3D image. UI and vector cameras
        // stay out: they draw at full resolution on top of the upscaled image.
        //
        // The list follows KerbalVR, which redirects the same stack into its own
        // RenderTextures:
        //   flight external  GalaxyCamera, Camera ScaledSpace, Camera 00
        //   flight IVA       plus InternalCamera
        //   editor           GalaxyCamera, sceneryCam, Main Camera, markerCam
        //   main menu        GalaxyCamera, Landscape Camera
        //
        // FXCamera and FXDepthCamera draw no part of the scene image and stay out.
        private static readonly string[] SceneCameras =
        {
            "GalaxyCamera", "Camera ScaledSpace", "Camera 01", "Camera 00",
            "InternalCamera", "sceneryCam", "Main Camera", "markerCam",
            "Landscape Camera"
        };

        internal static bool IsSceneCamera(string name)
        {
            return Array.IndexOf(SceneCameras, name) >= 0;
        }

        private readonly List<CameraRedirect> redirects = new List<CameraRedirect>();
        private GameObject presenterObject;
        private Camera presenterCamera;
        private UpscalerPresenter presenter;

        internal List<CameraRedirect> Redirects
        {
            get { return redirects; }
        }

        // The presenter camera, or null.
        internal Camera Presenter
        {
            get { return presenterObject != null ? presenterCamera : null; }
        }

        // The 3D stack into target, and the presenter for rig behind it; false where no
        // camera of the stack is there.
        internal bool Redirect(RenderTexture target, UpscalerRig rig)
        {
            redirects.Clear();
            float lastDepth = float.MinValue;
            string lastName = "none";

            foreach (string wanted in SceneCameras)
            {
                foreach (Camera other in Camera.allCameras)
                {
                    if (other == null || other.name != wanted) continue;

                    // Removed at once rather than reused: Destroy takes effect at the
                    // end of the frame, and a component pending destruction runs its
                    // OnDisable then and undoes the fresh redirect.
                    foreach (CameraRedirect stale in other.gameObject.GetComponents<CameraRedirect>())
                        Object.DestroyImmediate(stale);

                    // Unity's mouse events still reach it: UnityMouseEvents.
                    CameraRedirect redirect = other.gameObject.AddComponent<CameraRedirect>();
                    redirect.Redirect(target);
                    redirects.Add(redirect);

                    if (other.depth > lastDepth) { lastDepth = other.depth; lastName = other.name; }
                }
            }

            if (redirects.Count == 0) return false;

            // The presenter camera, directly behind the 3D stack. It draws nothing
            // (empty culling mask); its OnRenderImage has the frame buffer as its
            // target, at full resolution.
            presenterObject = new GameObject("ReDefinitionPresenter");
            Camera presenterCam = presenterObject.AddComponent<Camera>();
            presenterCam.cullingMask = 0;
            presenterCam.clearFlags = CameraClearFlags.Nothing;
            presenterCam.depth = lastDepth + 0.1f;
            presenterCam.targetTexture = null;
            presenterCam.allowHDR = true;
            presenterCam.allowMSAA = false;
            presenterCam.useOcclusionCulling = false;
            presenterCam.depthTextureMode = DepthTextureMode.None;

            presenterCamera = presenterCam;
            presenter = presenterObject.AddComponent<UpscalerPresenter>();
            presenter.Rig = rig;

            Debug.Log(Log.Tag + " " + redirects.Count + " cameras redirected; last in the stack '"
                      + lastName + "' (depth " + lastDepth.ToString("0.##")
                      + "), presenter camera at depth " + presenterCam.depth.ToString("0.##") + ".");
            return true;
        }

        // The projection a camera of the stack rendered this frame with, its jitter in it;
        // false where it was not jittered.
        internal bool RenderedProjection(Camera camera, out Matrix4x4 projection)
        {
            foreach (CameraRedirect redirect in redirects)
                if (redirect != null && redirect.Camera == camera && redirect.DidJitter)
                {
                    projection = redirect.AppliedProjection;
                    return true;
                }
            projection = Matrix4x4.identity;
            return false;
        }

        // For UnityMouseEvents: each redirected camera lends its screen for the
        // length of Unity's mouse event pass; those that did go into lent.
        internal void LendScreens(List<CameraRedirect> lent)
        {
            for (int i = 0; i < redirects.Count; i++)
            {
                CameraRedirect redirect = redirects[i];
                if (redirect != null && redirect.LendScreen()) lent.Add(redirect);
            }
        }

        // The layers the redirected cameras render: what can end up in the image
        // the upscaler reconstructs.
        internal int VisibleLayerMask()
        {
            int mask = 0;
            foreach (CameraRedirect redirect in redirects)
                if (redirect != null && redirect.Camera != null) mask |= redirect.Camera.cullingMask;
            return mask;
        }

        internal void Release()
        {
            // Switched off before the deferred Destroy: a camera with OnRenderImage
            // still renders in this frame, and without the rig it would blit its own
            // target -- never cleared, drawn into by nothing -- over the frame buffer.
            if (presenter != null) { presenter.Rig = null; presenter = null; }
            if (presenterObject != null)
            {
                Camera presenterCam = presenterObject.GetComponent<Camera>();
                if (presenterCam != null) presenterCam.enabled = false;
                Object.Destroy(presenterObject);
                presenterObject = null;
            }
            presenterCamera = null;

            foreach (CameraRedirect redirect in redirects)
            {
                if (redirect == null) continue;
                redirect.Restore();
                // See Redirect: nothing left over until end of frame. Not on a
                // camera whose object is being deactivated, as in a scene's teardown:
                // Unity refuses that ("Cannot destroy Component while GameObject is
                // being activated or deactivated").
                if (redirect.gameObject.activeInHierarchy) Object.DestroyImmediate(redirect);
                else Object.Destroy(redirect);
            }
            redirects.Clear();
        }
    }
}
