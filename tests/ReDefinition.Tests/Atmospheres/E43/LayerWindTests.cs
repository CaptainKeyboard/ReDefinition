using System;
using AtmoSim.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.E43
{
    // Package E43: the steering of the layers' wind (pure parts), the refill of the window and the switches. The game-only parts (EVE's members, the
    // axis and sense of its rotation) are rows of the series, not tests.
    [TestClass]
    public class LayerWindTests
    {
        private const double Kerbin = 600000.0;

        [TestMethod]
        public void AWesterlyWindAbovePatternSpeedTurnsTheLayerEastward()
        {
            double lat = 45.0 * Math.PI / 180.0;
            double rate = LayerWindSteering.TargetRate(25.0, 17.0, lat, Kerbin);
            Assert.IsTrue(rate > 0.0);
            Assert.AreEqual(8.0 / (Kerbin * Math.Cos(lat)), rate, 1e-12);
            Assert.IsTrue(LayerWindSteering.TargetRate(9.0, 17.0, lat, Kerbin) < 0.0, "a low layer slower than the patterns turns westward against them");
        }

        [TestMethod]
        public void TheSumOfPatternAndDriftIsTheWindOfTheLayer()
        {
            // the rule of the brief: pattern motion + EVE's drift = field wind, in both signs, away from the poles
            foreach (double latDeg in new[] { -60.0, -30.0, 0.0, 20.0, 45.0, 60.0 })
                foreach (double wind in new[] { -5.0, 3.0, 24.0 })
                {
                    double lat = latDeg * Math.PI / 180.0, pattern = 15.0;
                    double rate = LayerWindSteering.TargetRate(wind, pattern, lat, Kerbin);
                    double drift = rate * Kerbin * Math.Cos(lat);
                    Assert.AreEqual(wind, pattern + drift, 1e-9, "lat " + latDeg);
                }
        }

        [TestMethod]
        public void TheRateIsLimitedNearThePolesAndInItsChange()
        {
            double rate = LayerWindSteering.TargetRate(40.0, 0.0, 89.0 * Math.PI / 180.0, Kerbin);
            Assert.IsTrue(Math.Abs(rate) <= LayerWindSteering.MaxRateRadS + 1e-15);
            double r = 0.0;
            for (int i = 0; i < 10; i++) r = LayerWindSteering.StepRate(r, 5.0e-5, 1.0, LayerWindSteering.MaxAccelRadS2);
            Assert.AreEqual(10 * LayerWindSteering.MaxAccelRadS2, r, 1e-15);
            Assert.AreEqual(5.0e-5, LayerWindSteering.StepRate(4.99999e-5, 5.0e-5, 1.0, LayerWindSteering.MaxAccelRadS2), 1e-18, "a target within one step is reached");
            // 0 to 20 m/s at 45 degrees takes minutes, not frames
            double target = 20.0 / (Kerbin * Math.Cos(Math.PI / 4.0));
            Assert.IsTrue(target / LayerWindSteering.MaxAccelRadS2 > 120.0);
        }

        [TestMethod]
        public void TheUpwardSpeedFollowsTheUpdraftWithALimit()
        {
            Assert.AreEqual(0.5f, LayerWindSteering.UpTarget(0.5f, 0.0), 1e-6);
            Assert.AreEqual(1.0f, LayerWindSteering.UpTarget(0.5f, 100.0), 1e-6, "the share is capped at the reference updraft");
            Assert.AreEqual(0.5f, LayerWindSteering.UpTarget(0.5f, -4.0), 1e-6, "a downdraft does not make the clouds sink");
            float v = 0.5f;
            for (int i = 0; i < 3; i++) v = LayerWindSteering.StepUp(v, 1.0f, 0.5f, 1f);
            Assert.AreEqual(0.5f * (1f + 3f * LayerWindSteering.UpMaxChangePerS), v, 1e-6);
        }

        [TestMethod]
        public void TheAngleErrorIsTheDifferenceOfTheDirections()
        {
            Assert.AreEqual(0.0, LayerWindSteering.AngleErrorDeg(10.0, 10.0, 0.0), 1e-9);
            Assert.AreEqual(Math.Atan(0.3) * 180.0 / Math.PI, LayerWindSteering.AngleErrorDeg(10.0, 10.0, 3.0), 1e-9, "a northward wind component is an error of an eastward drift");
            Assert.AreEqual(180.0, LayerWindSteering.AngleErrorDeg(-10.0, 10.0, 0.0), 1e-9);
        }

        [TestMethod]
        public void TheLayerMiddleIsTheMeanRadiusMinusTheBody()
        {
            Assert.AreEqual(5000.0, LayerWindSteering.LayerMidHeightM(604000f, 606000f, Kerbin), 1e-3);
        }

        [TestMethod]
        public void TheWindowIsNotOursWhereThereIsNoGeneratorOrEverySwitchIsOff()
        {
            bool couple = LayerWindOptions.Couple;
            try
            {
                Assert.IsTrue(LayerWindOptions.AnyOn);
                LayerWindOptions.WindShearOverride = LayerWindOptions.VerticalMotionOverride = LayerWindOptions.AnvilTiltOverride = false;
                LayerWindOptions.Couple = false;
                Assert.IsFalse(LayerWindOptions.AnyOn);
            }
            finally
            {
                LayerWindOptions.WindShearOverride = LayerWindOptions.VerticalMotionOverride = LayerWindOptions.AnvilTiltOverride = null;
                LayerWindOptions.Couple = couple;
            }
        }

        [TestMethod]
        public void TheRefillWritesTheLayeredColumnsInTheFieldsUnitsAndNeverASinkingUpdraft()
        {
            var g = new WeatherGenerator(BodyParams.KerbinLike(), 1);
            var w = new WeatherWindow();
            w.Resize(8, 8, 12);
            w.CentreLatDeg = 12.0; w.CentreLonDeg = 40.0; w.RadiusKm = 100.0;
            w.SpacingHorizontalM = 28000f; w.SpacingVerticalM = 900f; w.OriginAltM = 450f; w.Ut = 3.1e6;
            var o = LayerWindOptions.Layers();
            WindowAdvection.Refill(w, 0, 3, g, o);
            WindowAdvection.Refill(w, 3, 5, g, o);
            // the same columns written one by one by the core
            double a = g.Body.RadiusM, cosC = Math.Cos(12.0 * Math.PI / 180.0);
            var d = new float[12]; var up = new float[12];
            double worstWater = 0.0;
            for (int j = 0; j < 8; j++)
                for (int i = 0; i < 8; i++)
                {
                    double lat = 12.0 * Math.PI / 180.0 + (j - 3.5) * 28000.0 / a, lon = DMath.WrapPi(40.0 * Math.PI / 180.0 + (i - 3.5) * 28000.0 / (a * cosC));
                    LayerCoupling.Column(g, lat, lon, w.Ut, 450.0, 900.0, 12, d, 0, 1, up, o);
                    for (int k = 0; k < 12; k++)
                    {
                        int index = (k * 8 + j) * 8 + i;
                        worstWater = Math.Max(worstWater, Math.Abs(w.CloudWater[index] - d[k] * GeneratorField.WindowGramsPerDensity));
                        Assert.IsTrue(w.Updraft[index] >= 0f);
                        Assert.AreEqual(Math.Max(0f, up[k]), w.Updraft[index], 1e-6);
                    }
                }
            Assert.IsTrue(worstWater < 1e-5, "difference " + worstWater);
        }

        [TestMethod]
        public void TheLayerOptionsFollowTheSwitches()
        {
            LayerOptions o = LayerWindOptions.Layers();
            Assert.IsTrue(o.Shear && o.Vertical && o.Tilt && o.Couple && o.AmbientUpdraft);
            Assert.AreEqual(LayerSteeringMode.MainPeriod, LayerWindOptions.Steering);
        }
    }
}
