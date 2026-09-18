using System.Text;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // Which cameras exist, in what order, drawing into what -- into KSP.log on
    // its hotkey (the Keys tab), for a camera stack that behaves unexpectedly.
    internal static class CameraSurvey
    {
        // Which cameras exist, in what order, drawing into what -- for a camera
        // stack that behaves unexpectedly. FXCamera, for one, draws at depth 3,
        // after the upscaler's presenter.
        internal static void Write()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Log.Tag).Append(" Camera survey in scene ").Append(HighLogic.LoadedScene).AppendLine();

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
