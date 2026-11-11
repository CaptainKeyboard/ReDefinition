using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // Registers a scene of the weather player by id. The runner finds every class with this attribute by reflection (-mode scene
    // -scene <id>), so a package adds a scene file to tests/WeatherPlayer/Assets/Runner and edits no shared list.
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class WeatherSceneAttribute : Attribute
    {
        public readonly string Id;

        public WeatherSceneAttribute(string id)
        {
            Id = id;
        }
    }

    // One pose of a camera path: where it is and where it looks (degrees).
    public struct CameraPose
    {
        public Vector3 Position;
        public float Yaw, Pitch;

        public CameraPose(Vector3 position, float yaw, float pitch)
        {
            Position = position;
            Yaw = yaw;
            Pitch = pitch;
        }
    }

    // The camera paths of the player, frame by frame (one pose per frame of the frame-indexed clock): the turns of the raytracing
    // player's turn runner (2 and 12 degrees a frame, a 40 degree jump), a hover, a fly-by at 100 m/s and the entry into a volume.
    public static class CameraPaths
    {
        public const float FrameSeconds = 1f / 60f;
        public static readonly string[] Names = { "turn2", "turn12", "jump40", "hover", "flyby100", "entry" };

        // The named path from a start pose. Every path begins with six held frames, so that the history settles.
        public static List<CameraPose> Make(string name, CameraPose start, Vector3 vehicleAt, Bounds volume)
        {
            var path = new List<CameraPose>();
            CameraPose p = start;
            for (int i = 0; i < 6; i++) path.Add(p);
            switch (name)
            {
                case "turn2":
                    for (int i = 0; i < 26; i++) { p.Yaw += 2f; path.Add(p); }
                    for (int i = 0; i < 30; i++) path.Add(p);
                    break;
                case "turn12":
                    for (int i = 0; i < 10; i++) { p.Yaw += 12f; path.Add(p); }
                    for (int i = 0; i < 30; i++) path.Add(p);
                    break;
                case "jump40":
                    for (int i = 0; i < 6; i++) path.Add(p);
                    p.Yaw += 40f;
                    for (int i = 0; i < 30; i++) path.Add(p);
                    break;
                case "hover":
                    // A hover is not still: a slow sway of 0.3 m and 0.2 degrees, as a held aircraft moves.
                    for (int i = 0; i < 90; i++)
                    {
                        float t = i * FrameSeconds * 2f * Mathf.PI * 0.5f;
                        path.Add(new CameraPose(start.Position + new Vector3(0.3f * Mathf.Sin(t), 0.2f * Mathf.Sin(1.3f * t), 0f),
                                                start.Yaw + 0.2f * Mathf.Sin(0.7f * t), start.Pitch));
                    }
                    break;
                case "flyby100":
                {
                    // Past the vehicle at 100 m/s, 40 m to its side, looking at it.
                    Vector3 from = vehicleAt + new Vector3(40f, 8f, -300f);
                    for (int i = 0; i < 360; i++)
                    {
                        Vector3 at = from + new Vector3(0f, 0f, 100f * i * FrameSeconds);
                        Vector3 look = vehicleAt - at;
                        path.Add(new CameraPose(at, Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg, -Mathf.Atan2(look.y, new Vector2(look.x, look.z).magnitude) * Mathf.Rad2Deg));
                    }
                    break;
                }
                case "entry":
                {
                    // Into the volume's centre at 60 m/s from 600 m before its edge.
                    Vector3 from = volume.center + new Vector3(0f, 0f, -(volume.extents.z + 600f));
                    for (int i = 0; i < 480; i++)
                        path.Add(new CameraPose(from + new Vector3(0f, 0f, 60f * i * FrameSeconds), 0f, 0f));
                    break;
                }
                default:
                    throw new ArgumentException("no camera path " + name);
            }
            return path;
        }
    }

    // The base of a scene of the weather player: the fake world every scene shares (a terrain with a coast and a sea plane, a fake
    // sky, sun and ambient at a fixed exposure, a vehicle stand-in with lamps), the camera, and a frame-indexed clock. A scene
    // derives from it, gets [WeatherScene("id")] and implements Run, a coroutine that poses the camera, waits frames and emits
    // metrics into Sink. Everything is deterministic: the world is built from fixed numbers, the clock counts frames, not seconds.
    public abstract class WeatherSceneBase
    {
        public const int Width = 1280, Height = 720;
        // The exposure of the fake sky and light: fixed, so that a luma value means the same in every scene and every run.
        public const float Exposure = 1f;
        // The shoreline lies along x = 0: land for x > 0, the sea for x < 0 (sea level y = 0).
        public const float SeaLevel = 0f;

        protected Camera Cam;
        protected Light Sun;
        protected GameObject Vehicle;
        protected Light[] VehicleLamps;
        protected RenderTexture Target;
        protected MetricSink Sink;
        protected string Output;
        private GameObject sea;

        // The frames counted since the scene began; the clock in seconds is the count at 60 frames a second, whatever the machine does.
        public int FrameIndex { get; private set; }

        public double ClockSeconds { get { return FrameIndex / 60.0; } }

        // Builds the world and the camera; called by the runner before Run.
        internal void Setup(string output, MetricSink sink, string bundleOrNull)
        {
            Output = output;
            Sink = sink;
            QualitySettings.shadows = ShadowQuality.Disable;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            BuildWorld();
            BuildContent();
        }

        // The scene's own content, after the world and before the first frame.
        protected virtual void BuildContent()
        {
        }

        // The scene itself. Yield null for a frame (use Frames, which also advances the clock).
        public abstract IEnumerator Run();

        // Waits that many frames and advances the clock.
        protected IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                FrameIndex++;
                yield return null;
            }
        }

        protected void Pose(CameraPose pose)
        {
            Cam.transform.position = pose.Position;
            Cam.transform.rotation = Quaternion.Euler(pose.Pitch, pose.Yaw, 0f);
        }

        // The height of the terrain: the seabed at -40 m west of the shore, a beach rising 4 % east of it, dunes of 30 m on top.
        public static float TerrainHeight(float x, float z)
        {
            if (x < 0f) return -40f + 10f * Mathf.Clamp01(1f + x / 400f);
            return Mathf.Min(0.04f * x, 160f) + 30f * Mathf.Sin(x * 0.0013f) * Mathf.Cos(z * 0.0011f) * Mathf.Clamp01(x / 600f);
        }

        private void BuildWorld()
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.30f, 0.32f, 0.36f) * Exposure;
            var skyMaterial = new Material(Shader.Find("Hidden/ReDefinition/WeatherPlayerSky"));
            skyMaterial.SetColor("_Horizon", new Color(0.60f, 0.63f, 0.64f) * Exposure);
            skyMaterial.SetColor("_Zenith", new Color(0.30f, 0.42f, 0.62f) * Exposure);
            skyMaterial.SetFloat("_Curve", 0.6f);
            RenderSettings.skybox = skyMaterial;

            Material template = Resources.Load<Material>("WeatherStandard");
            var land = new Material(template) { color = new Color(0.34f, 0.36f, 0.30f) };
            land.SetFloat("_Glossiness", 0.05f);
            var terrain = new GameObject("Terrain");
            terrain.AddComponent<MeshFilter>().sharedMesh = TerrainMesh(20000f, 160);
            terrain.AddComponent<MeshRenderer>().sharedMaterial = land;
            var water = new Material(template) { color = new Color(0.08f, 0.20f, 0.30f) };
            water.SetFloat("_Glossiness", 0.9f);
            sea = GameObject.CreatePrimitive(PrimitiveType.Plane);
            sea.name = "Sea";
            sea.transform.position = new Vector3(0f, SeaLevel, 0f);
            sea.transform.localScale = new Vector3(1000f, 1f, 1000f);
            sea.GetComponent<Renderer>().sharedMaterial = water;
            UnityEngine.Object.Destroy(sea.GetComponent<Collider>());

            Sun = new GameObject("Sun").AddComponent<Light>();
            Sun.type = LightType.Directional;
            Sun.transform.rotation = Quaternion.Euler(38f, 200f, 0f);
            Sun.shadows = LightShadows.None;
            Sun.intensity = 0.8f * Exposure;

            Vehicle = BuildVehicle();
            Target = new RenderTexture(Width, Height, 24, RenderTextureFormat.DefaultHDR) { name = "scene target" };
            Target.Create();
            Cam = new GameObject("Camera").AddComponent<Camera>();
            Cam.fieldOfView = 60f;
            Cam.nearClipPlane = 0.5f;
            Cam.farClipPlane = 60000f;
            Cam.renderingPath = RenderingPath.Forward;
            Cam.allowHDR = true;
            Cam.allowMSAA = false;
            Cam.clearFlags = CameraClearFlags.Skybox;
            Cam.targetTexture = Target;
            Cam.depthTextureMode = DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
            var screen = new GameObject("Screen").AddComponent<Camera>();
            screen.clearFlags = CameraClearFlags.Nothing;
            screen.cullingMask = 0;
            screen.depth = 10;
            screen.gameObject.AddComponent<SceneShow>().Source = Target;
            Pose(new CameraPose(new Vector3(2000f, 120f, -800f), 0f, 4f));
        }

        // A stand-in for a craft: a body of 3 m by 20 m on the beach and two lamps (spot lights) on its flanks.
        private GameObject BuildVehicle()
        {
            Material template = Resources.Load<Material>("WeatherStandard");
            var root = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            root.name = "Vehicle";
            root.transform.position = new Vector3(800f, TerrainHeight(800f, 0f) + 10f, 0f);
            root.transform.localScale = new Vector3(3f, 10f, 3f);
            root.GetComponent<Renderer>().sharedMaterial = new Material(template) { color = new Color(0.85f, 0.85f, 0.82f) };
            UnityEngine.Object.Destroy(root.GetComponent<Collider>());
            VehicleLamps = new Light[2];
            for (int i = 0; i < 2; i++)
            {
                var lamp = new GameObject(i == 0 ? "Lamp left" : "Lamp right").AddComponent<Light>();
                lamp.type = LightType.Spot;
                lamp.range = 120f;
                lamp.spotAngle = 40f;
                lamp.intensity = 2f;
                lamp.shadows = LightShadows.None;
                lamp.transform.SetParent(root.transform, false);
                lamp.transform.localPosition = new Vector3(i == 0 ? -0.6f : 0.6f, 0.2f, 0f);
                lamp.transform.localRotation = Quaternion.Euler(0f, 0f, 0f);
                VehicleLamps[i] = lamp;
            }
            return root;
        }

        private static Mesh TerrainMesh(float size, int cells)
        {
            int n = cells + 1;
            var vertices = new Vector3[n * n];
            var uvs = new Vector2[n * n];
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    float wx = (x / (float)cells - 0.5f) * size, wz = (z / (float)cells - 0.5f) * size;
                    vertices[z * n + x] = new Vector3(wx, TerrainHeight(wx, wz), wz);
                    uvs[z * n + x] = new Vector2(wx / 50f, wz / 50f);
                }
            var triangles = new int[cells * cells * 6];
            int k = 0;
            for (int z = 0; z < cells; z++)
                for (int x = 0; x < cells; x++)
                {
                    int a = z * n + x, b = a + 1, c = a + n, d = c + 1;
                    triangles[k++] = a; triangles[k++] = c; triangles[k++] = b;
                    triangles[k++] = b; triangles[k++] = c; triangles[k++] = d;
                }
            var mesh = new Mesh { name = "terrain", indexFormat = IndexFormat.UInt32 };
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    // Shows the scene's target in the window.
    internal sealed class SceneShow : MonoBehaviour
    {
        internal RenderTexture Source;

        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            Graphics.Blit(Source, destination);
        }
    }
}
