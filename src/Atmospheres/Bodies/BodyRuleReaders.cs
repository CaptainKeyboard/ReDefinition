using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The limits the readers hold the generator's inputs to.
    internal static class BodyLimits
    {
        internal const double MinRotationPeriodS = 60.0;
        internal const double MaxRotationPeriodS = 1.0e9;
        internal const double MinObliquityDeg = 0.0;
        internal const double MaxObliquityDeg = 90.0;
        internal const double MinPressureKPa = 0.001;
        internal const double MaxPressureKPa = 1.0e5;
        internal const double MinGravityMs2 = 0.01;
        internal const double MaxGravityMs2 = 1000.0;
        internal const double MinTemperatureK = 3.0;
        internal const double MaxTemperatureK = 2000.0;
        internal const int MaxSeedOffset = 1000000000;
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
                r.Bool("permanent", false), r.Bool("liquid", true), r.Float("icePassThicknessM", 0.3f, 0f, 100f));
        }

        internal static GeneratorRules Generator(BodyNodeReader r)
        {
            var levers = new Dictionary<string, double>();
            BodyNodeReader l = r.Child("LEVERS");
            foreach (string key in l.Keys())
            {
                double value = l.Double(key, double.NaN);
                if (!double.IsNaN(value) && !double.IsInfinity(value)) levers[key] = value;
            }
            return new GeneratorRules(r.Bool("enabled", true), r.DoubleOrAuto("rotationPeriodS", BodyLimits.MinRotationPeriodS, BodyLimits.MaxRotationPeriodS),
                r.DoubleOrAuto("obliquityDeg", BodyLimits.MinObliquityDeg, BodyLimits.MaxObliquityDeg), r.DoubleOrAuto("solarFluxWm2", 0.0, 1.0e6),
                r.DoubleOrAuto("surfacePressureKPa", BodyLimits.MinPressureKPa, BodyLimits.MaxPressureKPa),
                r.DoubleOrAuto("gravityMs2", BodyLimits.MinGravityMs2, BodyLimits.MaxGravityMs2), r.DoubleOrAuto("tropopauseM", 100.0, 1.0e6),
                r.Double("tropopauseDepthShare", 0.15, 0.01, 1.0), r.DoubleOrAuto("lapseKPerM", 0.0, 0.1),
                r.DoubleOrAuto("surfaceMeanK", BodyLimits.MinTemperatureK, BodyLimits.MaxTemperatureK), r.DoubleOrAuto("diurnalLandAmpK", 0.0, 200.0),
                r.DoubleOrAuto("gasConstant", 10.0, 10000.0), r.DoubleOrAuto("cp", 100.0, 100000.0), r.DoubleOrAuto("precipFactor", 0.0, 10.0),
                r.Enum("oceanSource", GeneratorOceanSource.Terrain), r.DoubleOrAuto("oceanFraction", 0.0, 1.0), r.Double("seaLevelM", 0.0, -1.0e6, 1.0e6),
                r.DoubleOrAuto("salinityPsu", 0.0, 400.0), r.DoubleOrAuto("freezeShiftKPerPsu", -1.0, 0.0), r.DoubleOrAuto("mixedLayerDepthM", 0.1, 10000.0),
                r.DoubleOrAuto("seaHeatCapacity", 0.01, 100.0), r.Enum("dust", GeneratorDust.Auto), r.DoubleOrAuto("dustWindThresholdMs", 0.1, 200.0),
                r.Int("seedOffset", 0, -BodyLimits.MaxSeedOffset, BodyLimits.MaxSeedOffset), r.Bool("waves", true), r.Bool("cells", true), levers);
        }

        internal static CloudRules Clouds(BodyNodeReader r)
        {
            CloudSourceKind? source = null;
            if (r.Has("cloudSource")) source = r.Enum("cloudSource", CloudSourceKind.Ours);
            return new CloudRules(source, r.Enum("discover", CloudDiscovery.Auto), r.String("baseLayer", ""), r.List("weatherLayers"),
                r.Has("snowTypeNames") ? r.List("snowTypeNames") : null, r.Has("stratusTypeNames") ? r.List("stratusTypeNames") : null,
                r.List("dustTypeNames"), r.Int("eveLayerCap", 0, 0, 64), CloudLayers(r), r.Float("sphereScale", 1f, 0.25f, 1f));
        }

        // The most LAYER nodes and TYPE nodes a body's own cloud set holds (the set may be larger than the draw limit; groups and the cap choose).
        internal const int MaxLayerSpecs = 8;

        // The LAYER nodes of CLOUDS (CloudLayerSpec): a layer or type that cannot be read is a warning and is left out, never an exception.
        internal static List<CloudLayerSpec> CloudLayers(BodyNodeReader clouds)
        {
            var layers = new List<CloudLayerSpec>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            ConfigNode[] nodes = clouds.Nodes("LAYER");
            for (int n = 0; n < nodes.Length; n++)
            {
                BodyNodeReader r = clouds.Wrap(nodes[n], "LAYER");
                string name = r.String("name", "");
                if (name.Length == 0)
                {
                    r.Warn("name", "a LAYER node without a name is ignored");
                    continue;
                }
                if (layers.Count >= MaxLayerSpecs)
                {
                    r.Warn("name", "at most " + MaxLayerSpecs + " LAYER nodes count; " + name + " is ignored");
                    continue;
                }
                if (!names.Add(name))
                {
                    r.Warn("name", "the layer " + name + " is there twice; the first one counts");
                    continue;
                }
                double inner = r.Double("innerAltitudeM", double.NaN, -1.0e7, 1.0e7);
                double outer = r.Double("outerAltitudeM", double.NaN, -1.0e7, 1.0e7);
                if (double.IsNaN(inner) || double.IsNaN(outer) || !(outer > inner))
                {
                    r.Warn("outerAltitudeM", "the layer " + name + " needs innerAltitudeM below outerAltitudeM; it is ignored");
                    continue;
                }
                var types = new List<CloudTypeSpec>();
                ConfigNode[] typeNodes = r.Nodes("TYPE");
                for (int t = 0; t < typeNodes.Length && types.Count < CloudLayerDef.MaxTypes; t++)
                {
                    CloudTypeSpec type = CloudType(r.Wrap(typeNodes[t], "TYPE"), name);
                    if (type != null) types.Add(type);
                }
                if (types.Count == 0)
                {
                    r.Warn("TYPE", "the layer " + name + " has no readable TYPE node; it is ignored");
                    continue;
                }
                layers.Add(new CloudLayerSpec(name, (float)inner, (float)outer, r.Float("sigmaPerM", 0.03f, 1.0e-6f, 10f), r.Enum("kind", LayerKind.Weather),
                    r.String("group", ""), r.Float("coverage", 1f, 0f, 1f), r.Float("edgeSoftness", 0.15f, 0f, 1f), r.Float("fadeNearM", 0f, 0f, 1.0e9f),
                    r.Float("fadeFarM", float.PositiveInfinity, 0f, float.PositiveInfinity), r.Float("shadowStrength", 1f, 0f, 1f),
                    r.Float("noiseAmount", 0.3f, 0f, 1f), r.Float("noiseScaleM", 8000f, 1f, 1.0e7f), types) { FairType = r.Float("fairType", -1f, -1f, 1f), Deck = DeckRule(r),
                    SoftnessScale = r.Float("softnessScale", 1f, 0.1f, 10f), WaterPower = r.Float("waterPower", 2f, 0.5f, 6f),
                    LightAmbient =r.Float("lightAmbient", 1f, 0f, 4f),
                    LightIsotropy = r.Float("lightIsotropy", 0f, 0f, 1f),
                    RainType = r.Float("rainType", 1f, 0f, 1f), FibreStretch = r.Float("fibreStretch", 1f, 1f, 50f), VerticalScale = r.Float("verticalScale", 0f, 0f, 1f),
                    AltoType = r.Float("altoType", -1f, -1f, 1f), AltoFrom = r.Float("altoFrom", 0.05f, 0f, 1f), AltoFull = r.Float("altoFullAt", 0.3f, 0f, 1f),
                    Meso = Meso(r.Child("MESO")), TypePatchKm = r.Float("typePatchKm", 0f, 0f, 2000f),
                    TypePatchDepths = r.Float("typePatchDepths", CloudMesoSpec.DefaultTypePatchDepths, 0.2f, 100f), TypePatchBlend = r.Float("typePatchBlend", 0.06f, 0.005f, 0.5f) });
            }
            return layers;
        }

        // The layer's deck rule (WeatherDeckRule): null without deckRule = 1, else the defaults with the keys of the layer's node.
        private static WeatherDeckRule DeckRule(BodyNodeReader r)
        {
            if (r.Float("deckRule", 0f, 0f, 1f) < 0.5f) return null;
            WeatherDeckRule d = WeatherDeckRule.Defaults;
            return new WeatherDeckRule
            {
                FogSpreadK = r.Float("deckFogSpreadK", d.FogSpreadK, 0f, 50f),
                FogSpreadBandK = r.Float("deckFogSpreadBandK", d.FogSpreadBandK, 0f, 50f),
                FogWindMs = r.Float("deckFogWindMs", d.FogWindMs, 0f, 100f),
                FogWindBandMs = r.Float("deckFogWindBandMs", d.FogWindBandMs, 0f, 100f),
                PrecipFrom = r.Float("deckPrecipFrom", d.PrecipFrom, 0f, 1f),
                PrecipFullAt = r.Float("deckPrecipFullAt", d.PrecipFullAt, 0f, 1f),
                FogType = r.Float("deckFogType", d.FogType, 0f, 1f),
                PrecipType = r.Float("deckPrecipType", d.PrecipType, 0f, 1f),
                Fog = r.Float("deckFog", d.Fog ? 1f : 0f, 0f, 1f) >= 0.5f,
                RainFloor = r.Float("deckRainFloor", d.RainFloor, 0f, 1f),
                RainFloorFullAt = r.Float("deckRainFloorFullAt", d.RainFloorFullAt, 0f, 1f)
            };
        }

        // TYPE { name, baseShare, topShare, erosion, PROFILE { key0 = x y, key1 = ... } }; null (with a warning) where the type is unusable.
        private static CloudTypeSpec CloudType(BodyNodeReader r, string layer)
        {
            string name = r.String("name", "");
            float baseShare = r.Float("baseShare", 0f, 0f, 1f), topShare = r.Float("topShare", 1f, 0f, 1f);
            if (name.Length == 0 || !(topShare > baseShare))
            {
                r.Warn("topShare", "a type of " + layer + " needs a name and baseShare below topShare; it is ignored");
                return null;
            }
            Curve profile = ProfileCurve(r.Child("PROFILE"));
            if (profile == null)
            {
                r.Warn("PROFILE", "the type " + name + " of " + layer + " needs a PROFILE with keys 'height density' (height 0 to 1 ascending); it is ignored");
                return null;
            }
            return new CloudTypeSpec(name, baseShare, topShare, r.Float("erosion", 0.3f, 0f, 1f), profile)
            {
                BaseGain = r.Float("baseGain", 0.3f, 0f, 1f),
                RiseShare = r.Float("riseShare", 0.5f, 0f, 1f),
                DensityScale = r.Float("densityScale", 1f, 0.001f, 100f),
                BaseLight = r.Float("baseLight", 1f, 0f, 4f),
                HeadLight = r.Float("headLight", 1f, 0f, 4f),
                BaseAmbient = r.Float("baseAmbient", 1f, 0f, 4f),
                HeadAmbient = r.Float("headAmbient", 1f, 0f, 4f),
                Lift = ProfileCurve(r.Child("LIFT"), "lift"),
                Climate = Climate(r.Child("CLIMATE"))
            };
        }

        // MESO { scaleKm, octaves, amount, edge, shoulder, stretch, rainKeep, scatter, scatterDepths, scatterSoft } (E40 L3, CloudMesoSpec); null without the node.
        private static CloudMesoSpec Meso(BodyNodeReader r)
        {
            if (!r.Present) return null;
            return new CloudMesoSpec
            {
                ScaleKm = r.Float("scaleKm", 0f, 0f, 2000f),
                ScaleDepths = r.Float("scaleDepths", 4.3f, 0.2f, 100f),
                ScaleSystems = r.Float("scaleSystems", 0f, 0f, 10f),                Octaves = r.Int("octaves", 2, 1, 4),
                Amount = r.Float("amount", 0f, 0f, 1f),
                Edge = r.Float("edge", 0.25f, 0.02f, 1f),
                Shoulder = r.Float("shoulder", 1f, 1f, 6f),
                Cells = r.Float("cells", 0f, 0f, 1f) >= 0.5f,
                Stretch = r.Float("stretch", 1f, 1f, 20f),
                RainKeep = r.Float("rainKeep", 1f, 0f, 1f),
                RainKeepFrom = r.Float("rainKeepFrom", 0.02f, 0f, 1f),
                RainKeepFullAt = r.Float("rainKeepFullAt", 0.25f, 0f, 1f),
                Scatter = r.Float("scatter", 0f, 0f, 1f),
                ScatterDepths = r.Float("scatterDepths", CloudMesoSpec.DefaultScatterDepths, 0.2f, 100f),
                ScatterSoft = r.Float("scatterSoft", 0.3f, 0.02f, 1f)
            };
        }

        // CLIMATE { weight, LATITUDE { key0 = x weight, ... }, SURFACE {...}, HUMIDITY {...}, PRECIPITATION {...} } (E40 L3, CloudClimateSpec); null without the node.
        private static CloudClimateSpec Climate(BodyNodeReader r)
        {
            if (!r.Present) return null;
            return new CloudClimateSpec
            {
                Weight = r.Float("weight", 1f, 0f, 1f),
                Latitude = ProfileCurve(r.Child("LATITUDE"), "weight"),
                Surface = ProfileCurve(r.Child("SURFACE"), "weight"),
                Humidity = ProfileCurve(r.Child("HUMIDITY"), "weight"),
                Precipitation = ProfileCurve(r.Child("PRECIPITATION"), "weight")
            };
        }

        // PROFILE { key0 = height density, ... } (and LIFT { key0 = height lift, ... }, the same form): heights 0 to 1 ascending, values 0 to 1. Null where there is no valid curve.
        private static Curve ProfileCurve(BodyNodeReader r, string what = "density")
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
                if (parts.Length != 2 || !BodyNodeReader.TryNumber(parts[0], out x) || !BodyNodeReader.TryNumber(parts[1], out y)
                    || x < 0.0 || x > 1.0 || y < 0.0 || y > 1.0 || (xs.Count > 0 && !(x > xs[xs.Count - 1])))
                {
                    r.Warn("key" + i, "'" + text + "' is not 'height " + what + "' with both in 0 to 1 and the heights ascending; the curve is ignored");
                    return null;
                }
                xs.Add((float)x);
                ys.Add((float)y);
            }
            return xs.Count == 0 ? null : new Curve(xs.ToArray(), ys.ToArray());
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
