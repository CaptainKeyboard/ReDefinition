namespace ReDefinition.Atmospheres
{
    // The season phase the ground uses now: the setting's forced phase if one is set, otherwise the clock.
    internal static class SeasonState
    {
        // The phase in [0, 1); null where the game has no Kerbin yet and no phase is forced. Nothing is painted for a null phase: the
        // ground keeps its colours until the clock is there, instead of showing a winter.
        internal static double? Phase()
        {
            return Choose(AtmospheresSettings.ForcePhase, SeasonClock.Current(AtmospheresSettings.YearLengthFactor));
        }

        // The forced phase when it is from 0 to 1, otherwise the clock's, otherwise null.
        internal static double? Choose(float forced, double? clock)
        {
            if (forced >= 0f && forced < 1f) return forced;
            return clock;
        }
    }
}
