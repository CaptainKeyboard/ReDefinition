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
    // KSP's defaults are the ones its own reset sets: GameSettings.SetDefaultValues
    // assigns every static field of GameSettings anew -- 119 new KeyBindings among
    // them -- and does nothing else (its IL in KSP 1.12.5: stores to its own fields
    // and constructors without side effects). Every field is saved first, the
    // defaults are read, and every field is put back as it was, the same objects
    // included: nothing that holds one of KSP's bindings sees a change.
    internal static class KspKeyBindings
    {
        // Where a binding counts: a key used on EVA and one used flying a vessel
        // never meet.
        internal const int Flight = 1;
        internal const int Eva = 2;
        internal const int Editor = 4;
        internal const int Map = 8;
        internal const int Everywhere = Flight | Eva | Editor | Map;

        internal sealed class Binding
        {
            public string Name;
            public string Title;
            public string Group;
            public int Situations;
            public int Modes;
            public FieldInfo Field;

            public string Read(bool secondary)
            {
                KeyCode code = Code(Field.GetValue(null), secondary);
                return code == KeyCode.None ? KeyCombination.NoneText : code.ToString();
            }

            // Any key, a modifier among them: KSP binds LeftShift to the throttle.
            public void Write(bool secondary, string text)
            {
                object binding = Field.GetValue(null);
                if (binding == null) return;
                SetCode(binding, secondary, KeyCombination.ParseLoose(text).Key);
                Field.SetValue(null, binding);
            }

            // What KSP ships for it; null where it could not be read.
            public string Default(bool secondary)
            {
                string[] shipped;
                if (!Defaults().TryGetValue(Name, out shipped)) return null;
                return shipped[secondary ? 1 : 0];
            }

        }

        private static List<Binding> bindings;
        private static FieldInfo primary;
        private static FieldInfo secondary;
        private static FieldInfo codeField;
        private static Type keyCodeExtended;

        // Where a name that starts like this belongs, and where its binding counts.
        // KSP writes its field names in more than one way -- PITCH_DOWN,
        // EVA_forward, Editor_pitchUp, CustomActionGroup1 -- so they are matched
        // without regard to case. What matches nothing stands under General and
        // counts everywhere.
        private sealed class KeyGroup
        {
            public string Name;
            public int Situations;
            public string[] Prefixes;
        }

        private static readonly KeyGroup[] Groups =
        {
            // Flying a vessel -- also in map view, where it is still flown.
            new KeyGroup
            {
                Name = "Flight", Situations = Flight | Map,
                Prefixes = new[]
                {
                    "PITCH", "YAW", "ROLL", "THROTTLE", "TRANSLATE", "SAS", "RCS", "LAUNCH", "BRAKES", "LANDING_GEAR",
                    "HEADLIGHT", "PRECISION", "WHEEL", "CustomActionGroup", "AbortActionGroup", "AGROUP", "Docking",
                    "UIMODE", "TOGGLE_SPACENAV", "NAVBALL", "SCROLL_ICONS",
                },
            },
            new KeyGroup { Name = "EVA", Situations = Eva, Prefixes = new[] { "EVA" } },
            new KeyGroup { Name = "Editor", Situations = Editor, Prefixes = new[] { "Editor" } },
            new KeyGroup
            {
                Name = "Camera", Situations = Flight | Eva | Map,
                Prefixes = new[] { "CAMERA", "ZOOM", "SCROLL_VIEW" },
            },
            new KeyGroup
            {
                Name = "Map and vessels", Situations = Flight | Eva | Map,
                Prefixes = new[] { "MAP", "FOCUS" },
            },
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
                KeyGroup group = GroupFor(field.Name);
                bindings.Add(new Binding
                {
                    Name = field.Name,
                    Title = Readable(field.Name),
                    Group = group != null ? group.Name : "General",
                    Situations = group != null ? group.Situations : Everywhere,
                    // switchState tells flight modes apart -- staging, docking -- and
                    // means nothing beyond flight.
                    Modes = group != null && group.Name == "Flight" ? mask : -1,
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

        // PITCH_DOWN as "Pitch down", Editor_pitchUp as "Editor pitch up",
        // CustomActionGroup1 as "Custom action group 1": words apart, the first
        // letter upper case, the rest lower case. KSP's own texts for these rows
        // live in its settings screen's prefab, not beside the fields.
        internal static string Readable(string name)
        {
            System.Text.StringBuilder words = new System.Text.StringBuilder(name.Length + 8);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c == '_')
                {
                    words.Append(' ');
                    continue;
                }
                char before = i > 0 ? name[i - 1] : ' ';
                bool newWord = (char.IsUpper(c) && char.IsLower(before))
                               || (char.IsDigit(c) && char.IsLetter(before));
                if (newWord && words.Length > 0 && words[words.Length - 1] != ' ') words.Append(' ');
                words.Append(char.ToLowerInvariant(c));
            }
            string text = words.ToString().Trim();
            return text.Length == 0 ? name : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        internal static string GroupOf(string name)
        {
            KeyGroup group = GroupFor(name);
            return group != null ? group.Name : "General";
        }

        // The situations of one of KSP's groups by its name; everywhere for any
        // other section.
        internal static int SituationsOfGroup(string group)
        {
            foreach (KeyGroup candidate in Groups)
                if (string.Equals(candidate.Name, group, StringComparison.OrdinalIgnoreCase)) return candidate.Situations;
            return Everywhere;
        }

        // KSP's group names, in the order they stand, General last.
        internal static List<string> GroupOrderNames()
        {
            List<string> names = new List<string>();
            foreach (KeyGroup group in Groups) names.Add(group.Name);
            names.Add("General");
            return names;
        }

        internal static int SituationsOf(string name)
        {
            KeyGroup group = GroupFor(name);
            return group != null ? group.Situations : Everywhere;
        }

        private static KeyGroup GroupFor(string name)
        {
            foreach (KeyGroup group in Groups)
            {
                foreach (string prefix in group.Prefixes)
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return group;
            }
            return null;
        }

        private static int GroupOrder(string group)
        {
            for (int i = 0; i < Groups.Length; i++)
                if (Groups[i].Name == group) return i;
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

        private static Dictionary<string, string[]> defaults;

        // KSP's own defaults of every binding, by field name: primary and
        // secondary. Once per run -- they are KSP's code, not the player's.
        private static Dictionary<string, string[]> Defaults()
        {
            if (defaults != null) return defaults;
            defaults = new Dictionary<string, string[]>();
            if (All().Count == 0) return defaults;

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
                foreach (Binding binding in All())
                {
                    object shipped = binding.Field.GetValue(null);
                    defaults[binding.Name] = new[] { Text(Code(shipped, false)), Text(Code(shipped, true)) };
                }
            }
            catch (Exception e)
            {
                defaults.Clear();
                CompatibilityLog.Warn("ksp-binding-defaults", "KSP's default key bindings could not be read ("
                                                              + CompatibilityLog.Reason(e)
                                                              + "); the reset leaves KSP's bindings as they are.");
            }
            finally
            {
                // Everything as it was, whatever went wrong above.
                for (int i = 0; i < fields.Count; i++) fields[i].SetValue(null, saved[i]);
            }
            return defaults;
        }

        private static string Text(KeyCode code)
        {
            return code == KeyCode.None ? KeyCombination.NoneText : code.ToString();
        }

        // KSP writes its settings file itself; the values are read live from
        // GameSettings, so a change counts at once and is kept on the next save.
        internal static void Save()
        {
            GameSettings.SaveSettings();
        }
    }
}
