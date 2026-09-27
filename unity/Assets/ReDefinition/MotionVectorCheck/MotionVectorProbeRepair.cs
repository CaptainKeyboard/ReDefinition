using System.Collections;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.MotionVectorCheck
{
    // Unity's motion vectors repaired where Unity has not written them
    // (UnityMotionRepair in the plugin): the same two buffers on the check camera, the
    // buffer cleared at the end of every frame and pass 3 at BeforeImageEffectsOpaque.
    // The camera and object cases then show that Unity writes every pixel anew and the
    // repair keeps its values. Here a buffer before the repair, at the same event,
    // clears what Unity wrote, as if it had written nothing; the camera moves, and
    // pass 3 must give the move worked out for each pixel of a sphere ahead. One line,
    // "repair", to the result file.
    public partial class MotionVectorProbe
    {
        private RenderTexture repairCopy;
        // What the buffer holds right after the mark, at the end of the frame.
        private RenderTexture markedCopy;
        private CommandBuffer repairBuffer;

        private void AttachRepair()
        {
            repairCopy = new RenderTexture(Width, Height, 0, RenderTextureFormat.RGHalf);
            RenderTexture repairDepth = new RenderTexture(Width, Height, 0, RenderTextureFormat.RFloat);
            CommandBuffer repair = new CommandBuffer { name = "MotionVectorCheck repair" };
            repair.Blit(BuiltinRenderTextureType.MotionVectors, repairCopy);
            repair.Blit(BuiltinRenderTextureType.ResolvedDepth, repairDepth);
            repair.SetGlobalTexture("_ReDefinitionRepairMotion", repairCopy);
            repair.SetGlobalTexture("_ReDefinitionRepairDepth", repairDepth);
            repair.SetGlobalVector("_ReDefinitionRepairTexel", new Vector4(1f / Width, 1f / Height, Width, Height));
            repair.Blit(Texture2D.blackTexture, BuiltinRenderTextureType.MotionVectors, pass, 3);
            cam.AddCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, repair);
            repairBuffer = repair;
            CommandBuffer mark = new CommandBuffer { name = "MotionVectorCheck mark" };
            mark.SetRenderTarget(BuiltinRenderTextureType.MotionVectors);
            mark.ClearRenderTarget(false, true, new Color(-100f, -100f, 0f, 0f));
            markedCopy = new RenderTexture(Width, Height, 0, RenderTextureFormat.RGHalf);
            mark.Blit(BuiltinRenderTextureType.MotionVectors, markedCopy);
            cam.AddCommandBuffer(CameraEvent.AfterEverything, mark);
        }

        private IEnumerator RepairCase()
        {
            GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.transform.position = cam.transform.position + cam.transform.forward * 10f;
            ball.transform.localScale = Vector3.one * 4f;
            ball.GetComponent<Renderer>().sharedMaterial = new Material(surface) { color = Color.white };
            CommandBuffer unwritten = new CommandBuffer { name = "MotionVectorCheck unwritten" };
            unwritten.SetRenderTarget(BuiltinRenderTextureType.MotionVectors);
            unwritten.ClearRenderTarget(false, true, new Color(-100f, -100f, 0f, 0f));
            cam.RemoveCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, repairBuffer);
            cam.AddCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, unwritten);
            cam.AddCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, repairBuffer);
            for (int i = 0; i < StillFrames; i++) yield return null;

            Matrix4x4 before = ViewProjection();
            cam.transform.position += new Vector3(-0.3f, -0.2f, 0f);
            Matrix4x4 now = ViewProjection();
            Matrix4x4 clipToPrevious = before * now.inverse;
            Shader.SetGlobalMatrix("_ReDefinitionSceneClipToPrevClip", clipToPrevious);
            Shader.SetGlobalMatrix("_ReDefinitionSkyClipToPrevClip", clipToPrevious);
            Shader.SetGlobalVector("_ReDefinitionSceneRange", new Vector4(cam.nearClipPlane, cam.farClipPlane, float.MaxValue, 1f));
            yield return new WaitForEndOfFrame();

            Texture2D image = Read(color, TextureFormat.RGBAHalf);
            Texture2D motion = Read(unityMotion, TextureFormat.RGBAFloat);
            Texture2D depth = Read(sceneDepth, TextureFormat.RFloat);
            Texture2D seen = Read(repairCopy, TextureFormat.RGHalf);
            int pixels = 0, missing = 0;
            double error = 0.0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (seen.GetPixel(x, y).r < -50f) missing++;
                    if (image.GetPixel(x, y).r < 0.1f) continue;
                    float d = depth.GetPixel(x, y).r;
                    Vector4 clip = new Vector4((x + 0.5f) / Width * 2f - 1f, (y + 0.5f) / Height * 2f - 1f, d, 1f);
                    Vector4 back = clipToPrevious * clip;
                    Vector2 expected = new Vector2(clip.x / clip.w - back.x / back.w, clip.y / clip.w - back.y / back.w) * 0.5f;
                    Color m = motion.GetPixel(x, y);
                    error += (new Vector2(m.r, m.g) - expected).magnitude * Width;
                    pixels++;
                }
            }
            result.AppendLine(string.Format(CultureInfo.InvariantCulture, "repair {0} {1} {2} {3}",
                pixels, missing, Width * Height, pixels > 0 ? error / pixels : -1.0));

            Shader.SetGlobalVector("_ReDefinitionSceneRange", Vector4.zero);
            cam.RemoveCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, unwritten);
            Destroy(ball);
            for (int i = 0; i < StillFrames; i++) yield return null;
        }
    }
}
