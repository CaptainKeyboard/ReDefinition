using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Api.Internal;
using ReDefinition.Core;
using ReDefinition.Proxy;
using ReDefinition.Raytracing;
using ReDefinition.Upscaler;

namespace ReDefinition.Tests.Atmospheres.C24
{
    // Package C24: the depth hook, the history reset on a weather cut, the caps, the cloud optical depth estimate and the raytracing
    // inputs that need no GPU.
    [TestClass]
    public class C24Tests
    {
        // ---- the depth hook ----

        [TestMethod]
        public void TheDepthHookRegistersOnceAndUnregisters()
        {
            Action<UnityEngine.Rendering.CommandBuffer, UnityEngine.RenderTexture, UnityEngine.RenderTexture, UnityEngine.Camera> handler =
                (buffer, image, depth, camera) => { };
            Assert.IsFalse(SharedFrame.AfterUpscalingDepthHooks.Any);
            Api.Hooks.RegisterAfterUpscalingWithDepth(handler);
            Api.Hooks.RegisterAfterUpscalingWithDepth(handler);
            Assert.AreEqual(1, SharedFrame.AfterUpscalingDepthHooks.Count);
            Api.Hooks.UnregisterAfterUpscalingWithDepth(handler);
            Assert.IsFalse(SharedFrame.AfterUpscalingDepthHooks.Any);
        }

        [TestMethod]
        public void TheInterfaceVersionIsThree()
        {
            Assert.AreEqual(3, Api.ApiInfo.Version);
        }

        // ---- the history reset ----

        [TestMethod]
        public void ALevelJumpBeyondTheStepIsACut()
        {
            var detector = new WeatherCutDetector();
            Assert.IsFalse(detector.Observe(true, "Kerbin", 0.1f), "the first frame is no cut");
            Assert.IsFalse(detector.Observe(true, "Kerbin", 0.3f), "a step of 0.2");
            Assert.IsTrue(detector.Observe(true, "Kerbin", 0.7f), "a step of 0.4");
            Assert.IsFalse(detector.Observe(true, "Kerbin", 0.7f));
            Assert.IsTrue(detector.Observe(true, "Kerbin", 0.1f), "a fall of 0.6 is a cut as well");
        }

        [TestMethod]
        public void ABodyChangeIsACutAndAnInactiveViewForgets()
        {
            var detector = new WeatherCutDetector();
            Assert.IsFalse(detector.Observe(true, "Kerbin", 0.2f));
            Assert.IsTrue(detector.Observe(true, "Laythe", 0.2f));
            Assert.IsFalse(detector.Observe(false, "Laythe", 0.9f), "no weather, no cut");
            Assert.IsFalse(detector.Observe(true, "Duna", 0.9f), "the first active frame after a gap is no cut by itself");
            Assert.IsFalse(detector.Observe(true, "Duna", float.NaN));
        }

        // ---- the caps ----

        [TestMethod]
        public void TheCapsFollowTheRigAndTheProfile()
        {
            var caps = new WeatherRenderCaps();
            WeatherCapsWriter.Fill(caps, true, UpscalerBackend.Dlss, true, "High");
            Assert.AreEqual(UpscalerKind.Dlss, caps.Upscaler);
            Assert.IsTrue(caps.FrameGenerationOn);
            Assert.IsTrue(caps.HostApiAvailable);
            Assert.AreEqual(QualityTier.High, caps.Tier);

            WeatherCapsWriter.Fill(caps, false, UpscalerBackend.Dlss, false, null);
            Assert.AreEqual(UpscalerKind.None, caps.Upscaler, "no upscaler while it does not run");
            Assert.AreEqual(QualityTier.Auto, caps.Tier);

            WeatherCapsWriter.Fill(caps, true, UpscalerBackend.Amd, false, "ultra");
            Assert.AreEqual(UpscalerKind.Fsr, caps.Upscaler);
            Assert.AreEqual(QualityTier.Ultra, caps.Tier);
            WeatherCapsWriter.Fill(caps, true, UpscalerBackend.Xess, false, "low");
            Assert.AreEqual(UpscalerKind.Xess, caps.Upscaler);
            Assert.AreEqual(QualityTier.Low, WeatherCapsWriter.TierOf("LOW"));
            Assert.AreEqual(QualityTier.Auto, WeatherCapsWriter.TierOf("nonsense"));
        }

        // ---- the optical depth estimate ----

        private static CloudDensityEstimate.Deck Deck()
        {
            return new CloudDensityEstimate.Deck { BottomM = 2000.0, TopM = 6000.0, Density = 1f, TimeFade = 1f };
        }

        [TestMethod]
        public void TheEstimateIsZeroAtCoverageZeroAndWithoutADeck()
        {
            Assert.AreEqual(0f, CloudDensityEstimate.VerticalOpticalDepth(Deck(), 0.0, h => 0f));
            Assert.AreEqual(0f, CloudDensityEstimate.VerticalOpticalDepth(Deck(), 0.0, null));
            Assert.AreEqual(0f, CloudDensityEstimate.VerticalOpticalDepth(Deck(), 7000.0, h => 1f), "the observer above the deck");
            var empty = Deck();
            empty.TopM = empty.BottomM;
            Assert.AreEqual(0f, CloudDensityEstimate.VerticalOpticalDepth(empty, 0.0, h => 1f));
            var dark = Deck();
            dark.TimeFade = 0f;
            Assert.AreEqual(0f, CloudDensityEstimate.VerticalOpticalDepth(dark, 0.0, h => 1f));
            Assert.AreEqual(0f, CloudDensityEstimate.VerticalOpticalDepth(Deck(), 0.0, h => float.NaN));
        }

        [TestMethod]
        public void TheEstimateRisesWithCoverageAndIsBounded()
        {
            float previous = -1f;
            foreach (float coverage in new[] { 0f, 0.1f, 0.25f, 0.5f, 0.75f, 1f })
            {
                float tau = CloudDensityEstimate.VerticalOpticalDepth(Deck(), 0.0, h => coverage);
                Assert.IsTrue(tau > previous || (coverage == 0f && tau == 0f), "coverage " + coverage + " gave " + tau);
                Assert.IsTrue(tau >= 0f && tau <= CloudDensityEstimate.TauAtFullCoverage + 1e-4f);
                previous = tau;
            }
            Assert.AreEqual(CloudDensityEstimate.TauAtFullCoverage, CloudDensityEstimate.VerticalOpticalDepth(Deck(), 0.0, h => 1f), 1e-4f);
            Assert.AreEqual(CloudDensityEstimate.TauAtFullCoverage, CloudDensityEstimate.VerticalOpticalDepth(Deck(), 0.0, h => 9f), 1e-4f,
                            "a coverage above 1 counts as 1");
        }

        [TestMethod]
        public void TheEstimateFromInsideTheDeckCountsTheCloudAboveOnly()
        {
            float whole = CloudDensityEstimate.VerticalOpticalDepth(Deck(), 0.0, h => 1f);
            float half = CloudDensityEstimate.VerticalOpticalDepth(Deck(), 4000.0, h => 1f);
            Assert.AreEqual(whole / 2f, half, 1e-4f);
            var d = Deck();
            d.TimeFade = 0.5f;
            Assert.AreEqual(whole / 2f, CloudDensityEstimate.VerticalOpticalDepth(d, 0.0, h => 1f), 1e-4f);
        }

        [TestMethod]
        public void TheTransmissionOfTheEstimateFollowsTheSharedDefinition()
        {
            float tau = CloudDensityEstimate.VerticalOpticalDepth(Deck(), 0.0, h => 0.5f);
            float overhead = SunTransmission.From(tau, (float)(Math.PI / 2.0));
            float low = SunTransmission.From(tau, 0.2f);
            Assert.IsTrue(overhead > low && overhead < 1f && low >= 0f);
        }

        // ---- the layer preference and the repack ----

        [TestMethod]
        public void ThePreferredObjectIsTriedFirstAndEveryObjectOnce()
        {
            for (int preferred = -1; preferred < 5; preferred++)
            {
                var seen = new List<int>();
                for (int step = 0; step < 5; step++) seen.Add(CloudLayerPreference.IndexAt(step, preferred));
                seen.Sort();
                CollectionAssert.AreEqual(new List<int> { 0, 1, 2, 3, 4 }, seen, "preferred " + preferred);
                if (preferred >= 0) Assert.AreEqual(preferred, CloudLayerPreference.IndexAt(0, preferred));
            }
        }

        [TestMethod]
        public void ARepaintRepacksAtMostEveryHalfSecond()
        {
            Assert.IsFalse(CloudLayerPreference.RepaintDue(5, 5, 10f), "same version");
            Assert.IsFalse(CloudLayerPreference.RepaintDue(6, 5, 0.2f), "too soon");
            Assert.IsTrue(CloudLayerPreference.RepaintDue(6, 5, 0.5f));
        }

        // ---- the rays' inputs ----

        [TestMethod]
        public void TheRaysReachIsCappedInFogOnly()
        {
            const float full = RtPacketBuilder.RayReach;
            Assert.AreEqual(full, RtWeatherMath.ReachFor(0.1f, 50f, full), "below the level floor the weather changes nothing");
            Assert.AreEqual(full, RtWeatherMath.ReachFor(0.5f, 1000000f, full), "clear air");
            Assert.AreEqual(3000f, RtWeatherMath.ReachFor(0.5f, 1000f, full));
            Assert.AreEqual(RtWeatherMath.MinReachM, RtWeatherMath.ReachFor(0.9f, 10f, full), "never under the near field");
            Assert.AreEqual(full, RtWeatherMath.ReachFor(0.5f, 0f, full), "no usable visibility");
        }

        [TestMethod]
        public void HeavyWeatherTakesOneBounceFewerButNeverBelowOne()
        {
            Assert.AreEqual(2f, RtWeatherMath.BouncesFor(0.7f, 2f));
            Assert.AreEqual(1f, RtWeatherMath.BouncesFor(0.71f, 2f));
            Assert.AreEqual(1f, RtWeatherMath.BouncesFor(1f, 1f));
            Assert.AreEqual(3f, RtWeatherMath.BouncesFor(1f, 4f));
        }

        [TestMethod]
        public void TheSunShareCountsWithHalfWeight()
        {
            Assert.AreEqual(1f, RtWeatherMath.SunScale(1f));
            Assert.AreEqual(0.5f, RtWeatherMath.SunScale(0f), 1e-6f);
            Assert.AreEqual(0.75f, RtWeatherMath.SunScale(0.5f), 1e-6f);
            Assert.AreEqual(1f, RtWeatherMath.SunScale(float.NaN));
            Assert.AreEqual(1f, RtWeatherMath.SunScale(3f));
        }
    }
}
