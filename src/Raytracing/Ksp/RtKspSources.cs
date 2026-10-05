using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // What the rays see in KSP (docs/development/raytracing.md, "What the rays see").
    // Each source keeps its renderers and hands those in range over every frame.

    // Opaque, drawn, on a layer the scene camera draws, and not hung on a camera.
    internal static class RtFilter
    {
        // The scene camera's culling mask, set each frame (RtKsp).
        internal static int CullingMask = ~0;

        // What hangs on a camera is no part of the scene: mods hang helper meshes
        // there for Unity to run a pass -- EVE a quad with EVE/InvisibleShadowCaster
        // under FlightCamera (ScreenSpaceShadowsManager), Deferred one with
        // Deferred/DummyForwardShader under the camera it works on
        // (ForwardRenderingCompatibility), both with bounds far beyond the quad so
        // that nothing culls them (Atmosphere.dll, Deferred.dll, decompiled). The
        // flight camera itself hangs under the vessel it follows, among the renderers
        // the vessel's parts are read from: taken in, such a quad would stand at the
        // eye and cast a shadow. Found anew every frame (RtKsp): cameras come and go,
        // and the flight camera moves from vessel to vessel.
        private static readonly HashSet<Renderer> onCameras = new HashSet<Renderer>();
        private static readonly List<Renderer> found = new List<Renderer>();
        private static Camera[] cameras = new Camera[8];

        internal static void FindOnCameras(Transform flightCamera)
        {
            onCameras.Clear();
            if (cameras.Length < Camera.allCamerasCount) cameras = new Camera[Camera.allCamerasCount * 2];
            int count = Camera.GetAllCameras(cameras);
            for (int i = 0; i < count; i++)
                if (cameras[i] != null) Take(cameras[i].transform);
            if (flightCamera != null) Take(flightCamera);
        }

        private static void Take(Transform root)
        {
            root.GetComponentsInChildren(true, found);
            foreach (Renderer renderer in found) onCameras.Add(renderer);
        }

        // Parallax's material for a quad whose finer copy draws in its place: it
        // discards everything (DiscardAll.shader), at the alpha-test queue.
        private const string DiscardAll = "Custom/DiscardAll";

        // Per shader, whether it is DiscardAll: a shader's name is a new string each
        // time it is read.
        private static readonly Dictionary<Shader, bool> discards = new Dictionary<Shader, bool>();
        // A renderer's materials, read into this list rather than a new array each time.
        private static readonly List<Material> sharedMaterials = new List<Material>();

        internal static bool Opaque(Material material)
        {
            if (material == null) return false;
            // Transparent and additive shaders sit at 2501 and up (Waterfall's plumes,
            // Firefly, particles, translucent parts, DistantObject's flares); cut-outs at
            // 2450 keep their holes only in the raster, which the rays accept.
            if (material.renderQueue >= 2501) return false;
            Shader shader = material.shader;
            if (shader == null) return true;
            bool discard;
            if (!discards.TryGetValue(shader, out discard)) discards[shader] = discard = shader.name == DiscardAll;
            return !discard;
        }

        internal static bool Drawn(Renderer renderer)
        {
            return renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy
                   && (CullingMask & (1 << renderer.gameObject.layer)) != 0 && !onCameras.Contains(renderer);
        }

        internal static void Add(Renderer renderer, Mesh mesh, List<RtItem> into, uint flags = 0)
        {
            if (mesh == null || !Drawn(renderer)) return;
            Add(renderer, mesh, renderer.localToWorldMatrix, into, flags);
        }

        // A submesh without a material of its own draws with the first, as Unity does.
        internal static void Add(Renderer renderer, Mesh mesh, Matrix4x4 localToWorld, List<RtItem> into, uint flags = 0)
        {
            List<Material> materials = sharedMaterials;
            renderer.GetSharedMaterials(materials);
            int subMeshes = mesh.subMeshCount;
            for (int s = 0; s < subMeshes; s++)
            {
                Material material = s < materials.Count ? materials[s] : materials.Count > 0 ? materials[0] : null;
                if (!Opaque(material)) continue;
                into.Add(new RtItem
                {
                    Mesh = mesh, SubMesh = s, Material = material, LocalToWorld = localToWorld, Mask = 0xFF, Flags = flags,
                });
            }
        }
    }

    // The loaded vessels' parts in flight, the ship in the editors. Skinned meshes
    // (landing gear, kerbals) are baked into meshes of their own when their bones moved,
    // which the scene hands over again.
    internal sealed class RtVesselSource : IRtSource
    {
        private sealed class PartRenderers
        {
            public MeshFilter[] Filters;
            public SkinnedMeshRenderer[] Skinned;
            public int Frame;
        }

        // Skinned meshes baked a frame; the rest keep their last bake.
        private const int BakesPerFrame = 16;
        // A part's renderers looked for again after this many frames, at most this many
        // parts a frame, so that a ship's parts, all seen first in one frame, spread out.
        private const int RefreshFrames = 300;
        private const int RefreshesPerFrame = 8;

        private readonly Dictionary<Part, PartRenderers> parts = new Dictionary<Part, PartRenderers>();
        private sealed class Bake
        {
            public Mesh Mesh;
            public Vector4 Pose;
            // The renderer's bones, read with its part's renderers: each read is a new array.
            public Transform[] Bones;
            public int BonesFrame;
        }

        private readonly Dictionary<SkinnedMeshRenderer, Bake> baked = new Dictionary<SkinnedMeshRenderer, Bake>();
        private readonly List<SkinnedMeshRenderer> goneSkinned = new List<SkinnedMeshRenderer>();
        private readonly List<Part> scratch = new List<Part>();
        private readonly List<Part> gone = new List<Part>();
        private readonly RtScene scene;
        private int frame;
        private int bakeCursor;
        private const float range = 5000f;

        internal RtVesselSource(RtScene scene)
        {
            this.scene = scene;
        }

        internal int Parts { get; private set; }

        public void Collect(List<RtItem> into)
        {
            frame++;
            scratch.Clear();
            if (HighLogic.LoadedSceneIsEditor)
            {
                if (EditorLogic.fetch != null && EditorLogic.fetch.ship != null) Take(EditorLogic.fetch.ship.parts);
            }
            else if (HighLogic.LoadedSceneIsFlight && FlightGlobals.VesselsLoaded != null)
            {
                Vector3 camera = scene.Eye;
                foreach (Vessel vessel in FlightGlobals.VesselsLoaded)
                    if (vessel != null && (vessel.transform.position - camera).sqrMagnitude < range * range)
                        Take(vessel.parts);
            }
            Parts = scratch.Count;

            int bakes = 0;
            int index = 0;
            int refreshes = 0;
            foreach (Part part in scratch)
            {
                if (part == null) continue;
                PartRenderers renderers;
                if (!parts.TryGetValue(part, out renderers)
                    || (frame - renderers.Frame > RefreshFrames && refreshes++ < RefreshesPerFrame))
                {
                    // Looked at again now and then: parts change their models (B9PartSwitch).
                    renderers = new PartRenderers
                    {
                        Filters = part.GetComponentsInChildren<MeshFilter>(false),
                        Skinned = part.GetComponentsInChildren<SkinnedMeshRenderer>(false),
                        Frame = frame,
                    };
                    parts[part] = renderers;
                }
                foreach (MeshFilter filter in renderers.Filters)
                {
                    if (filter == null) continue;
                    RtFilter.Add(filter.GetComponent<MeshRenderer>(), filter.sharedMesh, into);
                }
                foreach (SkinnedMeshRenderer skinned in renderers.Skinned)
                {
                    if (!RtFilter.Drawn(skinned)) continue;
                    Bake bake;
                    bool fresh = !baked.TryGetValue(skinned, out bake);
                    if (fresh)
                    {
                        bake = new Bake { Mesh = new Mesh { name = "ReDefinition baked " + skinned.name } };
                        baked[skinned] = bake;
                    }
                    if (bake.Bones == null || bake.BonesFrame != renderers.Frame)
                    {
                        bake.Bones = skinned.bones;
                        bake.BonesFrame = renderers.Frame;
                    }
                    // Baked again when the pose changed, a few a frame; the bones are
                    // compared relative to the renderer, which the flight moves as a whole.
                    if (fresh || ((index + bakeCursor) % 4 == 0 && bakes < BakesPerFrame))
                    {
                        Vector4 pose = Pose(skinned, bake.Bones);
                        if (fresh || pose != bake.Pose)
                        {
                            skinned.BakeMesh(bake.Mesh);
                            scene.Invalidate(bake.Mesh);
                            bake.Pose = pose;
                            bakes++;
                        }
                    }
                    index++;
                    // BakeMesh leaves the mesh in the renderer's space, without its scale.
                    Transform t = skinned.transform;
                    RtFilter.Add(skinned, bake.Mesh, Matrix4x4.TRS(t.position, t.rotation, Vector3.one), into);
                }
            }
            bakeCursor++;

            // Parts and skinned renderers gone let go, their baked meshes with them.
            if (frame % 120 == 0)
            {
                gone.Clear();
                foreach (Part part in parts.Keys)
                    if (part == null) gone.Add(part);
                foreach (Part part in gone) parts.Remove(part);
                goneSkinned.Clear();
                foreach (SkinnedMeshRenderer skinned in baked.Keys)
                    if (skinned == null) goneSkinned.Add(skinned);
                foreach (SkinnedMeshRenderer skinned in goneSkinned)
                {
                    if (baked[skinned].Mesh != null) Object.Destroy(baked[skinned].Mesh);
                    baked.Remove(skinned);
                }
            }
        }

        // One by one: Mono's AddRange copies through a new array, every frame here.
        private void Take(List<Part> from)
        {
            for (int i = 0; i < from.Count; i++) scratch.Add(from[i]);
        }

        // A sum over the bones' places and turns relative to the renderer: it changes
        // when any bone moves.
        private static Vector4 Pose(SkinnedMeshRenderer skinned, Transform[] bones)
        {
            Transform t = skinned.transform;
            Quaternion inverse = Quaternion.Inverse(t.rotation);
            Vector4 sum = Vector4.zero;
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null) continue;
                Vector3 place = t.InverseTransformPoint(bones[i].position);
                Quaternion turn = inverse * bones[i].rotation;
                float weight = i + 1;
                sum += new Vector4(place.x + turn.x, place.y + turn.y, place.z + turn.z, turn.w) * weight;
            }
            return sum;
        }

        internal void Clear()
        {
            foreach (Bake bake in baked.Values)
                if (bake.Mesh != null) Object.Destroy(bake.Mesh);
            baked.Clear();
            parts.Clear();
        }
    }

    // The terrain's quads near the camera: every visible leaf of the main body's quad
    // tree, from its root quads (PQS.quads, PQ.subNodes; decompiled). A quad's mesh is
    // built on the CPU and stays readable; it is built again when the quad is reused
    // for another place or a neighbour's subdivision swaps its triangles, which its
    // place and edge state tell. Parallax's finer copy near the camera is a renderer
    // under the quad, taken with it; the quad itself then draws with a transparent
    // material and is left out.
    internal sealed class RtTerrainSource : IRtSource
    {
        private sealed class QuadState
        {
            public Vector3d Place;
            public PQS.EdgeState Edge;
            public MeshFilter[] Filters;
            // Per filter, the mesh's shape when last handed over: Parallax refills its
            // finer copy in place as the camera moves (JobifiedSubdivision), with no
            // event to tell.
            public int[] Vertices;
            public Bounds[] Extent;
            public int[] Checked;
            public int Seen;
        }

        // A refilled mesh handed over again at most this often, in frames.
        private const int RefillFrames = 10;
        // A quad not seen for this many frames is forgotten: by then the scene has let
        // its meshes go too (RtScene.UnseenFrames), and they are handed over anew.
        private const int ForgetFrames = 600;
        private int frame;

        private readonly Dictionary<PQ, QuadState> quads = new Dictionary<PQ, QuadState>();
        private readonly List<PQ> forgotten = new List<PQ>();
        private readonly Stack<PQ> stack = new Stack<PQ>();
        private readonly RtScene scene;
        internal const float Range = 30000f;

        internal RtTerrainSource(RtScene scene)
        {
            this.scene = scene;
        }

        internal int Quads { get; private set; }

        public void Collect(List<RtItem> into)
        {
            Quads = 0;
            frame++;
            CelestialBody body = RtKspMods.SceneBody();
            PQS pqs = body != null ? body.pqsController : null;
            if (pqs == null || pqs.quads == null || !pqs.isActiveAndEnabled) return;
            Vector3 eye = scene.Eye;
            float range2 = Range * Range;

            stack.Clear();
            foreach (PQ root in pqs.quads)
                if (root != null) stack.Push(root);
            while (stack.Count > 0)
            {
                PQ quad = stack.Pop();
                if (quad.isSubdivided && quad.subNodes != null)
                {
                    foreach (PQ sub in quad.subNodes)
                        if (sub != null) stack.Push(sub);
                    continue;
                }
                if (!quad.isVisible || !quad.isBuilt || quad.isForcedInvisible || quad.meshRenderer == null) continue;
                if ((quad.meshRenderer.bounds.ClosestPoint(eye) - eye).sqrMagnitude > range2) continue;

                QuadState state;
                if (!quads.TryGetValue(quad, out state) || state.Place != quad.positionPlanet || state.Edge != quad.edgeState)
                {
                    if (state != null)
                        foreach (MeshFilter filter in state.Filters)
                            if (filter != null) scene.Invalidate(filter.sharedMesh);
                    MeshFilter[] filters = quad.GetComponentsInChildren<MeshFilter>(false);
                    state = new QuadState
                    {
                        Place = quad.positionPlanet,
                        Edge = quad.edgeState,
                        Filters = filters,
                        Vertices = new int[filters.Length],
                        Extent = new Bounds[filters.Length],
                        Checked = new int[filters.Length],
                    };
                    for (int i = 0; i < filters.Length; i++)
                        if (filters[i] != null && filters[i].sharedMesh != null)
                        {
                            state.Vertices[i] = filters[i].sharedMesh.vertexCount;
                            state.Extent[i] = filters[i].sharedMesh.bounds;
                            state.Checked[i] = frame;
                        }
                    quads[quad] = state;
                    if (quad.mesh != null) scene.Invalidate(quad.mesh);
                }
                state.Seen = frame;
                for (int i = 0; i < state.Filters.Length; i++)
                {
                    MeshFilter filter = state.Filters[i];
                    if (filter == null) continue;
                    Mesh mesh = filter.sharedMesh;
                    if (mesh != null && frame - state.Checked[i] >= RefillFrames)
                    {
                        state.Checked[i] = frame;
                        if (mesh.vertexCount != state.Vertices[i] || mesh.bounds != state.Extent[i])
                        {
                            state.Vertices[i] = mesh.vertexCount;
                            state.Extent[i] = mesh.bounds;
                            scene.Invalidate(mesh);
                        }
                    }
                    RtFilter.Add(filter.GetComponent<MeshRenderer>(), mesh, into, RtNative.InstanceTerrain);
                }
                Quads++;
            }

            // Quads destroyed, or not seen for long, let go.
            if (frame % 120 == 0)
            {
                forgotten.Clear();
                foreach (KeyValuePair<PQ, QuadState> pair in quads)
                    if (pair.Key == null || frame - pair.Value.Seen > ForgetFrames) forgotten.Add(pair.Key);
                foreach (PQ quad in forgotten) quads.Remove(quad);
            }
        }

        internal void Clear()
        {
            quads.Clear();
        }
    }

    // Parallax's scatter -- rocks, trees, grass -- as the GPU placed it this frame
    // (RtKspMods.ParallaxScatter): culled by Parallax to the view and a short radius
    // around the camera, so what lies behind the camera further out casts no shadow.
    internal sealed class RtParallaxScatter : IRtGpuSource
    {
        internal int Buffers { get; private set; }

        public void CollectGpu(List<RtGpuItem> into)
        {
            Buffers = HighLogic.LoadedSceneIsFlight || HighLogic.LoadedScene == GameScenes.SPACECENTER
                ? RtKspMods.ParallaxScatter(into)
                : 0;
        }
    }

    // The buildings of the home body's cities -- the space centre, the launch sites --
    // near the camera; in the editors, their own scenery.
    internal sealed class RtScenerySource : IRtSource
    {
        private readonly List<MeshFilter> filters = new List<MeshFilter>();
        private readonly RtScene scene;
        private GameScenes found = GameScenes.LOADING;
        private float lookedAt;
        private const float range = 8000f;

        internal RtScenerySource(RtScene scene)
        {
            this.scene = scene;
        }

        internal int Renderers { get; private set; }

        public void Collect(List<RtItem> into)
        {
            // The scenery found anew when the scene changes, and every ten seconds, as
            // the space centre's buildings upgrade.
            if (found != HighLogic.LoadedScene || Time.unscaledTime - lookedAt > 10f)
            {
                found = HighLogic.LoadedScene;
                lookedAt = Time.unscaledTime;
                filters.Clear();
                if (HighLogic.LoadedSceneIsEditor)
                {
                    foreach (string root in new[] { "VABscenery", "SPHscenery" })
                    {
                        GameObject scenery = GameObject.Find(root);
                        if (scenery != null) filters.AddRange(scenery.GetComponentsInChildren<MeshFilter>(false));
                    }
                }
                else
                {
                    foreach (PQSCity city in Object.FindObjectsOfType<PQSCity>())
                        filters.AddRange(city.GetComponentsInChildren<MeshFilter>(false));
                    foreach (PQSCity2 city in Object.FindObjectsOfType<PQSCity2>())
                        filters.AddRange(city.GetComponentsInChildren<MeshFilter>(false));
                }
            }
            Vector3 eye = scene.Eye;
            float range2 = range * range;
            Renderers = 0;
            foreach (MeshFilter filter in filters)
            {
                if (filter == null) continue;
                MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                if (renderer == null || (renderer.bounds.ClosestPoint(eye) - eye).sqrMagnitude > range2) continue;
                // The VAB's and SPH's halls stand on the scenery's layer (15), which the
                // editor's camera does not draw itself (its mask 0, 1 and 16, measured
                // 2026-10-05): taken as long as they are shown.
                if (HighLogic.LoadedSceneIsEditor)
                {
                    if (renderer.enabled && renderer.gameObject.activeInHierarchy && filter.sharedMesh != null)
                        RtFilter.Add(renderer, filter.sharedMesh, renderer.localToWorldMatrix, into, RtNative.InstanceScenery);
                }
                else RtFilter.Add(renderer, filter.sharedMesh, into, RtNative.InstanceScenery);
                Renderers++;
            }
        }
    }
}
