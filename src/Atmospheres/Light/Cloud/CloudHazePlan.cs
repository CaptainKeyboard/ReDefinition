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
    //   cloud.farHaze      -1: the cloudFarHaze setting decides, and the automatic rule applies too (AutoByDefault, the default); 0 off; 1 on; 2 automatic: on exactly where
    //                      the pass is behind Scatterer's scattering
    //   cloud.airSource    -1: Scatterer's atmosphere node of the body when Scatterer is loaded and the node reads, else the pass's own air; 0 the own air; 1 Scatterer's node
    //   cloud.hazeColour   0: the sun's share (CloudBridgeAir.Inscatter, the default); 1: a display colour, cloud.hazeR / hazeG / hazeB (0 to 1, as the camera shows it)
    //   cloud.hazeShare    the sun's share of the in-scatter of colour source 0 (CloudBridgeAir.HorizonShare)
    internal static class CloudHazePlan
    {
        internal const string TuneAirSource = "cloud.airSource", TuneColour = "cloud.hazeColour", TuneShare = "cloud.hazeShare",
            TuneR = "cloud.hazeR", TuneG = "cloud.hazeG", TuneB = "cloud.hazeB";

        // Whether the aerial perspective is on by itself behind Scatterer's scattering. On by default (a visible look feature costs at most 0.05 ms and read in the right
        // direction in every measurement); cloud.farHaze 0 switches it off.
        internal const bool AutoByDefault = true;

        // The display colour of colour source 1 when a start gives none: the sea's haze at the horizon row of the baseline frames of start 55 (above view, prof_b0 of
        // the order frames: 0.620 / 0.711 / 0.749).
        internal static readonly Color DefaultDisplayColour = new Color(0.620f, 0.711f, 0.749f, 1f);

        internal enum AirSource { Auto = -1, Own = 0, Scatterer = 1 }

        // The two numbers the force key and the auto key make: whether the aerial perspective is forced on (haze), and whether it is left to the automatic rule (auto).
        // force: cloud.farHaze (-1, 0, 1 or 2), setting: the cloudFarHaze setting.
        internal static void Resolve(float force, bool setting, out bool haze, out bool auto)
        {
            haze = setting;
            auto = AutoByDefault;
            if (force > 1.5f) { haze = false; auto = true; }
            else if (force > 0f) { haze = true; auto = false; }
            else if (force == 0f) { haze = false; auto = false; }
        }

        // Whether the aerial perspective runs in a frame: forced or set on, or automatic with the pass behind Scatterer's scattering.
        internal static bool Active(bool haze, bool auto, bool behindScattering)
        {
            return haze || (auto && behindScattering);
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
        // the pass applies to its colour, CloudOutputStage.Decode), so that a deck that fades completely shows exactly that colour on the screen.
        internal static Color Inscatter(int source, Color sun, Color moon, Color ambient, Vector3 rayleighPerM, float share, Color display, float white, float exposure, float gamma)
        {
            return Inscatter(source, sun, moon, ambient, rayleighPerM, share, display, white, exposure, gamma, CloudBridgeAir.BlueExponent);
        }

        // The same with the blue exponent of the sun-share colour (colour source 0) as a number: the body's hazeBlue for the colour a far cloud fades into, hazeNearBlue for the in-scatter
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
            float w = 1f - airTransmittance.z;
            float r = near.r + (far.r - near.r) * w, g = near.g + (far.g - near.g) * w, b = near.b + (far.b - near.b) * w;
            return new Color(premultiplied.r * airTransmittance.x + alpha * r * (1f - airTransmittance.x), premultiplied.g * airTransmittance.y + alpha * g * (1f - airTransmittance.y),
                             premultiplied.b * airTransmittance.z + alpha * b * (1f - airTransmittance.z), premultiplied.a);
        }

        // The in-scatter belongs to the air, not to the cloud's light. The output stage divides every channel of the pass by the white point's tint (the adaptation to the sun a cloud gets),
        // and the sky and the sea behind the cloud, which the haze should melt it into, are not divided: with the in-scatter in the stage too, a far cloud faded into a colour 4 to 5 percent
        // bluer than the sky it stands in front of (the tint of Kerbin's sun at a cloud is 1.04 / 0.99 / 0.95; E40 T1 tint). The radiance is multiplied by the tint so that the stage's
        // division cancels. With the stage off (gamma at or below the off value) nothing is divided and nothing is changed.
        internal static Color ExemptFromWhitePoint(Color radiance, Color whiteTint, float gamma)
        {
            if (gamma <= CloudOutputStage.OffGamma) return radiance;
            return new Color(radiance.r * Mathf.Max(whiteTint.r, 1e-3f), radiance.g * Mathf.Max(whiteTint.g, 1e-3f), radiance.b * Mathf.Max(whiteTint.b, 1e-3f), radiance.a);
        }
    }
}
