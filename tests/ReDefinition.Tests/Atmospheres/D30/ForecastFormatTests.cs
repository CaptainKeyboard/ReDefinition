using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.D30
{
    // D30: the strings of the forecast interface. Levels carry their number and letter, temperatures are degrees Celsius, latitudes read
    // "deg N/S latitude", the wind label names its source.
    [TestClass]
    public class ForecastFormatTests
    {
        [TestMethod]
        public void LevelsCarryTheirNumberAndLetterSoThatColourIsNotTheOnlySignal()
        {
            Assert.AreEqual("Heavy (4)", ForecastFormat.LevelName(PrecipBand.Heavy));
            Assert.AreEqual("Flurries (1)", ForecastFormat.LevelName(PrecipBand.Flurries));
            Assert.AreEqual("H4", ForecastStrings.Cell(PrecipBand.Heavy));
            Assert.AreEqual("B5", ForecastStrings.Cell(PrecipBand.Blizzard));
            Assert.AreEqual("-0", ForecastStrings.Cell(PrecipBand.None));
            var letters = new System.Collections.Generic.HashSet<string>();
            var colours = new System.Collections.Generic.HashSet<string>();
            for (int b = 0; b <= 5; b++)
            {
                letters.Add(ForecastFormat.Letter((PrecipBand)b));
                colours.Add(ForecastFormat.Hex((PrecipBand)b));
            }
            Assert.AreEqual(6, letters.Count, "Each band has its own letter.");
            Assert.AreEqual(6, colours.Count, "Each band has its own colour.");
        }

        [TestMethod]
        public void DirectionsAndDistancesReadAsTheCompassAndMetres()
        {
            Assert.AreEqual("N", ForecastFormat.Direction(0));
            Assert.AreEqual("NE", ForecastFormat.Direction(44));
            Assert.AreEqual("W", ForecastFormat.Direction(270));
            Assert.AreEqual("N", ForecastFormat.Direction(359));
            Assert.AreEqual("NW", ForecastFormat.Direction(-45));
            Assert.AreEqual("850 m", ForecastFormat.Distance(850));
            Assert.AreEqual("4.2 km", ForecastFormat.Distance(4200));
            Assert.AreEqual("12 km", ForecastFormat.Distance(12400));
            Assert.AreEqual("-", ForecastFormat.Distance(double.NaN));
            Assert.AreEqual("> 10 km", ForecastFormat.Visibility(100000f));
            Assert.AreEqual(90.0, ForecastFormat.Bearing(0, 0, 0, 10), 1e-6);
            Assert.AreEqual(0.0, ForecastFormat.Bearing(0, 0, 10, 0), 1e-6);
            Assert.AreEqual(270.0, ForecastFormat.Bearing(28.5, -80.5, 28.5, -81.8), 0.5);
        }

        [TestMethod]
        public void TemperaturesAreDegreesCelsiusAndPlacesUseDegNSLatitude()
        {
            Assert.AreEqual("-5 °C", ForecastFormat.Temperature(268.15f));
            Assert.AreEqual("15 °C", ForecastFormat.Temperature(288.15f));
            Assert.AreEqual("-", ForecastFormat.Temperature(float.NaN));
            Assert.AreEqual("28.5 deg N latitude", ForecastFormat.Latitude(28.5));
            Assert.AreEqual("33.0 deg S latitude", ForecastFormat.Latitude(-33.04));
            Assert.AreEqual("80.5 deg W longitude", ForecastFormat.Longitude(-80.5));
            Assert.AreEqual("80.5 deg E longitude", ForecastFormat.Longitude(80.5));
            Assert.AreEqual("9 m/s NW",ForecastFormat.Wind(9f, 315f));
            Assert.AreEqual("0 m/s", ForecastFormat.Wind(0.2f, 90f));
        }

        [TestMethod]
        public void LeadTimesReadAsOffsetsFromNow()
        {
            Assert.AreEqual("now", ForecastFormat.Lead(0));
            Assert.AreEqual("+30 min", ForecastFormat.Lead(1800));
            Assert.AreEqual("+1 h", ForecastFormat.Lead(3600));
            Assert.AreEqual("+2 h 30 min", ForecastFormat.Lead(9000));
            Assert.AreEqual("Heavy snow", ForecastFormat.Precip(PrecipBand.Heavy, PrecipType.Snow));
            Assert.AreEqual("Clear", ForecastFormat.Precip(PrecipBand.None, PrecipType.None));
        }

        [TestMethod]
        public void TheWindLabelNamesItsSourceAndFadesForPersistence()
        {
            Assert.AreEqual("Wind: ours", ForecastFormat.WindSourceLabel(WeatherSource.Ours()));
            Assert.AreEqual("Wind: KWP", ForecastFormat.WindSourceLabel(WeatherSource.Foreign("KWP")));
            Assert.AreEqual("Wind: KWP, now only", ForecastFormat.WindSourceLabel(WeatherSource.ExternalNowcast("KWP")));
            Assert.AreEqual(1f, ForecastFormat.ConfidenceAlpha(WeatherSource.Ours(), 0.2f), "Our own wind is not faded.");
            Assert.AreEqual(1f, ForecastFormat.ConfidenceAlpha(WeatherSource.Foreign("KWP"), 0.2f));
            float near = ForecastFormat.ConfidenceAlpha(WeatherSource.ExternalNowcast("KWP"), 1f);
            float far = ForecastFormat.ConfidenceAlpha(WeatherSource.ExternalNowcast("KWP"), 0f);
            Assert.AreEqual(1f, near);
            Assert.IsTrue(far < 0.5f && far > 0f, "A carried-forward wind fades with the lead: " + far);
        }

        [TestMethod]
        public void TheStateTextSaysComputingOrApproximateOrOff()
        {
            Assert.AreEqual("", ForecastFormat.StateText(true, true, FieldReadiness.Ready, true));
            StringAssert.Contains(ForecastFormat.StateText(true, true, FieldReadiness.Ready, false), "computing");
            StringAssert.Contains(ForecastFormat.StateText(true, true, FieldReadiness.CatchingUp, false), "approximate");
            StringAssert.Contains(ForecastFormat.StateText(true, true, FieldReadiness.Seeding, true), "approximate");
            StringAssert.Contains(ForecastFormat.StateText(true, true, FieldReadiness.Broken, true), "stopped");
            StringAssert.Contains(ForecastFormat.StateText(false, true, FieldReadiness.Ready, true), "switched off");
        }
    }
}
