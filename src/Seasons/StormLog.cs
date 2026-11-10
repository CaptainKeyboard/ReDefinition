using System.Globalization;
using UnityEngine;

namespace ReDefinition.Seasons
{
    // The log lines of a storm in flight, built from plain values: StormEffect decides when, this class says what. The numbers are
    // written with the invariant culture.
    internal static class StormLog
    {
        private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

        // The line at the start of the flight scene: the storm settings in force.
        internal static string Ready()
        {
            return "storm effect ready in flight: " + (SeasonsSettings.StormsEnabled ? "storms on" : "storms off")
                   + (SeasonsSettings.ForceStorm >= 0f ? ", FORCED STORM " + SeasonsSettings.ForceStorm.ToString("0.00", Culture) : "")
                   + ", seed " + SeasonsSettings.StormSeed + ", visibility at full " + SeasonsSettings.StormVisibilityM.ToString("0", Culture)
                   + " m, veil " + SeasonsSettings.StormVeil.ToString("0.00", Culture)
                   + ", snowfall " + (SeasonsSettings.StormSnowfall ? "on" : "off")
                   + ", sky darkening " + SeasonsSettings.StormSkyDarkening.ToString("0.00", Culture)
                   + ", EVE snow " + (SeasonsSettings.StormEveSnow ? "on" : "off") + " (" + EveSnowLink.Summary + ")"
                   + ", EVE rain to snow " + (SeasonsSettings.StormEveRainToSnow ? "on (" + EveRainLink.Summary + ")" : "off")
                   + ", cells: radius " + SeasonsSettings.StormCellRadiusKm.ToString("0.#", Culture) + " km, volume "
                   + (SeasonsSettings.StormVolume ? "on" : "off") + ", up to " + SeasonsSettings.StormMaxCells
                   + (SeasonsSettings.ForceCellOn ? ", FORCED CELL at " + SeasonsSettings.ForceCellLatitude.ToString("0.00", Culture)
                       + "," + SeasonsSettings.ForceCellLongitude.ToString("0.00", Culture) : "");
        }

        // EVE's rain replaced by snow for the first time in a stretch: the rain coverage and what was raised.
        internal static string RainReplaced(float rain, int snowFields, string fieldNames, int sounds)
        {
            return "EVE rain replaced by snow: rain coverage " + rain.ToString("0.00", Culture)
                   + " written to " + snowFields + " snow field(s) (" + fieldNames + "), snow sounds raised " + sounds;
        }

        // The camera came into a snow region and EVE's rain is suppressed there.
        internal static string RainSuppressed(string described, double snowCover, double latitude, double altitudeM, int snowFields)
        {
            return "EVE rain suppressed in snow region: " + described + " (snow cover " + snowCover.ToString("0.00", Culture)
                   + " at lat " + latitude.ToString("0.0", Culture) + ", " + altitudeM.ToString("0", Culture)
                   + " m; EVE snow fields " + snowFields + ")";
        }

        // A storm cell came into range for the first time.
        internal static string CellBorn(int number, StormCells.Cell cell, double groundM)
        {
            return "storm cell " + number.ToString(Culture) + " born at lat " + cell.Latitude.ToString("0.00", Culture)
                   + " lon " + cell.Longitude.ToString("0.00", Culture) + " radius " + (cell.RadiusM / 1000).ToString("0.0", Culture)
                   + " km (" + (cell.Forced ? "forced" : "schedule") + ", peak " + cell.Height.ToString("0.00", Culture)
                   + ", age " + (cell.AgeSeconds / 60).ToString("0", Culture) + " min, ground " + groundM.ToString("0", Culture)
                   + " m (PQS height at the centre), cloud base " + cell.BaseAglM.ToString("0", Culture) + " m above it, drift "
                   + cell.SpeedMs.ToString("0.0", Culture) + " m/s toward " + Geo.ToDeg(cell.Heading).ToString("0", Culture)
                   + " deg, " + (cell.DistanceM / 1000).ToString("0.0", Culture) + " km from the camera)";
        }

        // Another mod changes the fog density while the storm runs.
        internal static string FogChangedOutside(float found, float written)
        {
            return "storm: another mod changes RenderSettings.fogDensity while the storm runs (" + found.ToString("R", Culture)
                   + " against " + written.ToString("R", Culture) + "); the storm sets it again every frame";
        }

        // The numbers of a storm that has reached its target.
        internal sealed class Settled
        {
            internal float Intensity;
            internal string Source;
            internal double VisibilityM;
            internal double VeilAlpha, SkyVeilAlpha;
            internal string Flakes;
            internal float Brightness, AmbientFactor;
            internal string AmbientScaling;
            internal int AmbientExternalWrites;
            internal string EveFields;
        }

        internal static string StormSettled(Settled s)
        {
            return "storm settled at intensity " + s.Intensity.ToString("0.00", Culture) + s.Source
                   + ": visibility " + s.VisibilityM.ToString("0", Culture) + " m, fog density "
                   + RenderSettings.fogDensity.ToString("0.00000", Culture) + " read back from RenderSettings (fog "
                   + RenderSettings.fog + ", " + RenderSettings.fogMode + "), veil alpha "
                   + s.VeilAlpha.ToString("0.00", Culture)
                   + ", sky veil alpha " + s.SkyVeilAlpha.ToString("0.00", Culture)
                   + ", flakes " + s.Flakes
                   + ", fog brightness " + s.Brightness.ToString("0.00", Culture)
                   + ", ambient factor " + s.AmbientFactor.ToString("0.00", Culture) + ": " + s.AmbientScaling
                   + " (baseline rewritten by others " + s.AmbientExternalWrites + " times)"
                   + ", EVE snow fields raised " + s.EveFields;
        }

        // The first frame of a storm: the path used, the camera, the shaders and what was there before.
        internal static string StormBegins(int number, Camera cam, string veilShader, string flakeShader, FogController fog, string eveSnowState)
        {
            return "storm " + number + " begins: path RenderSettings fog (ExponentialSquared) + sky veil + flakes, ambient light and EVE snow coverage in Camera.onPreCull, no image effect and no command buffer; camera "
                   + cam.name + " renders " + cam.actualRenderingPath + ", HDR " + cam.allowHDR + ", MSAA " + cam.allowMSAA
                   + "; veil shader " + veilShader + "; flake shader " + flakeShader
                   + "; fog before: " + (fog.SavedFog ? "on" : "off") + " " + fog.SavedMode + " " + fog.SavedDensity.ToString("0.#####", Culture)
                   + "; ambient before: " + RenderSettings.ambientMode + " " + StormAmbient.Describe()
                   + "; EVE snow field: " + eveSnowState;
        }

        // The storm is over: the fog put back (or left, where another mod owns it now).
        internal static string StormEnds(bool fogRestored)
        {
            return "storm ends: " + (fogRestored ? "fog restored to " : "fog left as another mod set it, now ") + (RenderSettings.fog ? "on" : "off") + " "
                   + RenderSettings.fogMode + " " + RenderSettings.fogDensity.ToString("0.#####", Culture) + ", ambient restored to " + StormAmbient.Describe()
                   + ", EVE snow coverage put back, veil and flakes hidden";
        }

        // The EVE snow state words of the begin line.
        internal static string EveSnowState(bool enabled, bool controllable, int fields, string names, string summary)
        {
            if (!enabled) return "off by setting";
            if (!controllable) return summary;
            return fields > 0 ? "found " + fields + " (" + names + "), raised with the storm" : "found in EVE's code but no snow field is loaded for this body";
        }
    }
}
