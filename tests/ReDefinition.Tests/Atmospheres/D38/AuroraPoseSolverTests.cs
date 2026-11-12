using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.D38
{
    // The sun elevation a night pose of the aurora start can ask for at a latitude (K-W3 finding: the pose at 70 degrees N latitude was
    // reported as out of reach; on a body without tilt it is reachable, and the solver says which latitudes are not).
    [TestClass]
    public class AuroraPoseSolverTests
    {
        private static double Elevation(double latDeg, double declinationDeg, double cosHour)
        {
            double lat = latDeg * Math.PI / 180.0, dec = declinationDeg * Math.PI / 180.0;
            return Math.Asin(Math.Sin(lat) * Math.Sin(dec) + Math.Cos(lat) * Math.Cos(dec) * cosHour) * 180.0 / Math.PI;
        }

        [TestMethod]
        public void AtSeventyDegreesWithoutTiltTheSunReachesMinusTwelve()
        {
            double low, high;
            AuroraPoseSolver.Range(70.0, 0.0, out low, out high);
            Assert.AreEqual(-20.0, low, 1e-9, "the midnight sun at 70 degrees latitude without tilt stands at -20");
            Assert.AreEqual(20.0, high, 1e-9);
            AuroraPoseSolver.Plan plan = AuroraPoseSolver.Solve(70.0, 0.0);
            Assert.IsFalse(plan.Skip);
            Assert.AreEqual(-12.0, plan.WantedDeg, 1e-9);
        }

        [TestMethod]
        public void TheAskedElevationIsRaisedToTheLowestTheLatitudeReachesPlusTheMargin()
        {
            AuroraPoseSolver.Plan plan = AuroraPoseSolver.Solve(78.0, 0.0);
            Assert.IsFalse(plan.Skip);
            Assert.AreEqual(-11.0, plan.WantedDeg, 1e-9);
            Assert.IsTrue(plan.WantedDeg >= plan.LowestDeg);
        }

        [TestMethod]
        public void ALatitudeWithoutNightIsSkippedAndNotAsked()
        {
            Assert.IsTrue(AuroraPoseSolver.Solve(85.0, 0.0).Skip, "the midnight sun at 85 degrees stands at -5");
            // A body with a tilt: summer at 70 degrees N latitude has no night, winter has one deeper than asked for.
            AuroraPoseSolver.Plan summer = AuroraPoseSolver.Solve(70.0, 23.0);
            Assert.IsTrue(summer.Skip, "lowest sun " + summer.LowestDeg);
            AuroraPoseSolver.Plan winter = AuroraPoseSolver.Solve(70.0, -23.0);
            Assert.IsFalse(winter.Skip);
            Assert.AreEqual(-12.0, winter.WantedDeg, 1e-9);
        }

        [TestMethod]
        public void TheHourAngleOfAReachableElevationGivesThatElevationBack()
        {
            foreach (double dec in new[] { 0.0, 23.0, -23.0, 3.0 })
            {
                foreach (double lat in new[] { 60.0, 70.0, 78.0, 45.0 })
                {
                    AuroraPoseSolver.Plan plan = AuroraPoseSolver.Solve(lat, dec);
                    double c = AuroraPoseSolver.CosHourAngle(lat, dec, plan.WantedDeg);
                    Assert.IsTrue(c >= -1.0 - 1e-9 && c <= 1.0 + 1e-9, "lat " + lat + " dec " + dec + " cos " + c);
                    Assert.AreEqual(plan.WantedDeg, Elevation(lat, dec, Math.Max(-1.0, Math.Min(1.0, c))), 1e-6, "lat " + lat + " dec " + dec);
                }
            }
        }

        [TestMethod]
        public void AnElevationBeyondTheLowestOfTheLatitudeHasNoHourAngle()
        {
            double c = AuroraPoseSolver.CosHourAngle(70.0, 0.0, -25.0);
            Assert.IsTrue(c < -1.0, "cos " + c);
            Assert.IsTrue(AuroraPoseSolver.CosHourAngle(90.0, 0.0, -12.0) > 1.0, "at the pole no hour angle exists");
        }

        [TestMethod]
        public void TheCurtainStandsOverheadUnderTheOvalAndLowerTheFartherAway()
        {
            const double R = 600000.0, Mid = 62500.0;
            Assert.AreEqual(90.0, AuroraPoseSolver.CurtainElevationDeg(70.0, 70.0, R, Mid), 1e-6, "under the oval");
            double near = AuroraPoseSolver.CurtainElevationDeg(66.0, 70.0, R, Mid);
            double far = AuroraPoseSolver.CurtainElevationDeg(66.0, 78.0, R, Mid);
            // 4 degrees of latitude on Kerbin are 42 km along the meridian: the middle of the layer is seen from 55 to 60 degrees up (the K-W3 repeat picture at the top edge)
            Assert.IsTrue(near > 50.0 && near < 65.0, "near " + near);
            Assert.IsTrue(far < near && far > 15.0 && far < 35.0, "far " + far);
            Assert.AreEqual(near, AuroraPoseSolver.CurtainElevationDeg(74.0, 70.0, R, Mid), 1e-9, "the same on either side of the camera");
        }

        [TestMethod]
        public void TheDeclinationIsTheAngleAboveTheEquatorPlane()
        {
            Assert.AreEqual(0.0, AuroraPoseSolver.DeclinationDeg(0.0), 1e-9);
            Assert.AreEqual(30.0, AuroraPoseSolver.DeclinationDeg(0.5), 1e-9);
            Assert.AreEqual(90.0, AuroraPoseSolver.DeclinationDeg(2.0), 1e-9, "a value past the range is held to it");
        }
    }
}
