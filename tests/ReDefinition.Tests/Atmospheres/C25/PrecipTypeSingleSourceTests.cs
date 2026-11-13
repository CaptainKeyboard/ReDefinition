using System;
using AtmoSim.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.C25
{
    // The type of a fall has one definition (AtmoSim.Core.PrecipTypeMath): the weather generator, whose fractions the forecast, the cover
    // and the ground effects read, and the frame driver, which draws the sky with PrecipTypeModel, must give the same answer.
    [TestClass]
    public class PrecipTypeSingleSourceTests
    {
        private const int Body = FieldFixture.BodyIndex;

        [TestMethod]
        public void TheModelAndTheCoreFormulaAgreeOverATemperatureAndLevelSweep()
        {
            int compared = 0;
            for (double kelvin = 245.0; kelvin <= 300.0; kelvin += 0.37)
            {
                for (double level = 0.0; level <= 1.0001; level += 0.05)
                {
                    double snow, sleet, rain;
                    PrecipTypeMath.Fractions(kelvin, level, PrecipTypeMath.DefaultCentreC, PrecipTypeMath.DefaultWidthC, out snow, out sleet, out rain);
                    TypeMix mix = PrecipTypeModel.Mix((float)kelvin, (float)level, PrecipTypeModel.DefaultCentreC, PrecipTypeModel.DefaultWidthC);
                    double sum = snow + sleet + rain;
                    Assert.AreEqual(snow / sum, mix.Snow, 2e-5, "snow at " + kelvin + " K, level " + level);
                    Assert.AreEqual(sleet / sum, mix.Sleet, 2e-5, "sleet at " + kelvin + " K, level " + level);
                    Assert.AreEqual(rain / sum, mix.Rain, 2e-5, "rain at " + kelvin + " K, level " + level);
                    compared++;
                }
            }
            Assert.IsTrue(compared > 2000);
        }

        [TestMethod]
        public void TheGeneratorsFractionsAreTheModelsAtThePointsTemperatureAndLevel()
        {
            GeneratorField field = FieldFixture.Field();
            // The fixture's body has no PRECIP rule: both sides use the defaults of the model.
            float centre = PrecipTypeModel.DefaultCentreC, width = PrecipTypeModel.DefaultWidthC;
            int precipitating = 0, snowy = 0, rainy = 0, sleety = 0;
            double warmest = double.MinValue, coldest = double.MaxValue;
            for (double ut = 0; ut < 3.0e6; ut += 36000.0)
            {
                for (int lat = -85; lat <= 85; lat += 10)
                {
                    for (int lon = -180; lon < 180; lon += 20)
                    {
                        WeatherSample s;
                        field.Sample(Body, lat, lon, 0, ut, out s);
                        if (!(s.Level01 > 0f) || s.Type == PrecipType.Dust) continue;
                        precipitating++;
                        warmest = Math.Max(warmest, s.TemperatureK);
                        coldest = Math.Min(coldest, s.TemperatureK);
                        TypeMix mix = PrecipTypeModel.Mix(s.TemperatureK, s.Level01, centre, width);
                        string at = "lat " + lat + " lon " + lon + " ut " + ut + " T " + s.TemperatureK + " level " + s.Level01;
                        Assert.AreEqual(mix.Snow, s.SnowFraction, 2e-3, "snow, " + at);
                        Assert.AreEqual(mix.Sleet, s.SleetFraction, 2e-3, "sleet, " + at);
                        Assert.AreEqual(mix.Rain, s.RainFraction, 2e-3, "rain, " + at);
                        // The forecast says what the sky draws, except in a tie of two fractions (float against double).
                        if (Math.Abs(mix.Snow - mix.Sleet) > 2e-3 && Math.Abs(mix.Rain - mix.Sleet) > 2e-3 && Math.Abs(mix.Snow - mix.Rain) > 2e-3)
                            Assert.AreEqual(mix.Dominant, s.Type, at);
                        if (s.Type == PrecipType.Snow) snowy++;
                        else if (s.Type == PrecipType.Rain) rainy++;
                        else if (s.Type == PrecipType.Sleet) sleety++;
                    }
                }
            }
            Assert.IsTrue(precipitating > 200, "points with a fall: " + precipitating);
            Assert.IsTrue(snowy > 0 && rainy > 0, "the sweep has to cross the change from snow to rain: snow " + snowy + ", rain " + rainy + ", sleet " + sleety);
            Assert.IsTrue(warmest - coldest > 20.0, "temperature range of the falls: " + (warmest - coldest));
        }

        [TestMethod]
        public void OnlyTheHistoricalProfilesKeepTheOldFixedModelAndTheCurrentOneUsesTheUnifiedOne()
        {
            Assert.IsFalse(BodyParams.KerbinLike().PrecipTypeLegacy);
            Assert.IsTrue(BodyParams.KerbinLike().Round1().PrecipTypeLegacy);
            Assert.IsTrue(BodyParams.KerbinLike().Round2().PrecipTypeLegacy);
            Assert.IsTrue(BodyParams.KerbinLike().Round3().PrecipTypeLegacy);
            Assert.IsTrue(BodyParams.KerbinLike().Signed().PrecipTypeLegacy);
            for (double kelvin = 260.0; kelvin <= 285.0; kelvin += 0.5)
            {
                double snow, sleet, rain;
                PrecipTypeMath.LegacyFractions(kelvin, 273.15, out snow, out sleet, out rain);
                double fs = 1.0 / (1.0 + Math.Exp((kelvin - 273.15 - 1.2 - 1.0) / 0.9));
                Assert.AreEqual(fs, snow, 1e-9);
                Assert.AreEqual(4.0 * fs * (1.0 - fs), sleet, 1e-9);
                Assert.AreEqual(1.0 - fs, rain, 1e-9);
            }
        }

        [TestMethod]
        public void AToolGeneratorWithoutALevelTableStillGivesFractionsThatAddUp()
        {
            BodyParams p = BodyParams.KerbinLike();
            Assert.IsNull(p.PrecipLevelOf);
            var g = new WeatherGenerator(p, 7UL);
            Weather w;
            for (int i = 0; i < 400; i++)
            {
                g.Sample(0.4 * Math.Sin(i), 0.01 * i, 0.0, 1.0e5 * i, out w);
                if (w.PrecipMmH > 0.0) Assert.IsTrue(w.SnowFrac + w.RainFrac <= 1.0000001 && w.SnowFrac >= 0.0 && w.RainFrac >= 0.0);
            }
        }
    }
}
