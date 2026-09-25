using System.Collections.Generic;
using System.Text;
using System;
using ReDefinition.Bridges;
using ReDefinition.Core;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The rig's part for frame generation's HUD-less copy: the backbuffer taken
    // after the last camera that draws the scene, before the UI.
    public partial class UpscalerRig
    {
        // Frame generation's HUD-less copy. See AttachHudLessCapture.
        private RenderTexture hudLessCopy;
        private CommandBuffer hudLessBuffer;
        private readonly List<Camera> hudLessCameras = new List<Camera>();
        private float hudLessRecheckTime = float.MaxValue;

        // Cameras that draw onto the backbuffer after the presenter and are still
        // scene, not UI. They are not redirected (UpscalerRig.SceneCameras), and
        // frame generation's HUD-less snapshot is taken after them: effects they
        // draw are scene and are interpolated; if they draw nothing, a copy after
        // them changes nothing.
        private static readonly string[] EffectCameras = { "FXDepthCamera", "FXCamera" };

        // The HUD-less image for frame generation.
        //
        // FSR keeps the UI out of interpolation by comparing the presented frame
        // with a copy of it that has no UI; whatever differs is UI. AMD documents
        // three ways of handling UI, and this is the one it added "for
        // compatibility with engines that can not apply either of the other two
        // options" -- a callback that draws the UI a second time, or the UI in a
        // texture of its own. KSP is such an engine: its UI and every mod's OnGUI
        // window go straight onto the backbuffer.
        //
        // The upscaler's output would differ from the backbuffer everywhere -- half
        // float against 8 bit, and without what the effect cameras draw after the
        // presenter. So the rig copies the backbuffer itself, at the end of every
        // camera that draws scene content -- the
        // presenter and the effect cameras after it. The last of them to render
        // leaves the finished scene; whatever is drawn after that is UI to FSR,
        // and is logged by name. The proxy's "HUD-less check" measures the result.
        //
        // The copy is a Blit recorded in the CommandBuffer, so Unity orders it
        // with the rest of the frame's drawing; the proxy copies this texture at
        // Present, once all of it is submitted (FrameGeneration::RegisterInputs).
        private void AttachHudLessCapture(Camera presenterCam, bool recheckLater)
        {
            DetachHudLessCapture();
            if (!FrameGenerationBridge.Available || presenterCam == null || hudLessCopy == null) return;

            hudLessBuffer = new CommandBuffer { name = "ReDefinition.HudLessCapture" };
            // The frame with the active vessel's shadow taken out where frame generation
            // would drag it along with the ground (VesselShadowLayer), or the frame as it is.
            hudLessWithShadow = VesselShadowLayer.Enabled;
            if (!hudLessWithShadow
                || !vesselShadow.Compose(hudLessBuffer, BuiltinRenderTextureType.CameraTarget, hudLessCopy, renderSize))
                hudLessBuffer.Blit(BuiltinRenderTextureType.CameraTarget, hudLessCopy);

            // AfterEverything: after the presenter's OnRenderImage blit, and
            // after an effect camera's own drawing.
            foreach (Camera carrier in HudLessCarriers(presenterCam))
            {
                carrier.AddCommandBuffer(CameraEvent.AfterEverything, hudLessBuffer);
                hudLessCameras.Add(carrier);
            }

            // One more look a few seconds in: effect cameras can be created after
            // the rig, and the survey then shows the stack as it really runs.
            hudLessRecheckTime = recheckLater ? Time.unscaledTime + 5f : float.MaxValue;

            LogHudLessCapture();
        }

        // The presenter, and every effect camera drawing onto the backbuffer after
        // it. Camera.allCameras lists only enabled cameras; one switched off
        // later keeps the capture and simply does not render, and the copy taken
        // after the presenter then stands.
        private static List<Camera> HudLessCarriers(Camera presenterCam)
        {
            List<Camera> carriers = new List<Camera> { presenterCam };
            foreach (Camera other in Camera.allCameras)
            {
                if (other == null || other == presenterCam || other.targetTexture != null) continue;
                if (System.Array.IndexOf(EffectCameras, other.name) < 0) continue;
                if (other.depth <= presenterCam.depth) continue;
                carriers.Add(other);
            }
            return carriers;
        }

        private void RecheckHudLessCapture()
        {
            hudLessRecheckTime = float.MaxValue;

            Camera presenterCam = presenterObject != null ? presenterObject.GetComponent<Camera>() : null;
            if (presenterCam == null)
            {
                DetachHudLessCapture();
                return;
            }

            List<Camera> current = HudLessCarriers(presenterCam);
            bool unchanged = current.Count == hudLessCameras.Count
                             && current.TrueForAll(c => hudLessCameras.Contains(c));

            if (unchanged) LogHudLessCapture();
            else AttachHudLessCapture(presenterCam, false);

            CameraSurvey.Write();
        }

        private void DetachHudLessCapture()
        {
            if (hudLessBuffer != null)
            {
                foreach (Camera carrier in hudLessCameras)
                    if (carrier != null) carrier.RemoveCommandBuffer(CameraEvent.AfterEverything, hudLessBuffer);

                hudLessBuffer.Release();
                hudLessBuffer = null;
            }

            hudLessCameras.Clear();
            hudLessRecheckTime = float.MaxValue;
        }

        // Which cameras the snapshot follows, and what is drawn onto the
        // backbuffer after the last of them. A post-processing layer there would
        // change every pixel after the snapshot; FSR would take the whole image
        // for UI and interpolation would quietly do nothing -- so that case is
        // called out by name.
        private void LogHudLessCapture()
        {
            if (hudLessCameras.Count == 0) return;

            float lastDepth = float.MinValue;
            StringBuilder sb = new StringBuilder();
            sb.Append(Log.Tag).Append(" HUD-less snapshot at the end of");
            foreach (Camera carrier in hudLessCameras)
            {
                if (carrier == null) continue;
                sb.Append(" '").Append(carrier.name).Append("' (").Append(carrier.depth.ToString("0.##")).Append(')');
                lastDepth = Mathf.Max(lastDepth, carrier.depth);
            }

            sb.Append(". Drawn after the last of them, and therefore UI to frame generation:");

            Camera[] cameras = Camera.allCameras;
            System.Array.Sort(cameras, (a, b) => a.depth.CompareTo(b.depth));

            bool any = false;
            foreach (Camera other in cameras)
            {
                if (other == null || other.targetTexture != null || other.depth <= lastDepth) continue;

                any = true;
                sb.Append(" '").Append(other.name).Append("' (").Append(other.depth.ToString("0.##")).Append(')');
                if (HasComponentNamed(other, "PostProcessLayer"))
                    sb.Append(" [WARNING: post-processing after the snapshot -- FSR would see the whole image as UI]");
            }

            if (!any) sb.Append(" no camera");
            sb.Append(", then every OnGUI window and screen-space overlay canvas.");
            Debug.Log(sb.ToString());
        }

        private string DescribeHudLessCarriers()
        {
            if (hudLessCameras.Count == 0) return "not attached";

            StringBuilder sb = new StringBuilder();
            foreach (Camera carrier in hudLessCameras)
            {
                if (sb.Length > 0) sb.Append(", ");
                if (carrier == null) { sb.Append("(destroyed)"); continue; }
                sb.Append("'").Append(carrier.name).Append("' (").Append(carrier.depth.ToString("0.##")).Append(')');
            }
            return sb.ToString();
        }

        private static bool HasComponentNamed(Component host, string typeName)
        {
            foreach (Component component in host.GetComponents<Component>())
                if (component != null && component.GetType().Name == typeName) return true;
            return false;
        }
    }
}
