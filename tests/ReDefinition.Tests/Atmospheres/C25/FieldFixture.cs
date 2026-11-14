using System.Collections.Generic;
using AtmoSim.Core;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.B13;

namespace ReDefinition.Tests.Atmospheres.C25
{
    // A weather field over a body made from what the game would tell: the facts and the rules of a stock-like home planet, the recorded
    // heights and temperature curves of the B09 dumps.
    internal static class FieldFixture
    {
        internal const double HomeYear = 9203544.6;
        internal const int BodyIndex = 3;

        // The facts the probe reads for a home-planet-like body.
        internal static BodyFacts HomeFacts(string name = "Testworld")
        {
            BodyFacts f = BodyTestSupport.Planet(name, HomeYear);
            f.Index = BodyIndex;
            f.RadiusM = 600000.0;
            f.DaySeconds = 21549.425;
            f.AtmosphereDepthM = 70000.0;
            f.SeaLevelPressureKPa = 101.325;
            f.SeaLevelTemperatureK = 309.6;
            f.HasOcean = true;
            f.SurfaceGravityMs2 = 9.81;
            f.SolarFluxWm2 = 1360.0;
            f.GasConstant = 287.0;
            f.Cp = 1004.0;
            return f;
        }

        internal static BodyClimate Climate(BodyFacts facts, string nodeText = null)
        {
            return BodyTestSupport.Run(facts, nodeText).Climate;
        }

        // The parameter set of the home-planet-like body with the recorded heights and temperature fit.
        internal static BodyParams Params(BodyClimate climate, BodyFacts facts, string dumpBody = "Kerbin")
        {
            int w, h;
            bool ocean;
            float[] heights = DumpReader.Heights(dumpBody, out w, out h, out ocean);
            return GeneratorBodies.Build(climate, facts, heights, ThermalFit.From(DumpReader.Thermal(dumpBody)), new List<string>());
        }

        internal static GeneratorState State(BodyClimate climate, BodyParams parameters)
        {
            string error;
            GeneratorState s = GeneratorState.Create(BodyIndex, climate.Name, parameters, GeneratorBodies.SeedOf(climate), null, ReDefinition.Core.PrecipType.None, out error);
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsNotNull(s, error);
            return s;
        }

        // A second body of another size and atmosphere (P1, planet independence): the facts of the stock ocean moon of the gas giant (radius 500 km, atmosphere 50 km deep, 60.8 kPa, g 7.85,
        // a day of 52981 s) with its recorded heights and temperature fit (dump body "Laythe"). Its troposphere is 7.5 km deep against the home planet's 10.5 km.
        internal static BodyFacts SecondFacts()
        {
            BodyFacts f = BodyTestSupport.Planet("Secondworld", HomeYear);
            f.Index = BodyIndex;
            f.RadiusM = 500000.0;
            f.DaySeconds = 52980.88;
            f.AtmosphereDepthM = 50000.0;
            f.SeaLevelPressureKPa = 60.795;
            f.SeaLevelTemperatureK = 290.0;
            f.HasOcean = true;
            f.SurfaceGravityMs2 = 7.85;
            f.SolarFluxWm2 = 53.0;
            f.GasConstant = 287.0;
            f.Cp = 1004.0;
            return f;
        }

        // A field with the second body (SecondFacts, dump heights and temperatures of "Laythe"), ready, maps rendered inline.
        internal static GeneratorField SecondField(int seed = 1)
        {
            BodyFacts facts = SecondFacts();
            var climate = new BodyClimate("Secondworld", BodyIndex, facts.RadiusM, HomeYear, facts.DaySeconds, BodyStatus.Full, seed);
            var field = new GeneratorField();
            field.Say = s => { };
            field.Maps.Synchronous = true;
            field.SetState(State(climate, Params(climate, facts, "Laythe")));
            return field;
        }

        // A field with one body, ready, maps rendered inline.
        internal static GeneratorField Field(int seed = 1, string dumpBody = "Kerbin")
        {
            BodyFacts facts = HomeFacts();
            var climate = new BodyClimate("Testworld", BodyIndex, 600000.0, HomeYear, 21549.425, BodyStatus.Full, seed);
            var field = new GeneratorField();
            field.Say = s => { };
            field.Maps.Synchronous = true;
            field.SetState(State(climate, Params(climate, facts, dumpBody)));
            return field;
        }
    }
}
