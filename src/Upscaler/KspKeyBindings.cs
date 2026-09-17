using System;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Framework;
using UnityEngine;

namespace ReDefinition
{
    // KSP's own key bindings, read from GameSettings rather than listed by hand:
    // every static KeyBinding field there (about 119 in KSP 1.12.5), each with a
    // primary and a secondary key.
    //
    // A KeyBinding holds one KeyCode per key, so a combination with modifiers
    // cannot be one of KSP's: the key is taken and the modifiers are left, and the
    // row says so. KSP's own modifier key is a binding of its own (MODIFIER_KEY).
    //
    // switchState says in which situations a binding counts (settings.cfg writes it
    // as modeMask): two that never count together are no conflict.
    //
    // KSP's defaults are not read here: its InputSettings names the same bindings
    // differently -- CAMERA_ORBIT_UP is viewOrbitUp, SAS_HOLD is sasMomentairly --
    // so only part of them could be matched, and a half reset of a keyboard layout
    // is worse than none. KSP's own settings screen resets them.
    internal static class KspKeyBindings
    {
        internal sealed class Binding
        {
            public string Name;
            public string Title;
            public string Group;
            public int Modes;
            public FieldInfo Field;

            public string Read(bool secondary)
            {
                KeyCode code = Code(Field.GetValue(null), secondary);
                return code == KeyCode.None ? KeyCombination.NoneText : code.ToString();
            }

            public void Write(bool secondary, string text)
            {
                object binding = Field.GetValue(null);
                if (binding == null) return;
                SetCode(binding, secondary, KeyCombination.Parse(text).Key);
                Field.SetValue(null, binding);
            }

        }

        private static List<Binding> bindings;
        private static FieldInfo primary;
        private static FieldInfo secondary;
        private static FieldInfo codeField;
        private static Type keyCodeExtended;

        // Where a name that starts like this belongs, as KSP's own input screen
        // groups them. What matches nothing stands under General.
        private static readonly string[][] Groups =
        {
            new[] { "Flight", "PITCH", "YAW", "ROLL", "THROTTLE", "TRANSLATE", "SAS", "RCS", "LAUNCH", "STAGE", "BRAKES",
                    "ABORT", "GEAR", "LIGHT", "PRECISION", "TIME_WARP", "WHEEL", "CUSTOM", "HEADLIGHT" },
            new[] { "Camera", "CAMERA", "ZOOM", "SCROLL_VIEW", "VIEW" },
            new[] { "EVA", "EVA" },
            new[] { "Editor", "EDITOR", "SYMMETRY", "SNAP", "CONSTRUCTION" },
            new[] { "Map", "MAP", "FOCUS", "TARGET", "NAVBALL", "SCROLL_ICONS" },
            new[] { "Ship control", "DOCKING", "TRANSLATION", "MODIFIER" },
        };

        internal static IList<Binding> All()
        {
            if (bindings != null) return bindings;
            bindings = new List<Binding>();
            Type settings = typeof(GameSettings);
            Type keyBinding = typeof(KeyBinding);
            primary = keyBinding.GetField("primary", HostStack.Any);
            secondary = keyBinding.GetField("secondary", HostStack.Any);
            FieldInfo modes = keyBinding.GetField("switchState", HostStack.Any);
            keyCodeExtended = primary != null ? primary.FieldType : null;
            codeField = keyCodeExtended != null ? keyCodeExtended.GetField("code", HostStack.Any) : null;
            if (primary == null || secondary == null || codeField == null)
            {
                CompatibilityLog.Warn("ksp-bindings", "KSP's key bindings are not the shape this build was written"
                                                      + " against: they are left out of the Keys tab.");
                return bindings;
            }

            foreach (FieldInfo field in settings.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType != keyBinding || field.IsLiteral) continue;
                object value = field.GetValue(null);
                if (value == null) continue;
                int mask = -1;
                if (modes != null)
                {
                    try
                    {
                        mask = Convert.ToInt32(modes.GetValue(value));
                    }
                    catch (Exception)
                    {
                        mask = -1;
                    }
                    if (mask == 0) mask = -1;
                }
                bindings.Add(new Binding
                {
                    Name = field.Name,
                    Title = Readable(field.Name),
                    Group = GroupOf(field.Name),
                    Modes = mask,
                    Field = field,
                });
            }
            bindings.Sort((a, b) =>
            {
                int group = GroupOrder(a.Group).CompareTo(GroupOrder(b.Group));
                return group != 0 ? group : string.CompareOrdinal(a.Title, b.Title);
            });
            return bindings;
        }

        // The groups in the order their rows stand.
        internal static List<string> GroupNames()
        {
            List<string> names = new List<string>();
            foreach (Binding binding in All())
                if (!names.Contains(binding.Group)) names.Add(binding.Group);
            return names;
        }

        // PITCH_DOWN as "Pitch down": KSP's own texts for these rows live in its
        // settings screen's prefab, not beside the fields.
        internal static string Readable(string name)
        {
            string text = name.Replace('_', ' ').ToLowerInvariant();
            return text.Length == 0 ? name : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        internal static string GroupOf(string name)
        {
            foreach (string[] group in Groups)
            {
                for (int i = 1; i < group.Length; i++)
                    if (name.StartsWith(group[i], StringComparison.Ordinal)) return group[0];
            }
            return "General";
        }

        private static int GroupOrder(string group)
        {
            for (int i = 0; i < Groups.Length; i++)
                if (Groups[i][0] == group) return i;
            return Groups.Length;
        }

        private static KeyCode Code(object binding, bool second)
        {
            if (binding == null) return KeyCode.None;
            object extended = (second ? secondary : primary).GetValue(binding);
            if (extended == null) return KeyCode.None;
            return (KeyCode)codeField.GetValue(extended);
        }

        private static void SetCode(object binding, bool second, KeyCode code)
        {
            FieldInfo which = second ? secondary : primary;
            object extended = which.GetValue(binding);
            if (extended == null) extended = Activator.CreateInstance(keyCodeExtended);
            codeField.SetValue(extended, code);
            which.SetValue(binding, extended);
        }

        // KSP writes its settings file itself; the values are read live from
        // GameSettings, so a change counts at once and is kept on the next save.
        internal static void Save()
        {
            GameSettings.SaveSettings();
        }
    }
}
