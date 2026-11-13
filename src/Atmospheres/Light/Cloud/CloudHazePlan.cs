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
    //   cloud.farHaze      -1: the cloudFarHaze setting decides, and AutoByDefault says whether the automatic rule applies too (the default); 0 off; 1 on; 2 automatic: on exactly where
    //                      the pass is behind Scatterer's scattering
    //   cloud.airSource    -1: Scatterer's atmosphere node of the body when Scatterer is loaded and the node reads, else the pass's own air; 0 the own air; 1 Scatterer's node
    //   cloud.hazeColour   0: the sun's share (CloudBridgeAir.Inscatter, the default); 1: a display colour, cloud.hazeR / hazeG / hazeB (0 to 1, as the camera shows it)
    //   cloud.hazeShare    the sun's share of the in-scatter of colour source 0 (CloudBridgeAir.HorizonShare)
    internal static class CloudHazePlan
    {
        internal const string TuneAirSource = "cloud.airSource", TuneColour = "cloud.hazeColour", TuneShare = "cloud.hazeShare",
            TuneR = "cloud.hazeR", TuneG = "cloud.hazeG", TuneB = "cloud.hazeB";

        // Whether the aerial perspective is on by itself behind Scatterer's scattering. Off until the game rows of the horizon fade (K-T2d start 56, the frames hhA /
        // hhA2 / hhC against ord0) are inside their numbers: the offline checks do not reach the game's pixels.
        internal const bool AutoByDefault = false;

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
            if (source != 1) return CloudBridgeAir.Inscatter(sun, moon, ambient, rayleighPerM, share);
            return new Color(CloudOutputStage.Decode(display.r, white, exposure, gamma), CloudOutputStage.Decode(display.g, white, exposure, gamma),
                             CloudOutputStage.Decode(display.b, white, exposure, gamma), 1f);
        }
    }
}
