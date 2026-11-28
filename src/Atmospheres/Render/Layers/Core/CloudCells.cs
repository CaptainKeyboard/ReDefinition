using System;

namespace ReDefinition.Atmospheres
{
    // THE CLOUDS' FORMS (owner orders CELLS 2026-11-17 and CLOUD FIELD 2026-11-19): the body of a cloud of a type with a form, the C# twin of
    // unity/Assets/ReDefinitionAtmospheres/Shaders/CloudCells.cginc (the same share, hash and body; the arithmetic in double). The map (Kerbin: a texel of 7.4 km) says how much of a region is
    // cloud; the cloud field puts that share down as clouds (CloudField.Cloud, the twin of the shader's FieldCloud), and each cloud is a body (CellBody). The CPU readers of the shape (the cloud
    // shadow's twin CloudShadowShapeNoise, the tests) read them as the layer pass and the shadow do. The code is the project's own. (The first placement, a jittered lattice of discs, was a
    // test tool since the field and went with master plan 47.U stage 1.)
    internal static class CloudCells
    {
        // The body's numbers are carried in the units the first placement had: a body's horizontal coordinate times EdgeX and its horizontal semi-axis over EdgeX (CellRead.XBest, .RBest).
        internal const double EdgeX = 0.725;

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

        // The lowbias32 integer mixer (Chris Wellons, hash-prospector, The Unlicense; docs/credits-and-licences.md): CloudField's PhaseHash calls it for the phases of the
        // field's spectral synthesis.
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

        // What the field's read left (the shader's gCellX, gCellR, gCellCore): the point's horizontal coordinate in the body times EdgeX (below 0: no body), the body's horizontal
        // semi-axis over EdgeX in m and the cloud's core strength. The body (CellBody) reads them.
        // Hv (the shader's gCellHv): the point's height in the body, 0 its base and 1 its surface at the body's middle, by the type's OUTLINE (CloudField.Body): the height over the
        // body's top where the type has no outline curve (the half ellipsoid), the curve's equivalent height where it has one. Top: the body's top as a share of the type's height.
        internal struct CellRead
        {
            internal double XBest, RBest, Core, Hv, Top;
            // The shader's body test: a body was read.
            internal bool Body { get { return XBest >= 0.0; } }
        }

        // The body of a cloud at a point, as the shader's CellBody: its share, 0 outside and 1 inside, rising over the band at its surface. The body is a half ellipsoid over the type's
        // flat base (horizontal semi-axis EdgeX times RBest, vertical the cloud's own height, a share of the type's height typeHeightM from the core strength); its depth below
        // the surface in m, (1 - the ellipsoid's radial coordinate) times the shorter semi-axis, is moved by the bulges: the first octave (bulge, 0..1) by up to bodyBulge of that
        // semi-axis, the finer ones (fineX, fineY, centred on 0) in proportion to their size, with the gain fineGain. The point's height in the body is the read's (Hv: the type's outline);
        // band: the width in m.
        internal static double CellBody(CellRead read, double bulge, double fineX, double fineY, double typeHeightM, double bodyBulge, double band, double fineGain)
        {
            double top = Math.Max(0.15, Math.Min(1.0, read.Core / 0.5));
            double semiH = Math.Max(top * typeHeightM, 1.0), semiR = Math.Max(EdgeX * read.RBest, 1.0);
            double xv = read.XBest / EdgeX, hv = read.Hv;
            double axis = Math.Min(semiR, semiH);
            double q = (1.0 - Math.Sqrt(xv * xv + hv * hv)) * axis + bodyBulge * axis * (bulge - 0.5 + fineGain * (0.37 * fineX + 0.137 * fineY));
            return Saturate(q / Math.Max(band, 1.0) + 0.5);
        }

        private static double Saturate(double x)
        {
            return x <= 0.0 ? 0.0 : x >= 1.0 ? 1.0 : x;
        }
    }
}
