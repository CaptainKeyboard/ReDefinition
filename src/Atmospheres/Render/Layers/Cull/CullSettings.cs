using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The switches and numbers of the visibility culling and the distance LOD of the own cloud layers (package T16). Plain data the caller keeps and
    // rewrites each frame (LayerFrame.Cull); Upload writes the part the march reads into the layer material, the tile pass (CullTiles) the rest.
    // Every switch is off by default: whatever is on lowers the cost at some error, and a quality reduction waits for the owner's agreement.
    //
    //   SkipEmpty         a tile of 16 x 16 pixels whose rays cannot reach a cloud (the maps' dilated maximum along the ray bundle) marches no layer
    //   SkipClosed        layers above a deck that is certainly closed for a whole tile (seen from below) are not marched for it
    //   DistanceCut       a span whose layer is below the cut weight after the air (needs the aerial perspective) is not marched; it fades in between
    //   StepGrowth        the step grows with the length along the ray inside a span
    //   TapLod            the sun march is repeated less often with distance (the reuse stride and the weight cut-off grow)
    //   DetailLod         the noise detail's footprint grows with the pixel's size at the distance (the detail drops where the pixel cannot resolve it)
    //   CapDistanceM      beyond this distance only the nearest CapKeepSpans spans of a ray are marched (0: off)
    internal sealed class CullSettings
    {
        public bool SkipEmpty, SkipClosed, DistanceCut, StepGrowth, TapLod, DetailLod;
        public float CapDistanceM;

        // The numbers of the switches. CutWeight: the transmittance of the air below which a layer is cut (it fades up to twice this). StepGrowthPerM: the
        // relative growth of the step per m along the span. The tap LOD factor applies from TapStartM and has taken full effect at TapEndM.
        // FootScale: the pixel's footprint of the detail LOD in pixels. CapKeepSpans: spans kept at any distance.
        public float CutWeight = 0.02f, StepGrowthPerM = 4e-5f, TapStartM = 20000f, TapEndM = 120000f, TapFactor = 3f, FootScale = 1.5f;
        public int CapKeepSpans = 2;

        // A measurement lever (no setting): which tests of the skip run. Bit 0 the shells (no ray of the tile reaches a layer), bit 1 the terrain (the
        // scene ends before the layer starts), bit 2 the maps (the dilated maximum along the bundle). Sweeps of the package report the three apart.
        public int Tests = 7;

        // Set by the caller of the pass before Apply: the angular size of one pixel of the march target (rad).
        public float PixelAngleRad;

        internal bool TilesNeeded
        {
            get { return SkipEmpty || SkipClosed; }
        }

        internal bool AnyOn
        {
            get { return SkipEmpty || SkipClosed || DistanceCut || StepGrowth || TapLod || DetailLod || CapDistanceM > 0f; }
        }

        private static class Ids
        {
            internal static readonly int A = Shader.PropertyToID("_CullA"), B = Shader.PropertyToID("_CullB"), C = Shader.PropertyToID("_CullC"),
                D = Shader.PropertyToID("_CullD"), T = Shader.PropertyToID("_CullT");
        }

        // The march's part. _CullA stays off (the tile pass turns it on when it records), the others follow the switches.
        internal void Upload(Material m)
        {
            m.SetVector(Ids.A, new Vector4(0f, 0f, CullMath.TilePixels, 0f));
            m.SetVector(Ids.B, new Vector4(DistanceCut ? 1f : 0f, Mathf.Max(CutWeight, 1e-4f), Mathf.Max(CapDistanceM, 0f), Mathf.Max(CapKeepSpans, 1)));
            m.SetVector(Ids.C, new Vector4(StepGrowth ? Mathf.Max(StepGrowthPerM, 0f) : 0f, TapStartM, Mathf.Max(TapEndM, TapStartM + 1f), TapLod ? Mathf.Max(TapFactor, 1f) : 1f));
            m.SetVector(Ids.D, new Vector4(DetailLod ? 1f : 0f, Mathf.Max(FootScale, 0f) * PixelAngleRad, 0f, 0f));
        }

        // The tile pass's part: the switches of the skip, and the tile grid over the full target.
        internal void UploadTiles(Material m, int tilesX, int tilesY, int fullW, int fullH)
        {
            m.SetVector(Ids.A, new Vector4(SkipEmpty ? 1f : 0f, SkipClosed ? 1f : 0f, CullMath.TilePixels, SkipEmpty ? Tests : 0));
            m.SetVector(Ids.T,new Vector4(tilesX, tilesY, fullW, fullH));
        }
    }
}
