using System;

namespace ReDefinition.Atmospheres
{
    // The length of a body's season year. A planet's year is its orbit period; a moon's seasons follow the planet it circles (the star
    // lights both), so its year is the period of the planet whose parent is the star. A year longer than a cap, counted in years of the
    // player's home planet, is cut to the cap so that a season stays playable; the factors of the node and the player then multiply it.
    internal static class BodyYear
    {
        // The cap in home years (key yearCap of the SEASONS node; 0 there means no cap).
        internal const double DefaultCapHomeYears = 3.0;

        // The source a body gets where its node names none: a moon follows its planet.
        internal static YearSourceKind DefaultSource(BodyFacts facts)
        {
            return facts != null && facts.IsMoon && facts.ParentOrbitPeriod > 0 ? YearSourceKind.ParentOrbitPeriod : YearSourceKind.OrbitPeriod;
        }

        // The year before the cap and the factors; 0 where the source gives none.
        internal static double Base(BodyFacts facts, YearSourceKind source, double secondsKey)
        {
            switch (source)
            {
                case YearSourceKind.Seconds:
                    return secondsKey > 0 ? secondsKey : 0.0;
                case YearSourceKind.ParentOrbitPeriod:
                    return facts.ParentOrbitPeriod > 0 ? facts.ParentOrbitPeriod : facts.OrbitPeriod;
                default:
                    return facts.OrbitPeriod;
            }
        }

        // The year of the body in seconds. capped tells whether the cap cut it. A source that gives none gives 0.
        internal static double Compute(BodyFacts facts, YearSourceKind source, double secondsKey, double capHomeYears, double factor, out bool capped)
        {
            capped = false;
            double year = Base(facts, source, secondsKey);
            if (!(year > 0) || double.IsInfinity(year)) return 0.0;
            if (source != YearSourceKind.Seconds && capHomeYears > 0 && facts.HomeOrbitPeriod > 0)
            {
                double cap = capHomeYears * facts.HomeOrbitPeriod;
                if (year > cap)
                {
                    year = cap;
                    capped = true;
                }
            }
            return SeasonClock.YearSeconds(year, factor);
        }
    }
}
