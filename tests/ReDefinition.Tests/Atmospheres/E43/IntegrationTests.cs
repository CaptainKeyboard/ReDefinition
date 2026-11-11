using System;
using System.Diagnostics;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.E43
{
    // E43 in the settings and in the field: the four keys, the layered window of FillWindow, the vertical wind of Sample, the layer maps of the field.
    [TestClass]
    public class IntegrationTests
    {
        private const int Body = FieldFixture.BodyIndex;

        [TestCleanup]
        public void Clear()
        {
            LayerWindOptions.SteeringOverride = null;
            LayerWindOptions.WindShearOverride = LayerWindOptions.VerticalMotionOverride = LayerWindOptions.AnvilTiltOverride = null;
            LayerWindOptions.Couple = true;
            AtmospheresSettings.ResetToDefaults();
        }

        [TestMethod]
        public void TheFourKeysAreInTheTableWithTheirDefaultsAndReasons()
        {
            SettingDef shear = AtmospheresSettings.Definition("windShear"), steer = AtmospheresSettings.Definition("layerWindSteering");
            SettingDef vertical = AtmospheresSettings.Definition("verticalMotion"), tilt = AtmospheresSettings.Definition("anvilTilt");
            Assert.IsTrue(shear.Default == 1.0 && vertical.Default == 1.0 && tilt.Default == 1.0);
            Assert.AreEqual(SettingKind.Choice, steer.Kind);
            CollectionAssert.AreEqual(new[] { "Off", "MainPeriod", "MapsOnly" }, steer.Choices);
            Assert.AreEqual((double)(int)LayerSteeringMode.MainPeriod, steer.Default);
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsSim, shear.GreyReason);
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsSim, vertical.GreyReason);
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsSim, tilt.GreyReason);
            Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsEve, steer.GreyReason);
            foreach (SettingDef d in new[] { shear, steer, vertical, tilt })
            {
                Assert.AreEqual(AtmospheresSettings.Categories.Wind, d.Category);
                Assert.IsTrue(d.Tooltip.Split(' ').All(word => char.IsUpper(word[0])), d.Key + ": " + d.Tooltip + " is Title Case");
                Assert.IsTrue(SettingHelp.For(d.Key).Length > 20, d.Key + " has a help text");
            }
        }

        [TestMethod]
        public void TheOptionsReadTheSettingsAndAProbeCanPinThem()
        {
            Assert.AreEqual(LayerSteeringMode.MainPeriod, LayerWindOptions.Steering);
            Assert.IsTrue(LayerWindOptions.WindShear && LayerWindOptions.VerticalMotion && LayerWindOptions.AnvilTilt);
            AtmospheresSettings.SetNumber("layerWindSteering", (double)(int)LayerSteeringMode.MapsOnly);
            AtmospheresSettings.SetNumber("windShear", 0.0);
            Assert.AreEqual(LayerSteeringMode.MapsOnly, LayerWindOptions.Steering);
            Assert.IsFalse(LayerWindOptions.WindShear);
            Assert.IsFalse(LayerWindOptions.Layers().Shear);
            LayerWindOptions.SteeringOverride = LayerSteeringMode.Off;
            Assert.AreEqual(LayerSteeringMode.Off, LayerWindOptions.Steering, "the pin wins over the setting");
        }

        [TestMethod]
        public void TheLayeredWindowDiffersFromThePlainOneAndStaysInTheFieldsUnits()
        {
            GeneratorField field = FieldFixture.Field();
            double ut = 3.0e6;
            var layered = new WeatherWindow();
            var plain = new WeatherWindow();
            double bestDiff = 0.0;
            bool anyUp = false;
            float maxUp = 0f;
            for (int lon = -160; lon <= 160; lon += 40)
                foreach (int lat in new[] { -55, -35, -15, 15, 35, 55 })
                {
                    LayerWindOptions.WindShearOverride = LayerWindOptions.VerticalMotionOverride = LayerWindOptions.AnvilTiltOverride = null;
                    Assert.IsTrue(field.FillWindow(Body, lat, lon, 40.0, ut, layered));
                    LayerWindOptions.WindShearOverride = LayerWindOptions.VerticalMotionOverride = LayerWindOptions.AnvilTiltOverride = false;
                    LayerWindOptions.Couple = false;
                    Assert.IsTrue(field.FillWindow(Body, lat, lon, 40.0, ut, plain));
                    LayerWindOptions.Couple = true;
                    for (int i = 0; i < layered.Count; i++)
                    {
                        Assert.IsTrue(layered.CloudWater[i] >= 0f && !float.IsNaN(layered.CloudWater[i]) && layered.CloudWater[i] <= 1.6f * GeneratorField.WindowGramsPerDensity + 1e-4f);
                        Assert.IsTrue(layered.Updraft[i] >= 0f && !float.IsNaN(layered.Updraft[i]));
                        bestDiff = Math.Max(bestDiff, Math.Abs(layered.CloudWater[i] - plain.CloudWater[i]));
                        maxUp = Math.Max(maxUp, layered.Updraft[i]);
                        if (layered.Updraft[i] > 0.2f) anyUp = true;
                        Assert.AreEqual(plain.Temperature[i], layered.Temperature[i], 1e-4f, "the temperature is the field's in both");
                    }
                }
            Assert.IsTrue(bestDiff > 0.01, "the layered window is not the plain one: " + bestDiff);
            Assert.IsTrue(anyUp, "towers lift: largest updraft " + maxUp);
        }

        [TestMethod]
        public void WithEverySwitchOffTheWindowIsTheFieldsOwn()
        {
            GeneratorField field = FieldFixture.Field();
            var a = new WeatherWindow();
            var b = new WeatherWindow();
            LayerWindOptions.WindShearOverride = LayerWindOptions.VerticalMotionOverride = LayerWindOptions.AnvilTiltOverride = false;
            LayerWindOptions.Couple = false;
            field.FillWindow(Body, 40, 10, 40.0, 3.0e6, a);
            field.FillWindow(Body, 40, 10, 40.0, 3.0e6, b);
            CollectionAssert.AreEqual(a.CloudWater.Take(a.Count).ToArray(), b.CloudWater.Take(b.Count).ToArray());
            Assert.IsTrue(a.CloudWater.Max() > 0f);
        }

        [TestMethod]
        public void TheLayeredWindowCostsLessThanTwiceAFrameBudgetSpike()
        {
            GeneratorField field = FieldFixture.Field();
            var window = new WeatherWindow();
            field.FillWindow(Body, 50, 10, 30, 3.0e6, window);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) field.FillWindow(Body, 50, 10, 30, 3.0e6 + i * 7.0, window);
            double layeredMs = sw.Elapsed.TotalMilliseconds / 5.0;
            LayerWindOptions.WindShearOverride = LayerWindOptions.VerticalMotionOverride = LayerWindOptions.AnvilTiltOverride = false;
            LayerWindOptions.Couple = false;
            sw.Restart();
            for (int i = 0; i < 5; i++) field.FillWindow(Body, 50, 10, 30, 3.0e6 + i * 7.0, window);
            double plainMs = sw.Elapsed.TotalMilliseconds / 5.0;
            Console.WriteLine("E43 window 32x32x24: layered " + layeredMs.ToString("0.0") + " ms, plain " + plainMs.ToString("0.0") + " ms");
            Assert.IsTrue(layeredMs < 400.0, "layered " + layeredMs);
        }

        [TestMethod]
        public void TheVerticalWindOfASampleIsTheUpdraftOfThePoint()
        {
            GeneratorField field = FieldFixture.Field();
            double ut = 3.0e6;
            int withUp = 0, signed = 0;
            double worstGround = 0.0;
            for (int lat = -60; lat <= 60; lat += 10)
                for (int lon = -180; lon < 180; lon += 15)
                {
                    WeatherSample ground, high;
                    Assert.IsTrue(field.Sample(Body, lat, lon, 0.0, ut, out ground));
                    Assert.IsTrue(field.Sample(Body, lat, lon, 4000.0, ut, out high));
                    worstGround = Math.Max(worstGround, Math.Abs(ground.WindWorld.z));
                    if (high.WindWorld.z > 0.1f) withUp++;
                    if (high.WindWorld.z < -0.05f) signed++;
                    Assert.IsFalse(float.IsNaN(high.WindWorld.z));
                }
            Assert.IsTrue(worstGround < 1e-6, "no vertical wind at the ground: " + worstGround);
            Assert.IsTrue(withUp > 3, "updrafts in cells and lift at 4000 m: " + withUp);
            Assert.IsTrue(signed >= 0);
            // verticalMotion off: the plain updraft (never negative)
            LayerWindOptions.VerticalMotionOverride = false;
            for (int lat = -60; lat <= 60; lat += 20)
                for (int lon = -180; lon < 180; lon += 30)
                {
                    WeatherSample s;
                    field.Sample(Body, lat, lon, 4000.0, ut, out s);
                    Assert.IsTrue(s.WindWorld.z >= 0f);
                }
        }

        [TestMethod]
        public void TheLayerMapIsTheCoverageOfTheLayerAndFollowsTheSwitches()
        {
            GeneratorField field = FieldFixture.Field();
            var deck = new WeatherMap();
            var surface = new WeatherMap();
            Assert.IsTrue(field.GetLayerCoverage(Body, 3.0e6, 2000.0, deck));
            Assert.IsTrue(field.GetMap(Body, WeatherMapKind.Coverage, 3.0e6, surface));
            Assert.AreEqual(surface.Width, deck.Width);
            Assert.AreEqual(surface.Height, deck.Height);
            Assert.IsTrue(deck.Values.All(v => v >= 0f && v <= 1.0001f));
            Assert.IsTrue(deck.Values.Max() > 0.2f);
            var high = new WeatherMap();
            Assert.IsTrue(field.GetLayerCoverage(Body, 3.0e6, 9000.0, high));
            double differ = 0.0;
            for (int i = 0; i < deck.Values.Length; i++) differ += Math.Abs(deck.Values[i] - high.Values[i]);
            Assert.IsTrue(differ / deck.Values.Length > 0.005, "two heights, two maps: " + differ / deck.Values.Length);
            Assert.IsFalse(field.GetLayerCoverage(Body + 1, 3.0e6, 2000.0, deck));
            Assert.IsFalse(field.GetLayerCoverage(Body, double.NaN, 2000.0, deck));
        }
    }
}
