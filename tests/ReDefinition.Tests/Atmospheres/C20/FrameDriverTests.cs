using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.C20
{
    // The frame driver's composer, the light writer's sun rule, the exposure hook and the ambient tracker pattern the writer uses, without the
    // game.
    [TestClass]
    public class FrameDriverTests
    {
        private static DriverInput Input(float level, float kelvin, int frame, double ut = 1000.0)
        {
            DriverInput input = DriverInput.Neutral();
            input.Active = true;
            input.BodyName = "Kerbin";
            input.Sample = WeatherSample.Clear(1);
            input.Sample.Level01 = level;
            input.Sample.TemperatureK = kelvin;
            input.Sample.WindWorld = new Vector3(4f, 0f, 0f);
            input.Sample.OpticalDepthVertical = level * 20f;
            input.Ut = ut;
            input.DtGame = 1.0;
            input.DtReal = 1f / 60f;
            input.AltitudeM = 100f;
            input.SunElevationRad = 0.5f;
            input.CameraBodyPos = new Vector3(600000f, 0f, 0f);
            input.Seed = 11;
            input.FrameCount = frame;
            input.CloudLayerName = "";
            return input;
        }

        private static void Settle(FrameComposer composer, WeatherView view, float level, float kelvin, ref int frame, ref double ut)
        {
            for (int i = 0; i < 200; i++)
            {
                DriverInput input = Input(level, kelvin, frame++, ut += 1.0);
                input.ShowerPulse = false;
                composer.Compose(ref input, view);
            }
        }

        [TestMethod]
        public void TheViewIsPublishedWithinOneFrame()
        {
            var composer = new FrameComposer();
            var view = new WeatherView();
            DriverInput input = Input(0.7f, 268f, 41);
            composer.Compose(ref input, view);
            Assert.IsTrue(view.Active);
            Assert.AreEqual(41, view.FrameStamp);
            Assert.AreEqual(0.7f, view.Level, 1e-5f, "the first frame sets the level at once");
            Assert.AreEqual(PrecipType.Snow, view.Type);
            Assert.AreEqual(PrecipBand.Heavy, view.Band);
            Assert.IsTrue(view.Version > 0);
            Assert.AreEqual("Kerbin", view.BodyName);
            Assert.IsFalse(view.IsStale(42));
        }

        [TestMethod]
        public void TheViewFollowsTheLookTable()
        {
            var composer = new FrameComposer();
            var view = new WeatherView();
            int frame = 0;
            double ut = 0.0;
            Settle(composer, view, 1f, 250f, ref frame, ref ut);
            LookValues top = PrecipitationLook.Evaluate(1f);
            Assert.AreEqual(top.VisibilityM, view.VisibilityM, 1f);
            Assert.AreEqual(top.VeilAlpha, view.VeilGroundAlpha, 1e-4f);
            Assert.AreEqual(top.FlakesPerS, view.FlakeRatePerS, 1f);
            Assert.AreEqual(top.AmbientFactor, view.AmbientFactor, 1e-4f);
            Assert.AreEqual(PrecipitationLook.FogDensity(top.VisibilityM), view.FogDensity, 1e-5f);
            Assert.AreEqual(3, view.FlakeLayers.Length);
            Assert.IsTrue(view.FlakeLayers[0].Width < view.FlakeLayers[2].Width);
        }

        [TestMethod]
        public void TheLevelRisesNoFasterThanTheRateLimit()
        {
            var composer = new FrameComposer();
            var view = new WeatherView();
            DriverInput first = Input(0f, 268f, 1);
            composer.Compose(ref first, view);
            for (int s = 1; s <= 30; s++)
            {
                DriverInput input = Input(1f, 268f, 1 + s, 1000.0 + s);
                input.ShowerPulse = false;
                composer.Compose(ref input, view);
            }
            Assert.AreEqual(30f / 90f, composer.Level, 1e-4f);
        }

        [TestMethod]
        public void NoAllocationInAFrame()
        {
            var composer = new FrameComposer();
            var view = new WeatherView();
            int frame = 0;
            double ut = 0.0;
            Settle(composer, view, 0.6f, 270f, ref frame, ref ut);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                DriverInput input = Input(0.6f + 0.1f * (float)Math.Sin(i * 0.05), 270f + (float)Math.Sin(i * 0.01), frame++, ut += 0.5);
                composer.Compose(ref input, view);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.AreEqual(0L, after - before, "bytes allocated by 1000 frames");
        }

        [TestMethod]
        public void AnInactiveBodyClearsTheView()
        {
            var composer = new FrameComposer();
            var view = new WeatherView();
            DriverInput on = Input(0.8f, 268f, 1);
            composer.Compose(ref on, view);
            Assert.IsTrue(view.Active);
            DriverInput off = Input(0.8f, 268f, 2);
            off.Active = false;
            composer.Compose(ref off, view);
            Assert.IsFalse(view.Active);
            Assert.AreEqual(0f, view.Level);
            Assert.AreEqual(PrecipType.None, view.Type);
            Assert.AreEqual(1f, view.SunTransmission);
            Assert.AreEqual(2, view.FrameStamp);
            Assert.IsTrue(view.IsStale(2), "an inactive view is no weather");
        }

        [TestMethod]
        public void TheHeightFadeRemovesTheWeatherAboveTheCloudTop()
        {
            var composer = new FrameComposer();
            var view = new WeatherView();
            DriverInput low = Input(0.8f, 268f, 1);
            low.AltitudeM = 3000f;
            composer.Compose(ref low, view);
            Assert.AreEqual(0.8f, view.Level, 1e-5f);
            DriverInput high = Input(0.8f, 268f, 2);
            high.AltitudeM = 12000f;
            high.Jump = true;
            composer.Compose(ref high, view);
            Assert.AreEqual(0f, view.Level, 1e-6f);
            Assert.AreEqual(PrecipType.None, view.Type);
            Assert.AreEqual(0f, view.FlakeRatePerS);
            Assert.AreEqual(0.5f, FrameComposer.HeightFactor(7000f, 4000f, 10000f), 1e-5f);
        }

        [TestMethod]
        public void TheShowerPulseIsSmoothAndNeverTouchesTheFieldLevel()
        {
            var withPulse = new FrameComposer();
            var without = new FrameComposer();
            var viewA = new WeatherView();
            var viewB = new WeatherView();
            float previous = float.NaN;
            float maxStep = 0f;
            float min = 10f, max = -10f;
            for (int s = 0; s < 4 * 3600; s++)
            {
                DriverInput a = Input(0.12f, 268f, s, 5000.0 + s);
                a.ShowerPulse = true;
                a.Jump = true;
                DriverInput b = a;
                b.ShowerPulse = false;
                withPulse.Compose(ref a, viewA);
                without.Compose(ref b, viewB);
                Assert.AreEqual(without.Level, withPulse.Level, 0f, "the level the forecast reads is the same");
                float shown = withPulse.DisplayLevel;
                if (!float.IsNaN(previous)) maxStep = Math.Max(maxStep, Math.Abs(shown - previous));
                previous = shown;
                min = Math.Min(min, shown / 0.12f);
                max = Math.Max(max, shown / 0.12f);
            }
            Assert.IsTrue(maxStep < 0.12f * 0.65f * 1.5f / 120f + 1e-5f, "largest step per second " + maxStep);
            Assert.IsTrue(min >= 0.35f - 1e-3f && max <= 1f + 1e-3f && max - min > 0.3f, "pulse range " + min + " to " + max);
            float atFlurries = ShowerPulse.Factor(0.0, 3, 0.1f);
            Assert.IsTrue(atFlurries >= 0.35f && atFlurries <= 1f);
            Assert.AreEqual(1f, ShowerPulse.Factor(1234.5, 3, 0.25f), 1e-6f, "no pulse above flurries");
        }

        [TestMethod]
        public void TheSunTransmissionEqualsTheHelperAndFallsWithTheCloud()
        {
            var composer = new FrameComposer();
            var view = new WeatherView();
            float previous = 2f;
            for (int i = 0; i <= 10; i++)
            {
                DriverInput input = Input(0.5f, 268f, i);
                input.Sample.OpticalDepthVertical = 0f;
                input.OpticalDepth = i * 0.5f;
                input.SunElevationRad = 0.6f;
                composer.Compose(ref input, view);
                Assert.AreEqual(ReDefinition.Core.SunTransmission.From(i * 0.5f, 0.6f), view.SunTransmission, 1e-6f);
                Assert.IsTrue(view.SunTransmission <= previous);
                previous = view.SunTransmission;
            }
            Assert.AreEqual(1f, ReDefinition.Core.SunTransmission.From(0f, 0.6f));
        }

        [TestMethod]
        public void TheTypeChangesThroughSleetWithoutAJumpAtTheLine()
        {
            var composer = new FrameComposer();
            var view = new WeatherView();
            DriverInput start = Input(0.5f, 266f, 0);
            composer.Compose(ref start, view);
            Assert.AreEqual(PrecipType.Snow, view.Type);
            bool sleet = false;
            double ut = 1000.0;
            TypeMix before = composer.Mix;
            for (int s = 1; s <= 9000; s++)
            {
                // The air warms from 266 K by 1 K every 10 game minutes and stops at 280 K.
                DriverInput input = Input(0.5f, Math.Min(266f + s / 600f, 280f), s, ut += 1.0);
                input.ShowerPulse = false;
                composer.Compose(ref input, view);
                if (view.Type == PrecipType.Sleet) sleet = true;
                if (s % 60 == 0)
                {
                    Assert.IsTrue(Math.Abs(composer.Mix.Snow - before.Snow) <= 0.1f + 1e-4f && Math.Abs(composer.Mix.Rain - before.Rain) <= 0.1f + 1e-4f);
                    before = composer.Mix;
                }
            }
            Assert.IsTrue(sleet, "sleet between snow and rain");
            Assert.AreEqual(PrecipType.Rain, view.Type);
        }

        [TestMethod]
        public void ABodyKindOverridesTheTemperatureType()
        {
            var composer = new FrameComposer();
            var view = new WeatherView();
            DriverInput input = Input(0.6f, 200f, 1);
            input.BodyKind = PrecipType.Dust;
            composer.Compose(ref input, view);
            Assert.AreEqual(PrecipType.Dust, view.Type);
        }

        [TestMethod]
        public void TheViewVersionRisesOnlyWhenAValueMoved()
        {
            var composer = new FrameComposer();
            var view = new WeatherView();
            int frame = 0;
            double ut = 0.0;
            Settle(composer, view, 0.5f, 268f, ref frame, ref ut);
            int version = view.Version;
            DriverInput same = Input(0.5f, 268f, frame++, ut);
            same.ShowerPulse = false;
            same.DtGame = 0.0;
            same.DtReal = 0f;
            composer.Compose(ref same, view);
            Assert.AreEqual(version, view.Version);
            Assert.AreEqual(frame - 1, view.FrameStamp);
        }

        [TestMethod]
        public void TheSunDimmingCountsHalfWhereAnEveLayerCarriesTheWeather()
        {
            Assert.AreEqual(0.1f, LightWriter.SunFactorOf(0.1f, false), 1e-6f);
            Assert.AreEqual(0.55f, LightWriter.SunFactorOf(0.1f, true), 1e-6f);
            Assert.AreEqual(1f, LightWriter.SunFactorOf(1f, true), 1e-6f);
        }

        [TestMethod]
        public void TheLightWriterIdlesWithoutWeatherAndTheExposureHookIsSilentWithoutTufx()
        {
            var writer = new LightWriter();
            var view = new WeatherView();
            view.Clear();
            writer.Update(view, null);
            Assert.IsFalse(writer.Writing);
            Assert.AreEqual(0, writer.ExternalWrites);
            writer.Shutdown();
            var hook = new TufxHook();
            hook.Apply(0.8f);
            hook.Apply(0f);
            hook.Restore();
            Assert.IsFalse(hook.Available, "no TUFX assembly is loaded");
        }

        [TestMethod]
        public void TheTrackerPatternScalesTheSunAndRestoresItAtTheEnd()
        {
            var intensity = new AmbientTracker();
            var shadow = new AmbientTracker();
            float sun = 1.2f, strength = 1f;
            for (int frame = 0; frame < 5; frame++)
            {
                sun = intensity.Scale(new Color(sun, 0f, 0f, 1f), 0.4f).r;
                strength = shadow.Scale(new Color(strength, 0f, 0f, 1f), 0.4f).r;
            }
            Assert.AreEqual(0.48f, sun, 1e-5f, "scaled once from the baseline, not compounded");
            Color restore;
            Assert.IsTrue(intensity.Restore(new Color(sun, 0f, 0f, 1f), out restore));
            Assert.AreEqual(1.2f, restore.r, 1e-6f);
            // Another mod wrote the value: it is the new baseline and the restore leaves it alone.
            var other = new AmbientTracker();
            float v = other.Scale(new Color(1f, 0f, 0f, 1f), 0.5f).r;
            v = other.Scale(new Color(0.9f, 0f, 0f, 1f), 0.5f).r;
            Assert.AreEqual(1, other.ExternalWrites);
            Assert.AreEqual(0.45f, v, 1e-6f);
            Assert.IsFalse(other.Restore(new Color(0.3f, 0f, 0f, 1f), out restore));
        }
    }
}
