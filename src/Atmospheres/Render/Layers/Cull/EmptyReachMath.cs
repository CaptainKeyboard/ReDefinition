using System;

namespace ReDefinition.Atmospheres
{
    // The pure maths of the empty reach of the layer march (E40 C1), in float and double; the shader (EmptyReach in ReDefinitionAtmospheresLayers.shader) is the same arithmetic and
    // the unit tests hold the two to a brute force over the map.
    //
    // A sample of the march whose map value fails the coverage bound (the shader's first test of LayerDensity: the map value plus the layer's coverage minus one plus the largest shift
    // of the shape noise plus half the soft edge is not above zero) has no density, and neither has any sample whose bilinear read of the map touches only such texels. The reach of a
    // texel is the distance (in texels) to the nearest texel that can pass the bound, less the margin that the bilinear read and the sample's place in its texel take. A march that has
    // read an empty sample leaps over the grid points that lie within the reach of it: they are empty too, the result is the same and the density reads are fewer.
    //
    // The distance is the chamfer distance with the weights 1 and sqrt 2 (two sweeps over the map, wrapping in longitude through a padding), which never falls below the Euclidean distance
    // and exceeds it by 8.24 percent at most; the reach divides by that, so that it never exceeds the Euclidean distance. Techniques: the distance field of empty-space skipping in
    // volume rendering (Hart, "Sphere Tracing", The Visual Computer 1996, applied to the map's support), the chamfer distance (Borgefors, 1986).
    internal static class EmptyReachMath
    {
        // The texels the reach keeps back: a point whose bilinear read touches no occupied texel is more than the diagonal (1.414) from every occupied centre, and a sample lies up to the
        // half diagonal (0.707) from the centre of the texel the reach was read in: 2.12, taken as 2.2.
        internal const double MarginTexels = 2.2;
        // The chamfer (1, sqrt 2) distance over the Euclidean one, at most.
        internal const double ChamferOverestimate = 1.0824;
        // The longest reach in texels (the map's 512 texels are 3.8 degrees of longitude at the equator each: 7.4 km on Kerbin; the longest ray of the pass is a few hundred km).
        internal const int MaxReach = 120;
        // The padding of the longitude wrap, in texels (the longest reach plus the chamfer's own spread).
        private const int Pad = 128;

        // The bound's value below which a texel counts as empty: the share is zero wherever the bound is not above zero (the bound is the share's argument with the noise at its highest), the
        // epsilon takes the rounding of the sums (a map value of zero comes out of them at zero within a few 1e-8) and leaves a density of 1e-5 of the edge's width unaccounted for.
        internal const float BoundEpsilon = 1e-5f;

        // Whether a texel of the map can hold density for a layer with these numbers: the shader's coverage bound (the first test of LayerDensity, after the clear-sky rule) above zero.
        internal static bool Occupied(byte value, float coverage, float noiseAmount, float edge, float clearSkyK)
        {
            float cov = value / 255f;
            cov = Math.Min(cov, 2f * cov - clearSkyK);
            float bound = cov + coverage - 1f + 0.5f * noiseAmount + 0.5f * Math.Max(edge, 1e-4f);
            return bound > BoundEpsilon;
        }

        // The table of Occupied over the 256 values of a map.
        internal static void Table(bool[] occupied, float coverage, float noiseAmount, float edge, float clearSkyK)
        {
            for (int v = 0; v < 256; v++) occupied[v] = Occupied((byte)v, coverage, noiseAmount, edge, clearSkyK);
        }

        // The reach map of a w x h map (row 0 at the north pole, column 0 at longitude -180): a texel is occupied where the map as it is or as it will be (a slew moves every texel
        // between the two) can pass the bound. `work` holds (w + 2 Pad) x h floats. The result is one byte per texel.
        internal static void Build(byte[] now, byte[] goal, int w, int h, bool[] occupied, byte[] reach, float[] work)
        {
            int pw = w + 2 * Pad;
            const float Inf = 1e9f;
            for (int y = 0; y < h; y++)
            {
                int row = y * pw, src = y * w;
                for (int x = 0; x < pw; x++)
                {
                    int sx = ((x - Pad) % w + w) % w;
                    bool on = occupied[now[src + sx]] || (goal != null && occupied[goal[src + sx]]);
                    work[row + x] = on ? 0f : Inf;
                }
            }
            const float Diagonal = 1.41421356f;
            for (int y = 0; y < h; y++)
            {
                int row = y * pw, up = row - pw;
                for (int x = 0; x < pw; x++)
                {
                    float v = work[row + x];
                    if (x > 0) v = Math.Min(v, work[row + x - 1] + 1f);
                    if (y > 0)
                    {
                        v = Math.Min(v, work[up + x] + 1f);
                        if (x > 0) v = Math.Min(v, work[up + x - 1] + Diagonal);
                        if (x < pw - 1) v = Math.Min(v, work[up + x + 1] + Diagonal);
                    }
                    work[row + x] = v;
                }
            }
            for (int y = h - 1; y >= 0; y--)
            {
                int row = y * pw, down = row + pw;
                for (int x = pw - 1; x >= 0; x--)
                {
                    float v = work[row + x];
                    if (x < pw - 1) v = Math.Min(v, work[row + x + 1] + 1f);
                    if (y < h - 1)
                    {
                        v = Math.Min(v, work[down + x] + 1f);
                        if (x < pw - 1) v = Math.Min(v, work[down + x + 1] + Diagonal);
                        if (x > 0) v = Math.Min(v, work[down + x - 1] + Diagonal);
                    }
                    work[row + x] = v;
                }
            }
            for (int y = 0; y < h; y++)
            {
                int row = y * pw + Pad, dst = y * w;
                for (int x = 0; x < w; x++)
                {
                    double d = work[row + x] / ChamferOverestimate - MarginTexels;
                    reach[dst + x] = d <= 0.0 ? (byte)0 : (byte)Math.Min(MaxReach, Math.Floor(d));
                }
            }
        }

        // The length along the ray (m) within which the map is empty around a sample whose texel has this reach (the shader's EmptyReach): the reach as an angle on the planet (a texel
        // is the angle pi / h across and 2 pi / w times the cosine of the latitude along a parallel; the cosine is that of the latitude the reach can touch), as the chord of the
        // smallest radius the ray can have, which holds for any two points of a straight line at least that far from the centre. v is the map's v (0 at the north pole).
        internal static double ReachMetres(int texels, double v, int w, int h, double radiusM)
        {
            double texelLat = Math.PI / h, texelLon = 2.0 * Math.PI / w;
            double latMax = Math.Min(Math.Abs(0.5 - v) * Math.PI + texels * texelLat, 1.5707);
            double angle = texels * Math.Min(texelLat, texelLon * Math.Cos(latMax));
            return radiusM * Math.Sin(Math.Min(angle, 1.5707));
        }
    }
}
