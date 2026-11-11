using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The body facts the legacy field needs, without the game's objects.
    internal struct LegacyBody
    {
        internal string Name;
        internal double Radius;
        // The length of a season year in seconds (SeasonClock.YearSeconds).
        internal double YearSeconds;
    }

    // Looks a body up by its index; false for a body the seasons do not apply to.
    internal delegate bool LegacyBodyLookup(int bodyIndex, out LegacyBody body);

    // The weather field that keeps the main branch behaving as before the simulation lands: a thin IWeatherField over the EXISTING
    // storm schedule (StormCells, StormModel) and snow line (SnowMask). It reports the precipitation level of the storm cell at a place
    // as snow, the cloud cover as that level, and the ground snow as a latitude-only map; it has no wind, no temperature and no other
    // maps. It does NOT write WeatherView (the frame driver of package C20 is the single writer, and adapts StormEffect), and it
    // injects nothing into EVE: the existing EveLayerStorm path runs as before.
    //
    // This folder (Bridge/Legacy) passes to package C25 (to C26 in the fallback case) when A02 is merged, and that package deletes it
    // together with the storm files it wraps.
    internal sealed class LegacyField : IWeatherField
    {
        // The step of the legacy field's version, in seconds of game time.
        internal const double VersionStepSeconds = 60.0;
        // The depth in metres a fully covered ground reports in the SnowCover map. [VERIFY] The legacy cover has no depth; this
        // scales its 0..1 coverage to a plausible figure for consumers of the map.
        internal const double FullCoverDepthM = 0.30;
        // The size of the SnowCover map (latitude rows, longitude columns).
        internal const int MapWidth = 128;
        internal const int MapHeight = 64;

        private readonly LegacyBodyLookup bodies;
        private readonly Func<LegacyBody, StormCells.Rules> rulesFor;
        private readonly Func<long> definition;
        private readonly StormCells.Cell[] cells = new StormCells.Cell[StormCells.Capacity];
        private long bucket = long.MinValue;
        private long version;

        // bodies and rulesFor are the game's by default (LegacyBridge.GameBodies and LegacyBridge.GameRules); a test passes its own.
        internal LegacyField(LegacyBodyLookup bodies, Func<LegacyBody, StormCells.Rules> rulesFor, Func<long> definition)
        {
            if (bodies == null) throw new ArgumentNullException("bodies");
            if (rulesFor == null) throw new ArgumentNullException("rulesFor");
            this.bodies = bodies;
            this.rulesFor = rulesFor;
            this.definition = definition ?? (() => 0L);
        }

        public bool Sample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample)
        {
            sample = WeatherSample.Clear(bodyIndex);
            LegacyBody body;
            if (!bodies(bodyIndex, out body)) return false;
            Touch(ut);
            StormCells.Rules rules = rulesFor(body);
            int count = StormCells.Collect(cells, ut, latDeg, lonDeg, 1000.0, rules);
            double intensity = StormCells.IntensityAt(cells, count, latDeg, lonDeg, body.Radius);
            if (!(intensity > 0)) return true;
            float level = (float)(intensity > 1 ? 1 : intensity);
            sample.Level01 = level;
            sample.Type = PrecipType.Snow;
            sample.SnowFraction = 1f;
            sample.CloudCover01 = level;
            return true;
        }

        public bool GetMap(int bodyIndex, WeatherMapKind kind, double ut, WeatherMap target)
        {
            if (target == null) return false;
            target.Resize(MapWidth, MapHeight);
            Array.Clear(target.Values, 0, target.Values.Length);
            target.Kind = kind;
            target.BodyIndex = bodyIndex;
            target.Ut = ut;
            target.Readiness = FieldReadiness.Ready;
            target.Approximate = true;
            LegacyBody body;
            if (kind != WeatherMapKind.SnowCover || !bodies(bodyIndex, out body))
            {
                target.Version = version;
                return false;
            }
            Touch(ut);
            target.Version = version;
            StormCells.Rules rules = rulesFor(body);
            double phase = rules.ForcedPhase >= 0 && rules.ForcedPhase < 1 ? rules.ForcedPhase : SeasonClock.Phase(ut, body.YearSeconds);
            for (int y = 0; y < MapHeight; y++)
            {
                double lat = 90.0 - (y + 0.5) * 180.0 / MapHeight;
                float depth = (float)(SnowMask.Coverage(lat, 0, phase, 0, rules.WinterLine) * FullCoverDepthM);
                for (int x = 0; x < MapWidth; x++) target.Values[y * MapWidth + x] = depth;
            }
            return true;
        }

        public bool FillWindow(int bodyIndex, double centreLatDeg, double centreLonDeg, double radiusKm, double ut, WeatherWindow target)
        {
            return false;
        }

        public void Advance(double budgetMs)
        {
        }

        // Rises when the game time moves into a new minute that was asked about.
        private void Touch(double ut)
        {
            if (double.IsNaN(ut) || double.IsInfinity(ut)) return;
            long now = (long)Math.Floor(ut / VersionStepSeconds);
            if (now == bucket) return;
            bucket = now;
            version++;
        }

        public long Version { get { return version; } }
        public long DefinitionId { get { return definition(); } }
        public bool ThreadSafe { get { return false; } }
        public double StepSeconds { get { return VersionStepSeconds; } }
        public FieldReadiness Readiness { get { return FieldReadiness.Ready; } }
    }

    // Puts the legacy field into WeatherHub.Field when nothing else did, and takes it out again.
    internal static class LegacyBridge
    {
        private static LegacyField installed;

        // The legacy field is in the hub.
        internal static bool Installed
        {
            get { return installed != null && ReferenceEquals(WeatherHub.Field, installed); }
        }

        // Installs the legacy field over the game's bodies and settings. Does nothing where another field is already assigned.
        internal static bool Install()
        {
            return Install(new LegacyField(GameBodies, GameRules, GameDefinition));
        }

        // Installs a given legacy field (a test passes its own).
        internal static bool Install(LegacyField field)
        {
            if (!(WeatherHub.Field is NullField)) return false;
            installed = field;
            WeatherHub.Field = field;
            return true;
        }

        // Removes the legacy field if it is the one in the hub.
        internal static void Uninstall()
        {
            if (installed != null && ReferenceEquals(WeatherHub.Field, installed)) WeatherHub.Field = null;
            installed = null;
        }

        // The game's bodies: the ones the seasons apply to (Kerbin) that have an atmosphere and an orbit.
        internal static bool GameBodies(int bodyIndex, out LegacyBody body)
        {
            body = new LegacyBody();
            if (FlightGlobals.Bodies == null || bodyIndex < 0 || bodyIndex >= FlightGlobals.Bodies.Count) return false;
            CelestialBody b = FlightGlobals.Bodies[bodyIndex];
            if (b == null || !SeasonClock.Applies(b.name) || !b.atmosphere || b.orbit == null) return false;
            body.Name = b.name;
            body.Radius = b.Radius;
            body.YearSeconds = SeasonClock.YearSeconds(b.orbit.period, AtmospheresSettings.YearLengthFactor);
            return true;
        }

        // The storm rules the existing effect builds from the settings. They are read from the legacy settings shims, which are
        // [Obsolete] on purpose: this folder is deleted with them.
#pragma warning disable CS0618
        internal static StormCells.Rules GameRules(LegacyBody body)
        {
            return new StormCells.Rules
            {
                BodyRadius = body.Radius,
                YearSeconds = body.YearSeconds,
                ForcedPhase = AtmospheresSettings.ForcePhase,
                WinterLine = AtmospheresSettings.WinterSnowLine,
                Frequency = AtmospheresSettings.StormFrequency,
                RadiusKm = AtmospheresSettings.StormCellRadiusKm,
                Seed = AtmospheresSettings.StormSeed,
                Scheduled = AtmospheresSettings.StormsEnabled,
                HasForced = AtmospheresSettings.ForceCellOn,
                ForcedLatitude = AtmospheresSettings.ForceCellLatitude,
                ForcedLongitude = AtmospheresSettings.ForceCellLongitude,
                ForcedRadiusKm = AtmospheresSettings.ForceCellRadiusKm
            };
        }

        private static long GameDefinition()
        {
            return unchecked(0x4C454741L << 32 | (uint)AtmospheresSettings.StormSeed);
        }
#pragma warning restore CS0618
    }

    // Starts the legacy bridge with the other modules and stops it at their shutdown.
    [AtmospheresModule(5)]
    internal sealed class LegacyBridgeModule : IAtmospheresModule
    {
        public void Init()
        {
            if (LegacyBridge.Install()) AtmospheresLog.Say("legacy weather field installed (storm cells and snow line behind IWeatherField)");
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
        }

        public void Tick(float dt)
        {
        }

        public void Shutdown()
        {
            LegacyBridge.Uninstall();
        }
    }
}
