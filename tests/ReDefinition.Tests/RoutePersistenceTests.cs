using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Planner.Autopilot.Background;

namespace ReDefinition.Tests
{
    // The routes a vessel flies on its own, as they are written to the save file and read from it.
    [TestClass]
    public class RoutePersistenceTests
    {
        private static BackgroundStep Orbit(double ut)
        {
            return new BackgroundStep { Kind = StepKind.Orbit, UT = ut, Body = "Minmus", Inclination = 0.1, Eccentricity = 0.2, SemiMajorAxis = 100000.0, Lan = 10.0, ArgumentOfPeriapsis = 20.0, MeanAnomaly = 1.5, Epoch = ut, Keep = 0.8, Why = "capture", Reached = 1 };
        }

        private static BackgroundStep RoundTrip(BackgroundStep step)
        {
            var node = new ConfigNode("STEP");
            RoutePersistence.WriteStep(node, step);
            return RoutePersistence.ReadStep(node);
        }

        [TestMethod]
        public void AnOrbitStepIsReadAsItWasWritten()
        {
            BackgroundStep step = Orbit(1500000.5);
            step.Keeps[42u] = 0.25;
            step.Cuts.Add(new PartsCut { Name = "Debris", Parts = { 7u, 8u } });
            BackgroundStep read = RoundTrip(step);
            Assert.IsNotNull(read);
            Assert.AreEqual(StepKind.Orbit, read.Kind);
            Assert.AreEqual(1500000.5, read.UT);
            Assert.AreEqual("Minmus", read.Body);
            Assert.AreEqual(100000.0, read.SemiMajorAxis);
            Assert.AreEqual(0.8, read.Keep);
            Assert.AreEqual(1, read.Reached);
            Assert.AreEqual(0.25, read.Keeps[42u]);
            Assert.AreEqual(1, read.Cuts.Count);
            CollectionAssert.AreEqual(new uint[] { 7u, 8u }, read.Cuts[0].Parts.ToArray());
        }

        [TestMethod]
        public void AReleaseStepNeedsNoOrbit()
        {
            var step = new BackgroundStep { Kind = StepKind.Release, UT = 1000.0, Why = "release" };
            step.Cuts.Add(new PartsCut { Name = "Satellite", Parts = { 3u } });
            BackgroundStep read = RoundTrip(step);
            Assert.IsNotNull(read);
            Assert.AreEqual(StepKind.Release, read.Kind);
        }

        [TestMethod]
        public void AStepThatCannotBeAnOrbitIsLeftOut()
        {
            BackgroundStep noBody = Orbit(1000.0);
            noBody.Body = "";
            Assert.IsNull(RoundTrip(noBody));
            BackgroundStep flat = Orbit(1000.0);
            flat.SemiMajorAxis = 0.0;
            Assert.IsNull(RoundTrip(flat));
            BackgroundStep notANumber = Orbit(1000.0);
            notANumber.Inclination = double.NaN;
            Assert.IsNull(RoundTrip(notANumber));
            BackgroundStep noTime = Orbit(0.0);
            Assert.IsNull(RoundTrip(noTime));
            BackgroundStep tooMuch = Orbit(1000.0);
            tooMuch.Keep = 1.5;
            Assert.IsNull(RoundTrip(tooMuch));
        }

        [TestMethod]
        public void AStepWithoutAKindIsLeftOut()
        {
            var node = new ConfigNode("STEP");
            node.AddValue("ut", "1000");
            Assert.IsNull(RoutePersistence.ReadStep(node));
        }

        [TestMethod]
        public void AnOpenOrbitHasANegativeSemiMajorAxis()
        {
            BackgroundStep hyperbola = Orbit(1000.0);
            hyperbola.Eccentricity = 1.5;
            hyperbola.SemiMajorAxis = -500000.0;
            Assert.IsNotNull(RoundTrip(hyperbola));
        }

        [TestMethod]
        public void AnEllipseWithANegativeAxisAndAHyperbolaWithAPositiveOneAreLeftOut()
        {
            BackgroundStep ellipse = Orbit(1000.0);
            ellipse.SemiMajorAxis = -100000.0;
            Assert.IsNull(RoundTrip(ellipse));
            BackgroundStep hyperbola = Orbit(1000.0);
            hyperbola.Eccentricity = 1.5;
            Assert.IsNull(RoundTrip(hyperbola));
        }

        [TestMethod]
        public void ASavedRouteIsLoadedWithAllItsSteps()
        {
            var entry = new RouteEntry { Vessel = new System.Guid("0b1f3a4e-0000-0000-0000-000000000001"), Name = "Probe" };
            entry.Steps.Add(Orbit(2000.0));
            entry.Steps.Add(Orbit(1000.0));
            var saved = new ConfigNode("SCENARIO");
            RoutePersistence.Save(new[] { entry }, saved);
            var loaded = RoutePersistence.Load(saved);
            Assert.AreEqual(1, loaded.Count);
            CollectionAssert.AreEqual(new[] { 1000.0, 2000.0 }, loaded[0].Steps.Select(s => s.UT).ToArray());
        }
    }
}
