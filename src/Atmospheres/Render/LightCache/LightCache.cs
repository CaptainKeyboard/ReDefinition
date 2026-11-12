using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The light cache of the cloud pass (package T03, decision prototype B): an atlas of 64 x 64 x 32 voxels (32 tiles of 64 x 64, eight to a row)
    // holding the optical depth towards the sun (r) and straight up (g) at the voxel centres, over a box in the planet frame that follows the
    // camera. The layer shader reads it with a trilinear fetch inside the box and falls back to its per-sample march outside (prototype A).
    // The fill is pass 3 of the layer shader: one full-screen draw of the atlas, so a partial fill (time slicing) is a scissor over some tiles.
    // It uses nothing of the game, so the weather player links it as it is.
    internal sealed class LightCache
    {
        internal const int GridN = 64, GridNz = 32, TilesPerRow = 8;
        internal const int PassFill = 3;

        private static readonly int IdLightCache = Shader.PropertyToID("_LightCache"), IdCacheA = Shader.PropertyToID("_CacheA"),
            IdCacheB = Shader.PropertyToID("_CacheB");

        private RenderTexture atlas;

        // Whether the shader reads the atlas.
        public bool Enabled;
        // The box: minimum corner and size in m (planet frame).
        public Vector3 Min, Size = new Vector3(4000f, 4000f, 4000f);

        internal RenderTexture Atlas { get { return atlas; } }
        internal int Voxels { get { return GridN * GridN * GridNz; } }

        internal void Ensure()
        {
            if (atlas != null) return;
            atlas = new RenderTexture(GridN * TilesPerRow, GridN * (GridNz / TilesPerRow), 0, RenderTextureFormat.RGFloat, RenderTextureReadWrite.Linear)
            {
                name = "cloud light cache",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            atlas.Create();
        }

        // Centres the box on a point, its corner snapped to whole voxels so that a moving camera does not shimmer the cache's grid.
        internal void Follow(Vector3 centre)
        {
            Vector3 voxel = new Vector3(Size.x / GridN, Size.y / GridN, Size.z / GridNz);
            Vector3 min = centre - Size * 0.5f;
            Min = new Vector3(Mathf.Round(min.x / voxel.x) * voxel.x, Mathf.Round(min.y / voxel.y) * voxel.y, Mathf.Round(min.z / voxel.z) * voxel.z);
        }

        internal void Bind(Material m)
        {
            Ensure();
            m.SetTexture(IdLightCache, atlas);
            m.SetVector(IdCacheA, new Vector4(Min.x, Min.y, Min.z, 0f));
            m.SetVector(IdCacheB, new Vector4(Size.x, Size.y, Size.z, TilesPerRow));
        }

        // Fills the whole atlas now (immediate mode, for the weather player and for a single refresh); the layer material carries the frame.
        internal void Fill(Material layerMaterial)
        {
            Ensure();
            Bind(layerMaterial);
            Graphics.Blit(null, atlas, layerMaterial, PassFill);
        }

        internal void Dispose()
        {
            if (atlas != null) atlas.Release();
            Object.Destroy(atlas);
            atlas = null;
        }
    }
}
