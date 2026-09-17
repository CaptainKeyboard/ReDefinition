using System.Text;
using UnityEngine;

namespace ReDefinition
{
    // The log tag every file uses, and the camera survey on Right Ctrl + Right
    // Shift + N. The motion vector measurement the upscaler rests on is in
    // docs/development/upscaler.md.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class UpscalerProbe : MonoBehaviour
    {
        public const string Tag = "[ReDefinition]";

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Debug.Log(Tag + " Loaded. The hotkeys stand in the settings window, under Keys.");
        }

        // Which cameras exist, in what order, drawing into what -- for a camera
        // stack that behaves unexpectedly. FXCamera, for one, draws at depth 3,
        // after the upscaler's presenter.
        public static void LogCameraSurvey()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Tag).Append(" Camera survey in scene ").Append(HighLogic.LoadedScene).AppendLine();

            foreach (Camera cam in Camera.allCameras)
            {
                sb.Append("  ").Append(cam.name)
                  .Append(" | depth ").Append(cam.depth.ToString("0.##"))
                  .Append(" | mask 0x").Append(cam.cullingMask.ToString("X8"))
                  .Append(" | path ").Append(cam.actualRenderingPath)
                  .Append(" | clear ").Append(cam.clearFlags)
                  .Append(" | target ").Append(cam.targetTexture == null ? "backbuffer" : cam.targetTexture.name)
                  .Append(" | depthTextureMode ").Append(cam.depthTextureMode)
                  .Append(" | HDR ").Append(cam.allowHDR)
                  .Append(" | MSAA ").Append(cam.allowMSAA)
                  .Append(" | projection ")
                  .Append(CameraRedirect.ProjectionIsUnitys(cam, cam.projectionMatrix) ? "Unity's own" : "set by a script")
                  .AppendLine();
            }

            sb.Append("  QualitySettings.antiAliasing = ").Append(QualitySettings.antiAliasing);
            Debug.Log(sb.ToString());
        }
    }
}
