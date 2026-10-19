using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using ReDefinition.Core;
using ReDefinition.Shared;
using ReDefinition.Upscaler;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Scenery
{
    // Parallax's grass on the space centre's lawns. Parallax grows its grass on the terrain,
    // which lies 2.7 to 3.2 m under the space centre's grass surfaces (measured 2026-10-05,
    // the KscGrassGap probe), so its clumps stay under them. Here Parallax's own clumps of
    // this frame -- its ScatterRenderers' outputLOD0-2, with their density, noise, scale,
    // turn and the terrain's colour -- are lifted onto the lawns where they lie under one
    // (ReDefinitionKscGrass.compute) and drawn with Parallax's own meshes and materials,
    // so its shader, wind and lighting. The lawns: the triangles of the space centre's
    // ground meshes drawn with the KSC ground shaders, where their blend mask (the red of
    // _BlendMaskTexture, on the second texture coordinates: tarmac) leaves grass and no
    // collider of the space centre stands over them; a clump only where all of its model's
    // reach lies on them. Without Parallax, or with one whose fields differ, nothing is
    // drawn. OwnSettings.KscGrass switches it.
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    public sealed class KscGrass : MonoBehaviour
    {
        // Parallax's scatters on Kerbin that grow on lawns.
        private static readonly string[] GrassScatters = { "KerbinGrass", "KerbinGrassTall", "KerbinFern", "Daisy" };
        // The lawns' map: metres per texel.
        private const float Texel = 1f;
        // Where the blend mask is at least this much tarmac, a metre of lawn is not grass.
        private const float MostTarmac = 0.25f;
        // The share of a clump's model reach that must stand on the lawn.
        private const float Footprint = 0.85f;
        // A collider this far over the lawn (a pipe, a wall) keeps clumps off.
        private const float Overhead = 0.3f;
        private const float RayHeight = 30f;
        // Room beyond this many metres is not told apart.
        private const float MostRoom = 16f;

        private sealed class Kind
        {
            public string Name;
            public object Renderer;
            public readonly FieldInfo[] Outputs = new FieldInfo[3];
            public readonly Mesh[] Meshes = new Mesh[3];
            public readonly Material[] Materials = new Material[3];
            public float Reach;
            public readonly ComputeBuffer[] Lifted = new ComputeBuffer[3];
            // Parallax's clumps of this frame no ground of the space centre covers, for the
            // rays (RaysBuffer).
            public readonly ComputeBuffer[] Kept = new ComputeBuffer[3];
            public readonly int[] KeptFrame = { -1, -1, -1 };
            public readonly ComputeBuffer[] Arguments = new ComputeBuffer[3];
            public readonly MaterialPropertyBlock[] Properties = new MaterialPropertyBlock[3];
        }

        // A triangle of the space centre's lawns, relative to the space centre's root.
        private sealed class Lawn
        {
            public Vector3 A, B, C;
            public Vector2 UvA, UvB, UvC;
            public Color32[] Mask;
            public Vector2Int MaskSize;

            public float Tarmac(Vector2 uv)
            {
                int x = Mathf.Clamp((int)((uv.x - Mathf.Floor(uv.x)) * MaskSize.x), 0, MaskSize.x - 1);
                int y = Mathf.Clamp((int)((uv.y - Mathf.Floor(uv.y)) * MaskSize.y), 0, MaskSize.y - 1);
                return Mask[y * MaskSize.x + x].r / 255f;
            }
        }

        private readonly List<Kind> kinds = new List<Kind>();
        private Transform root;
        private ComputeShader lift;
        private int liftKernel = -1;
        private Texture2D lawnMap;
        private Vector4 grid;
        private ComputeBuffer counts;
        private readonly uint[] arguments = new uint[5];
        private bool ready;
        private int[] capacities = new int[0];

        // Whether Parallax is installed, looked up once: the settings window asks it as it
        // draws the row.
        private static bool? parallaxInstalled;
        internal static bool ParallaxInstalled
        {
            get
            {
                if (parallaxInstalled == null) parallaxInstalled = TypeLookup.Find("Parallax.ScatterManager") != null;
                return parallaxInstalled.Value;
            }
        }
        private int drawnFrames;

        private static readonly int SourceId = Shader.PropertyToID("_Source");
        private static readonly int CountsId = Shader.PropertyToID("_Counts");
        private static readonly int CountIndexId = Shader.PropertyToID("_CountIndex");
        private static readonly int CapacityId = Shader.PropertyToID("_Capacity");
        private static readonly int LiftedId = Shader.PropertyToID("_Lifted");
        private static readonly int KeptId = Shader.PropertyToID("_Kept");
        private static readonly int WorldToRootId = Shader.PropertyToID("_WorldToRoot");
        private static readonly int RootToWorldId = Shader.PropertyToID("_RootToWorld");
        private static readonly int LawnsId = Shader.PropertyToID("_Lawns");
        private static readonly int GridId = Shader.PropertyToID("_Grid");
        private static readonly int SizeId = Shader.PropertyToID("_Size");
        private static readonly int ReachId = Shader.PropertyToID("_Reach");
        private static readonly int InstanceDataId = Shader.PropertyToID("_InstanceData");

        private IEnumerator Start()
        {
            bool failed = false;
            // Parallax makes its scatters' renderers once the terrain around them is built,
            // some 2.7 s into the scene (measured 2026-10-13): asked four times a second for a
            // minute, rather than after 3 s and then every 5 s, which kept the lawns bare for
            // 9 to 12 s.
            for (int attempt = 0; attempt < 240 && kinds.Count == 0 && !failed; attempt++)
            {
                yield return new WaitForSeconds(0.25f);
                try
                {
                    failed = !FindParallax();
                }
                catch (Exception e)
                {
                    failed = true;
                    Debug.Log(Log.Tag + " Space centre grass: none (Parallax's scatters not as known: " + e.GetType().Name + ": "
                              + e.Message + ").");
                }
            }
            if (failed) yield break;
            if (kinds.Count == 0)
            {
                Debug.Log(Log.Tag + " Space centre grass: none (no Parallax grass on this body).");
                yield break;
            }
            try
            {
                string problem;
                lift = ShaderBundle.LoadCompute("ReDefinitionKscGrass", out problem);
                liftKernel = lift != null ? lift.FindKernel("Lift") : -1;
                if (liftKernel < 0)
                {
                    Debug.LogWarning(Log.Tag + " Space centre grass: none (" + (problem ?? "the lift kernel did not compile") + ").");
                    yield break;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Space centre grass: none (" + e.GetType().Name + ": " + e.Message + ").");
                yield break;
            }
            yield return MapLawns();
            if (!ready) yield break;
            Camera.onPreCull += Draw;
            current = this;
            // Made again once the space centre stands whole: KSP switches its parts on by
            // distance, and lawns and the colliders over them that were not on yet were
            // missing from the first map (measured: 398 000 m2 of lawn in a flight's first
            // map, 478 000 in the space centre's).
            yield return new WaitForSeconds(10f);
            try
            {
                // A scatter whose meshes were not loaded yet at the first look.
                FindParallax();
            }
            catch (Exception)
            {
                // The scatters found first are kept.
            }
            yield return MapLawns();
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= Draw;
            if (current == this) current = null;
            foreach (Kind kind in kinds)
                for (int lod = 0; lod < 3; lod++)
                {
                    kind.Lifted[lod]?.Release();
                    kind.Kept[lod]?.Release();
                    kind.Arguments[lod]?.Release();
                }
            counts?.Release();
            if (lawnMap != null) Destroy(lawnMap);
        }

        private static KscGrass current;

        // The lifted clumps of this frame for the raytracing, as Parallax's own scatter
        // (RtKspMods.ParallaxScatter): each scatter's buffers with its mesh and material.
        internal static int CollectGpu(List<Raytracing.RtGpuItem> into)
        {
            KscGrass grass = current;
            if (grass == null || !grass.ready || !Enabled()) return 0;
            int buffers = 0;
            foreach (Kind kind in grass.kinds)
                for (int lod = 0; lod < 3; lod++)
                {
                    if (kind.Lifted[lod] == null || !kind.Lifted[lod].IsValid() || kind.Meshes[lod] == null) continue;
                    into.Add(new Raytracing.RtGpuItem
                    {
                        Transforms = kind.Lifted[lod],
                        Mesh = kind.Meshes[lod],
                        SubMesh = 0,
                        Material = kind.Materials[lod],
                        Mask = 0xFF,
                    });
                    buffers++;
                }
            return buffers;
        }

        // The buffer the rays take for a renderer's level of detail of Parallax's scatter
        // (RtKspMods.ParallaxScatter): where its clumps are lifted this frame, the ones no
        // ground of the space centre covers (Kept); else Parallax's own. Under the space
        // centre's ground Parallax's clumps stand on the terrain metres below it, where no ray
        // reaches them. At the launch pad on an RTX A4500 (measured 2026-10-19): the ones under
        // the ground left out, the sun's, the diffuse and the reflected rays 6.66 ms to 6.1, the
        // frame 34.85 ms to 34.3, the picture the same (every tile within 0.1 of a thousandth);
        // all of Parallax's own left out (-redefinitionProbeNoParallaxScatterRays), 5.0 ms,
        // their shadows on the terrain around the space centre gone with them.
        internal static ComputeBuffer RaysBuffer(object renderer, int lod, ComputeBuffer own)
        {
            KscGrass grass = current;
            if (grass == null || !grass.ready || !Enabled() || own == null) return own;
            foreach (Kind kind in grass.kinds)
                if (ReferenceEquals(kind.Renderer, renderer))
                {
                    ComputeBuffer kept = kind.Kept[lod];
                    // Filled this frame or the last: the rays collect their buffers in the
                    // camera's OnPreCull, which may come before this frame's Draw, and read
                    // them as the camera renders, after Draw's dispatch.
                    return kind.KeptFrame[lod] >= Time.frameCount - 1 && kept != null && kept.IsValid() && kept.count == own.count
                        ? kept : own;
                }
            return own;
        }

        private static bool Enabled()
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            return addon == null || addon.Live.KscGrass;
        }

        // Parallax.ScatterManager.Instance.activeScatterRenderers: each ScatterRenderer's
        // planetName, scatter (scatterName "<body>-<name>"), meshLOD0-2,
        // instancedMaterialLOD0-2 and outputLOD0-2. False where Parallax is not installed.
        private bool FindParallax()
        {
            Type managerType = TypeLookup.Find("Parallax.ScatterManager");
            if (managerType == null)
            {
                Debug.Log(Log.Tag + " Space centre grass: none (Parallax is not installed).");
                return false;
            }
            const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance;
            object manager = managerType.GetField("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            IList renderers = manager != null ? managerType.GetField("activeScatterRenderers", Public).GetValue(manager) as IList : null;
            if (renderers == null) return true;
            string body = FlightGlobals.currentMainBody != null ? FlightGlobals.currentMainBody.name : null;
            foreach (object renderer in renderers)
            {
                if (renderer == null) continue;
                Type rendererType = renderer.GetType();
                string planet = (string)rendererType.GetField("planetName", Public).GetValue(renderer);
                if (body != null && planet != body) continue;
                object scatter = rendererType.GetField("scatter", Public).GetValue(renderer);
                if (scatter == null) continue;
                string name = (string)scatter.GetType().GetField("scatterName", Public).GetValue(scatter);
                if (name == null || !name.StartsWith(planet + "-", StringComparison.Ordinal)) continue;
                name = name.Substring(planet.Length + 1);
                if (Array.IndexOf(GrassScatters, name) < 0 || kinds.Exists(k => k.Name == name)) continue;
                var kind = new Kind { Name = name, Renderer = renderer };
                for (int lod = 0; lod < 3; lod++)
                {
                    kind.Meshes[lod] = rendererType.GetField("meshLOD" + lod, Public).GetValue(renderer) as Mesh;
                    kind.Materials[lod] = rendererType.GetField("instancedMaterialLOD" + lod, Public).GetValue(renderer) as Material;
                    kind.Outputs[lod] = rendererType.GetField("outputLOD" + lod, Public);
                    if (kind.Outputs[lod] == null || kind.Outputs[lod].FieldType != typeof(ComputeBuffer))
                        throw new MissingFieldException("ScatterRenderer.outputLOD" + lod);
                }
                if (kind.Meshes[0] == null || kind.Materials[0] == null) continue;
                Bounds model = kind.Meshes[0].bounds;
                kind.Reach = Footprint * Mathf.Max(Mathf.Max(Mathf.Abs(model.min.x), Mathf.Abs(model.max.x)),
                                                   Mathf.Max(Mathf.Abs(model.min.z), Mathf.Abs(model.max.z)));
                kinds.Add(kind);
            }
            return true;
        }

        // The lawns' map in the plane of the space centre's root, a texel a metre: the lawn's
        // height and the room around it, in metres, to tarmac, the lawn's edge or a collider
        // over it. Most of it off the main thread -- at once it held a frame for 0.7 s
        // (measured 2026-10-05, the descent from the main menu) --: the triangles read here,
        // rasterised and the rays laid out on a thread, the rays cast as Unity's job, the
        // room measured on a thread, the texture made here. Sets ready.
        private IEnumerator MapLawns()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            long mainThread = 0;
            PQSCity ksc = null;
            foreach (PQSCity city in FindObjectsOfType<PQSCity>())
                if (city.name == "KSC") ksc = city;
            if (ksc == null)
            {
                Debug.Log(Log.Tag + " Space centre grass: none (the space centre not found).");
                yield break;
            }
            root = ksc.transform;
            var lawns = new List<Lawn>();
            mainThread += watch.ElapsedMilliseconds;
            // Over as many frames as it takes, a few milliseconds each (CollectLawns).
            IEnumerator collect = CollectLawns(ksc, root.worldToLocalMatrix, lawns);
            while (true)
            {
                long step = watch.ElapsedMilliseconds;
                bool more;
                try
                {
                    more = collect.MoveNext();
                }
                catch (Exception e)
                {
                    Debug.LogWarning(Log.Tag + " Space centre grass: none (" + e.GetType().Name + ": " + e.Message + ").");
                    yield break;
                }
                mainThread += watch.ElapsedMilliseconds - step;
                if (!more) break;
                yield return null;
            }
            if (lawns.Count == 0)
            {
                Debug.Log(Log.Tag + " Space centre grass: none (no lawns found).");
                yield break;
            }
            Matrix4x4 rootToWorld = root.localToWorldMatrix;
            Vector3 down = -root.up;
            int layerMask = 1 << root.gameObject.layer;

            var map = new LawnMap();
            NativeArray<RaycastCommand> commands = default(NativeArray<RaycastCommand>);
            NativeArray<RaycastHit> hits = default(NativeArray<RaycastHit>);
            try
            {
                var raster = System.Threading.Tasks.Task.Run(() => map.Rasterise(lawns));
                while (!raster.IsCompleted) yield return null;
                if (raster.Exception != null) throw raster.Exception.InnerException;

                long before = watch.ElapsedMilliseconds;
                commands = new NativeArray<RaycastCommand>(map.Lawn.Count, Allocator.Persistent);
                hits = new NativeArray<RaycastHit>(map.Lawn.Count, Allocator.Persistent);
                mainThread += watch.ElapsedMilliseconds - before;
                NativeArray<RaycastCommand> laid = commands;
                var layout = System.Threading.Tasks.Task.Run(() => map.LayRays(laid, rootToWorld, down, layerMask));
                while (!layout.IsCompleted) yield return null;
                if (layout.Exception != null) throw layout.Exception.InnerException;

                before = watch.ElapsedMilliseconds;
                JobHandle cast = RaycastCommand.ScheduleBatch(commands, hits, 256);
                mainThread += watch.ElapsedMilliseconds - before;
                while (!cast.IsCompleted) yield return null;
                cast.Complete();

                NativeArray<RaycastHit> found = hits;
                var measure = System.Threading.Tasks.Task.Run(() => map.Measure(found));
                while (!measure.IsCompleted) yield return null;
                if (measure.Exception != null) throw measure.Exception.InnerException;
            }
            finally
            {
                if (commands.IsCreated) commands.Dispose();
                if (hits.IsCreated) hits.Dispose();
            }

            long started = watch.ElapsedMilliseconds;
            // A map made again replaces the one before in the same frame.
            if (lawnMap != null) Destroy(lawnMap);
            grid = new Vector4(map.X0, map.Z0, Texel, map.Y0);
            lawnMap = new Texture2D(map.Width, map.Depth, TextureFormat.RGHalf, false, true)
            {
                name = "ReDefinition space centre lawns",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            lawnMap.SetPixelData(map.Texels, 0);
            lawnMap.Apply(false, true);
            if (counts == null || counts.count != kinds.Count * 3)
            {
                counts?.Release();
                counts = new ComputeBuffer(kinds.Count * 3, 4, ComputeBufferType.Raw);
            }
            mainThread += watch.ElapsedMilliseconds - started;
            ready = true;
            Debug.Log(Log.Tag + " Space centre grass: Parallax's " + string.Join(", ", kinds.ConvertAll(k => k.Name).ToArray())
                      + " lifted onto " + (map.Lawn.Count - map.Covered) + " m2 of lawn (" + map.Covered + " m2 under a collider left out, map "
                      + map.Width + " x " + map.Depth + " m); " + watch.ElapsedMilliseconds + " ms, " + mainThread
                      + " ms of them on the main thread.");
        }

        // The lawns' map's data, worked out off the main thread.
        private sealed class LawnMap
        {
            public float X0, Z0, Y0;
            public int Width, Depth;
            private float[] height;
            private bool[] lawnAt;
            // The texels the space centre's ground covers, lawn or not.
            private bool[] groundAt;
            // The lawn texels, in the order of their rays.
            public readonly List<int> Lawn = new List<int>();
            public int Covered;
            public ushort[] Texels;

            public void Rasterise(List<Lawn> lawns)
            {
                float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue, y0 = float.MaxValue;
                foreach (Lawn lawn in lawns)
                    foreach (Vector3 p in new[] { lawn.A, lawn.B, lawn.C })
                    {
                        x0 = Math.Min(x0, p.x);
                        x1 = Math.Max(x1, p.x);
                        z0 = Math.Min(z0, p.z);
                        z1 = Math.Max(z1, p.z);
                        y0 = Math.Min(y0, p.y);
                    }
                X0 = x0;
                Z0 = z0;
                Y0 = y0;
                int width = Width = (int)Math.Ceiling((x1 - x0) / Texel) + 1;
                int depth = Depth = (int)Math.Ceiling((z1 - z0) / Texel) + 1;
                height = new float[width * depth];
                lawnAt = new bool[width * depth];
                groundAt = new bool[width * depth];
                foreach (Lawn lawn in lawns)
                {
                    var a = new Vector2(lawn.A.x, lawn.A.z);
                    Vector2 ab = new Vector2(lawn.B.x, lawn.B.z) - a, ac = new Vector2(lawn.C.x, lawn.C.z) - a;
                    float det = ab.x * ac.y - ab.y * ac.x;
                    if (Math.Abs(det) < 1e-6f) continue;
                    int ax = (int)Math.Floor((Math.Min(lawn.A.x, Math.Min(lawn.B.x, lawn.C.x)) - x0) / Texel);
                    int bx = (int)Math.Floor((Math.Max(lawn.A.x, Math.Max(lawn.B.x, lawn.C.x)) - x0) / Texel);
                    int az = (int)Math.Floor((Math.Min(lawn.A.z, Math.Min(lawn.B.z, lawn.C.z)) - z0) / Texel);
                    int bz = (int)Math.Floor((Math.Max(lawn.A.z, Math.Max(lawn.B.z, lawn.C.z)) - z0) / Texel);
                    for (int x = Math.Max(ax, 0); x <= bx && x < width; x++)
                        for (int z = Math.Max(az, 0); z <= bz && z < depth; z++)
                        {
                            Vector2 q = new Vector2(x0 + (x + 0.5f) * Texel, z0 + (z + 0.5f) * Texel) - a;
                            float u = (q.x * ac.y - q.y * ac.x) / det, w = (ab.x * q.y - ab.y * q.x) / det;
                            if (u < 0f || w < 0f || u + w > 1f) continue;
                            groundAt[z * width + x] = true;
                            if (lawn.Mask != null && lawn.Tarmac(lawn.UvA + u * (lawn.UvB - lawn.UvA) + w * (lawn.UvC - lawn.UvA)) >= MostTarmac)
                                continue;
                            int i = z * width + x;
                            float y = lawn.A.y + u * (lawn.B.y - lawn.A.y) + w * (lawn.C.y - lawn.A.y);
                            if (!lawnAt[i] || y > height[i]) height[i] = y;
                            lawnAt[i] = true;
                        }
                }
                for (int i = 0; i < lawnAt.Length; i++)
                    if (lawnAt[i]) Lawn.Add(i);
            }

            // A ray down onto each lawn texel from RayHeight over it.
            public void LayRays(NativeArray<RaycastCommand> commands, Matrix4x4 rootToWorld, Vector3 down, int layerMask)
            {
                for (int n = 0; n < Lawn.Count; n++)
                {
                    int i = Lawn[n];
                    var local = new Vector3(X0 + (i % Width + 0.5f) * Texel, height[i] + RayHeight, Z0 + (i / Width + 0.5f) * Texel);
                    commands[n] = new RaycastCommand(rootToWorld.MultiplyPoint3x4(local), down, RayHeight + Overhead, layerMask, 1);
                }
            }

            // Texels a collider stands over are no lawn; the room around the rest; the
            // texture's data.
            public void Measure(NativeArray<RaycastHit> hits)
            {
                for (int n = 0; n < Lawn.Count; n++)
                {
                    // A ray that met nothing leaves a distance of 0.
                    float distance = hits[n].distance;
                    if (distance > 0f && distance < RayHeight - Overhead)
                    {
                        lawnAt[Lawn[n]] = false;
                        Covered++;
                    }
                }
                float[] room = Room(lawnAt, Width, Depth);
                Texels = new ushort[Width * Depth * 2];
                for (int i = 0; i < lawnAt.Length; i++)
                {
                    Texels[i * 2] = Half(lawnAt[i] ? height[i] - Y0 : 0f);
                    // A lawn texel without room counts as ground, not as no ground.
                    Texels[i * 2 + 1] = Half(lawnAt[i] && room[i] > 0f ? room[i] : groundAt[i] ? -1f : 0f);
                }
            }

            // A float as IEEE half, for values from 0 to some hundreds: managed, for this
            // thread, where Unity's Mathf.FloatToHalf calls into the engine.
            private static ushort Half(float value)
            {
                int bits = BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
                int sign = (bits >> 16) & 0x8000;
                int exponent = ((bits >> 23) & 0xFF) - 127 + 15;
                int mantissa = bits & 0x7FFFFF;
                if (value == 0f || exponent <= 0) return (ushort)sign;
                if (exponent >= 31) return (ushort)(sign | 0x7BFF);
                return (ushort)(sign | (exponent << 10) | (mantissa >> 13));
            }
        }

        // Each lawn texel's distance to the nearest texel that is not lawn, in metres up to
        // MostRoom (a two-pass chamfer distance).
        private static float[] Room(bool[] lawnAt, int width, int depth)
        {
            const float Straight = Texel, Diagonal = Texel * 1.41421356f;
            var room = new float[lawnAt.Length];
            for (int i = 0; i < room.Length; i++) room[i] = lawnAt[i] ? MostRoom : 0f;
            for (int z = 0; z < depth; z++)
                for (int x = 0; x < width; x++)
                {
                    int i = z * width + x;
                    if (room[i] == 0f) continue;
                    float r = room[i];
                    r = Mathf.Min(r, x > 0 ? room[i - 1] + Straight : Straight);
                    r = Mathf.Min(r, z > 0 ? room[i - width] + Straight : Straight);
                    r = Mathf.Min(r, x > 0 && z > 0 ? room[i - width - 1] + Diagonal : Diagonal);
                    r = Mathf.Min(r, x < width - 1 && z > 0 ? room[i - width + 1] + Diagonal : Diagonal);
                    room[i] = r;
                }
            for (int z = depth - 1; z >= 0; z--)
                for (int x = width - 1; x >= 0; x--)
                {
                    int i = z * width + x;
                    if (room[i] == 0f) continue;
                    float r = room[i];
                    r = Mathf.Min(r, x < width - 1 ? room[i + 1] + Straight : Straight);
                    r = Mathf.Min(r, z < depth - 1 ? room[i + width] + Straight : Straight);
                    r = Mathf.Min(r, x < width - 1 && z < depth - 1 ? room[i + width + 1] + Diagonal : Diagonal);
                    r = Mathf.Min(r, x > 0 && z < depth - 1 ? room[i + width - 1] + Diagonal : Diagonal);
                    room[i] = r;
                }
            return room;
        }

        // The lawns' masks as read back from the GPU, kept for the map made again (MapLawns
        // runs twice a flight): the read waits for the GPU.
        private static readonly Dictionary<Texture, Color32[]> masks = new Dictionary<Texture, Color32[]>();
        private static readonly Dictionary<Texture, Vector2Int> maskSizes = new Dictionary<Texture, Vector2Int>();
        private const long CollectSliceMs = 4;

        // Stepped by MapLawns, a frame's share at a time: in one frame it took up to 42 ms of
        // the main thread, the second map in flight a visible stall (the perf monitor).
        private static IEnumerator CollectLawns(PQSCity ksc, Matrix4x4 rootToLocal, List<Lawn> lawns)
        {
            var materials = new List<Material>();
            var slice = System.Diagnostics.Stopwatch.StartNew();
            foreach (MeshFilter filter in ksc.GetComponentsInChildren<MeshFilter>(false))
            {
                if (slice.ElapsedMilliseconds >= CollectSliceMs)
                {
                    yield return null;
                    slice.Reset();
                    slice.Start();
                }
                if (filter == null) continue;
                MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                Mesh ground = filter.sharedMesh;
                if (renderer == null || !renderer.enabled || ground == null || !ground.isReadable) continue;
                renderer.GetSharedMaterials(materials);
                Vector3[] vertices = ground.vertices;
                var uv2 = new List<Vector2>();
                ground.GetUVs(1, uv2);
                Matrix4x4 meshToRoot = rootToLocal * filter.transform.localToWorldMatrix;
                for (int s = 0; s < ground.subMeshCount && s < materials.Count; s++)
                {
                    Material material = materials[s];
                    if (material == null || material.shader == null
                        || material.shader.name.IndexOf("Diffuse Ground KSC", StringComparison.Ordinal) < 0)
                        continue;
                    Texture maskTexture = material.HasProperty("_BlendMaskTexture") ? material.GetTexture("_BlendMaskTexture") : null;
                    Vector4 maskScale = material.HasProperty("_BlendMaskTexture_ST")
                        ? material.GetVector("_BlendMaskTexture_ST") : new Vector4(1, 1, 0, 0);
                    Color32[] mask = null;
                    Vector2Int maskSize = Vector2Int.zero;
                    if (maskTexture != null && uv2.Count == vertices.Length)
                    {
                        if (!masks.TryGetValue(maskTexture, out mask))
                        {
                            masks[maskTexture] = mask = TexturePixels.Read(maskTexture, 512, out maskSize);
                            maskSizes[maskTexture] = maskSize;
                        }
                        maskSize = maskSizes[maskTexture];
                    }
                    int[] triangles = ground.GetTriangles(s);
                    for (int t = 0; t + 2 < triangles.Length; t += 3)
                    {
                        int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                        var lawn = new Lawn
                        {
                            A = meshToRoot.MultiplyPoint3x4(vertices[a]),
                            B = meshToRoot.MultiplyPoint3x4(vertices[b]),
                            C = meshToRoot.MultiplyPoint3x4(vertices[c]),
                            Mask = mask,
                            MaskSize = maskSize,
                        };
                        Vector3 cross = Vector3.Cross(lawn.B - lawn.A, lawn.C - lawn.A);
                        if (cross.magnitude <= 2e-4f) continue;
                        // Lawns face up; the banks and skirts of the ground meshes do not.
                        if (Mathf.Abs(cross.normalized.y) < 0.85f) continue;
                        if (mask != null)
                        {
                            lawn.UvA = Scaled(uv2[a], maskScale);
                            lawn.UvB = Scaled(uv2[b], maskScale);
                            lawn.UvC = Scaled(uv2[c], maskScale);
                        }
                        lawns.Add(lawn);
                    }
                }
            }
        }

        private static Vector2 Scaled(Vector2 uv, Vector4 scale)
        {
            return new Vector2(uv.x * scale.x + scale.z, uv.y * scale.y + scale.w);
        }

        // Before Camera.main culls, after Parallax filled its outputs in its LateUpdate
        // (ScatterManager): its clumps over the lawns lifted and drawn for that camera, as
        // Parallax draws them (ScatterRenderer.Render).
        private void Draw(Camera camera)
        {
            if (!ready || camera != Camera.main || root == null || !Enabled()) return;
            Matrix4x4 rootToWorld = root.localToWorldMatrix;
            lift.SetMatrix(WorldToRootId, root.worldToLocalMatrix);
            lift.SetMatrix(RootToWorldId, rootToWorld);
            lift.SetVector(GridId, grid);
            lift.SetInts(SizeId, lawnMap.width, lawnMap.height);
            lift.SetTexture(liftKernel, LawnsId, lawnMap);
            lift.SetBuffer(liftKernel, CountsId, counts);
            var bounds = new Bounds(rootToWorld.MultiplyPoint3x4(new Vector3(grid.x + lawnMap.width * 0.5f, grid.w, grid.y + lawnMap.height * 0.5f)),
                                    new Vector3(lawnMap.width, 200f, lawnMap.height) * 1.5f);
            int layer = root.gameObject.layer;
            for (int k = 0; k < kinds.Count; k++)
            {
                Kind kind = kinds[k];
                lift.SetFloat(ReachId, kind.Reach);
                for (int lod = 0; lod < 3; lod++)
                {
                    var source = kind.Outputs[lod].GetValue(kind.Renderer) as ComputeBuffer;
                    Mesh mesh = kind.Meshes[lod];
                    Material material = kind.Materials[lod];
                    if (source == null || !source.IsValid() || mesh == null || material == null) continue;
                    if (kind.Lifted[lod] == null || kind.Lifted[lod].count != source.count)
                    {
                        kind.Lifted[lod]?.Release();
                        kind.Lifted[lod] = new ComputeBuffer(source.count, 64, ComputeBufferType.Append);
                        kind.Kept[lod]?.Release();
                        kind.Kept[lod] = new ComputeBuffer(source.count, 64, ComputeBufferType.Append);
                        if (kind.Arguments[lod] == null)
                            kind.Arguments[lod] = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
                        arguments[0] = mesh.GetIndexCount(0);
                        arguments[1] = 0;
                        arguments[2] = mesh.GetIndexStart(0);
                        arguments[3] = mesh.GetBaseVertex(0);
                        arguments[4] = 0;
                        kind.Arguments[lod].SetData(arguments);
                        kind.Properties[lod] = kind.Properties[lod] ?? new MaterialPropertyBlock();
                        kind.Properties[lod].SetBuffer(InstanceDataId, kind.Lifted[lod]);
                    }
                    int slot = k * 3 + lod;
                    kind.Lifted[lod].SetCounterValue(0);
                    kind.Kept[lod].SetCounterValue(0);
                    ComputeBuffer.CopyCount(source, counts, slot * 4);
                    lift.SetBuffer(liftKernel, SourceId, source);
                    lift.SetBuffer(liftKernel, LiftedId, kind.Lifted[lod]);
                    lift.SetBuffer(liftKernel, KeptId, kind.Kept[lod]);
                    lift.SetInt(CountIndexId, slot);
                    lift.SetInt(CapacityId, source.count);
                    if (capacities.Length < kinds.Count * 3) capacities = new int[kinds.Count * 3];
                    capacities[slot] = source.count;
                    lift.Dispatch(liftKernel, (source.count + 63) / 64, 1, 1);
                    ComputeBuffer.CopyCount(kind.Lifted[lod], kind.Arguments[lod], 4);
                    kind.KeptFrame[lod] = Time.frameCount;
                    Graphics.DrawMeshInstancedIndirect(mesh, 0, material, bounds, kind.Arguments[lod], 0, kind.Properties[lod],
                                                       ShadowCastingMode.On, true, layer, camera);
                }
            }
            // Every 600 frames: whether one of Parallax's own buffers is nearly full; its capacity
            // caps what is drawn (maxRenderableObjects; an append past it is dropped, and which
            // ones changes per frame, so the grass flickers). Logged only then.
            if (++drawnFrames % 600 == 0 && SystemInfo.supportsAsyncGPUReadback && counts != null)
            {
                int[] capacity = (int[])capacities.Clone();
                var names = kinds.ConvertAll(k => k.Name);
                AsyncGPUReadback.Request(counts, request =>
                {
                    if (request.hasError) return;
                    var data = request.GetData<uint>();
                    bool full = false;
                    for (int slot = 0; slot < data.Length && slot < capacity.Length; slot++)
                        full |= capacity[slot] > 0 && data[slot] >= capacity[slot] * 0.95f;
                    if (!full) return;
                    var text = new System.Text.StringBuilder(Log.Tag + " Space centre grass: Parallax's buffers filled (LOD0 / LOD1 / LOD2 of their capacity):");
                    for (int k = 0; k < names.Count; k++)
                    {
                        text.Append(' ').Append(names[k]);
                        for (int lod = 0; lod < 3; lod++)
                        {
                            int slot = k * 3 + lod;
                            if (slot >= data.Length || slot >= capacity.Length) continue;
                            text.Append(lod == 0 ? " " : " / ").Append(data[slot]).Append('/').Append(capacity[slot]);
                        }
                        text.Append(';');
                    }
                    Debug.Log(text.ToString());
                });
            }
            if (drawnFrames == 300 && SystemInfo.supportsAsyncGPUReadback)
                foreach (Kind kind in kinds)
                {
                    string name = kind.Name, cameraName = camera.name;
                    var lifted = new int[3];
                    int pending = 0;
                    for (int lod = 0; lod < 3; lod++)
                    {
                        if (kind.Arguments[lod] == null) continue;
                        int at = lod;
                        pending++;
                        AsyncGPUReadback.Request(kind.Arguments[lod], request =>
                        {
                            if (!request.hasError) lifted[at] = (int)request.GetData<uint>()[1];
                            if (--pending == 0)
                                Debug.Log(Log.Tag + " Space centre grass: " + name + " lifted for '" + cameraName + "': "
                                          + lifted[0] + " / " + lifted[1] + " / " + lifted[2] + " clumps by level of detail.");
                        });
                    }
                }
        }
    }
}
