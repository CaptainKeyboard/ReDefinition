using System;

namespace ReDefinition.Atmospheres
{
    // The pure maths of the own clouds' light (package T03): the phase function, the octave sum and the light march's tap layout. The shader
    // (ReDefinitionAtmospheresCloudLight.cginc) mirrors every function here; the unit tests judge these, the weather player judges the shader
    // against the same checks (tools/weather_metrics/light.py).
    //
    // Technique sources: the dual-lobe Henyey-Greenstein phase (Henyey and Greenstein 1941) with the forward lobe for the silver
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
        // msIsotropy blends the phase of the octaves above the first towards the isotropic 1 / 4 pi (light that scattered several times).
        internal static double OctaveSum(double mu, double tau, int count, double a, double b, double c, double msBrightness,
                                         double forwardG, double backG, double forwardWeight, double msIsotropy = 0.0)
        {
            double sum = 0, an = 1, bn = 1, cn = 1;
            int n = Math.Max(1, Math.Min(count, 4));
            for (int i = 0; i < n; i++)
            {
                double p = Phase(mu, forwardG, backG, forwardWeight, cn);
                if (i > 0) p += (InvFourPi - p) * msIsotropy;
                sum += (i == 0 ? 1.0 : msBrightness) * bn * p * Math.Exp(-tau * an);
                an *= a; bn *= b; cn *= c;
            }
            return sum;
        }

        // The bound on the octave energy: the source function of a perfect white diffuser lit by the sun is E / pi, which is 4 times the
        // isotropic source E / 4 pi that the weights are measured in. An octave set whose weights add up to more than this scatters more
        // than a thick, non-absorbing cloud can return (FX-T03: the earlier fit reached 6.6 and made a cloud towards the sun 2.4 times too bright).
        internal const double EnergyBound = 4.0;

        // The most energy the octaves can scatter, relative to a single scattering of the same unattenuated light: the sum of their weights.
        // With b <= 0.5 and msBrightness <= 1 it stays below 2; the fitted defaults (msBrightness 2.71, b 0.515) give 3.5, inside EnergyBound.
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

        // The share of the sun's light that reaches a point after the air above it (mirrors SunAir of the shader): Beer-Lambert with the extinction
        // tauZenith (per channel, at sea level along the zenith), the airmass of Kasten and Young (1989, Applied Optics 28: 1 / (sin h +
        // 0.50572 (h + 6.07995 deg)^-1.6364)) and the air thinning exponentially with the altitude. Zero for a sun below the horizon.
        // whiteBalance (T03v2): the frame's sun colour is the colour of the sun at the zenith above the point (the white point of the exposure); only the
        // air path beyond the zenith's reddens it, so the air mass enters as (m - 1). Without it the sun is yellow already at 35 degrees (a cloud top in
        // the sun comes out sand-beige, 0.69 blue-to-red) because the frame's colour is then counted as outside the air and a camera or an eye would
        // have removed that cast again.
        internal static double SunAir(double sinElevation, double tauZenith, double altitudeM, double scaleHeightM, bool whiteBalance = false)
        {
            if (scaleHeightM <= 0.0) return 1.0;
            double s = Math.Max(-1.0, Math.Min(1.0, sinElevation));
            double elevation = Math.Asin(s) * 180.0 / Math.PI;
            double airmass = 1.0 / (Math.Max(s, 0.0) + 0.50572 * Math.Pow(Math.Max(elevation + 6.07995, 0.05), -1.6364));
            if (whiteBalance) airmass = Math.Max(airmass - 1.0, 0.0);
            double tau = tauZenith * Math.Exp(-Math.Max(altitudeM, 0.0) / scaleHeightM);
            double t = Math.Max(0.0, Math.Min(1.0, (s + 0.02) / 0.02));
            return Math.Exp(-tau * airmass) * t * t * (3.0 - 2.0 * t);
        }

        // The deep-core diffusion term's share at optical depth tau towards the light (mirrors the shader): it builds up with the depth and then falls
        // off by 1 / (1 + decay tau), the form of a conservatively scattering slab's transmission (two-stream: T = 1 / (1 + 0.75 (1 - g) tau)), so that a
        // thick cloud is darker at its far side; the decay itself is fitted (CloudLightSettings.DiffusionDecay). decay 0 keeps the saturating term of FX-T03.
        internal static double DiffusionShare(double tau, double tauScale, double decay)
        {
            return (1.0 - Math.Exp(-tau / Math.Max(tauScale, 1e-3))) / (1.0 + Math.Max(decay, 0.0) * tau);
        }

        // The powder factor: 1 - exp(-2 sigma L) blended in by strength.
        internal static double Powder(double density, double lengthM, double strength)
        {
            double s = Math.Max(0.0, Math.Min(1.0, strength));
            return (1.0 - s) + s * (1.0 - Math.Exp(-2.0 * density * lengthM));
        }
    }
}
