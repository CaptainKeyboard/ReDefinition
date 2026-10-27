using System.Collections.Generic;
using UnityEngine;
using ReDefinition.Core;
using ReDefinition.Proxy;

namespace ReDefinition.Raytracing
{
    // What the rays see in KSP: the vessels, the terrain, the space centre's scenery and Parallax's scatter, each
    // a source that keeps its renderers and hands those in range over every frame.

    // Opaque, drawn, on a layer the scene camera draws, and not hung on a camera.
    internal static class RtFilter
    {
        // The scene camera's culling mask, set each frame (RtKsp).
        internal static int CullingMask = ~0;

        // What hangs on a camera is no part of the scene. Mods hang helper meshes
        // there for Unity to run a pass: EVE a quad with EVE/InvisibleShadowCaster
        // under FlightCamera (ScreenSpaceShadowsManager), and Deferred one with
        // Deferred/DummyForwardShader under the camera it works on
        // (ForwardRenderingCompatibility). Both have bounds far beyond the quad so
        // nothing culls them (Atmosphere.dll, Deferred.dll, decompiled). The
        // flight camera hangs under the vessel it follows, among the renderers
        // the vessel's parts are read from. Taken in, a quad would stand at the
        // eye and cast a shadow. Found anew every frame (RtKsp): cameras come and go,
        // and the flight camera moves from vessel to vessel.
        private static readonly HashSet<Renderer> onCameras = new HashSet<Renderer>();
        private static readonly List<Renderer> found = new List<Renderer>();
        private static Camera[] cameras = new Camera[8];

        // The renderers under every camera and under the flight camera, found anew; from RtKsp every frame.
        internal static void FindOnCameras(Transform flightCamera)
        {
            onCameras.Clear();
            if (cameras.Length < Camera.allCamerasCount) cameras = new Camera[Camera.allCamerasCount * 2];
            int count = Camera.GetAllCameras(cameras);
            for (int i = 0; i < count; i++)
                if (cameras[i] != null) Take(cameras[i].transform);
            if (flightCamera != null) Take(flightCamera);
        }

        // Every renderer under the transform, active or not.
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

        // Below the transparent queues and not Parallax's DiscardAll; null is not.
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

        // Enabled, active, on a layer of the culling mask, and not hung on a camera.
        internal static bool Drawn(Renderer renderer)
        {
            return renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy
                   && (CullingMask & (1 << renderer.gameObject.layer)) != 0 && !onCameras.Contains(renderer);
        }

        // The renderer's mesh at its own transform, where it is drawn.
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
                    Mesh = mesh, SubMesh = s, Material = material, LocalToWorld = localToWorld, Mask = 0xFF, Flags = flags, Source = renderer,
                });
            }
        }
    }

    // The loaded vessels' parts in flight, the ship in the editors. Skinned meshes
    // (landing gear, kerbals) are baked into meshes of their own when their bones moved,
    // which the scene hands over again.
    internal sealed class RtVesselSource : IRtSource
    {
        // A part's renderers as last found, and in which frame, looked for again every RefreshFrames.
        private sealed class PartRenderers
        {
            // The part's mesh filters, found in its active children.
            public MeshFilter[] Filters;
            // Each filter's renderer, found with it.
            public MeshRenderer[] Renderers;
            public SkinnedMeshRenderer[] Skinned;
            public int Frame;
        }

        // Skinned meshes baked a frame; the rest keep their last bake.
        private const int BakesPerFrame = 16;
        // A part's renderers looked for again after this many frames, at most this many
        // parts a frame, so that a ship's parts, all seen first in one frame, spread out.
        private const int RefreshFrames = 300;
        private const int RefreshesPerFrame = 8;

        // Each part's renderers by part.
        private readonly Dictionary<Part, PartRenderers> parts = new Dictionary<Part, PartRenderers>();
        // A skinned renderer's bake.
        private sealed class Bake
        {
            // The baked mesh, and the pose it was baked at (Pose).
            public Mesh Mesh;
            public Vector4 Pose;
            // The renderer's bones, read with its part's renderers: each read is a new array.
            public Transform[] Bones;
            public int BonesFrame;
        }

        // The bakes by skinned renderer; lists for the frame's parts and those gone; the scene; the frame count; the
        // bakes' turn (a quarter of the skinned meshes checked each frame); how near the eye a vessel is taken.
        private readonly Dictionary<SkinnedMeshRenderer, Bake> baked = new Dictionary<SkinnedMeshRenderer, Bake>();
        private readonly List<SkinnedMeshRenderer> goneSkinned = new List<SkinnedMeshRenderer>();
        private readonly List<Part> scratch = new List<Part>();
        private readonly List<Part> gone = new List<Part>();
        private readonly RtScene scene;
        private int frame;
        private int bakeCursor;
        private const float range = 5000f;

        // For the scene the parts are handed to, which takes their meshes.
        internal RtVesselSource(RtScene scene)
        {
            this.scene = scene;
        }

        // How many parts were looked at this frame, for the log.
        internal int Parts { get; private set; }

        // Every frame: the ship's parts in the editors, the loaded vessels' within range of the eye in flight, their
        // meshes, and their skinned meshes baked where their pose changed.
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
                    MeshFilter[] filters = part.GetComponentsInChildren<MeshFilter>(false);
                    renderers = new PartRenderers
                    {
                        Filters = filters,
                        Renderers = new MeshRenderer[filters.Length],
                        Skinned = part.GetComponentsInChildren<SkinnedMeshRenderer>(false),
                        Frame = frame,
                    };
                    for (int i = 0; i < filters.Length; i++)
                        if (filters[i] != null) renderers.Renderers[i] = filters[i].GetComponent<MeshRenderer>();
                    parts[part] = renderers;
                }
                for (int i = 0; i < renderers.Filters.Length; i++)
                {
                    MeshFilter filter = renderers.Filters[i];
                    if (filter == null) continue;
                    RtFilter.Add(renderers.Renderers[i], filter.sharedMesh, into);
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

        // Lets the baked meshes go and forgets the parts, as the raytracing stops.
        internal void Clear()
        {
            foreach (Bake bake in baked.Values)
                if (bake.Mesh != null) Object.Destroy(bake.Mesh);
            baked.Clear();
            parts.Clear();
        }
    }

    // The terrain's quads near the camera: every visible leaf of the scene body's quad
    // tree, from its root quads (PQS.quads, PQ.subNodes; decompiled). A quad's mesh is
    // built on the CPU and stays readable; it is built again when the quad is reused
    // for another place or a neighbour's subdivision swaps its triangles, which its
    // place and edge state tell. Parallax's finer copy near the camera is a renderer
    // under the quad, taken with it; the quad itself then draws with a transparent
    // material and is left out.
    internal sealed class RtTerrainSource : IRtSource
    {
        // What a quad was when last handed over, to tell when its mesh changed.
        private sealed class QuadState
        {
            // Where the quad stands on the planet and its edges' state when its filters were found.
            public Vector3d Place;
            public PQS.EdgeState Edge;
            // Parallax adds its finer copy as a child once the camera comes near and takes
            // it away again (TerrainShaderQuadData.CreateFakeQuad, OutOfRange): the filters
            // are looked for again when the children change.
            public int Children;
            public MeshFilter[] Filters;
            // Each filter's renderer, found with it.
            public MeshRenderer[] Renderers;
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

        // Each quad's state by quad; the quads to let go; the walk's stack; the scene; how near the eye a quad is taken.
        private readonly Dictionary<PQ, QuadState> quads = new Dictionary<PQ, QuadState>();
        private readonly List<PQ> forgotten = new List<PQ>();
        private readonly Stack<PQ> stack = new Stack<PQ>();
        private readonly RtScene scene;
        internal const float Range = 30000f;

        // For the scene the quads are handed to, which takes their meshes.
        internal RtTerrainSource(RtScene scene)
        {
            this.scene = scene;
        }

        // How many quads went into the scene this frame, for the log.
        internal int Quads { get; private set; }

        // Every frame: the visible leaves of the scene body's quad tree within Range of the eye, and the meshes under
        // them; a mesh handed over again where its quad was reused or Parallax refilled it.
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
                int children = quad.transform.childCount;
                if (!quads.TryGetValue(quad, out state) || state.Place != quad.positionPlanet || state.Edge != quad.edgeState
                    || state.Children != children)
                {
                    if (state != null)
                        foreach (MeshFilter filter in state.Filters)
                            if (filter != null) scene.Invalidate(filter.sharedMesh);
                    MeshFilter[] filters = quad.GetComponentsInChildren<MeshFilter>(false);
                    state = new QuadState
                    {
                        Place = quad.positionPlanet,
                        Edge = quad.edgeState,
                        Children = children,
                        Filters = filters,
                        Renderers = new MeshRenderer[filters.Length],
                        Vertices = new int[filters.Length],
                        Extent = new Bounds[filters.Length],
                        Checked = new int[filters.Length],
                    };
                    for (int i = 0; i < filters.Length; i++)
                        if (filters[i] != null) state.Renderers[i] = filters[i].GetComponent<MeshRenderer>();
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
                    RtFilter.Add(state.Renderers[i], mesh, into, RtNative.InstanceTerrain);
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

        // Forgets the quads as the raytracing stops; the scene lets their meshes go itself.
        internal void Clear()
        {
            quads.Clear();
        }
    }

    // Parallax's scatter (rocks, trees, grass) as the GPU placed it this frame
    // (RtKspMods.ParallaxScatter), and its grass lifted onto the space centre's lawns
    // (Scenery.KscGrass). Parallax culls it to the view and a short radius
    // around the camera, so what lies behind the camera further out casts no shadow.
    internal sealed class RtParallaxScatter : IRtGpuSource
    {
        // How many scatter buffers went into the scene this frame, for the log.
        internal int Buffers { get; private set; }

        // -redefinitionProbeNoParallaxScatterRays: Parallax's own scatter left out of the
        // rays, the space centre's lifted grass kept, to compare (the scene probe).
        private static readonly bool ParallaxLeftOut =
            CommandLine.Has("-redefinitionProbeNoParallaxScatterRays");

        // -redefinitionProbeNoKscGrassRays: the space centre's lifted grass left out of the rays too.
        private static readonly bool KscGrassLeftOut =
            CommandLine.Has("-redefinitionProbeNoKscGrassRays");

        // Every frame in flight and at the space centre: the scatter buffers handed over, counted for the log.
        public void CollectGpu(List<RtGpuItem> into)
        {
            Buffers = HighLogic.LoadedSceneIsFlight || HighLogic.LoadedScene == GameScenes.SPACECENTER
                ? (ParallaxLeftOut ? 0 : RtKspMods.ParallaxScatter(into)) + (KscGrassLeftOut ? 0 : Scenery.KscGrass.CollectGpu(into))
                : 0;
        }
    }

    // The buildings of the scene's cities (PQSCity, PQSCity2) -- the space centre, the launch sites --
    // near the camera; in the editors, the VAB's and SPH's halls.
    internal sealed class RtScenerySource : IRtSource
    {
        // A city's (or an editor hall's) mesh filters with their renderers, found when its
        // hierarchy last changed, and the sphere they lie in, relative to its root.
        private sealed class Site
        {
            // The city's or the hall's root transform, and whether its filters were looked for since it changed.
            public Transform Root;
            public bool Found;
            // Looked through again then: filters without their mesh yet.
            public float AgainAt = -1f;
            public int Retries;
            public readonly List<MeshFilter> Filters = new List<MeshFilter>();
            public readonly List<MeshRenderer> Renderers = new List<MeshRenderer>();
            // Per renderer, its mesh's middle relative to the root and its reach.
            public readonly List<Vector4> Spheres = new List<Vector4>();
            // Its skinned renderers -- the space centre's waving flags --, baked into meshes
            // of their own (Baked).
            public readonly List<SkinnedMeshRenderer> Skinned = new List<SkinnedMeshRenderer>();
            public Vector3 LocalCentre;
            public float Radius;
        }

        // The sites found; lists filled while a site is looked through.
        private readonly List<Site> sites = new List<Site>();
        private readonly List<MeshFilter> found = new List<MeshFilter>();
        private readonly List<SkinnedMeshRenderer> foundSkinned = new List<SkinnedMeshRenderer>();
        // The skinned renderers' meshes as last baked, and when: their cloth moves every
        // frame, and the rays follow it every frame: a stale bake shadows the cloth drawn
        // beside it with black streaks. A bake handed over again is refit in place.
        private readonly Dictionary<SkinnedMeshRenderer, Mesh> baked = new Dictionary<SkinnedMeshRenderer, Mesh>();
        private readonly Dictionary<SkinnedMeshRenderer, int> bakedAt = new Dictionary<SkinnedMeshRenderer, int>();
        private readonly List<SkinnedMeshRenderer> goneBakes = new List<SkinnedMeshRenderer>();
        private readonly List<MeshFilter> all = new List<MeshFilter>();
        private readonly RtScene scene;
        private GameScenes sitesOf = GameScenes.LOADING;
        private float sceneAt;
        private int laterLooks;
        private float lookedAt;
        private const float range = 8000f;

        // Subscribes once per run to the space centre's building events (changedAt).
        internal RtScenerySource(RtScene scene)
        {
            this.scene = scene;
            if (!listening)
            {
                listening = true;
                GameEvents.OnKSCFacilityUpgraded.Add((facility, level) => changedAt = Time.unscaledTime);
                GameEvents.OnKSCStructureCollapsed.Add(building => changedAt = Time.unscaledTime);
                GameEvents.OnKSCStructureRepaired.Add(building => changedAt = Time.unscaledTime);
            }
        }

        // The space centre's buildings changed -- upgraded, collapsed, rebuilt -- at this
        // time: every site is looked through again a second later, when the new models
        // stand. Otherwise a site is looked through once a scene: KSP switches its parts on
        // and off by distance, and the inactive ones are among its filters.
        private static bool listening;
        private static float changedAt = -1f;

        // How many renderers went into the scene this frame, for the log.
        internal int Renderers { get; private set; }
        // How often a site's filters were looked for again.
        internal static int Refreshes;

        // The scenery's mesh filters as last found.
        internal IEnumerable<MeshFilter> Filters
        {
            get
            {
                all.Clear();
                foreach (Site site in sites) all.AddRange(site.Filters);
                return all;
            }
        }

        // Every frame: the sites' renderers and baked skinned meshes within range of the eye, the sites looked for
        // again on a scene change and every ten seconds.
        public void Collect(List<RtItem> into)
        {
            // The sites found anew when the scene changes and every ten seconds, which is
            // cheap; a site's filters once (measured 2026-10-05: looking through all of them
            // every ten seconds held a frame for 6.8 ms).
            if (changedAt >= 0f && Time.unscaledTime - changedAt > 1f)
            {
                changedAt = -1f;
                foreach (Site site in sites) site.Found = false;
            }
            // And 5 and 20 seconds into the scene, for the models spawned after it began.
            float sinceScene = Time.unscaledTime - sceneAt;
            if (sitesOf == HighLogic.LoadedScene && (laterLooks == 0 && sinceScene > 5f || laterLooks == 1 && sinceScene > 20f))
            {
                laterLooks++;
                foreach (Site site in sites) site.Found = false;
            }
            if (sitesOf != HighLogic.LoadedScene || Time.unscaledTime - lookedAt > 10f)
            {
                lookedAt = Time.unscaledTime;
                if (sitesOf != HighLogic.LoadedScene)
                {
                    sites.Clear();
                    ReleaseBaked();
                    sceneAt = Time.unscaledTime;
                    laterLooks = 0;
                }
                else ForgetGoneBakes();
                sitesOf = HighLogic.LoadedScene;
                FindSites();
            }
            Vector3 eye = scene.Eye;
            Renderers = 0;
            foreach (Site site in sites)
            {
                if (site.Root == null) continue;
                if (!site.Found || (site.AgainAt >= 0f && Time.unscaledTime >= site.AgainAt)) Refresh(site);
                // A whole site out of range is passed over with one test, and each renderer
                // by its mesh's sphere, as math, without asking the engine for its bounds.
                Matrix4x4 toWorld = site.Root.localToWorldMatrix;
                float scale = site.Root.lossyScale.x;
                Vector3 centre = toWorld.MultiplyPoint3x4(site.LocalCentre);
                float reach = range + site.Radius * scale;
                if ((centre - eye).sqrMagnitude > reach * reach) continue;
                for (int i = 0; i < site.Filters.Count; i++)
                {
                    MeshFilter filter = site.Filters[i];
                    MeshRenderer renderer = site.Renderers[i];
                    Vector4 sphere = site.Spheres[i];
                    float near = range + sphere.w * scale;
                    if ((toWorld.MultiplyPoint3x4(sphere) - eye).sqrMagnitude > near * near) continue;
                    if (filter == null || renderer == null) continue;
                    int before = into.Count;
                    // The VAB's and SPH's halls stand on the scenery's layer (15), which the
                    // editor's camera does not draw itself (its mask 0, 1 and 16, measured
                    // 2026-10-05): taken as long as they are shown.
                    if (HighLogic.LoadedSceneIsEditor)
                    {
                        if (renderer.enabled && renderer.gameObject.activeInHierarchy && filter.sharedMesh != null)
                            RtFilter.Add(renderer, filter.sharedMesh, renderer.localToWorldMatrix, into, RtNative.InstanceScenery);
                    }
                    else RtFilter.Add(renderer, filter.sharedMesh, into, RtNative.InstanceScenery);
                    if (into.Count > before) Renderers++;
                }
                foreach (SkinnedMeshRenderer skinned in site.Skinned)
                {
                    if (!RtFilter.Drawn(skinned)) continue;
                    Transform t = skinned.transform;
                    if ((t.position - eye).sqrMagnitude > range * range) continue;
                    int before = into.Count;
                    Mesh mesh = Baked(skinned);
                    // BakeMesh leaves the mesh in the renderer's space, without its scale.
                    RtFilter.Add(skinned, mesh, Matrix4x4.TRS(t.position, t.rotation, Vector3.one), into, RtNative.InstanceScenery);
                    if (into.Count > before) Renderers++;
                }
            }
        }

        // The sites of the scene: the editor's hall in the editors, every PQSCity and PQSCity2 elsewhere; gone ones
        // dropped, new ones added to be looked through.
        private void FindSites()
        {
            var roots = new List<Transform>();
            if (HighLogic.LoadedSceneIsEditor)
            {
                foreach (string name in new[] { "VABscenery", "SPHscenery" })
                {
                    GameObject scenery = GameObject.Find(name);
                    if (scenery != null) roots.Add(scenery.transform);
                }
            }
            else
            {
                foreach (PQSCity city in Object.FindObjectsOfType<PQSCity>()) roots.Add(city.transform);
                foreach (PQSCity2 city in Object.FindObjectsOfType<PQSCity2>()) roots.Add(city.transform);
            }
            sites.RemoveAll(s => s.Root == null || !roots.Contains(s.Root));
            foreach (Transform root in roots)
                if (!sites.Exists(s => s.Root == root)) sites.Add(new Site { Root = root });
        }

        // The baked meshes let go: the scene's or the raytracing's end (Clear).
        private void ReleaseBaked()
        {
            foreach (Mesh mesh in baked.Values)
                if (mesh != null) Object.Destroy(mesh);
            baked.Clear();
            bakedAt.Clear();
        }

        // Every ten seconds, with the sites: the bakes of skinned renderers destroyed since
        // -- a flag taken down, a building rebuilt -- let go with their meshes.
        private void ForgetGoneBakes()
        {
            goneBakes.Clear();
            foreach (KeyValuePair<SkinnedMeshRenderer, Mesh> entry in baked)
                if (entry.Key == null) goneBakes.Add(entry.Key);
            foreach (SkinnedMeshRenderer skinned in goneBakes)
            {
                if (baked[skinned] != null) Object.Destroy(baked[skinned]);
                baked.Remove(skinned);
                bakedAt.Remove(skinned);
            }
        }

        // Everything let go as the raytracing stops (RtKsp.Detach): a new source is made
        // when it starts again.
        internal void Clear()
        {
            ReleaseBaked();
            sites.Clear();
            sitesOf = GameScenes.LOADING;
        }

        // The skinned renderer's mesh baked once per frame, into a mesh of its own made the first time.
        private Mesh Baked(SkinnedMeshRenderer skinned)
        {
            Mesh mesh;
            if (!baked.TryGetValue(skinned, out mesh) || mesh == null)
            {
                mesh = new Mesh { name = "ReDefinition baked " + skinned.name };
                baked[skinned] = mesh;
                bakedAt[skinned] = -1;
            }
            if (bakedAt[skinned] != Time.frameCount)
            {
                skinned.BakeMesh(mesh);
                scene.Invalidate(mesh);
                bakedAt[skinned] = Time.frameCount;
            }
            return mesh;
        }

        // A site's filters with their renderers, and the sphere round them.
        private void Refresh(Site site)
        {
            if (site.AgainAt >= 0f) site.Retries++;
            site.Found = true;
            site.AgainAt = -1f;
            site.Filters.Clear();
            site.Renderers.Clear();
            site.Spheres.Clear();
            site.Skinned.Clear();
            Refreshes++;
            // Inactive ones too: KSP switches a city's parts on and off by distance without
            // changing its hierarchy; whether one is drawn is asked each frame (RtFilter).
            site.Root.GetComponentsInChildren(true, found);
            Bounds around = new Bounds();
            bool any = false;
            Matrix4x4 toRoot = site.Root.worldToLocalMatrix;
            foreach (MeshFilter filter in found)
            {
                MeshRenderer renderer = filter != null ? filter.GetComponent<MeshRenderer>() : null;
                if (renderer == null) continue;
                // From the mesh, which an inactive renderer's bounds do not hold; one without
                // its mesh yet is looked for again in two seconds.
                if (filter.sharedMesh == null)
                {
                    // At most five times: a filter may stay without a mesh for good.
                    if (site.Retries < 5) site.AgainAt = Time.unscaledTime + 2f;
                    continue;
                }
                Bounds b = filter.sharedMesh.bounds;
                Matrix4x4 meshToRoot = toRoot * filter.transform.localToWorldMatrix;
                Vector3 local = meshToRoot.MultiplyPoint3x4(b.center);
                Vector3 scale = meshToRoot.lossyScale;
                float extent = b.extents.magnitude * Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
                Bounds piece = new Bounds(local, Vector3.one * (2f * extent));
                site.Filters.Add(filter);
                site.Renderers.Add(renderer);
                site.Spheres.Add(new Vector4(local.x, local.y, local.z, extent));
                if (any) around.Encapsulate(piece);
                else around = piece;
                any = true;
            }
            site.Root.GetComponentsInChildren(true, foundSkinned);
            foreach (SkinnedMeshRenderer skinned in foundSkinned)
                if (skinned != null && skinned.sharedMesh != null) site.Skinned.Add(skinned);
            site.LocalCentre = around.center;
            site.Radius = around.extents.magnitude;
        }
    }
}
