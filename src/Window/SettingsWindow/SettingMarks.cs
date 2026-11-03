using System;
using System.Collections.Generic;
using ReDefinition.Modules;
using ReDefinition.Settings;
using static ReDefinition.Window.SettingsWindow;

namespace ReDefinition.Window
{
    // The marks on a row: the dot before its name while a change waits for Apply, the profile's mark where the
    // row differs from the chosen profile, and the "Default: X" line in its tooltip. What the chosen profile
    // holds for each row is read once per profile and kept.
    internal static class SettingMarks
    {
        // The colour of the mark for a change waiting for Apply.
        internal const string MarkColor = "#8fc8ff";
        // U+2022, which the dialogs' font holds.
        internal const string Mark = "•";

        // The row's name, marked while a change to it waits for Apply -- made by
        // hand, by a profile or by Reset.
        internal static string MarkedTitle(BundledSetting setting)
        {
            return MarkedName(setting.Title, model.Changed(setting.Key), BundledDefault(setting) != null);
        }

        // A row's name with its marks, kept per name until either mark changes: KSP asks
        // for the label's text in every frame it shows.
        private sealed class MarkedText
        {
            // Whether the text was made changed, differing from the profile, and at all; and the text made.
            public bool Changed, Differs, Made;
            public string Text;
        }

        // The marked names made so far, by the row's name.
        private static readonly Dictionary<string, MarkedText> markedNames = new Dictionary<string, MarkedText>();

        // The name with the change mark and the profile mark where they apply, made anew only when one of them changes.
        internal static string MarkedName(string title, bool changed, bool differs)
        {
            MarkedText kept;
            if (!markedNames.TryGetValue(title, out kept))
            {
                kept = new MarkedText();
                markedNames[title] = kept;
            }
            if (!kept.Made || kept.Changed != changed || kept.Differs != differs)
            {
                kept.Text = ProfileMarked(Marked(title, changed), differs);
                kept.Changed = changed;
                kept.Differs = differs;
                kept.Made = true;
            }
            return kept.Text;
        }

        // A row that holds another value than the chosen profile gives it: a mark of its
        // own before the name, and its tooltip ends in "Default: X".
        private const string ProfileMarkColor = "#e0b040";

        // The name with the profile's mark before it where the row differs from the profile.
        internal static string ProfileMarked(string title, bool differs)
        {
            return differs ? "<color=" + ProfileMarkColor + ">" + Mark + "</color> " + title : title;
        }

        // The tooltip as the row was built with it, and "Default: X" below it where
        // `defaultOf` gives X: kept in the element, which KSP hands to its tooltip each
        // frame, and in the tooltip the window attached itself (DialogTooltips).
        internal static void DefaultTooltip(DialogGUIBase control, Func<string> defaultOf)
        {
            string plain = control.tooltipText ?? "";
            string shownDefault = null;
            Callback before = control.OnUpdate;
            control.OnUpdate = () =>
            {
                if (before != null) before();
                string now = defaultOf();
                if (now == shownDefault) return;
                shownDefault = now;
                control.tooltipText = now == null ? plain : plain + (plain.Length > 0 ? "\n" : "") + "Default: " + now;
                KSP.UI.TooltipTypes.TooltipController_Text tooltip =
                    control.uiItem != null ? control.uiItem.GetComponent<KSP.UI.TooltipTypes.TooltipController_Text>() : null;
                if (tooltip != null) tooltip.textString = control.tooltipText;
            };
        }

        // The chosen profile's values, by the bundled settings' keys and by ReDefinition's
        // own rows, for the profile last asked about.
        private static string defaultsOf;
        private static readonly Dictionary<string, string> bundledDefaults = new Dictionary<string, string>();
        private static readonly Dictionary<ModuleSetting, string> ownDefaults = new Dictionary<ModuleSetting, string>();

        // Reads the profile's values into the two tables where it is not the one read last; false where there is no such
        // profile or none is chosen.
        private static bool DefaultsFor(string profileName)
        {
            if (string.IsNullOrEmpty(profileName)) return false;
            if (defaultsOf == profileName) return true;
            GraphicsProfile profile = ProfileApplier.Find(ProfilesTab.Loaded, profileName);
            if (profile == null) return false;
            bundledDefaults.Clear();
            foreach (KeyValuePair<BundledSetting, string> pair in ProfileApplier.Values(profile, new List<string>()))
                bundledDefaults[pair.Key.Key] = pair.Value;
            ownDefaults.Clear();
            foreach (KeyValuePair<ModuleSetting, string> pair in ModuleProfiles.Values(profile, null))
                ownDefaults[pair.Key] = pair.Value;
            defaultsOf = profileName;
            return true;
        }

        // The profile's value of a bundled row as the row shows values, where the row holds
        // another one; null where it holds the profile's, the profile has none, or none is
        // chosen.
        // Every row asks twice in every frame, so each answer is kept while the rows, the chosen profile and
        // the profiles' lists are as they were: the answer depends on nothing else.
        internal static string BundledDefault(BundledSetting setting)
        {
            if (defaultKnownVersion != model.Version || defaultKnownGeneration != profilesGeneration
                || !string.Equals(defaultKnownProfile, model.Profile, StringComparison.Ordinal))
            {
                bundledDefaultKnown.Clear();
                defaultKnownVersion = model.Version;
                defaultKnownGeneration = profilesGeneration;
                defaultKnownProfile = model.Profile;
            }
            string known;
            if (bundledDefaultKnown.TryGetValue(setting.Key, out known)) return known;
            string profileValue, now;
            known = !DefaultsFor(model.Profile) || !bundledDefaults.TryGetValue(setting.Key, out profileValue)
                    || !model.TryGetPending(setting.Key, out now) || SettingValues.Same(now, profileValue)
                ? null
                : profileValue;
            bundledDefaultKnown[setting.Key] = known;
            return known;
        }

        // What BundledDefault answered since the rows, the chosen profile or the profiles' lists last changed, and
        // what they were then. The generation counts ProfilesTab.Load, which reads the lists again.
        private static readonly Dictionary<string, string> bundledDefaultKnown = new Dictionary<string, string>();
        private static int defaultKnownVersion = -1;
        private static int defaultKnownGeneration = -1;
        private static string defaultKnownProfile;
        private static int profilesGeneration;

        // The profile's value of one of ReDefinition's own rows as the row shows it, where the row holds another one;
        // null otherwise.
        internal static string OwnDefault(ModuleSetting setting, OwnSettings settings)
        {
            string profileValue;
            // The upscaler's switch a profile only turns on, as ModuleProfiles.Differences
            // leaves it to the player.
            if (settings == null || setting.OnlyActivates || !DefaultsFor(model.Profile)
                || !ownDefaults.TryGetValue(setting, out profileValue)
                || SettingValues.Same(OwnNormalized(setting, settings), profileValue))
                return null;
            // As the row shows its value (KspSettingsSection.Shown).
            if (setting.Label != null) return setting.Label(profileValue);
            bool on;
            return setting.Control == SettingControl.Toggle && bool.TryParse(profileValue, out on)
                ? KspSettingsSection.StateText(on) : profileValue;
        }

        // One of ReDefinition's own settings in that object as ModuleSetting.Normalize spells it, read once per frame
        // for the copy the window edits.
        private static string OwnNormalized(ModuleSetting setting, OwnSettings settings)
        {
            return edit != null ? edit.Reads.Normalized(setting, settings) : setting.Normalize(setting.Read(settings));
        }

        // The name with the change mark before it while a change waits.
        internal static string Marked(string title, bool changed)
        {
            return changed ? "<color=" + MarkColor + ">" + Mark + "</color> " + title : title;
        }

        // The profiles' lists were read again: what was kept of them is stale.
        internal static void ProfilesReloaded()
        {
            defaultsOf = null;
            profilesGeneration++;
        }
    }
}
