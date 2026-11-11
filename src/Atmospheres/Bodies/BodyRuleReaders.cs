using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The limits the readers hold the simulation grid to.
    internal static class BodyLimits
    {
        internal const int MinGridLat = 8;
        internal const int MaxGridLat = 256;
        internal const int MinGridLon = 16;
        internal const int MaxGridLon = 512;
        internal const int MinLevels = 4;
        internal const int MaxLevels = 64;
        internal const double MinStepSeconds = 60.0;
        internal const double MaxStepSeconds = 86400.0;
        internal const int MinEpochSteps = 1;
        internal const int MaxEpochSteps = 100000;
    }

    // Reads the sub-nodes of REDEFINITION_BODY into the rule classes of the schema. Every method takes a reader on the sub-node (which
    // may be absent) and returns the rule with the schema's defaults for what is missing; a key that cannot be read becomes a warning.
    internal static class BodyRuleReaders
    {
        internal static TemperatureRules Temperature(BodyNodeReader r, Curve probed)
        {
            TemperatureSource source = r.Enum("source", TemperatureSource.Game);
            Curve target = TargetCurve(r.Child("TARGET")) ?? probed;
            return new TemperatureRules(source, r.Double("waterCelsius", 0.0, -273.15, 1000.0), r.Double("wetBulbOffsetK", 0.7, -20.0, 20.0),
                r.DoubleOrAuto("swing", 0.0, 500.0), target);
        }

        // TARGET { key0 = altitudeM temperatureK, key1 = ... }: the altitudes ascend. Null where there is no valid curve.
        private static Curve TargetCurve(BodyNodeReader r)
        {
            if (!r.Present) return null;
            var xs = new List<float>();
            var ys = new List<float>();
            for (int i = 0; i < 64; i++)
            {
                string text = r.Raw("key" + i);
                if (text == null) break;
                string[] parts = text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                double x, y;
                if (parts.Length != 2 || !BodyNodeReader.TryNumber(parts[0], out x) || !BodyNodeReader.TryNumber(parts[1], out y))
                {
                    r.Warn("key" + i, "'" + text + "' is not 'altitudeM temperatureK'; the target curve is ignored");
                    return null;
                }
                if (xs.Count > 0 && !(x > xs[xs.Count - 1]))
                {
                    r.Warn("key" + i, "the altitudes of the target must ascend; the target curve is ignored");
                    return null;
                }
                xs.Add((float)x);
                ys.Add((float)y);
            }
            return xs.Count == 0 ? null : new Curve(xs.ToArray(), ys.ToArray());
        }

        // The TYPE nodes of PRECIP; the guessed types where there are none. The band rates come from the key bandRates.
        internal static PrecipRules Precip(BodyNodeReader r, List<PrecipTypeRule> guessed)
        {
            var types = new List<PrecipTypeRule>();
            int index = 0;
            foreach (ConfigNode child in r.Nodes("TYPE"))
            {
                BodyNodeReader t = r.Wrap(child, "TYPE[" + index++ + "]");
                PrecipType kind = t.Enum("kind", PrecipType.None);
                string id = t.String("id", kind.ToString().ToLowerInvariant());
                bool coldDefault = kind == PrecipType.Snow || kind == PrecipType.Sleet || kind == PrecipType.FreezingRain;
                types.Add(new PrecipTypeRule(id, kind, t.Double("centreC", 1.0, -500.0, 2000.0), t.Double("widthC", 1.0, 0.01, 1000.0), t.Bool("coldSide", coldDefault),
                    t.String("field", ""), t.List("levelNames"), t.String("look", "")));
            }
            float[] rates = null;
            double[] given = r.Numbers("bandRates");
            if (given != null)
            {
                if (given.Length != 5) r.Warn("bandRates", "five numbers are needed, " + given.Length + " given; the default is used");
                else if (!Ascending(given)) r.Warn("bandRates", "the rates must be positive and ascend; the default is used");
                else rates = new[] { (float)given[0], (float)given[1], (float)given[2], (float)given[3], (float)given[4] };
            }
            return new PrecipRules(types.Count > 0 ? types : guessed, rates);
        }

        private static bool Ascending(double[] values)
        {
            if (!(values[0] > 0)) return false;
            for (int i = 1; i < values.Length; i++)
            {
                if (!(values[i] > values[i - 1])) return false;
            }
            return true;
        }

        // The COVER nodes; the guessed covers where the body's node has none.
        internal static List<CoverRules> Covers(BodyNodeReader r, List<CoverRules> guessed)
        {
            var covers = new List<CoverRules>();
            int index = 0;
            foreach (ConfigNode child in r.Nodes("COVER"))
            {
                BodyNodeReader c = r.Wrap(child, "COVER[" + index++ + "]");
                CoverKind kind = c.Enum("kind", CoverKind.Snow);
                covers.Add(new CoverRules(c.String("id", kind.ToString().ToLowerInvariant()), kind, c.Colour("colour"), c.Float("roughness", 0.8f, 0f, 1f),
                    c.Float("maxDepthM", 0.5f, 0f, 100f), c.Double("minLat", -90.0, -90.0, 90.0), c.Double("maxLat", 90.0, -90.0, 90.0),
                    c.Double("minAlt", double.NegativeInfinity), c.Double("maxAlt", double.PositiveInfinity), c.Double("maxSlope", 60.0, 0.0, 90.0),
                    c.Bool("noWater", true), c.Bool("tracks", true), c.Double("tracksTauHours", 3.0, 0.0, 10000.0), c.Float("driftAmount", 0.3f, 0f, 1f),
                    c.Float("driftStretch", 4f, 1f, 100f)));
            }
            return covers.Count > 0 ? covers : (guessed ?? covers);
        }

        internal static IceRules Ice(BodyNodeReader r)
        {
            return new IceRules(r.Float("floeSizeKm", 3f, 0.01f, 1000f), r.Float("leadFraction", 0.08f, 0f, 1f), r.Colour("colour"), r.Float("roughness", 0.6f, 0f, 1f),
                r.Bool("permanent", false), r.Bool("liquid", true));
        }

        internal static CellRules Cells(BodyNodeReader r)
        {
            var bands = new List<CellBand>();
            int index = 0;
            foreach (ConfigNode child in r.Nodes("BAND"))
            {
                BodyNodeReader b = r.Wrap(child, "BAND[" + index++ + "]");
                var kinds = new List<PrecipType>();
                foreach (string name in b.List("types"))
                {
                    PrecipType kind;
                    if (Enum.TryParse(name, true, out kind) && Enum.IsDefined(typeof(PrecipType), kind)) kinds.Add(kind);
                    else b.Warn("types", "'" + name + "' is not a precipitation type; it is ignored");
                }
                bands.Add(new CellBand(b.String("id", "band" + index), b.Double("fromLat", 0.0, -90.0, 90.0), b.Double("toLat", 90.0, -90.0, 90.0),
                    b.Double("chance", 1.0, 0.0, 1.0), kinds));
            }
            return new CellRules(r.Double("sectorLengthKm", 100.0, 1.0, 10000.0), r.Double("radiusMinKm", 5.0, 0.1, 5000.0), r.Double("radiusMaxKm", 25.0, 0.1, 5000.0),
                r.Double("driftMinMs", 1.5, 0.0, 200.0), r.Double("driftMaxMs", 4.0, 0.0, 200.0), r.Double("driftDeg", 90.0, 0.0, 360.0),
                r.Double("slotBodyDays", 4.0, 0.01, 1000.0), r.Enum("gate", CellGate.Winter), r.Double("broadShare", 0.0, 0.0, 1.0),
                r.Double("coreShare", 1.0, 0.0, 1.0), bands);
        }

        internal static SimRules Sim(BodyNodeReader r)
        {
            var forcing = new Dictionary<string, double>();
            BodyNodeReader f = r.Child("FORCING");
            foreach (string key in f.Keys())
            {
                double value = f.Double(key, double.NaN);
                if (!double.IsNaN(value)) forcing[key] = value;
            }
            return new SimRules(r.Int("gridLat", 64, BodyLimits.MinGridLat, BodyLimits.MaxGridLat), r.Int("gridLon", 128, BodyLimits.MinGridLon, BodyLimits.MaxGridLon),
                r.Int("levels", 16, BodyLimits.MinLevels, BodyLimits.MaxLevels), r.Double("stepSeconds", 3600.0, BodyLimits.MinStepSeconds, BodyLimits.MaxStepSeconds),
                r.Int("epochSteps", 666, BodyLimits.MinEpochSteps, BodyLimits.MaxEpochSteps), r.Double("seaHeatCapacity", 1.0, 0.01, 100.0),
                r.Double("freezingPointC", 0.0, -273.15, 1000.0), forcing);
        }

        internal static CloudRules Clouds(BodyNodeReader r)
        {
            CloudSourceKind? source = null;
            if (r.Has("cloudSource")) source = r.Enum("cloudSource", CloudSourceKind.Ours);
            return new CloudRules(source, r.Enum("discover", CloudDiscovery.Auto), r.String("baseLayer", ""), r.List("weatherLayers"),
                r.Has("snowTypeNames") ? r.List("snowTypeNames") : null, r.Has("stratusTypeNames") ? r.List("stratusTypeNames") : null,
                r.List("dustTypeNames"), r.Int("eveLayerCap", 0, 0, 64));
        }

        internal static LookRules Look(BodyNodeReader r)
        {
            return new LookRules(r.Colour("fogColour"), r.Float("visibilityMinM", 60f, 1f, 100000f), r.Colour("veilGroundColour"), r.Colour("veilSkyColour"),
                r.Colour("flakeColour"));
        }

        internal static SoundRules Sound(BodyNodeReader r)
        {
            return new SoundRules(r.String("windBed", ""), r.String("precipBed", ""), r.String("footstepSurface", ""), r.Bool("grit", false));
        }

        internal static WindRules Wind(BodyNodeReader r)
        {
            return new WindRules(r.Float("maxWind", 12f, 0f, 500f), r.Float("violent", 25f, 0f, 1000f), r.Bool("windOnLanded", true), r.Float("groundFactor", 0.1f, 0f, 1f),
                r.Float("fullHeightM", 50f, 1f, 100000f), r.Float("maxAccel", 1.5f, 0f, 100f), r.Float("maxSlew", 8f, 0f, 1000f));
        }

        internal static GameplayRules Gameplay(BodyNodeReader r)
        {
            return new GameplayRules(r.Float("solarLoss", 1f, 0f, 1f));
        }

        internal static AuroraRules Aurora(BodyNodeReader r)
        {
            return new AuroraRules(r.Bool("enabled", false), r.Float("minLatDeg", 60f, 0f, 90f), r.Colour("colour"), r.Float("strength", 1f, 0f, 10f));
        }

        internal static LightMediumRules LightMedium(BodyNodeReader r)
        {
            return new LightMediumRules(r.Float("density", 1f, 0f, 100f), r.Colour("scatterColour"), r.Float("dustScatter", 0f, 0f, 100f));
        }

        internal static FrostRules Frost(BodyNodeReader r)
        {
            return new FrostRules(r.Bool("enabled", true), r.Float("maxTemperatureK", 268f, 0f, 1000f), r.Float("strength", 1f, 0f, 10f));
        }

        internal static ForecastRules Forecast(BodyNodeReader r)
        {
            return new ForecastRules(r.Double("stepSeconds", 3600.0, 60.0, 86400.0), r.Double("horizonHours", 24.0, 1.0, 720.0));
        }
    }
}
