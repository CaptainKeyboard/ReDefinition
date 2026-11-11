using System;

namespace ReDefinition.Atmospheres
{
    // The width of the mark a wheel presses. The tyre's width is the smallest dimension of the wheel's own mesh (a tyre is a disc); the
    // stamp is a disc of radius half that width, never narrower than half a texel (the lattice of the tiles is 0.5 m, so a narrower
    // tyre still marks one texel). Where the mesh gives nothing, the tuning key tracks.halfWidthM stands. Pure. Package D34, finding R5.
    internal static class TrackWidth
    {
        // The half width in metres for a wheel mesh whose three dimensions (local size times scale) are a, b and c; fallbackHalfM where
        // none of them is a usable number.
        internal static float HalfWidthM(float a, float b, float c, float fallbackHalfM)
        {
            float smallest = float.MaxValue;
            foreach (float v in new[] { a, b, c })
                if (v > 0.01f && !float.IsNaN(v) && !float.IsInfinity(v) && v < smallest) smallest = v;
            if (smallest == float.MaxValue) return fallbackHalfM;
            return Math.Max((float)TrackTiles.TexelMetres * 0.5f, smallest * 0.5f);
        }
    }
}
