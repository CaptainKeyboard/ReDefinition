using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Dashboard;
using ReDefinition.Tests.Atmospheres.B13;

namespace ReDefinition.Tests.Atmospheres.Integration
{
    // The integration items collected from the builder reports of D30 and D35: the settings the forecast interface reads, the licence that
    // ships with the atmospheres, the dashboard's weather switch, the sea ice default and the series start.
    [TestClass]
    public class IntegrationItemsTests
    {
        private static string Read(params string[] relative)
        {
            return File.ReadAllText(RepoRoot.Locate(relative));
        }

        [TestMethod]
        public void TheKerbalAlarmClockLicenceIsStagedIntoTheAtmospheresPackage()
        {
            Assert.IsTrue(File.Exists(RepoRoot.Locate("licenses", "KerbalAlarmClock-MIT.txt")), "the licence text is in licenses/");
            string csproj = Read("src", "ReDefinition.csproj");
            Assert.IsTrue(Regex.IsMatch(csproj, @"<Copy SourceFiles=""\$\(_RepoRoot\)licenses\\KerbalAlarmClock-MIT\.txt"" DestinationFiles=""\$\(_StageAtmospheres\)\\LICENSE-KerbalAlarmClock\.txt"""),
                "the release target copies it as LICENSE-KerbalAlarmClock.txt into the atmospheres stage");
            Assert.IsTrue(Read("licenses", "SOURCE.md").Contains("LICENSE-KerbalAlarmClock.txt"), "licenses/SOURCE.md names the staged file");
        }

        [TestMethod]
        public void TheTwoForecastSwitchesAreSettingsThatDefaultToOn()
        {
            foreach (string key in new[] { "forecastOverlay", "alertScreenMessage" })
            {
                SettingDef def = AtmospheresSettings.Definition(key);
                Assert.IsNotNull(def, key);
                Assert.AreEqual(SettingKind.Bool, def.Kind, key);
                Assert.AreEqual(1.0, def.Default, key + " defaults to on");
            }
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsForecast, AtmospheresSettings.Definition("forecastOverlay").GreyReason);
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsAlerts, AtmospheresSettings.Definition("alertScreenMessage").GreyReason);
            string contract = Read("src", "Atmospheres", "CONTRACT.md");
            Assert.IsTrue(contract.Contains("| `forecastOverlay` | on | on / off | Forecast Map Overlay | needs-forecast | - |"));
            Assert.IsTrue(contract.Contains("| `alertScreenMessage` | on | on / off | Alert Screen Messages | needs-alerts | - |"));
        }

        [TestMethod]
        public void TheSeaIceDefaultIsTheOrbitPictureAndTheContractSaysSo()
        {
            Assert.AreEqual((double)(int)SeaIceMode.Orbit, AtmospheresSettings.Definition("seaIce").Default);
            Assert.IsTrue(Read("src", "Atmospheres", "CONTRACT.md").Contains("| `seaIce` | Orbit | Off / Orbit / OrbitAndGround / Physical |"));
        }

        [TestMethod]
        public void TheIceNodeCarriesThePassThicknessAsInformation()
        {
            Assert.AreEqual(0.3f, BodyTestSupport.Run(BodyTestSupport.Planet("X", BodyTestSupport.HomeYear)).Climate.Ice.PassThicknessM, 1e-6f);
            BodyParseResult r = BodyTestSupport.Run(BodyTestSupport.Planet("X", BodyTestSupport.HomeYear), "REDEFINITION_BODY { name = X ; ICE { icePassThicknessM = 0.8 } }");
            Assert.AreEqual(0.8f, r.Climate.Ice.PassThicknessM, 1e-6f);
        }

        [TestMethod]
        public void TheIceDoesNotHangOnTheGroundCoverSwitchInTheOrbitTick()
        {
            string tick = Read("src", "Atmospheres", "Cover", "OrbitSnow.cs");
            Assert.IsFalse(Regex.IsMatch(tick, @"OrbitOverlay\s*\|\|\s*!AtmospheresSettings\.GroundCover"), "the host runs without the cover switch");
            Assert.IsTrue(tick.Contains("OrbitOverlayHost.DrawRound"));
        }

        [TestMethod]
        public void TheDashboardsWeatherSwitchIsOnByDefaultAndComesBack()
        {
            Assert.IsTrue(DashboardSettings.WeatherPush, "default on");
            string file = Path.Combine(Path.GetTempPath(), "dashboard-push-" + Guid.NewGuid().ToString("N"), "settings.cfg");
            bool before = DashboardSettings.WeatherPush;
            try
            {
                DashboardSettings.WeatherPush = false;
                DashboardSettings.Write(file);
                DashboardSettings.WeatherPush = true;
                DashboardSettings.Read(file);
                Assert.IsFalse(DashboardSettings.WeatherPush);
            }
            finally
            {
                DashboardSettings.WeatherPush = before;
                if (File.Exists(file)) Directory.Delete(Path.GetDirectoryName(file), true);
            }
        }

        [TestMethod]
        public void TheTelemetryRegistersTheWeatherKeysBehindTheSwitch()
        {
            string telemetry = Read("src", "Dashboard", "Telemetry.cs");
            Assert.IsTrue(telemetry.Contains("sceneReaders[\"w.forecast\"]") && telemetry.Contains("WeatherTelemetry.Shared.Forecast(a)"));
            Assert.IsTrue(telemetry.Contains("sceneReaders[\"w.alerts\"]") && telemetry.Contains("WeatherTelemetry.Shared.Alerts(a)"));
            Assert.IsTrue(Regex.IsMatch(telemetry, @"w\.forecast""\] = a => DashboardSettings\.WeatherPushOn"), "the forecast key answers only while the switch is on");
            Assert.IsTrue(Regex.IsMatch(telemetry, @"w\.alerts""\] = a => DashboardSettings\.WeatherPushOn"), "the alerts key answers only while the switch is on");
        }

        [TestMethod]
        public void ThePlannerShowsTheStormLinesThroughTheSeam()
        {
            string plan = Read("src", "Planner", "Ui", "PlannerView.FlightPlan.cs");
            Assert.IsTrue(plan.Contains("StormOnPlan.LinesOf(plan)"));
            Assert.IsTrue(Read("src", "Planner", "Planning", "StormOnPlan.cs").Contains("StormOnRoute.FindWithSeam(legs, RadiusOf)"));
        }

        [TestMethod]
        public void TheSeriesK_W3bHasTheIceStartAndTheD35Section()
        {
            string series = Read("tests", "series", "K-W3b.json");
            Assert.IsTrue(Regex.IsMatch(series, @"""id"": ""ice"".*""-redefinitionProbeSea"".*""-atmoProbe"", ""IceProbe"""), "the ice start flies the sea capsule and runs IceProbe");
            Assert.IsTrue(Regex.IsMatch(series, @"""id"": ""D35"", ""package"": ""D35"""));
            Assert.IsTrue(Regex.IsMatch(series, @"""plannedStarts"": 6"));
        }

        [TestMethod]
        public void TheBenchmarkMatrixHasTheIceAndForecastRows()
        {
            string matrix = Read("tools", "benchmark_matrix.json");
            foreach (string id in new[] { "weather-forecastOverlay", "weather-alertScreenMessage", "weather-seaIceOrbitAndGround", "weather-iceGroundOverlay", "weather-iceFarKm" })
                Assert.IsTrue(matrix.Contains("\"id\": \"" + id + "\""), id);
            Assert.IsTrue(matrix.Contains("\"atmo.ice.groundOverlay\": \"0\"") && matrix.Contains("\"atmo.ice.farKm\": \"20\""));
        }
    }
}
