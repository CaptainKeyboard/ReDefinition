using System.Collections;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.MotionVectorCheck
{
    // The player half of MotionVectorCheck (Editor/MotionVectorCheck.cs): a deferred camera
    // that renders into a render texture, as every camera ReDefinition redirects does, and a
    // sphere. After thirty still frames the camera moves, thirty frames later the sphere.
    // In the frame of each move Unity's motion vectors are copied at BeforeImageEffects, and
    // pass 2 of Hidden/ReDefinition/CloudMotion computes its own for the same motion, over a
    // depth at the far plane everywhere. One line per case goes to the result file: the
    // sphere's pixels, how many of them the pass covered, and the mean of both motion
    // vectors over them.
    public class MotionVectorProbe : MonoBehaviour
    {
        public Shader cloudMotion;
        public Shader surface;

        private const int Width = 320, Height = 180;
        private const int StillFrames = 30;

        private Camera cam;
        private GameObject sphere;
        private RenderTexture color, unityMotion, farDepth, noMotion, computed;
        private Material pass;
        private readonly StringBuilder result = new StringBuilder();

        private IEnumerator Start()
        {
            string path = ResultPath();
            cam = new GameObject("check camera").AddComponent<Camera>();
            cam.transform.position = new Vector3(0f, 0f, -10f);
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 1000f;
            cam.renderingPath = RenderingPath.DeferredShading;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            color = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGBHalf);
            cam.targetTexture = color;
            cam.aspect = (float)Width / Height;
            cam.depthTextureMode = DepthTextureMode.Depth | DepthTextureMode.MotionVectors;

            unityMotion = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGBFloat);
            CommandBuffer capture = new CommandBuffer { name = "MotionVectorCheck capture" };
            capture.Blit(BuiltinRenderTextureType.MotionVectors, unityMotion);
            cam.AddCommandBuffer(CameraEvent.BeforeImageEffects, capture);

            sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.transform.position = new Vector3(3f, 2f, 0f);
            sphere.transform.localScale = Vector3.one * 2f;
            // White, lit from the camera's side: the sphere's pixels are the bright ones.
            sphere.GetComponent<Renderer>().sharedMaterial = new Material(surface) { color = Color.white };
            Light sun = new GameObject("check light").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.identity;

            farDepth = new RenderTexture(Width, Height, 0, RenderTextureFormat.RFloat);
            noMotion = new RenderTexture(Width, Height, 0, RenderTextureFormat.RGHalf);
            computed = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGBFloat);
            Graphics.Blit(Texture2D.blackTexture, farDepth);
            Graphics.Blit(Texture2D.blackTexture, noMotion);
            pass = new Material(cloudMotion);

            result.AppendLine("device " + SystemInfo.graphicsDeviceType + " uvStartsAtTop "
                              + SystemInfo.graphicsUVStartsAtTop + " passes " + pass.passCount);

            for (int i = 0; i < StillFrames; i++) yield return null;
            yield return Measure("camera", () => cam.transform.position = new Vector3(-0.3f, -0.2f, -10f));
            for (int i = 0; i < StillFrames; i++) yield return null;
            yield return Measure("object", () => sphere.transform.position += new Vector3(0.3f, 0.2f, 0f));

            System.IO.File.WriteAllText(path, result.ToString());
            Application.Quit();
        }

        private static string ResultPath()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-result") return args[i + 1];
            return System.IO.Path.Combine(Application.dataPath, "../motion-vector-check.txt");
        }

        private Matrix4x4 ViewProjection()
        {
            return GL.GetGPUProjectionMatrix(cam.projectionMatrix, false) * cam.worldToCameraMatrix;
        }

        private IEnumerator Measure(string label, System.Action move)
        {
            Matrix4x4 previousViewProjection = ViewProjection();
            Matrix4x4 previousLocalToWorld = sphere.transform.localToWorldMatrix;
            move();
            yield return new WaitForEndOfFrame();

            Matrix4x4 now = sphere.transform.localToWorldMatrix;
            Vector3 center = sphere.transform.position;
            Vector4[] spheres = new Vector4[16];
            Matrix4x4[] motions = new Matrix4x4[8];
            for (int i = 0; i < motions.Length; i++) motions[i] = Matrix4x4.identity;
            spheres[0] = new Vector4(center.x, center.y, center.z, 1f);
            motions[0] = previousLocalToWorld * now.inverse;
            Matrix4x4 viewProjection = ViewProjection();
            Shader.SetGlobalVectorArray("_ReDefinitionBodySpheres", spheres);
            Shader.SetGlobalMatrixArray("_ReDefinitionBodyMotions", motions);
            Shader.SetGlobalFloat("_ReDefinitionBodyCount", 1f);
            Shader.SetGlobalMatrix("_ReDefinitionScaledViewProjection", viewProjection);
            Shader.SetGlobalMatrix("_ReDefinitionScaledPreviousViewProjection", previousViewProjection);
            Shader.SetGlobalMatrix("_ReDefinitionScaledInverseViewProjection", viewProjection.inverse);
            Shader.SetGlobalVector("_ReDefinitionScaledCameraPosition", cam.transform.position);
            Shader.SetGlobalVector("_ReDefinitionCloudTexel", new Vector4(1f / Width, 1f / Height, Width, Height));
            Shader.SetGlobalTexture("_ReDefinitionUnityMotion", noMotion);
            Shader.SetGlobalTexture("_ReDefinitionCloudSceneDepth", farDepth);
            Graphics.Blit(Texture2D.blackTexture, computed, pass, 2);

            Texture2D image = Read(color, TextureFormat.RGBAHalf);
            Texture2D unity = Read(unityMotion, TextureFormat.RGBAFloat);
            Texture2D ours = Read(computed, TextureFormat.RGBAFloat);
            Vector2 unitySum = Vector2.zero, oursSum = Vector2.zero;
            int pixels = 0, covered = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (image.GetPixel(x, y).r < 0.1f) continue;
                    Color u = unity.GetPixel(x, y);
                    Color o = ours.GetPixel(x, y);
                    unitySum += new Vector2(u.r, u.g);
                    oursSum += new Vector2(o.r, o.g);
                    if (Mathf.Abs(o.r) + Mathf.Abs(o.g) > 1e-7f) covered++;
                    pixels++;
                }
            }
            Vector2 unityMean = pixels > 0 ? unitySum / pixels : Vector2.zero;
            Vector2 oursMean = pixels > 0 ? oursSum / pixels : Vector2.zero;
            result.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3} {4} {5} {6}",
                label, pixels, covered, unityMean.x, unityMean.y, oursMean.x, oursMean.y));
        }

        private static Texture2D Read(RenderTexture texture, TextureFormat format)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = texture;
            Texture2D copy = new Texture2D(texture.width, texture.height, format, false);
            copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            copy.Apply();
            RenderTexture.active = previous;
            return copy;
        }
    }
}
