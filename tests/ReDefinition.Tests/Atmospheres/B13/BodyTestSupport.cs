using System.Collections.Generic;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.B13
{
    // Synthetic bodies for the body-configuration tests: the facts the probe would read, written by hand. The numbers are round
    // stand-ins of a stock-like system, not read from the game.
    internal static class BodyTestSupport
    {
        internal const double HomeYear = 9.2e6;
        internal const double OuterYear = 1.05e8;
        internal const double DunaYear = 1.73154e7;

        internal static BodyFacts Planet(string name, double year, bool atmosphere = true, bool surface = true)
        {
            return new BodyFacts
            {
                Name = name,
                Index = 3,
                RadiusM = 500000.0,
                DaySeconds = 20000.0,
                HasAtmosphere = atmosphere,
                AtmosphereDepthM = atmosphere ? 60000.0 : 0.0,
                SeaLevelPressureKPa = atmosphere ? 100.0 : 0.0,
                SeaLevelTemperatureK = 288.0,
                HasSurface = surface,
                OrbitPeriod = year,
                HomeOrbitPeriod = HomeYear
            };
        }

        internal static BodyFacts Moon(string name, double parentYear, bool atmosphere, bool surface = true, bool ocean = false)
        {
            BodyFacts f = Planet(name, 500000.0, atmosphere, surface);
            f.IsMoon = true;
            f.ParentOrbitPeriod = parentYear;
            f.HasOcean = ocean;
            return f;
        }

        // A node from a compact text: braces and semicolons stand for line breaks (a ConfigNode wants one key per line).
        internal static ConfigNode Root(string text)
        {
            return ConfigNode.Parse(text.Replace(";", "\n").Replace("{", "\n{\n").Replace("}", "\n}\n"));
        }

        internal static ConfigNode Parse(string text)
        {
            return Root(text).GetNode(BodyConfigParser.NodeName);
        }

        internal static BodyParseResult Run(BodyFacts facts, string nodeText = null, BodyPlayerOptions player = null)
        {
            return BodyConfigParser.Parse(facts, nodeText == null ? null : Parse(nodeText), player, 7);
        }

        internal static List<string> Names(IEnumerable<PrecipTypeRule> types)
        {
            var result = new List<string>();
            foreach (PrecipTypeRule t in types) result.Add(t.Id);
            return result;
        }
    }
}
