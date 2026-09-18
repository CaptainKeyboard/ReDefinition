using System;
using System.Collections.Generic;
using ReDefinition.Framework;
using UnityEngine;

namespace ReDefinition
{
    // The Keys tab: every binding a player has -- ReDefinition's own, the bundled
    // mods' and KSP's -- with a search field above them, since KSP alone brings
    // about 119.
    //
    // A row shows its key and takes a new one: clicked, it listens (KeyCapture),
    // the next key pressed is taken, Escape cancels, and the button beside it
    // clears the binding. The modifiers are switches beside it, each clicked
    // through off, left and right: pressed modifiers are not taken, since Windows
    // turns AltGr into Ctrl and right Alt at once, and a row with its modifiers in
    // switches stays as narrow as its key.
    //
    // A combination two rows share is shown in yellow on both, and nothing is
    // refused (Conflicts).
    //
    // What the rows show is worked out again only after a change, not in every
    // frame: the tab holds about 250 rows, and each would otherwise read KSP's
    // bindings by reflection and parse its text in every frame.
    internal static partial class SettingsWindow
    {
        private const float BindingWidth = 90f;
        private const float BindingButtonWidth = 22f;
        private const float ModifierWidth = 50f;
        // Narrower than the other tabs': the switches stand beside every row.
        private const float KeyNameWidth = 168f;
        private const float KeySourceWidth = 52f;
        private const string ListeningText = "<color=#ffdd55>Press a key...</color>";

        private static string keySearch = "";

        // Counts every change to a binding in the tab: what the rows show is kept
        // until it moves. Also moved once a second, for a value a mod's own window
        // changed meanwhile.
        private static int keysVersion;
        private static float keysVersionTime;

        private struct Shown
        {
            public int Version;
            public string Text;
        }

        private static readonly Dictionary<string, Shown> shownTexts = new Dictionary<string, Shown>();

        private static void KeysChanged()
        {
            keysVersion++;
        }

        private static int KeysVersion()
        {
            if (Time.unscaledTime >= keysVersionTime)
            {
                keysVersionTime = Time.unscaledTime + 1f;
                keysVersion++;
            }
            return keysVersion;
        }

        // A row's binding as text, read again only after a change.
        private static string CachedText(string key, Func<string> read)
        {
            int version = KeysVersion();
            Shown shown;
            if (shownTexts.TryGetValue(key, out shown) && shown.Version == version) return shown.Text;
            string text;
            try
            {
                text = read() ?? KeyCombination.NoneText;
            }
            catch (Exception)
            {
                text = KeyCombination.NoneText;
            }
            shownTexts[key] = new Shown { Version = version, Text = text };
            return text;
        }

        // Both a row's own name and its mod's are searched: "camera" finds KSP's
        // camera rows, "scatterer" the mod's.
        private static bool MatchesSearch(string title, string owner)
        {
            if (keySearch.Length == 0) return true;
            return (title != null && title.IndexOf(keySearch, StringComparison.OrdinalIgnoreCase) >= 0)
                   || (owner != null && owner.IndexOf(keySearch, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static DialogGUIBase[] KeyRows()
        {
            shownTexts.Clear();
            groupsShownFor = null;
            KeysChanged();
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            rows.Add(new DialogGUIHorizontalLayout(0f, RowHeight + 6f, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel("Search", KeyNameWidth),
                new DialogGUITextInput("", false, 64, text =>
                {
                    keySearch = text ?? "";
                    return text;
                }, 160f, RowHeight + 6f)));

            foreach (ModuleSetting setting in OurModules.Rows(SettingCategory.Keys))
                rows.Add(OwnBindingRow(setting));
            return rows.ToArray();
        }

        // KSP's own bindings, after ReDefinition's and the mods'. They are edited
        // here and written on Apply, as everything else in this window is.
        private static readonly Dictionary<string, string> kspPending = new Dictionary<string, string>();

        private static DialogGUIBase[] KspKeyRows()
        {
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            string group = null;
            foreach (KspKeyBindings.Binding binding in KspKeyBindings.All())
            {
                if (binding.Group != group)
                {
                    group = binding.Group;
                    string title = group;
                    DialogGUIBase header = new DialogGUIBox(title, -1f, RowHeight, null);
                    header.OptionEnabledCondition = () => GroupShown(title);
                    rows.Add(header);
                }
                rows.Add(KspBindingRow(binding));
            }
            return rows.ToArray();
        }

        // The groups a search leaves rows in, worked out once per search.
        private static string groupsShownFor;
        private static readonly HashSet<string> groupsShown = new HashSet<string>();

        // A group's title stands only while one of its rows does.
        private static bool GroupShown(string group)
        {
            if (keySearch.Length == 0) return true;
            if (groupsShownFor != keySearch)
            {
                groupsShownFor = keySearch;
                groupsShown.Clear();
                foreach (KspKeyBindings.Binding binding in KspKeyBindings.All())
                    if (MatchesSearch(binding.Title, "KSP")) groupsShown.Add(binding.Group);
            }
            return groupsShown.Contains(group);
        }

        private static DialogGUIBase KspBindingRow(KspKeyBindings.Binding binding)
        {
            KspKeyBindings.Binding shown = binding;
            DialogGUIBase first = KspBindingControl(shown, false);
            DialogGUIBase second = KspBindingControl(shown, true);
            DialogGUIBase row = new DialogGUIHorizontalLayout(0f, RowHeight + 4f, 0f, new RectOffset(),
                TextAnchor.MiddleLeft, new DialogGUILabel(shown.Title, KeyNameWidth), first, new DialogGUISpace(6f),
                second, new DialogGUISpace(4f),
                new DialogGUILabel("<color=#9a9a9a>KSP</color>", KeySourceWidth));
            row.OptionEnabledCondition = () => MatchesSearch(shown.Title, "KSP");
            return row;
        }

        // One of the two keys KSP keeps per binding. A KeyBinding holds one KeyCode
        // and no modifier -- KSP's own modifier key is a binding of its own.
        private static DialogGUIBase KspBindingControl(KspKeyBindings.Binding binding, bool second)
        {
            KspKeyBindings.Binding shown = binding;
            string key = "ksp." + shown.Name + (second ? ".secondary" : ".primary");
            Func<string> current = () => CachedText(key, () =>
            {
                string pending;
                return kspPending.TryGetValue(key, out pending) ? pending : shown.Read(second);
            });
            Conflicts.Register(key, current, shown.Modes);

            Func<string> label = () => KeyCapture.Listening(key) ? ListeningText : BindingLabel(key, current());
            DialogGUIButton take = new DialogGUIButton(label, () => KeyCapture.Start(key, text =>
            {
                kspPending[key] = text;
                KeysChanged();
            }), BindingWidth, RowHeight + 4f, false);
            take.tooltipText = (second ? "The second key for " : "The key for ") + shown.Title
                               + ".\nKSP keeps one key per binding; its own modifier key is a binding of its own."
                               + "\nClick, then press the key. Escape cancels; x clears it.";

            DialogGUIButton clear = new DialogGUIButton("x", () =>
            {
                if (KeyCapture.Listening(key)) KeyCapture.Stop();
                kspPending[key] = KeyCombination.NoneText;
                KeysChanged();
            }, BindingButtonWidth, RowHeight + 4f, false);

            return new DialogGUIHorizontalLayout(0f, RowHeight + 4f, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                take, new DialogGUISpace(4f), clear);
        }

        // On Apply: into GameSettings, then KSP's own save.
        private static void ApplyKeyBindings()
        {
            KeysChanged();
            if (kspPending.Count == 0) return;
            bool written = false;
            foreach (KspKeyBindings.Binding binding in KspKeyBindings.All())
            {
                written |= Write(binding, false);
                written |= Write(binding, true);
            }
            kspPending.Clear();
            if (!written) return;
            try
            {
                KspKeyBindings.Save();
            }
            catch (Exception e)
            {
                Debug.LogWarning(UpscalerProbe.Tag + " KSP's key bindings could not be saved: " + e);
            }
        }

        private static bool Write(KspKeyBindings.Binding binding, bool second)
        {
            string key = "ksp." + binding.Name + (second ? ".secondary" : ".primary");
            string text;
            if (!kspPending.TryGetValue(key, out text)) return false;
            try
            {
                binding.Write(second, text);
                return true;
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("ksp-binding-" + key, binding.Title + ": the binding could not be set ("
                                                            + CompatibilityLog.Reason(e) + ").");
                return false;
            }
        }

        // Reset to defaults: ReDefinition's own bindings and the mods' go back to
        // their defaults with their other settings, and KSP's to what KSP ships
        // (KspKeyBindings.Defaults). Filled in only: Apply writes them.
        internal static void ResetKeyBindings()
        {
            kspPending.Clear();
            foreach (KspKeyBindings.Binding binding in KspKeyBindings.All())
            {
                ResetOne(binding, false);
                ResetOne(binding, true);
            }
            KeysChanged();
        }

        private static void ResetOne(KspKeyBindings.Binding binding, bool second)
        {
            string shipped = binding.Default(second);
            if (shipped == null) return;
            kspPending["ksp." + binding.Name + (second ? ".secondary" : ".primary")] = shipped;
        }

        // ReDefinition's own hotkeys, over the settings copy the window edits.
        private static DialogGUIBase OwnBindingRow(ModuleSetting setting)
        {
            ModuleSetting shown = setting;
            string key = "redefinition." + shown.Key;
            Func<string> text = () => CachedText(key, () => shown.Read(edit.After));
            DialogGUIBase row = BindingRow(key, shown.Title, shown.Tooltip, "ReDefinition", text,
                value => shown.Write(edit.After, value), () => true, 2);
            row.OptionEnabledCondition = () => MatchesSearch(shown.Title, "ReDefinition");
            return row;
        }

        // A bundled mod's binding, over the edit model like its other settings. How
        // many modifiers it can hold is the mod's: Scatterer keeps one beside each of
        // its keys.
        private static DialogGUIBase BundledBindingRow(BundledSetting setting)
        {
            BundledSetting shown = setting;
            string key = shown.Key;
            Func<string> text = () => CachedText(key, () =>
            {
                string pending;
                return model.TryGetPending(key, out pending) ? pending : KeyCombination.NoneText;
            });
            DialogGUIBase row = BindingRow(key, shown.Title, shown.Tooltip, shown.Owner.ModName, text,
                value => model.Change(key, value),
                () => model.Bundled && model.HasPending(key), shown.MaxModifiers);
            row.OptionEnabledCondition = () => MatchesSearch(shown.Title, shown.Owner.ModName);
            return row;
        }

        // The modifiers a row switches, each with its right-hand counterpart.
        private static readonly KeyCode[][] ModifierPairs =
        {
            new[] { KeyCode.LeftControl, KeyCode.RightControl },
            new[] { KeyCode.LeftAlt, KeyCode.RightAlt },
            new[] { KeyCode.LeftShift, KeyCode.RightShift },
        };

        private static readonly string[] ModifierLabels = { "Ctrl", "Alt", "Shift" };

        // The row itself: the name, the key as a button that listens when it is
        // clicked, a button that clears it, switches for the modifiers, and where it
        // comes from.
        private static DialogGUIBase BindingRow(string key, string title, string tooltip, string owner,
                                                Func<string> current, Action<string> set, Func<bool> changeable,
                                                int maxModifiers)
        {
            // ReDefinition's and the mods' bindings count in every situation.
            Conflicts.Register(key, current, -1);
            Action<string> write = text =>
            {
                set(text);
                KeysChanged();
            };
            // A new key keeps the modifiers the switches hold.
            Action<string> taken = text =>
                write(KeyCombination.Parse(current()).WithKey(KeyCombination.Parse(text).Key).ToString());
            Func<string> label = () => KeyCapture.Listening(key)
                ? ListeningText
                : BindingLabel(key, KeyText(KeyCombination.Parse(current())));
            DialogGUIButton take = new DialogGUIButton(label, () => KeyCapture.Start(key, taken), BindingWidth,
                RowHeight + 4f, false);
            take.OptionInteractableCondition = changeable;
            take.tooltipText = (string.IsNullOrEmpty(tooltip) ? title : tooltip)
                               + "\nClick, then press the key; the switches beside it set the modifiers."
                               + "\nEscape cancels; x clears the binding."
                               + (maxModifiers < 2 ? "\nThis mod keeps one modifier beside the key." : "");

            DialogGUIButton clear = new DialogGUIButton("x", () =>
            {
                if (KeyCapture.Listening(key)) KeyCapture.Stop();
                write(KeyCombination.NoneText);
            }, BindingButtonWidth, RowHeight + 4f, false);
            clear.OptionInteractableCondition = changeable;
            clear.tooltipText = "Clears this binding.";

            List<DialogGUIBase> row = new List<DialogGUIBase>
            {
                new DialogGUILabel(title, KeyNameWidth), take, new DialogGUISpace(4f), clear, new DialogGUISpace(4f),
            };
            for (int i = 0; i < ModifierPairs.Length; i++)
            {
                row.Add(ModifierSwitch(key, ModifierPairs[i], ModifierLabels[i], current, write, changeable,
                    maxModifiers));
            }
            row.Add(new DialogGUISpace(4f));
            row.Add(new DialogGUILabel("<color=#9a9a9a>" + owner + "</color>", KeySourceWidth));
            return new DialogGUIHorizontalLayout(0f, RowHeight + 4f, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                row.ToArray());
        }

        // One modifier, clicked through off, left and right. Grey where the binding
        // holds as many modifiers as its mod can keep and this is none of them.
        private static DialogGUIBase ModifierSwitch(string key, KeyCode[] pair, string label, Func<string> current,
                                                    Action<string> write, Func<bool> changeable, int maxModifiers)
        {
            KeyCode left = pair[0];
            KeyCode right = pair[1];
            string leftText = "<color=#ffdd55>L " + label + "</color>";
            string rightText = "<color=#ffdd55>R " + label + "</color>";
            Func<string> text = () =>
            {
                KeyCombination now = KeyCombination.Parse(current());
                if (now.HasModifier(left)) return leftText;
                return now.HasModifier(right) ? rightText : label;
            };
            Func<bool> fits = () =>
            {
                KeyCombination now = KeyCombination.Parse(current());
                return now.HasModifier(left) || now.HasModifier(right) || now.ModifierCount < maxModifiers;
            };
            DialogGUIButton button = new DialogGUIButton(text, () =>
            {
                KeyCombination now = KeyCombination.Parse(current());
                if (!now.IsBound || !fits()) return;
                // The row is no longer listening for a key: this is the answer.
                if (KeyCapture.Listening(key)) KeyCapture.Stop();
                write(now.Cycled(left, right).ToString());
            }, ModifierWidth, RowHeight + 4f, false);
            button.OptionInteractableCondition = () => changeable() && fits()
                                                       && KeyCombination.Parse(current()).IsBound;
            button.tooltipText = label + ": click once for the left key, again for the right one, a third time for"
                                 + " none.\nGrey where the binding holds as many modifiers as it can.";
            return button;
        }

        // The key alone: the modifiers stand in their switches.
        private static string KeyText(KeyCombination combination)
        {
            return combination.IsBound ? combination.Key.ToString() : KeyCombination.NoneText;
        }

        // What the button shows: the key, in yellow where another row has the same
        // combination.
        private static string BindingLabel(string key, string text)
        {
            string shown = string.IsNullOrEmpty(text) ? KeyCombination.NoneText : text;
            if (!Conflicts.Shares(key, keysVersion)) return shown;
            // Kept, not joined again in every frame.
            string yellow;
            if (yellowLabels.TryGetValue(shown, out yellow)) return yellow;
            yellow = "<color=#ffdd55>" + shown + "</color>";
            yellowLabels[shown] = yellow;
            return yellow;
        }

        private static readonly Dictionary<string, string> yellowLabels = new Dictionary<string, string>();
    }
}
