using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // One thing the rays see this frame: a submesh of a mesh with its material and
    // its transform. Sources fill these (IRtSource); the scene hands them over.
    internal struct RtItem
    {
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
        void Collect(List<RtItem> into);
    }

    // Instances only the GPU knows: an append buffer of transforms (float4x4, the
    // mesh's to the world, rows 0-2 read), filled for this frame before the camera
    // draws, as Parallax fills its scatter's (ScatterRenderer.outputLOD0-2).
    internal struct RtGpuItem
    {
        public ComputeBuffer Transforms;
        public Mesh Mesh;
        public int SubMesh;
        public Material Material;
        public uint Mask;
    }

    internal interface IRtGpuSource
    {
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
        // New material textures read back a frame, as each costs a GPU readback.
        private const int TexturesPerFrame = 4;

        private sealed class MeshEntry
        {
            public int Id;
            public int LastSeen;
            public Mesh Mesh;
            public int SubMesh;
            public bool Dirty;
        }

        private sealed class MaterialEntry
        {
            public int Id;
            public int LastSeen;
            public bool TextureDone;
        }

        private readonly Dictionary<long, MeshEntry> meshes = new Dictionary<long, MeshEntry>();
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
        private int nextMeshId = 1;
        private int nextMaterialId = 1;
        private int nextTextureLayer;
        private int generation = -1;
        private int frame;
        private RenderTexture textureTarget;
        private Texture2D textureRead;
        private byte[] texturePixels;
        private static byte[] gammaToSrgb;

        internal int InstanceCount { get; private set; }
        // This frame's GPU instances: the items the camera gathers, each run's place, and
        // their places together.
        internal readonly List<RtGpuItem> GpuItems = new List<RtGpuItem>();
        internal readonly List<int> GpuFirst = new List<int>();
        // Each item's places this frame: what it took lately and a quarter more.
        internal readonly List<int> GpuPlaces = new List<int>();
        internal int GpuTotal { get; private set; }
        // Where the camera is this frame, for the sources' range.
        internal Vector3 Eye { get; private set; }
        internal int Uploads { get; private set; }
        internal int MeshCount { get { return meshes.Count; } }
        internal int MaterialCount { get { return materials.Count; } }
        internal int Skipped { get; private set; }
        internal string LastSkip { get; private set; }

        // In gamma space, as KSP renders, a texture's values are what the lighting
        // works with; the proxy's array is sRGB, so they are encoded to come out as
        // they were.
        internal bool GammaSpace = QualitySettings.activeColorSpace == ColorSpace.Gamma;

        internal void AddSource(IRtSource source)
        {
            if (!sources.Contains(source)) sources.Add(source);
        }

        internal void RemoveSource(IRtSource source)
        {
            sources.Remove(source);
        }

        internal void AddGpuSource(IRtGpuSource source)
        {
            if (!gpuSources.Contains(source)) gpuSources.Add(source);
        }

        // Everything this frame, relative to the camera at cameraPosition, for the frame
        // of that index (RtFramePacket's frameIndex).
        internal void Update(RtPosition cameraPosition, int serviceGeneration, uint frameIndex)
        {
            frame++;
            if (serviceGeneration != generation)
            {
                // A new device: nothing handed over before is there any more.
                generation = serviceGeneration;
                meshes.Clear();
                keysOf.Clear();
                materials.Clear();
                texturesToRead.Clear();
                nextTextureLayer = 0;
            }

            Eye = new Vector3((float)cameraPosition.x, (float)cameraPosition.y, (float)cameraPosition.z);
            Uploads = 0;
            items.Clear();
            foreach (IRtSource source in sources)
                source.Collect(items);

            if (instances.Length < items.Count) Array.Resize(ref instances, Math.Max(items.Count, instances.Length * 2));
            int count = 0;
            Skipped = 0;
            foreach (RtItem item in items)
            {
                int mesh = MeshId(item.Mesh, item.SubMesh);
                if (mesh == 0)
                {
                    Skipped++;
                    continue;
                }
                int material = MaterialId(item.Material);
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
            ReleaseUnseen();
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
                int mesh = MeshId(item.Mesh, item.SubMesh);
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

        private static long Key(Mesh mesh, int subMesh)
        {
            return ((long)mesh.GetInstanceID() << 8) ^ subMesh;
        }

        // The proxy's id of a submesh, handed over when first seen; 0 where it cannot
        // be read.
        private int MeshId(Mesh mesh, int subMesh)
        {
            if (mesh == null) return 0;
            long key = Key(mesh, subMesh);
            MeshEntry entry;
            if (meshes.TryGetValue(key, out entry))
            {
                entry.LastSeen = frame;
                if (entry.Dirty)
                {
                    // Built again since: handed over again under the same id.
                    entry.Dirty = false;
                    if (!Upload(mesh, subMesh, entry.Id)) return 0;
                    Uploads++;
                }
                return entry.Id;
            }
            if (!mesh.isReadable)
            {
                LastSkip = "mesh " + mesh.name + " is not readable";
                return 0;
            }
            int id = nextMeshId++;
            if (!Upload(mesh, subMesh, id)) return 0;
            Uploads++;
            meshes[key] = new MeshEntry { Id = id, LastSeen = frame, Mesh = mesh, SubMesh = subMesh };
            List<long> keys;
            if (!keysOf.TryGetValue(mesh, out keys)) keysOf[mesh] = keys = new List<long>();
            keys.Add(key);
            return id;
        }

        private bool Upload(Mesh mesh, int subMesh, int id)
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
                };
                return RtNative.KspRtSetMesh(ref desc) == 1;
            }
            finally
            {
                positions.Free();
                normalHandle.Free();
                if (uvHandle.IsAllocated) uvHandle.Free();
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

        private int MaterialId(Material material)
        {
            if (material == null) return 0;
            MaterialEntry entry;
            if (materials.TryGetValue(material, out entry))
            {
                entry.LastSeen = frame;
                return entry.Id;
            }
            entry = new MaterialEntry { Id = nextMaterialId++, LastSeen = frame };
            materials[material] = entry;
            SendMaterial(material, entry.Id, -1);
            if (MainTexture(material) != null && nextTextureLayer < RtNative.MaxMaterialTextures)
                texturesToRead.Enqueue(material);
            else
                entry.TextureDone = true;
            return entry.Id;
        }

        private static Texture MainTexture(Material material)
        {
            return material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
        }

        private void SendMaterial(Material material, int id, int layer)
        {
            RtMaterial surface = RtMaterial.Of(material);
            var desc = new RtNative.MaterialDesc
            {
                Size = (uint)Marshal.SizeOf(typeof(RtNative.MaterialDesc)),
                Magic = RtNative.MaterialMagic,
                Material = id,
                AlbedoR = surface.Albedo.r, AlbedoG = surface.Albedo.g, AlbedoB = surface.Albedo.b, AlbedoA = surface.Albedo.a,
                SpecularR = surface.Specular.r, SpecularG = surface.Specular.g, SpecularB = surface.Specular.b,
                Smoothness = surface.Smoothness,
                EmissionR = surface.Emission.r, EmissionG = surface.Emission.g, EmissionB = surface.Emission.b,
                Texture = layer,
            };
            RtNative.KspRtSetMaterial(ref desc);
        }

        // A few material textures a frame: rendered down to 64 pixels, read back and
        // handed over as a layer of the proxy's array.
        private void ReadTextures()
        {
            for (int n = 0; n < TexturesPerFrame && texturesToRead.Count > 0; n++)
            {
                Material material = texturesToRead.Dequeue();
                MaterialEntry entry;
                if (material == null || !materials.TryGetValue(material, out entry) || entry.TextureDone) continue;
                entry.TextureDone = true;
                Texture texture = MainTexture(material);
                if (texture == null) continue;
                int layer = nextTextureLayer++;
                if (!ReadTexture(texture, layer)) continue;
                SendMaterial(material, entry.Id, layer);
            }
        }

        private bool ReadTexture(Texture texture, int layer)
        {
            int size = RtNative.MaterialTextureSize;
            if (textureTarget == null)
            {
                textureTarget = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                textureTarget.Create();
                textureRead = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
                texturePixels = new byte[size * size * 4];
            }
            RenderTexture previous = RenderTexture.active;
            Graphics.Blit(texture, textureTarget);
            RenderTexture.active = textureTarget;
            textureRead.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
            RenderTexture.active = previous;
            Color32[] colours = textureRead.GetPixels32();
            byte[] encode = GammaSpace ? GammaToSrgb() : null;
            // Rows from the top, as the proxy takes them; Unity's from the bottom.
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    Color32 c = colours[(size - 1 - y) * size + x];
                    int o = (y * size + x) * 4;
                    texturePixels[o] = encode != null ? encode[c.r] : c.r;
                    texturePixels[o + 1] = encode != null ? encode[c.g] : c.g;
                    texturePixels[o + 2] = encode != null ? encode[c.b] : c.b;
                    texturePixels[o + 3] = c.a;
                }
            return RtNative.KspRtSetMaterialTexture(layer, texturePixels, size) == 1;
        }

        // The sRGB code whose decoded value is the byte's own value.
        private static byte[] GammaToSrgb()
        {
            if (gammaToSrgb != null) return gammaToSrgb;
            gammaToSrgb = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double v = i / 255.0;
                double s = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;
                gammaToSrgb[i] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(s * 255.0)));
            }
            return gammaToSrgb;
        }

        private readonly List<long> released = new List<long>();

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
            if (frame % 600 == 0)
            {
                // Meshes destroyed: their keys go too.
                var dead = new List<Mesh>();
                foreach (Mesh mesh in keysOf.Keys)
                    if (mesh == null) dead.Add(mesh);
                foreach (Mesh mesh in dead) keysOf.Remove(mesh);
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
            keysOf.Clear();
            materials.Clear();
            texturesToRead.Clear();
            RtNative.KspRtSetInstances(instances, 0, RtNative.AnyFrame);
            RtNative.KspRtSetGpuGroups(gpuGroups, 0, RtNative.AnyFrame);
            GpuItems.Clear();
            GpuFirst.Clear();
            GpuTotal = 0;
            if (textureTarget != null)
            {
                textureTarget.Release();
                UnityEngine.Object.Destroy(textureTarget);
                UnityEngine.Object.Destroy(textureRead);
                textureTarget = null;
            }
        }
    }

    // A position in double precision, for the camera in a world KSP moves.
    internal struct RtPosition
    {
        public double x, y, z;

        public RtPosition(double x, double y, double z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static RtPosition From(Vector3 v)
        {
            return new RtPosition(v.x, v.y, v.z);
        }
    }
}
