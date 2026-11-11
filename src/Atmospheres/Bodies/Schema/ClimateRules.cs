using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The rule classes of a body's climate, read from the sub-nodes of its REDEFINITION_BODY node (GameData/ReDefinition/Atmospheres/Bodies).
    // All are immutable: built once by the body provider, replaced as a whole when the configuration changes. Every parameter of a
    // constructor has the value of the generic default, a body without a node, so that a missing key means that value and no
    // Kerbin number appears in code. Node and key names are in the comments.

    // Helpers for the immutable lists the rules hold.
    internal static class SchemaList
    {
        internal static IReadOnlyList<T> Of<T>(IEnumerable<T> items)
        {
            return new List<T>(items ?? new T[0]).AsReadOnly();
        }
    }

    // How the season phase of the body runs. Node SEASONS.
    internal enum SeasonClockKind
    {
        // The phase follows the year of the orbit (key clock = Orbit).
        Orbit,
        // A constant phase.
        Constant,
        // No seasons.
        None,
        // The phase is fixed at FixedPhase.
        Fixed
    }

    // Where the length of the year comes from.
    internal enum YearSourceKind
    {
        OrbitPeriod,
        // For a moon: the period of the planet it circles.
        ParentOrbitPeriod,
        // The key yearSeconds.
        Seconds
    }

    // The body's season phase: an input of the field's insolation. Node SEASONS.
    internal sealed class SeasonRules
    {
        internal static readonly SeasonRules Default = new SeasonRules();

        internal SeasonRules(SeasonClockKind clock = SeasonClockKind.Orbit, double fixedPhase = 0.0, YearSourceKind yearSource = YearSourceKind.OrbitPeriod,
            double yearSeconds = 0.0, double yearFactor = 1.0, double tiltDegrees = double.NaN, double hemisphereOffset = 0.5, bool eccentricSeason = false)
        {
            Clock = clock;
            FixedPhase = fixedPhase;
            YearSource = yearSource;
            YearSeconds = yearSeconds;
            YearFactor = yearFactor;
            TiltDegrees = tiltDegrees;
            HemisphereOffset = hemisphereOffset;
            EccentricSeason = eccentricSeason;
        }

        // Keys clock and, for Fixed, the phase.
        internal SeasonClockKind Clock { get; }
        internal double FixedPhase { get; }
        // Keys yearSource and yearSeconds (used when the source is Seconds).
        internal YearSourceKind YearSource { get; }
        internal double YearSeconds { get; }
        // Key yearFactor: multiplies the year, in addition to the player's global setting.
        internal double YearFactor { get; }
        // Key tilt: NaN is Auto (read the body's axis, else none), a number is the axial tilt in degrees.
        internal double TiltDegrees { get; }
        // Key hemispherePhaseOffset: the southern year's offset against the northern, as a share of the year.
        internal double HemisphereOffset { get; }
        // Key eccentricitySeason.
        internal bool EccentricSeason { get; }
    }

    // Where the body's air temperature comes from.
    internal enum TemperatureSource
    {
        // KSP's own temperature (GetFullTemperature).
        Game,
        // A pure model fitted to a probe dump.
        Model,
        // A fixed value.
        Fixed
    }

    // The temperature of the body: the target the field relaxes towards, and the numbers that decide rain against snow. Node TEMPERATURE.
    internal sealed class TemperatureRules
    {
        internal static readonly TemperatureRules Default = new TemperatureRules();

        internal TemperatureRules(TemperatureSource source = TemperatureSource.Game, double waterCelsius = 0.0, double wetBulbOffsetK = 0.7,
            double swing = double.NaN, Curve target = null)
        {
            Source = source;
            WaterCelsius = waterCelsius;
            WetBulbOffsetK = wetBulbOffsetK;
            Swing = swing;
            Target = target;
        }

        // Key source.
        internal TemperatureSource Source { get; }
        // Key waterCelsius: the temperature at which water freezes on this body.
        internal double WaterCelsius { get; }
        // Key wetBulbOffsetK.
        internal double WetBulbOffsetK { get; }
        // Key swing: the seasonal amplitude in kelvin; NaN is Auto (from the tilt).
        internal double Swing { get; }
        // The target temperature over altitude in metres (kelvin), built from the probes of KSP's temperature curves; null where the
        // game's own temperature is the target.
        internal Curve Target { get; }
    }

    // One type of precipitation of a body and the temperature band it falls in. The type is a logistic share of the temperature
    // (centre, width), not a hard threshold. Node TYPE inside PRECIP.
    internal sealed class PrecipTypeRule
    {
        internal PrecipTypeRule(string id, ReDefinition.Core.PrecipType kind, double centreC = 1.0, double widthC = 1.0, bool coldSide = true, string field = "",
            IEnumerable<string> levelNames = null, string look = "")
        {
            Id = id ?? "";
            Kind = kind;
            CentreC = centreC;
            WidthC = widthC;
            ColdSide = coldSide;
            Field = field ?? "";
            LevelNames = SchemaList.Of(levelNames);
            Look = look ?? "";
        }

        // Key id.
        internal string Id { get; }
        // Key kind.
        internal ReDefinition.Core.PrecipType Kind { get; }
        // Keys centreC and widthC: the wet-bulb temperature at which half falls as this type, and the width of the change.
        internal double CentreC { get; }
        internal double WidthC { get; }
        // True where the type falls below the centre (snow), false above it (rain).
        internal bool ColdSide { get; }
        // Key field: the name of EVE's particle field for this type on this body, or empty.
        internal string Field { get; }
        // Key levelNames: the names of the five bands from Flurries to Blizzard for this type; empty means PrecipScale.DefaultName.
        internal IReadOnlyList<string> LevelNames { get; }
        // Key look: the name of the look preset.
        internal string Look { get; }
    }

    // Precipitation of the body. Node PRECIP.
    internal sealed class PrecipRules
    {
        // The rates in mm of water per hour at which the bands Flurries, Light, Moderate, Heavy and Blizzard start, for
        // PrecipScale.LevelFromRate. [VERIFY: signed in the header of package B10; these are placeholders until then.]
        internal static readonly float[] PlaceholderBandRates = { 0.05f, 0.25f, 1.0f, 2.5f, 6.0f };

        internal static readonly PrecipRules Default = new PrecipRules();

        internal PrecipRules(IEnumerable<PrecipTypeRule> types = null, float[] bandRatesMmPerH = null)
        {
            Types = SchemaList.Of(types);
            bandRates = (float[])(bandRatesMmPerH ?? PlaceholderBandRates).Clone();
        }

        private readonly float[] bandRates;

        // The types, in the order they are tried. Empty: the type follows the temperature only.
        internal IReadOnlyList<PrecipTypeRule> Types { get; }
        // Key bandRates (five numbers): a copy, so that the rule stays as it was made.
        internal float[] BandRatesMmPerH
        {
            get { return (float[])bandRates.Clone(); }
        }
    }

    // What lies on the ground.
    internal enum CoverKind
    {
        None,
        Snow,
        Sand,
        Dust,
        Ash
    }

    // One layer of surface cover of a body: what it is, how it looks and where it may lie. Node COVER.
    internal sealed class CoverRules
    {
        internal static readonly CoverRules None = new CoverRules("none", CoverKind.None);

        internal CoverRules(string id, CoverKind kind, Color? colour = null, float roughness = 0.8f, float maxDepthM = 0.5f,
            double minLatDeg = -90.0, double maxLatDeg = 90.0, double minAltM = double.NegativeInfinity, double maxAltM = double.PositiveInfinity,
            double maxSlopeDeg = 60.0, bool noWater = true, bool tracks = true, double tracksTauHours = 3.0, float driftAmount = 0.3f,
            float driftStretch = 4f)
        {
            Id = id ?? "";
            Kind = kind;
            Colour = colour ?? new Color(0.93f, 0.95f, 0.98f, 1f);
            Roughness = roughness;
            MaxDepthM = maxDepthM;
            MinLatDeg = minLatDeg;
            MaxLatDeg = maxLatDeg;
            MinAltM = minAltM;
            MaxAltM = maxAltM;
            MaxSlopeDeg = maxSlopeDeg;
            NoWater = noWater;
            Tracks = tracks;
            TracksTauHours = tracksTauHours;
            DriftAmount = driftAmount;
            DriftStretch = driftStretch;
        }

        internal string Id { get; }
        internal CoverKind Kind { get; }
        // Linear colour of the cover.
        internal Color Colour { get; }
        internal float Roughness { get; }
        // The depth in metres at which the cover is full.
        internal float MaxDepthM { get; }
        // The mask: where the cover may lie.
        internal double MinLatDeg { get; }
        internal double MaxLatDeg { get; }
        internal double MinAltM { get; }
        internal double MaxAltM { get; }
        internal double MaxSlopeDeg { get; }
        internal bool NoWater { get; }
        // Tracks of wheels and kerbals, and how long they take to fade.
        internal bool Tracks { get; }
        internal double TracksTauHours { get; }
        // How much the wind drifts the cover and how far the pattern stretches along the wind.
        internal float DriftAmount { get; }
        internal float DriftStretch { get; }
    }

    // Sea ice and ice sheets as a look: the freezing itself is the field's, so there is no freeze line and no lag here. Node ICE.
    internal sealed class IceRules
    {
        internal static readonly IceRules Default = new IceRules();

        internal IceRules(float floeSizeKm = 3f, float leadFraction = 0.08f, Color? colour = null, float roughness = 0.6f, bool permanent = false, bool liquid = true)
        {
            FloeSizeKm = floeSizeKm;
            LeadFraction = leadFraction;
            Colour = colour ?? new Color(0.85f, 0.92f, 0.97f, 1f);
            Roughness = roughness;
            Permanent = permanent;
            Liquid = liquid;
        }

        // The size of the floes in the pattern, in km, and the share of open water between them.
        internal float FloeSizeKm { get; }
        internal float LeadFraction { get; }
        internal Color Colour { get; }
        internal float Roughness { get; }
        // True where the sea is always frozen (fraction 1, no clock).
        internal bool Permanent { get; }
        // False where the body has no liquid ocean.
        internal bool Liquid { get; }
    }

    // When a cell of the fallback weather generator may occur.
    internal enum CellGate
    {
        Winter,
        Always,
        Season,
        Temperature
    }

    // A latitude band of the fallback generator and what may fall there. Node bands inside CELLS.
    internal sealed class CellBand
    {
        internal CellBand(string id, double fromLatDeg, double toLatDeg, double chance, IEnumerable<ReDefinition.Core.PrecipType> types)
        {
            Id = id ?? "";
            FromLatDeg = fromLatDeg;
            ToLatDeg = toLatDeg;
            Chance = chance;
            Types = SchemaList.Of(types);
        }

        internal string Id { get; }
        internal double FromLatDeg { get; }
        internal double ToLatDeg { get; }
        internal double Chance { get; }
        internal IReadOnlyList<ReDefinition.Core.PrecipType> Types { get; }
    }

    // The weather cells of the FALLBACK generator, used only when the simulation is not. Node CELLS.
    internal sealed class CellRules
    {
        internal static readonly CellRules Default = new CellRules();

        internal CellRules(double sectorLengthKm = 100.0, double radiusMinKm = 5.0, double radiusMaxKm = 25.0, double driftMinMs = 1.5, double driftMaxMs = 4.0,
            double driftDeg = 90.0, double slotBodyDays = 4.0, CellGate gate = CellGate.Winter, double broadShare = 0.0, double coreShare = 1.0,
            IEnumerable<CellBand> bands = null)
        {
            SectorLengthKm = sectorLengthKm;
            RadiusMinKm = radiusMinKm;
            RadiusMaxKm = radiusMaxKm;
            DriftMinMs = driftMinMs;
            DriftMaxMs = driftMaxMs;
            DriftDeg = driftDeg;
            SlotBodyDays = slotBodyDays;
            Gate = gate;
            BroadShare = broadShare;
            CoreShare = coreShare;
            Bands = SchemaList.Of(bands);
        }

        internal double SectorLengthKm { get; }
        internal double RadiusMinKm { get; }
        internal double RadiusMaxKm { get; }
        internal double DriftMinMs { get; }
        internal double DriftMaxMs { get; }
        internal double DriftDeg { get; }
        // The slot length in days of the body.
        internal double SlotBodyDays { get; }
        internal CellGate Gate { get; }
        // The shares of broad and core cells in the level table.
        internal double BroadShare { get; }
        internal double CoreShare { get; }
        internal IReadOnlyList<CellBand> Bands { get; }
    }

    // The simulation grid and its forcing. Node SIM. The player's settings simGrid, simLevels and simSeed override the grid and the seed.
    internal sealed class SimRules
    {
        internal static readonly SimRules Default = new SimRules();

        internal SimRules(int gridLat = 64, int gridLon = 128, int levels = 16, double stepSeconds = 3600.0, int epochSteps = 666,
            double seaHeatCapacity = 1.0, double freezingPointC = 0.0, IDictionary<string, double> forcing = null)
        {
            GridLat = gridLat;
            GridLon = gridLon;
            Levels = levels;
            StepSeconds = stepSeconds;
            EpochSteps = epochSteps;
            SeaHeatCapacity = seaHeatCapacity;
            FreezingPointC = freezingPointC;
            var copy = new Dictionary<string, double>();
            if (forcing != null)
            {
                foreach (KeyValuePair<string, double> pair in forcing) copy[pair.Key] = pair.Value;
            }
            Forcing = copy;
        }

        // The grid: latitude and longitude cells, and the vertical levels.
        internal int GridLat { get; }
        internal int GridLon { get; }
        internal int Levels { get; }
        // The fixed step in seconds of game time, and the steps in one epoch.
        internal double StepSeconds { get; }
        internal int EpochSteps { get; }
        // The sea's heat capacity against the default, and the freezing point of its water in degrees Celsius.
        internal double SeaHeatCapacity { get; }
        internal double FreezingPointC { get; }
        // Further forcing parameters by name (key = number), read by the simulation.
        internal IReadOnlyDictionary<string, double> Forcing { get; }
    }
}
