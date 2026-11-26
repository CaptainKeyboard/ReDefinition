using System;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The decisions of the pass's aerial perspective (E40 horizon fade), kept out of the module so that the unit tests drive them without the engine.
    //
    // Why the pass has its own: behind Scatterer's screen-space scattering (cloud.afterScatterer) the air of Scatterer is no longer applied to the cloud, and nothing replaced
    // it (K-T2d start 55: ord0, Scatterer's air on the cloud, minus ord1, none, is +0.17 / +0.20 / +0.23 in the bands 33 to 37 km away). With the pass in front of the
    // scattering (order 0) Scatterer fogs the cloud by the sea behind it, so the pass's own fog would double it and stays off there.
    //
    // The keys (ATMO_TUNING, all default to the behaviour before this package):
    //   cloud.aerialPerspective      -1: the cloudAerialPerspective setting decides, and the automatic rule applies too (AutoByDefault, the default); 0 off; 1 on; 2 automatic: on exactly where
    //                      the pass is behind Scatterer's scattering
    //   cloud.airSource    -1: Scatterer's atmosphere node of the body when Scatterer is loaded and the node reads, else the pass's own air; 0 the own air; 1 Scatterer's node
    //   cloud.aerialPerspectiveColour   0: the sun's share (CloudBridgeAir.Inscatter, the default); 1: a display colour, cloud.aerialPerspectiveR / aerialPerspectiveG / aerialPerspectiveB (0 to 1, as the camera shows it)
    //   cloud.aerialPerspectiveShare    the sun's share of the in-scatter of colour source 0 (CloudBridgeAir.HorizonShare)
    internal static class AerialPerspectivePlan
    {
        internal const string TuneAirSource = "cloud.airSource", TuneColour = "cloud.aerialPerspectiveColour", TuneShare = "cloud.aerialPerspectiveShare",
            TuneR = "cloud.aerialPerspectiveR", TuneG = "cloud.aerialPerspectiveG", TuneB = "cloud.aerialPerspectiveB";

        // Whether the aerial perspective is on by itself behind Scatterer's scattering. On by default (a visible look feature costs at most 0.05 ms and read in the right
        // direction in every measurement); cloud.aerialPerspective 0 switches it off.
        internal const bool AutoByDefault = true;

        // The display colour of colour source 1 when a start gives none: the sea's aerial perspective at the horizon row of the baseline frames of start 55 (above view, prof_b0 of
        // the order frames: 0.620 / 0.711 / 0.749).
        internal static readonly Color DefaultDisplayColour = new Color(0.620f, 0.711f, 0.749f, 1f);

        internal enum AirSource { Auto = -1, Own = 0, Scatterer = 1 }

        // The two numbers the force key and the auto key make: whether the aerial perspective is forced on (aerialPerspective), and whether it is left to the automatic rule (auto).
        // force: cloud.aerialPerspective (-1, 0, 1 or 2), setting: the cloudAerialPerspective setting.
        internal static void Resolve(float force, bool setting, out bool aerialPerspective, out bool auto)
        {
            aerialPerspective = setting;
            auto = AutoByDefault;
            if (force > 1.5f) { aerialPerspective = false; auto = true; }
            else if (force > 0f) { aerialPerspective = true; auto = false; }
            else if (force == 0f) { aerialPerspective = false; auto = false; }
        }

        // Whether the aerial perspective runs in a frame: forced or set on, or automatic with the pass behind Scatterer's scattering.
        internal static bool Active(bool aerialPerspective, bool auto, bool behindScattering)
        {
            return aerialPerspective || (auto && behindScattering);
        }

        // Whether the air is read from Scatterer's node: the key allows it and (for the automatic key) Scatterer is loaded.
        internal static bool UseScattererAir(AirSource source, bool scattererLoaded)
        {
            return source == AirSource.Scatterer || (source == AirSource.Auto && scattererLoaded);
        }

        internal static AirSource SourceFrom(float key)
        {
            return key < -0.5f ? AirSource.Auto : key > 0.5f ? AirSource.Scatterer : AirSource.Own;
        }

        // The in-scatter colour in the pass's radiance: source 0 the sun's share, source 1 the display colour decoded through the output stage (the inverse of the encoding
        // the pass applies to its colour, CloudOutputStage.Decode), so that a cloud layer that fades completely shows exactly that colour on the screen.
        internal static Color Inscatter(int source, Color sun, Color moon, Color ambient, Vector3 rayleighPerM, float share, Color display, float white, float exposure, float gamma)
        {
            return Inscatter(source, sun, moon, ambient, rayleighPerM, share, display, white, exposure, gamma, CloudBridgeAir.BlueExponent);
        }

        // The same with the blue exponent of the sun-share colour (colour source 0) as a number: the body's aerialPerspectiveBlue for the colour a far cloud fades into, aerialPerspectiveNearBlue for the in-scatter
        // over a short path (E40 T1 tint). The display colour of source 1 has no exponent.
        internal static Color Inscatter(int source, Color sun, Color moon, Color ambient, Vector3 rayleighPerM, float share, Color display, float white, float exposure, float gamma, float blueExponent)
        {
            if (source != 1) return CloudBridgeAir.Inscatter(sun, moon, ambient, rayleighPerM, share, blueExponent);
            return new Color(CloudOutputStage.Decode(display.r, white, exposure, gamma), CloudOutputStage.Decode(display.g, white, exposure, gamma),
                             CloudOutputStage.Decode(display.b, white, exposure, gamma), 1f);
        }

        // The aerial perspective of the layer shader's march, for one pixel, per channel (the CPU twin of the line at the end of the march): the cloud's premultiplied colour times the air's
        // transmittance plus its alpha times the in-scatter times what the air took. The in-scatter colour is the short-path colour near and the horizon colour far, blended by the blue
        // channel's extinction (1 - airT.z): a few kilometres of air have taken a quarter of the blue, a horizon's worth all of it.
        internal static Color AerialMix(Color premultiplied, float alpha, Vector3 airTransmittance, Color near, Color far)
        {
            return AerialMix(premultiplied, alpha, airTransmittance, near, far, 1f);
        }

        // The same with the share of the flux the air gets under the clouds (PRUEFPROTOKOLL 272, 302): the camera's diffuse share (_FarC.w, FarViewSettings.CameraLitShare); it scales the in-scatter.
        internal static Color AerialMix(Color premultiplied, float alpha, Vector3 airTransmittance, Color near, Color far, float litAir)
        {
            float w = 1f - airTransmittance.z;
            float r = (near.r + (far.r - near.r) * w) * litAir, g = (near.g + (far.g - near.g) * w) * litAir, b = (near.b + (far.b - near.b) * w) * litAir;
            return new Color(premultiplied.r * airTransmittance.x + alpha * r * (1f - airTransmittance.x), premultiplied.g * airTransmittance.y + alpha * g * (1f - airTransmittance.y),
                             premultiplied.b * airTransmittance.z + alpha * b * (1f - airTransmittance.z), premultiplied.a);
        }

        // The two-stream decay of a non-absorbing cloud's diffuse transmission, 1 / (1 + decay tau) (CloudLightMath.OtherLayersTransmission, decay 0.75 (1 - g); PRUEFPROTOKOLL 272):
        // the share of the flux the air and the scene below the clouds get (OwnCloudProbeLight.DiffuseShare of the depth overhead, PRUEFPROTOKOLL 302).
        internal const float TwoStreamDecay = 0.11f;

        // cloud.aerialPerspectiveSky (E40 H1 aerial perspective): 1 (the default) the colour a far cloud fades into is the radiance of the body's air along the cloud's own view ray (CloudHorizonSky), the short-path
        // colour the same medium's local source over its extinction; 0 the law of before (the sun's share and the Rayleigh power law of aerialPerspectiveBlue / aerialPerspectiveNearBlue, the body's aerialPerspectiveTint gain).
        internal const string TuneSky = "cloud.aerialPerspectiveSky";

        // The sun's and the moon's view of the horizon at one frame, for SkyInscatter: the camera's height above the sea, the height of the lowest layer (the ray its far clouds stand on), the
        // elevation of each light and the azimuth of the view from it, and how bright the moon's light is against the sun's per channel (zero: no moon).
        internal struct SkyView
        {
            public double HeightM, LayerAltitudeM, SunElevation, SunAzimuth, MoonElevation, MoonAzimuth;
            public Color MoonOverSun;
        }

        // The colours a far cloud fades into at the end of the air (far) and over a short path (near), in the pass's radiance, from the body's air: the sky's display value of the horizon ray
        // (Scatterer's display law, skyExposure of the node) decoded through the output stage so that a cloud layer that has faded completely shows the sky's colour on the screen, floored at the
        // ambient. False where the air gives no medium (the caller keeps the law of before). The evaluators keep the higher orders between frames.
        internal static bool SkyInscatter(CloudHorizonSky.Evaluator sun, CloudHorizonSky.Evaluator moon, CloudBridgeAir.Air air, double planetRadiusM, SkyView v, Color ambient, float white, float exposure,
                                          float gamma, out Color far, out Color near)
        {
            far = near = ambient;
            CloudHorizonSky.Medium medium = CloudHorizonSky.MediumOf(air, planetRadiusM);
            if (!medium.Valid) return false;
            double elevation = CloudHorizonSky.LayerElevation(v.HeightM, v.LayerAltitudeM, planetRadiusM);
            CloudHorizonSky.Horizon h = sun.Evaluate(medium, v.HeightM, elevation, v.SunElevation, v.SunAzimuth);
            if (v.MoonOverSun.grayscale > 0f)
            {
                CloudHorizonSky.Horizon m = moon.Evaluate(medium, v.HeightM, elevation, v.MoonElevation, v.MoonAzimuth);
                h.Far.R += v.MoonOverSun.r * m.Far.R; h.Far.G += v.MoonOverSun.g * m.Far.G; h.Far.B += v.MoonOverSun.b * m.Far.B;
                h.Near.R += v.MoonOverSun.r * m.Near.R; h.Near.G += v.MoonOverSun.g * m.Near.G; h.Near.B += v.MoonOverSun.b * m.Near.B;
            }
            double skyExposure = air.SkyExposureAt(v.HeightM);
            CloudHorizonSky.Rgb df = CloudHorizonSky.Display(h.Far, skyExposure), dn = CloudHorizonSky.Display(h.Near, skyExposure);
            far = new Color(Math.Max(ambient.r, CloudOutputStage.Decode((float)df.R, white, exposure, gamma)), Math.Max(ambient.g, CloudOutputStage.Decode((float)df.G, white, exposure, gamma)),
                            Math.Max(ambient.b, CloudOutputStage.Decode((float)df.B, white, exposure, gamma)), 1f);
            near = new Color(Math.Max(ambient.r, CloudOutputStage.Decode((float)dn.R, white, exposure, gamma)), Math.Max(ambient.g, CloudOutputStage.Decode((float)dn.G, white, exposure, gamma)),
                             Math.Max(ambient.b, CloudOutputStage.Decode((float)dn.B, white, exposure, gamma)), 1f);
            return true;
        }

        // The body's gain per channel on the colour a far cloud fades into (body keys aerialPerspectiveTintR / G / B, E40 FL far): the Rayleigh power law of aerialPerspectiveBlue is bluer and greener-poor than the horizon of
        // a sky with aerosols, so the gain moves the law's colour onto the horizon's. The gain multiplies the radiance, so the colour keeps following the sun's colour and height. One: the law as it was.
        internal static Color WithTint(Color radiance, Vector3 gain)
        {
            return new Color(radiance.r * gain.x, radiance.g * gain.y, radiance.b * gain.z, radiance.a);
        }

        // The in-scatter belongs to the air, not to the cloud's light. The output stage divides every channel of the pass by the white point's tint (the adaptation to the sun a cloud gets),
        // and the sky and the sea behind the cloud, which the aerial perspective should melt it into, are not divided: with the in-scatter in the stage too, a far cloud faded into a colour 4 to 5 percent
        // bluer than the sky it stands in front of (the tint of Kerbin's sun at a cloud is 1.04 / 0.99 / 0.95; E40 T1 tint). The radiance is multiplied by the tint so that the stage's
        // division cancels. With the stage off (gamma at or below the off value) nothing is divided and nothing is changed.
        internal static Color ExemptFromWhitePoint(Color radiance, Color whiteTint, float gamma)
        {
            if (gamma <= CloudOutputStage.OffGamma) return radiance;
            return new Color(radiance.r * Mathf.Max(whiteTint.r, 1e-3f), radiance.g * Mathf.Max(whiteTint.g, 1e-3f), radiance.b * Mathf.Max(whiteTint.b, 1e-3f), radiance.a);
        }
    }
}
