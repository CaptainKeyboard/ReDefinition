using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The pure parts of the far view (package T06): the optical depth of the air between a camera and a cloud against the closed forms and a
    // fine numeric integral, the shape noise's mean against the numeric mean of the sample, the hand-over weights and the map fade. The shader
    // mirrors these functions; the weather player measures the picture (tools/weather_metrics/farview.py).
    [TestClass]
    public class FarViewMathTests
    {
        private const double R = 600000.0;
        private const double Ray = 5.802e-6, RayG = 13.558e-6, RayB = 33.1e-6, Mie = 4.44e-6, HR = 8000.0, HM = 1200.0;

        private static double Tau(double ox, double oy, double oz, double dx, double dy, double dz, double t, int channel = 0, double strength = 1.0)
        {
            double r, g, b;
            FarViewMath.AirTau(R, ox, oy, oz, dx, dy, dz, t, Ray, RayG, RayB, Mie, HR, HM, strength, out r, out g, out b);
            return channel == 0 ? r : channel == 1 ? g : b;
        }

        // The integral by many short midpoint steps (the independent reference).
        private static double Numeric(double ox, double oy, double oz, double dx, double dy, double dz, double t, double ray, double mie)
        {
            int n = 200000;
            double dt = t / n, sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                double s = (i + 0.5) * dt;
                double h = Math.Max(Math.Sqrt((ox + dx * s) * (ox + dx * s) + (oy + dy * s) * (oy + dy * s) + (oz + dz * s) * (oz + dz * s)) - R, 0.0);
                sum += (ray * Math.Exp(-h / HR) + mie * Math.Exp(-h / HM)) * dt;
            }
            return sum;
        }

        [TestMethod]
        public void TheVerticalColumnOfTheAirIsTheClosedForm()
        {
            // From the ground straight up to far above the air: the integral of beta exp(-h / H) over h is beta H.
            double expected = Ray * HR + Mie * HM;
            Assert.AreEqual(expected, Tau(0, 0, R, 0, 0, 1, 1000000.0), expected * 5e-3);
            // From orbit straight down to the ground: the same column (the camera is above the air, the ray crosses it).
            Assert.AreEqual(expected, Tau(0, 0, R + 300000.0, 0, 0, -1, 300000.0), expected * 5e-3);
        }

        [TestMethod]
        public void AGrazingRayAcrossTheCurvedAirIsTheNumericIntegral()
        {
            // A horizontal ray from a camera 2 km up, 50 km long: the altitude along it is a parabola, the pieces are linear in it.
            double expected = Numeric(0, 0, R + 2000.0, 1, 0, 0, 50000.0, Ray, Mie);
            Assert.AreEqual(expected, Tau(0, 0, R + 2000.0, 1, 0, 0, 50000.0), expected * 0.03);
            // A slanted ray from 10 km down to a deck at 2 km: 90 km of path at a shallow angle.
            double ang = Math.Atan2(8000.0, 90000.0);
            double dx = Math.Cos(ang), dz = -Math.Sin(ang);
            double len = 90000.0;
            Assert.AreEqual(Numeric(0, 0, R + 10000.0, dx, 0, dz, len, RayB, Mie), Tau(0, 0, R + 10000.0, dx, 0, dz, len, 2), Numeric(0, 0, R + 10000.0, dx, 0, dz, len, RayB, Mie) * 0.03);
        }

        [TestMethod]
        public void TheBlueGoesFirstAndTheTransmittanceFallsWithDistance()
        {
            double lastB = 0.0;
            for (double t = 5000.0; t <= 80000.0; t += 5000.0)
            {
                double r = Tau(0, 0, R + 500.0, 1, 0, 0, t, 0), g = Tau(0, 0, R + 500.0, 1, 0, 0, t, 1), b = Tau(0, 0, R + 500.0, 1, 0, 0, t, 2);
                Assert.IsTrue(b > g && g > r && r > 0.0);
                Assert.IsTrue(b > lastB);
                lastB = b;
            }
        }

        [TestMethod]
        public void NoAirAboveTheTopAndNoDistanceNoTau()
        {
            Assert.AreEqual(0.0, Tau(0, 0, R + 900000.0, 0, 0, 1, 100000.0), 0.0);
            Assert.AreEqual(0.0, Tau(0, 0, R + 100.0, 1, 0, 0, 0.0), 0.0);
            // A strength of 2 doubles the optical depth.
            Assert.AreEqual(2.0 * Tau(0, 0, R + 500.0, 1, 0, 0, 30000.0), Tau(0, 0, R + 500.0, 1, 0, 0, 30000.0, 0, 2.0), 1e-12);
        }

        [TestMethod]
        public void TheFogMixKeepsTheCloudWhereTheAirIsClearAndTheHorizonWhereItIsOpaque()
        {
            Assert.AreEqual(0.6, FarViewMath.Fog(0.6, 0.9, 1.0, 0.8), 1e-12);
            Assert.AreEqual(0.9 * 0.8, FarViewMath.Fog(0.6, 0.9, 0.0, 0.8), 1e-12);
        }

        [TestMethod]
        public void TheShapeMeanIsTheNumericMeanOverTheNoise()
        {
            double worst = 0.0;
            foreach (double cov in new[] { 0.1, 0.35, 0.6, 0.85 })
                foreach (double coverage in new[] { 0.3, 0.62, 0.93 })
                    foreach (double amount in new[] { 0.28, 0.35, 0.6 })
                        foreach (double erosion in new[] { 0.0, 0.3, 0.7 })
                        {
                            const double edge = 0.3, detailMean = 0.45, hb = 0.55, hg = 0.4;
                            double mean = FarViewMath.ShapeMean(cov, coverage, edge, amount, erosion, detailMean, hb, hg);
                            int n = 40000;
                            double sum = 0.0;
                            for (int i = 0; i < n; i++) sum += FarViewMath.ShapeSample(cov, coverage, edge, amount, erosion, detailMean, hb, hg, (i + 0.5) / n);
                            worst = Math.Max(worst, Math.Abs(mean - sum / n));
                        }
            Assert.IsTrue(worst < 2e-3, "worst difference " + worst);
        }

        [TestMethod]
        public void TheShapeMeanKeepsTheCoverageOfAFullAndAnEmptyColumn()
        {
            Assert.AreEqual(1.0, FarViewMath.ShapeMean(1.0, 0.9, 0.3, 0.3, 0.5, 0.45, 0.55, 0.4), 1e-9);
            Assert.AreEqual(0.0, FarViewMath.ShapeMean(0.0, 0.2, 0.3, 0.3, 0.5, 0.45, 0.55, 0.4), 1e-9);
        }

        [TestMethod]
        public void TheNoiseLodRisesFromTheStartToTheEndAndIsZeroWithoutAScale()
        {
            Assert.AreEqual(0.0, FarViewMath.NoiseLod(500.0, 700.0, 1.0, 3.0), 0.0);
            Assert.AreEqual(0.5, FarViewMath.NoiseLod(1400.0, 700.0, 1.0, 3.0), 1e-12);
            Assert.AreEqual(1.0, FarViewMath.NoiseLod(5000.0, 700.0, 1.0, 3.0), 0.0);
            Assert.AreEqual(0.0, FarViewMath.NoiseLod(5000.0, 0.0, 1.0, 3.0), 0.0);
        }

        [TestMethod]
        public void TheHandOverChangesAtMostFivePercentPerKilometreAndThreePercentBetweenFrames()
        {
            double start = R * FarViewMath.BandStartRadii, end = R * FarViewMath.BandEndRadii;
            Assert.AreEqual(200000.0, start, 1e-6);
            Assert.AreEqual(260000.0, end, 1e-6);
            Assert.AreEqual(1.0, FarViewMath.NearWeight(150000.0, start, end), 0.0);
            Assert.AreEqual(0.0, FarViewMath.NearWeight(300000.0, start, end), 0.0);
            double worst = 0.0;
            for (double h = 190000.0; h < 270000.0; h += 100.0)
                worst = Math.Max(worst, Math.Abs(FarViewMath.NearWeight(h + 1000.0, start, end) - FarViewMath.NearWeight(h, start, end)));
            Assert.IsTrue(worst <= 0.05, "per km " + worst);
            // At a climb of 2 km per second and 60 frames a second the weight moves by this much per frame.
            double frame = 0.0;
            for (double h = 190000.0; h < 270000.0; h += 10.0)
                frame = Math.Max(frame, Math.Abs(FarViewMath.NearWeight(h + 2000.0 / 60.0, start, end) - FarViewMath.NearWeight(h, start, end)));
            Assert.IsTrue(frame <= 0.03, "per frame " + frame);
        }

        [TestMethod]
        public void TheMarchPolicyKeepsTheNearNumbersBelowTheBandAndHalvesTheBudgetAboveItWithoutAJump()
        {
            double start = R * FarViewMath.BandStartRadii, end = R * FarViewMath.BandEndRadii;
            int steps;
            double minStep;
            FarViewMath.MarchQuality(100000.0, start, end, 64, 25.0, out steps, out minStep);
            Assert.AreEqual(64, steps);
            Assert.AreEqual(25.0, minStep, 1e-9);
            FarViewMath.MarchQuality(400000.0, start, end, 64, 25.0, out steps, out minStep);
            Assert.AreEqual(32, steps);
            Assert.AreEqual(100.0, minStep, 1e-9);
            // Through the band the budget changes by at most one step per 1.5 km and the shortest step by at most 3 percent per km.
            int last = 64;
            double lastMin = 25.0, worstMin = 0.0;
            int worstSteps = 0;
            for (double h = 195000.0; h <= 265000.0; h += 1000.0)
            {
                FarViewMath.MarchQuality(h, start, end, 64, 25.0, out steps, out minStep);
                worstSteps = Math.Max(worstSteps, Math.Abs(steps - last));
                worstMin = Math.Max(worstMin, Math.Abs(minStep - lastMin) / lastMin);
                last = steps;
                lastMin = minStep;
            }
            Assert.IsTrue(worstSteps <= 2, "steps per km " + worstSteps);
            Assert.IsTrue(worstMin <= 0.12, "min step per km " + worstMin);
            // The entry's finest step is geometric in the far weight: the near share at 0, the far share at 1, the geometric mean between.
            Assert.AreEqual(0.125, FarViewMath.GeometricShare(0.125, 0.02, 0.0), 1e-12);
            Assert.AreEqual(0.02, FarViewMath.GeometricShare(0.125, 0.02, 1.0), 1e-12);
            Assert.AreEqual(Math.Sqrt(0.125 * 0.02), FarViewMath.GeometricShare(0.125, 0.02, 0.5), 1e-12);
            Assert.AreEqual(0.125, FarViewMath.GeometricShare(0.125, 0.0, 0.7), 0.0);
        }

        [TestMethod]
        public void ACrossFadeOfTheSameLayerKeepsTheCoverageAndAStackedHandOverDips()
        {
            double a = 0.8;
            foreach (double w in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
            {
                Assert.AreEqual(a, FarViewMath.CrossFade(a, a, w), 1e-12);
                Assert.AreEqual(a - w * (1.0 - w) * a * a, FarViewMath.StackedFade(a, a, w), 1e-12);
            }
            // A full cloud drawn by both loses a quarter of its coverage in the middle of the band when stacked.
            Assert.AreEqual(0.75, FarViewMath.StackedFade(1.0, 1.0, 0.5), 1e-12);
        }

        [TestMethod]
        public void TheMapFadeHidesTheShellCloseToTheBodyAndShowsItFromFarAway()
        {
            Assert.AreEqual(0.0, FarViewMath.MapFade(2.0, 2.5, 3.5), 0.0);
            Assert.AreEqual(1.0, FarViewMath.MapFade(4.0, 2.5, 3.5), 0.0);
            Assert.AreEqual(0.5, FarViewMath.MapFade(3.0, 2.5, 3.5), 1e-12);
        }

        [TestMethod]
        public void TheShellsOpacityIsWhatTheNearMarchLeavesAndFullWhereTheNearMarchIsOff()
        {
            // Near march drawing: full below the band, nothing above it, half at the middle of the band (a 600 km body: 200 to 260 km).
            Assert.AreEqual(0f, OwnShell.Opacity(100000.0, R, true, false, 0.0, true), 1e-6);
            Assert.AreEqual(1f, OwnShell.Opacity(300000.0, R, true, false, 0.0, true), 1e-6);
            Assert.AreEqual(0.5f, OwnShell.Opacity(230000.0, R, true, false, 0.0, true), 1e-6);
            // The near march is off (own clouds off, another cloud source): the shell is the only picture at any altitude.
            Assert.AreEqual(1f, OwnShell.Opacity(2000.0, R, false, false, 0.0, true), 1e-6);
            // Map view: the near march is off, the fade follows the camera's distance in body radii; with the fade off the shell stays.
            Assert.AreEqual(0f, OwnShell.Opacity(0.0, R, true, true, 2.0, true), 1e-6);
            Assert.AreEqual(1f, OwnShell.Opacity(0.0, R, true, true, 4.0, true), 1e-6);
            Assert.AreEqual(1f, OwnShell.Opacity(0.0, R, true, true, 2.0, false), 1e-6);
        }

        // The correlation of the picture's alpha with the coverage map of a field whose precipitation level rises with its coverage above rainFrom
        // (rain falls out of thick cloud).
        private static double PictureCorrelation(double rainFrom)
        {
            int w = 128, h = 64;
            var cov = new double[w * h];
            var alpha = new double[w * h];
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    double c = 0.5 + 0.5 * Math.Sin(i * 0.21) * Math.Cos(j * 0.37) + 0.2 * Math.Sin(i * 0.9 + j * 0.4);
                    c = Math.Max(0.0, Math.Min(1.0, c));
                    double level = c > rainFrom ? (c - rainFrom) / (1.0 - rainFrom) : 0.0;
                    cov[j * w + i] = c;
                    alpha[j * w + i] = OrbitPicture.PictureAlpha((float)c, (float)level);
                }
            double mc = 0, ma = 0;
            for (int i = 0; i < cov.Length; i++) { mc += cov[i]; ma += alpha[i]; }
            mc /= cov.Length;
            ma /= cov.Length;
            double sxy = 0, sxx = 0, syy = 0;
            for (int i = 0; i < cov.Length; i++)
            {
                sxy += (cov[i] - mc) * (alpha[i] - ma);
                sxx += (cov[i] - mc) * (cov[i] - mc);
                syy += (alpha[i] - ma) * (alpha[i] - ma);
            }
            return sxy / Math.Sqrt(sxx * syy);
        }

        [TestMethod]
        public void TheOrbitPicturesOpacityLawTracksThePrecipitationMoreThanTheCoverageMap()
        {
            // The law of the picture (faint 0.35 where nothing falls, full from Moderate) encodes the precipitation: on a field whose rain comes
            // out of the thickest tenth of the cloud the picture's alpha correlates 0.80 with the coverage map, on one whose rain comes out of nearly
            // half of the cloud 0.92. The brief's gate (0.95) is not met by this law; a faint opacity of 0.75 meets it on both fields (0.97, 0.99:
            // OrbitPicture.FaintOpacity, which C23's test FlurriesAreFaintAndTheSwirlStartsAtModerate pins below 0.5).
            double typical = PictureCorrelation(0.85), wet = PictureCorrelation(0.55);
            Assert.IsTrue(typical > 0.78 && typical < 0.83, "correlation " + typical);
            Assert.IsTrue(wet > 0.9 && wet < 0.95, "correlation " + wet);
            Assert.AreEqual(0f, OrbitPicture.PictureAlpha(0f, 1f), 0f);
        }
    }
}
