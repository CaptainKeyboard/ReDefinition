using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Starts ReDefinition Atmospheres once, when the game starts: checks the shared Common DLL, reads the settings and writes one line
    // into KSP.log.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    internal sealed class AtmospheresStartup : MonoBehaviour
    {
        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            try
            {
                string problem = CommonVersionCheck.Problem("ReDefinition Atmospheres", typeof(AtmospheresStartup).Assembly);
                if (problem != null) Debug.LogWarning(Log.Tag + " " + problem);
                AtmospheresSettings.Load();
            }
            catch (Exception e)
            {
                AtmospheresLog.WarnOnce("the start-up checks failed and the defaults are used (" + CompatibilityLog.Reason(e) + ")");
            }
            // Each part is added on its own: one that cannot start does not keep the others from it.
            AddPart<SnowRefresher>();
            AddPart<OrbitSnow>();
            try
            {
                AtmospheresLog.Say(StartupLine());
            }
            catch (Exception e)
            {
                AtmospheresLog.WarnOnce("the start-up line could not be written (" + CompatibilityLog.Reason(e) + ")");
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
                AtmospheresLog.WarnOnce(typeof(T).Name + " could not be added (" + CompatibilityLog.Reason(e) + ")");
            }
        }

        // What the log says at the start: the settings, and which of the optional mods are there.
        internal static string StartupLine()
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            return (AtmospheresSettings.Enabled ? "on" : "off")
                   + ", body " + SeasonClock.BodyName
                   + ", year length factor " + AtmospheresSettings.YearLengthFactor.ToString(culture)
                   + ", snow strength " + AtmospheresSettings.SnowStrength.ToString(culture)
                   + ", winter snow line " + AtmospheresSettings.WinterSnowLine.ToString(culture) + " deg"
                   + ", ground refresh " + EnumNames<AtmospheresSettings.GroundRefresh>.Name(AtmospheresSettings.GroundRefreshMode)
                   + " every " + AtmospheresSettings.RefreshSeconds.ToString(culture) + " s"
                   + (AtmospheresSettings.ForcePhase >= 0f ? ", FORCED PHASE " + AtmospheresSettings.ForcePhase.ToString(culture) : "")
                   + ", storms " + (AtmospheresSettings.StormsEnabled ? "on, frequency " + AtmospheresSettings.StormFrequency.ToString(culture) : "off")
                   + ", orbit snow " + (AtmospheresSettings.OrbitSnow ? "on, map width up to " + AtmospheresSettings.OrbitSnowWidth : "off")
                   + ", storm sky darkening " + AtmospheresSettings.StormSkyDarkening.ToString(culture)
                   + ", Parallax " + (TypeLookup.Find("Parallax.ConfigLoader") != null ? "found" : "not found")
                   + ", EVE " + (TypeLookup.Find("Atmosphere.RaymarchedCloudsQualityManager") != null ? "found" : "not found")
                   + ", EVE snow field: " + (AtmospheresSettings.StormEveSnow ? EveSnowLink.Summary : "off by setting")
                   + ", EVE rain to snow: " + (AtmospheresSettings.StormEveRainToSnow ? EveRainLink.Summary : "off by setting");
        }
    }
}
