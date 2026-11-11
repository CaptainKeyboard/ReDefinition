using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Core;
using ReDefinition.Dashboard;
using ReDefinition.Hud;
using ReDefinition.Hud.Readouts;

namespace ReDefinition.Tests.Atmospheres.D30
{
    // D30: the flight panel's weather line and the dashboard's model of the forecast: the texts, the absence without a forecast, and the
    // JSON round trip.
    [TestClass]
    public class WeatherLineAndDashboardTests
    {
        private const double H = D30Fixtures.H;

        [TestMethod]
        public void TheLineNamesTheNextPrecipitationWithItsTimeAndTheWindNow()
        {
            ForecastReport report = D30Fixtures.Report(0.0, PrecipBand.None, PrecipBand.None, PrecipBand.Light, PrecipBand.Heavy, PrecipBand.None);
            report.Steps[0].WindMs = 9f;
            // From 0:00 on: Heavy is the strongest of the run that begins at 2 h; the first step is 8.5 m/s -> "9 m/s".
            string text = WeatherLine.Text(report, 0.0);
            Console.WriteLine("D30 line: " + text);
            Assert.AreEqual("Weather: Heavy snow in 2 h | wind 9 m/s", text);
            Assert.AreEqual("Weather: Heavy snow in 1 h 10 min | wind 9 m/s", WeatherLine.Text(report, 3000.0), "From 50 minutes in, the next step is 70 minutes away.");
            Assert.AreEqual("Weather: Heavy snow in 1 h | wind 7 m/s", WeatherLine.Text(report, H), "From the second step on, its own wind.");
        }

        [TestMethod]
        public void NowLineClearLineAndNoLine()
        {
            ForecastReport report = D30Fixtures.Report(0.0, PrecipBand.Moderate, PrecipBand.Moderate, PrecipBand.None);
            StringAssert.StartsWith(WeatherLine.Text(report, 0.0), "Weather: Moderate snow now | wind ");
            ForecastReport clear = D30Fixtures.Report(0.0, PrecipBand.None, PrecipBand.None);
            StringAssert.StartsWith(WeatherLine.Text(clear, 0.0), "Weather: clear | wind ");
            Assert.IsNull(WeatherLine.Text(null, 0.0), "No forecast, no line.");
            ForecastReport unavailable = D30Fixtures.Report(0.0, PrecipBand.Heavy);
            unavailable.Available = false;
            Assert.IsNull(WeatherLine.Text(unavailable, 0.0), "While the data is not available there is no line.");
            Assert.IsNull(WeatherLine.Text(new ForecastReport { Available = true }, 0.0));
        }

        [TestMethod]
        public void TheLineIsAReadoutTheProfilesCanNameAndRegisteringTwiceAddsItOnce()
        {
            Readout readout = WeatherLine.MakeReadout();
            Assert.AreEqual("weather", readout.Key);
            Assert.AreEqual("WEATHER", readout.Title);
            Assert.IsTrue(readout.Paired);
            Assert.IsNotNull(readout.Applies);
            ReDefinition.Hud.Readouts.ReadoutCatalog.All.RemoveAll(r => r.Key == "weather");
            WeatherLine.Register();
            WeatherLine.Register();
            Assert.AreEqual(1, ReDefinition.Hud.Readouts.ReadoutCatalog.All.Count(r => r.Key == "weather"));
            ReDefinition.Hud.Readouts.ReadoutCatalog.All.RemoveAll(r => r.Key == "weather");
        }

        [TestMethod]
        public void TheDashboardModelRoundTripsThroughItsJson()
        {
            ForecastReport report = D30Fixtures.Report(1000.0, PrecipBand.None, PrecipBand.Light, PrecipBand.Heavy);
            report.Wind = WeatherSource.Foreign("KWP");
            report.Generation = 7;
            report.NearestSystemEtaUt = 8000.0;
            report.NearestSystemDistanceM = double.NaN;
            ForecastSite site = D30Fixtures.Site("Space Centre");
            WeatherDashboardModel model = WeatherDashboardModel.From(report, site, "Kerbin");
            string json = model.Encode();
            Console.WriteLine("D30 dashboard json: " + json.Substring(0, Math.Min(160, json.Length)));
            WeatherDashboardModel back = WeatherDashboardModel.Decode(json);
            Assert.IsTrue(back.Available);
            Assert.AreEqual("Kerbin", back.Body);
            Assert.AreEqual("Space Centre", back.Site);
            Assert.AreEqual(28.5, back.LatDeg, 1e-9);
            Assert.AreEqual(-80.5, back.LonDeg, 1e-9);
            Assert.AreEqual("Foreign(KWP)", back.WindSource);
            Assert.AreEqual(7L, back.Generation);
            Assert.AreEqual(8000.0, back.NearestEtaUt, 1e-6);
            Assert.IsTrue(double.IsNaN(back.NearestDistanceM), "A NaN is written as null and read as NaN.");
            Assert.AreEqual(3, back.Ut.Length);
            CollectionAssert.AreEqual(new[] { "None", "Light", "Heavy" }, back.Band);
            CollectionAssert.AreEqual(new[] { "None", "Snow", "Snow" }, back.Type);
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(report.Steps[i].Ut, back.Ut[i], 1e-6);
                Assert.AreEqual(report.Steps[i].WindMs, back.WindMs[i], 1e-5);
                Assert.AreEqual(report.Steps[i].VisibilityM, back.VisibilityM[i], 1e-2);
                Assert.AreEqual(report.Steps[i].Level01, back.Level[i], 1e-6);
            }
        }

        [TestMethod]
        public void TheAlertFeedKeepsTheLastAlertsAndRoundTripsAndCanBeSwitchedOff()
        {
            var feed = new WeatherAlertFeed();
            for (int i = 0; i < WeatherAlertFeed.Capacity + 5; i++)
            {
                feed.Add(new ForecastAlert { Kind = AlertKind.Arrived, Ut = i * H, Band = PrecipBand.Heavy, Site = D30Fixtures.Site(), Text = "Heavy snow at Pad " + i });
            }
            Assert.AreEqual(WeatherAlertFeed.Capacity, feed.Length);
            Assert.AreEqual(WeatherAlertFeed.Capacity + 5, (int)feed.Count);
            var decoded = Json.DecodeObject(feed.Encode());
            var text = (System.Collections.Generic.List<object>)decoded["text"];
            Assert.AreEqual(WeatherAlertFeed.Capacity, text.Count);
            Assert.AreEqual("Heavy snow at Pad " + (WeatherAlertFeed.Capacity + 4), (string)text.Last());
            feed.Enabled = false;
            feed.Add(new ForecastAlert { Text = "ignored", Site = D30Fixtures.Site() });
            Assert.AreEqual(WeatherAlertFeed.Capacity + 5, (int)feed.Count);
        }

        [TestMethod]
        public void TheTelemetryListensOnceAndAnswersNullWithoutAForecast()
        {
            var telemetry = new WeatherTelemetry();
            Func<ForecastSite, ForecastReport> forecast = WeatherHooks.Forecast;
            try
            {
                WeatherHooks.Forecast = null;
                Assert.IsNull(telemetry.Forecast(new string[0]), "Without the atmospheres a read answers null.");
                Assert.IsNull(telemetry.Alerts(new string[0]));
                telemetry.Attach();
                telemetry.Attach();
                WeatherHooks.RaiseAlert(new ForecastAlert { Kind = AlertKind.Approaching, Site = D30Fixtures.Site(), Text = "x" });
                Assert.AreEqual(1, telemetry.Feed.Length, "Attached twice, listening once.");
                Assert.IsNotNull(telemetry.Alerts(new string[0]));
            }
            finally
            {
                telemetry.Detach();
                WeatherHooks.Forecast = forecast;
            }
        }
    }
}
