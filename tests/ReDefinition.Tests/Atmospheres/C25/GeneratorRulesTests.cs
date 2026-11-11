using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.B13;

namespace ReDefinition.Tests.Atmospheres.C25
{
    // The GENERATOR node of a body (GeneratorRules), the genEnabled and genSeed settings and the fit of the game's temperature curves.
    [TestClass]
    public class GeneratorRulesTests
    {
        [TestMethod]
        public void ABodyWithoutANodeStatesNothingAndTheGameDecides()
        {
            GeneratorRules g = BodyTestSupport.Run(BodyTestSupport.Planet("Testworld", BodyTestSupport.HomeYear)).Climate.Generator;
            Assert.IsTrue(g.Enabled && g.Waves && g.Cells);
            Assert.AreEqual(GeneratorOceanSource.Terrain, g.OceanSource);
            Assert.AreEqual(GeneratorDust.Auto, g.Dust);
            foreach (double v in new[] { g.RotationPeriodS, g.ObliquityDeg, g.SolarFluxWm2, g.SurfacePressureKPa, g.GravityMs2, g.TropopauseM, g.LapseKPerM, g.SurfaceMeanK,
                g.DiurnalLandAmpK, g.GasConstant, g.Cp, g.PrecipFactor, g.OceanFraction, g.SalinityPsu, g.FreezeShiftKPerPsu, g.MixedLayerDepthM, g.SeaHeatCapacity, g.DustWindThresholdMs })
            {
                Assert.IsTrue(double.IsNaN(v), "a number the node leaves out is NaN (read from the game)");
            }
            Assert.AreEqual(0, g.Levers.Count);
        }

        [TestMethod]
        public void ANodeStatesWhatDiffersAndTheLeversAreRead()
        {
            BodyParseResult r = BodyTestSupport.Run(BodyTestSupport.Planet("X", BodyTestSupport.HomeYear),
                "REDEFINITION_BODY { name = X ; GENERATOR { enabled = false ; rotationPeriodS = 40000 ; obliquityDeg = 12 ; oceanSource = Synthetic ; oceanFraction = 0.3 ;"
                + " dust = On ; seedOffset = 5 ; waves = false ; LEVERS { SeasonalResponse = 0.5 ; CellTrigger = 0 } } }");
            GeneratorRules g = r.Climate.Generator;
            Assert.AreEqual(0, r.Warnings.Count);
            Assert.IsFalse(g.Enabled);
            Assert.IsFalse(g.Waves);
            Assert.IsTrue(g.Cells);
            Assert.AreEqual(40000.0, g.RotationPeriodS);
            Assert.AreEqual(12.0, g.ObliquityDeg);
            Assert.AreEqual(GeneratorOceanSource.Synthetic, g.OceanSource);
            Assert.AreEqual(0.3, g.OceanFraction);
            Assert.AreEqual(GeneratorDust.On, g.Dust);
            Assert.AreEqual(5, g.SeedOffset);
            Assert.AreEqual(2, g.Levers.Count);
            Assert.AreEqual(0.5, g.Levers["SeasonalResponse"]);
            Assert.AreEqual(0.0, g.Levers["CellTrigger"]);
        }

        [TestMethod]
        public void TheGeneratorSettingsAreTheSimEnabledAndSimSeedReplacements()
        {
            Assert.IsNotNull(AtmospheresSettings.Definition("genEnabled"));
            Assert.IsNotNull(AtmospheresSettings.Definition("genSeed"));
            Assert.IsNull(AtmospheresSettings.Definition("simEnabled"));
            Assert.IsNull(AtmospheresSettings.Definition("simSeed"));
            Assert.AreEqual("Weather Generator", AtmospheresSettings.Definition("genEnabled").Tooltip);
        }

        [TestMethod]
        public void TheFitOfTheRecordedCurvesGivesTheNumbersOfTheProof()
        {
            // The offline proof read these from the same dumps (RESULT.md H2): mean, equator, pole, lapse and diurnal amplitude.
            AssertFit("Kerbin", 286.6, 309.6, 240.6, 7.11, 4.45);
            AssertFit("Duna", 249.7, 262.0, 185.5, 1.33, 9.0);
            AssertFit("Laythe", 282.1, 285.0, 248.0, 13.4, 3.0);
            AssertFit("Eve", 407.6, 424.5, 364.0, 9.55, 4.5);
        }

        private static void AssertFit(string body, double mean, double equator, double pole, double lapsePerKm, double diurnal)
        {
            ThermalFit f = ThermalFit.From(DumpReader.Thermal(body));
            Assert.IsNotNull(f, body);
            Assert.AreEqual(mean, f.MeanK, 0.06, body + " mean");
            Assert.AreEqual(equator, f.EquatorK, 0.06, body + " equator");
            Assert.AreEqual(pole, f.PoleK, 0.06, body + " pole");
            Assert.AreEqual(lapsePerKm, f.LapseKPerM * 1000.0, 0.03, body + " lapse");
            Assert.AreEqual(diurnal, f.DiurnalAmpK, 0.006, body + " diurnal amplitude");
        }

        [TestMethod]
        public void AnUnusableTableGivesNoFit()
        {
            Assert.IsNull(ThermalFit.From(null));
            Assert.IsNull(ThermalFit.From(new ThermalSamples()));
            ThermalSamples s = DumpReader.Thermal("Kerbin");
            s.Kelvin[3] = double.NaN;
            Assert.IsNull(ThermalFit.From(s));
        }
    }
}
