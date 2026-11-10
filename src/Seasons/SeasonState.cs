namespace ReDefinition.Seasons
{
    // The season phase the ground uses now: the setting's forced phase if one is set, otherwise the clock.
    internal static class SeasonState
    {
        // The phase in [0, 1); 0 where the game has no Kerbin yet.
        internal static double Phase()
        {
            return Choose(SeasonsSettings.ForcePhase, SeasonClock.Current(SeasonsSettings.YearLengthFactor));
        }

        // The forced phase when it is from 0 to 1, otherwise the clock's, otherwise 0.
        internal static double Choose(float forced, double? clock)
        {
            if (forced >= 0f && forced < 1f) return forced;
            return clock ?? 0;
        }
    }
}
