using System.Collections.Generic;
using System.IO;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.B13;

namespace ReDefinition.Tests.Atmospheres.E41
{
    // Paths of the shipped files and the stock bodies as plain facts, for the tests of the per-body clouds, the body nodes and the
    // layer discovery. The numbers of the stock bodies are the game's published values, rounded; the tests need their kind (star,
    // atmosphere, surface, moon) and their order of size, not the digits.
    internal static class E41Support
    {
        internal static string Root()
        {
            return RepoRoot.Root("src", "ReDefinition.csproj");
        }

        internal static string CloudsFile(string body)
        {
            return Path.Combine(Root(), "GameData", "ReDefinition", "Atmospheres", "Clouds", "Clouds-" + body + ".cfg");
        }

        internal static string BodyFile(string body)
        {
            return Path.Combine(Root(), "GameData", "ReDefinition", "Atmospheres", "Bodies", body + ".cfg");
        }

        internal static string PackFixture(string file)
        {
            return Path.Combine(Root(), "tests", "data", "packs", file);
        }

        internal static ConfigNode BodyNode(string body)
        {
            return ConfigNode.Load(BodyFile(body)).GetNode(BodyConfigParser.NodeName);
        }

        internal static List<PackLayerInfo> Layers(params string[] files)
        {
            var all = new List<PackLayerInfo>();
            foreach (string file in files)
            {
                all.AddRange(PackConfigReader.Read(new[] { ConfigNode.Load(file) }, Path.GetFileName(file)));
            }
            return all;
        }

        // One stock body: the facts the probe would read and the status the checklist expects with the shipped nodes.
        internal sealed class Stock
        {
            internal BodyFacts Facts;
            internal string Expected;
            internal bool HasNode;
        }

        internal static List<Stock> StockBodies()
        {
            var list = new List<Stock>();
            list.Add(Make("Sun",261600000, 0, 0, 0, true, false, false, 0, "Off(Star)", false));
            list.Add(Make("Moho", 250000, 0, 0, 5.0e6, false, false, false, 2.2e6, "Off(NoAtmosphere)", false));
            list.Add(Make("Eve", 700000, 90000, 506.625, 80500, false, false, false, 5.657995e6, "Full", true));
            list.Add(Make("Gilly", 13000, 0, 0, 28255, false, true, false, 388587, "Off(NoAtmosphere)", false));
            list.Add(Make("Kerbin", 600000, 70000, 101.325, 21600, false, false, false, 9.203545e6, "Full", true));
            list.Add(Make("Mun", 200000, 0, 0, 138984, false, true, false, 138984, "Off(NoAtmosphere)", false));
            list.Add(Make("Minmus", 60000, 0, 0, 40400, false, true, false, 1.08e6, "Off(NoAtmosphere)", false));
            list.Add(Make("Duna", 320000, 50000, 6.755, 65518, false, false, false, 1.73154e7, "Full", true));
            list.Add(Make("Ike", 130000, 0, 0, 65518, false, true, false, 65518, "Off(NoAtmosphere)", false));
            list.Add(Make("Dres", 138000, 0, 0, 34800, false, false, false, 5.0e7, "Off(NoAtmosphere)", false));
            // Jool: an atmosphere, no surface and no ocean.
            list.Add(Make("Jool", 6000000, 200000, 1519.875, 36000, false, false, false, 1.05e8, "Off(NoSurface)", true, false));
            list.Add(Make("Laythe", 500000, 50000, 60.795, 52980, false, true, true, 1.05e8, "Full", true));
            list.Add(Make("Vall", 300000, 0, 0, 105962, false, true, false, 1.05e8, "Off(NoAtmosphere)", false));
            list.Add(Make("Tylo", 600000, 0, 0, 211926, false, true, false, 1.05e8, "Off(NoAtmosphere)", false));
            list.Add(Make("Bop", 65000, 0, 0, 544507, false, true, false, 1.05e8, "Off(NoAtmosphere)", false));
            list.Add(Make("Pol", 44000, 0, 0, 901903, false, true, false, 1.05e8, "Off(NoAtmosphere)", false));
            list.Add(Make("Eeloo", 210000, 0, 0, 19460, false, false, false, 1.9e8, "Off(NoAtmosphere)", false));
            return list;
        }

        private static Stock Make(string name, double radius, double depth, double kPa, double day, bool star, bool moon, bool ocean, double orbit,
            string expected, bool hasNode, bool surface = true)
        {
            var f = new BodyFacts
            {
                Name = name,
                Index = 1,
                RadiusM = radius,
                DaySeconds = day,
                IsStar = star,
                HasAtmosphere = depth > 0,
                AtmosphereDepthM = depth,
                SeaLevelPressureKPa = kPa,
                SeaLevelTemperatureK = depth > 0 ? 288.0 : double.NaN,
                HasSurface = surface && !star,
                HasOcean = ocean,
                OrbitPeriod = orbit,
                IsMoon = moon,
                ParentOrbitPeriod = moon ? 1.05e8 : 0.0,
                HomeOrbitPeriod = 9.203545e6
            };
            return new Stock { Facts = f, Expected = expected, HasNode = hasNode };
        }
    }
}
