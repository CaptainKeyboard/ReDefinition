using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Raytracing;

namespace ReDefinition.Tests
{
    // The light of a body the sun lights (RtReflectorLight): Lambert's sphere, and the
    // raytracing's brightness for it.
    [TestClass]
    public class RtReflectorLightTests
    {
        // The Mun: 200 km radius, 12 000 km from Kerbin; its surface some 0.3 linear.
        private const double MunRadius = 200000.0;
        private const double MunDistance = 12000000.0;

        // A disc seen past another: all of it in the open, none behind a larger disc, the part not covered between, and what a
        // smaller disc leaves in front of its middle.
        [TestMethod]
        public void ABodyBeforeTheSunHidesItsOverlap()
        {
            const double sun = 0.0047;
            Assert.AreEqual(1.0, RtReflectorLight.Uncovered(sun, 0.01, 0.1), 1e-12);
            Assert.AreEqual(0.0, RtReflectorLight.Uncovered(sun, 0.01, 0.002), 1e-12);
            Assert.AreEqual(1.0 - 0.5 * 0.5, RtReflectorLight.Uncovered(sun, sun * 0.5, 0.0), 1e-12);
            double edge = RtReflectorLight.Uncovered(sun, 0.01, 0.01 + sun / 2);
            Assert.IsTrue(edge > 0.0 && edge < 1.0);
            // Half the sun's width covered by a disc far larger: the visible part is about a half.
            Assert.AreEqual(0.5, RtReflectorLight.Uncovered(sun, 0.5, 0.5 + 1e-9), 0.01);
        }

        // Lambert's phase function: 1 at no phase angle, 1/pi at a half moon, 0 at the new, and falling in
        // between.
        [TestMethod]
        public void TheSphereIsFullAtNoPhaseAngleAndDarkAtHalfACircle()
        {
            Assert.AreEqual(1.0, RtReflectorLight.Phase(0.0), 1e-12);
            Assert.AreEqual(0.0, RtReflectorLight.Phase(Math.PI), 1e-12);
            Assert.AreEqual(1.0 / Math.PI, RtReflectorLight.Phase(Math.PI / 2), 1e-12);
            for (double a = 0.0; a < Math.PI; a += 0.1)
                Assert.IsTrue(RtReflectorLight.Phase(a + 0.1) < RtReflectorLight.Phase(a));
        }

        // What is left of a body's light: the phase times the sun that its shadow leaves, and never more than the full body's.
        [TestMethod]
        public void TheLightLeftIsThePhaseTimesTheSunTheShadowLeaves()
        {
            Assert.AreEqual(1.0, RtReflectorLight.Lit(0.0, 1.0), 1e-12);
            Assert.AreEqual(0.0, RtReflectorLight.Lit(Math.PI, 1.0), 1e-12);
            Assert.AreEqual(0.5 / Math.PI, RtReflectorLight.Lit(Math.PI / 2, 0.5), 1e-12);
            Assert.AreEqual(0.0, RtReflectorLight.Lit(0.0, 0.0), 1e-12);
            Assert.AreEqual(1.0, RtReflectorLight.Lit(0.0, 7.0), 1e-12);
        }

        // The full Mun sends the albedo times 2/3 times its angular radius squared, between 5 and 6
        // hundred-thousandths of the sunlight.
        [TestMethod]
        public void TheFullMunSendsAFewHundredThousandthsOfTheSunlight()
        {
            double share = RtReflectorLight.Share(0.3, MunRadius, MunDistance, 1.0, 1.0);
            Assert.AreEqual(0.3 * 2.0 / 3.0 * Math.Pow(MunRadius / MunDistance, 2), share, 1e-12);
            Assert.IsTrue(share > 5e-5 && share < 6e-5, share.ToString());
        }

        // No light from a body when the observer is inside it or it has no radius, and no brightness without a
        // share or a percentage.
        [TestMethod]
        public void NothingFromInsideABodyOrWithoutOne()
        {
            Assert.AreEqual(0.0, RtReflectorLight.Share(0.3, MunRadius, MunRadius * 0.5, 1.0, 1.0));
            Assert.AreEqual(0.0, RtReflectorLight.Share(0.3, 0.0, MunDistance, 1.0, 1.0));
            Assert.AreEqual(0.0, RtReflectorLight.Brightness(0.0, 100.0));
            Assert.AreEqual(0.0, RtReflectorLight.Brightness(1e-4, 0.0));
        }

        // At the default 100 % the full Mun lights the scene with between 5 and 7 hundredths of the sunlight.
        [TestMethod]
        public void TheFullMunLightsWithSomeSixHundredthsOfTheSun()
        {
            double share = RtReflectorLight.Share(0.3, MunRadius, MunDistance, 1.0, 1.0);
            double brightness = RtReflectorLight.Brightness(share, 100.0);
            Assert.IsTrue(brightness > 0.05 && brightness < 0.07, brightness.ToString());
        }

        // A larger share always gives more brightness, but never reaches the ceiling of 0.3 of the sunlight.
        [TestMethod]
        public void BrighterBodiesStayBrighterAndBelowTheCeiling()
        {
            double previous = 0.0;
            for (double share = 1e-8; share < 10.0; share *= 2.0)
            {
                double brightness = RtReflectorLight.Brightness(share, 100.0);
                Assert.IsTrue(brightness > previous);
                Assert.IsTrue(brightness < 0.3);
                previous = brightness;
            }
        }

        // The player's percentage scales the brightness in proportion.
        [TestMethod]
        public void ThePercentageScalesTheBrightness()
        {
            double share = 5e-5;
            Assert.AreEqual(2.0 * RtReflectorLight.Brightness(share, 100.0), RtReflectorLight.Brightness(share, 200.0), 1e-12);
            Assert.AreEqual(0.5 * RtReflectorLight.Brightness(share, 100.0), RtReflectorLight.Brightness(share, 50.0), 1e-12);
        }

        // The sun twice as far from the body as from the observer: a quarter of the light
        // at the observer reaches the body.
        [TestMethod]
        public void TheSunsDistanceToTheBodyCounts()
        {
            double near = RtReflectorLight.Share(0.3, MunRadius, MunDistance, 1.0, 1.0);
            double far = RtReflectorLight.Share(0.3, MunRadius, MunDistance, 1.0, 2.0);
            Assert.AreEqual(near / 4.0, far, 1e-15);
        }
    }
}
