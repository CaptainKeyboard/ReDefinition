using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Upscaler;
using UnityEngine.Rendering;

// Kerbal Frame Generator's settings class as the mod names it (full name KFG_Settings), standing in for the
// other mod: HostStack's KFG control finds it by that name.
public static class KFG_Settings
{
    // The blend's global switch.
    public static bool effectEnabled;
}

namespace ReDefinition.Tests
{
    // The parts of HostStack that read and write other mods' objects by name, tested on stand-ins: the reflection
    // helpers, TUFX's struct-held modes, the camera event of an SMAA type and Kerbal Frame Generator's switch.
    [TestClass]
    public class HostStackControlTests
    {
        // An object with a field, a property and statics, as another mod's classes have them.
        public class Foreign
        {
            public int Field = 3;
            public string ReadOnlyProperty { get { return "read"; } }
            public string Property { get; set; }
            public static int StaticField = 7;
            public int InstanceOnly = 1;
        }

        // TUFX's antialiasing modes as the post processing stack names them.
        public enum Mode { None, Fxaa, Taa }

        // A mode held in a struct, as TUFX's profile holds it.
        public struct Aa
        {
            public Mode Mode;
        }

        // A profile with its two antialiasing struct fields.
        public class Profile
        {
            public Aa Antialiasing;
            public Aa SecondaryCameraAntialiasing;
        }

        // An SMAA class that names its camera event, and one that holds a different kind of value there.
        public class SmaaWithEvent
        {
            public static CameraEvent SMAACameraEvent = CameraEvent.BeforeImageEffects;
        }

        public class SmaaWithoutEvent
        {
        }

        public class SmaaWithWrongEvent
        {
            public static int SMAACameraEvent = 5;
        }

        // The reflection helpers read fields and properties, and report a name the type lacks as false.
        [TestMethod]
        public void ReflectionReadsFieldsAndProperties()
        {
            Foreign foreign = new Foreign { Property = "set" };
            object value;

            Assert.IsTrue(ForeignReflection.TryGet(foreign, typeof(Foreign), "Field", out value));
            Assert.AreEqual(3, value);
            Assert.IsTrue(ForeignReflection.TryGet(foreign, typeof(Foreign), "ReadOnlyProperty", out value));
            Assert.AreEqual("read", value);
            Assert.IsTrue(ForeignReflection.TryGet(foreign, typeof(Foreign), "Property", out value));
            Assert.AreEqual("set", value);
            Assert.IsFalse(ForeignReflection.TryGet(foreign, typeof(Foreign), "Missing", out value));
            Assert.IsNull(value);
        }

        // A property without a setter cannot be written; a field and a property with one can.
        [TestMethod]
        public void ReflectionWritesWhatCanBeWritten()
        {
            Foreign foreign = new Foreign();

            Assert.IsTrue(ForeignReflection.TrySet(foreign, typeof(Foreign), "Field", 9));
            Assert.AreEqual(9, foreign.Field);
            Assert.IsTrue(ForeignReflection.TrySet(foreign, typeof(Foreign), "Property", "now"));
            Assert.AreEqual("now", foreign.Property);
            Assert.IsFalse(ForeignReflection.TrySet(foreign, typeof(Foreign), "ReadOnlyProperty", "x"));
            Assert.IsFalse(ForeignReflection.TrySet(foreign, typeof(Foreign), "Missing", 1));
        }

        // The static lookups see static fields only, and write only a value of the field's type.
        [TestMethod]
        public void StaticLookupsSeeStaticFieldsOnly()
        {
            int before = Foreign.StaticField;
            try
            {
                object value;
                Assert.IsTrue(ForeignReflection.TryGetStatic(typeof(Foreign), "StaticField", out value));
                Assert.AreEqual(before, value);
                Assert.IsFalse(ForeignReflection.TryGetStatic(typeof(Foreign), "InstanceOnly", out value));

                Assert.IsFalse(ForeignReflection.TrySetStatic(typeof(Foreign), "StaticField", "text"));
                Assert.IsFalse(ForeignReflection.TrySetStatic(typeof(Foreign), "StaticField", null));
                Assert.IsFalse(ForeignReflection.TrySetStatic(typeof(Foreign), "InstanceOnly", 2));
                Assert.AreEqual(before, Foreign.StaticField);

                Assert.IsTrue(ForeignReflection.TrySetStatic(typeof(Foreign), "StaticField", 11));
                Assert.AreEqual(11, Foreign.StaticField);
            }
            finally
            {
                Foreign.StaticField = before;
            }
        }

        // A struct-held mode is read with its enum type, and a write goes through the box and back into the profile.
        [TestMethod]
        public void TufxModeIsReadAndWrittenThroughTheStruct()
        {
            Profile profile = new Profile();
            profile.Antialiasing.Mode = Mode.Taa;
            object mode;
            Type modeType;

            Assert.IsTrue(TufxAaControl.TryGetAaMode(profile, typeof(Profile), "Antialiasing", out mode, out modeType));
            Assert.AreEqual(Mode.Taa, mode);
            Assert.AreEqual(typeof(Mode), modeType);

            Assert.IsTrue(TufxAaControl.TrySetAaMode(profile, typeof(Profile), "Antialiasing", Mode.None));
            Assert.AreEqual(Mode.None, profile.Antialiasing.Mode);
            Assert.AreEqual(Mode.None, profile.SecondaryCameraAntialiasing.Mode);
        }

        // A field the profile lacks reads as false, and no mode is written without a value.
        [TestMethod]
        public void TufxModeOfAMissingFieldIsFalse()
        {
            Profile profile = new Profile();
            object mode;
            Type modeType;

            Assert.IsFalse(TufxAaControl.TryGetAaMode(profile, typeof(Profile), "Missing", out mode, out modeType));
            Assert.IsNull(mode);
            Assert.IsNull(modeType);
            Assert.IsFalse(TufxAaControl.TrySetAaMode(profile, typeof(Profile), "Missing", Mode.None));
            Assert.IsFalse(TufxAaControl.TrySetAaMode(profile, typeof(Profile), "Antialiasing", null));
        }

        // The swap puts `to` in only where the profile holds `from`: what was chosen in TUFX's window since stands.
        [TestMethod]
        public void SwapChangesOnlyWhatStillHoldsTheOldMode()
        {
            Profile profile = new Profile();
            profile.Antialiasing.Mode = Mode.None;

            Assert.IsTrue(TufxAaControl.SwapAaMode(profile, typeof(Profile), "Antialiasing", Mode.None, Mode.Taa));
            Assert.AreEqual(Mode.Taa, profile.Antialiasing.Mode);

            Assert.IsFalse(TufxAaControl.SwapAaMode(profile, typeof(Profile), "Antialiasing", Mode.None, Mode.Fxaa));
            Assert.AreEqual(Mode.Taa, profile.Antialiasing.Mode);

            Assert.IsFalse(TufxAaControl.SwapAaMode(profile, typeof(Profile), "Antialiasing", Mode.Taa, Mode.Taa));
            Assert.IsFalse(TufxAaControl.SwapAaMode(profile, typeof(Profile), "Antialiasing", Mode.Taa, null));
            Assert.AreEqual(Mode.Taa, profile.Antialiasing.Mode);
        }

        // An SMAA type's own camera event is used; a type without one, or with another kind of value, gets the default.
        [TestMethod]
        public void SmaaEventIsReadFromTheType()
        {
            Assert.AreEqual(CameraEvent.BeforeImageEffects, SmaaControl.EventOf(typeof(SmaaWithEvent)));
            Assert.AreEqual(CameraEvent.AfterForwardAlpha, SmaaControl.EventOf(typeof(SmaaWithoutEvent)));
            Assert.AreEqual(CameraEvent.AfterForwardAlpha, SmaaControl.EventOf(typeof(SmaaWithWrongEvent)));
        }

        // Found on, the blend is switched off, and Restore gives it back on.
        [TestMethod]
        public void KfgBlendFoundOnIsSwitchedOffAndBackOn()
        {
            KFG_Settings.effectEnabled = true;
            KfgControl kfg = new KfgControl();

            Assert.AreEqual("Kerbal Frame Generator blend switched off.", kfg.Disable());
            Assert.IsFalse(KFG_Settings.effectEnabled);

            Assert.AreEqual(" Kerbal Frame Generator blend back on.", kfg.PutBack());
            Assert.IsTrue(KFG_Settings.effectEnabled);
            Assert.AreEqual("", kfg.PutBack());
        }

        // Found off, the blend is left off and nothing is put back.
        [TestMethod]
        public void KfgBlendFoundOffStaysOff()
        {
            KFG_Settings.effectEnabled = false;
            KfgControl kfg = new KfgControl();

            Assert.AreEqual("Kerbal Frame Generator blend off.", kfg.Disable());
            Assert.AreEqual("", kfg.PutBack());
            Assert.IsFalse(KFG_Settings.effectEnabled);
        }

        // Switched on by the player after it was switched off here, the blend is found on again and goes back on.
        [TestMethod]
        public void KfgBlendTurnedOnLaterIsRememberedAsOn()
        {
            KFG_Settings.effectEnabled = false;
            KfgControl kfg = new KfgControl();
            kfg.Disable();
            KFG_Settings.effectEnabled = true;

            Assert.AreEqual("Kerbal Frame Generator blend switched off.", kfg.Disable());
            Assert.AreEqual(" Kerbal Frame Generator blend back on.", kfg.PutBack());
            Assert.IsTrue(KFG_Settings.effectEnabled);
        }

        // A blend the player has turned on since it was switched off here is not touched by the restore.
        [TestMethod]
        public void KfgRestoreLeavesWhatThePlayerTurnedOn()
        {
            KFG_Settings.effectEnabled = true;
            KfgControl kfg = new KfgControl();
            kfg.Disable();
            KFG_Settings.effectEnabled = true;

            Assert.AreEqual("", kfg.PutBack());
            Assert.IsTrue(KFG_Settings.effectEnabled);
        }

        // The report row for the blend is suitable only while the switch is off, and the rows go through the interface.
        [TestMethod]
        public void KfgRowFollowsTheSwitch()
        {
            IHostFilter kfg = new KfgControl();
            var lines = new List<HostStack.Line>();

            KFG_Settings.effectEnabled = true;
            kfg.AddLines(lines);
            KFG_Settings.effectEnabled = false;
            kfg.AddLines(lines);

            Assert.AreEqual(2, lines.Count);
            Assert.IsFalse(lines[0].Ok);
            Assert.IsTrue(lines[1].Ok);
            Assert.AreEqual("switch off", lines[1].Value);
            Assert.IsTrue(lines[1].Present);
        }

        // Apply and Restore append their text to the report in the order the add-on shows it.
        [TestMethod]
        public void KfgReportsThroughTheInterface()
        {
            KFG_Settings.effectEnabled = true;
            IHostFilter kfg = new KfgControl();
            var report = new StringBuilder();

            kfg.Apply(report);
            kfg.Restore(report);

            Assert.AreEqual("Kerbal Frame Generator blend switched off.  Kerbal Frame Generator blend back on.",
                            report.ToString());
            Assert.AreEqual("Kerbal Frame Generator", kfg.Label);
        }

        // A report row of a mod that is not installed counts as suitable, and says it is not there.
        [TestMethod]
        public void MissingRowIsOkAndNotPresent()
        {
            HostStack.Line missing = HostStack.Line.Missing("Scatterer");
            HostStack.Line info = HostStack.Line.Info("TUFX profile", "Default");

            Assert.IsTrue(missing.Ok);
            Assert.IsFalse(missing.Present);
            Assert.AreEqual("not installed", missing.Value);
            Assert.IsTrue(info.Ok);
            Assert.IsTrue(info.Present);
            Assert.AreEqual("Default", info.Value);
        }
    }
}
