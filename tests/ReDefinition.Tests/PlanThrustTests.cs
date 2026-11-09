using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Planner.Planning;

namespace ReDefinition.Tests
{
    // The planner's thrust check (PlanThrust) on a vessel of one stage, so that the figures come by hand.
    // Kerbin's gravity at the surface is 3.5316e12 / 600,000^2 = 9.81 m/s^2, and the vessel weighs 10 t as it starts.
    [TestClass]
    public class PlanThrustTests
    {
        private const int Kerbin = PlannerModelFixture.Kerbin, Mun = PlannerModelFixture.Mun;

        // The vessel's one stage with `kerbin` kN of thrust at Kerbin's sea level and 200 in vacuum, lifting off from the surface to orbit.
        private static PlannerModel Launch(double kerbin)
        {
            return PlannerModelFixture.Model(
                PlannerModelFixture.OneStage(6000.0, 200.0, kerbin), PlannerModelFixture.ToKerbinOrbit());
        }

        // The thrust over the weight at the surface: 180 kN in the air over 98.1 kN, 200 kN in vacuum over the same weight.
        [TestMethod]
        public void TheThrustOverTheWeightIsCountedInTheAirAndInVacuum()
        {
            PlanThrust thrust = Launch(180.0).Thrust;

            Assert.AreEqual(180.0 / 98.1, thrust.TwrAt(1, 10.0, Kerbin, true), 1e-9);
            Assert.AreEqual(200.0 / 98.1, thrust.TwrAt(1, 10.0, Kerbin, false), 1e-9);
            Assert.AreEqual(0.0, thrust.TwrAt(1, 0.0, Kerbin, true), "no mass, no ratio");
            Assert.AreEqual(0.0, thrust.TwrAt(7, 10.0, Kerbin, true), "no such stage, no ratio");
        }

        // A lift-off needs 1.2 from a body with air and 1 from one without; along a runway nothing.
        [TestMethod]
        public void ALiftOffNeedsOnePointTwoInTheAirAndOneWithout()
        {
            PlanThrust thrust = Launch(180.0).Thrust;

            Assert.AreEqual(1.2, thrust.TwrRequired(true, false, Kerbin), 1e-12);
            Assert.AreEqual(1.0, thrust.TwrRequired(true, false, Mun), 1e-12);
            Assert.AreEqual(1.0, thrust.TwrRequired(false, false, Kerbin), 1e-12, "a landing is not held to the air's figure");
            Assert.AreEqual(0.0, thrust.TwrRequired(true, true, Kerbin), 1e-12, "a runway needs none");
        }

        // The air at a body's surface: Kerbin's one atmosphere, none on the Mun.
        [TestMethod]
        public void TheAirAtASurfaceIsTheBodysPressureOrNone()
        {
            PlanThrust thrust = Launch(180.0).Thrust;

            Assert.AreEqual(1.0, thrust.SurfaceAir(Kerbin), 1e-12);
            Assert.AreEqual(0.0, thrust.SurfaceAir(Mun), 1e-12);
        }

        // The launch step lifts off on the vessel's one stage, which the budget burns at the vessel's 10 t.
        [TestMethod]
        public void TheLaunchBurnsTheOneStageAtTenTonnes()
        {
            PlannerModel model = Launch(180.0);

            ThrustStep entry = model.Thrust.Entry(0);

            Assert.IsNotNull(entry);
            Assert.IsTrue(entry.Twr, "a launch is decided by its TWR");
            Assert.AreEqual(Kerbin, entry.Body);
            Assert.AreEqual(1, entry.Uses.Count);
            Assert.AreEqual(1, model.Thrust.FirstUse(0).Stage);
            Assert.AreEqual(10.0, model.Thrust.FirstUse(0).Mass, 1e-9);
            Assert.AreSame(model.Thrust.FirstUse(0), model.Thrust.LastUse(0), "one stage is both the first and the last");
            Assert.AreSame(model.Thrust.FirstUse(0), model.Thrust.Deciding(entry));
            Assert.IsNull(model.Thrust.Entry(1), "no such row");
            Assert.AreEqual(1, model.Thrust.Entries.Count);
        }

        // The texts of a launch that lifts off: what it needs, what it has in green, and the summary says all is well.
        [TestMethod]
        public void ALaunchThatLiftsOffIsWrittenInGreen()
        {
            PlanThrust thrust = Launch(180.0).Thrust;
            ThrustStep entry = thrust.Entry(0);

            Assert.AreEqual("<color=#9a9a9a>TWR 1.20</color>", thrust.NeedsText(entry));
            Assert.AreEqual("<color=#8fdc7a>S1  1.83</color>", thrust.HasText(entry));
            Assert.AreEqual("<color=#8fdc7a>Enough thrust on every step</color>", thrust.Summary());
            Assert.AreEqual("", thrust.NeedsText(null));
            Assert.AreEqual("", thrust.HasText(null));
        }

        // A vessel that cannot lift off says so in the step's warnings, in red in the texts, and in the summary.
        [TestMethod]
        public void AVesselThatCannotLiftOffIsWarnedOf()
        {
            PlannerModel model = Launch(90.0);
            ThrustStep entry = model.Thrust.Entry(0);

            Assert.AreEqual("TWR 0.92 of S1 is too low to lift off Kerbin", model.Warnings[0].Single(line => line.StartsWith("TWR")));
            Assert.AreEqual("<color=#e6474d>S1  0.92</color>", model.Thrust.HasText(entry));
            Assert.AreEqual("<color=#e6474d>TWR too low on 1 step</color>", model.Thrust.Summary());
        }

        // The warnings of a vessel that lifts off do not mention the TWR.
        [TestMethod]
        public void AVesselThatLiftsOffHasNoTwrWarning()
        {
            PlannerModel model = Launch(180.0);

            Assert.IsFalse(model.Warnings[0].Any(line => line.StartsWith("TWR")));
        }

        // A burn in orbit is decided by the share of the orbit it takes, not by a TWR.
        [TestMethod]
        public void ABurnInOrbitIsDecidedByTheShareOfTheOrbit()
        {
            PlannerModel model = PlannerModelFixture.Model(
                PlannerModelFixture.OneStage(6000.0, 200.0, 180.0),
                PlannerTests.Orbit(Kerbin, 80000), PlannerTests.Orbit(Mun, 14000));

            ThrustStep entry = model.Thrust.Entry(0);

            Assert.IsNotNull(entry);
            Assert.IsFalse(entry.Twr);
            Assert.IsTrue(entry.Burns.Count > 0, "the transfer burn and the capture");
            Assert.IsNotNull(entry.Longest);
            StringAssert.Contains(model.Thrust.NeedsText(entry), "% orbit");
        }

        // A stage's RCS: the TWR at standard gravity as it starts and as the propellant is spent, and how long it burns.
        [TestMethod]
        public void TheRcsOfAStageIsWorkedOutAtStandardGravity()
        {
            RcsInfo rcs = new RcsInfo { Mass = 2.0, Thrust = 0.4, Isp = 200.0, Propellant = 0.1 };

            Assert.AreEqual(0.4 / (2.0 * 9.80665), rcs.StartTwr, 1e-12);
            Assert.AreEqual(0.4 / (1.9 * 9.80665), rcs.EndTwr, 1e-12);
            Assert.AreEqual(0.1 * 200.0 * 9.80665 / 0.4, rcs.Seconds, 1e-9);
            Assert.AreEqual(0.0, new RcsInfo().StartTwr, "no mass, no ratio");
            Assert.AreEqual(0.0, new RcsInfo { Mass = 1.0, Thrust = 1.0 }.Seconds, "no Isp, no burn time");
        }

        // An RCS ratio, far below 1, shows three places under 0.1 and two above.
        [TestMethod]
        public void AnRcsRatioShowsThreePlacesUnderOneTenth()
        {
            Assert.AreEqual("0.050", PlanThrust.RcsRatio(0.05));
            Assert.AreEqual("0.10", PlanThrust.RcsRatio(0.1));
            Assert.AreEqual("1.50", PlanThrust.RcsRatio(1.5));
        }
    }
}
