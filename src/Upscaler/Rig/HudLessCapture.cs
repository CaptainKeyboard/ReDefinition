using System.Collections.Generic;
using System.Text;
using ReDefinition.Proxy;
using ReDefinition.Core;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // Frame generation's HUD-less copy: the backbuffer taken after the last camera that draws the scene, before
    // the UI. Owns the command buffer, the cameras that carry it and the one look at them after the attach.
    internal sealed class HudLessCapture
    {
        // Frame generation's HUD-less capture into UpscalerTextures.HudLess. See
        // Attach.
        private CommandBuffer buffer;
        private readonly List<Camera> carriers = new List<Camera>();
        private float recheckTime = UpscalerRig.NotScheduled;

        // How long after attaching the capture takes its one second look at the cameras.
        private const float RecheckDelaySeconds = 5f;
        private bool composed;
        private bool copyLogged;

        // VesselShadowLayer.Enabled as the HUD-less capture was recorded with it.
        private bool withShadow;

        // Cameras that draw onto the backbuffer after the presenter and are still
        // scene, not UI. They are not redirected (UpscalerCameras.SceneCameras), and
        // frame generation's HUD-less snapshot is taken after them: effects they
        // draw are scene and are interpolated; if they draw nothing, a copy after
        // them changes nothing.
        private static readonly string[] EffectCameras = { "FXDepthCamera", "FXCamera" };

        // Whether the capture is attached to its cameras.
        internal bool Attached
        {
            get { return buffer != null; }
        }

        // Whether the capture was recorded with the vessel's shadow taken out of it.
        internal bool WithShadow
        {
            get { return withShadow; }
        }

        // Whether the one look at the cameras, a few seconds after attaching, is due.
        internal bool RecheckDue
        {
            get { return Time.unscaledTime >= recheckTime; }
        }

        // The HUD-less image for frame generation.
        //
        // FSR keeps the UI out of interpolation by comparing the presented frame
        // with a copy of it that has no UI; whatever differs is UI. AMD documents
        // three ways of handling UI, and this is the one it added "for
        // compatibility with engines that can not apply either of the other two
        // options". The other two are a callback that draws the UI a second time, or the UI in a
        // texture of its own. KSP is such an engine: its UI and every mod's OnGUI
        // window go straight onto the backbuffer.
        internal void Attach(Camera presenterCam, bool recheckLater, RenderTexture hudLess, VesselShadowLayer shadow,
                             Vector2Int renderSize)
        {
            Detach();
            if (!FrameGenerationBridge.Available || presenterCam == null || hudLess == null) return;

            buffer = new CommandBuffer { name = "ReDefinition.HudLessCapture" };
            withShadow = VesselShadowLayer.Enabled;
            RecordCopy(hudLess, shadow, renderSize);

            // AfterEverything: after the presenter's OnRenderImage blit, and
            // after an effect camera's own drawing.
            foreach (Camera carrier in Carriers(presenterCam))
            {
                carrier.AddCommandBuffer(CameraEvent.AfterEverything, buffer);
                carriers.Add(carrier);
            }

            // One more look a few seconds in: effect cameras can be created after
            // the rig, and the survey then shows the stack as it really runs.
            recheckTime = recheckLater ? Time.unscaledTime + RecheckDelaySeconds : UpscalerRig.NotScheduled;

            LogCapture();
        }

        // The frame with the active vessel's shadow taken out where frame generation
        // would drag it along with the ground (VesselShadowLayer), or the frame as it is.
        // Recorded again into the same buffer, still on its carriers, where the vessel
        // shadow's textures were made anew (RefreshComposition). The copy is a Blit recorded in the
        // CommandBuffer, so Unity orders it with the rest of the frame's drawing. The proxy copies this texture at
        // Present, once all of it is submitted (FrameGeneration::RegisterInputs).
        private void RecordCopy(RenderTexture hudLess, VesselShadowLayer shadow, Vector2Int renderSize)
        {
            buffer.Clear();
            bool nowComposed = withShadow
                               && shadow.Compose(buffer, BuiltinRenderTextureType.CameraTarget, hudLess, renderSize);
            if (!nowComposed) buffer.Blit(BuiltinRenderTextureType.CameraTarget, hudLess);
            if (nowComposed == composed && copyLogged) return;
            composed = nowComposed;
            copyLogged = true;
            Debug.Log(Log.Tag + " HUD-less copy: " + (nowComposed ? "with the vessel's shadow taken out (VesselShadowLayer)"
                                                                  : withShadow ? "the frame as it is -- the vessel shadow's"
                                                                                 + " composition could not be set up"
                                                                               : "the frame as it is") + ".");
        }

        // Before the HUD-less buffer runs this frame: recorded again where the textures
        // it was recorded with are no longer the vessel shadow's.
        internal void RefreshComposition(RenderTexture hudLess, VesselShadowLayer shadow, Vector2Int renderSize)
        {
            if (buffer != null && composed && !shadow.ComposedCurrent(hudLess))
                RecordCopy(hudLess, shadow, renderSize);
        }

        // The presenter, and every effect camera drawing onto the backbuffer after
        // it. The upscaler's output would differ from the backbuffer everywhere: half float against 8 bit, and
        // without what the effect cameras draw after the presenter. So the rig copies the backbuffer itself, at the
        // end of every camera that draws scene content. Camera.allCameras lists only enabled cameras; one switched off
        // later keeps the capture and simply does not render, and the copy taken
        // after the presenter then stands.
        internal static List<Camera> Carriers(Camera presenterCam)
        {
            List<Camera> found = new List<Camera> { presenterCam };
            foreach (Camera other in Camera.allCameras)
            {
                if (other == null || other == presenterCam || other.targetTexture != null) continue;
                if (System.Array.IndexOf(EffectCameras, other.name) < 0) continue;
                if (other.depth <= presenterCam.depth) continue;
                found.Add(other);
            }
            return found;
        }

        // The one look a few seconds after attaching: the capture moved to the carriers there are now, or the
        // same ones logged again, and the camera survey written.
        internal void Recheck(Camera presenterCam, RenderTexture hudLess, VesselShadowLayer shadow, Vector2Int renderSize)
        {
            recheckTime = UpscalerRig.NotScheduled;

            if (presenterCam == null)
            {
                Detach();
                return;
            }

            List<Camera> current = Carriers(presenterCam);
            bool unchanged = current.Count == carriers.Count
                             && current.TrueForAll(c => carriers.Contains(c));

            if (unchanged) LogCapture();
            else Attach(presenterCam, false, hudLess, shadow, renderSize);

            CameraSurvey.Write();
        }

        // Takes the HUD-less buffer off its carriers and frees it: frame generation off, or the rig torn down.
        internal void Detach()
        {
            if (buffer != null)
            {
                foreach (Camera carrier in carriers)
                    if (carrier != null) carrier.RemoveCommandBuffer(CameraEvent.AfterEverything, buffer);

                buffer.Release();
                buffer = null;
            }
            composed = false;
            copyLogged = false;

            carriers.Clear();
            recheckTime = UpscalerRig.NotScheduled;
        }

        // Which cameras the snapshot follows, and what is drawn onto the
        // backbuffer after the last of them. The last of them to render leaves the finished scene. Whatever is
        // drawn after that is UI to FSR, and is logged by name. The proxy's "HUD-less check" measures the result. A post-processing layer there would
        // change every pixel after the snapshot; FSR would take the whole image
        // for UI and interpolation would quietly do nothing -- so that case is
        // called out by name.
        private void LogCapture()
        {
            if (carriers.Count == 0) return;

            float lastDepth = float.MinValue;
            StringBuilder sb = new StringBuilder();
            sb.Append(Log.Tag).Append(" HUD-less snapshot at the end of");
            foreach (Camera carrier in carriers)
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

        // The carriers of the HUD-less copy with their depths, for "Write diagnostics to log".
        internal string Describe()
        {
            if (carriers.Count == 0) return "not attached";

            StringBuilder sb = new StringBuilder();
            foreach (Camera carrier in carriers)
            {
                if (sb.Length > 0) sb.Append(", ");
                if (carrier == null) { sb.Append("(destroyed)"); continue; }
                sb.Append("'").Append(carrier.name).Append("' (").Append(carrier.depth.ToString("0.##")).Append(')');
            }
            return sb.ToString();
        }

        // Whether host carries a component of that type name, for types of mods that may not be installed.
        private static bool HasComponentNamed(Component host, string typeName)
        {
            foreach (Component component in host.GetComponents<Component>())
                if (component != null && component.GetType().Name == typeName) return true;
            return false;
        }
    }
}
