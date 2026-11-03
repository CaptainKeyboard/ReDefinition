using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Autopilot;
using ReDefinition.Autopilot.Guidance;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Orbits;

namespace ReDefinition.Tests
{
    // The arithmetic of the climb, the descent's aim and the guidance board, with values that can be worked out by hand.
    [TestClass]
    public class GuidanceMathTests
    {
        // Kerbin's gravitational parameter and radius, and the radius of a 100 km orbit.
        private const double Mu = 3.5316e12;
        private const double Body = 600000.0;
        private const double Low = 700000.0;

        private static State Circular(double radius)
        {
            return new State(new V3(radius, 0.0, 0.0), new V3(0.0, Math.Sqrt(Mu / radius), 0.0));
        }

        // From a circular orbit, the burn along the velocity that puts the apoapsis at a height is the first burn of a Hohmann transfer.
        [TestMethod]
        public void TheRaiseBurnFromACircularOrbitIsTheFirstBurnOfAHohmannTransfer()
        {
            State here = Circular(Low);
            double aim = 1000000.0 - Body;
            double speed = Math.Sqrt(Mu / Low);
            double hohmann = speed * (Math.Sqrt(2.0 * 1000000.0 / (Low + 1000000.0)) - 1.0);
            Assert.AreEqual(hohmann, ClimbGuide.RaiseBurn(Mu, Body, here, here.V.Unit, aim), 1e-3);
        }

        // At the periapsis of an ellipse that reaches higher than the aim, the burn is under zero, by as much as the speed there is over the Hohmann transfer's.
        [TestMethod]
        public void TheRaiseBurnIsNegativeWhereTheApoapsisIsTooHigh()
        {
            double circular = Math.Sqrt(Mu / Low);
            State here = new State(new V3(Low, 0.0, 0.0), new V3(0.0, 1.15 * circular, 0.0));
            double burn = ClimbGuide.RaiseBurn(Mu, Body, here, here.V.Unit, 800000.0 - Body);
            Assert.AreEqual(circular * Math.Sqrt(2.0 * 800000.0 / (Low + 800000.0)) - 1.15 * circular, burn, 1e-3);
            Assert.IsTrue(burn < 0.0);
        }

        [TestMethod]
        public void TheApoapsisOfACircularOrbitIsItsRadiusAndOfAnOpenOneIsInfinite()
        {
            Assert.AreEqual(Low, ClimbGuide.ApoapsisRadius(Circular(Low), Mu), 1e-3);
            State open = new State(new V3(Low, 0.0, 0.0), new V3(0.0, 1.5 * Math.Sqrt(2.0 * Mu / Low), 0.0));
            Assert.IsTrue(double.IsPositiveInfinity(ClimbGuide.ApoapsisRadius(open, Mu)));
        }

        // A throttle that falls evenly over T seconds from full has covered a T / 2 times the throttle squared of the burn when it is at the throttle.
        [TestMethod]
        public void TheEvenFallOfTheThrottleCoversTheBurnAsTheSquareOfTheThrottle()
        {
            double a = 10.0;
            Assert.AreEqual(0.5 * a * ClimbGuide.RampSeconds, ClimbGuide.RampBurn(a), 1e-9);
            double full = ClimbGuide.RampBurn(a);
            Assert.AreEqual(1.0, ClimbGuide.RampShare(full, a), 1e-12);
            Assert.AreEqual(0.5, ClimbGuide.RampShare(full * 0.25, a), 1e-12);
            Assert.AreEqual(0.0, ClimbGuide.RampShare(0.0, a), 1e-12);
            // More burn than the fall covers asks for full thrust, and no engines ask for it as well.
            Assert.AreEqual(1.0, ClimbGuide.RampShare(full * 3.0, a), 1e-12);
            Assert.AreEqual(1.0, ClimbGuide.RampShare(50.0, 0.0), 1e-12);
        }

        // The measured share moves towards a sample by the share of the time constant that went by, and a sample outside the range is kept inside it.
        [TestMethod]
        public void TheMeasuredShareMovesTowardsTheSampleAndStaysInItsRange()
        {
            Assert.AreEqual(1.0 + (0.5 - 1.0) * (1.0 - Math.Exp(-1.0)), ClimbGuide.Blend(1.0, 0.5, 2.0), 1e-12);
            Assert.AreEqual(1.0, ClimbGuide.Blend(1.0, 1.0, 0.5), 1e-12);
            Assert.AreEqual(1.5, ClimbGuide.Blend(1.0, 9.0, 1e6), 1e-9);
            Assert.AreEqual(0.2, ClimbGuide.Blend(1.0, -3.0, 1e6), 1e-9);
        }

        // An orbit with the place under its track needs no turn, and one with it off the track is turned by the angle between them, which costs 2 v sin(angle / 2).
        [TestMethod]
        public void ThePlaneTurnCostsTwoVSinHalfTheAngleAndBringsThePlaceUnderTheTrack()
        {
            State here = Circular(Low);
            double angle = 10.0 * Math.PI / 180.0;
            V3 place = new V3(0.0, Math.Cos(angle), Math.Sin(angle));
            Burn burn;
            PlaneTurn turn = PlaneChange.Plan(here, Mu, Body, time => place, 200.0, double.NaN, out burn);
            Assert.AreEqual(PlaneTurn.Needed, turn);
            Assert.AreEqual(2.0 * Math.Sqrt(Mu / Low) * Math.Sin(angle / 2.0), burn.Magnitude, 1e-3);
            State at = Kepler.Propagate(here, burn.Delay, Mu);
            State after = new State(at.R, at.V + burn.DeltaV);
            Assert.AreEqual(0.0, V3.Dot(place, V3.Cross(after.R, after.V).Unit), 1e-9);
        }

        [TestMethod]
        public void ThePlaneIsLeftWhereThePlaceIsUnderTheTrackOrTheOrbitIsOpen()
        {
            Burn burn;
            V3 near = new V3(0.0, Math.Cos(0.001), Math.Sin(0.001));
            Assert.AreEqual(PlaneTurn.None, PlaneChange.Plan(Circular(Low), Mu, Body, time => near, 1000.0, double.NaN, out burn));
            State open = new State(new V3(Low, 0.0, 0.0), new V3(0.0, 1.5 * Math.Sqrt(2.0 * Mu / Low), 0.0));
            V3 off = new V3(0.0, 0.0, 1.0);
            Assert.AreEqual(PlaneTurn.Impossible, PlaneChange.Plan(open, Mu, Body, time => off, 200.0, double.NaN, out burn));
        }

        // The retrograde burn that lowers the periapsis from a circular orbit to the body's surface is the first burn of the Hohmann transfer down.
        [TestMethod]
        public void TheRetrogradeBurnToTheSurfaceIsTheFirstBurnOfAHohmannTransferDown()
        {
            State here = Circular(Low);
            V3 burn;
            Assert.IsTrue(DeorbitAim.Retrograde(here, Mu, Body, out burn));
            double speed = Math.Sqrt(Mu / Low);
            Assert.AreEqual(speed * (1.0 - Math.Sqrt(2.0 * Body / (Low + Body))), burn.Length, 1e-3);
            Assert.IsTrue(V3.Dot(burn, here.V) < 0.0);
            Assert.AreEqual(Body, DeorbitAim.PeriapsisAfter(here, Mu, burn.Length), 1e-2);
        }

        // An orbit that is down there already is kept, and a periapsis that no burn short of stopping reaches is none.
        [TestMethod]
        public void TheRetrogradeBurnIsNoneWhereTheOrbitIsLowAlreadyAndFalseWhereItCannotReach()
        {
            V3 burn;
            Assert.IsTrue(DeorbitAim.Retrograde(Circular(Low), Mu, Low + 1000.0, out burn));
            Assert.AreEqual(0.0, burn.Length, 1e-12);
            Assert.IsFalse(DeorbitAim.Retrograde(Circular(Low), Mu, 10.0, out burn));
        }

        [TestMethod]
        public void TheDistanceOverTheGroundFollowsTheGreatCircleAndTheLongitudeWraps()
        {
            Assert.AreEqual(Body * Math.PI / 2.0, DeorbitAim.Distance(Body, 0.0, 0.0, 0.0, 90.0), 1e-6);
            Assert.AreEqual(Body * Math.PI / 2.0, DeorbitAim.Distance(Body, 0.0, 10.0, 90.0, 123.0), 1e-6);
            Assert.AreEqual(0.0, DeorbitAim.Distance(Body, 20.0, 179.0, 20.0, -181.0), 1e-3);
            Assert.AreEqual(-170.0, DeorbitAim.Wrap(190.0), 1e-12);
            Assert.AreEqual(170.0, DeorbitAim.Wrap(-190.0), 1e-12);
            Assert.AreEqual(30.0, DeorbitAim.Wrap(750.0), 1e-12);
        }

        // The bar is under the mark where thrust is to be given and over it where less is wanted; the number says the same, as a speed, by its sign.
        [TestMethod]
        public void TheBarAndTheNumberOfTheBoardAlwaysAgree()
        {
            Assert.AreEqual(0.5, GuidanceBoard.NeedOf(0.8, 0.3), 1e-12);
            Assert.AreEqual(1.0, GuidanceBoard.NeedOf(2.0, -1.0), 1e-12);
            Assert.AreEqual(-1.0, GuidanceBoard.NeedOf(-2.0, 1.0), 1e-12);
            Assert.AreEqual(0.25, GuidanceBoard.ArcOf(0.5, false), 1e-12);
            Assert.AreEqual(0.75, GuidanceBoard.ArcOf(-0.5, false), 1e-12);
            Assert.AreEqual(0.0, GuidanceBoard.ArcOf(0.5, true), 1e-12);
            Assert.AreEqual("40 m/s short", GuidanceBoard.NumberTextOf(0.5, 80.0));
            Assert.AreEqual("40 m/s over", GuidanceBoard.NumberTextOf(-0.5, 80.0));
            Assert.AreEqual("0 m/s", GuidanceBoard.NumberTextOf(0.005, 80.0));
            Assert.AreEqual("", GuidanceBoard.NumberTextOf(0.5, 0.0));
            // Under the mark is short, over it is over: the arc and the sign of the number never disagree.
            for (double need = -1.0; need <= 1.0; need += 0.05)
            {
                string text = GuidanceBoard.NumberTextOf(need, 100.0);
                if (Math.Abs(need) * 100.0 < 1.0) continue;
                Assert.AreEqual(GuidanceBoard.ArcOf(need, false) < 0.5, text.EndsWith("short"), "need " + need);
            }
        }

        [TestMethod]
        public void TheLevelOfTheBoardIsZeroOnTargetAndTwoFarOff()
        {
            Assert.AreEqual(0, GuidanceBoard.LevelOf(0.05, false));
            Assert.AreEqual(1, GuidanceBoard.LevelOf(-0.3, false));
            Assert.AreEqual(2, GuidanceBoard.LevelOf(0.6, false));
            Assert.AreEqual(0, GuidanceBoard.LevelOf(0.9, true));
        }

        // The throttle holds the dynamic pressure to its limit and a part under its own: full under them and none at them.
        [TestMethod]
        public void TheHeatLimitsGiveFullThrottleUnderThemAndNoneAtThem()
        {
            Assert.AreEqual(1.0, Heat.PressureThrottle(0.0), 1e-12);
            Assert.AreEqual(1.0, Heat.PressureThrottle(Heat.MaxDynamicPressure * 0.99), 1e-12);
            Assert.AreEqual(0.25, Heat.PressureThrottle(Heat.MaxDynamicPressure * 1.05), 1e-9);
            Assert.AreEqual(0.0, Heat.PressureThrottle(Heat.MaxDynamicPressure * 1.07), 1e-12);
            Assert.AreEqual(1.0, Heat.TemperatureThrottle(0.9), 1e-12);
            Assert.AreEqual(0.5, Heat.TemperatureThrottle(0.975), 1e-9);
            Assert.AreEqual(0.0, Heat.TemperatureThrottle(1.0), 1e-12);
            Assert.AreEqual(0.0, Heat.TemperatureThrottle(1.2), 1e-12);
        }
    }
}
