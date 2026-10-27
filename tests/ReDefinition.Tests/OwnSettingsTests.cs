using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Modules;
using ReDefinition.Proxy;

namespace ReDefinition.Tests
{
    // OwnSettings' table of settings: every public field is in it, so it is saved,
    // compared and, where the add-on reads it live, copied.
    [TestClass]
    public class OwnSettingsTests
    {
        // A value of the same type that differs from the given one, for each type OwnSettings' fields have.
        private static object Other(object value)
        {
            if (value is bool b) return !b;
            if (value is float f) return f + 7f;
            if (value is int i) return i + 1;
            if (value is string) return "Ctrl+F7";
            Type type = value.GetType();
            if (type.IsEnum)
            {
                Array values = Enum.GetValues(type);
                foreach (object candidate in values)
                    if (!candidate.Equals(value)) return candidate;
            }
            throw new InvalidOperationException("no other value for " + type.Name);
        }

        // The probe's ini shots set ReDefinition's settings by their keys (SceneProbe, mod.<key>).
        [TestMethod]
        public void ASettingIsSetByItsKey()
        {
            OwnSettings settings = new OwnSettings();
            Assert.IsTrue(settings.SetValue("technique", "Dlss"));
            Assert.AreEqual(UpscalerBackend.Dlss, settings.Backend);
            Assert.IsTrue(settings.SetValue("raytracing", "Full"));
            Assert.AreEqual("Full", settings.ValueOf("raytracing"));
            Assert.IsTrue(settings.SetValue("rayReconstruction", "false"));
            Assert.IsFalse(settings.RayReconstruction);
            Assert.AreEqual("Shadows", settings.ValueOf("rayReconstructionFrom"));
            Assert.IsTrue(settings.SetValue("rayReconstructionFrom", "Full"));
            Assert.AreEqual("Full", settings.ValueOf("rayReconstructionFrom"));
            Assert.IsFalse(settings.SetValue("technique", "Nonsense"));
            Assert.IsFalse(settings.SetValue("noSuchKey", "1"));
            Assert.IsNull(settings.ValueOf("noSuchKey"));
        }

        // Changing any one public field changes the snapshot: a field missing from the table would be neither
        // saved nor seen as changed.
        [TestMethod]
        public void EveryFieldCountsInTheSnapshot()
        {
            foreach (FieldInfo field in typeof(OwnSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var settings = new OwnSettings();
                string before = settings.Snapshot();
                field.SetValue(settings, Other(field.GetValue(settings)));
                Assert.AreNotEqual(before, settings.Snapshot(), field.Name + " is not in OwnSettings' table");
            }
        }

        // ChangeLive copies onto the live settings only what a view changed, keeping a live value the view did
        // not touch; CopyLive takes the values read live and leaves the add-on's own, such as Enabled.
        [TestMethod]
        public void ChangeLiveTakesOnlyWhatChanged()
        {
            var live = new OwnSettings { RaytracingMoonlight = 40f, MoonGlow = true };
            var before = new OwnSettings { RaytracingMoonlight = 10f, MoonGlow = true };
            var after = before.Clone();
            after.MoonGlow = false;
            live.ChangeLive(before, after);
            Assert.IsFalse(live.MoonGlow);
            Assert.AreEqual(40f, live.RaytracingMoonlight);

            var copy = new OwnSettings();
            copy.CopyLive(new OwnSettings { RaytracingMoonlight = 12.5f, Enabled = true });
            Assert.AreEqual(12.5f, copy.RaytracingMoonlight);
            Assert.IsFalse(copy.Enabled, "Enabled is the add-on's own, not read live");
        }

        // The navball's place from the file reaches the live settings at the start. Without it the add-on began at the
        // centre and the next save wrote that over the place the player had put it at.
        [TestMethod]
        public void TheNavballsPlaceFromTheFileIsTakenAtTheStart()
        {
            var live = new OwnSettings();
            live.CopyLive(new OwnSettings { NavballOffset = -120f, NavballScreen = "2560x1440" });
            Assert.AreEqual(-120f, live.NavballOffset);
            Assert.AreEqual("2560x1440", live.NavballScreen);
        }

        // The raytracing's light colour is read live (RtKsp): a view's change has to reach
        // the live settings, which are what is saved.
        [TestMethod]
        public void TheLightColourIsReadLive()
        {
            var live = new OwnSettings();
            var before = new OwnSettings();
            var after = before.Clone();
            after.RaytracingLightColour = ReDefinition.Raytracing.RaytracingLightColour.Raytraced;
            live.ChangeLive(before, after);
            Assert.AreEqual(ReDefinition.Raytracing.RaytracingLightColour.Raytraced, live.RaytracingLightColour);
        }

        // The upscaler's switches the add-on holds in its settings object (quality, technique, DLSS model, the
        // masks, the jitter and the rest) travel with CopyLive and ChangeLive. The ones whose setters can refuse
        // or clamp a value (the switch, the sharpness, frame generation) stay the add-on's own.
        [TestMethod]
        public void TheUpscalersSwitchesTravelAsLiveSettings()
        {
            var from = new OwnSettings
            {
                Quality = FidelityFX.FSR3.Fsr3Upscaler.QualityMode.Performance, Backend = UpscalerBackend.Dlss,
                DlssModel = DlssModel.L, MipmapBias = false, AutoExposure = false, CompensateLodBias = false,
                DisableMsaa = false, ForceAnisotropic = false, TufxAfterUpscaling = false, TransparencyMask = true,
                ReactiveMask = ReDefinition.Upscaler.UpscalerMasks.ReactiveSource.Automatic, Jitter = false, SkinnedMotionVectors = false,
                Enabled = true, FrameGeneration = true, Sharpness = 0.5f,
            };
            var copy = new OwnSettings();
            copy.CopyLive(from);
            Assert.AreEqual(from.Quality, copy.Quality);
            Assert.AreEqual(UpscalerBackend.Dlss, copy.Backend);
            Assert.AreEqual(DlssModel.L, copy.DlssModel);
            Assert.AreEqual(ReDefinition.Upscaler.UpscalerMasks.ReactiveSource.Automatic, copy.ReactiveMask);
            Assert.IsFalse(copy.MipmapBias || copy.AutoExposure || copy.CompensateLodBias || copy.DisableMsaa);
            Assert.IsFalse(copy.ForceAnisotropic || copy.TufxAfterUpscaling || copy.Jitter || copy.SkinnedMotionVectors);
            Assert.IsTrue(copy.TransparencyMask);
            Assert.IsFalse(copy.Enabled, "the add-on's own");
            Assert.IsFalse(copy.FrameGeneration, "the add-on's own");
            Assert.AreEqual(1f, copy.Sharpness, "the add-on's own");
        }

        // The bounces read from a number as written, "2.0" included. NaN and the infinities in a hand-edited file
        // leave the default, where a cast to int would turn them into the smallest integer.
        [TestMethod]
        public void TheBouncesReadAFiniteNumberOnly()
        {
            var settings = new OwnSettings();
            Assert.IsTrue(settings.SetValue("raytracingBounces", "3"));
            Assert.AreEqual(3, settings.RaytracingBounces);
            Assert.IsTrue(settings.SetValue("raytracingBounces", "4.0"));
            Assert.AreEqual(4, settings.RaytracingBounces);
            foreach (string text in new[] { "NaN", "Infinity", "-Infinity", "many" })
            {
                Assert.IsFalse(settings.SetValue("raytracingBounces", text), text);
                Assert.AreEqual(4, settings.RaytracingBounces, text + " leaves the value");
            }
        }

        // A text setting takes what its validation allows and keeps its value for anything else; those without
        // validation take any text, a missing one as empty.
        [TestMethod]
        public void TextSettingsTakeOnlyWhatTheyAllow()
        {
            var settings = new OwnSettings();
            Assert.IsTrue(settings.SetValue("lowLatency", "Off"));
            Assert.AreEqual("Off", settings.LowLatency);
            Assert.IsFalse(settings.SetValue("lowLatency", "Sometimes"));
            Assert.AreEqual("Off", settings.LowLatency);
            Assert.IsTrue(settings.SetValue("frameGenerationTechnique", "Dlss"));
            Assert.IsFalse(settings.SetValue("frameGenerationTechnique", "Off"));
            Assert.AreEqual("Dlss", settings.FrameGenerationTechnique);
            Assert.IsTrue(settings.SetValue("diagnosticsPins", "a,b"));
            Assert.AreEqual("a,b", settings.DiagnosticsPins);
            Assert.IsTrue(settings.SetValue("navballScreen", "1920x1080"));
            Assert.AreEqual("1920x1080", settings.NavballScreen);
        }
    }
}
