namespace ReDefinition.Atmospheres
{
    // What the atmospheres do for a body.
    internal enum BodyStatusKind
    {
        // Nothing: no atmosphere, no surface, or switched off. The reason says which.
        Off,
        // Cover on the ground only, no weather of its own (no EVE clouds).
        CoverOnly,
        // Weather, clouds and cover.
        Full,
        // The configuration or the probes failed; the reason says how. The body is treated as Off.
        Broken
    }

    // The status of a body with the reason where there is one. Immutable.
    internal sealed class BodyStatus
    {
        internal static readonly BodyStatus CoverOnly = new BodyStatus(BodyStatusKind.CoverOnly, "");
        internal static readonly BodyStatus Full = new BodyStatus(BodyStatusKind.Full, "");

        private BodyStatus(BodyStatusKind kind, string reason)
        {
            Kind = kind;
            Reason = reason ?? "";
        }

        internal BodyStatusKind Kind { get; }
        internal string Reason { get; }

        internal static BodyStatus Off(string reason)
        {
            return new BodyStatus(BodyStatusKind.Off, reason);
        }

        internal static BodyStatus Broken(string reason)
        {
            return new BodyStatus(BodyStatusKind.Broken, reason);
        }

        // Whether weather runs on the body (Full).
        internal bool WeatherRuns
        {
            get { return Kind == BodyStatusKind.Full; }
        }

        public override string ToString()
        {
            return Reason.Length == 0 ? Kind.ToString() : Kind + "(" + Reason + ")";
        }
    }

    // Everything the atmospheres know about one body, from its REDEFINITION_BODY node and the game's data. Immutable and built lazily
    // by the body provider (Bodies.Get); a body without a node gets the generic default. The pure functions of the other classes take a
    // BodyClimate or one of its rules as the first argument. Unity objects (PQS mods, materials, layers) are NOT held here: they live in
    // attachments owned by the component that needs them.
    internal sealed class BodyClimate
    {
        internal BodyClimate(string name, int index, double radius, double yearSeconds, double daySeconds, BodyStatus status, int seed,
            SeasonRules seasons = null, TemperatureRules temperature = null, PrecipRules precip = null, System.Collections.Generic.IEnumerable<CoverRules> covers = null,
            IceRules ice = null, CellRules cells = null, SimRules sim = null, CloudRules clouds = null, LookRules look = null, SoundRules sound = null,
            WindRules wind = null, GameplayRules gameplay = null, AuroraRules aurora = null, LightMediumRules lightMedium = null, FrostRules frost = null,
            ForecastRules forecast = null)
        {
            Name = name ?? "";
            Index = index;
            Radius = radius;
            YearSeconds = yearSeconds;
            DaySeconds = daySeconds;
            Status = status ?? BodyStatus.Off("no status");
            Seed = seed;
            Seasons = seasons ?? SeasonRules.Default;
            Temperature = temperature ?? TemperatureRules.Default;
            Precip = precip ?? PrecipRules.Default;
            Covers = SchemaList.Of(covers);
            Ice = ice ?? IceRules.Default;
            Cells = cells ?? CellRules.Default;
            Sim = sim ?? SimRules.Default;
            Clouds = clouds ?? CloudRules.Default;
            Look = look ?? LookRules.Default;
            Sound = sound ?? SoundRules.Default;
            Wind = wind ?? WindRules.Default;
            Gameplay = gameplay ?? GameplayRules.Default;
            Aurora = aurora ?? AuroraRules.Default;
            LightMedium = lightMedium ?? LightMediumRules.Default;
            Frost = frost ?? FrostRules.Default;
            Forecast = forecast ?? ForecastRules.Default;
        }

        // The body's name (CelestialBody.name) and its index in FlightGlobals.Bodies.
        internal string Name { get; }
        internal int Index { get; }
        // The radius in metres, the length of the season year and of the day in seconds of game time.
        internal double Radius { get; }
        internal double YearSeconds { get; }
        internal double DaySeconds { get; }
        internal BodyStatus Status { get; }
        // The seed of the body's weather: a hash of its name and the player's global seed (setting simSeed).
        internal int Seed { get; }

        internal SeasonRules Seasons { get; }
        internal TemperatureRules Temperature { get; }
        internal PrecipRules Precip { get; }
        internal System.Collections.Generic.IReadOnlyList<CoverRules> Covers { get; }
        internal IceRules Ice { get; }
        internal CellRules Cells { get; }
        internal SimRules Sim { get; }
        internal CloudRules Clouds { get; }
        internal LookRules Look { get; }
        internal SoundRules Sound { get; }
        internal WindRules Wind { get; }
        internal GameplayRules Gameplay { get; }
        internal AuroraRules Aurora { get; }
        internal LightMediumRules LightMedium { get; }
        internal FrostRules Frost { get; }
        internal ForecastRules Forecast { get; }

        // The seed of a body: a stable hash (FNV-1a over the name) mixed with the player's global seed. string.GetHashCode is not used, it
        // differs between runtimes.
        internal static int SeedOf(string name, int globalSeed)
        {
            unchecked
            {
                uint h = 2166136261u;
                string text = name ?? "";
                for (int i = 0; i < text.Length; i++)
                {
                    h ^= text[i];
                    h *= 16777619u;
                }
                h ^= (uint)globalSeed * 2654435761u;
                h ^= h >> 15;
                h *= 2246822519u;
                h ^= h >> 13;
                return (int)h;
            }
        }

        // The generic default of a body with no node: every rule at its default, no covers. Not Kerbin: the numbers belong to no body.
        internal static BodyClimate Generic(string name, int index, double radius, double yearSeconds, double daySeconds, BodyStatus status, int seed)
        {
            return new BodyClimate(name, index, radius, yearSeconds, daySeconds, status, seed);
        }
    }
}
