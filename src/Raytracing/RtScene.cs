using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Raytracing
{
    // One thing the rays see this frame: a submesh of a mesh with its material and
    // its transform. Sources fill these (IRtSource); the scene hands them over.
    internal struct RtItem
    {
        // The mesh, its submesh and the material it is drawn with, its transform to the world, and the ray mask
        // (bits 0-7; 0 for all rays).
        public Mesh Mesh;
        public int SubMesh;
        public Material Material;
        public Matrix4x4 LocalToWorld;
        public uint Mask;
        // RtNative.InstanceTerrain for the ground, which Parallax displaces off its mesh.
        public uint Flags;
    }

    // Where the rays' scene comes from: KSP's parts, terrain and buildings in the
    // game (src/Raytracing/Ksp), all renderers of a scene in the test player.
    internal interface IRtSource
    {
        // Adds this frame's items to `into`.
        void Collect(List<RtItem> into);
    }

    // Instances only the GPU knows: an append buffer of transforms (float4x4, the
    // mesh's to the world, rows 0-2 read), filled for this frame before the camera
    // draws, as Parallax fills its scatter's (ScatterRenderer.outputLOD0-2).
    internal struct RtGpuItem
    {
        // The append buffer of transforms, the mesh, its submesh and the material of every instance in it, and the
        // ray mask (0 for all rays).
        public ComputeBuffer Transforms;
        public Mesh Mesh;
        public int SubMesh;
        public Material Material;
        public uint Mask;
    }

    // Where the GPU's instances come from: Parallax's scatter in the game (RtKspMods).
    internal interface IRtGpuSource
    {
        // Adds this frame's buffers to `into`.
        void CollectGpu(List<RtGpuItem> into);
    }

    // The meshes, materials and instances handed to the proxy. A mesh or material is
    // handed over once, when first seen, and released when not seen for a while or
    // destroyed; the instances every frame, relative to the camera. When the proxy's
    // device changes (its generation), everything is handed over again.
    internal sealed class RtScene
    {
        // Not seen for this many frames: released.
        private const int UnseenFrames = 300;
        // New material textures drawn a frame, and at most this many on their way back.
        private const int TexturesPerFrame = 4;
        private const int TexturesInFlight = 8;

        // A submesh handed over.
        private sealed class MeshEntry
        {
            // Its id, the frame it was last seen in, the mesh and submesh, and whether the mesh was built again since,
            // to be handed over again.
            public int Id;
            public int LastSeen;
            public Mesh Mesh;
            public int SubMesh;
            public bool Dirty;
        }

        // A material handed over.
        private sealed class MaterialEntry
        {
            // The proxy's id of the material, and the frame it was last seen in.
            public int Id;
            public int LastSeen;
            // The space centre's (RtMaterialIntent).
            public bool Scenery;
            // The texture read for it, or waiting to be: a mod may set it later --
            // Parallax's scatter gets its textures a frame or more after its materials
            // (ScatterRenderer.SetMaterialTexturesAsync) -- or change it.
            public Texture Texture;
            // Waiting to be read, or being read back.
            public bool Queued;
            public int Layer = -1;
        }

        // A material's small texture on its way back from the GPU (AsyncGPUReadback),
        // with the render texture it was drawn into; kept and used again.
        private sealed class TextureRead
        {
            // The texture drawn into, the material and its entry, the layer of the proxy's texture array it goes to,
            // and whether it is world-mapped (its average taken).
            public RenderTexture Target;
            public Material Material;
            public MaterialEntry Entry;
            public int Layer;
            public bool WorldMapped;
            // The scene's epoch when asked: a new device or a clear since, and the
            // material's id and layer mean nothing any more.
            public int Epoch;
            public Action<AsyncGPUReadbackRequest> Done;
        }

        // The submeshes handed over, by Key.
        private readonly Dictionary<long, MeshEntry> meshes = new Dictionary<long, MeshEntry>();
        // Submeshes that can never be handed over -- not readable, or not triangles -- not
        // tried again each frame.
        private readonly HashSet<long> unusable = new HashSet<long>();
        // Submeshes the last hand-over failed for -- empty yet, or refused by the proxy --,
        // with the frame it failed in: tried again after FailedRetryFrames, not every frame,
        // each try reading the whole mesh into new arrays and taking the frame's upload budget.
        private readonly Dictionary<long, int> failed = new Dictionary<long, int>();
        private const int FailedRetryFrames = 30;
        private readonly Dictionary<Mesh, List<long>> keysOf = new Dictionary<Mesh, List<long>>();
        private readonly Dictionary<Material, MaterialEntry> materials = new Dictionary<Material, MaterialEntry>();
        private readonly List<RtItem> items = new List<RtItem>();
        private readonly List<IRtSource> sources = new List<IRtSource>();
        private readonly List<IRtGpuSource> gpuSources = new List<IRtGpuSource>();
        private readonly List<RtGpuItem> gpuCollected = new List<RtGpuItem>();
        private RtNative.GpuGroup[] gpuGroups = new RtNative.GpuGroup[16];
        // How many places of each GPU buffer were taken lately, as read back (RtCamera):
        // a buffer's capacity is what its mod may ever need, Parallax's up to hundreds
        // of thousands, and every place handed over is an instance to build.
        private readonly Dictionary<ComputeBuffer, int> gpuTaken = new Dictionary<ComputeBuffer, int>();
        private readonly List<ComputeBuffer> gpuGone = new List<ComputeBuffer>();
        // Places for a buffer not read back yet.
        private const int GpuFirstPlaces = 1024;
        private RtNative.Instance[] instances = new RtNative.Instance[1024];
        private readonly Queue<Material> texturesToRead = new Queue<Material>();
        // Layers of the proxy's texture array that released materials left.
        private readonly Stack<int> freeLayers = new Stack<int>();
        // A material's texture looked at again: every frame while it has none, else
        // once in this many frames.
        private const int TextureCheckFrames = 120;
        // A material without its texture yet: looked at again this often, frames.
        private const int MissingTextureCheckFrames = 10;
        // Meshes and materials not seen for UnseenFrames: looked for this often, frames.
        private const int ReleaseCheckFrames = 30;
        private static readonly int RtCutoff = Shader.PropertyToID("_ReDefinitionRtCutoff");
        // The capture shader's pass that makes a material's small texture.
        private const int MaterialTexturePass = 6;
        // The capture shader's material (RtCamera); without it a plain copy.
        internal Material TextureReader;
        private int nextMeshId = 1;
        private int nextMaterialId = 1;
        private int nextTextureLayer;
        private int generation = -1;
        private int frame;
        private int epoch;
        private readonly Stack<TextureRead> idleReads = new Stack<TextureRead>();
        private int lostTargets;
        private int readsInFlight;
        // Where AsyncGPUReadback is not supported: read at once into this texture.
        private Texture2D textureRead;
        private byte[] texturePixels;
        private static float[] srgbToLinear;

        // How many instances this frame handed over, for the diagnostics.
        internal int InstanceCount { get; private set; }
        // This frame's GPU instances: the items the camera gathers, each run's place, and
        // their places together.
        internal readonly List<RtGpuItem> GpuItems = new List<RtGpuItem>();
        internal readonly List<int> GpuFirst = new List<int>();
        // Each item's places this frame: what it took lately and a quarter more.
        internal readonly List<int> GpuPlaces = new List<int>();
        // All GPU items' places this frame together: the instances the camera gathers.
        internal int GpuTotal { get; private set; }
        // Where the camera is this frame, for the sources' range.
        internal Vector3 Eye { get; private set; }
        // How many submeshes are handed over, for the diagnostics.
        internal int MeshCount { get { return meshes.Count; } }
        // How many materials are handed over, for the diagnostics.
        internal int MaterialCount { get { return materials.Count; } }
        // How many items this frame left out because their mesh could not be handed over (yet).
        internal int Skipped { get; private set; }
        // Why the last mesh was left out, for the diagnostics.
        internal string LastSkip { get; private set; }

        // In gamma space, as KSP renders, Unity's colours are gamma-space values; the
        // light transport is linear (RtCommon.hlsli), so the materials' colours are taken
        // to linear before they go over. Their small textures need nothing: the proxy's
        // array is sRGB, and its decoding of the bytes Unity renders -- gamma-space
        // values there, sRGB codes in linear space -- gives the linear value either way.
        // Read where it is used: RtCamera makes the scene as a field, in its constructor,
        // where Unity does not allow QualitySettings to be read (a development player
        // throws there, and the raytracing never starts).
        internal bool GammaSpace
        {
            get { return QualitySettings.activeColorSpace == ColorSpace.Gamma; }
        }

        // Adds a source of items, once.
        internal void AddSource(IRtSource source)
        {
            if (!sources.Contains(source)) sources.Add(source);
        }

        // Adds a source of GPU instances, once.
        internal void AddGpuSource(IRtGpuSource source)
        {
            if (!gpuSources.Contains(source)) gpuSources.Add(source);
        }

        // Everything this frame, relative to the camera at cameraPosition, for the frame
        // of that index (RtFramePacket's frameIndex).
        internal void Update(RtPosition cameraPosition, int serviceGeneration, uint frameIndex)
        {
            frame++;
            uploadTicks = 0;
            if (serviceGeneration != generation)
            {
                // A new device: nothing handed over before is there any more.
                generation = serviceGeneration;
                epoch++;
                meshes.Clear();
                keysOf.Clear();
                failed.Clear();
                materials.Clear();
                texturesToRead.Clear();
                freeLayers.Clear();
                nextTextureLayer = 0;
            }

            Eye = new Vector3((float)cameraPosition.x, (float)cameraPosition.y, (float)cameraPosition.z);
            items.Clear();
            foreach (IRtSource source in sources)
                source.Collect(items);

            if (instances.Length < items.Count) Array.Resize(ref instances, Math.Max(items.Count, instances.Length * 2));
            int count = 0;
            Skipped = 0;
            foreach (RtItem item in items)
            {
                int mesh = MeshId(item.Mesh, item.SubMesh, item.Material);
                if (mesh == 0)
                {
                    Skipped++;
                    continue;
                }
                int material = MaterialId(item.Material, (item.Flags & RtNative.InstanceScenery) != 0);
                Matrix4x4 m = item.LocalToWorld;
                RtNative.Instance instance;
                instance.M00 = m.m00; instance.M01 = m.m01; instance.M02 = m.m02;
                instance.M03 = (float)(m.m03 - cameraPosition.x);
                instance.M10 = m.m10; instance.M11 = m.m11; instance.M12 = m.m12;
                instance.M13 = (float)(m.m13 - cameraPosition.y);
                instance.M20 = m.m20; instance.M21 = m.m21; instance.M22 = m.m22;
                instance.M23 = (float)(m.m23 - cameraPosition.z);
                instance.Mesh = mesh;
                instance.Material = material;
                instance.Mask = item.Mask != 0 ? item.Mask : 0xFFu;
                instance.Flags = item.Flags;
                instances[count++] = instance;
            }
            InstanceCount = count;
            RtNative.KspRtSetInstances(instances, count, frameIndex);
            UpdateGpu(frameIndex);

            ReadTextures();
            if (frame % ReleaseCheckFrames == 0) ReleaseUnseen();
        }

        // The GPU's runs, each where it starts among them, and their groups for the proxy.
        private void UpdateGpu(uint frameIndex)
        {
            gpuCollected.Clear();
            foreach (IRtGpuSource source in gpuSources)
                source.CollectGpu(gpuCollected);
            GpuItems.Clear();
            GpuFirst.Clear();
            GpuPlaces.Clear();
            int total = 0;
            int groups = 0;
            foreach (RtGpuItem item in gpuCollected)
            {
                if (item.Transforms == null || !item.Transforms.IsValid() || item.Transforms.count == 0) continue;
                int mesh = MeshId(item.Mesh, item.SubMesh, item.Material);
                if (mesh == 0)
                {
                    Skipped++;
                    continue;
                }
                if (groups == gpuGroups.Length) Array.Resize(ref gpuGroups, groups * 2);
                gpuGroups[groups++] = new RtNative.GpuGroup
                {
                    Mesh = mesh,
                    Material = MaterialId(item.Material),
                    First = (uint)total,
                    Count = (uint)Places(item.Transforms),
                    Mask = item.Mask != 0 ? item.Mask : 0xFFu,
                };
                GpuItems.Add(item);
                GpuFirst.Add(total);
                GpuPlaces.Add((int)gpuGroups[groups - 1].Count);
                total += (int)gpuGroups[groups - 1].Count;
            }
            GpuTotal = total;
            if (frame % 300 == 0)
            {
                gpuGone.Clear();
                foreach (ComputeBuffer buffer in gpuTaken.Keys)
                    if (buffer == null || !buffer.IsValid()) gpuGone.Add(buffer);
                foreach (ComputeBuffer buffer in gpuGone) gpuTaken.Remove(buffer);
            }
            RtNative.KspRtSetGpuGroups(gpuGroups, groups, frameIndex);
        }

        // The places handed over for a buffer: what it took lately, a quarter and 64 more, rounded up to 64; GpuFirstPlaces
        // before a read-back; never more than its capacity.
        private int Places(ComputeBuffer buffer)
        {
            int taken;
            int places = gpuTaken.TryGetValue(buffer, out taken) ? (taken + taken / 4 + 64 + 63) / 64 * 64 : GpuFirstPlaces;
            return Math.Max(1, Math.Min(places, buffer.count));
        }

        // A buffer's places taken, as read back a frame or two late.
        internal void NoteGpuTaken(ComputeBuffer buffer, int taken)
        {
            if (buffer != null) gpuTaken[buffer] = taken;
        }

        // A submesh's key: the mesh's instance id in the high half, the submesh in the low.
        private static long Key(Mesh mesh, int subMesh)
        {
            return ((long)mesh.GetInstanceID() << 32) | (uint)subMesh;
        }

        // What reading meshes and handing them over may take of a frame: the rest waits for
        // the next frames, their instances left out until then. Read all in one frame, a
        // flight's first frames took up to 27 ms for one mesh and 30 ms for the scene's
        // update (the perf monitor). The first mesh of a frame is read whatever it takes.
        private const double UploadBudgetMs = 2.0;
        private long uploadTicks;

        // Whether this frame's uploads are still within the budget; the first always is.
        private bool UploadAllowed
        {
            get { return uploadTicks == 0 || uploadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency < UploadBudgetMs; }
        }

        // Upload, its time counted against the frame's budget.
        private bool TimedUpload(Mesh mesh, int subMesh, int id, Material material)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            bool done = Upload(mesh, subMesh, id, material);
            uploadTicks += Math.Max(1L, System.Diagnostics.Stopwatch.GetTimestamp() - start);
            return done;
        }

        // The proxy's id of a submesh, handed over when first seen; 0 where it cannot
        // be read, or not yet within this frame's upload budget.
        // `material`: the one it is drawn with, for the colours KSP's terrain shaders make
        // of its vertex colours (RtStockTerrain).
        private int MeshId(Mesh mesh, int subMesh, Material material)
        {
            if (mesh == null) return 0;
            long key = Key(mesh, subMesh);
            MeshEntry entry;
            if (meshes.TryGetValue(key, out entry))
            {
                entry.LastSeen = frame;
                if (entry.Dirty && UploadAllowed)
                {
                    // Built again since: handed over again under the same id.
                    entry.Dirty = false;
                    if (!TimedUpload(mesh, subMesh, entry.Id, material)) return 0;
                }
                return entry.Id;
            }
            if (!UploadAllowed) return 0;
            if (unusable.Contains(key)) return 0;
            int failedAt;
            if (failed.TryGetValue(key, out failedAt) && frame - failedAt < FailedRetryFrames) return 0;
            if (!mesh.isReadable)
            {
                unusable.Add(key);
                LastSkip = "mesh " + mesh.name + " is not readable";
                return 0;
            }
            if (subMesh < 0 || subMesh >= mesh.subMeshCount || mesh.GetTopology(subMesh) != MeshTopology.Triangles)
            {
                unusable.Add(key);
                LastSkip = "mesh " + mesh.name + " has no triangles in submesh " + subMesh;
                return 0;
            }
            int id = nextMeshId++;
            if (!TimedUpload(mesh, subMesh, id, material))
            {
                failed[key] = frame;
                return 0;
            }
            failed.Remove(key);
            meshes[key] = new MeshEntry { Id = id, LastSeen = frame, Mesh = mesh, SubMesh = subMesh };
            List<long> keys;
            if (!keysOf.TryGetValue(mesh, out keys)) keysOf[mesh] = keys = new List<long>();
            keys.Add(key);
            return id;
        }

        // Hands a submesh's vertices, normals (flat ones where it has none), UVs, colours and triangles to the proxy
        // under `id`; false, with LastSkip saying why, where it is empty or the proxy refuses it.
        private bool Upload(Mesh mesh, int subMesh, int id, Material material)
        {
            if (subMesh < 0 || subMesh >= mesh.subMeshCount
                || mesh.GetTopology(subMesh) != MeshTopology.Triangles)
            {
                LastSkip = "mesh " + mesh.name + " has no triangles in submesh " + subMesh;
                return false;
            }
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector2[] uvs = mesh.uv;
            // Parallax's terrain takes the planet's colour from them (RtTerrainColour); KSP's
            // own, the colour its shader makes of them (RtStockTerrain).
            Color32[] colors = RtStockTerrain.Colours(mesh, material) ?? mesh.colors32;
            int[] indices = mesh.GetIndices(subMesh);
            if (vertices.Length == 0 || indices.Length < 3)
            {
                LastSkip = "mesh " + mesh.name + " is empty";
                return false;
            }
            if (normals.Length != vertices.Length) normals = FlatNormals(vertices, indices);

            GCHandle positions = GCHandle.Alloc(vertices, GCHandleType.Pinned);
            GCHandle normalHandle = GCHandle.Alloc(normals, GCHandleType.Pinned);
            GCHandle uvHandle = uvs.Length == vertices.Length ? GCHandle.Alloc(uvs, GCHandleType.Pinned) : default(GCHandle);
            GCHandle colorHandle = colors.Length == vertices.Length ? GCHandle.Alloc(colors, GCHandleType.Pinned) : default(GCHandle);
            GCHandle indexHandle = GCHandle.Alloc(indices, GCHandleType.Pinned);
            try
            {
                var desc = new RtNative.MeshDesc
                {
                    Size = (uint)Marshal.SizeOf(typeof(RtNative.MeshDesc)),
                    Magic = RtNative.MeshMagic,
                    Mesh = id,
                    VertexCount = (uint)vertices.Length,
                    IndexCount = (uint)(indices.Length - indices.Length % 3),
                    Positions = positions.AddrOfPinnedObject(),
                    Normals = normalHandle.AddrOfPinnedObject(),
                    Uvs = uvHandle.IsAllocated ? uvHandle.AddrOfPinnedObject() : IntPtr.Zero,
                    Indices = indexHandle.AddrOfPinnedObject(),
                    Colors = colorHandle.IsAllocated ? colorHandle.AddrOfPinnedObject() : IntPtr.Zero,
                };
                return RtNative.KspRtSetMesh(ref desc) == 1;
            }
            finally
            {
                positions.Free();
                normalHandle.Free();
                if (uvHandle.IsAllocated) uvHandle.Free();
                if (colorHandle.IsAllocated) colorHandle.Free();
                indexHandle.Free();
            }
        }

        // A mesh whose vertices or triangles changed: handed over again when next seen.
        internal void Invalidate(Mesh mesh)
        {
            List<long> keys;
            if (mesh == null || !keysOf.TryGetValue(mesh, out keys)) return;
            foreach (long key in keys)
            {
                MeshEntry entry;
                if (meshes.TryGetValue(key, out entry)) entry.Dirty = true;
            }
        }

        // Normals from the triangles, where a mesh brings none.
        internal static Vector3[] FlatNormals(Vector3[] vertices, int[] indices)
        {
            var normals = new Vector3[vertices.Length];
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                Vector3 a = vertices[indices[i]], b = vertices[indices[i + 1]], c = vertices[indices[i + 2]];
                Vector3 n = Vector3.Cross(b - a, c - a);
                normals[indices[i]] += n;
                normals[indices[i + 1]] += n;
                normals[indices[i + 2]] += n;
            }
            for (int i = 0; i < normals.Length; i++)
                normals[i] = normals[i].sqrMagnitude > 0f ? normals[i].normalized : Vector3.up;
            return normals;
        }

        // `scenery`: the space centre's, whose untouched defaults are matt (RtMaterialIntent).
        private int MaterialId(Material material, bool scenery = false)
        {
            if (material == null) return 0;
            MaterialEntry entry;
            if (materials.TryGetValue(material, out entry))
            {
                if (entry.LastSeen != frame && !entry.Queued
                    && (frame + entry.Id) % (entry.Texture == null ? MissingTextureCheckFrames : TextureCheckFrames) == 0
                    && MainTexture(material) != entry.Texture)
                {
                    entry.Queued = true;
                    texturesToRead.Enqueue(material);
                }
                entry.LastSeen = frame;
                return entry.Id;
            }
            entry = new MaterialEntry { Id = nextMaterialId++, LastSeen = frame };
            materials[material] = entry;
            entry.Scenery = scenery;
            SendMaterial(material, entry.Id, -1, scenery);
            if (MainTexture(material) != null)
            {
                entry.Queued = true;
                texturesToRead.Enqueue(material);
            }
            return entry.Id;
        }

        // The texture the rays take for the material's colour (RtMaterial.MainTexture).
        private static Texture MainTexture(Material material)
        {
            bool worldMapped;
            return RtMaterial.MainTexture(material, out worldMapped);
        }

        // Hands the material's values to the proxy under `id`, its texture at `layer` of the proxy's array (-1 for
        // none): the surface (RtMaterial), its colour from the terrain's where it is terrain, its shine as meant
        // (RtMaterialIntent), its diffuse share and highlight for the forward light.
        private void SendMaterial(Material material, int id, int layer, bool scenery)
        {
            RtMaterial surface = RtMaterial.Of(material);
            RtMaterialIntent intent = RtMaterialIntent.Of(material, scenery);
            // Parallax's terrain: its colour from the planet's vertex colours, which the
            // proxy adds and takes to linear itself where vertexFactor > 0.
            Color terrain;
            float vertexFactor = 0f;
            bool isTerrain = RtTerrainColour.Of(material, out terrain, out vertexFactor);
            if (isTerrain)
            {
                surface.Albedo = terrain;
                layer = -1;
            }
            else if (RtStockTerrain.Applies(material))
            {
                // KSP's own terrain: the whole colour in the vertex colours (RtStockTerrain).
                surface.Albedo = Color.black;
                vertexFactor = 1f;
                layer = -1;
            }
            if (GammaSpace)
            {
                if (vertexFactor <= 0f) surface.Albedo = surface.Albedo.linear;
                surface.Emission = surface.Emission.linear;
            }
            var desc = new RtNative.MaterialDesc
            {
                Size = (uint)Marshal.SizeOf(typeof(RtNative.MaterialDesc)),
                Magic = RtNative.MaterialMagic,
                Material = id,
                AlbedoR = surface.Albedo.r, AlbedoG = surface.Albedo.g, AlbedoB = surface.Albedo.b, AlbedoA = surface.Cutoff,
                SpecularR = surface.Specular.r, SpecularG = surface.Specular.g, SpecularB = surface.Specular.b,
                Smoothness = surface.Smoothness,
                EmissionR = surface.Emission.r, EmissionG = surface.Emission.g, EmissionB = surface.Emission.b,
                Texture = layer,
                VertexColour = vertexFactor,
                Flags = intent.Mode,
                GlossDivisor = intent.GlossDivisor,
                GlossMean = intent.GlossMean,
                DiffuseShare = RtDeferredLook.DiffuseShare(material),
            };
            if (vertexFactor <= 0f)
            {
                Vector4 highlight = RtForwardHighlight.Of(material, GammaSpace);
                desc.HighlightR = highlight.x;
                desc.HighlightG = highlight.y;
                desc.HighlightB = highlight.z;
                desc.HighlightShape = highlight.w;
            }
            // Where the shine is meant otherwise than the G-buffer's, the hits take it as well.
            if (intent.Mode != RtNative.SurfaceKeep)
            {
                desc.SpecularR = intent.Specular.r;
                desc.SpecularG = intent.Specular.g;
                desc.SpecularB = intent.Specular.b;
                desc.Smoothness = intent.Smoothness;
            }
            RtNative.KspRtSetMaterial(ref desc);
        }

        // A few material textures a frame: rendered down to 64 pixels, read back without
        // waiting for the GPU (AsyncGPUReadback; its callback runs on the main thread, in
        // a later frame) and handed over as a layer of the proxy's array. A synchronous
        // read would stall the main thread until the GPU caught up.
        private void ReadTextures()
        {
            for (int n = 0; n < TexturesPerFrame && texturesToRead.Count > 0 && readsInFlight < TexturesInFlight; n++)
            {
                Material material = texturesToRead.Dequeue();
                MaterialEntry entry;
                if (material == null || !materials.TryGetValue(material, out entry)) continue;
                bool worldMapped;
                Texture texture = RtMaterial.MainTexture(material, out worldMapped);
                entry.Texture = texture;
                if (texture != null && entry.Layer < 0)
                {
                    if (freeLayers.Count > 0) entry.Layer = freeLayers.Pop();
                    else if (nextTextureLayer < RtNative.MaxMaterialTextures) entry.Layer = nextTextureLayer++;
                }
                if (texture == null || entry.Layer < 0)
                {
                    entry.Queued = false;
                    continue;
                }
                ReadTexture(material, entry, texture, worldMapped);
            }
        }

        // Draws one material's texture small and reads it back, without waiting where AsyncGPUReadback is supported.
        private void ReadTexture(Material material, MaterialEntry entry, Texture texture, bool worldMapped)
        {
            TextureRead read = idleReads.Count > 0 ? idleReads.Pop() : NewRead();
            // A pooled target destroyed under it -- a scene change's unloading of
            // unused assets took them before they were marked (NewRead) -- is
            // replaced: AsyncGPUReadback.Request on it throws.
            if (read.Target == null)
            {
                lostTargets++;
                Debug.Log("[ReDefinition] Raytracing: a texture read target was destroyed while pooled, made anew ("
                          + lostTargets + " so far).");
                read = NewRead();
            }
            read.Material = material;
            read.Entry = entry;
            read.Layer = entry.Layer;
            read.WorldMapped = worldMapped;
            read.Epoch = epoch;
            RenderTexture previous = RenderTexture.active;
            if (TextureReader != null)
            {
                TextureReader.SetFloat(RtCutoff, RtMaterial.Of(material).Cutoff);
                Graphics.Blit(texture, read.Target, TextureReader, MaterialTexturePass);
            }
            else
            {
                Graphics.Blit(texture, read.Target);
            }
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                RenderTexture.active = previous;
                readsInFlight++;
                AsyncGPUReadback.Request(read.Target, 0, TextureFormat.RGBA32, read.Done);
                return;
            }
            int size = RtNative.MaterialTextureSize;
            if (textureRead == null) textureRead = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            RenderTexture.active = read.Target;
            textureRead.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
            RenderTexture.active = previous;
            Deliver(read, textureRead.GetRawTextureData<byte>());
            read.Material = null;
            read.Entry = null;
            idleReads.Push(read);
        }

        // A read with its target of MaterialTextureSize and its own callback.
        private TextureRead NewRead()
        {
            int size = RtNative.MaterialTextureSize;
            var read = new TextureRead
            {
                // Pooled across scenes: the unloading of unused assets at a scene
                // change destroyed pooled targets without it (counted in the log).
                Target = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                {
                    hideFlags = HideFlags.DontUnloadUnusedAsset,
                },
            };
            read.Target.Create();
            read.Done = request => Done(read, request);
            return read;
        }

        // A finished readback, handed over if its material still has the entry and layer
        // it was read for: released meanwhile, its layer may be another's by now.
        private void Done(TextureRead read, AsyncGPUReadbackRequest request)
        {
            readsInFlight--;
            MaterialEntry current;
            if (read.Epoch == epoch && read.Material != null && materials.TryGetValue(read.Material, out current)
                && current == read.Entry && current.Layer == read.Layer)
            {
                if (request.hasError)
                {
                    // Read again when next seen.
                    current.Queued = false;
                    current.Texture = null;
                }
                else
                    Deliver(read, request.GetData<byte>());
            }
            read.Material = null;
            read.Entry = null;
            if (read.Epoch == epoch)
                idleReads.Push(read);
            else if (read.Target != null)
            {
                // Read for a device or a scene gone.
                read.Target.Release();
                UnityEngine.Object.Destroy(read.Target);
            }
        }

        // The read texture into its layer of the proxy's array, then the material again with that layer; a read
        // smaller than the size leaves the material without a texture.
        private void Deliver(TextureRead read, NativeArray<byte> raw)
        {
            MaterialEntry entry = read.Entry;
            entry.Queued = false;
            int size = RtNative.MaterialTextureSize;
            if (raw.Length < size * size * 4)
            {
                entry.Texture = null;
                return;
            }
            if (texturePixels == null) texturePixels = new byte[size * size * 4];
            Pack(raw, texturePixels, size, read.WorldMapped);
            if (RtNative.KspRtSetMaterialTexture(read.Layer, texturePixels, size) == 1)
                SendMaterial(read.Material, entry.Id, read.Layer, entry.Scenery);
        }

        // A small texture as the proxy takes it from Unity's pixels (RGBA, rows from the
        // bottom): rows from the top, the bytes as they are, which the proxy's sRGB array
        // decodes to linear values (GammaSpace). A world-mapped one
        // (RtMaterial.MainTexture) is its average colour throughout, averaged in linear.
        internal static void Pack(NativeArray<byte> raw, byte[] into, int size, bool average)
        {
            if (average)
            {
                float[] decode = SrgbToLinear();
                double r = 0, g = 0, b = 0;
                int count = size * size;
                for (int i = 0; i < count * 4; i += 4)
                {
                    r += decode[raw[i]];
                    g += decode[raw[i + 1]];
                    b += decode[raw[i + 2]];
                }
                double scale = 1.0 / count;
                byte red = LinearToSrgb(r * scale), green = LinearToSrgb(g * scale), blue = LinearToSrgb(b * scale);
                for (int o = 0; o < count * 4; o += 4)
                {
                    into[o] = red;
                    into[o + 1] = green;
                    into[o + 2] = blue;
                    into[o + 3] = 255;
                }
                return;
            }
            for (int y = 0; y < size; y++)
            {
                int from = (size - 1 - y) * size * 4;
                int to = y * size * 4;
                for (int x = 0; x < size * 4; x += 4)
                {
                    byte cr = raw[from + x], cg = raw[from + x + 1], cb = raw[from + x + 2];
                    into[to + x] = cr;
                    into[to + x + 1] = cg;
                    into[to + x + 2] = cb;
                    into[to + x + 3] = raw[from + x + 3];
                }
            }
        }

        // The sRGB code whose decoded value is v.
        private static byte LinearToSrgb(double v)
        {
            double s = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;
            return (byte)Math.Max(0, Math.Min(255, (int)Math.Round(s * 255.0)));
        }


        // An sRGB code's decoded value.
        private static float[] SrgbToLinear()
        {
            if (srgbToLinear != null) return srgbToLinear;
            srgbToLinear = new float[256];
            for (int i = 0; i < 256; i++)
            {
                double s = i / 255.0;
                srgbToLinear[i] = (float)(s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4));
            }
            return srgbToLinear;
        }

        // ReleaseUnseen's lists of what it releases, kept so that a check allocates nothing.
        private readonly List<long> released = new List<long>();
        private readonly List<Material> releasedMaterials = new List<Material>();

        // Releases the meshes and materials destroyed or not seen for UnseenFrames, their texture layers freed; now and
        // then forgets the keys of destroyed meshes and the long lists of unusable and failed ones.
        private void ReleaseUnseen()
        {
            released.Clear();
            foreach (KeyValuePair<long, MeshEntry> pair in meshes)
                if (pair.Value.Mesh == null || frame - pair.Value.LastSeen > UnseenFrames)
                    released.Add(pair.Key);
            foreach (long key in released)
            {
                MeshEntry entry = meshes[key];
                RtNative.KspRtReleaseMesh(entry.Id);
                meshes.Remove(key);
                List<long> keys;
                if (entry.Mesh != null && keysOf.TryGetValue(entry.Mesh, out keys))
                {
                    keys.Remove(key);
                    if (keys.Count == 0) keysOf.Remove(entry.Mesh);
                }
            }
            releasedMaterials.Clear();
            foreach (KeyValuePair<Material, MaterialEntry> pair in materials)
                if (pair.Key == null || frame - pair.Value.LastSeen > UnseenFrames)
                    releasedMaterials.Add(pair.Key);
            foreach (Material material in releasedMaterials)
            {
                MaterialEntry entry = materials[material];
                RtNative.KspRtReleaseMaterial(entry.Id);
                if (entry.Layer >= 0) freeLayers.Push(entry.Layer);
                materials.Remove(material);
            }
            if (frame % 600 == 0)
            {
                // Meshes destroyed: their keys go too.
                var dead = new List<Mesh>();
                foreach (Mesh mesh in keysOf.Keys)
                    if (mesh == null) dead.Add(mesh);
                foreach (Mesh mesh in dead) keysOf.Remove(mesh);
                // The unusable ones of meshes long gone: asked once more if still around.
                if (unusable.Count > 4096) unusable.Clear();
                if (failed.Count > 4096) failed.Clear();
            }
        }

        // Everything handed over let go, as the scene or the camera goes.
        internal void Clear()
        {
            foreach (MeshEntry entry in meshes.Values)
                RtNative.KspRtReleaseMesh(entry.Id);
            foreach (MaterialEntry entry in materials.Values)
                RtNative.KspRtReleaseMaterial(entry.Id);
            meshes.Clear();
            unusable.Clear();
            failed.Clear();
            keysOf.Clear();
            materials.Clear();
            texturesToRead.Clear();
            freeLayers.Clear();
            nextTextureLayer = 0;
            RtNative.KspRtSetInstances(instances, 0, RtNative.AnyFrame);
            RtNative.KspRtSetGpuGroups(gpuGroups, 0, RtNative.AnyFrame);
            GpuItems.Clear();
            GpuFirst.Clear();
            GpuTotal = 0;
            // Readbacks still on their way find the epoch changed and let their render
            // textures go themselves (Done).
            epoch++;
            while (idleReads.Count > 0)
            {
                TextureRead read = idleReads.Pop();
                // Unity destroys a texture of its own on unloading unused assets.
                if (read.Target == null) continue;
                read.Target.Release();
                UnityEngine.Object.Destroy(read.Target);
            }
            if (textureRead != null)
            {
                UnityEngine.Object.Destroy(textureRead);
                textureRead = null;
            }
        }
    }

    // A position in double precision, for the camera in a world KSP moves.
    internal struct RtPosition
    {
        // The position in Unity's world.
        public double x, y, z;

        // The position at x, y, z.
        public RtPosition(double x, double y, double z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        // A Unity position in double precision.
        public static RtPosition From(Vector3 v)
        {
            return new RtPosition(v.x, v.y, v.z);
        }
    }
}
