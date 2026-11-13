// The tuning table of the game is not in the player (it reads the game's config nodes). CloudCoverShift (linked by tests/WeatherPlayer/linked/T16.txt because the
// layer renderer reads the coverage shift through CloudLayerDef.EffectiveCoverage) needs only these two members; the player's value is always the compiled default.
namespace ReDefinition.Atmospheres
{
    internal static class Tuning
    {
        internal static long Version
        {
            get { return 0; }
        }

        internal static float Get(string key, float compiledDefault)
        {
            return compiledDefault;
        }
    }
}
