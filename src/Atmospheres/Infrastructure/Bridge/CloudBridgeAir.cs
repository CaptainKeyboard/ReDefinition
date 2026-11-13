using System;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The air between the camera and the clouds of a body, from the body's own atmosphere data (package T09, aerial perspective, first
    // version "own extinction and in-scatter"): the extinction coefficients and scale heights the far view's haze (FarViewSettings) takes,
    // and the colour of the in-scattered light, which follows the sky: the sun's colour at the camera (reddened at sunset), the light the
    // scene keeps at night, the moons' light. Scatterer's own cloud shader is never the renderer; where Scatterer draws the sky its sun colour
    // already carries the air (see OwnCloudModule.SunColourIsReddened) and its atmosphere node of the body supplies the coefficients (FromScatterer,
    // read by ScattererAirReader), so that the pass's aerial perspective has the optical depth of the air Scatterer draws.
    //
    // The coefficients are the sea-level values of Earth's air (Hillaire, EGSR 2020, as FarViewSettings names them) scaled by the density of the
    // body's air against Earth's: Rayleigh and the aerosol share are both proportional to the number density of the gas, so a thinner or a
    // denser atmosphere scales them. The composition of the gas is not read (a body whose air is not nitrogen and oxygen scatters differently):
    // [VERIFY] against a body with another air before it is shipped for one.
    internal static class CloudBridgeAir
    {
        // The density of Earth's air at sea level in kg/m3 (the reference of the scaling), and the gas constant in J/(mol K).
        internal const double EarthDensity = 1.225, GasConstant = 8.314462618;
        // The Earth values of the far view's settings, the scale heights of the two species' ratio, and the share of the horizon radiance the air
        // in-scatters (the old constant Inscatter 0.62, 0.68, 0.76 against the sun colour 3.0, 2.9, 2.7 of the parity harness: mean 0.23).
        internal static readonly Vector3 EarthRayleighPerM = new Vector3(5.802e-6f, 13.558e-6f, 33.1e-6f);
        internal const float EarthMiePerM = 4.44e-6f, MieToRayleighHeight = 1200f / 8000f, HorizonShare = 0.23f;
        // How much bluer than grey the in-scatter is: the exponent of the Rayleigh coefficient's ratio to its mean.
        internal const float BlueExponent = 0.35f;

        // The numbers of the air the haze takes.
        internal struct Air
        {
            public Vector3 RayleighPerM;
            public float MiePerM, RayleighHeightM, MieHeightM;
            public bool Valid;
        }

        // The air of a body: the sea-level pressure in Pa, temperature in K and molar mass in kg/mol, and the pressure at a second height
        // (pressureAtHeightPa at heightM above the sea level) from which the scale height follows. Invalid where the data cannot describe an air.
        internal static Air From(double seaPressurePa, double seaTemperatureK, double molarMassKgMol, double pressureAtHeightPa, double heightM)
        {
            var a = new Air();
            if (!(seaPressurePa > 0.0) || !(seaTemperatureK > 0.0) || !(molarMassKgMol > 0.0)) return a;
            double density = seaPressurePa * molarMassKgMol / (GasConstant * seaTemperatureK);
            double scale = density / EarthDensity;
            double height = 0.0;
            if (heightM > 0.0 && pressureAtHeightPa > 0.0 && pressureAtHeightPa < seaPressurePa)
                height = heightM / Math.Log(seaPressurePa / pressureAtHeightPa);
            if (!(height > 1.0)) height = GasConstant * seaTemperatureK / (molarMassKgMol * 9.81);
            a.RayleighPerM = EarthRayleighPerM * (float)scale;
            a.MiePerM = EarthMiePerM * (float)scale;
            a.RayleighHeightM = (float)height;
            a.MieHeightM = (float)(height * MieToRayleighHeight);
            a.Valid = true;
            return a;
        }

        // The air of a body from Scatterer's atmosphere node (E40 horizon fade): m_betaR is Rayleigh scattering per km (Rayleigh extinction equals it), BETA_MSca the
        // Mie scattering per km and Scatterer divides it by 0.9 for the extinction (SkyNode: betaMEx = BETA_MSca / 0.9; Bruneton and Neyret's value), HR and HM the
        // scale heights in km. The far view has one grey aerosol species, so the Mie extinction is the mean of the three channels. Ozone absorption (a thin layer
        // at 20 km, a few percent of the optical depth of a slant path through the lower air) is not part of the model. Invalid for non-finite or non-positive values.
        internal const float ScattererMieExtinctionDivisor = 0.9f;

        internal static Air FromScatterer(Vector3 betaRPerKm, Vector3 betaMScaPerKm, float rayleighHeightKm, float mieHeightKm)
        {
            var a = new Air();
            float mie = (betaMScaPerKm.x + betaMScaPerKm.y + betaMScaPerKm.z) / 3f;
            if (!Positive(betaRPerKm.x) || !Positive(betaRPerKm.y) || !Positive(betaRPerKm.z) || !Positive(mie) || !Positive(rayleighHeightKm) || !Positive(mieHeightKm)) return a;
            a.RayleighPerM = betaRPerKm / 1000f;
            a.MiePerM = mie / ScattererMieExtinctionDivisor / 1000f;
            a.RayleighHeightM = rayleighHeightKm * 1000f;
            a.MieHeightM = mieHeightKm * 1000f;
            a.Valid = true;
            return a;
        }

        private static bool Positive(float v)
        {
            return v > 0f && !float.IsInfinity(v);
        }

        // Writes the air into the far view's settings (the switches and the strength stay as the caller set them).
        internal static void Apply(FarViewSettings far, Air air)
        {
            if (!air.Valid) return;
            far.RayleighPerM = air.RayleighPerM;
            far.MiePerM = air.MiePerM;
            far.RayleighHeightM = air.RayleighHeightM;
            far.MieHeightM = air.MieHeightM;
        }

        // The colour of the in-scattered light of the air at the horizon, in the units of the frame's radiance. sun is the sun's colour as the
        // clouds are lit by it (reddened where the air path is long), moon the colour the moons and planets add (zero where none), ambient the
        // scene's own light at night. The blue share follows the air's Rayleigh coefficient; the result never falls below the ambient, so a
        // night sky does not turn the haze black or let it keep the day's colour.
        internal static Color Inscatter(Color sun, Color moon, Color ambient, Vector3 rayleighPerM)
        {
            return Inscatter(sun, moon, ambient, rayleighPerM, HorizonShare);
        }

        // The same with the horizon share as a number (cloud.hazeShare).
        internal static Color Inscatter(Color sun, Color moon, Color ambient, Vector3 rayleighPerM, float share)
        {
            float mean = (rayleighPerM.x + rayleighPerM.y + rayleighPerM.z) / 3f;
            float r = Tint(rayleighPerM.x, mean), g = Tint(rayleighPerM.y, mean), b = Tint(rayleighPerM.z, mean);
            float lr = (sun.r + moon.r) * share * r, lg = (sun.g + moon.g) * share * g, lb = (sun.b + moon.b) * share * b;
            return new Color(Math.Max(lr, ambient.r), Math.Max(lg, ambient.g), Math.Max(lb, ambient.b), 1f);
        }

        private static float Tint(float coefficient, float mean)
        {
            return mean > 0f ? (float)Math.Pow(Math.Max(coefficient, 1e-9f) / mean, BlueExponent) : 1f;
        }
    }
}
