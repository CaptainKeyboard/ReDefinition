using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // Where the climate of a body comes from. The default provider gives a generic climate; the body configuration package replaces it
    // with the one that reads REDEFINITION_BODY nodes.
    internal interface IBodyProvider
    {
        // The climate of a body, built on first use and then cached; null for a null body.
        BodyClimate Get(CelestialBody body);
    }

    // The climates of the bodies. Pure data per body; only the active body has heavy attachments elsewhere.
    internal static class Bodies
    {
        private static IBodyProvider provider = new DefaultBodyProvider();

        // The provider; assigning null puts the default back.
        internal static IBodyProvider Provider
        {
            get { return provider; }
            set { provider = value ?? new DefaultBodyProvider(); }
        }

        // The climate of a body.
        internal static BodyClimate Get(CelestialBody body)
        {
            return provider.Get(body);
        }

        // The climate of the active body (BodyWatcher), or null where there is none.
        internal static BodyClimate Active
        {
            get { return provider.Get(BodyWatcher.ActiveBody); }
        }
    }

    // The generic climate for any body: a body with an atmosphere is Full, one without is Off(NoAtmosphere); every rule is the rule's
    // default. It reads the game only for the name, index, radius, rotation and orbit period.
    internal sealed class DefaultBodyProvider : IBodyProvider
    {
        private readonly Dictionary<string, BodyClimate> cache = new Dictionary<string, BodyClimate>();

        public BodyClimate Get(CelestialBody body)
        {
            if (body == null) return null;
            BodyClimate climate;
            if (cache.TryGetValue(body.name, out climate)) return climate;
            double year = 0.0;
            try
            {
                if (body.orbit != null) year = body.orbit.period;
            }
            catch (Exception)
            {
                // A body whose orbit cannot be read has no year.
            }
            BodyStatus status = body.atmosphere ? BodyStatus.Full : BodyStatus.Off("NoAtmosphere");
            climate = BodyClimate.Generic(body.name, body.flightGlobalsIndex, body.Radius, year, body.rotationPeriod, status,
                BodyClimate.SeedOf(body.name, AtmospheresSettings.SimSeed));
            cache[body.name] = climate;
            return climate;
        }
    }
}
