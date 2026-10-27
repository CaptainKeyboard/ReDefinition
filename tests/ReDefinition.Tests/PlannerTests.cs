using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Engineer;

namespace ReDefinition.Tests
{
    // The planner's route (RoutePlanner) against the stock system's known delta-v figures. The class is in parts, each
    // with the tests of one subject: the stage budget, the vessel's shape, the groups, the route's steps and the firing
    // (PlannerTests.StageBudget.cs and the others beside this one).
    [TestClass]
    public partial class PlannerTests
    {
        // The bodies' indices in Stock().
        private const int Sun = 0, Kerbin = 1, Mun = 2, Minmus = 3, Duna = 4, Eve = 5, Laythe = 6, Tylo = 7, StockJool = 8;

        // The stock bodies, as KSP 1.12.5 defines them.
        internal static List<PlannerBody> Stock()
        {
            return new List<PlannerBody>
            {
                new PlannerBody { Name = "Sun", Mu = 1.1723328e18, Radius = 261600000 },
                new PlannerBody
                {
                    Name = "Kerbin", Parent = Sun, Mu = 3.5316e12, Radius = 600000, RotationPeriod = 21549.425,
                    AtmosphereDepth = 70000, SurfacePressure = 1.0, SemiMajorAxis = 13599840256, LowOrbitAltitude = 80000,
                    // About the game's speed of sound some 20 km up (PlannerBodies logs its own).
                    Oxygen = true, SpeedOfSound = 300,
                },
                new PlannerBody
                {
                    Name = "Mun", Parent = Kerbin, Mu = 6.5138398e10, Radius = 200000, RotationPeriod = 138984.38,
                    SemiMajorAxis = 12000000, LowOrbitAltitude = 14000,
                },
                new PlannerBody
                {
                    Name = "Minmus", Parent = Kerbin, Mu = 1.7658e9, Radius = 60000, RotationPeriod = 40400,
                    SemiMajorAxis = 47000000, Inclination = 6, LowOrbitAltitude = 10000,
                },
                new PlannerBody
                {
                    Name = "Duna", Parent = Sun, Mu = 3.0136321e11, Radius = 320000, RotationPeriod = 65517.859,
                    AtmosphereDepth = 50000, SurfacePressure = 0.0666, SemiMajorAxis = 20726155264, Inclination = 0.06,
                    LowOrbitAltitude = 60000,
                },
                new PlannerBody
                {
                    Name = "Eve", Parent = Sun, Mu = 8.1717302e12, Radius = 700000, RotationPeriod = 80500,
                    AtmosphereDepth = 90000, SurfacePressure = 5.0, SemiMajorAxis = 9832684544, Inclination = 2.1,
                    LowOrbitAltitude = 100000,
                },
                new PlannerBody
                {
                    Name = "Laythe", Parent = StockJool, Mu = 1.962e12, Radius = 500000, RotationPeriod = 52980.879,
                    AtmosphereDepth = 50000, SurfacePressure = 0.6, SemiMajorAxis = 27184000, LowOrbitAltitude = 60000,
                },
                new PlannerBody
                {
                    Name = "Tylo", Parent = StockJool, Mu = 2.82528e12, Radius = 600000, RotationPeriod = 211926.36,
                    SemiMajorAxis = 68500000, Inclination = 0.025, LowOrbitAltitude = 17000,
                },
                new PlannerBody
                {
                    Name = "Jool", Parent = Sun, Mu = 2.82528e14, Radius = 6000000, RotationPeriod = 36000,
                    AtmosphereDepth = 200000, SurfacePressure = 15, SemiMajorAxis = 68773560320, Inclination = 1.304,
                    LowOrbitAltitude = 210000,
                },
            };
        }

        // A stop in orbit of the body at that altitude in metres.
        internal static RouteStop Orbit(int body, double altitude)
        {
            return new RouteStop { Body = body, Kind = StopKind.Orbit, Altitude = altitude };
        }

        // A stop on the body's surface, landed on parachutes or aerobraking where `atmosphere` says so.
        internal static RouteStop Surface(int body, bool atmosphere = false)
        {
            return new RouteStop { Body = body, Kind = StopKind.Surface, UseAtmosphere = atmosphere };
        }

        // Asserts the value lies within the tolerance of what was expected, naming both where it does not.
        private static void Near(double expected, double actual, double tolerance, string what)
        {
            Assert.IsTrue(System.Math.Abs(actual - expected) <= tolerance,
                what + ": expected about " + expected + ", was " + actual.ToString("0"));
        }

        // The launches to low orbit from six bodies match the stock delta-v map within 4 %.
        [TestMethod]
        public void LaunchesMatchTheStockMapWithinAFewPerCent()
        {
            List<PlannerBody> bodies = Stock();
            Near(3400, RoutePlanner.Launch(bodies[Kerbin]), 3400 * 0.04, "Kerbin");
            Near(580, RoutePlanner.Launch(bodies[Mun]), 580 * 0.04, "Mun");
            Near(1450, RoutePlanner.Launch(bodies[Duna]), 1450 * 0.04, "Duna");
            Near(8000, RoutePlanner.Launch(bodies[Eve]), 8000 * 0.04, "Eve");
            Near(2900, RoutePlanner.Launch(bodies[Laythe]), 2900 * 0.04, "Laythe");
            Near(2270, RoutePlanner.Launch(bodies[Tylo]), 2270 * 0.04, "Tylo");
        }

        // From Kerbin's 80 km orbit to the Mun's 14 km one the transfer is about 856 m/s and the capture 310.
        [TestMethod]
        public void KerbinOrbitToMunOrbitIsAboutEightSixtyAndThreeTen()
        {
            Leg leg = RoutePlanner.LegOf(Stock(), Orbit(Kerbin, 80000), Orbit(Mun, 14000), 0, false);

            Near(856, leg.Lines.Single(line => line.Name == "Transfer burn").DeltaV, 5, "transfer");
            Near(310, leg.Lines.Single(line => line.Name == "Capture into orbit").DeltaV, 5, "capture");
        }

        // From Kerbin's orbit to Duna's the ejection and the capture match the stock figures of 1072 and 616 m/s.
        [TestMethod]
        public void KerbinOrbitToDunaOrbitLeavesAndCapturesAcrossTheSun()
        {
            Leg leg = RoutePlanner.LegOf(Stock(), Orbit(Kerbin, 80000), Orbit(Duna, 60000), 0, false);

            Near(1072, leg.Lines.Single(line => line.Name == "Transfer burn").DeltaV, 10, "ejection");
            Near(616, leg.Lines.Single(line => line.Name == "Capture into orbit").DeltaV, 10, "capture");
        }

        // Aerobraking at Duna leaves only a small burn to circularise.
        [TestMethod]
        public void AerobrakingLeavesOnlyTheCircularisation()
        {
            RouteStop dunaAero = Orbit(Duna, 60000);
            dunaAero.UseAtmosphere = true;
            Leg leg = RoutePlanner.LegOf(Stock(), Orbit(Kerbin, 80000), dunaAero, 0, false);

            Assert.IsTrue(leg.Lines.Single(line => line.Name.StartsWith("Aerobraking")).DeltaV < 50);
        }

        // From a moon home the burn leaves the moon and the capture is at the planet: the Mun's 856 and 310 the
        // other way round.
        [TestMethod]
        public void FromAMoonHomeTheBurnLeavesTheMoonAndTheCaptureIsAtThePlanet()
        {
            Leg leg = RoutePlanner.LegOf(Stock(), Orbit(Mun, 14000), Orbit(Kerbin, 80000), 0, false);

            Near(310, leg.Lines.Single(line => line.Name == "Transfer burn").DeltaV, 5, "leaving the Mun");
            Near(856, leg.Lines.Single(line => line.Name == "Capture into orbit").DeltaV, 5, "capture at Kerbin");
        }

        // Leaving the Mun for Duna takes more than leaving the Mun alone and less than going down to Kerbin's
        // orbit first.
        [TestMethod]
        public void FromTheMunToDunaTheBurnLeavesBothTheMunAndKerbin()
        {
            List<PlannerBody> bodies = Stock();
            double fromMun = RoutePlanner.LegOf(bodies, Orbit(Mun, 14000), Orbit(Duna, 60000), 0, false)
                                         .Lines.Single(line => line.Name == "Transfer burn").DeltaV;
            double fromKerbin = RoutePlanner.LegOf(bodies, Orbit(Kerbin, 80000), Orbit(Duna, 60000), 0, false)
                                            .Lines.Single(line => line.Name == "Transfer burn").DeltaV;

            Assert.IsTrue(fromMun > 300 && fromMun < fromKerbin + 400,
                "leaving the Mun for Duna takes more than the Mun's escape, less than going down to Kerbin first: "
                + fromMun.ToString("0"));
        }

        // From Kerbin's surface to the Mun's a leg is the launch, the transfer, the capture and the landing, near
        // the stock map's sum.
        [TestMethod]
        public void SurfaceToSurfaceHasALaunchAndALanding()
        {
            Leg leg = RoutePlanner.LegOf(Stock(), Surface(Kerbin), Surface(Mun), 0, false);

            CollectionAssert.AreEqual(new[] { "Launch to 80 km orbit", "Transfer burn", "Capture into orbit", "Landing" },
                leg.Lines.Select(line => line.Name).ToArray());
            Near(3400 + 856 + 310 + 580, leg.DeltaV, 250, "Kerbin to the Mun's surface");
        }

        // A landing on parachutes needs no burn on Kerbin and a powered end on Duna's thin air.
        [TestMethod]
        public void ParachutesOnKerbinNeedNothingAndOnDunaAPoweredEnd()
        {
            List<PlannerBody> bodies = Stock();
            Assert.AreEqual(0.0, RoutePlanner.Landing(bodies[Kerbin], true));
            double duna = RoutePlanner.Landing(bodies[Duna], true);
            Assert.IsTrue(duna > 100 && duna < 400, duna.ToString("0"));
        }

        // A plane change is added to a leg between bodies of different inclination only when the route asks for
        // one, and costs more.
        [TestMethod]
        public void APlaneChangeIsAddedOnlyWhenWanted()
        {
            List<PlannerBody> bodies = Stock();
            Leg flat = RoutePlanner.LegOf(bodies, Orbit(Mun, 14000), Orbit(Minmus, 10000), 0, false);
            Leg tilted = RoutePlanner.LegOf(bodies, Orbit(Mun, 14000), Orbit(Minmus, 10000), 0, true);

            Assert.IsTrue(tilted.DeltaV > flat.DeltaV);
            Assert.IsTrue(tilted.Lines.Any(line => line.Name == "Plane change"));
        }

        // A route of four stops is three legs, one between each two.
        [TestMethod]
        public void ARouteOfFourStopsHasThreeLegs()
        {
            List<RouteStop> stops = new List<RouteStop> { Surface(Kerbin), Orbit(Kerbin, 80000), Orbit(Mun, 14000), Surface(Mun) };

            Assert.AreEqual(3, RoutePlanner.Legs(Stock(), stops, false).Count);
        }
    }
}
