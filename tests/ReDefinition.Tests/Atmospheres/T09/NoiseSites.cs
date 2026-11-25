using System;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The noise parameters of a layer of the default seed and the places of the sites that TopClosureTests and SoftDensityExplore sample.
    internal static class NoiseSites
    {
        // The layer's noise at its index under seed 1: the product's base offset and layer seed, untiled and curled, on a Kerbin-sized sphere.
        internal static NoiseLayerParams ParamsOf(CloudLayerDef layer, int index, double scaleM = 0.0)
        {
            double bx, by, bz;
            NoiseSeeds.BaseOffset(1u, index, out bx, out by, out bz);
            return new NoiseLayerParams
            {
                ScaleM = scaleM > 0 ? scaleM : layer.EffectiveNoiseScaleM, Untile = true, Curl = true, OffsetX = bx, OffsetY = by, OffsetZ = bz,
                Seed = NoiseSeeds.LayerSeed(1u, index), SphereScale = 1.0, ReferenceRadiusM = 600000.0
            };
        }

        // Moves the noise lattice to a site: the three offsets (a place on the planet cuts the periodic volume at its own height as well as its own longitude and latitude: a patch at one
        // radius sees one slice of the volume, so sites that only moved sideways would all see the same slice).
        internal static void Place(ref NoiseLayerParams prm, int site)
        {
            // The sites are a Kronecker sequence over one period of the periodic volume (the plastic-number constants), so that n sites cover the volume's three phases evenly: a patch of the
            // planet sees ONE slice of the volume, and a volume has only a handful of independent slices per period; random offsets leave the mean of 60 sites 0.05 to 0.10 off.
            double period = NoiseModel.ShapePeriodScales * prm.ScaleM;
            prm.OffsetX += Frac(site * 0.8191725134 + 0.1) * period;
            prm.OffsetY += Frac(site * 0.6710436067 + 0.2) * period;
            prm.OffsetZ += Frac(site * 0.5497004779 + 0.3) * period;
        }

        private static double Frac(double x)
        {
            return x - Math.Floor(x);
        }
    }
}
