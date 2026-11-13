using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The tile map of the culling (package T16): one texel per tile of CullMath.TilePixels pixels of the full target, written by pass 5 of the layer shader
    // before the march. Red holds the bits of the layers the tile marches (bit i = layer i), green the reasons layers were dropped (1 the maps, 2 the
    // terrain, 4 no ray reaches the shell, 8 behind a closed deck), blue the farthest scene distance of the tile. The march reads it per pixel
    // (Pass0), and aid view 6 draws it.
    internal sealed class CullTiles
    {
        private RenderTexture tiles;
        private static readonly int IdTiles = Shader.PropertyToID("_CullTiles"), IdBMax0 = Shader.PropertyToID("_CullMax0"),
            IdBMax1 = Shader.PropertyToID("_CullMax1"), IdBMin0 = Shader.PropertyToID("_CullMin0"), IdBMin1 = Shader.PropertyToID("_CullMin1");

        internal RenderTexture Tiles { get { return tiles; } }
        internal int TilesX { get; private set; }
        internal int TilesY { get; private set; }

        private void Ensure(int fullW, int fullH)
        {
            int tx = Math.Max(1, (fullW + CullMath.TilePixels - 1) / CullMath.TilePixels), ty = Math.Max(1, (fullH + CullMath.TilePixels - 1) / CullMath.TilePixels);
            if (tiles != null && tiles.width == tx && tiles.height == ty) return;
            if (tiles != null)
            {
                tiles.Release();
                UnityEngine.Object.Destroy(tiles);
            }
            tiles = new RenderTexture(tx, ty, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = "cloud cull tiles",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            tiles.Create();
            TilesX = tx;
            TilesY = ty;
        }

        // Records the tile pass for a camera whose full target is fullW x fullH, with the material state of this frame's Apply. Nothing, and the march
        // reading no tiles, where neither skip switch is on. The scene depth global must be set already (the pass reads it like the march).
        internal void Record(CommandBuffer cb, LayerRenderer renderer, CullBounds bounds, CullSettings cull, int fullW, int fullH, Action<CommandBuffer> markStart,
                             Action<CommandBuffer> markEnd)
        {
            Material m = renderer.Material;
            // Apply has left _CullA off, so the march reads no tiles.
            if (cull == null || !cull.TilesNeeded || bounds.MaxFine == null) return;
            Ensure(fullW, fullH);
            cull.UploadTiles(m, TilesX, TilesY, fullW, fullH);
            m.SetTexture(IdTiles, tiles);
            m.SetTexture(IdBMax0, bounds.MaxFine);
            m.SetTexture(IdBMax1, bounds.MaxCoarse);
            m.SetTexture(IdBMin0, bounds.MinFine);
            m.SetTexture(IdBMin1, bounds.MinCoarse);
            if (markStart != null) markStart(cb);
            cb.Blit(BuiltinRenderTextureType.None, tiles, m, LayerRenderer.PassCullTile);
            if (markEnd != null) markEnd(cb);
        }

        internal void Dispose()
        {
            if (tiles != null)
            {
                tiles.Release();
                UnityEngine.Object.Destroy(tiles);
                tiles = null;
            }
        }
    }
}
