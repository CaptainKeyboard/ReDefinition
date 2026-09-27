using System.Collections;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.MotionVectorCheck
{
    // Ground far away, seen from far away (SceneMotion in the plugin): KSP's flight camera
    // zoomed out over the Mun stands some 150 km from the origin, with its near plane of
    // 0.21 m and far plane of 750 km, and looks at ground 100 to 150 km away. A camera of
    // its own, on a layer of its own, at that place, a sphere of 50 km radius ahead, and a
    // move of 10 m sideways and 6 m up. Over the sphere: how much Unity's motion vectors
    // differ from one pixel to the next, how much pass 2's with the scene camera's move
    // differ, how far pass 2's lie from the same motion worked out here for each pixel,
    // and both means. One line, "far", to the result file.
    public partial class MotionVectorProbe
    {
        private const int FarLayer = 30;

        private IEnumerator FarCase()
        {
            Camera far = new GameObject("far camera").AddComponent<Camera>();
            far.transform.position = new Vector3(-56259.66f, 139028.45f, 2369.80f);
            far.transform.rotation = Quaternion.identity;
            far.fieldOfView = 60f;
            far.nearClipPlane = 0.21f;
            far.farClipPlane = 750000f;
            far.renderingPath = RenderingPath.DeferredShading;
            far.clearFlags = CameraClearFlags.SolidColor;
            far.backgroundColor = Color.black;
            far.cullingMask = 1 << FarLayer;
            RenderTexture farColor = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGBHalf);
            far.targetTexture = farColor;
            far.aspect = (float)Width / Height;
            far.depthTextureMode = DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
            cam.enabled = false;

            RenderTexture farMotion = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGBFloat);
            RenderTexture farDepthCapture = new RenderTexture(Width, Height, 0, RenderTextureFormat.RFloat);
            CommandBuffer capture = new CommandBuffer { name = "MotionVectorCheck far capture" };
            capture.Blit(BuiltinRenderTextureType.MotionVectors, farMotion);
            capture.Blit(BuiltinRenderTextureType.ResolvedDepth, farDepthCapture);
            far.AddCommandBuffer(CameraEvent.BeforeImageEffects, capture);

            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ground.layer = FarLayer;
            ground.transform.position = far.transform.position + new Vector3(20000f, -30000f, 150000f);
            ground.transform.localScale = Vector3.one * 100000f;
            ground.GetComponent<Renderer>().sharedMaterial = new Material(surface) { color = Color.white };

            for (int i = 0; i < StillFrames; i++) yield return null;
            Matrix4x4 previousProjection = GL.GetGPUProjectionMatrix(far.projectionMatrix, false);
            Vector3 previousPosition = far.transform.position;
            far.transform.position += new Vector3(10f, 6f, 0f);
            yield return new WaitForEndOfFrame();

            // The scene camera's move as the plugin works it out: only the difference of
            // the two placements, the camera not turned.
            Matrix4x4 projection = GL.GetGPUProjectionMatrix(far.projectionMatrix, false);
            Vector3 moved = far.transform.position - previousPosition;
            Matrix4x4 flip = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
            Matrix4x4 cameraToPrevious = flip * Matrix4x4.Translate(moved) * flip;
            Matrix4x4 clipToPrevious = previousProjection * cameraToPrevious * projection.inverse;
            Shader.SetGlobalMatrix("_ReDefinitionSceneClipToPrevClip", clipToPrevious);
            Shader.SetGlobalMatrix("_ReDefinitionSkyClipToPrevClip", Matrix4x4.identity);
            Shader.SetGlobalVector("_ReDefinitionSceneRange", new Vector4(far.nearClipPlane, far.farClipPlane, 500f, 1f));
            Shader.SetGlobalFloat("_ReDefinitionBodyCount", 0f);
            Shader.SetGlobalVector("_ReDefinitionCloudTexel", new Vector4(1f / Width, 1f / Height, Width, Height));
            Shader.SetGlobalTexture("_ReDefinitionUnityMotion", farMotion);
            Shader.SetGlobalTexture("_ReDefinitionCloudSceneDepth", farDepthCapture);
            RenderTexture farComputed = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGBFloat);
            Graphics.Blit(Texture2D.blackTexture, farComputed, pass, 2);

            // Unity's motion vectors not written: the copy still holds the value the
            // plugin cleared it to. With the distance from which the camera's own move
            // is taken out of reach, pass 2 must take it for exactly those pixels.
            RenderTexture missingMotion = new RenderTexture(Width, Height, 0, RenderTextureFormat.RGHalf);
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture.active = missingMotion;
            GL.Clear(false, true, new Color(-100f, -100f, 0f, 0f));
            RenderTexture.active = previousActive;
            Shader.SetGlobalTexture("_ReDefinitionUnityMotion", missingMotion);
            Shader.SetGlobalVector("_ReDefinitionSceneRange", new Vector4(far.nearClipPlane, far.farClipPlane, float.MaxValue, 1f));
            RenderTexture missingComputed = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGBFloat);
            Graphics.Blit(Texture2D.blackTexture, missingComputed, pass, 2);
            Shader.SetGlobalVector("_ReDefinitionSceneRange", Vector4.zero);

            Texture2D image = Read(farColor, TextureFormat.RGBAHalf);
            Texture2D unity = Read(farMotion, TextureFormat.RGBAFloat);
            Texture2D ours = Read(farComputed, TextureFormat.RGBAFloat);
            Texture2D depth = Read(farDepthCapture, TextureFormat.RFloat);
            Texture2D missing = Read(missingComputed, TextureFormat.RGBAFloat);
            double missingOff = 0.0;
            double unityJump = 0.0, oursJump = 0.0, oursError = 0.0, expectedSum = 0.0;
            int pixels = 0, pairs = 0;
            Vector2 unitySum = Vector2.zero, oursSum = Vector2.zero;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (image.GetPixel(x, y).r < 0.05f) continue;
                    float d = depth.GetPixel(x, y).r;
                    if (d <= 0f) continue;
                    Vector4 clip = new Vector4((x + 0.5f) / Width * 2f - 1f, (y + 0.5f) / Height * 2f - 1f, d, 1f);
                    Vector4 before = clipToPrevious * clip;
                    // In pixels, as the upscaler reads them.
                    double expected = (clip.x / clip.w - before.x / before.w) * 0.5 * Width;
                    double o = ours.GetPixel(x, y).r * Width;
                    oursError += System.Math.Abs(o - expected);
                    missingOff += System.Math.Abs(missing.GetPixel(x, y).r * Width - o);
                    unitySum += (Vector2)(Vector4)unity.GetPixel(x, y) * Width;
                    oursSum += (Vector2)(Vector4)ours.GetPixel(x, y) * Width;
                    expectedSum += expected;
                    pixels++;
                    if (x + 1 < Width && image.GetPixel(x + 1, y).r >= 0.05f)
                    {
                        unityJump += System.Math.Abs((unity.GetPixel(x + 1, y).r - unity.GetPixel(x, y).r) * Width);
                        oursJump += System.Math.Abs((ours.GetPixel(x + 1, y).r - o / Width) * Width);
                        pairs++;
                    }
                }
            }
            Vector2 unityMean = pixels > 0 ? unitySum / pixels : Vector2.zero;
            Vector2 oursMean = pixels > 0 ? oursSum / pixels : Vector2.zero;
            result.AppendLine(string.Format(CultureInfo.InvariantCulture, "far {0} {1} {2} {3} {4} {5} {6} {7} {8}",
                pixels, pairs > 0 ? unityJump / pairs : -1.0, pairs > 0 ? oursJump / pairs : -1.0,
                pixels > 0 ? oursError / pixels : -1.0, pixels > 0 ? expectedSum / pixels : 0.0,
                unityMean.x, unityMean.y, oursMean.x, oursMean.y));
            result.AppendLine(string.Format(CultureInfo.InvariantCulture, "missing {0} {1}",
                pixels, pixels > 0 ? missingOff / pixels : -1.0));

            far.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, capture);
            Destroy(ground);
            Destroy(far.gameObject);
            cam.enabled = true;
        }
    }
}
