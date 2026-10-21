using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReDefinition.Tests
{
    // OwnSettings' table of settings: every public field is in it, so it is saved,
    // compared and, where the add-on reads it live, copied.
    [TestClass]
    public class OwnSettingsTests
    {
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
            Assert.AreEqual(Bridges.UpscalerBackend.Dlss, settings.Backend);
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
    }
}
