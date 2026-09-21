using System.Collections.Generic;
using System.Reflection;
using System;
using KSP.Localization;
using ReDefinition.Core;

namespace ReDefinition.Window
{
    // KSP's joystick and gamepad axes, as its input screen lists them
    // (InputSettings.xml in sharedassets3.assets): the flight controls, the four
    // custom axes, the wheels, EVA and the camera. The mouse wheel is the one
    // static AxisBinding left out: its scale is a setting of its own (KSP.cfg,
    // mouseWheelSensitivity), as in KSP's own screen.
    //
    // An AxisBinding holds a primary and a secondary AxisBinding_Single: the axis
    // bound (idTag "joy<device>.<axis>", the controller's name, device and axis
    // index), whether it is inverted, its sensitivity and its deadzone. The window
    // edits copies, made and written the way KSP's own screen does it -- through
    // the binding's ConfigNode (SettingsInputBinding.SetupAxis and OnAccept).
    internal static class KspAxes
    {
        internal sealed class Axis
        {
            public string Name;
            public string Title;
            public string Group;
            // The AxisBinding as GameSettings holds it now.
            public Func<AxisBinding> Get;
        }

        private static List<Axis> axes;

        // In the order and groups of KSP's input screen, with its titles.
        internal static IList<Axis> All()
        {
            if (axes != null) return axes;
            axes = new List<Axis>();
            Add("AXIS_PITCH", 900775, "Flight: rotation");
            Add("AXIS_ROLL", 900776, "Flight: rotation");
            Add("AXIS_YAW", 900777, "Flight: rotation");
            Add("AXIS_TRANSLATE_X", 900778, "Flight: translation");
            Add("AXIS_TRANSLATE_Y", 900779, "Flight: translation");
            Add("AXIS_TRANSLATE_Z", 900780, "Flight: translation");
            Add("AXIS_THROTTLE", 900781, "Flight: throttle");
            Add("AXIS_THROTTLE_INC", 900782, "Flight: throttle");
            for (int i = 0; i < 4; i++)
            {
                int index = i;
                axes.Add(new Axis
                {
                    Name = "AXIS_CUSTOM[" + index + "].axisBinding",
                    Title = Title(8320072 + index, "Custom axis " + (index + 1)),
                    Group = "Flight: custom axes",
                    Get = () => GameSettings.AXIS_CUSTOM != null ? GameSettings.AXIS_CUSTOM[index].axisBinding : null,
                });
            }
            Add("AXIS_WHEEL_STEER", 900810, "Wheels");
            Add("AXIS_WHEEL_THROTTLE", 900781, "Wheels");
            Add("axis_EVA_translate_z", 900780, "EVA");
            Add("axis_EVA_translate_x", 900778, "EVA");
            Add("axis_EVA_translate_y", 900779, "EVA");
            Add("axis_EVA_pitch", 900828, "EVA");
            Add("axis_EVA_yaw", 900829, "EVA");
            Add("axis_EVA_roll", 900830, "EVA");
            Add("AXIS_CAMERA_HDG", 900877, "Camera");
            Add("AXIS_CAMERA_PITCH", 900878, "Camera");
            return axes;
        }

        private static void Add(string field, int localization, string group)
        {
            FieldInfo info = typeof(GameSettings).GetField(field, BindingFlags.Public | BindingFlags.Static);
            if (info == null || info.FieldType != typeof(AxisBinding))
            {
                CompatibilityLog.Warn("ksp-axis-" + field, "KSP has no axis " + field + " here: it is left out of the"
                                                           + " Axes tab.");
                return;
            }
            axes.Add(new Axis
            {
                Name = field,
                Title = Title(localization, KspKeyBindings.Readable(field)),
                Group = group,
                Get = () => info.GetValue(null) as AxisBinding,
            });
        }

        private static string Title(int localization, string fallback)
        {
            try
            {
                string text = Localizer.Format("#autoLOC_" + localization);
                if (!string.IsNullOrEmpty(text) && !text.StartsWith("#autoLOC", StringComparison.Ordinal)) return text;
            }
            catch (Exception)
            {
                // Outside the game: the field's name.
            }
            return fallback;
        }

        // A copy, as KSP's own screen makes one.
        internal static AxisBinding Copy(AxisBinding binding)
        {
            if (binding == null) return null;
            ConfigNode node = new ConfigNode();
            binding.Save(node);
            AxisBinding copy = new AxisBinding();
            copy.Load(node);
            return copy;
        }

        // The copy written into the binding KSP holds, as KSP's screen does it.
        internal static void Write(AxisBinding into, AxisBinding from)
        {
            if (into == null || from == null) return;
            ConfigNode node = new ConfigNode();
            from.Save(node);
            into.Load(node);
        }

        // Whether two bindings hold the same, as their config nodes say.
        internal static bool Same(AxisBinding a, AxisBinding b)
        {
            if (a == null || b == null) return a == b;
            ConfigNode left = new ConfigNode();
            ConfigNode right = new ConfigNode();
            a.Save(left);
            b.Save(right);
            return left.ToString() == right.ToString();
        }

        private static Dictionary<string, AxisBinding> defaults;

        // What KSP ships for every axis: GameSettings.SetDefaultValues assigns
        // every static field anew and does nothing else, so every field is saved,
        // the defaults are copied, and every field is put back -- the same objects
        // included (as KspKeyBindings reads KSP's default keys).
        internal static AxisBinding Default(Axis axis)
        {
            if (defaults == null)
            {
                defaults = new Dictionary<string, AxisBinding>();
                List<FieldInfo> fields = new List<FieldInfo>();
                foreach (FieldInfo field in typeof(GameSettings).GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                                                              | BindingFlags.Static))
                {
                    if (!field.IsLiteral && !field.IsInitOnly) fields.Add(field);
                }
                object[] saved = new object[fields.Count];
                for (int i = 0; i < fields.Count; i++) saved[i] = fields[i].GetValue(null);
                try
                {
                    GameSettings.SetDefaultValues();
                    foreach (Axis each in All()) defaults[each.Name] = Copy(each.Get());
                }
                catch (Exception e)
                {
                    defaults.Clear();
                    CompatibilityLog.Warn("ksp-axis-defaults", "KSP's default axes could not be read ("
                                                               + CompatibilityLog.Reason(e)
                                                               + "); the reset leaves them to KSP's own reset.");
                }
                finally
                {
                    for (int i = 0; i < fields.Count; i++) fields[i].SetValue(null, saved[i]);
                }
            }
            AxisBinding shipped;
            return defaults.TryGetValue(axis.Name, out shipped) ? Copy(shipped) : null;
        }
    }
}
