using System.Collections.Generic;
using System;
using ReDefinition.Core;
using ReDefinition.Modules;
using ReDefinition.Settings;
using ReDefinition.Ui;
using UnityEngine;
using static ReDefinition.Window.SettingsWindow;

namespace ReDefinition.Window
{
    // The Keys tab: every binding a player has -- ReDefinition's own, the bundled
    // mods' and KSP's -- with a search field above them, since KSP alone brings
    // about 119.
    //
    // A row shows its key and takes a new one: clicked, it listens (KeyCapture),
    // the next key pressed is taken, Escape cancels, and the button beside it
    // clears the binding.
    // On the rows of ReDefinition and the mods, the modifiers are switches beside it, each clicked through off,
    // left and right. Pressed modifiers are not taken there, since Windows
    // turns AltGr into Ctrl and right Alt at once, and a row with its modifiers in
    // switches stays as narrow as its key. KSP's own rows have no switches: a modifier is a key of its own there.
    //
    // A combination two rows share is shown in yellow on both, and nothing is
    // refused (Conflicts).
    //
    // What the rows show is worked out again only after a change, not in every
    // frame: the tab holds about 250 rows, and each would otherwise read KSP's
    // bindings by reflection and parse its text in every frame.
    internal static class KeysTab
    {
        // The widths of a binding's key button, of its clear button and of a modifier switch.
        private const float BindingWidth = 90f;
        private const float BindingButtonWidth = 22f;
        private const float ModifierWidth = 50f;
        // Narrower than the other tabs': the switches stand beside every row.
        private const float KeyNameWidth = 168f;
        private const float KeySourceWidth = 52f;
        private const string ListeningText = "<color=#ffdd55>Press a key...</color>";

        // Counts every change to a binding in the tab: what the rows show is kept
        // until it moves. Also moved, at most once a second, when a value a mod's own window
        // changed meanwhile shows in a row (KeysVersion).
        private static int keysVersion;
        private static float keysVersionTime;

        // A row's text as last read, how to read it again, and the version of the bindings it was read at.
        private struct Shown
        {
            // The version it was read at, the text, and what reads it.
            public int Version;
            public string Text;
            public Func<string> Read;
        }

        // The texts last read, by the row's key.
        private static readonly Dictionary<string, Shown> shownTexts = new Dictionary<string, Shown>();

        // Marks every row's text as due to be read again.
        internal static void Changed()
        {
            keysVersion++;
        }

        // The bindings' version. Once a second every text is read again, so that a value changed elsewhere shows,
        // and the version moves only where one of them differs: the rows and the conflicts are worked out again then
        // and not otherwise.
        private static int KeysVersion()
        {
            if (Time.unscaledTime >= keysVersionTime)
            {
                keysVersionTime = Time.unscaledTime + 1f;
                if (ShownTextsDiffer()) keysVersion++;
            }
            return keysVersion;
        }

        // Reads every text a row showed again; true where one is another text now. The ones read at the version
        // in force stay current, and the others are read when their row asks (CachedText).
        private static bool ShownTextsDiffer()
        {
            bool differs = false;
            foreach (string key in new List<string>(shownTexts.Keys))
            {
                Shown shown = shownTexts[key];
                string text = ReadText(shown.Read);
                if (text == shown.Text) continue;
                differs = true;
                shown.Text = text;
                shownTexts[key] = shown;
            }
            return differs;
        }

        // A binding's text from its reader; None where it throws or gives nothing.
        private static string ReadText(Func<string> read)
        {
            try
            {
                return read() ?? KeyCombination.NoneText;
            }
            catch (Exception)
            {
                return KeyCombination.NoneText;
            }
        }

        // A row's binding as text, read again only after a change.
        private static string CachedText(string key, Func<string> read)
        {
            int version = KeysVersion();
            Shown shown;
            if (shownTexts.TryGetValue(key, out shown) && shown.Version == version) return shown.Text;
            string text = ReadText(read);
            shownTexts[key] = new Shown { Version = version, Text = text, Read = read };
            return text;
        }

        // The Keys tab's rows: every binding by its section, each section under a header that folds it.
        internal static DialogGUIBase[] Rows()
        {
            shownTexts.Clear();
            groupsShownFor = null;
            Changed();
            List<DialogGUIBase> rows = new List<DialogGUIBase>();

            // Every binding by its section: ReDefinition's and the mods' under Mods
            // unless a registration names another (its KEY block's `group`), KSP's
            // under its groups. Mods first, then KSP's groups, then sections of the
            // mods' own.
            sectionMembers.Clear();
            Dictionary<string, List<Func<DialogGUIBase>>> sections = new Dictionary<string, List<Func<DialogGUIBase>>>(
                StringComparer.OrdinalIgnoreCase);
            List<string> order = new List<string> { BundledSetting.ModsGroup };
            order.AddRange(KspKeyBindings.GroupOrderNames());

            foreach (ModuleSetting setting in OurModules.Rows(SettingCategory.Keys))
            {
                ModuleSetting own = setting;
                AddToSection(sections, order, BundledSetting.ModsGroup, own.Title, "ReDefinition",
                    () => OwnBindingRow(own));
            }
            foreach (BundledSetting setting in WindowLayout.In(SettingCategory.Keys))
            {
                BundledSetting bundled = setting;
                // A setting of another kind a registration places here -- a switch
                // that belongs beside its mod's keys -- stands under Mods with its
                // usual control. One with none to draw has no row, as in any tab.
                bool binding = bundled.Control == SettingControl.Binding;
                if (!binding && bundled.Control == SettingControl.Value) continue;
                shownKeys.Add(bundled.Key);
                SettingsTabs.NoteKey(SettingCategory.Keys, bundled.Key);
                AddToSection(sections, order, binding ? bundled.Group : BundledSetting.ModsGroup, bundled.Title,
                    bundled.Owner.ModName, () => binding ? BundledBindingRow(bundled, true) : BundledRows.Row(bundled));
            }
            foreach (KspKeyBindings.Binding binding in KspKeyBindings.All())
            {
                KspKeyBindings.Binding ksp = binding;
                AddToSection(sections, order, ksp.Group, ksp.Title, "KSP", () => KspBindingRow(ksp));
            }

            foreach (string name in order)
            {
                List<Func<DialogGUIBase>> members;
                if (!sections.TryGetValue(name, out members) || members.Count == 0) continue;
                string section = SectionName(name);
                // Built first: a setting with nothing to draw -- a list whose choices
                // only the running mod knows, before it has them -- has no row.
                List<DialogGUIBase> built = new List<DialogGUIBase>();
                foreach (Func<DialogGUIBase> build in members)
                {
                    DialogGUIBase row = build();
                    if (row == null) continue;
                    Func<bool> matches = row.OptionEnabledCondition;
                    row.OptionEnabledCondition = () => GroupOpen(section) && (matches == null || matches());
                    built.Add(row);
                }
                if (built.Count == 0) continue;
                rows.Add(GroupHeader(section, built.Count));
                rows.AddRange(built);
            }
            return rows.ToArray();
        }

        // What a search looks through, per section: each row's name and where it
        // comes from.
        private static readonly Dictionary<string, List<string[]>> sectionMembers =
            new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase);

        // Adds a row's builder to its section, keeping the sections' order, and notes its name and source for the search.
        private static void AddToSection(Dictionary<string, List<Func<DialogGUIBase>>> sections, List<string> order,
                                         string section, string title, string owner, Func<DialogGUIBase> build)
        {
            string name = string.IsNullOrEmpty(section) ? BundledSetting.ModsGroup : section;
            List<Func<DialogGUIBase>> members;
            if (!sections.TryGetValue(name, out members))
            {
                members = new List<Func<DialogGUIBase>>();
                sections[name] = members;
                if (!order.Exists(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase)))
                    order.Add(name);
            }
            members.Add(build);

            string key = SectionName(name);
            List<string[]> names;
            if (!sectionMembers.TryGetValue(key, out names))
            {
                names = new List<string[]>();
                sectionMembers[key] = names;
            }
            names.Add(new[] { title, owner });
            SettingsSearch.NotePageText(SettingCategory.Keys, title + " " + owner + " " + key);
        }

        // A section as its header shows it: KSP's own spelling where it is one of
        // KSP's groups, whatever case a registration wrote.
        private static string SectionName(string name)
        {
            if (string.Equals(name, BundledSetting.ModsGroup, StringComparison.OrdinalIgnoreCase))
                return BundledSetting.ModsGroup;
            foreach (string known in KspKeyBindings.GroupOrderNames())
                if (string.Equals(known, name, StringComparison.OrdinalIgnoreCase)) return known;
            return name;
        }

        // KSP's own bindings are edited here and written on Apply, as everything
        // else in this window is.
        internal static readonly Dictionary<string, string> Pending = new Dictionary<string, string>();

        // The sections, each folded until it is opened -- Mods open at first: a row
        // of a folded section is inactive, and costs nothing while the window is
        // dragged. All of KSP's rows at once are about a thousand elements, which
        // Unity would lay out again for every step of a drag. A search shows its
        // matches in every section. Kept as the player left them for the run.
        private static readonly HashSet<string> openGroups =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BundledSetting.ModsGroup };

        // A section's header: a button that opens and folds it, with how many
        // bindings it holds.
        private static DialogGUIBase GroupHeader(string group, int count)
        {
            string title = group;
            DialogGUIButton header = DialogRows.FoldHeader(title, () => GroupOpen(title), () =>
            {
                if (!openGroups.Remove(title)) openGroups.Add(title);
            }, PageWidth - 60f, RowHeight + 6f,
                "Opens or folds the " + title + " bindings. A search shows its matches in every section.");
            header.OptionEnabledCondition = () => GroupShown(title);
            header.OptionInteractableCondition = () => !SettingsSearch.Searching;
            return header;
        }

        // Whether a section's rows stand: while it is open, or while something is searched.
        private static bool GroupOpen(string group)
        {
            return SettingsSearch.Searching || openGroups.Contains(group);
        }

        // The groups a search leaves rows in, worked out once per search.
        private static string groupsShownFor;
        private static readonly HashSet<string> groupsShown = new HashSet<string>();

        // A section's title stands only while one of its rows does.
        private static bool GroupShown(string group)
        {
            if (!SettingsSearch.Searching) return true;
            if (groupsShownFor != SettingsSearch.Text)
            {
                groupsShownFor = SettingsSearch.Text;
                groupsShown.Clear();
                foreach (KeyValuePair<string, List<string[]>> section in sectionMembers)
                {
                    foreach (string[] member in section.Value)
                    {
                        if (!SettingsSearch.Matches(member[0], member[1])) continue;
                        groupsShown.Add(section.Key);
                        break;
                    }
                }
            }
            return groupsShown.Contains(group);
        }

        // A row for one of KSP's bindings: its name, its two keys, and KSP as its source; filtered by the search.
        private static DialogGUIBase KspBindingRow(KspKeyBindings.Binding binding)
        {
            KspKeyBindings.Binding shown = binding;
            DialogGUIBase first = KspBindingControl(shown, false);
            DialogGUIBase second = KspBindingControl(shown, true);
            // Asked in every frame.
            string primaryKey = "ksp." + shown.Name + ".primary", secondaryKey = "ksp." + shown.Name + ".secondary";
            DialogGUIBase row = DialogRows.Row(RowHeight + 4f,
                new DialogGUILabel(() => SettingMarks.Marked(shown.Title, Pending.ContainsKey(primaryKey)
                                                             || Pending.ContainsKey(secondaryKey)),
                    KeyNameWidth), first, new DialogGUISpace(6f),
                second, new DialogGUISpace(4f),
                new DialogGUILabel(RichText.Grey("KSP"), KeySourceWidth));
            row.OptionEnabledCondition = SettingsSearch.Filter(shown.Title, "KSP");
            return row;
        }

        // One of the two keys KSP keeps per binding. A KeyBinding holds one KeyCode
        // and no modifier -- KSP's own modifier key is a binding of its own.
        private static DialogGUIBase KspBindingControl(KspKeyBindings.Binding binding, bool second)
        {
            KspKeyBindings.Binding shown = binding;
            string key = "ksp." + shown.Name + (second ? ".secondary" : ".primary");
            Func<string> read = () =>
            {
                string pending;
                return Pending.TryGetValue(key, out pending) ? pending : shown.Read(second);
            };
            Func<string> current = () => CachedText(key, read);
            Conflicts.Register(key, current, shown.Situations, shown.Modes, () => shown.Default(second));

            Func<string> label = () => KeyCapture.Listening(key) ? ListeningText : BindingLabel(key, current());
            // Any key, a modifier among them: KSP binds LeftShift to the throttle.
            DialogGUIButton take = null;
            take = new DialogGUIButton(label, () => KeyCapture.Start(key, text =>
            {
                Pending[key] = text;
                Changed();
            }, true, () => Showing(take)), BindingWidth, RowHeight + 4f, false);
            take.tooltipText = (second ? "The second key for " : "The key for ") + shown.Title
                               + ".\nKSP keeps one key per binding; its own modifier key is a binding of its own."
                               + "\nClick, then press the key. Escape cancels; x clears it.";

            DialogGUIButton clear = new DialogGUIButton("x", () =>
            {
                if (KeyCapture.Listening(key)) KeyCapture.Stop();
                Pending[key] = KeyCombination.NoneText;
                Changed();
            }, BindingButtonWidth, RowHeight + 4f, false);

            return DialogRows.Row(RowHeight + 4f, take, new DialogGUISpace(4f), clear);
        }

        // On Apply: into GameSettings; true where one was written, for KSP's save
        // at the end of Apply.
        internal static bool Apply()
        {
            Changed();
            if (Pending.Count == 0) return false;
            bool written = false;
            foreach (KspKeyBindings.Binding binding in KspKeyBindings.All())
            {
                written |= Write(binding, false);
                written |= Write(binding, true);
            }
            Pending.Clear();
            return written;
        }

        // Writes a binding's pending key to KSP's settings; false where none waits, or the write failed (logged).
        private static bool Write(KspKeyBindings.Binding binding, bool second)
        {
            string key = "ksp." + binding.Name + (second ? ".secondary" : ".primary");
            string text;
            if (!Pending.TryGetValue(key, out text)) return false;
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
        // their defaults with their other settings.
        // KSP's go back to what KSP's own reset sets: what KSP ships with the preset of the keyboard layout it
        // detects (KspKeyBindings.Defaults), the same KspReset loads before these
        // are written. Filled in only: Apply writes them.
        internal static void Reset()
        {
            Pending.Clear();
            string layout = KspKeyBindings.DetectedLayout(true);
            foreach (KspKeyBindings.Binding binding in KspKeyBindings.All())
            {
                ResetOne(binding, layout, false);
                ResetOne(binding, layout, true);
            }
            Changed();
        }

        // Puts one of KSP's keys back to the layout's shipped default, where it has one.
        private static void ResetOne(KspKeyBindings.Binding binding, string layout, bool second)
        {
            string shipped = binding.DefaultIn(layout, second);
            if (shipped == null) return;
            Pending["ksp." + binding.Name + (second ? ".secondary" : ".primary")] = shipped;
        }

        // ReDefinition's own hotkeys, over the settings copy the window edits.
        private static DialogGUIBase OwnBindingRow(ModuleSetting setting)
        {
            ModuleSetting shown = setting;
            string key = "redefinition." + shown.Key;
            Func<string> read = () => shown.Read(edit.After);
            Func<string> text = () => CachedText(key, read);
            DialogGUIBase row = BindingRow(key, shown.Title, shown.Tooltip, "ReDefinition", text,
                value => edit.Write(shown, value), () => true, 2, KspKeyBindings.Everywhere);
            row.OptionEnabledCondition = SettingsSearch.Filter(shown.Title, "ReDefinition");
            return row;
        }

        // A bundled mod's binding, over the edit model like its other settings. How
        // many modifiers it can hold is the mod's: Scatterer keeps one beside each of
        // its keys.
        // inKeysTab: filtered by the Keys tab's search; a binding a registration
        // places in another tab stands there whatever the search holds.
        internal static DialogGUIBase BundledBindingRow(BundledSetting setting, bool inKeysTab)
        {
            BundledSetting shown = setting;
            string key = shown.Key;
            Func<string> read = () =>
            {
                string pending;
                return model.TryGetPending(key, out pending) ? pending : KeyCombination.NoneText;
            };
            Func<string> text = () => CachedText(key, read);
            // Placed in one of KSP's groups, it counts where that group does.
            DialogGUIBase row = BindingRow(key, shown.Title, shown.Tooltip, shown.Owner.ModName, text,
                value => model.Change(key, value),
                () => (model.Bundled || SettingsApply.SetDirectly(shown, false)) && model.HasPending(key), shown.MaxModifiers,
                KspKeyBindings.SituationsOfGroup(shown.Group));
            if (shown.CrossedWith != null)
            {
                string other = shown.CrossedWith;
                Conflicts.RegisterCrossed(key, shown.Owner.Id, () =>
                {
                    string otherText;
                    return Conflicts.Crossed(text(), model.TryGetPending(other, out otherText)
                        ? otherText
                        : KeyCombination.NoneText);
                }, KspKeyBindings.SituationsOfGroup(shown.Group));
            }
            if (inKeysTab) row.OptionEnabledCondition = SettingsSearch.Filter(shown.Title, shown.Owner.ModName);
            return row;
        }

        // The modifiers a row switches, each with its right-hand counterpart.
        private static readonly KeyCode[][] ModifierPairs =
        {
            new[] { KeyCode.LeftControl, KeyCode.RightControl },
            new[] { KeyCode.LeftAlt, KeyCode.RightAlt },
            new[] { KeyCode.LeftShift, KeyCode.RightShift },
        };

        // The switches' labels, in ModifierPairs' order.
        private static readonly string[] ModifierLabels = { "Ctrl", "Alt", "Shift" };

        // The row itself: the name, the key as a button that listens when it is
        // clicked, a button that clears it, switches for the modifiers, and where it
        // comes from.
        private static DialogGUIBase BindingRow(string key, string title, string tooltip, string owner,
                                                Func<string> current, Action<string> set, Func<bool> changeable,
                                                int maxModifiers, int situations)
        {
            Conflicts.Register(key, current, situations, -1, null);
            Action<string> write = text =>
            {
                set(text);
                Changed();
            };
            // A new key keeps the modifiers the switches hold.
            Action<string> taken = text =>
                write(KeyCombination.Parse(current()).WithKey(KeyCombination.Parse(text).Key).ToString());
            Func<string> label = () => KeyCapture.Listening(key)
                ? ListeningText
                : BindingLabel(key, KeyText(KeyCombination.Parse(current())));
            DialogGUIButton take = null;
            take = new DialogGUIButton(label, () => KeyCapture.Start(key, taken, false, () => Showing(take)), BindingWidth,
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
            row.Add(new DialogGUILabel(RichText.Grey(owner), KeySourceWidth));
            return DialogRows.Row(RowHeight + 4f, row.ToArray());
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

        // The yellow labels made so far, by their text.
        private static readonly Dictionary<string, string> yellowLabels = new Dictionary<string, string>();
    }
}
