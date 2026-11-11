using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // What a body without its own PRECIP and COVER nodes gets: a guess from the names of EVE's particle fields that belong to it and
    // from the temperature at its surface. Pure; the body's node always wins over it.
    internal static class BodyGuess
    {
        // The wet-bulb temperature, above the freezing point of the body's water, at which half of the precipitation is rain.
        internal const double WetBulbCentreAboveFreezingC = 1.0;
        // A surface hotter than the boiling point plus this, or colder than the freezing point minus the second number, has no water precipitation.
        internal const double HotAboveBoilingC = 0.0;
        internal const double ColdBelowFreezingC = 60.0;
        private const double BoilingAboveFreezingC = 100.0;

        // Whether the surface is too hot or too cold for water to fall; false where the temperature is unknown.
        internal static bool OutsideWaterRange(double seaLevelKelvin, double waterCelsius)
        {
            if (double.IsNaN(seaLevelKelvin) || seaLevelKelvin <= 0) return false;
            double c = seaLevelKelvin - 273.15;
            return c > waterCelsius + BoilingAboveFreezingC + HotAboveBoilingC || c < waterCelsius - ColdBelowFreezingC;
        }

        // The kind of precipitation a field name stands for ("snow-Laythe" is snow); None where the prefix is not a known kind.
        internal static PrecipType KindOfField(string field)
        {
            if (string.IsNullOrEmpty(field)) return PrecipType.None;
            int dash = field.IndexOf('-');
            string prefix = dash < 0 ? field : field.Substring(0, dash);
            PrecipType kind;
            if (Enum.TryParse(prefix, true, out kind) && Enum.IsDefined(typeof(PrecipType), kind) && kind != PrecipType.None) return kind;
            return PrecipType.None;
        }

        // The types of a body from its particle fields. Fields of snow or rain give both (a water cycle: what is not found as a field
        // is drawn by our own renderer); fields of dust, sand, ash or methane fall at any temperature. No fields: a surface outside
        // the range of liquid water gets none, any other gets an empty list (the type then follows the temperature only).
        internal static List<PrecipTypeRule> Precip(BodyFacts facts, double waterCelsius)
        {
            var types = new List<PrecipTypeRule>();
            string snowField = "";
            string rainField = "";
            var others = new List<PrecipTypeRule>();
            foreach (string field in facts.EveFields ?? new List<string>())
            {
                PrecipType kind = KindOfField(field);
                if (kind == PrecipType.Snow || kind == PrecipType.Sleet) snowField = field;
                else if (kind == PrecipType.Rain) rainField = field;
                else if (kind != PrecipType.None && kind != PrecipType.FreezingRain)
                {
                    others.Add(new PrecipTypeRule(kind.ToString().ToLowerInvariant(), kind, -273.15, 1.0, false, field));
                }
            }
            double centre = waterCelsius + WetBulbCentreAboveFreezingC;
            if (snowField.Length > 0 || rainField.Length > 0)
            {
                types.Add(new PrecipTypeRule("snow", PrecipType.Snow, centre, 1.0, true, snowField));
                types.Add(new PrecipTypeRule("rain", PrecipType.Rain, centre, 1.0, false, rainField));
            }
            types.AddRange(others);
            if (types.Count == 0 && OutsideWaterRange(facts.SeaLevelTemperatureK, waterCelsius))
            {
                types.Add(new PrecipTypeRule("none", PrecipType.None));
            }
            return types;
        }

        // The ground cover of a body with a solid surface from its types: snow where snow falls, a layer of dust, sand or ash where those fall.
        internal static List<CoverRules> Covers(BodyFacts facts, IEnumerable<PrecipTypeRule> types)
        {
            var covers = new List<CoverRules>();
            if (!facts.HasSurface) return covers;
            foreach (PrecipTypeRule type in types)
            {
                switch (type.Kind)
                {
                    case PrecipType.Snow:
                        covers.Add(new CoverRules("snow", CoverKind.Snow));
                        break;
                    case PrecipType.Dust:
                        covers.Add(new CoverRules("dust", CoverKind.Dust, new Color(0.50f, 0.42f, 0.33f, 1f)));
                        break;
                    case PrecipType.Sand:
                        covers.Add(new CoverRules("sand", CoverKind.Sand, new Color(0.62f, 0.52f, 0.36f, 1f)));
                        break;
                    case PrecipType.Ash:
                        covers.Add(new CoverRules("ash", CoverKind.Ash, new Color(0.22f, 0.21f, 0.20f, 1f)));
                        break;
                }
            }
            return covers;
        }
    }
}
