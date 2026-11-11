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

    // Where the generator's ocean-fraction and moisture-source raster comes from.
    internal enum GeneratorOceanSource
    {
        // The game's terrain: ocean where the body has an ocean and the surface lies below sea level.
        Terrain,
        // Synthetic continents of the seed with the key oceanFraction as the target (a body without readable terrain).
        Synthetic,
        // No ocean at all (a dry body).
        None
    }

    // Whether the body carries dust or sand that wind lifts.
    internal enum GeneratorDust
    {
        // Dusty where the body's precipitation types include dust or sand.
        Auto,
        On,
        Off
    }

    // The inputs of the weather generator for one body (package C25). Node GENERATOR. A key that is NaN (the default) means "not
    // stated": the generator takes the value from the game's facts of the body (radius, rotation, pressure, gravity, the probe of the
    // temperature curves) or, where the game has none, the generator's own default. All numbers are SI, degrees where the key says so.
    // The sub-node LEVERS names a field of the generator's parameter set and a value (a tuning lever of the generator), so that a lever
    // added to the generator needs no new key.
    internal sealed class GeneratorRules
    {
        internal static readonly GeneratorRules Default = new GeneratorRules();

        internal GeneratorRules(bool enabled = true, double rotationPeriodS = double.NaN, double obliquityDeg = double.NaN, double solarFluxWm2 = double.NaN,
            double surfacePressureKPa = double.NaN, double gravityMs2 = double.NaN, double tropopauseM = double.NaN, double tropopauseDepthShare = 0.15,
            double lapseKPerM = double.NaN, double surfaceMeanK = double.NaN, double diurnalLandAmpK = double.NaN, double gasConstant = double.NaN,
            double cp = double.NaN, double precipFactor = double.NaN, GeneratorOceanSource oceanSource = GeneratorOceanSource.Terrain,
            double oceanFraction = double.NaN, double seaLevelM = 0.0, double salinityPsu = double.NaN, double freezeShiftKPerPsu = double.NaN,
            double mixedLayerDepthM = double.NaN, double seaHeatCapacity = double.NaN, GeneratorDust dust = GeneratorDust.Auto,
            double dustWindThresholdMs = double.NaN, int seedOffset = 0, bool waves = true, bool cells = true, IDictionary<string, double> levers = null)
        {
            Enabled = enabled;
            RotationPeriodS = rotationPeriodS;
            ObliquityDeg = obliquityDeg;
            SolarFluxWm2 = solarFluxWm2;
            SurfacePressureKPa = surfacePressureKPa;
            GravityMs2 = gravityMs2;
            TropopauseM = tropopauseM;
            TropopauseDepthShare = tropopauseDepthShare;
            LapseKPerM = lapseKPerM;
            SurfaceMeanK = surfaceMeanK;
            DiurnalLandAmpK = diurnalLandAmpK;
            GasConstant = gasConstant;
            Cp = cp;
            PrecipFactor = precipFactor;
            OceanSource = oceanSource;
            OceanFraction = oceanFraction;
            SeaLevelM = seaLevelM;
            SalinityPsu = salinityPsu;
            FreezeShiftKPerPsu = freezeShiftKPerPsu;
            MixedLayerDepthM = mixedLayerDepthM;
            SeaHeatCapacity = seaHeatCapacity;
            Dust = dust;
            DustWindThresholdMs = dustWindThresholdMs;
            SeedOffset = seedOffset;
            Waves = waves;
            Cells = cells;
            var copy = new Dictionary<string, double>();
            if (levers != null)
            {
                foreach (KeyValuePair<string, double> pair in levers) copy[pair.Key] = pair.Value;
            }
            Levers = copy;
        }

        // Key enabled: false switches the generator off for this body (no weather there).
        internal bool Enabled { get; }
        // Key rotationPeriodS: the sidereal rotation period in seconds; NaN reads the body's.
        internal double RotationPeriodS { get; }
        // Key obliquityDeg: the axial tilt that drives the insolation. NaN: the tilt of the SEASONS node when the body is tilted, else
        // DefaultObliquityDeg (the game's bodies have no tilt, the seasons are an artificial phase).
        internal double ObliquityDeg { get; }
        // Key solarFluxWm2: the stellar flux at the body; NaN reads it from the body's orbit and the star's luminosity.
        internal double SolarFluxWm2 { get; }
        // Key surfacePressureKPa: NaN reads the body's sea-level pressure.
        internal double SurfacePressureKPa { get; }
        // Key gravityMs2: NaN reads the body's surface gravity.
        internal double GravityMs2 { get; }
        // Keys tropopauseM and tropopauseDepthShare: the height of the tropopause; NaN takes the share of the atmosphere's depth.
        internal double TropopauseM { get; }
        internal double TropopauseDepthShare { get; }
        // Keys lapseKPerM, surfaceMeanK, diurnalLandAmpK: NaN takes the fit of the game's temperature curves (ThermalFit), else the temperature
        // target of the body, else the generator's default.
        internal double LapseKPerM { get; }
        internal double SurfaceMeanK { get; }
        internal double DiurnalLandAmpK { get; }
        // Keys gasConstant and cp: J/(kg K) of the air; NaN is dry air.
        internal double GasConstant { get; }
        internal double Cp { get; }
        // Key precipFactor: the body factor of the absolute precipitation bands; NaN scales with the square root of the surface pressure.
        internal double PrecipFactor { get; }
        // Keys oceanSource, oceanFraction (the target of the synthetic source) and seaLevelM (the height that counts as sea level).
        internal GeneratorOceanSource OceanSource { get; }
        internal double OceanFraction { get; }
        internal double SeaLevelM { get; }
        // Keys salinityPsu, freezeShiftKPerPsu, mixedLayerDepthM, seaHeatCapacity (relative to water): the sea of the ice model. The
        // freezing point of the liquid is the TEMPERATURE node's waterCelsius.
        internal double SalinityPsu { get; }
        internal double FreezeShiftKPerPsu { get; }
        internal double MixedLayerDepthM { get; }
        internal double SeaHeatCapacity { get; }
        // Keys dust and dustWindThresholdMs.
        internal GeneratorDust Dust { get; }
        internal double DustWindThresholdMs { get; }
        // Key seedOffset: added to the body's seed, so that two bodies of one name or one saved world can differ.
        internal int SeedOffset { get; }
        // Keys waves and cells: false removes the moving systems or the convective cells (a switch of the generator's A/B runs).
        internal bool Waves { get; }
        internal bool Cells { get; }
        // The LEVERS sub-node: a field of the generator's parameter set and its number (a boolean field takes 0 or 1).
        internal IReadOnlyDictionary<string, double> Levers { get; }

        // The tilt that stands in for the game's missing one: an artificial axis so that the insolation has seasons. [VERIFY with the owner]
        internal const double DefaultObliquityDeg = 23.44;
    }
}
