using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.D30
{
    // D30: the window's content. The view is pure, so every line the player reads is checked here: the states, the next storm with its
    // ETA, the launch conditions, the hourly strip and the table with the field's temperature.
    [TestClass]
    public class ForecastViewTests
    {
        private const double H = D30Fixtures.H;

        private static ForecastViewInput Input(ForecastReport report, ForecastDetail detail)
        {
            return new ForecastViewInput
            {
                Report = report, Detail = detail, NowUt = 0.0, Site = D30Fixtures.Site(), BodyName = "Kerbin", BodyRadiusM = D30Fixtures.RadiusM,
                FieldTemperatureK = ut => 268.15f, LaunchSite = true
            };
        }

        private static ForecastDetail Storm(double etaH, double clearH, bool active = false)
        {
            return new ForecastDetail { Known = true, ThresholdEtaUt = etaH * H, ClearUt = clearH * H, ActiveAtStart = active };
        }

        [TestMethod]
        public void TheStatesAreOffNoBodyVacuumComputingApproximateAndReady()
        {
            ForecastReport report = D30Fixtures.Report(0.0, PrecipBand.None, PrecipBand.Heavy);
            ForecastViewInput input = Input(report, Storm(1, 2));
            input.Enabled = false;
            Assert.AreEqual(ForecastState.Off, ForecastView.Build(input).State);
            input.Enabled = true;
            input.HasBody = false;
            Assert.AreEqual(ForecastState.NoBody, ForecastView.Build(input).State);
            input.HasBody = true;
            input.Vacuum = true;
            ForecastView vacuum = ForecastView.Build(input);
            Assert.AreEqual(ForecastState.Vacuum, vacuum.State);
            StringAssert.Contains(vacuum.StateText, "vacuum");
            input.Vacuum = false;
            input.Report = null;
            ForecastView computing = ForecastView.Build(input);
            Assert.AreEqual(ForecastState.Computing, computing.State);
            StringAssert.Contains(computing.StateText, "computing");
            Assert.IsFalse(computing.HasData);
            input.Report = report;
            input.Readiness = FieldReadiness.CatchingUp;
            ForecastView approximate = ForecastView.Build(input);
            Assert.AreEqual(ForecastState.Approximate, approximate.State);
            StringAssert.Contains(approximate.StateText, "approximate");
            Assert.AreEqual(0, approximate.Rows.Length, "No numbers are shown while the field is not Ready.");
            input.Readiness = FieldReadiness.Ready;
            ForecastView ready = ForecastView.Build(input);
            Assert.AreEqual(ForecastState.Ready, ready.State);
            Assert.AreEqual("", ready.StateText);
        }

        [TestMethod]
        public void TheNextStormLineGivesTheEtaTheLevelAndTheNearestSystem()
        {
            ForecastReport report = D30Fixtures.Report(0.0, PrecipBand.None, PrecipBand.None, PrecipBand.Heavy, PrecipBand.Heavy, PrecipBand.None, PrecipBand.None);
            // A system 12 km west of the site: 12000 m / (600000 m * cos(28.5 deg)) in degrees of longitude.
            double dLon = 12000.0 / (D30Fixtures.RadiusM * Math.Cos(28.5 * Math.PI / 180.0)) * 180.0 / Math.PI;
            report.Tracks = new[] { D30Fixtures.Track(7, 0.0, 28.5, -80.5 - dLon, 0.0, 0.0, 12, 20000f, 0.7f) };
            ForecastViewInput input = Input(report, Storm(2.0 + 10.0 / 60.0, 4));
            ForecastView view = ForecastView.Build(input);
            Console.WriteLine("D30 view: " + view.NextStormLine + " | " + view.ArrivalLine + " | " + view.ClearLine);
            Assert.AreEqual("Next storm in 2 h 10 min (Heavy (4), 12 km W)", view.NextStormLine);
            StringAssert.StartsWith(view.ArrivalLine, "Arrives ");
            StringAssert.StartsWith(view.ClearLine, "Clears ");
            StringAssert.Contains(view.ClearLine, "in 4 h");
        }

        [TestMethod]
        public void AStormThatIsOnAndOneBeyondTheHorizonAreWorded()
        {
            ForecastReport report = D30Fixtures.Report(0.0, PrecipBand.Heavy, PrecipBand.Heavy, PrecipBand.None);
            ForecastView now = ForecastView.Build(Input(report, new ForecastDetail { Known = true, ThresholdEtaUt = 0.0, ClearUt = 2 * H, ActiveAtStart = true }));
            StringAssert.StartsWith(now.NextStormLine, "Storm now: Heavy snow");
            Assert.AreEqual("Arrived", now.ArrivalLine);
            ForecastView beyond = ForecastView.Build(Input(report, new ForecastDetail { Known = true, ThresholdEtaUt = 1 * H, ClearUt = double.NaN }));
            StringAssert.Contains(beyond.ClearLine, "after the forecast horizon");
            ForecastView none = ForecastView.Build(Input(D30Fixtures.Report(0.0, PrecipBand.None, PrecipBand.None), new ForecastDetail { Known = true, ThresholdEtaUt = double.NaN, ClearUt = double.NaN }));
            Assert.AreEqual("No storm within 24 h", none.NextStormLine);
        }

        [TestMethod]
        public void TheLaunchConditionsShowTheStatusTheReasonsAndTheBestWindow()
        {
            ForecastReport report = D30Fixtures.Report(0.0, PrecipBand.None, PrecipBand.None);
            ForecastViewInput input = Input(report, ForecastDetail.None);
            input.Launch = new LaunchAssessment
            {
                Status = LaunchStatus.Marginal, Reasons = new[] { "Wind 12 m/s above the limit of 10 m/s in 2 h" }, WindowStartUt = 4 * H, WindowEndUt = 7 * H
            };
            ForecastView view = ForecastView.Build(input);
            Assert.AreEqual("Launch: Marginal", view.LaunchHeadline);
            Assert.AreEqual("Wind 12 m/s above the limit of 10 m/s in 2 h", view.LaunchReasons[0]);
            Assert.AreEqual(" ", view.LaunchReasons[1]);
            StringAssert.StartsWith(view.WindowLine, "Best window in the next 24 h:");
            StringAssert.Contains(view.WindowLine, "(3 h)");
            input.Launch = new LaunchAssessment { Status = LaunchStatus.NoGo, Reasons = new[] { "Heavy snow above the limit of Moderate now" } };
            ForecastView nogo = ForecastView.Build(input);
            Assert.AreEqual("Launch: No-Go", nogo.LaunchHeadline);
            Assert.AreEqual("No launch window in the next 24 h", nogo.WindowLine);
            input.LaunchSite = false;
            Assert.AreEqual("", ForecastView.Build(input).LaunchHeadline, "The launch conditions apply to launch sites only.");
        }

        [TestMethod]
        public void TheStripShowsLetterAndNumberOnTheBandColourAndPagesBeyondADay()
        {
            var bands = new PrecipBand[30];
            for (int i = 0; i < bands.Length; i++) bands[i] = i < 2 ? PrecipBand.None : i < 5 ? PrecipBand.Light : PrecipBand.Heavy;
            ForecastReport report = D30Fixtures.Report(0.0, bands);
            ForecastViewInput input = Input(report, ForecastDetail.None);
            ForecastView first = ForecastView.Build(input);
            Assert.AreEqual(24, first.Strip.Length);
            Assert.AreEqual(2, first.StripPages);
            StringAssert.StartsWith(first.StripText(), "-0 -0 L2 L2 L2 H4 H4");
            Assert.AreEqual("E69F00", first.Strip[5].BackHex);
            Assert.AreEqual("0", first.Strip[0].Lead);
            Assert.AreEqual("23", first.Strip[23].Lead);
            input.StripFirst = 24;
            ForecastView second = ForecastView.Build(input);
            Assert.AreEqual(6, second.Strip.Length);
            Assert.AreEqual(1, second.StripPage);
            Assert.AreEqual("24", second.Strip[0].Lead);
        }

        [TestMethod]
        public void TheTableCarriesTheFieldsTemperatureInCelsiusAndTheWindWithItsDirection()
        {
            ForecastReport report = D30Fixtures.Report(0.0, PrecipBand.None, PrecipBand.Light, PrecipBand.Heavy);
            ForecastView view = ForecastView.Build(Input(report, ForecastDetail.None));
            Assert.AreEqual(3, view.Rows.Length);
            Assert.AreEqual(ForecastView.Columns.Length, view.Rows[0].Length);
            Assert.AreEqual("now", view.Rows[0][0]);
            Assert.AreEqual("+1 h", view.Rows[1][0]);
            Assert.AreEqual("Light (2)", view.Rows[1][1]);
            Assert.AreEqual("snow", view.Rows[2][2]);
            Assert.AreEqual("-", view.Rows[0][2], "No type while nothing falls.");
            Assert.AreEqual("7 m/s from NW", view.Rows[1][4]);
            Assert.AreEqual("-5 °C", view.Rows[2][6]);
            Assert.AreEqual("Field Temp", ForecastView.Columns[6]);
        }

        [TestMethod]
        public void TheWindLineNamesTheSourceAndTheSiteLineTheCoordinates()
        {
            ForecastReport report = D30Fixtures.Report(0.0, PrecipBand.None, PrecipBand.None);
            report.Wind = WeatherSource.ExternalNowcast("KWP");
            report.Steps[1].Confidence01 = 0.4f;
            ForecastView view = ForecastView.Build(Input(report, ForecastDetail.None));
            StringAssert.StartsWith(view.WindLine, "Wind: KWP, now only");
            StringAssert.Contains(view.WindLine, "confidence 40 %");
            Assert.AreEqual("Kerbin, Pad: 28.5 deg N latitude, 80.5 deg W longitude", view.SiteLine);
            Assert.IsTrue(view.Strip[1].Alpha < 1f, "The cell of a carried-forward wind is faded.");
        }
    }
}
