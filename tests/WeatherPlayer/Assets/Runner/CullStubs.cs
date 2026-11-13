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
    }
}
