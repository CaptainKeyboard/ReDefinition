using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Seasons
{
    // Starts ReDefinition Seasons once, when the game starts: checks the shared Common DLL, reads the settings and writes one line
    // into KSP.log.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    internal sealed class SeasonsStartup : MonoBehaviour
    {
        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            string problem = CommonVersionCheck.Problem("ReDefinition Seasons", typeof(SeasonsStartup).Assembly);
            if (problem != null) Debug.LogWarning(Log.Tag + " " + problem);
            SeasonsSettings.Load();
            Debug.Log(Log.Tag + " " + StartupLine());
        }

        // What the log says at the start: the settings, and which of the optional mods are there.
        internal static string StartupLine()
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            return "Seasons: " + (SeasonsSettings.Enabled ? "on" : "off")
                   + ", body " + SeasonClock.BodyName
                   + ", year length factor " + SeasonsSettings.YearLengthFactor.ToString(culture)
                   + ", snow strength " + SeasonsSettings.SnowStrength.ToString(culture)
                   + ", winter snow line " + SeasonsSettings.WinterSnowLine.ToString(culture) + " deg"
                   + ", storms " + (SeasonsSettings.StormsEnabled ? "on, frequency " + SeasonsSettings.StormFrequency.ToString(culture) : "off")
                   + ", Parallax " + (TypeLookup.Find("Parallax.ConfigLoader") != null ? "found" : "not found")
                   + ", EVE " + (TypeLookup.Find("Atmosphere.RaymarchedCloudsQualityManager") != null ? "found" : "not found");
        }
    }
}
