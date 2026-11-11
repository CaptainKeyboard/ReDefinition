using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.A02
{
    // The legacy field: the existing storm schedule and snow line behind IWeatherField, and the rule that it takes the hub only where
    // nothing else did.
    [TestClass]
    public class LegacyBridgeTests
    {
        private const double Radius = 600000.0;
        private const double Year = 426.0 * 6 * 3600;

        private static bool Bodies(int index, out LegacyBody body)
        {
            body = new LegacyBody { Name = "Kerbin", Radius = Radius, YearSeconds = Year };
            return index == 1;
        }

        private static StormCells.Rules NoStorms(LegacyBody body)
        {
            return new StormCells.Rules { BodyRadius = body.Radius, YearSeconds = body.YearSeconds, ForcedPhase = -1, WinterLine = 42, Frequency = 1, RadiusKm = 12, Seed = 1 };
        }

        private static StormCells.Rules ForcedCell(LegacyBody body)
        {
            StormCells.Rules rules = NoStorms(body);
            rules.HasForced = true;
            rules.ForcedLatitude = 60;
            rules.ForcedLongitude = 10;
            rules.ForcedRadiusKm = 20;
            return rules;
        }

        private static LegacyField Field(System.Func<LegacyBody, StormCells.Rules> rules)
        {
            return new LegacyField(Bodies, rules, () => 77L);
        }

        [TestCleanup]
        public void Restore()
        {
            LegacyBridge.Uninstall();
            WeatherHub.Field = null;
        }

        [TestMethod]
        public void ABodyTheSeasonsDoNotCoverGivesFalseAndAClearSample()
        {
            LegacyField field = Field(NoStorms);
            WeatherSample sample;
            Assert.IsFalse(field.Sample(5, 60, 10, 0, 1000, out sample));
            Assert.AreEqual(0f, sample.Level01);
            Assert.AreEqual(PrecipType.None, sample.Type);
            Assert.AreEqual(WeatherSample.ClearVisibilityM, sample.VisibilityM);
        }

        [TestMethod]
        public void WithNoStormTheSampleIsClear()
        {
            LegacyField field = Field(NoStorms);
            WeatherSample sample;
            Assert.IsTrue(field.Sample(1, 60, 10, 0, 1000, out sample));
            Assert.AreEqual(0f, sample.Level01);
            Assert.AreEqual(PrecipType.None, sample.Type);
        }

        [TestMethod]
        public void InTheMiddleOfTheForcedCellItSnowsAtFullLevel()
        {
            LegacyField field = Field(ForcedCell);
            WeatherSample sample;
            Assert.IsTrue(field.Sample(1, 60, 10, 0, 1000, out sample));
            Assert.AreEqual(1f, sample.Level01, 1e-3f);
            Assert.AreEqual(PrecipType.Snow, sample.Type);
            Assert.AreEqual(1f, sample.SnowFraction);
            Assert.AreEqual(sample.Level01, sample.CloudCover01);
            Assert.IsTrue(float.IsNaN(sample.TemperatureK), "the legacy field knows no temperature");
            Assert.AreEqual(Vector3Zero, sample.WindWorld);
        }

        private static readonly UnityEngine.Vector3 Vector3Zero = UnityEngine.Vector3.zero;

        [TestMethod]
        public void FarFromTheForcedCellItIsClear()
        {
            LegacyField field = Field(ForcedCell);
            WeatherSample sample;
            field.Sample(1, -40, 100, 0, 1000, out sample);
            Assert.AreEqual(0f, sample.Level01);
        }

        [TestMethod]
        public void TheSnowCoverMapFollowsTheSnowLineAndOtherMapsAreEmpty()
        {
            LegacyField field = Field(NoStorms);
            var map = new WeatherMap();
            Assert.IsTrue(field.GetMap(1, WeatherMapKind.SnowCover, 0, map));
            Assert.AreEqual(LegacyField.MapWidth, map.Width);
            Assert.AreEqual(LegacyField.MapHeight, map.Height);
            float pole = map.Sample(89, 0), equator = map.Sample(0, 0);
            Assert.IsTrue(pole > 0.25f, "the cap is covered: " + pole);
            Assert.AreEqual(0f, equator, 1e-6f);
            Assert.IsFalse(field.GetMap(1, WeatherMapKind.Humidity, 0, map));
            Assert.AreEqual(0f, map.Sample(89, 0));
        }

        [TestMethod]
        public void TheVersionRisesWithTheMinuteAndTheFieldNeverClaimsMoreThanItHas()
        {
            LegacyField field = Field(NoStorms);
            WeatherSample sample;
            field.Sample(1, 0, 0, 0, 10, out sample);
            long v1 = field.Version;
            field.Sample(1, 0, 0, 0, 20, out sample);
            Assert.AreEqual(v1, field.Version, "same minute");
            field.Sample(1, 0, 0, 0, 200, out sample);
            Assert.IsTrue(field.Version > v1);
            Assert.AreEqual(77L, field.DefinitionId);
            Assert.IsFalse(field.ThreadSafe);
            Assert.IsFalse(field.FillWindow(1, 0, 0, 10, 0, new WeatherWindow()));
            Assert.AreEqual(FieldReadiness.Ready, field.Readiness);
        }

        [TestMethod]
        public void ItTakesTheHubOnlyWhereNothingElseDid()
        {
            WeatherHub.Field = null;
            LegacyField legacy = Field(NoStorms);
            Assert.IsTrue(LegacyBridge.Install(legacy));
            Assert.IsTrue(LegacyBridge.Installed);
            Assert.AreSame(legacy, WeatherHub.Field);
            LegacyBridge.Uninstall();
            Assert.IsInstanceOfType(WeatherHub.Field, typeof(NullField));

            var other = new CountingField();
            WeatherHub.Field = other;
            Assert.IsFalse(LegacyBridge.Install(legacy), "another field is there");
            Assert.AreSame(other, WeatherHub.Field);
            LegacyBridge.Uninstall();
            Assert.AreSame(other, WeatherHub.Field, "uninstalling does not remove a field it did not install");
        }

        [TestMethod]
        public void TheModuleIsFoundByTheRegistry()
        {
            var found = ModuleRegistry.Discover(typeof(LegacyBridgeModule).Assembly);
            CollectionAssert.Contains(found, typeof(LegacyBridgeModule));
        }

        private sealed class CountingField : IWeatherField
        {
            public bool Sample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample)
            {
                sample = WeatherSample.Clear(bodyIndex);
                return false;
            }

            public bool GetMap(int bodyIndex, WeatherMapKind kind, double ut, WeatherMap target) { return false; }
            public bool FillWindow(int bodyIndex, double centreLatDeg, double centreLonDeg, double radiusKm, double ut, WeatherWindow target) { return false; }
            public void Advance(double budgetMs) { }
            public long Version { get { return 0; } }
            public long DefinitionId { get { return 0; } }
            public bool ThreadSafe { get { return true; } }
            public double StepSeconds { get { return 1; } }
            public FieldReadiness Readiness { get { return FieldReadiness.Ready; } }
        }
    }
}
