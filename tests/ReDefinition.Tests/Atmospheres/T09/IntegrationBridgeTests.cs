using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The pure parts of the integration of the own cloud pass (package T09): the air of a body, the in-scatter that follows the sky, the layer
    // stack built from the specs, the decisions of the module and the settings it applies. The module itself needs the game; its rows are in
    // tests/series/rows/T09.json.
    [TestClass]
    public class IntegrationBridgeTests
    {
        // --- The air ---------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void TheAirOfEarthGivesTheEarthCoefficientsAndAScaleHeightFromTheTwoPressures()
        {
            // 101325 Pa at 288.15 K, air; 26436 Pa at 10 km (the standard atmosphere): the scale height is 10 km / ln(3.833) = 7.44 km.
            CloudBridgeAir.Air a = CloudBridgeAir.From(101325.0, 288.15, 0.0289644, 26436.0, 10000.0);
            Assert.IsTrue(a.Valid);
            Assert.AreEqual(5.802e-6f, a.RayleighPerM.x, 0.02f * 5.802e-6f);
            Assert.AreEqual(33.1e-6f, a.RayleighPerM.z, 0.02f * 33.1e-6f);
            Assert.AreEqual(4.44e-6f, a.MiePerM, 0.02f * 4.44e-6f);
            Assert.AreEqual(7440f, a.RayleighHeightM, 80f);
            Assert.AreEqual(a.RayleighHeightM * CloudBridgeAir.MieToRayleighHeight, a.MieHeightM, 1f);
        }

        [TestMethod]
        public void ADenserOrThinnerAirScalesTheCoefficientsWithItsDensity()
        {
            CloudBridgeAir.Air earth = CloudBridgeAir.From(101325.0, 288.15, 0.0289644, 0.0, 0.0);
            CloudBridgeAir.Air thin = CloudBridgeAir.From(50662.5, 288.15, 0.0289644, 0.0, 0.0);
            Assert.AreEqual(0.5f, thin.RayleighPerM.z / earth.RayleighPerM.z, 0.001f);
            Assert.AreEqual(0.5f, thin.MiePerM / earth.MiePerM, 0.001f);
            // Without a second pressure the scale height is that of the isothermal column, R T / (M g).
            Assert.AreEqual(8.314462618 * 288.15 / (0.0289644 * 9.81), earth.RayleighHeightM, 1.0);
        }

        [TestMethod]
        public void DataThatCannotDescribeAnAirGiveAnInvalidResultAndLeaveTheSettingsAlone()
        {
            CloudBridgeAir.Air none = CloudBridgeAir.From(0.0, 288.0, 0.029, 0.0, 0.0);
            Assert.IsFalse(none.Valid);
            var far = new FarViewSettings();
            Vector3 before = far.RayleighPerM;
            CloudBridgeAir.Apply(far, none);
            Assert.AreEqual(before, far.RayleighPerM);
            CloudBridgeAir.Air earth = CloudBridgeAir.From(101325.0, 288.15, 0.0289644, 26436.0, 10000.0);
            CloudBridgeAir.Apply(far, earth);
            Assert.AreEqual(earth.RayleighHeightM, far.RayleighHeightM);
            Assert.IsFalse(far.Haze, "the switch stays as the caller set it");
        }

        [TestMethod]
        public void TheInscatterOfTheHarnessSunKeepsTheBrightnessOfTheOldConstantAndIsBluer()
        {
            Color c = CloudBridgeAir.Inscatter(new Color(3.0f, 2.9f, 2.7f), Color.black, Color.black, CloudBridgeAir.EarthRayleighPerM);
            // The old constant (0.62, 0.68, 0.76) was a guess; the model keeps its brightness (mean within 15 %) and is bluer, the way the
            // air's Rayleigh coefficient is.
            float mean = (c.r + c.g + c.b) / 3f;
            Assert.AreEqual((0.62f + 0.68f + 0.76f) / 3f, mean, 0.15f * 0.687f);
            Assert.IsTrue(c.b > c.g && c.g > c.r);
        }

        [TestMethod]
        public void TheInscatterFollowsTheSkyAtSunsetAndAtNight()
        {
            Vector3 air = CloudBridgeAir.EarthRayleighPerM;
            Color day = CloudBridgeAir.Inscatter(new Color(3.0f, 2.9f, 2.7f), Color.black, Color.black, air);
            Color sunset = CloudBridgeAir.Inscatter(new Color(2.6f, 1.0f, 0.35f), Color.black, Color.black, air);
            Assert.IsTrue(sunset.r / sunset.b > 2f * day.r / day.b, "a reddened sun gives a reddened haze");
            Assert.IsTrue(sunset.b < day.b);
            // Night: no sun, the scene's own light is the floor; with the moon added the haze is brighter than the floor alone.
            var ambient = new Color(0.01f, 0.012f, 0.02f);
            Color night = CloudBridgeAir.Inscatter(Color.black, Color.black, ambient, air);
            Assert.AreEqual(ambient.r, night.r, 1e-6f);
            Assert.AreEqual(ambient.b, night.b, 1e-6f);
            Color moonlit = CloudBridgeAir.Inscatter(Color.black, new Color(0.3f, 0.3f, 0.35f), ambient, air);
            Assert.IsTrue(moonlit.g > night.g);
            Assert.IsTrue(night.b < 0.05f * day.b, "the night haze is not the day's colour");
        }

        // --- The stack --------------------------------------------------------------------------------------------------------

        private static CloudLayerSpec Spec(string name, float inner, float outer, string group = "")
        {
            var type = new CloudTypeSpec("Slab", 0f, 1f, 0.3f, new Curve(new[] { 0f, 1f }, new[] { 1f, 1f }));
            return new CloudLayerSpec(name, inner, outer, 0.01f, LayerKind.Weather, group, 0.5f, 0.15f, 0f, float.PositiveInfinity, 1f, 0.3f, 1000f,
                                      new[] { type });
        }

        private static readonly CloudLayerSpec[] Specs =
        {
            Spec("rain", 0f, 2100f, "weather"), Spec("snow", 0f, 2100f, "weather"), Spec("cumulus", 1700f, 7400f), Spec("cirrus", 9000f, 11000f)
        };

        [TestMethod]
        public void TheStackHoldsOneMemberOfEachGroupLowestFirstWithMapsOnEveryLayer()
        {
            string key;
            LayerStackSource warm = CloudBridgeStack.Build(Specs, 4, g => 0, 1, 600000.0, true, out key);
            Assert.AreEqual(3, warm.LayerCount);
            Assert.AreEqual("rain|cumulus|cirrus|", key);
            for (int i = 0; i < warm.LayerCount; i++)
            {
                Assert.AreEqual(CloudBridgeStack.MapHandle, warm.LayerAt(i).CoverageMapHandle);
                Assert.AreEqual(CloudBridgeStack.MapHandle, warm.LayerAt(i).TypeMapHandle);
            }
            string coldKey;
            LayerStackSource cold = CloudBridgeStack.Build(Specs, 4, g => 1, 1, 600000.0, true, out coldKey);
            Assert.AreEqual("snow|cumulus|cirrus|", coldKey);
            Assert.AreNotEqual(key, coldKey);
        }

        [TestMethod]
        public void TheLayerCapDropsTheHighLayersFirstAndTheKeyNamesWhatIsDrawn()
        {
            string key;
            LayerStackSource two = CloudBridgeStack.Build(Specs, 2, null, 0, 600000.0, true, out key);
            Assert.AreEqual(2, two.LayerCount);
            Assert.AreEqual("rain|cumulus|", key);
            string again;
            CloudBridgeStack.Build(Specs, 2, null, 0, 600000.0, true, out again);
            Assert.AreEqual(key, again, "the same specs give the same key");
        }

        [TestMethod]
        public void TheFreezingPickKeepsItsLastValueInsideTheBand()
        {
            bool cold;
            Assert.AreEqual(0, CloudBridgeStack.PickByTemperature(280.0, false, 1.0, out cold));
            Assert.AreEqual(1, CloudBridgeStack.PickByTemperature(270.0, false, 1.0, out cold));
            Assert.IsTrue(cold);
            // 273.5 K is above freezing but inside the band: a stack that was cold stays cold, one that was warm stays warm.
            CloudBridgeStack.PickByTemperature(273.5, true, 1.0, out cold);
            Assert.IsTrue(cold);
            CloudBridgeStack.PickByTemperature(272.9, false, 1.0, out cold);
            Assert.IsFalse(cold);
            CloudBridgeStack.PickByTemperature(275.0, true, 1.0, out cold);
            Assert.IsFalse(cold);
        }

        [TestMethod]
        public void TheMapsAreAskedForAtTheMiddleOfTheLayersTightInterval()
        {
            string key;
            LayerStackSource s = CloudBridgeStack.Build(new[] { Spec("cumulus", 1700f, 7400f) }, 4, null, 0, 600000.0, true, out key);
            Assert.AreEqual(4550.0, CloudBridgeStack.MidHeightM(s.LayerAt(0)), 1.0);
        }

        // --- The module's decisions -------------------------------------------------------------------------------------------

        [TestMethod]
        public void ThePassDrawsOnlyInAFlightSceneOnABodyWithAirWeatherAndLayersAndNotInTheMapView()
        {
            Assert.IsTrue(OwnCloudPlan.Wanted(true, true, true, true, true, 3, false));
            Assert.IsFalse(OwnCloudPlan.Wanted(false, true, true, true, true, 3, false), "mod off");
            Assert.IsFalse(OwnCloudPlan.Wanted(true, false, true, true, true, 3, false), "not a flight scene");
            Assert.IsFalse(OwnCloudPlan.Wanted(true, true, false, true, true, 3, false), "no atmosphere");
            Assert.IsFalse(OwnCloudPlan.Wanted(true, true, true, false, true, 3, false), "no weather");
            Assert.IsFalse(OwnCloudPlan.Wanted(true, true, true, true, false, 3, false), "own clouds off or another source");
            Assert.IsFalse(OwnCloudPlan.Wanted(true, true, true, true, true, 0, false), "no layers");
            Assert.IsFalse(OwnCloudPlan.Wanted(true, true, true, true, true, 3, true), "map view");
        }

        [TestMethod]
        public void OnlyTheSourceOursDrawsTheOwnPass()
        {
            Func<string, double> on = key => key == AtmospheresSettings.Keys.OwnClouds ? 1.0 : 0.0;
            Assert.IsTrue(OwnCloudOptions.Active(on, CloudSourceKind.Ours));
            Assert.IsFalse(OwnCloudOptions.Active(on, CloudSourceKind.Eve));
            Assert.IsFalse(OwnCloudOptions.Active(on, CloudSourceKind.Both));
            Assert.IsFalse(OwnCloudOptions.Active(on, CloudSourceKind.None));
            Assert.IsFalse(OwnCloudOptions.Active(key => 0.0, CloudSourceKind.Ours));
        }

        [TestMethod]
        public void AJumpOfTheClockBeyondWhatTheFramesAndTheWarpAccountForIsACut()
        {
            Assert.IsFalse(OwnCloudPlan.WeatherCut(100.02, 100.0, 0.02, 1.0));
            Assert.IsFalse(OwnCloudPlan.WeatherCut(2000.0, 100.0, 0.02, 100000.0), "100000x warp moves 2000 s in a frame");
            Assert.IsTrue(OwnCloudPlan.WeatherCut(10000.0, 100.0, 0.02, 1.0), "a quickload");
            Assert.IsTrue(OwnCloudPlan.WeatherCut(50.0, 100.0, 0.02, 1.0), "a revert");
        }

        [TestMethod]
        public void TheHeightMapsAreRebuiltFasterUnderWarpAndTheCameraEventIsClamped()
        {
            Assert.AreEqual(OwnCloudPlan.HeightIntervalS, OwnCloudPlan.HeightInterval(1f));
            Assert.AreEqual(OwnCloudPlan.HeightIntervalWarpS, OwnCloudPlan.HeightInterval(100f));
            Assert.AreEqual(0, OwnCloudPlan.CameraEventIndex(-4f));
            Assert.AreEqual(1, OwnCloudPlan.CameraEventIndex(1f));
            Assert.AreEqual(2, OwnCloudPlan.CameraEventIndex(9f));
            Assert.IsTrue(OwnCloudPlan.Due(10f, 5f, 1f, false));
            Assert.IsFalse(OwnCloudPlan.Due(5.5f, 5f, 1f, false));
            Assert.IsTrue(OwnCloudPlan.Due(5.5f, 5f, 1f, true));
        }

        [TestMethod]
        public void TheFlashPeakScalesWithTheSunsBrightnessAndNeverVanishes()
        {
            Assert.AreEqual(1f, OwnCloudPlan.FlashPeakScale(OwnCloudPlan.HarnessSunLuminance), 1e-4f);
            Assert.AreEqual(2f, OwnCloudPlan.FlashPeakScale(2f * OwnCloudPlan.HarnessSunLuminance), 1e-4f);
            Assert.IsTrue(OwnCloudPlan.FlashPeakScale(0f) >= 0.1f);
        }

        // --- The settings -----------------------------------------------------------------------------------------------------------

        // Every key of the cloud group is read by one of the Apply methods of OwnCloudOptions: a new key that nothing applies is a switch that does nothing.
        [TestMethod]
        public void OwnCloudOptionsReadsEveryCloudSettingKey()
        {
            var read = new HashSet<string>(StringComparer.Ordinal);
            Func<string, double> v = key =>
            {
                read.Add(key);
                SettingDef def = AtmospheresSettings.Definition(key);
                return def.Kind == SettingKind.Text ? 0.0 : def.Default;
            };
            OwnCloudOptions.Active(v, CloudSourceKind.Ours);
            OwnCloudOptions.LayersToDraw(v, 4);
            OwnCloudOptions.Apply(new NoiseSwitches(), v);
            OwnCloudOptions.Apply(new HeightTuning(), v);
            OwnCloudOptions.Apply(new LayerFrame(), v);
            OwnCloudOptions.Apply(new CloudLightSettings(), v);
            OwnCloudOptions.Apply(new CloudShadowFrame(), v);
            OwnCloudOptions.ShadowActive(v, CloudSourceKind.Ours, 2);
            OwnCloudOptions.Apply(new FarViewSettings(), v);
            OwnCloudOptions.MapFade(v);
            OwnCloudOptions.Apply(new ReconSettings(), v);
            string[] keys =
            {
                "ownClouds", "layerCap", "layerStepQuality", "layerStepGrowth", "cloudNoise", "cloudNoiseUntile", "cloudNoiseCurl", "cloudNoiseTier",
                "cloudNoiseDistanceLod", "cloudFineDetail", "cloudEntryRefinement", "cloudHashDither", "cloudLightQuality", "cloudLightOctaves", "cloudPowder",
                "cloudSunTint", "cloudMoonLights", "cloudAmbientTaps", "cloudAmbientReuse", "cloudShadows", "cloudShadowQuality",
                "cloudShadowFloor", "cloudReconMode", "cloudResolution", "cloudRefineAtContours", "cloudSunMarchLong", "cloudSunWhiteBalance", "cloudDiffusionDecay", "cloudFarHaze",
                "cloudFarShapeMean", "cloudFarMinSamples", "cloudFarPolicy", "cloudFarMapFade", "cloudBaseVariation", "cloudBasePhysics", "cloudTopFromUpdraft",
                "baseRagged"
            };
            foreach (string key in keys) Assert.IsTrue(read.Contains(key), "nothing reads " + key);
        }

        // The defaults of this package's wiring leave every quality or cost switch where T10 and T12 left it.
        [TestMethod]
        public void TheDefaultsKeepTheReconstructionAndTheEdgeModeOffAndTheLightningOff()
        {
            Assert.AreEqual(ReconMode.Full, OwnCloudOptions_ReconModeDefault());
            Assert.IsFalse(EdgeSettings.AdaptiveDefault);
            Assert.IsFalse(EdgeSettings.ReconstructorDefault);
            Assert.IsFalse(EdgeSettings.StepBandDefault);
            Assert.IsFalse(LightningSwitches.EnabledDefault);
            Assert.IsFalse(LayerSwitches.OwnCloudsDefault);
            Assert.IsFalse(new FarViewSettings().Haze);
            Assert.IsFalse(new FarViewSettings().Policy);
            Assert.AreEqual(LayerFrame.SunMarchShortM, new LayerFrame().SunMarchM);
        }

        private static ReconMode OwnCloudOptions_ReconModeDefault()
        {
            var s = new ReconSettings();
            OwnCloudOptions.Apply(s, key => AtmospheresSettings.Definition(key).Default);
            return s.Mode;
        }
    }
}
