using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Flight;
using ReDefinition.Flight.Panel;

namespace ReDefinition.Tests
{
    // The situation the flight panel and the round gauge follow
    // (FlightProfiles.Classify), with the bounds it keeps once on, and the round
    // gauge's fixed scales.
    [TestClass]
    public class SituationTests
    {
        // A vessel in the air with the given vertical speed and height over the ground, no docking port near.
        private static FlightProfiles.Facts Aloft(double verticalSpeed, double overGround)
        {
            return new FlightProfiles.Facts { VerticalSpeed = verticalSpeed, OverGround = overGround, Port = double.NaN };
        }

        // Past the apoapsis while circularising: sinking at 75 km with the
        // periapsis still under the ground stays the ascent.
        [TestMethod]
        public void PastTheApoapsisStaysTheAscent()
        {
            Assert.AreEqual(PanelSituation.Ascent, FlightProfiles.Classify(Aloft(-30.0, 75000.0), PanelSituation.Ascent));
        }

        // Coming down faster than 5 m/s below 20 km over the ground turns the ascent into a landing.
        [TestMethod]
        public void FallingBackLowIsALanding()
        {
            Assert.AreEqual(PanelSituation.Landing, FlightProfiles.Classify(Aloft(-40.0, 8000.0), PanelSituation.Ascent));
        }

        // Sinking slowly low down is no landing yet.
        [TestMethod]
        public void SinkingSlowlyIsNoLandingYet()
        {
            Assert.AreEqual(PanelSituation.Ascent, FlightProfiles.Classify(Aloft(-3.0, 8000.0), PanelSituation.Ascent));
        }

        // A lander hovering or rising a little stays landing; climbing away ends it.
        [TestMethod]
        public void ALandingKeepsWhileHovering()
        {
            Assert.AreEqual(PanelSituation.Landing, FlightProfiles.Classify(Aloft(0.0, 300.0), PanelSituation.Landing));
            Assert.AreEqual(PanelSituation.Landing, FlightProfiles.Classify(Aloft(-3.0, 22000.0), PanelSituation.Landing));
            Assert.AreEqual(PanelSituation.Ascent, FlightProfiles.Classify(Aloft(5.0, 300.0), PanelSituation.Landing));
        }

        // Out of orbit, down to the landing's height, it stays the orbit.
        [TestMethod]
        public void ADescentFromOrbitStaysTheOrbit()
        {
            Assert.AreEqual(PanelSituation.Orbit, FlightProfiles.Classify(Aloft(-200.0, 50000.0), PanelSituation.Orbit));
            Assert.AreEqual(PanelSituation.Landing, FlightProfiles.Classify(Aloft(-200.0, 15000.0), PanelSituation.Orbit));
        }

        // A target makes no rendezvous while climbing, only in orbit.
        [TestMethod]
        public void ATargetMattersInOrbitOnly()
        {
            FlightProfiles.Facts climbing = Aloft(100.0, 30000.0);
            climbing.Target = true;
            Assert.AreEqual(PanelSituation.Ascent, FlightProfiles.Classify(climbing, PanelSituation.Ascent));
            FlightProfiles.Facts orbiting = Aloft(0.0, 90000.0);
            orbiting.Orbital = true;
            orbiting.Target = true;
            Assert.AreEqual(PanelSituation.Rendezvous, FlightProfiles.Classify(orbiting, PanelSituation.Ascent));
        }

        // A port 2.2 km off: docking goes on at 2 km, and stays on to 2.5 km.
        [TestMethod]
        public void DockingKeepsItsWiderBound()
        {
            FlightProfiles.Facts facts = Aloft(0.0, 90000.0);
            facts.Orbital = true;
            facts.Target = true;
            facts.Port = 2200.0;
            Assert.AreEqual(PanelSituation.Rendezvous, FlightProfiles.Classify(facts, PanelSituation.Rendezvous));
            Assert.AreEqual(PanelSituation.Docking, FlightProfiles.Classify(facts, PanelSituation.Docking));
            facts.Port = 2600.0;
            Assert.AreEqual(PanelSituation.Rendezvous, FlightProfiles.Classify(facts, PanelSituation.Docking));
        }

        // A winged vessel in the air is flying, even sinking fast low down, and not landing.
        [TestMethod]
        public void WingsInTheAirFlyAndDoNotLand()
        {
            FlightProfiles.Facts facts = Aloft(-20.0, 500.0);
            facts.Flying = true;
            facts.Winged = true;
            Assert.AreEqual(PanelSituation.Air, FlightProfiles.Classify(facts, PanelSituation.Air));
        }

        // The gauge's scales are logarithmic and fixed: time up to ten minutes, a minute at 64 %; speed up to
        // 100 m/s, 1 m/s at 35 %.
        [TestMethod]
        public void TheGaugesScalesAreFixed()
        {
            Assert.AreEqual(0.0, GaugeReadings.TimeScale(0.0), 1e-9);
            Assert.AreEqual(0.64, GaugeReadings.TimeScale(60.0), 0.01);
            Assert.AreEqual(1.0, GaugeReadings.TimeScale(600.0), 1e-9);
            Assert.AreEqual(1.0, GaugeReadings.TimeScale(3600.0), 1e-9);
            Assert.AreEqual(0.0, GaugeReadings.SpeedScale(-1.0), 1e-9);
            Assert.AreEqual(0.347, GaugeReadings.SpeedScale(1.0), 0.001);
            Assert.AreEqual(1.0, GaugeReadings.SpeedScale(100.0), 1e-9);
        }
    }
}
