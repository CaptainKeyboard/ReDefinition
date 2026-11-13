using System;

namespace ReDefinition.Atmospheres
{
    // The pure maths of the visibility culling and the distance LOD of the own cloud layers (package T16), in double precision; the shader include
    // (CloudCull.cginc) is the same arithmetic in float and the unit tests hold the two to each other. Plain numbers only (no engine types).
    //
    //   Emptiness: the coverage bound the march already uses per sample (a sample is empty where the map value plus the layer's coverage
    //   minus one plus the largest shift of the shape noise plus half the edge softness is not above zero), applied to a dilated maximum
    //   of the map over a ray bundle, so that a whole tile of rays is proved empty by a few reads.
    //   Closure: the same bound from below (the shape is one everywhere) plus a lower bound of the layer's vertical optical depth.
    //   Fades: the smooth steps by which the distance cut, the layer cap and the tap LOD end, so that nothing pops.
    //
    // Techniques: hierarchical empty-space skipping and conservative bounds in volume rendering (a coarse maximum pyramid of the density's support,
    // tested along the ray with a footprint-wide filter); the cone against sphere test is the standard one.
    internal static class CullMath
    {
        // The bound maps: the fine grid, the coarse grid (cells of the equirectangular map), and the share of a cell a footprint may use of it
        // (the bilinear read of the map reaches one texel past the footprint).
        internal const int FineW = 128, FineH = 64, CoarseW = 32, CoarseH = 16;
        internal const double CellUse = 0.85;
        // The coverage slack in map units: two steps of the 8 bit map.
        internal const double Slack = 2.0 / 255.0;
        // The tile edge in pixels of the full target, the rays sampled along a bundle and the optical depth from which a deck counts as closed.
        internal const int TilePixels = 16, BundleSamples = 12;
        internal const double ClosedTau = 6.0;

        // The shader's coverage bound (LayerDensity): a sample whose map value is `bound` is empty when this is not above zero.
        internal static double EmptyMargin(double bound, double coverage, double noiseAmount, double edge, double slack)
        {
            return bound + coverage - 1.0 + 0.5 * noiseAmount + 0.5 * Math.Max(edge, 1e-4) + slack;
        }

        internal static bool BoundIsEmpty(double bound, double coverage, double noiseAmount, double edge)
        {
            return EmptyMargin(bound, coverage, noiseAmount, edge, Slack) <= 0.0;
        }

        // True where the shape is one for every noise value, so that the density is the layer's own (the closure test, bound taken from below).
        internal static bool BoundIsSolid(double lowBound, double coverage, double noiseAmount, double edge)
        {
            return lowBound + coverage - 1.0 - 0.5 * noiseAmount - 0.5 * Math.Max(edge, 1e-4) - Slack >= 0.0;
        }

        // The cell size in rad of the level (0 fine, 1 coarse) and the level a footprint of angular radius a (rad) at latitude lat can use, or -1.
        internal static int Level(double angularRadius, double latitude)
        {
            double extent = Math.Max(angularRadius / Math.Max(Math.Cos(latitude), 0.02), angularRadius);
            if (extent <= CellUse * 2.0 * Math.PI / FineW) return 0;
            if (extent <= CellUse * 2.0 * Math.PI / CoarseW) return 1;
            return -1;
        }

        // The smooth step of a saturated argument.
        internal static double Smooth(double x)
        {
            x = Math.Min(1.0, Math.Max(0.0, x));
            return x * x * (3.0 - 2.0 * x);
        }

        // The visibility of a layer at a distance from its weight after the air: 0 where the weight is at most eps, 1 from twice eps.
        internal static double DistanceFade(double weight, double eps)
        {
            return Smooth((weight - eps) / eps);
        }

        // The weight of a layer after the air: the transmittance of the clearest channel (a layer is cut only where it is invisible in every channel).
        internal static double AirWeight(double tauR, double tauG, double tauB)
        {
            return Math.Exp(-Math.Min(tauR, Math.Min(tauG, tauB)));
        }

        // The layer cap by distance: the nearest `keep` spans are kept at any distance, the others thin out from capDistance over a quarter of it.
        internal static double CapFade(int rank, int keep, double distance, double capDistance)
        {
            if (capDistance <= 0.0 || rank < keep) return 1.0;
            return 1.0 - Smooth((distance - capDistance) / (0.25 * capDistance));
        }

        // The grown step: 1 at the span's start, growing linearly with the length along the ray.
        internal static double StepScale(double along, double growthPerM)
        {
            return 1.0 + growthPerM * Math.Max(along, 0.0);
        }

        // The factor on the stride and the cut-off of the light marches with distance: 1 up to start, factor at end.
        internal static double TapFactor(double t, double start, double end, double factor)
        {
            if (factor <= 1.0) return 1.0;
            double x = Math.Min(1.0, Math.Max(0.0, (t - start) / Math.Max(end - start, 1.0)));
            return 1.0 + (factor - 1.0) * x;
        }

        // The size of the noise footprint at a distance: never below the sample spacing.
        internal static double Foot(double t, double spacing, double pixelAngle)
        {
            return Math.Max(spacing, pixelAngle * t);
        }

        // Can any ray within the angle rho of d (o relative to the centre) reach a sphere of radius r? True when o is inside it.
        internal static bool ConeMayHit(double ox, double oy, double oz, double dx, double dy, double dz, double rho, double r)
        {
            double len = Math.Sqrt(ox * ox + oy * oy + oz * oz);
            if (len <= r) return true;
            double c = -(ox * dx + oy * dy + oz * dz) / len;
            double alpha = Math.Acos(Math.Min(1.0, Math.Max(-1.0, c)));
            double beta = Math.Asin(Math.Min(r / len, 1.0));
            return alpha - rho <= beta + 1e-4;
        }

        // The vertical optical depth every ray through a layer's full height collects at least: sigma times the thickness times the smallest
        // (top - base) share of the types times the smallest mean profile of the types. Two neighbouring types are interpolated by the march, the share
        // and the profile each linearly, so the product of the two least values bounds the interpolated column from below.
        internal static double MinVerticalTau(double sigmaPerM, double thicknessM, int types, Func<int, double> shareOfType, Func<int, double> meanProfileOfType)
        {
            if (types <= 0) return 0.0;
            double share = double.MaxValue, mean = double.MaxValue;
            for (int t = 0; t < types; t++)
            {
                share = Math.Min(share, shareOfType(t));
                mean = Math.Min(mean, meanProfileOfType(t));
            }
            return sigmaPerM * thicknessM * Math.Max(share, 0.0) * Math.Max(mean, 0.0);
        }

        // The maximum and the minimum of the texels of a w x h byte map that fall in each cell of a gw x gh grid (a texel belongs to the cell its centre
        // lies in). One pass over the map.
        internal static void Cells(byte[] map, int w, int h, int gw, int gh, byte[] max, byte[] min)
        {
            for (int i = 0; i < max.Length; i++)
            {
                max[i] = 0;
                min[i] = 255;
            }
            var column = new int[w];
            for (int x = 0; x < w; x++) column[x] = Math.Min(gw - 1, (int)((2L * x + 1) * gw / (2L * w)));
            for (int y = 0; y < h; y++)
            {
                int row = Math.Min(gh - 1, (int)((2L * y + 1) * gh / (2L * h))) * gw;
                int offset = y * w;
                for (int x = 0; x < w; x++)
                {
                    byte v = map[offset + x];
                    int k = row + column[x];
                    if (v > max[k]) max[k] = v;
                    if (v < min[k]) min[k] = v;
                }
            }
        }

        // The extreme of the cells of a coarser grid (whole blocks of an undilated grid).
        internal static byte[] Coarsen(byte[] cells, int w, int h, int gw, int gh, bool max)
        {
            var result = new byte[gw * gh];
            for (int i = 0; i < result.Length; i++) result[i] = max ? (byte)0 : (byte)255;
            for (int y = 0; y < h; y++)
            {
                int row = Math.Min(gh - 1, (int)((2L * y + 1) * gh / (2L * h))) * gw;
                for (int x = 0; x < w; x++)
                {
                    int k = row + Math.Min(gw - 1, (int)((2L * x + 1) * gw / (2L * w)));
                    byte v = cells[y * w + x];
                    if (max ? v > result[k] : v < result[k]) result[k] = v;
                }
            }
            return result;
        }

        // Every cell holds the extreme over the 3 x 3 cells around it (wrapping in x, clamped in y). A footprint within a cell around a point lies in
        // the 3 x 3 block of the point's cell.

        internal static byte[] Dilate(byte[] cell, int gw, int gh, bool max)
        {
            var result = new byte[cell.Length];
            for (int y = 0; y < gh; y++)
                for (int x = 0; x < gw; x++)
                {
                    byte best = max ? (byte)0 : (byte)255;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = Math.Min(gh - 1, Math.Max(0, y + dy));
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = (x + dx + gw) % gw;
                            byte v = cell[yy * gw + xx];
                            if (max ? v > best : v < best) best = v;
                        }
                    }
                    result[y * gw + x] = best;
                }
            return result;
        }
    }
}
