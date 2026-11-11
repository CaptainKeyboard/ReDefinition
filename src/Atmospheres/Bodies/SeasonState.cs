namespace ReDefinition.Atmospheres
{
    // The season phase a body uses now: the setting's forced phase if one is set, otherwise the body's own clock.
    internal static class SeasonState
    {
        // The phase in [0, 1) of a body; null where there is no body, no clock, or the game has no time yet and no phase is forced.
        // Nothing is painted for a null phase: the ground keeps its colours until the clock is there, instead of showing a winter.
        internal static double? Phase(CelestialBody body)
        {
            BodyClimate climate = Bodies.Get(body);
            return climate == null ? Choose(AtmospheresSettings.ForcePhase, null) : Phase(climate, Planetarium.fetch == null ? (double?)null : Planetarium.GetUniversalTime());
        }

        // The phase of a climate at a time (null where the game has none); the forced phase wins.
        internal static double? Phase(BodyClimate climate, double? universalTime)
        {
            double? clock = null;
            if (climate != null && climate.Seasons.Clock != SeasonClockKind.None && universalTime.HasValue)
            {
                SeasonModel model = SeasonModel.From(climate.Seasons, climate.YearSeconds, 0.0).WithYearFactor(AtmospheresSettings.YearLengthFactor);
                clock = SeasonClock.Phase(universalTime.Value, model);
            }
            return Choose(AtmospheresSettings.ForcePhase, clock);
        }

        // The phase of the first body with seasons the game has: the callers of the single-body API.
        [System.Obsolete("Use Phase(body); the callers are removed by D34 and C20.")]
        internal static double? Phase()
        {
            CelestialBody home = FlightGlobals.fetch == null || FlightGlobals.Bodies == null ? null : FlightGlobals.GetBodyByName(SeasonClock.BodyName);
            return Phase(home);
        }

        // The forced phase when it is from 0 to 1, otherwise the clock's, otherwise null.
        internal static double? Choose(float forced, double? clock)
        {
            if (forced >= 0f && forced < 1f) return forced;
            return clock;
        }
    }
}
