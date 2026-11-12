using System;

namespace ReDefinition.Atmospheres
{
    // The tuning keys and switches of the sea ice (package D35) and the one place that turns the settings into what is drawn. Tuning
    // keys (A01): the compiled values are the fallback; the series override them.
    internal static class IceTuning
    {
        // The switch of the near-ground overlay. RECON P10 and P11 measured that an opaque sheet at queue 2504 hides the stock and
        // Scatterer's ocean at 50 m and 500 m and left 5 km open (a dirty control), so the orbit picture is the default: the setting
        // seaIce is Orbit until the player picks OrbitAndGround. This key is the series' override of that choice (on by default, so the
        // setting decides; 0 keeps the overlay off whatever the setting says).
        internal const string GroundOverlayKey = "ice.groundOverlay";
        internal const bool GroundOverlayDefault = true;

        // The lift of the sheet along the normal, in metres (against the depth fight with the ocean), and the extra lift against wave crests.
        internal const string LiftKey = "ice.liftM";
        internal const float LiftDefault = 0.03f;
        internal const string WaveLiftKey = "ice.waveLiftM";
        internal const float WaveLiftDefault = 0.5f;

        // How far the sheet reaches (km) and where the dithered handover to the orbit picture starts (share of that).
        internal const string FarKey = "ice.farKm";
        internal const float FarDefault = 80f;
        internal const float FadeStartShare = 0.75f;

        // The camera altitude above the sea from which the sheet is not drawn (the orbit picture carries the sea), in km.
        internal const string MaxAltitudeKey = "ice.maxAltKm";
        internal const float MaxAltitudeDefault = 100f;

        // A fraction that replaces the field's on every sea of the body while it is 0 or more (the series' forced states); -1 off.
        internal const string ForceFractionKey = "ice.forceFraction";
        // A lead fraction that replaces the body's while it is 0 or more; -1 off.
        internal const string LeadOverrideKey = "ice.leadOverride";
        // 1: the module does not read the field's maps (the series asks for other times itself and must not be pulled back to the clock).
        internal const string HoldMapsKey = "ice.holdMaps";
        // 1: the sheet draws flat magenta where it is (the aid view of the series).
        internal const string AidKey = "ice.aid";

        // How often the orbit picture is made again at the earliest (real seconds) and how much game time must pass for it.
        internal const double PictureStepSeconds = 2.0;
        internal const double PictureGameStepSeconds = 1800.0;

        // The ground overlay's detail range when the setting is read as metres, and the dither lattice (cells along the radius).
        internal const float DitherCellsPerMetre = 1.0f;

        internal static bool GroundOverlayWanted
        {
            get
            {
                if (!AtmospheresSettings.Enabled || !AtmospheresSettings.WeatherEffects) return false;
                SeaIceMode mode = AtmospheresSettings.SeaIce;
                if (mode != SeaIceMode.OrbitAndGround && mode != SeaIceMode.Physical) return false;
                return Tuning.Flag(GroundOverlayKey, GroundOverlayDefault);
            }
        }

        internal static bool OrbitWanted
        {
            get
            {
                if (!AtmospheresSettings.Enabled || !AtmospheresSettings.WeatherEffects) return false;
                return AtmospheresSettings.SeaIce != SeaIceMode.Off;
            }
        }

        // The fraction forced on the sea, or a negative number.
        internal static float ForcedFraction
        {
            get
            {
                float v = Tuning.Get(ForceFractionKey, -1f);
                return v >= 0f ? Math.Min(1f, v) : -1f;
            }
        }
    }
}
