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
        // One of KSP's axes: its field, its title and its group in the Axes tab.
        internal sealed class Axis
        {
            // The GameSettings field (or AXIS_CUSTOM[i].axisBinding), the title in the game's language, and the group.
            public string Name;
            public string Title;
            public string Group;
            // The AxisBinding as GameSettings holds it now.
            public Func<AxisBinding> Get;
        }

        // The axes once listed.
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

        // Adds a static AxisBinding of GameSettings by its field name; one KSP lacks is warned about and left out.
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

        // KSP's text of that number, in the game's language; the fallback where it has none.
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

        // The fields AxisBinding_Single.Save writes of one side: name, axis, inv, sensitivity, deadzone, scale, group
        // (the lock mask) and modeMask (the switch state). Two sides that hold the same fields save the same node.
        private struct Saved
        {
            // The fields, as the side holds them.
            public string Name;
            public int AxisIdx;
            public bool Inverted;
            public float Sensitivity;
            public float Deadzone;
            public float Scale;
            public ulong LockMask;
            public InputBindingModes SwitchState;

            // The side's fields.
            public static Saved Of(AxisBinding_Single side)
            {
                return new Saved
                {
                    Name = side.name,
                    AxisIdx = side.axisIdx,
                    Inverted = side.inverted,
                    Sensitivity = side.sensitivity,
                    Deadzone = side.deadzone,
                    Scale = side.scale,
                    LockMask = side.inputLockMask,
                    SwitchState = side.switchState,
                };
            }

            // Whether the other holds the same fields, down to the sign of a zero, which a node writes.
            public bool Holds(Saved other)
            {
                return string.Equals(Name, other.Name, StringComparison.Ordinal) && AxisIdx == other.AxisIdx
                       && Inverted == other.Inverted && Alike(Sensitivity, other.Sensitivity)
                       && Alike(Deadzone, other.Deadzone) && Alike(Scale, other.Scale) && LockMask == other.LockMask
                       && SwitchState == other.SwitchState;
            }

            // Whether two numbers are the same one; NaN is none, which only costs a comparison.
            private static bool Alike(float a, float b)
            {
                return a == b && (a != 0f || 1f / a == 1f / b);
            }
        }

        // Whether two bindings hold the same, as Same says.
        // It is worked out again only when a field Save writes of either has changed.
        // The window compares every edited axis with KSP's in every frame, and Same builds two nodes and two
        // strings each time.
        internal sealed class Comparison
        {
            // The fields of both sides of each binding as of the last comparison, and what it found.
            private Saved liveFirst, liveSecond, copyFirst, copySecond;
            private bool made;
            private bool same;

            // Same(live, copy).
            public bool Of(AxisBinding live, AxisBinding copy)
            {
                if (live == null || copy == null) return Same(live, copy);
                Saved a = Saved.Of(live.primary);
                Saved b = Saved.Of(live.secondary);
                Saved c = Saved.Of(copy.primary);
                Saved d = Saved.Of(copy.secondary);
                if (made && a.Holds(liveFirst) && b.Holds(liveSecond) && c.Holds(copyFirst) && d.Holds(copySecond))
                    return same;
                same = Same(live, copy);
                liveFirst = a;
                liveSecond = b;
                copyFirst = c;
                copySecond = d;
                made = true;
                return same;
            }
        }

        // The shipped bindings by axis name, read once.
        private static Dictionary<string, AxisBinding> defaults;

        // What KSP ships for every axis (KspShipped), copied.
        internal static AxisBinding Default(Axis axis)
        {
            if (defaults == null)
            {
                defaults = new Dictionary<string, AxisBinding>();
                Exception e = KspShipped.Read(() =>
                {
                    foreach (Axis each in All()) defaults[each.Name] = Copy(each.Get());
                });
                if (e != null)
                {
                    defaults.Clear();
                    CompatibilityLog.Warn("ksp-axis-defaults", "KSP's default axes could not be read ("
                                                               + CompatibilityLog.Reason(e)
                                                               + "); the reset leaves them to KSP's own reset.");
                }
            }
            AxisBinding shipped;
            return defaults.TryGetValue(axis.Name, out shipped) ? Copy(shipped) : null;
        }
    }
}
