using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The light cache of the cloud pass: a texture array (one slice per voxel layer) of the optical depth towards the sun at its voxel centres, over a box in a local frame of the planet (the
    // horizontal axes on the tangent plane below the camera, the third straight up) that follows the camera. The march keeps its own first sun taps (where the heads
    // shade each other; their places unchanged where the span to the sun is 12 km long) and reads the rest of the way to the sun here, at the end of those taps; outside the box it marches the whole way.
    // The fill is pass 3 of the layer shader, one draw per slice, at the head of the march in the same buffer. It uses nothing of the game, so the weather player
    // links it as it is.
    internal sealed class LightCache
    {
        internal const int PassFill = 3;

        private static readonly int IdLightCache = Shader.PropertyToID("_LightCache"), IdCacheA = Shader.PropertyToID("_CacheA"),
            IdCacheX = Shader.PropertyToID("_CacheX"), IdCacheY = Shader.PropertyToID("_CacheY"), IdCacheZ = Shader.PropertyToID("_CacheZ"),
            IdCacheH = Shader.PropertyToID("_CacheH"), IdCacheSlice = Shader.PropertyToID("_CacheSlice");

        private RenderTexture volume;
        private Vector3 corner, axisX, axisY, axisZ, centre;
        private Vector4 placedFor;
        private bool placed;

        // Whether the march reads the cache.
        public bool Enabled;
        // The voxels across and up (312 m across, 336 m up; the owner judged 128 x 128 x 32 equal to the full march in the weather player, 2026-11-19).
        public int GridN = 128, GridNz = 32;
        // The box: its width and depth, its height above the tangent plane below the camera and how far it reaches below that plane (the surface curves away from
        // it: about 900 m at the far corner of a box laid 5 km behind the camera on Kerbin).
        public float SizeM = 40000f, HeightM = 10000f, BelowM = 1200f;
        // The march's own first sun taps; the growth of each of the fill's taps (CloudLightSettings.CacheFillSteps) over the one before.
        public int NearTaps = 3;
        public float FillRatio = 1.15f;

        internal RenderTexture Volume { get { return volume; } }
        internal int Voxels { get { return GridN * GridN * GridNz; } }

        internal void Ensure()
        {
            if (volume != null && volume.width == GridN && volume.volumeDepth == GridNz) return;
            Dispose();
            volume = new RenderTexture(GridN, GridN, 0, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear)
            {
                name = "cloud light cache",
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = GridNz,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            volume.Create();
        }

        // Places the box around the camera (planet frame). The frame stays where it is until the camera has moved an eighth of the box away from its centre, so that
        // the voxels do not shimmer while the camera moves; it is then laid anew under the camera.
        internal void Follow(Vector3 camera, float planetRadius)
        {
            var box = new Vector4(SizeM, HeightM, BelowM, planetRadius);
            if (placed && box == placedFor && (camera - centre).sqrMagnitude < 0.015625f * SizeM * SizeM) return;
            placedFor = box;
            Vector3 up = camera.normalized;
            Vector3 reference = Mathf.Abs(up.y) < 0.9f ? Vector3.up : Vector3.right;
            Vector3 east = Vector3.Cross(reference, up).normalized, north = Vector3.Cross(up, east);
            Vector3 ground = up * planetRadius;
            axisX = east * SizeM;
            axisY = north * SizeM;
            axisZ = up * (HeightM + BelowM);
            Vector3 along = camera - ground;
            Vector3 flat = along - up * Vector3.Dot(along, up);
            corner = ground + flat - 0.5f * axisX - 0.5f * axisY - up * BelowM;
            centre = camera;
            placed = true;
        }

        // An axis-aligned box of the planet frame instead of the one around the camera (the weather player's light scenes).
        internal void Place(Vector3 min, Vector3 size)
        {
            corner = min;
            axisX = new Vector3(size.x, 0f, 0f);
            axisY = new Vector3(0f, size.y, 0f);
            axisZ = new Vector3(0f, 0f, size.z);
            centre = min + 0.5f * size;
            placedFor = new Vector4(float.NaN, 0f, 0f, 0f);
            placed = true;
        }

        internal void Bind(Material m)
        {
            Ensure();
            m.SetTexture(IdLightCache, volume);
            m.SetVector(IdCacheA, new Vector4(corner.x, corner.y, corner.z, Enabled ? 1f : 0f));
            m.SetVector(IdCacheX, Edge(axisX));
            m.SetVector(IdCacheY, Edge(axisY));
            m.SetVector(IdCacheZ, Edge(axisZ));
            m.SetVector(IdCacheH, new Vector4(Mathf.Max(NearTaps, 1), Mathf.Max(FillRatio, 1f), GridNz, 0f));
        }

        private static Vector4 Edge(Vector3 v)
        {
            float l2 = Mathf.Max(v.sqrMagnitude, 1f);
            return new Vector4(v.x, v.y, v.z, 1f / l2);
        }

        // Records the fill of every slice with the layer material's state of this frame (its Apply and Bind ran before).
        internal void Record(CommandBuffer cb, Material layerMaterial)
        {
            Ensure();
            for (int z = 0; z < GridNz; z++)
            {
                cb.SetGlobalVector(IdCacheSlice, new Vector4(z, GridNz, GridN, GridN));
                // The slice as the target and a Blit into the active target: a Blit straight into RenderTargetIdentifier(array, 0, face, z) wrote slice 0 only (Unity
                // 2019.4, D3D11; read back in the weather player 2026-11-18).
                cb.SetRenderTarget(new RenderTargetIdentifier(volume), 0, CubemapFace.Unknown, z);
                cb.Blit(BuiltinRenderTextureType.None, BuiltinRenderTextureType.CurrentActive, layerMaterial, PassFill);
            }
        }

        // Fills the whole cache now (immediate mode, the weather player's light scenes); the layer material carries the frame.
        internal void Fill(Material layerMaterial)
        {
            Bind(layerMaterial);
            var cb = new CommandBuffer { name = "light cache fill" };
            Record(cb, layerMaterial);
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();
        }

        internal void Dispose()
        {
            if (volume != null)
            {
                volume.Release();
                Object.Destroy(volume);
            }
            volume = null;
        }
    }
}
