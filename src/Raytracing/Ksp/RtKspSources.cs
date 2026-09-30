using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // What the rays see in KSP (docs/development/raytracing.md, "What the rays see").
    // Each source keeps its renderers and hands those in range over every frame.

    // Opaque, drawn, on a layer the scene camera draws.
    internal static class RtFilter
    {
        internal static bool Opaque(Material material)
        {
            if (material == null) return false;
            // Transparent and additive shaders sit at 2501 and up (Waterfall's plumes,
            // Firefly, particles, translucent parts); cut-outs at 2450 keep their holes
            // only in the raster, which the rays accept.
            return material.renderQueue < 2501;
        }

        internal static void Add(Renderer renderer, Mesh mesh, List<RtItem> into)
        {
            if (renderer == null || mesh == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) return;
            Add(renderer.sharedMaterials, mesh, renderer.localToWorldMatrix, into);
        }

        // A submesh without a material of its own draws with the first, as Unity does.
        internal static void Add(Material[] materials, Mesh mesh, Matrix4x4 localToWorld, List<RtItem> into)
        {
            int subMeshes = mesh.subMeshCount;
            for (int s = 0; s < subMeshes; s++)
            {
                Material material = s < materials.Length ? materials[s] : materials.Length > 0 ? materials[0] : null;
                if (!Opaque(material)) continue;
                into.Add(new RtItem { Mesh = mesh, SubMesh = s, Material = material, LocalToWorld = localToWorld, Mask = 0xFF });
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

        private readonly Dictionary<Part, PartRenderers> parts = new Dictionary<Part, PartRenderers>();
        private sealed class Bake
        {
            public Mesh Mesh;
            public Vector4 Pose;
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
                if (EditorLogic.fetch != null && EditorLogic.fetch.ship != null) scratch.AddRange(EditorLogic.fetch.ship.parts);
            }
            else if (HighLogic.LoadedSceneIsFlight && FlightGlobals.VesselsLoaded != null)
            {
                Vector3 camera = scene.Eye;
                foreach (Vessel vessel in FlightGlobals.VesselsLoaded)
                    if (vessel != null && (vessel.transform.position - camera).sqrMagnitude < range * range)
                        scratch.AddRange(vessel.parts);
            }
            Parts = scratch.Count;

            int bakes = 0;
            int index = 0;
            foreach (Part part in scratch)
            {
                if (part == null) continue;
                PartRenderers renderers;
                if (!parts.TryGetValue(part, out renderers) || frame - renderers.Frame > 300)
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
                    if (skinned == null || !skinned.enabled) continue;
                    Bake bake;
                    bool fresh = !baked.TryGetValue(skinned, out bake);
                    if (fresh)
                    {
                        bake = new Bake { Mesh = new Mesh { name = "ReDefinition baked " + skinned.name } };
                        baked[skinned] = bake;
                    }
                    // Baked again when the pose changed, a few a frame; the bones are
                    // compared relative to the renderer, which the flight moves as a whole.
                    if (fresh || ((index + bakeCursor) % 4 == 0 && bakes < BakesPerFrame))
                    {
                        Vector4 pose = Pose(skinned);
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
                    RtFilter.Add(skinned.sharedMaterials, bake.Mesh, Matrix4x4.TRS(t.position, t.rotation, Vector3.one), into);
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

        // A sum over the bones' places and turns relative to the renderer: it changes
        // when any bone moves.
        private static Vector4 Pose(SkinnedMeshRenderer skinned)
        {
            Transform t = skinned.transform;
            Quaternion inverse = Quaternion.Inverse(t.rotation);
            Vector4 sum = Vector4.zero;
            Transform[] bones = skinned.bones;
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
        }

        private readonly Dictionary<PQ, QuadState> quads = new Dictionary<PQ, QuadState>();
        private readonly Stack<PQ> stack = new Stack<PQ>();
        private readonly RtScene scene;
        private const float range = 30000f;

        internal RtTerrainSource(RtScene scene)
        {
            this.scene = scene;
        }

        internal int Quads { get; private set; }

        public void Collect(List<RtItem> into)
        {
            Quads = 0;
            CelestialBody body = HighLogic.LoadedSceneIsFlight ? FlightGlobals.currentMainBody
                                 : HighLogic.LoadedScene == GameScenes.SPACECENTER ? FlightGlobals.GetHomeBody() : null;
            PQS pqs = body != null ? body.pqsController : null;
            if (pqs == null || pqs.quads == null || !pqs.isActiveAndEnabled) return;
            Vector3 eye = scene.Eye;
            float range2 = range * range;

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
                    state = new QuadState
                    {
                        Place = quad.positionPlanet,
                        Edge = quad.edgeState,
                        Filters = quad.GetComponentsInChildren<MeshFilter>(false),
                    };
                    quads[quad] = state;
                    if (quad.mesh != null) scene.Invalidate(quad.mesh);
                }
                foreach (MeshFilter filter in state.Filters)
                {
                    if (filter == null) continue;
                    RtFilter.Add(filter.GetComponent<MeshRenderer>(), filter.sharedMesh, into);
                }
                Quads++;
            }
        }

        internal void Clear()
        {
            quads.Clear();
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
                RtFilter.Add(renderer, filter.sharedMesh, into);
                Renderers++;
            }
        }
    }
}
