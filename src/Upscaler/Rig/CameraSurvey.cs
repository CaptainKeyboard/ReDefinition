using System;
using System.Text;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

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

        // What decides the scene camera's motion vectors, for motion vectors that do not
        // follow the camera (MotionVectorAudit). It logs the camera's state, the command buffers on each
        // of its events, the motion vector shader, and the globals Scatterer's
        // replacement for it reads.
        internal static void WriteMotionSources(Camera cam)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Log.Tag).Append(" Motion vector sources of '").Append(cam.name).Append("':")
              .Append(" enabled ").Append(cam.enabled)
              .Append(" | depthTextureMode ").Append(cam.depthTextureMode)
              .Append(" | path ").Append(cam.actualRenderingPath)
              .Append(" | target ").Append(cam.targetTexture == null ? "backbuffer" : cam.targetTexture.name)
              .Append(" | forceIntoRenderTexture ").Append(cam.forceIntoRenderTexture)
              .Append(" | motion vector shader ").Append(GraphicsSettings.GetShaderMode(BuiltinShaderType.MotionVectors));
            Shader custom = GraphicsSettings.GetCustomShader(BuiltinShaderType.MotionVectors);
            if (custom != null) sb.Append(" (custom '").Append(custom.name).Append("')");
            sb.Append(" | TAA_KeepPreviousMotionVectors ").Append(Shader.GetGlobalInt("TAA_KeepPreviousMotionVectors"))
              .Append(" | TAA_UseFloatingOriginCameraMotion ").Append(Shader.GetGlobalInt("TAA_UseFloatingOriginCameraMotion"))
              .AppendLine();
            foreach (CameraEvent evt in (CameraEvent[])Enum.GetValues(typeof(CameraEvent)))
            {
                CommandBuffer[] buffers = cam.GetCommandBuffers(evt);
                if (buffers.Length == 0) continue;
                sb.Append("  ").Append(evt).Append(':');
                foreach (CommandBuffer buffer in buffers)
                    sb.Append(" '").Append(buffer.name).Append("' (").Append(buffer.sizeInBytes).Append(" B)");
                sb.AppendLine();
            }
            Component[] components = cam.GetComponents<Component>();
            sb.Append("  components:");
            foreach (Component component in components)
            {
                if (component == null) continue;
                Behaviour behaviour = component as Behaviour;
                sb.Append(' ').Append(component.GetType().FullName);
                if (behaviour != null && !behaviour.enabled) sb.Append(" (off)");
            }
            Debug.Log(sb.ToString());
        }
    }
}
