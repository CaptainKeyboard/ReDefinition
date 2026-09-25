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
    public partial class MotionVectorProbe : MonoBehaviour
    {
        public Shader cloudMotion;
        public Shader surface;
        public Shader motionAudit;
        // Opaque, and drawn in the forward path only: it has no deferred pass.
        public Shader forwardOnly;
        public Shader vesselShadow;

        private const int Width = 320, Height = 180;
        private const int StillFrames = 30;

        private Camera cam;
        private GameObject sphere;
        private RenderTexture color, unityMotion, sceneDepth, farDepth, noMotion, computed, auditTarget;
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
            sceneDepth = new RenderTexture(Width, Height, 0, RenderTextureFormat.RFloat);
            // As the rig captures it: in the deferred path the only source that holds depth.
            capture.Blit(BuiltinRenderTextureType.ResolvedDepth, sceneDepth);
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
            yield return ReadBack();
            for (int i = 0; i < StillFrames; i++) yield return null;
            yield return Audit();
            yield return DepthSources();
            yield return ShadowCases();

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

        // Hidden/ReDefinition/MotionAudit, as VesselMotionVectors in the plugin draws it, over
        // the sphere moved in this frame: once with the sphere's previous matrix, where
        // it must find Unity's motion vectors right, and once with the current matrix in
        // its place, where it must find them wrong; then pass 1 writes them into empty
        // motion vectors, where pass 0 must find them right.
        // Which of the camera's depth sources hold an opaque object drawn in the forward
        // path after the deferred one: a cube with a forward-only shader in front of the
        // sphere. The rig captures ResolvedDepth; frame generation and the vessel checks
        // read it. One line: the depth each source holds at the cube's centre, where 0 is
        // the far plane.
        private IEnumerator DepthSources()
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.position = new Vector3(0f, 0f, -5f);
            cube.transform.localScale = Vector3.one;
            cube.GetComponent<Renderer>().sharedMaterial = new Material(forwardOnly) { color = Color.red };

            RenderTexture resolved = new RenderTexture(Width, Height, 0, RenderTextureFormat.RFloat);
            RenderTexture plain = new RenderTexture(Width, Height, 0, RenderTextureFormat.RFloat);
            RenderTexture global = new RenderTexture(Width, Height, 0, RenderTextureFormat.RFloat);
            CommandBuffer sources = new CommandBuffer { name = "MotionVectorCheck depth sources" };
            sources.Blit(BuiltinRenderTextureType.ResolvedDepth, resolved);
            sources.Blit(BuiltinRenderTextureType.Depth, plain);
            sources.Blit(new RenderTargetIdentifier("_CameraDepthTexture"), global);
            cam.AddCommandBuffer(CameraEvent.BeforeImageEffects, sources);
            for (int i = 0; i < 3; i++) yield return null;
            yield return new WaitForEndOfFrame();
            cam.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, sources);

            Vector3 viewport = cam.WorldToViewportPoint(cube.transform.position);
            int x = Mathf.FloorToInt(viewport.x * Width), y = Mathf.FloorToInt(viewport.y * Height);
            Texture2D rr = Read(resolved, TextureFormat.RFloat), pr = Read(plain, TextureFormat.RFloat),
                      gr = Read(global, TextureFormat.RFloat);
            float red = Read(color, TextureFormat.RGBAHalf).GetPixel(x, y).r;
            Vector3 s = cam.WorldToViewportPoint(sphere.transform.position);
            int sx = Mathf.FloorToInt(s.x * Width), sy = Mathf.FloorToInt(s.y * Height);
            // Reversed depth: near / distance, about 0.3 / 5 at the cube's front face.
            result.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "depthSources {0} {1} {2} cubeRed {3} sphere {4} {5} {6}",
                rr.GetPixel(x, y).r, pr.GetPixel(x, y).r, gr.GetPixel(x, y).r, red,
                rr.GetPixel(sx, sy).r, pr.GetPixel(sx, sy).r, gr.GetPixel(sx, sy).r));

            // The camera's own depth buffer, made a depth texture of its own that can be
            // read: the colour and the depth set apart (SetTargetBuffers).
            RenderTexture depthTarget = new RenderTexture(Width, Height, 24, RenderTextureFormat.Depth);
            depthTarget.Create();
            cam.SetTargetBuffers(color.colorBuffer, depthTarget.depthBuffer);
            for (int i = 0; i < 3; i++) yield return null;
            yield return new WaitForEndOfFrame();
            RenderTexture fromTarget = new RenderTexture(Width, Height, 0, RenderTextureFormat.RFloat);
            Graphics.Blit(depthTarget, fromTarget);
            Texture2D tr = Read(fromTarget, TextureFormat.RFloat);
            result.AppendLine(string.Format(CultureInfo.InvariantCulture, "depthTarget {0} sphere {1}",
                tr.GetPixel(x, y).r, tr.GetPixel(sx, sy).r));
            cam.targetTexture = color;

            // Pass 2 of Hidden/ReDefinition/MotionAudit draws the cube's depth into the
            // resolved depth that lacks it, as VesselMotionVectors does for the vessel.
            CommandBuffer write = new CommandBuffer { name = "MotionVectorCheck depth write" };
            write.SetRenderTarget(resolved);
            write.SetGlobalMatrix("_AuditRasterViewProjection",
                GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix);
            write.DrawRenderer(cube.GetComponent<Renderer>(), new Material(motionAudit), 0, 2);
            Graphics.ExecuteCommandBuffer(write);
            Texture2D wr = Read(resolved, TextureFormat.RFloat);
            result.AppendLine(string.Format(CultureInfo.InvariantCulture, "depthWrite {0} sphere {1}",
                wr.GetPixel(x, y).r, wr.GetPixel(sx, sy).r));
            Destroy(cube);
        }

        private IEnumerator Audit()
        {
            Matrix4x4 previousViewProjection = ViewProjection();
            Matrix4x4 previousModel = sphere.transform.localToWorldMatrix;
            sphere.transform.position += new Vector3(0.3f, 0.2f, 0f);
            yield return new WaitForEndOfFrame();
            AuditLine("audit", previousViewProjection, previousModel, unityMotion);
            AuditLine("auditWrong", previousViewProjection, sphere.transform.localToWorldMatrix, unityMotion);

            // Pass 1 into motion vectors that hold none, as the rig's RGHalf ones: pass 0
            // must then find them right.
            RenderTexture written = new RenderTexture(Width, Height, 0, RenderTextureFormat.RGHalf);
            Graphics.Blit(Texture2D.blackTexture, written);
            Material repair = new Material(motionAudit);
            CommandBuffer draw = new CommandBuffer { name = "MotionVectorCheck repair" };
            draw.SetRenderTarget(written);
            SetAuditGlobals(draw, previousViewProjection, previousModel, written);
            draw.DrawRenderer(sphere.GetComponent<Renderer>(), repair, 0, 1);
            Graphics.ExecuteCommandBuffer(draw);
            AuditLine("repair", previousViewProjection, previousModel, written);
        }

        private void SetAuditGlobals(CommandBuffer draw, Matrix4x4 previousViewProjection, Matrix4x4 previousModel,
                                     RenderTexture motion)
        {
            draw.SetGlobalMatrix("_AuditRasterViewProjection",
                GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix);
            draw.SetGlobalMatrix("_AuditViewProjection", ViewProjection());
            draw.SetGlobalMatrix("_AuditPreviousViewProjection", previousViewProjection);
            draw.SetGlobalMatrix("_AuditPreviousModel", previousModel);
            draw.SetGlobalFloat("_AuditPart", 0f);
            draw.SetGlobalFloat("_AuditBadPixels", 1f);
            draw.SetGlobalVector("_AuditTexel", new Vector4(1f / Width, 1f / Height, Width, Height));
            draw.SetGlobalTexture("_AuditMotion", motion);
            draw.SetGlobalTexture("_AuditDepth", sceneDepth);
        }

        private void AuditLine(string label, Matrix4x4 previousViewProjection, Matrix4x4 previousModel,
                               RenderTexture motion)
        {
            if (auditTarget == null) auditTarget = new RenderTexture(Width, Height, 0, RenderTextureFormat.R8);
            ComputeBuffer stats = new ComputeBuffer(4, sizeof(uint));
            stats.SetData(new uint[4]);
            Material audit = new Material(motionAudit);
            CommandBuffer draw = new CommandBuffer { name = "MotionVectorCheck audit" };
            draw.SetRenderTarget(auditTarget);
            draw.SetRandomWriteTarget(1, stats);
            SetAuditGlobals(draw, previousViewProjection, previousModel, motion);
            draw.DrawRenderer(sphere.GetComponent<Renderer>(), audit, 0, 0);
            draw.ClearRandomWriteTargets();
            Graphics.ExecuteCommandBuffer(draw);
            uint[] data = new uint[4];
            stats.GetData(data);
            stats.Release();
            result.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3}",
                label, data[0], data[1], data[3] / 16f));
        }

        // How MotionVectorAudit in the plugin addresses a pixel: a viewport position,
        // y up, read with AsyncGPUReadback at row y times the height. The sphere's
        // centre read so must be the sphere, and the row mirrored must not.
        private IEnumerator ReadBack()
        {
            Vector3 viewport = cam.WorldToViewportPoint(sphere.transform.position);
            int x = Mathf.FloorToInt(viewport.x * Width);
            int y = Mathf.FloorToInt(viewport.y * Height);
            float atRow = -1f, mirrored = -1f;
            CommandBuffer read = new CommandBuffer();
            read.RequestAsyncReadback(color, 0, x, 1, y, 1, 0, 1,
                r => { if (!r.hasError) atRow = r.GetData<ushort>()[0]; });
            read.RequestAsyncReadback(color, 0, x, 1, Height - 1 - y, 1, 0, 1,
                r => { if (!r.hasError) mirrored = r.GetData<ushort>()[0]; });
            Graphics.ExecuteCommandBuffer(read);
            for (int i = 0; i < 10 && (atRow < 0f || mirrored < 0f); i++) yield return null;
            AsyncGPUReadback.WaitAllRequests();
            result.AppendLine(string.Format(CultureInfo.InvariantCulture, "readback {0} {1}",
                atRow < 0f ? -1f : Mathf.HalfToFloat((ushort)atRow),
                mirrored < 0f ? -1f : Mathf.HalfToFloat((ushort)mirrored)));
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
