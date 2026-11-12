using System;

namespace ReDefinition.Atmospheres
{
    // The pure functions of the height variation (package T13), in double precision. The shader include BaseHeight.cginc writes the same
    // functions in float (RootWeight, Ragged, LocalBaseTop), so a number here is the number on the GPU; the tests and the weather player
    // compare the two.
    internal static class HeightModel
    {
        // The Magnus constants for the saturation vapour pressure over water as the WMO CIMO Guide (2008) gives them, ew = 6.112 exp(17.62 t / (243.12 + t)); a dimensionless and b in C.
        // Alduchov and Eskridge (1996) eq. 21 and Lawrence (2005) use 17.625 and 243.04; the dew point differs by under 0.02 K between the two sets (computed over -40 to 50 C, RH 10 to 100 %).
        private const double MagnusA = 17.62, MagnusB = 243.12;

        // The dew point in kelvin of air at a temperature and a relative humidity from 0 to 1 (clamped to 0.01 to 1, where the formula holds).
        internal static double DewPointK(double temperatureK, double humidity01)
        {
            double rh = Math.Max(0.01, Math.Min(1.0, humidity01));
            double tc = temperatureK - 273.15;
            double gamma = Math.Log(rh) + MagnusA * tc / (MagnusB + tc);
            return MagnusB * gamma / (MagnusA - gamma) + 273.15;
        }

        // The dew-point spread in kelvin (never negative).
        internal static double DewPointSpreadK(double temperatureK, double humidity01)
        {
            return Math.Max(0.0, temperatureK - DewPointK(temperatureK, humidity01));
        }

        // The lifting condensation level in m above the surface: about 125 m per kelvin of dew-point spread (Lawrence 2005).
        internal static double LclM(double spreadK, double metresPerKelvin)
        {
            return Math.Max(0.0, spreadK) * metresPerKelvin;
        }

        // The base offset in m the physics gives against the reference spread: the LCL of the place minus the LCL at the reference,
        // limited to either side by the tuning's maximum.
        internal static double PhysicsOffsetM(HeightTuning t, double temperatureK, double humidity01, double referenceSpreadK)
        {
            double spread = DewPointSpreadK(temperatureK, humidity01);
            double offset = LclM(spread, t.LclMPerK) - LclM(referenceSpreadK, t.LclMPerK);
            return Math.Max(-t.PhysicsMaxM, Math.Min(t.PhysicsMaxM, offset));
        }

        // The top term in m from an updraft in m/s: the gain times the updraft above the reference, within the tuning's maximum.
        internal static double TopTermM(HeightTuning t, double updraftMs)
        {
            double top = t.TopGainMPerMs * (updraftMs - t.ReferenceUpdraftMs);
            return Math.Max(-t.TopMaxM, Math.Min(t.TopMaxM, top));
        }

        // The share of a type that is rooted in the surface layer of the air (convective cloud, whose base is the condensation level):
        // 1 for a base share up to 0.1, 0 from 0.4, smooth between. Decks and cloud higher in the layer follow only the free variation.
        internal static double RootWeight(double baseShare)
        {
            double x = (baseShare - 0.1) / 0.3;
            x = x <= 0.0 ? 0.0 : x >= 1.0 ? 1.0 : x;
            return 1.0 - x * x * (3.0 - 2.0 * x);
        }

        // Smooth value noise in [0, 1) at a point in lattice units: the hash of the lattice corners (the cloud noise's NoiseHash, so the
        // shader's NzHash gives the same corner values) blended trilinearly with the quintic fade.
        internal static double ValueNoise(double x, double y, double z, uint seed)
        {
            double fx = Math.Floor(x), fy = Math.Floor(y), fz = Math.Floor(z);
            int ix = (int)fx, iy = (int)fy, iz = (int)fz;
            double ux = NoiseHash.Fade(x - fx), uy = NoiseHash.Fade(y - fy), uz = NoiseHash.Fade(z - fz);
            double acc = 0.0;
            for (int k = 0; k < 8; k++)
            {
                int ox = k & 1, oy = (k >> 1) & 1, oz = (k >> 2) & 1;
                double w = (ox != 0 ? ux : 1.0 - ux) * (oy != 0 ? uy : 1.0 - uy) * (oz != 0 ? uz : 1.0 - uz);
                acc += w * NoiseHash.Unit(NoiseHash.Hash3(ix + ox, iy + oy, iz + oz, seed));
            }
            return acc;
        }

        // The roughness of the lower boundary, -1 to 1, mean about 0: two octaves of value noise at a point of the base surface in
        // lattice units (the point is the position scaled by 1 / RaggedScaleM). The shader's Ragged is this function.
        internal static double Ragged(double x, double y, double z, uint seed)
        {
            double a = ValueNoise(x, y, z, seed) * 2.0 - 1.0;
            double b = ValueNoise(x * 2.31 + 17.0, y * 2.31 + 17.0, z * 2.31 + 17.0, seed ^ 0x5bd1e995u) * 2.0 - 1.0;
            return 0.7 * a + 0.3 * b;
        }

        // The ragged boundary at a position (relative to the planet's centre) of a layer whose inner altitude is given: the position is
        // projected onto the base sphere so that the displacement depends on the place on the ground, not on the height of the sample.
        internal static double RaggedAt(double px, double py, double pz, double baseRadiusM, double scaleM, uint seed)
        {
            double len = Math.Sqrt(px * px + py * py + pz * pz);
            if (!(len > 0.0)) return 0.0;
            double k = baseRadiusM / len / scaleM;
            return Ragged(px * k, py * k, pz * k, seed);
        }
    }
}
