using System;
using System.Runtime.InteropServices;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // The numbers of the weather cell the player draws: the same size and place as the storm cell of the game's stored frames
    // (a forced cell of radius 6 km, base 600 m above the ground), the light and the wind. One object so that the runner's sweeps
    // can change a field and draw again.
    internal sealed class CellSettings
    {
        // The cell: radius, base and top above the ground, how much of the base is dark, the density factor, m.
        public float Radius = 6000f, BaseM = 600f, TopM = 1300f, Dark = 0.9f, Density = 1f;
        // The slab march: steps, the render scale of its texture, whether its dither moves from frame to frame.
        public float Steps = 32f, Scale = 0.5f;
        public bool AnimatedDither;
        // The light: towards the sun, the sun's colour and its share, and the sky's light, as the slab takes them.
        public Vector3 TowardsSun = new Vector3(0.3f, 0.6f, 0.74f).normalized;
        public Color SunColour = new Color(1.00f, 0.96f, 0.88f, 1f);
        public float SunShare = 0.3f;
        public Color Ambient = new Color(0.50f, 0.54f, 0.60f, 1f);
        // The wind: a direction in degrees, the pattern's speed and the vertical shear as CurtainMath has them.
        public float WindYaw = 20f, WindSpeed = 9f, Shear = 0.25f;

        // Sets a field by its name (the command line's -set name=value), false for a name that is not one.
        public bool Set(string name, float value)
        {
            switch (name)
            {
                case "radius": Radius = value; return true;
                case "base": BaseM = value; return true;
                case "top": TopM = value; return true;
                case "dark": Dark = value; return true;
                case "density": Density = value; return true;
                case "steps": Steps = value; return true;
                case "scale": Scale = value; return true;
                case "ambient": Ambient = new Color(0.50f, 0.54f, 0.60f, 1f) * value; return true;
                case "sun": SunShare = value; return true;
                case "dither": AnimatedDither = value > 0.5f; return true;
                default: return false;
            }
        }
    }

    // The volume pass of the atmospheres and the storm curtain, drawn into the player's camera: the volume by command buffers (the march
    // into a texture of its own size after the sky, then laid over the scene), the curtain as the mod draws it (a bounding cylinder with
    // the curtain shader). Both read the camera's depth. The GPU time of the volume goes through the proxy's timestamp queries
    // (WeatherGpuTimer); the curtain's is measured by VolumeCostScene.
    internal sealed class WeatherSlab : MonoBehaviour
    {
        private static readonly int IdCell = Shader.PropertyToID("_Cell"), IdUp = Shader.PropertyToID("_Up"),
            IdRight = Shader.PropertyToID("_Right"), IdWind = Shader.PropertyToID("_Wind"),
            IdLook = Shader.PropertyToID("_Look"), IdSun = Shader.PropertyToID("_Sun"), IdSunColour = Shader.PropertyToID("_SunColour"),
            IdAmbient = Shader.PropertyToID("_AmbientColour"), IdNoise = Shader.PropertyToID("_Noise"),
            IdHeights = Shader.PropertyToID("_Heights");

        // Whether the volume and the curtain are drawn, the settings, the jitter in render pixels, and the frame's number.
        internal bool SlabOn = true, CurtainOn = true;
        internal CellSettings Cell = new CellSettings();
        internal Vector2 JitterPixels;
        internal int Frame;
        // The curtain's rays are marched at most this far while the camera is inside the cell, m, and its step count.
        internal float CurtainSteps = 28f;

        private Camera cam;
        private Material curtainMaterial;
        private Texture3D noise;
        private CommandBuffer markBeforeCurtain, markAfterCurtain;
        private GameObject curtainObject;
        private Transform curtainTransform;

        // The march texture the last frame wrote, for the analytic motion pass.
        internal RenderTexture SlabTexture { get { return Volume != null ? Volume.Renderer.Target : null; } }

        // Why the volume or the curtain cannot be drawn, or null.
        internal string Problem;

        // The volume pass of the atmospheres (scenario -scenario, tuning -vset name=v;name=v; -volume 0 draws the curtain alone): the
        // window, the renderer and the frame numbers.
        internal VolumeSetup Volume;

        private static readonly Action<CommandBuffer> MarkSlabStart = cb => WeatherGpuTimer.Mark(cb, WeatherGpuTimer.SlabStart),
            MarkSlabMarched = cb => WeatherGpuTimer.Mark(cb, WeatherGpuTimer.SlabMarched),
            MarkSlabEnd = cb => WeatherGpuTimer.Mark(cb, WeatherGpuTimer.SlabComposited);

        // Whether the pass draws the volume (and so the analytic motion comes from it).
        internal bool VolumeMode { get { return Volume != null; } }

        // The jitter index of the frame (the upscaler's phase), set by the pass with the jitter.
        internal int JitterIndex;

        // The volume pass's share S of the curtain gate, 0 to 1 (the curtain's step count follows it).
        internal float CurtainShare = 1f;

        internal void Init(Camera camera)
        {
            cam = camera;
            Shader curtainShader = Shader.Find("ReDefinition/AtmospheresStormVolume");
            if (curtainShader == null)
            {
                Problem = "shader missing: curtain";
                return;
            }
            int size = CurtainMath.NoiseSize;
            byte[] voxels = StormNoise.Volume(size, CurtainMath.NoisePeriod, CurtainMath.NoiseOctaves, CurtainMath.NoiseSeed);
            noise = new Texture3D(size, size, size, TextureFormat.RGBA32, true)
            {
                name = "weather player noise",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear
            };
            var colours = new Color32[voxels.Length];
            for (int i = 0; i < voxels.Length; i++) colours[i] = new Color32(voxels[i], voxels[i], voxels[i], 255);
            noise.SetPixels32(colours);
            noise.Apply(true, true);

            curtainMaterial = new Material(curtainShader) { hideFlags = HideFlags.HideAndDontSave };
            curtainMaterial.SetTexture(IdNoise, noise);
            curtainMaterial.EnableKeyword("STORM_DEPTH");

            curtainObject = new GameObject("weather player curtain");
            curtainObject.AddComponent<MeshFilter>().sharedMesh = CylinderMesh();
            var renderer = curtainObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = curtainMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            curtainTransform = curtainObject.transform;

            markBeforeCurtain = new CommandBuffer { name = "weather curtain start" };
            markAfterCurtain = new CommandBuffer { name = "weather curtain end" };
            WeatherGpuTimer.Mark(markBeforeCurtain, WeatherGpuTimer.CurtainStart);
            WeatherGpuTimer.Mark(markAfterCurtain, WeatherGpuTimer.CurtainEnd);
            cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, markBeforeCurtain);
            cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, markAfterCurtain);

            string volumeArgument = WeatherPass.Argument("-volume");
            if (volumeArgument != "0")
            {
                Volume = new VolumeSetup();
                string vset = WeatherPass.Argument("-vset");
                if (!string.IsNullOrEmpty(vset))
                {
                    foreach (string pair in vset.Split(';'))
                    {
                        string[] kv = pair.Split('=');
                        float v;
                        if (kv.Length == 2 && float.TryParse(kv[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
                            Volume.SetOverride(kv[0], v);
                    }
                }
                if (!Volume.Init(WeatherPass.Argument("-scenario") ?? "convective", VolumeSetup.TierArgument()))
                {
                    Problem = Volume.Problem;
                    Volume = null;
                    return;
                }
                Volume.Renderer.Attach(cam, CameraEvent.AfterSkybox);
                // The curtain stands under the core: its base is the core's, its width a share of the core's radius.
                Cell.BaseM = 1100f;
                Cell.Radius = 4500f;
                CurtainShare = Volume.Get("volume.curtainS", 1f);
                CurtainSteps = VolumeMath.CurtainSteps(CurtainShare);
            }
        }

        // The bounding cylinder of the curtain, radius 1 and height 1 from y 0, as CurtainPass builds it.
        private static Mesh CylinderMesh()
        {
            float[] v = CurtainMath.CylinderVertices(CurtainMath.Segments);
            var vertices = new Vector3[v.Length / 3];
            for (int i = 0; i < vertices.Length; i++) vertices[i] = new Vector3(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]);
            var mesh = new Mesh { name = "weather player cylinder" };
            mesh.vertices = vertices;
            mesh.triangles = CurtainMath.CylinderTriangles(CurtainMath.Segments);
            mesh.bounds = new Bounds(new Vector3(0f, 0.5f, 0f), new Vector3(2f, 1f, 2f));
            return mesh;
        }

        // The curtain's frame and the wind offsets of the frame; written before the camera culls. The frame's time is its number
        // over 60, so that a run repeats.
        internal void Prepare()
        {
            if (curtainMaterial == null) return;
            CellSettings c = Cell;
            float time = Frame / 60f;
            double yaw = c.WindYaw * Math.PI / 180.0;
            float dx = (float)Math.Cos(yaw), dz = (float)Math.Sin(yaw);
            float offsetX = CurtainMath.Wrap(dx * c.WindSpeed * time, CurtainMath.CloudTileM);
            float offsetZ = CurtainMath.Wrap(dz * c.WindSpeed * time, CurtainMath.CloudTileM);
            float fall = CurtainMath.Wrap(CurtainMath.FallSpeed * time, CurtainMath.StreakTileM);

            Transform t = cam.transform;
            Material m = curtainMaterial;
            m.SetVector(IdCell, new Vector4(0f, 0f, 0f, c.Radius));
            m.SetVector(IdUp, new Vector4(0f, 1f, 0f, 0f));
            m.SetVector(IdRight, new Vector4(1f, 0f, 0f, 0f));
            m.SetVector(IdSun, new Vector4(c.TowardsSun.x, c.TowardsSun.y, c.TowardsSun.z, c.SunShare));
            m.SetVector(IdSunColour, c.SunColour);

            float below = 150f;
            curtainTransform.position = new Vector3(0f, -below, 0f);
            curtainTransform.localScale = new Vector3(c.Radius * CurtainMath.Widening, c.BaseM + CurtainMath.SnowOnlyHeadroomM + below,
                                                      c.Radius * CurtainMath.Widening);
            m.SetVector(IdHeights, new Vector4(c.BaseM, c.BaseM + CurtainMath.SnowOnlyHeadroomM, below, 0.6f));
            m.SetVector(IdWind, new Vector4(offsetX, offsetZ, dx * (float)CurtainMath.Shear, fall));
            float longest = 60000f + c.Radius * 2f;
            Vector3 p = t.position;
            if (Mathf.Sqrt(p.x * p.x + p.z * p.z) < c.Radius) longest = Mathf.Min(longest, Mathf.Max(4f * 60f, 1.5f * c.BaseM));
            m.SetVector(IdLook, new Vector4(1f, CurtainSteps, dz * (float)CurtainMath.Shear, longest));
            m.SetVector(IdAmbient, new Color(0.72f, 0.76f, 0.82f, 1f));
            curtainObject.SetActive(CurtainOn);

            if (Volume != null)
            {
                Volume.Renderer.Commands.Clear();
                if (SlabOn)
                    Volume.Prepare(cam, c, Frame, time, JitterPixels, JitterIndex, MarkSlabStart, MarkSlabMarched, MarkSlabEnd, Volume.Get("volume.analytic", 1f) > 0.5f);
            }
        }
    }

    // The GPU time of the slab's passes and the curtain, by the proxy's timestamp queries (src/Probe/GpuProfiler.cs asks the same
    // exports): a marker issued into the render stream ends a query, and the proxy hands back the frame's markers in order with
    // the milliseconds from the Present before to each.
    internal static class WeatherGpuTimer
    {
        internal const int SlabStart = 101, SlabMarched = 102, SlabComposited = 103, CurtainStart = 104, CurtainEnd = 105;
        private const string Proxy = ReDefinition.Proxy.ProxyDll.Name;
        private static IntPtr markerFunc;
        private static bool failed;
        private static readonly int[] ids = new int[256];
        private static readonly float[] ms = new float[256];
        private static int lastFrame = -1;

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr KspProfGetMarkerFunc();

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern void KspProfSetEnabled(int enabled);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern void KspProfSetLabel(int id, string label);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspProfRead([Out] int[] ids, [Out] float[] ms, int capacity, out int frameNumber);

        // Whether the proxy answers.
        internal static bool Available { get { return markerFunc != IntPtr.Zero; } }

        // Turns the proxy's queries on; false where its exports are not there.
        internal static bool Start()
        {
            if (failed) return false;
            try
            {
                markerFunc = KspProfGetMarkerFunc();
                KspProfSetEnabled(1);
                KspProfSetLabel(SlabStart, "slab start");
                KspProfSetLabel(SlabMarched, "slab march");
                KspProfSetLabel(SlabComposited, "slab composite");
                KspProfSetLabel(CurtainStart, "curtain start");
                KspProfSetLabel(CurtainEnd, "curtain");
                return markerFunc != IntPtr.Zero;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                failed = true;
                return false;
            }
        }

        internal static void Mark(CommandBuffer buffer, int id)
        {
            if (markerFunc != IntPtr.Zero) buffer.IssuePluginEvent(markerFunc, id);
        }

        // The last complete frame's segments: the frame's GPU ms in total, and the ms up to each of our markers from the one
        // before it, in `into` by marker id (index 0..255 only for ids 101..105). False where there is no new frame.
        internal static bool Read(float[] segments, out float total, out int frame)
        {
            total = 0f;
            frame = 0;
            if (markerFunc == IntPtr.Zero) return false;
            int count = KspProfRead(ids, ms, ids.Length, out frame);
            if (frame == lastFrame || count < 2) return false;
            lastFrame = frame;
            total = ms[count - 1];
            for (int i = 0; i < segments.Length; i++) segments[i] = -1f;
            for (int i = 1; i < count; i++)
            {
                int slot = ids[i] - SlabStart;
                if (slot >= 0 && slot < segments.Length) segments[slot] = ms[i] - ms[i - 1];
            }
            return true;
        }
    }
}
