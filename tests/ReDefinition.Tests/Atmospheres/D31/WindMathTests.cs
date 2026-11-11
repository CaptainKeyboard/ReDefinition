using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.D31
{
    // D31: the arithmetic of the wind on vehicles, without the game: the frame rotation, the height and density profile, the speed cap, the
    // slew limit, the ramp, the spin cut-off and the acceleration clamp.
    [TestClass]
    public class WindMathTests
    {
        private static readonly Vec3 EastW = new Vec3(1, 0, 0), NorthW = new Vec3(0, 1, 0), UpW = new Vec3(0, 0, 1);

        [TestMethod]
        public void TheHeightProfileIsTheGroundFactorOnTheGroundAndFullFromTheFullHeight()
        {
            Assert.AreEqual(0.1, WindMath.HeightProfile(0.0, 0.1, 50.0), 1e-12);
            Assert.AreEqual(1.0, WindMath.HeightProfile(50.0, 0.1, 50.0), 1e-12);
            Assert.AreEqual(1.0, WindMath.HeightProfile(5000.0, 0.1, 50.0), 1e-12);
            double previous = -1.0;
            for (double h = 0; h <= 60; h += 2.5)
            {
                double p = WindMath.HeightProfile(h, 0.1, 50.0);
                Assert.IsTrue(p >= previous, "monotone at " + h);
                previous = p;
            }
            Assert.AreEqual(1.0, WindMath.HeightProfile(double.NaN, 0.1, 50.0), "an unknown height counts as high");
            Assert.AreEqual(1.0, WindMath.HeightProfile(-1.0, 0.1, 50.0));
        }

        [TestMethod]
        public void TheDensityProfileIsZeroInVacuumAndFullInDenseAir()
        {
            Assert.AreEqual(0.0, WindMath.DensityProfile(0.0));
            Assert.AreEqual(0.0, WindMath.DensityProfile(double.NaN));
            Assert.AreEqual(0.0, WindMath.DensityProfile(WindMath.DensityFadeFrom));
            Assert.AreEqual(1.0, WindMath.DensityProfile(1.225));
            double mid = WindMath.DensityProfile(0.5 * (WindMath.DensityFadeFrom + WindMath.DensityFull));
            Assert.IsTrue(mid > 0.4 && mid < 0.6, "the middle of the fade is about half, was " + mid);
        }

        [TestMethod]
        public void TheSpeedCapRisesFromMaxWindToTheViolentWindWithTheLevel()
        {
            Assert.AreEqual(12.0, WindMath.SpeedCap(12.0, 25.0, 0.0), 1e-9);
            Assert.AreEqual(12.0, WindMath.SpeedCap(12.0, 25.0, 0.4), 1e-9);
            Assert.AreEqual(25.0, WindMath.SpeedCap(12.0, 25.0, 0.8), 1e-9);
            Assert.AreEqual(25.0, WindMath.SpeedCap(12.0, 25.0, 1.0), 1e-9);
            Assert.AreEqual(12.0, WindMath.SpeedCap(12.0, 5.0, 1.0), "a violent wind under maxWind does not lower the cap");
        }

        [TestMethod]
        public void TheWindIsRotatedFromTheLocalFrameIntoTheWorldWithTheFramesOfThatMoment()
        {
            // A wind of 10 m/s to the east, 5 to the north, 2 up, in a world whose axes are the local ones.
            Vec3 w = WindMath.EnuToWorld(10, 5, 2, EastW, NorthW, UpW);
            Assert.AreEqual(10.0, w.X, 1e-12);
            Assert.AreEqual(5.0, w.Y, 1e-12);
            Assert.AreEqual(2.0, w.Z, 1e-12);
            // The body turns by 90 degrees about the world's z axis: east is now where north was, north where west was.
            Vec3 e2 = new Vec3(0, 1, 0), n2 = new Vec3(-1, 0, 0);
            Vec3 r = WindMath.EnuToWorld(10, 5, 2, e2, n2, UpW);
            Assert.AreEqual(-5.0, r.X, 1e-12);
            Assert.AreEqual(10.0, r.Y, 1e-12);
            Assert.AreEqual(2.0, r.Z, 1e-12);
            Assert.AreEqual(w.Length, r.Length, 1e-12, "a rotation keeps the speed");
        }

        [TestMethod]
        public void TheFrameRotationKeepsAMillimetrePerSecondAtTheScaleOfAPlanet()
        {
            // The basis is built from vectors of a body of 600 km radius in double; the wind comes out exact to far under 1e-9 m/s.
            double a = 1.2345678901234;
            var e = new Vec3(-Math.Sin(a), Math.Cos(a), 0);
            var n = new Vec3(0, 0, 1);
            var u = new Vec3(Math.Cos(a), Math.Sin(a), 0);
            Vec3 w = WindMath.EnuToWorld(3.0, 4.0, 0.0, e, n, u);
            Assert.AreEqual(5.0, w.Length, 1e-12);
        }

        [TestMethod]
        public void TheWindSlewsByAtMostTheLimitPerSecondAsAVector()
        {
            var from = new Vec3(0, 0, 0);
            var to = new Vec3(30, 40, 0);
            Vec3 step = WindMath.Slew(from, to, 8.0, 0.02);
            Assert.AreEqual(0.16, step.Length, 1e-9);
            Assert.AreEqual(0.6, step.X / step.Length, 1e-9, "the direction is the target's, it does not jump");
            Vec3 w = from;
            for (int i = 0; i < 50; i++) w = WindMath.Slew(w, to, 8.0, 0.02);
            Assert.AreEqual(8.0, w.Length, 1e-9, "after a second the speed has moved 8 m/s");
            Assert.AreEqual(to.X, WindMath.Slew(new Vec3(29.99, 40, 0), to, 8.0, 0.02).X, 1e-12, "a short way arrives");
        }

        [TestMethod]
        public void TheRampRisesFromZeroToOneOverThreeSeconds()
        {
            Assert.AreEqual(0.0, WindMath.Ramp(0.0));
            Assert.AreEqual(0.0, WindMath.Ramp(-1.0));
            Assert.AreEqual(0.5, WindMath.Ramp(1.5), 1e-12);
            Assert.AreEqual(1.0, WindMath.Ramp(3.0));
            Assert.AreEqual(1.0, WindMath.Ramp(100.0));
            Assert.AreEqual(0.0, WindMath.Ramp(double.NaN));
        }

        [TestMethod]
        public void AVesselThatSpinsTooFastGetsNoWind()
        {
            Assert.IsFalse(WindMath.SpinTooFast(1.0));
            Assert.IsFalse(WindMath.SpinTooFast(WindMath.MaxAngularSpeed));
            Assert.IsTrue(WindMath.SpinTooFast(WindMath.MaxAngularSpeed + 0.01));
            Assert.IsTrue(WindMath.SpinTooFast(double.NaN));
        }

        [TestMethod]
        public void TheClampKeepsTheExtraAccelerationUnderTheLimitAndTheDirectionOfTheWind()
        {
            var v = new Vec3(0, 0, -5);                // a canopy at its terminal speed
            var wind = new Vec3(300, 400, 0);          // an absurd wind of 500 m/s
            double density = 1.2, perMass = 0.4;       // m2 per tonne
            double extra = WindMath.ExtraAcceleration(density, perMass, v, wind);
            Assert.IsTrue(extra > 1.5, "the absurd wind would be over the limit, was " + extra);
            double s = WindMath.ClampShare(density, perMass, v, wind, 1.5);
            Assert.IsTrue(s > 0.0 && s < 1.0, "the clamp fires, share " + s);
            Assert.IsTrue(WindMath.ExtraAcceleration(density, perMass, v, wind * s) <= 1.5 + 1e-6);
            Assert.IsTrue(WindMath.ExtraAcceleration(density, perMass, v, wind * (s + 0.02 > 1 ? 1 : s + 0.02)) > 1.5 - 1e-3 || s > 0.98, "the share is the largest that fits");
            Assert.AreEqual(1.0, WindMath.ClampShare(density, perMass, v, new Vec3(1, 0, 0), 1.5), "a light wind passes whole");
            Assert.AreEqual(0.0, WindMath.ClampShare(density, perMass, v, wind, 0.0));
        }

        [TestMethod]
        public void TheClampDoesNotFireForAWindThatMakesNoDrag()
        {
            // No air, or no drag area: the extra acceleration is zero and the wind passes.
            Assert.AreEqual(0.0, WindMath.ExtraAcceleration(0.0, 0.4, new Vec3(0, 0, -5), new Vec3(30, 0, 0)));
            Assert.AreEqual(1.0, WindMath.ClampShare(0.0, 0.4, new Vec3(0, 0, -5), new Vec3(300, 0, 0), 1.5));
        }

        [TestMethod]
        public void TheOneGustFunctionStaysWithinItsDepthAndIsDeterministic()
        {
            var pos = new UnityEngine.Vector3(1000f, 2000f, 3000f);
            var dir = new UnityEngine.Vector3(1f, 0f, 0f);
            float min = float.MaxValue, max = float.MinValue;
            for (double ut = 0; ut < 600; ut += 0.5)
            {
                float g = WindNoise.Gust(ut, pos, dir, 1f, 7);
                Assert.AreEqual(g, WindNoise.Gust(ut, pos, dir, 1f, 7), "the same inputs give the same gust");
                min = Math.Min(min, g);
                max = Math.Max(max, g);
            }
            Assert.IsTrue(min >= 1f - WindNoise.DepthAtBlizzard - 1e-4f && max <= 1f + WindNoise.DepthAtBlizzard + 1e-4f, "within +-40 % at level 1: " + min + " .. " + max);
            Assert.IsTrue(max - min > 0.3f, "the gusts do swing, " + (max - min));
        }
    }
}
