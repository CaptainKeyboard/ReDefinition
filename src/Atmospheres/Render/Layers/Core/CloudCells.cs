using System;

namespace ReDefinition.Atmospheres
{
    // CELLS (owner order 2026-11-17): the single clouds from the layer map's cloud share, the C# twin of unity/Assets/ReDefinitionAtmospheres/Shaders/CloudCells.cginc (the same lattice, hash,
    // table and profile; the arithmetic in double). The map (Kerbin: a texel of 7.4 km) says how much of a region is cloud; the cells put that share down as single clouds on a jittered lattice
    // of the layer's pitch (CloudLayerDef.CellPitchM). The CPU readers of the shape (the cloud shadow's twin CloudShadowShapeNoise, the tests) take the map value through CellCover as the
    // layer pass and the shadow do. The code is the project's own; a jittered lattice for cell placement is the common Worley construction (S. Worley, "A Cellular Texture Basis Function",
    // SIGGRAPH 1996).
    internal static class CloudCells
    {
        internal const double Jitter = 1.0, Sigma = 0.5, AliveF0 = 0.25, AliveMax = 0.85, EdgeX = 0.725, RMax = 0.8;

        // A4: the steepest change per noise scale of the body's first billow octave (its rank) and of the finer octaves together, the bounds the march's step outside a body stands on
        // (CloudCells.cginc CellBodyGap; _CellE.w = TraceRankSlope + cellFine x TraceFineSlope). Measured on the volumes by CellTraceSlopeTests (the rank with a margin of a quarter, the fine
        // octaves exactly from the voxels).
        // Measured 2026-11-17 over the Medium and High volumes, seeds 1 and 7: the rank 5.07, the finer octaves 20.21 per noise scale.
        internal const double TraceRankSlope = 6.5, TraceFineSlope = 20.5;

        // The visible radius of a disc in pitches for the share f = 0, 1/16 .. 1: the Monte Carlo of the lattice (CloudCellsTests measures it again against this table).
        internal static readonly double[] RadiusTable =
            { 0.0, 0.2712, 0.2728, 0.2741, 0.2994, 0.3377, 0.3747, 0.4106, 0.4462, 0.4826, 0.5209, 0.5612, 0.6061, 0.6595, 0.7262, 0.8257, 1.2573 };

        internal static double VisibleRadius(double f)
        {
            double x = Saturate(f) * 16.0;
            int i = Math.Min((int)Math.Floor(x), 15);
            return RadiusTable[i] + (RadiusTable[i + 1] - RadiusTable[i]) * (x - i);
        }

        // The share of the region that is cloud: the mean of the layer's shape over the uniform noise value without erosion (the closed form of the mean of saturate over [x0 - a/2, x0 + a/2]).
        internal static double Share(double cov, double coverage, double edge, double amount)
        {
            double x0 = (cov + coverage - 1.0) / edge + 0.5;
            double a = amount / edge;
            if (a < 1e-3) return Saturate(x0);
            return (Anti(x0 + 0.5 * a) - Anti(x0 - 0.5 * a)) / a;
        }

        private static double Anti(double x)
        {
            return x <= 0.0 ? 0.0 : x < 1.0 ? 0.5 * x * x : x - 0.5;
        }

        internal static uint Hash(uint x)
        {
            unchecked
            {
                x ^= x >> 16;
                x *= 0x7FEB352Du;
                x ^= x >> 15;
                x *= 0x846CA68Bu;
                x ^= x >> 16;
                return x;
            }
        }

        internal static double Unit(uint h)
        {
            return (h >> 8) * (1.0 / 16777216.0);
        }

        // The profile (0..1) of the nearest disc at the map place (u: longitude with the layer's shift as a share of the turn, any value; v: 0 north to 1 south), as the shader's CellProfile.
        internal static double Profile(double u, double v, double pitch, double rm, double f, uint seed, out double size, out double reachM)
        {
            double xBest, rBest;
            return Profile(u, v, pitch, rm, f, seed, out size, out reachM, out xBest, out rBest);
        }

        // The same with the nearest disc's distance over its profile radius (xBest; 1e9 where there is none) and that radius in m (rBest), as the shader's CellProfile returns them.
        internal static double Profile(double u, double v, double pitch, double rm, double f, uint seed, out double size, out double reachM, out double xBest, out double rBest)
        {
            reachM = 0.0;
            size = 1.0;
            rBest = 0.0;
            double alive = Math.Min(AliveMax, f / AliveF0);
            double rProf = VisibleRadius(f) / EdgeX * pitch;
            double rCap = RMax * pitch;
            u -= Math.Floor(u);
            double lat = (0.5 - v) * Math.PI;
            double rows = Math.Max(1.0, Math.Floor(Math.PI * rm / pitch));
            double rowH = Math.PI * rm / rows;
            double yRow = (lat + 0.5 * Math.PI) * rm / rowH;
            double jr = Math.Floor(yRow);
            double coslat = Math.Cos(lat);
            // The reach stops where a disc of a cell outside the 3 x 3 around the point could begin (the shader's outerM).
            double fy = yRow - jr;
            double outerM = (1.0 + Math.Min(fy, 1.0 - fy)) * rowH;
            double best = 1e9, reach = 1e9;
            for (int dr = -1; dr <= 1; dr++)
            {
                double j = jr + dr;
                if (j < 0.0 || j >= rows) continue;
                double cols = Math.Max(1.0, Math.Round(2.0 * Math.PI * rm * Math.Cos((j + 0.5) * rowH / rm - 0.5 * Math.PI) / pitch));
                double xCol = u * cols;
                double ci = Math.Floor(xCol);
                double fx = xCol - ci;
                outerM = Math.Min(outerM, (1.0 + Math.Min(fx, 1.0 - fx)) * 2.0 * Math.PI * rm * coslat / cols);
                for (int dc = -1; dc <= 1; dc++)
                {
                    double c = ci + dc;
                    double cw = c - cols * Math.Floor(c / cols);
                    uint h;
                    unchecked { h = Hash((uint)j * 0x9E3779B1u ^ (uint)cw * 0x85EBCA6Bu ^ seed * 0xC2B2AE35u); }
                    if (Unit(h) >= alive) continue;
                    double jx = (Unit(Hash(h ^ 0xA511E9B3u)) - 0.5) * Jitter;
                    double jy = (Unit(Hash(h ^ 0x63D83595u)) - 0.5) * Jitter;
                    double s = 1.0 + Sigma * (2.0 * Unit(Hash(h ^ 0x2C1B3C6Du)) - 1.0);
                    double dx = (u - (c + 0.5 + jx) / cols) * 2.0 * Math.PI * rm * coslat;
                    double dy = (lat - ((j + 0.5 + jy) * rowH / rm - 0.5 * Math.PI)) * rm;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    double r = Math.Min(rProf * s, rCap);
                    double x = d / r;
                    if (x < best) { best = x; size = s; rBest = r; }
                    reach = Math.Min(reach, d - r);
                }
            }
            reachM = Math.Max(Math.Min(reach, outerM - rCap), 0.0);
            xBest = best;
            return 1.0 - SmoothStep(0.45, 1.0, best);
        }

        // What the last cell read left (the shader's gCellX, gCellR, gCellCore, gCellW): the nearest disc's distance over its profile radius (1e9: none), that radius in m, the cloud's core
        // strength and the share of the map's own value there. The body (CellBody) reads them.
        internal struct CellRead
        {
            internal double XBest, RBest, Core, WMap;
            // The shader's body test: a disc was read and the cells alone draw (no closed deck, no far view).
            internal bool Body { get { return XBest >= 0.0 && WMap <= 0.0; } }
        }

        // The layer map's value at a place with the cells, as the shader's CellCover (foot: the march's sample spacing in m, 0 for a point reader; wMap: the share of the map's own value).
        internal static double Cover(double cov, double u, double v, double pitch, double rm, double coverage, double edge, double amount, double foot, uint seed, out double reachM, out double wMap)
        {
            CellRead read;
            double c = Cover(cov, u, v, pitch, rm, coverage, edge, amount, foot, seed, out reachM, out read);
            wMap = read.WMap;
            return c;
        }

        // The same with the whole read of the nearest disc.
        internal static double Cover(double cov, double u, double v, double pitch, double rm, double coverage, double edge, double amount, double foot, uint seed, out double reachM, out CellRead read)
        {
            double f = Share(cov, coverage, edge, amount);
            double size, xBest, rBest;
            double p = Profile(u, v, pitch, rm, f, seed, out size, out reachM, out xBest, out rBest);
            double core = Math.Max(0.15, Math.Min(0.5, 0.5 * (cov + coverage - 1.0) + 0.35)) * size;
            double e = p > 0.5 ? (2.0 * p - 1.0) * core : p - 0.5;
            double wMap = Math.Max(Saturate((f - 0.8) / 0.15), foot > 0.0 ? Saturate((foot - 0.25 * pitch) / (0.5 * pitch)) : 0.0);
            read.XBest = xBest;
            read.RBest = rBest;
            read.Core = core;
            read.WMap = wMap;
            double cell = 1.0 - coverage + e;
            return cell + (cov - cell) * wMap;
        }

        // The body of a single cloud at a point, as the shader's CellBody: its share, 0 outside and 1 inside, rising over the band at its surface. The body is a half ellipsoid over the type's
        // flat base (horizontal semi-axis EdgeX of the disc's profile radius, vertical the cloud's own height, a share of the type's height typeHeightM from the core strength); its depth below
        // the surface in m, (1 - the ellipsoid's radial coordinate) times the shorter semi-axis, is moved by the billows: the first octave (billow, 0..1) by up to bodyBillow of that
        // semi-axis, the finer ones (fineX, fineY, centred on 0) in proportion to their size, with the gain fineGain. h: the height share of the type at the point; band: the width in m.
        internal static double CellBody(CellRead read, double h, double billow, double fineX, double fineY, double typeHeightM, double bodyBillow, double band, double fineGain)
        {
            double top = Math.Max(0.15, Math.Min(1.0, read.Core / 0.5));
            double semiH = Math.Max(top * typeHeightM, 1.0), semiR = Math.Max(EdgeX * read.RBest, 1.0);
            double xv = read.XBest / EdgeX, hv = h / top;
            double axis = Math.Min(semiR, semiH);
            double q = (1.0 - Math.Sqrt(xv * xv + hv * hv)) * axis + bodyBillow * axis * (billow - 0.5 + fineGain * (0.37 * fineX + 0.137 * fineY));
            return Saturate(q / Math.Max(band, 1.0) + 0.5);
        }

        // The same at a planet-centred point of the map frame (x towards longitude 0, y towards 90 east, z north; m) for a layer (its map shift, pitch, coverage, edge, noise amount).
        internal static double CoverAt(double cov, double x, double y, double z, CloudLayerDef layer, double rm, uint seed)
        {
            CellRead read;
            return CoverAt(cov, x, y, z, layer, rm, seed, out read);
        }

        // The same with the whole read of the nearest disc (no disc, a closed deck: XBest 1e9 and WMap 1 where the point is not on the sphere).
        internal static double CoverAt(double cov, double x, double y, double z, CloudLayerDef layer, double rm, uint seed, out CellRead read)
        {
            double r = Math.Sqrt(x * x + y * y + z * z);
            if (!(r > 0.0))
            {
                read.XBest = 1e9;
                read.RBest = 0.0;
                read.Core = 0.0;
                read.WMap = 1.0;
                return cov;
            }
            double latDeg = Math.Asin(Math.Max(-1.0, Math.Min(1.0, z / r))) * (180.0 / Math.PI);
            double lonDeg = Math.Atan2(y, x) * (180.0 / Math.PI);
            double u = (lonDeg - layer.MapShiftDeg + 180.0) / 360.0, v = (90.0 - latDeg) / 180.0;
            double reach;
            return Cover(cov, u, v, layer.CellPitchM, rm, layer.EffectiveCoverage, Math.Max(layer.EffectiveEdgeSoftness, 1e-4f), layer.EffectiveNoiseAmount, 0.0, seed, out reach, out read);
        }

        private static double Saturate(double x)
        {
            return x <= 0.0 ? 0.0 : x >= 1.0 ? 1.0 : x;
        }

        private static double SmoothStep(double a, double b, double x)
        {
            double t = Saturate((x - a) / (b - a));
            return t * t * (3.0 - 2.0 * t);
        }
    }
}
