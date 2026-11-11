using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.A01
{
    // The body schema (immutable data with named members) and the contracts between the packages.
    [TestClass]
    public class SchemaAndContractsTests
    {
        private static readonly Type[] SchemaTypes =
        {
            typeof(BodyClimate), typeof(BodyStatus), typeof(SeasonRules), typeof(TemperatureRules), typeof(PrecipRules), typeof(PrecipTypeRule),
            typeof(CoverRules), typeof(IceRules), typeof(CellRules), typeof(CellBand), typeof(SimRules), typeof(CloudRules), typeof(LookRules),
            typeof(SoundRules), typeof(WindRules), typeof(GameplayRules), typeof(AuroraRules), typeof(LightMediumRules), typeof(FrostRules),
            typeof(ForecastRules), typeof(Curve)
        };

        [TestMethod]
        public void EverySchemaClassIsSealedAndHasNoSetterAndNoPublicField()
        {
            var offenders = new List<string>();
            foreach (Type type in SchemaTypes)
            {
                if (!type.IsSealed) offenders.Add(type.Name + " is not sealed");
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    MethodInfo setter = property.GetSetMethod(true);
                    if (setter != null) offenders.Add(type.Name + "." + property.Name + " has a setter");
                }
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (!field.IsInitOnly && !field.Name.Contains("k__BackingField")) offenders.Add(type.Name + "." + field.Name + " can change");
                }
            }
            Assert.AreEqual(0, offenders.Count, string.Join("\n", offenders));
        }

        [TestMethod]
        public void TheGenericDefaultHasEveryRuleAndBelongsToNoBody()
        {
            BodyClimate c = BodyClimate.Generic("Testworld", 5, 600000.0, 9203545.0, 21600.0, BodyStatus.Full, 11);
            Assert.AreEqual("Testworld", c.Name);
            Assert.AreEqual(5, c.Index);
            Assert.AreEqual(600000.0, c.Radius);
            Assert.AreEqual(9203545.0, c.YearSeconds);
            Assert.AreEqual(21600.0, c.DaySeconds);
            Assert.AreEqual(11, c.Seed);
            Assert.IsTrue(c.Status.WeatherRuns);
            Assert.IsNotNull(c.Seasons);
            Assert.IsNotNull(c.Temperature);
            Assert.IsNotNull(c.Precip);
            Assert.AreEqual(0, c.Covers.Count);
            Assert.IsNotNull(c.Ice);
            Assert.IsNotNull(c.Cells);
            Assert.IsNotNull(c.Sim);
            Assert.IsNotNull(c.Clouds);
            Assert.IsNotNull(c.Look);
            Assert.IsNotNull(c.Sound);
            Assert.IsNotNull(c.Wind);
            Assert.IsNotNull(c.Gameplay);
            Assert.IsNotNull(c.Aurora);
            Assert.IsNotNull(c.LightMedium);
            Assert.IsNotNull(c.Frost);
            Assert.IsNotNull(c.Forecast);
            Assert.IsTrue(double.IsNaN(c.Seasons.TiltDegrees), "tilt is Auto");
            Assert.IsNull(c.Clouds.Source, "the player's cloudSource decides");
            Assert.AreEqual(5, c.Precip.BandRatesMmPerH.Length);
        }

        [TestMethod]
        public void TheBodyStatusNamesItsKindAndReason()
        {
            Assert.AreEqual("Full", BodyStatus.Full.ToString());
            Assert.AreEqual("Off(NoAtmosphere)", BodyStatus.Off("NoAtmosphere").ToString());
            Assert.AreEqual(BodyStatusKind.Broken, BodyStatus.Broken("envelope").Kind);
            Assert.IsFalse(BodyStatus.Broken("x").WeatherRuns);
            Assert.IsFalse(BodyStatus.CoverOnly.WeatherRuns);
            Assert.IsFalse(BodyStatus.Off("x").WeatherRuns);
        }

        [TestMethod]
        public void TheSeedIsStableAndDiffersByNameAndGlobalSeed()
        {
            Assert.AreEqual(BodyClimate.SeedOf("Kerbin", 1), BodyClimate.SeedOf("Kerbin", 1));
            Assert.AreNotEqual(BodyClimate.SeedOf("Kerbin", 1), BodyClimate.SeedOf("Duna", 1));
            Assert.AreNotEqual(BodyClimate.SeedOf("Kerbin", 1), BodyClimate.SeedOf("Kerbin", 2));
            // The value is pinned so that a side file's header hash stays valid between runtimes.
            Assert.AreEqual(664216370, BodyClimate.SeedOf("Kerbin", 1));
        }

        [TestMethod]
        public void ARuleHoldsACopyOfItsLists()
        {
            var rates = new[] { 1f, 2f, 3f, 4f, 5f };
            var precip = new PrecipRules(bandRatesMmPerH: rates);
            rates[0] = 99f;
            Assert.AreEqual(1f, precip.BandRatesMmPerH[0]);
            precip.BandRatesMmPerH[1] = 77f;
            Assert.AreEqual(2f, precip.BandRatesMmPerH[1]);
            var names = new List<string> { "a" };
            var rule = new PrecipTypeRule("snow", PrecipType.Snow, levelNames: names);
            names.Add("b");
            Assert.AreEqual(1, rule.LevelNames.Count);
            var forcing = new Dictionary<string, double> { { "k", 1.0 } };
            var sim = new SimRules(forcing: forcing);
            forcing["k"] = 2.0;
            Assert.AreEqual(1.0, sim.Forcing["k"]);
        }

        [TestMethod]
        public void TheLevelOfARateUsesTheBodysBandRates()
        {
            BodyClimate c = BodyClimate.Generic("T", 0, 1.0, 1.0, 1.0, BodyStatus.Full, 1);
            Assert.AreEqual(0.04f, PrecipScale.LevelFromRate(c.Precip.BandRatesMmPerH[0], c.Precip.BandRatesMmPerH), 1e-6);
        }

        [TestMethod]
        public void ACurveInterpolatesHoldsItsEndsAndRefusesBadKeys()
        {
            var curve = new Curve(new[] { 0f, 10f, 20f }, new[] { 100f, 200f, 0f });
            Assert.AreEqual(100f, curve.Evaluate(-5f));
            Assert.AreEqual(150f, curve.Evaluate(5f));
            Assert.AreEqual(100f, curve.Evaluate(15f));
            Assert.AreEqual(0f, curve.Evaluate(99f));
            Assert.AreEqual(3, curve.Count);
            Assert.ThrowsException<ArgumentException>(() => new Curve(new[] { 0f, 0f }, new[] { 1f, 2f }));
            Assert.ThrowsException<ArgumentException>(() => new Curve(new float[0], new float[0]));
            Assert.ThrowsException<ArgumentException>(() => new Curve(new[] { 0f }, new[] { 1f, 2f }));
        }

        [TestMethod]
        public void TheDefaultBodyProviderGivesNullForNoBodyAndTheProviderCanBeReplaced()
        {
            IBodyProvider original = Bodies.Provider;
            try
            {
                Assert.IsNull(Bodies.Get(null));
                var fake = new FakeProvider();
                Bodies.Provider = fake;
                Assert.IsNull(Bodies.Get(null));
                Assert.AreEqual(1, fake.Calls);
                Bodies.Provider = null;
                Assert.IsInstanceOfType(Bodies.Provider, typeof(DefaultBodyProvider));
            }
            finally
            {
                Bodies.Provider = original;
            }
        }

        private sealed class FakeProvider : IBodyProvider
        {
            internal int Calls;

            public BodyClimate Get(CelestialBody body)
            {
                Calls++;
                return null;
            }
        }

        private sealed class Layer : IOrbitOverlayLayer
        {
            internal Layer(string name, int order)
            {
                Name = name;
                Order = order;
            }

            public string Name { get; }
            public int Order { get; }
            public bool Wants(CelestialBody body) { return true; }
            public void Draw(CelestialBody body, bool focused, double ut) { }
        }

        [TestMethod]
        public void TheOrbitOverlayLayersAreKeptInOrderAndEachOnce()
        {
            var cover = new Layer("cover", 30);
            var clouds = new Layer("clouds", 10);
            var ice = new Layer("ice", 20);
            OrbitOverlayLayers.Register(cover);
            OrbitOverlayLayers.Register(clouds);
            OrbitOverlayLayers.Register(ice);
            OrbitOverlayLayers.Register(ice);
            try
            {
                CollectionAssert.AreEqual(new[] { "clouds", "ice", "cover" }, OrbitOverlayLayers.All.Select(l => l.Name).ToArray());
                Assert.ThrowsException<ArgumentNullException>(() => OrbitOverlayLayers.Register(null));
            }
            finally
            {
                OrbitOverlayLayers.Unregister(cover);
                OrbitOverlayLayers.Unregister(clouds);
                Assert.IsTrue(OrbitOverlayLayers.Unregister(ice));
                Assert.IsFalse(OrbitOverlayLayers.Unregister(ice));
            }
            Assert.AreEqual(0, OrbitOverlayLayers.All.Length);
        }

        [TestMethod]
        public void TheServicesStartWithNullObjectsAndTakeTheirImplementers()
        {
            Assert.IsFalse(Services.PostUpscale.HostAvailable);
            Assert.IsFalse(Services.PostUpscale.DepthAvailable);
            Assert.IsFalse(Services.PostUpscale.Register("x", null, false));
            Services.PostUpscale.RequestHistoryReset("test");
            Assert.IsFalse(Services.EveFieldSuppressor.Suppressed);
            Services.EveFieldSuppressor.SetSuppressed(true, "test");
            Assert.IsFalse(Services.EveFieldSuppressor.Suppressed, "nothing to suppress without EVE");
            IPostUpscaleCompositor original = Services.PostUpscale;
            try
            {
                Services.PostUpscale = null;
                Assert.IsNotNull(Services.PostUpscale);
            }
            finally
            {
                Services.PostUpscale = original;
            }
        }
    }
}
