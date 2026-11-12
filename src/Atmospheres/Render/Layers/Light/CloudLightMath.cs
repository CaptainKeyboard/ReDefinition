using System;

namespace ReDefinition.Atmospheres
{
    // The pure maths of the own clouds' light (package T03): the phase function, the octave sum and the light march's tap layout. The shader
    // (ReDefinitionAtmospheresCloudLight.cginc) mirrors every function here; the unit tests judge these, the weather player judges the shader
    // against the same checks (tools/weather_metrics/light.py).
    //
    // Techniques (no code taken): the dual-lobe Henyey-Greenstein phase (Henyey and Greenstein 1941) with the forward lobe for the silver
    // lining; the octave multiple-scattering approximation of Wrenninge, Kulla and Lundqvist ("Oz: The Great and Volumetric", 2013) as
    // applied to clouds by Hillaire ("Physically Based Sky, Atmosphere and Cloud Rendering in Frostbite", 2016, sections 5.7 and 5.8);
    // the cone-spread taps of the light march follow Schneider ("The real-time volumetric cloudscapes of Horizon Zero Dawn", SIGGRAPH 2015),
    // powder after the same talk.
    internal static class CloudLightMath
    {
        internal const double InvFourPi = 1.0 / (4.0 * Math.PI);

        // The Henyey-Greenstein function: integrates to 1 over the sphere.
        internal static double Hg(double mu, double g)
        {
            return (1.0 - g * g) / (4.0 * Math.PI * Math.Pow(Math.Max(1.0 + g * g - 2.0 * g * mu, 1e-6), 1.5));
        }

        // The dual-lobe phase function; ecc scales both lobes' g (the octaves' c^n).
        internal static double Phase(double mu, double forwardG, double backG, double forwardWeight, double ecc)
        {
            return forwardWeight * Hg(mu, forwardG * ecc) + (1.0 - forwardWeight) * Hg(mu, backG * ecc);
        }

        // The integral of the phase function over the sphere by the trapezoid rule on mu in [-1, 1]: 2 pi times the integral of p dmu.
        internal static double PhaseIntegral(double forwardG, double backG, double forwardWeight, double ecc, int samples)
        {
            double sum = 0;
            double h = 2.0 / (samples - 1);
            for (int i = 0; i < samples; i++)
            {
                double mu = -1.0 + i * h;
                double w = (i == 0 || i == samples - 1) ? 0.5 : 1.0;
                sum += w * Phase(mu, forwardG, backG, forwardWeight, ecc);
            }
            return 2.0 * Math.PI * sum * h;
        }

        // sum over octaves n < count of  (n == 0 ? 1 : msBrightness) * b^n * Phase(mu, c^n) * exp(-tau * a^n).
        internal static double OctaveSum(double mu, double tau, int count, double a, double b, double c, double msBrightness,
                                         double forwardG, double backG, double forwardWeight)
        {
            double sum = 0, an = 1, bn = 1, cn = 1;
            int n = Math.Max(1, Math.Min(count, 4));
            for (int i = 0; i < n; i++)
            {
                sum += (i == 0 ? 1.0 : msBrightness) * bn * Phase(mu, forwardG, backG, forwardWeight, cn) * Math.Exp(-tau * an);
                an *= a; bn *= b; cn *= c;
            }
            return sum;
        }

        // The most energy the octaves can scatter, relative to a single scattering of the same unattenuated light: the sum of their weights.
        // With b <= 0.5 and msBrightness <= 1 it stays below 2.
        internal static double OctaveEnergy(int count, double b, double msBrightness)
        {
            double sum = 0, bn = 1;
            int n = Math.Max(1, Math.Min(count, 4));
            for (int i = 0; i < n; i++)
            {
                sum += (i == 0 ? 1.0 : msBrightness) * bn;
                bn *= b;
            }
            return sum;
        }

        // The edges of the light march's steps along a span of length `length` with `steps` taps whose sizes grow by `ratio`: the tap i covers
        // [edges[i], edges[i + 1]] and is read at its middle. ratio 1 gives equal steps.
        internal static double[] TapEdges(double length, int steps, double ratio)
        {
            var edges = new double[steps + 1];
            double norm = ratio == 1.0 ? steps : (Math.Pow(ratio, steps) - 1.0) / (ratio - 1.0);
            double gain = 1.0;
            for (int i = 0; i < steps; i++)
            {
                edges[i + 1] = edges[i] + length * gain / norm;
                gain *= ratio;
            }
            return edges;
        }

        // The ambient floor in the units of the source function: a share of the sun's light scattered isotropically (sun luminance / 4 pi).
        internal static double AmbientFloor(double floorShare, double sunLuminance)
        {
            return floorShare * sunLuminance * InvFourPi;
        }

        // The powder factor: 1 - exp(-2 sigma L) blended in by strength.
        internal static double Powder(double density, double lengthM, double strength)
        {
            double s = Math.Max(0.0, Math.Min(1.0, strength));
            return (1.0 - s) + s * (1.0 - Math.Exp(-2.0 * density * lengthM));
        }
    }
}
