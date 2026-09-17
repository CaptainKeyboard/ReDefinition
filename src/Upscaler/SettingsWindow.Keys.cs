using System;
using System.Collections.Generic;
using ReDefinition.Framework;
using UnityEngine;

namespace ReDefinition
{
    // The Keys tab: every binding a player has -- ReDefinition's own, the bundled
    // mods' and KSP's -- with a search field above them, since KSP alone brings
    // about 127.
    //
    // A row shows its binding and takes a new one: clicked, it listens (KeyCapture),
    // the next combination of up to two modifiers and one key is taken, Escape
    // cancels, and the button beside it clears the binding. The modifiers can be
    // switched afterwards for a combination the system swallows before the game
    // sees it.
    //
    // A combination two rows share is shown in yellow on both, and nothing is
    // refused (Conflicts).
    internal static partial class SettingsWindow
    {
        private const float BindingWidth = 150f;
        private const float BindingButtonWidth = 24f;
        private const string ListeningText = "<color=#ffdd55>Press a key...</color>";

        private static string keySearch = "";

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
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            rows.Add(new DialogGUIHorizontalLayout(0f, RowHeight + 6f, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel("Search", NameWidth),
                new DialogGUITextInput("", false, 64, text =>
                {
                    keySearch = text ?? "";
                    return text;
                }, BindingWidth, RowHeight + 6f)));

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

        // A group's title stands only while one of its rows does.
        private static bool GroupShown(string group)
        {
            if (keySearch.Length == 0) return true;
            foreach (KspKeyBindings.Binding binding in KspKeyBindings.All())
                if (binding.Group == group && MatchesSearch(binding.Title, "KSP")) return true;
            return false;
        }

        private static DialogGUIBase KspBindingRow(KspKeyBindings.Binding binding)
        {
            KspKeyBindings.Binding shown = binding;
            DialogGUIBase first = KspBindingControl(shown, false);
            DialogGUIBase second = KspBindingControl(shown, true);
            DialogGUIBase row = new DialogGUIHorizontalLayout(0f, RowHeight + 4f, 0f, new RectOffset(),
                TextAnchor.MiddleLeft, new DialogGUILabel(shown.Title, NameWidth), first, new DialogGUISpace(6f), second,
                new DialogGUISpace(10f), new DialogGUILabel("<color=#9a9a9a>KSP</color>", SourceWidth));
            row.OptionEnabledCondition = () => MatchesSearch(shown.Title, "KSP");
            return row;
        }

        // One of the two keys KSP keeps per binding. Modifiers are left out: a
        // KeyBinding holds one KeyCode.
        private static DialogGUIBase KspBindingControl(KspKeyBindings.Binding binding, bool second)
        {
            KspKeyBindings.Binding shown = binding;
            string key = "ksp." + shown.Name + (second ? ".secondary" : ".primary");
            Func<string> current = () =>
            {
                string pending;
                return kspPending.TryGetValue(key, out pending) ? pending : shown.Read(second);
            };
            Conflicts.Register(key, current, shown.Modes);

            Func<string> label = () => KeyCapture.Listening(key) ? ListeningText : BindingLabel(key, current());
            DialogGUIButton take = new DialogGUIButton(label,
                () => KeyCapture.Start(key, text => kspPending[key] = KeyCombination.Parse(text).Key.ToString()),
                BindingWidth * 0.6f, RowHeight + 4f, false);
            take.tooltipText = (second ? "The second key for " : "The key for ") + shown.Title
                               + ".\nKSP keeps one key per binding: modifiers are left out, and its own modifier key"
                               + " is a binding of its own.\nClick, then press the key. Escape cancels; x clears it.";

            DialogGUIButton clear = new DialogGUIButton("x", () =>
            {
                if (KeyCapture.Listening(key)) KeyCapture.Stop();
                kspPending[key] = KeyCombination.NoneText;
            }, BindingButtonWidth, RowHeight + 4f, false);

            return new DialogGUIHorizontalLayout(0f, RowHeight + 4f, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                take, new DialogGUISpace(4f), clear);
        }

        // On Apply: into GameSettings, then KSP's own save.
        private static void ApplyKeyBindings()
        {
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

        // Reset to defaults: KSP's bindings go back to what KSP ships, where its
        // defaults can be read.
        internal static void ResetKeyBindings()
        {
            foreach (KspKeyBindings.Binding binding in KspKeyBindings.All())
            {
                ResetOne(binding, false);
                ResetOne(binding, true);
            }
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
            Func<string> text = () => shown.Read(edit.After);
            DialogGUIBase row = BindingRow(key, shown.Title, shown.Tooltip, "ReDefinition", text,
                value => shown.Write(edit.After, value), () => true);
            row.OptionEnabledCondition = () => MatchesSearch(shown.Title, "ReDefinition");
            return row;
        }

        // A bundled mod's binding, over the edit model like its other settings.
        private static DialogGUIBase BundledBindingRow(BundledSetting setting)
        {
            BundledSetting shown = setting;
            string key = shown.Key;
            Func<string> text = () =>
            {
                string pending;
                return model.TryGetPending(key, out pending) ? pending : KeyCombination.NoneText;
            };
            DialogGUIBase row = BindingRow(key, shown.Title, shown.Tooltip, shown.Owner.ModName, text,
                value => model.Change(key, value),
                () => model.Bundled && model.HasPending(key));
            row.OptionEnabledCondition = () => MatchesSearch(shown.Title, shown.Owner.ModName);
            return row;
        }

        // The row itself: the name, the binding as a button that listens when it is
        // clicked, a button that clears it, and where it comes from.
        private static DialogGUIBase BindingRow(string key, string title, string tooltip, string owner,
                                                Func<string> current, Action<string> set, Func<bool> changeable)
        {
            // ReDefinition's and the mods' bindings count in every situation.
            Conflicts.Register(key, current, -1);
            Func<string> label = () => KeyCapture.Listening(key) ? ListeningText : BindingLabel(key, current());
            DialogGUIButton take = new DialogGUIButton(label, () => KeyCapture.Start(key, set), BindingWidth,
                RowHeight + 4f, false);
            take.OptionInteractableCondition = changeable;
            take.tooltipText = (string.IsNullOrEmpty(tooltip) ? title : tooltip)
                               + "\nClick, then press the combination. Escape cancels; x clears the binding.";

            DialogGUIButton clear = new DialogGUIButton("x", () =>
            {
                if (KeyCapture.Listening(key)) KeyCapture.Stop();
                set(KeyCombination.NoneText);
            }, BindingButtonWidth, RowHeight + 4f, false);
            clear.OptionInteractableCondition = changeable;
            clear.tooltipText = "Clears this binding.";

            return new DialogGUIHorizontalLayout(0f, RowHeight + 4f, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel(title, NameWidth), take, new DialogGUISpace(6f), clear, new DialogGUISpace(10f),
                new DialogGUILabel("<color=#9a9a9a>" + owner + "</color>", SourceWidth));
        }

        // What the button shows: the combination, in yellow where another row has
        // the same one.
        private static string BindingLabel(string key, string text)
        {
            string shown = string.IsNullOrEmpty(text) ? KeyCombination.NoneText : text;
            return Conflicts.Shares(key) ? "<color=#ffdd55>" + shown + "</color>" : shown;
        }
    }
}
