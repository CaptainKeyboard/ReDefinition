// The tuning table of the game is not in the player (it reads the game's config nodes). CloudCoverShift and CloudLookTuning (linked by tests/WeatherPlayer/linked/T16.txt because
// the layer renderer reads the coverage threshold and the look knobs through CloudLayerDef's Effective* getters) need only these members. The player's values are the compiled
// defaults unless a job sets them (LayersJob.tuning, applied by LayersSetup): the same keys the game's runtime table has, so a sweep in the player goes through the code of the game.
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    internal static class Tuning
    {
        private static readonly Dictionary<string, float> Values = new Dictionary<string, float>();
        private static long version;

        internal static long Version
        {
            get { return version; }
        }

        internal static float Get(string key, float compiledDefault)
        {
            float v;
            return Values.TryGetValue(key, out v) ? v : compiledDefault;
        }

        // A switch: a value other than 0 is true.
        internal static bool Flag(string key, bool compiledDefault)
        {
            return Get(key, compiledDefault ? 1f : 0f) != 0f;
        }

        // The player's way to set a key (the game's table swaps at the frame boundary; a job sets its keys before it draws).
        internal static void SetOverride(string key, float value)
        {
            Values[key] = value;
            version++;
        }

        internal static void ClearOverrides()
        {
            if (Values.Count == 0) return;
            Values.Clear();
            version++;
        }

        // One key back to its compiled default (the game's Tuning.ClearOverride).
        internal static void ClearOverride(string key)
        {
            if (Values.Remove(key)) version++;
        }
    }

    // The log of the atmospheres for the map store's GPU slew (LayerMapStore.GpuSlew.cs, OPT1), which the player links with the store: the game's AtmospheresLog needs the main mod's
    // Log and CompatibilityLog, which the player does not have. A line goes to the player's log.
    internal static class AtmospheresLog
    {
        internal static void Say(string message)
        {
            UnityEngine.Debug.Log("[ReDefinition] Atmospheres: " + message);
        }
    }

    // The weather modules' main-thread timing (src/Atmospheres/Infrastructure/Registry/ModuleCpuTiming.cs), which the linked layer renderer marks its upload with: the game's
    // class writes a line a second to the game's log; the player has no such report and times nothing.
    internal static class ModuleCpuTiming
    {
        internal static long Begin()
        {
            return 0L;
        }

        internal static void End(string name, long started)
        {
        }
    }

    // The cloud shadow on Scatterer's ocean (src/Atmospheres/Render/Shadow/ScattererOceanCloudShadow.cs), which the linked CloudShadowIntegration holds: the game's class sets a
    // delegate of Scatterer by reflection and declares a foreign-code contract, which the player has neither the mod nor the checker for. Here it does nothing (no Scatterer, no ocean).
    internal sealed class ScattererOceanCloudShadow
    {
        internal ScattererOceanCloudShadow(CloudShadowRenderer renderer, System.Func<bool> enabled)
        {
        }

        internal void Update()
        {
        }

        internal void Uninstall()
        {
        }
    }
}
