using System;
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
            try
            {
                string problem = CommonVersionCheck.Problem("ReDefinition Seasons", typeof(SeasonsStartup).Assembly);
                if (problem != null) Debug.LogWarning(Log.Tag + " " + problem);
                SeasonsSettings.Load();
            }
            catch (Exception e)
            {
                SeasonsLog.WarnOnce("the start-up checks failed and the defaults are used (" + CompatibilityLog.Reason(e) + ")");
            }
            // Each part is added on its own: one that cannot start does not keep the others from it.
            AddPart<SnowRefresher>();
            AddPart<OrbitSnow>();
            try
            {
                SeasonsLog.Say(StartupLine());
            }
            catch (Exception e)
            {
                SeasonsLog.WarnOnce("the start-up line could not be written (" + CompatibilityLog.Reason(e) + ")");
            }
        }

        private void AddPart<T>() where T : Component
        {
            try
            {
                gameObject.AddComponent<T>();
            }
            catch (Exception e)
            {
                SeasonsLog.WarnOnce(typeof(T).Name + " could not be added (" + CompatibilityLog.Reason(e) + ")");
            }
        }

        // What the log says at the start: the settings, and which of the optional mods are there.
        internal static string StartupLine()
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            return (SeasonsSettings.Enabled ? "on" : "off")
                   + ", body " + SeasonClock.BodyName
                   + ", year length factor " + SeasonsSettings.YearLengthFactor.ToString(culture)
                   + ", snow strength " + SeasonsSettings.SnowStrength.ToString(culture)
                   + ", winter snow line " + SeasonsSettings.WinterSnowLine.ToString(culture) + " deg"
                   + ", ground refresh " + EnumNames<SeasonsSettings.GroundRefresh>.Name(SeasonsSettings.GroundRefreshMode)
                   + " every " + SeasonsSettings.RefreshSeconds.ToString(culture) + " s"
                   + (SeasonsSettings.ForcePhase >= 0f ? ", FORCED PHASE " + SeasonsSettings.ForcePhase.ToString(culture) : "")
                   + ", storms " + (SeasonsSettings.StormsEnabled ? "on, frequency " + SeasonsSettings.StormFrequency.ToString(culture) : "off")
                   + ", orbit snow " + (SeasonsSettings.OrbitSnow ? "on, map width up to " + SeasonsSettings.OrbitSnowWidth : "off")
                   + ", storm sky darkening " + SeasonsSettings.StormSkyDarkening.ToString(culture)
                   + ", Parallax " + (TypeLookup.Find("Parallax.ConfigLoader") != null ? "found" : "not found")
                   + ", EVE " + (TypeLookup.Find("Atmosphere.RaymarchedCloudsQualityManager") != null ? "found" : "not found")
                   + ", EVE snow field: " + (SeasonsSettings.StormEveSnow ? EveSnowLink.Summary : "off by setting")
                   + ", EVE rain to snow: " + (SeasonsSettings.StormEveRainToSnow ? EveRainLink.Summary : "off by setting");
        }
    }
}
