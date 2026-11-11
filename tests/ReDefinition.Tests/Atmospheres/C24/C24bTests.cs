using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Core;
using ReDefinition.Modules;
using ReDefinition.Proxy;
using ReDefinition.Raytracing;
using ReDefinition.Upscaler;

namespace ReDefinition.Tests.Atmospheres.C24
{
    // Package C24b: the fog in the frame packet, the three switches of the weather on the rays, and the calibration of the optical depth
    // estimate. What needs the GPU is the harness's (tests/DxgiProxy/RaytracingHarness.cpp, "fog").
    [TestClass]
    public class C24bTests
    {
        // ---- the fog in the packet ----

        [TestMethod]
        public void TheFogSitsBeforeTheInputsInTheFramePacket()
        {
            Type packet = typeof(RtNative.FramePacket);
            Assert.AreEqual(928, Marshal.OffsetOf(packet, "Fog").ToInt32());
            Assert.AreEqual(944, Marshal.OffsetOf(packet, "Inputs").ToInt32());
            Assert.AreEqual(1072, Marshal.SizeOf(packet));
            Assert.AreEqual(4, RtNative.NewFramePacket().Fog.Length);
        }

        [TestMethod]
        public void TheFogDensityIsZeroBelowTheFloorAndForBadValues()
        {
            Assert.AreEqual(0f, RtWeatherMath.FogDensityFor(0.1f, 0.01f), "below the level floor");
            Assert.AreEqual(0f, RtWeatherMath.FogDensityFor(0.5f, 0f));
            Assert.AreEqual(0f, RtWeatherMath.FogDensityFor(0.5f, -1f));
            Assert.AreEqual(0f, RtWeatherMath.FogDensityFor(0.5f, float.NaN));
            Assert.AreEqual(0f, RtWeatherMath.FogDensityFor(float.NaN, 0.01f));
            Assert.AreEqual(0.01f, RtWeatherMath.FogDensityFor(0.2f, 0.01f), "from the floor");
            Assert.AreEqual(RtWeatherMath.MaxFogDensity, RtWeatherMath.FogDensityFor(1f, 50f));
        }

        [TestMethod]
        public void TheFogKeepsExpOfMinusTheSquareOfDistanceTimesDensity()
        {
            Assert.AreEqual(1.0, RtWeatherMath.FogKeep(500.0, 0.0));
            Assert.AreEqual(1.0, RtWeatherMath.FogKeep(double.NaN, 0.01));
            Assert.AreEqual(Math.Exp(-1.0), RtWeatherMath.FogKeep(100.0, 0.01), 1e-12);
            Assert.AreEqual(Math.Exp(-4.0), RtWeatherMath.FogKeep(200.0, 0.01), 1e-12);
            // The density the weather writes for a visibility leaves 5 % of the contrast there: sqrt(3) / visibility.
            double density = 1.7320508 / 1000.0;
            Assert.AreEqual(0.0498, RtWeatherMath.FogKeep(1000.0, density), 0.001);
            Assert.AreEqual(0.0, RtWeatherMath.FogKeep(1.0e7, 0.001), 1e-12, "a ray that met nothing sees the fog's colour");
            Assert.IsTrue(RtWeatherMath.FogKeep(10.0, 0.01) > RtWeatherMath.FogKeep(20.0, 0.01), "the farther, the less");
        }

        // ---- the switches ----

        [TestMethod]
        public void TheQualityCostsAreOffAndTheFogIsOnByDefault()
        {
            var settings = new OwnSettings();
            Assert.IsTrue(settings.RtWeatherFog);
            Assert.IsFalse(settings.RtWeatherReachCap, "a quality cost, off until the owner agrees");
            Assert.IsFalse(settings.RtWeatherFewerBounces, "a quality cost, off until the owner agrees");
        }

        [TestMethod]
        public void TheSwitchesAreSettingsWithTheKeysTheMatrixUses()
        {
            var settings = new OwnSettings();
            foreach (string key in new[] { "rtWeatherFog", "rtWeatherReachCap", "rtWeatherFewerBounces" })
            {
                bool value;
                Assert.IsTrue(settings.SetValue(key, "True"), key);
                Assert.IsTrue(bool.TryParse(settings.ValueOf(key), out value) && value, key);
                Assert.IsTrue(settings.SetValue(key, "False"), key);
            }
        }

        // ---- the calibration ----

        private static List<CloudDensityCalibration.Point> PointsFor(float k, params float[] shapes)
        {
            var points = new List<CloudDensityCalibration.Point>();
            float elevation = 1.0f;
            foreach (float shape in shapes)
                points.Add(new CloudDensityCalibration.Point
                {
                    Shape = shape,
                    MeasuredTransmission = SunTransmission.From(k * shape, elevation),
                    SunElevationRad = elevation,
                });
            return points;
        }

        [TestMethod]
        public void TheFitFindsTheFactorOfExactData()
        {
            CloudDensityCalibration.Fit fit;
            Assert.IsTrue(CloudDensityCalibration.TryFit(PointsFor(1.7f, 0.1f, 0.25f, 0.5f, 0.75f, 1f), out fit));
            Assert.AreEqual(1.7f, fit.TauAtFullCoverage, 1e-3f);
            Assert.AreEqual(0f, fit.WorstRelativeError, 1e-3f);
            Assert.AreEqual(5, fit.Points);
            Assert.IsTrue(fit.Monotone);
            Assert.IsTrue(fit.WithinBound);
        }

        [TestMethod]
        public void TheFitFlagsATableThatMissesTheBoundOrIsNotMonotone()
        {
            // The depths fall once the shape rises: not monotone, and a poor fit.
            var points = PointsFor(2f, 0.2f, 0.4f, 0.6f, 0.8f, 1f);
            var spoilt = points[3];
            spoilt.MeasuredTransmission = 0.9f;
            points[3] = spoilt;
            CloudDensityCalibration.Fit fit;
            Assert.IsTrue(CloudDensityCalibration.TryFit(points, out fit));
            Assert.IsFalse(fit.Monotone);
            Assert.IsFalse(fit.WithinBound);
            // Four points are too few for the row.
            Assert.IsTrue(CloudDensityCalibration.TryFit(PointsFor(2f, 0.2f, 0.4f, 0.6f, 0.8f), out fit));
            Assert.IsFalse(fit.WithinBound, "five coverage values are required");
        }

        [TestMethod]
        public void TheFitIgnoresUnusablePointsAndNeedsTwo()
        {
            CloudDensityCalibration.Fit fit;
            Assert.IsFalse(CloudDensityCalibration.TryFit(null, out fit));
            Assert.IsFalse(CloudDensityCalibration.TryFit(PointsFor(2f, 0.5f), out fit));
            var points = PointsFor(2f, 0.3f, 0.6f);
            points.Add(new CloudDensityCalibration.Point { Shape = 0f, MeasuredTransmission = 0.5f, SunElevationRad = 1f });
            points.Add(new CloudDensityCalibration.Point { Shape = 0.5f, MeasuredTransmission = 1f, SunElevationRad = 1f });
            points.Add(new CloudDensityCalibration.Point { Shape = 0.5f, MeasuredTransmission = 0f, SunElevationRad = 1f });
            Assert.IsTrue(CloudDensityCalibration.TryFit(points, out fit));
            Assert.AreEqual(2, fit.Points);
            Assert.AreEqual(2f, fit.TauAtFullCoverage, 1e-3f);
        }

        [TestMethod]
        public void TheTypeDensityCountsAsOneUntilTheReferenceIsSet()
        {
            try
            {
                CloudDensityCalibration.Reset();
                Assert.AreEqual(1f, CloudDensityCalibration.TypeDensityFactor(0.012f));
                CloudDensityCalibration.Apply(4f, 0.012f);
                Assert.AreEqual(4f, CloudDensityEstimate.TauAtFullCoverage);
                Assert.AreEqual(0.5f, CloudDensityCalibration.TypeDensityFactor(0.006f), 1e-6f);
                Assert.AreEqual(1f, CloudDensityCalibration.TypeDensityFactor(float.NaN), "a bad density counts as 1");
                CloudDensityCalibration.Apply(-1f, -1f);
                Assert.AreEqual(CloudDensityCalibration.ProvisionalTauAtFullCoverage, CloudDensityEstimate.TauAtFullCoverage);
                Assert.AreEqual(0f, CloudDensityCalibration.TypeDensityReference);
            }
            finally
            {
                CloudDensityCalibration.Reset();
            }
        }

        [TestMethod]
        public void TheEstimateTakesTheDensityOfEachSlice()
        {
            var deck = new CloudDensityEstimate.Deck { BottomM = 0.0, TopM = 8000.0, Density = 1f, TimeFade = 1f };
            float uniform = CloudDensityEstimate.VerticalOpticalDepthBySlice(deck, 0.0,
                h => new CloudDensityEstimate.Slice { Coverage = 1f, Density = 1f });
            float lower = CloudDensityEstimate.VerticalOpticalDepthBySlice(deck, 0.0,
                h => new CloudDensityEstimate.Slice { Coverage = 1f, Density = h < 4000.0 ? 1f : 0f });
            Assert.AreEqual(CloudDensityEstimate.TauAtFullCoverage, uniform, 1e-4f);
            Assert.AreEqual(uniform / 2f, lower, 1e-4f, "the lower half at density 1, the upper at 0");
            Assert.AreEqual(2f * uniform, CloudDensityEstimate.VerticalOpticalDepthBySlice(deck, 0.0,
                h => new CloudDensityEstimate.Slice { Coverage = 1f, Density = 2f }), 1e-4f);
        }

        [TestMethod]
        public void TheShapeIsTheEstimateAtFactorOne()
        {
            var deck = new CloudDensityEstimate.Deck { BottomM = 1000.0, TopM = 5000.0, Density = 1f, TimeFade = 0.5f };
            Func<double, CloudDensityEstimate.Slice> half = h => new CloudDensityEstimate.Slice { Coverage = 0.5f, Density = 1f };
            Assert.AreEqual(0.25f, CloudDensityEstimate.Shape(deck, 0.0, half), 1e-5f);
            Assert.AreEqual(CloudDensityEstimate.TauAtFullCoverage * 0.25f, CloudDensityEstimate.VerticalOpticalDepthBySlice(deck, 0.0, half), 1e-5f);
        }
    }
}
