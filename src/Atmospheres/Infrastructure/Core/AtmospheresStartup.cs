using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Starts ReDefinition Atmospheres once, when the game starts: checks the main mod (HostGate), checks the shared Common DLL, reads the settings, adds the body watcher
    // and the module runner (the modules of the other packages start from there), and writes one line into KSP.log.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    internal sealed class AtmospheresStartup : MonoBehaviour
    {
        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            // The main mod is required: without it, or with an Api that is too old or incomplete, nothing of the atmospheres starts.
            Begin(HostGate.Problem, message => Debug.LogError(message), StartParts);
        }

        // The gate in front of the start: where the host's check names a problem, one error line is written and the parts are not
        // started. Returns whether they were.
        internal static bool Begin(Func<string> hostProblem, Action<string> error, Action startParts)
        {
            if (!HostGate.Allows(hostProblem(), error)) return false;
            startParts();
            return true;
        }

        private void StartParts()
        {
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
            AddPart<BodyWatcher>();
            AddPart<ModuleRunner>();
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
                   + ", weather effects " + (AtmospheresSettings.WeatherEffects ? "on" : "off")
                   + ", cloud source " + AtmospheresSettings.CloudSource
                   + ", year length factor " + AtmospheresSettings.YearLengthFactor.ToString(culture)
                   + ", cover strength " + AtmospheresSettings.CoverStrength.ToString(culture)
                   + (AtmospheresSettings.ForcePhase >= 0f ? ", FORCED PHASE " + AtmospheresSettings.ForcePhase.ToString(culture) : "")
                   + (AtmospheresSettings.ForceLevel >= 0f ? ", FORCED LEVEL " + AtmospheresSettings.ForceLevel.ToString(culture) : "")
                   + ", heavy share " + AtmospheresSettings.HeavyShare.ToString(culture)
                   + ", orbit overlay " + (AtmospheresSettings.OrbitOverlay ? "on, map width up to " + AtmospheresSettings.OrbitMapWidth : "off")
                   + ", sky dimming " + AtmospheresSettings.SkyDimming.ToString(culture)
                   + ", Parallax " + (TypeLookup.Find("Parallax.ConfigLoader") != null ? "found" : "not found")
                   + ", EVE " + (TypeLookup.Find("Atmosphere.RaymarchedCloudsQualityManager") != null ? "found" : "not found")
                   + ", EVE snow field: " + EveSnowLink.Summary
                   + ", EVE rain to snow: " + (AtmospheresSettings.TypeByTemperature ? EveRainLink.Summary : "off by setting");
        }
    }
}
