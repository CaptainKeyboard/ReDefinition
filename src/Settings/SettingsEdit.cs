using System;
using System.Collections.Generic;

namespace ReDefinition.Settings
{
    // What the settings window edits (docs/development/architecture.md): the
    // bundled settings' rows as the player, a profile or the reset left them,
    // where each value came from, the Profiles tab's status line, and Apply as a
    // series of steps -- free of the game's UI, so that the decisions are tested
    // without the game. SettingsWindow draws the rows and carries the steps out.
    //
    // The rows are copies, as in KSP's own settings dialog: nothing reaches a mod
    // before Apply, and Cancel leaves everything as it was.
    internal sealed class SettingsEdit
    {
        // What filled a row, where that was not the player, and with which value.
        // Apply acts on a row so filled only while it still holds that value; one
        // changed since is the player's:
        //   * Profile -- a profile's own value: set even where the save loaded now
        //     shows it already, so that a per-save value becomes the choice for
        //     every save;
        //   * Released -- what the applied profile set and the chosen one leaves
        //     alone: handed back to its mod. A hand edit ends it for good,
        //     whatever value the row comes back to;
        //   * Reset -- the default *Reset to defaults* filled in.
        // One map for the chosen profile's rows and one for the reset's, which a
        // profile chosen after it does not overwrite.
        internal enum Origin
        {
            Profile,
            Released,
            Reset,
        }

        // What Apply does with a row.
        internal enum StepKind
        {
            // The row's value becomes the choice (BundledSettings.Set).
            Set,
            // Back to its mod, with what it had before ReDefinition.
            Release,
            // The default the reset filled in (BundledSettings.ResetTo).
            Reset,
        }

        internal struct Step
        {
            public BundledSetting Setting;
            public StepKind Kind;
            public string Value;
        }

        private struct Filled
        {
            public Origin From;
            public string Value;
        }

        private readonly Dictionary<string, string> pending = new Dictionary<string, string>();
        // What each row held as it was read -- as the window opened, after Apply,
        // or as a sync found the mod's value changed.
        private readonly Dictionary<string, string> opened = new Dictionary<string, string>();
        private readonly Dictionary<string, Filled> filled = new Dictionary<string, Filled>();

        // The defaults *Reset to defaults* filled in, the settings the window does
        // not show among them: Apply sets each row still holding its default
        // through BundledSettings.ResetTo, even where the mod shows it already,
        // since a value ReDefinition keeps may still stand over it
        // (docs/player/graphics-profiles.md). Empty where no reset is chosen.
        private readonly Dictionary<string, string> resetValues = new Dictionary<string, string>();

        // Per profile, what choosing it fills in, and the settings it hands back to
        // their mods -- ones the applied profile set and it leaves alone: released
        // at Apply, not set.
        private Dictionary<string, Dictionary<BundledSetting, string>> profileValues =
            new Dictionary<string, Dictionary<BundledSetting, string>>();
        private Dictionary<string, HashSet<BundledSetting>> profileReleased =
            new Dictionary<string, HashSet<BundledSetting>>();

        // RowsPending, worked out again only when the rows have changed: the
        // Advanced and Restore buttons ask for it every frame.
        private int rowsPendingVersion = -1;
        private bool rowsPending;

        // A value put into a row: a choice row that does not list it takes it in
        // front.
        public Action<string, string> Offered;

        // The bundling switch as the window shows it, and the profile the rows
        // were last filled from; empty for none.
        public bool Bundled;
        public string Profile = "";

        // The profiles' lists were worked out before a mod's value changed: read
        // again once the values have settled, not in every tick a slider in a
        // mod's window moves, and before one is chosen.
        public bool ProfilesStale;

        // Counts the changes to the rows, for what is asked every frame.
        public int Version { get; private set; }

        public bool Resetting
        {
            get { return resetValues.Count > 0; }
        }

        // The rows as the window opens, or after Apply or Restore: each setting's
        // value now -- null where it cannot be read, which leaves the row without
        // a value -- with nothing filled and no reset chosen.
        // The toolbar buttons the window holds, by mod id: true where the button
        // is hidden. Filled by OpenButtons after Open, for the mods whose button
        // ReDefinition can hide at all.
        private readonly Dictionary<string, bool> buttons = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> buttonsOpened = new Dictionary<string, bool>();

        public void OpenButtons(IEnumerable<KeyValuePair<string, bool>> hidden)
        {
            buttons.Clear();
            buttonsOpened.Clear();
            foreach (KeyValuePair<string, bool> pair in hidden)
            {
                buttons[pair.Key] = pair.Value;
                buttonsOpened[pair.Key] = pair.Value;
            }
            Version++;
        }

        // False for a mod the window does not hold: one without a button to hide.
        public bool ButtonHidden(string modId)
        {
            bool hidden;
            return buttons.TryGetValue(modId, out hidden) && hidden;
        }

        public void SetButtonHidden(string modId, bool hidden)
        {
            bool now;
            if (!buttons.TryGetValue(modId, out now) || now == hidden) return;
            buttons[modId] = hidden;
            Version++;
        }

        public IEnumerable<KeyValuePair<string, bool>> Buttons
        {
            get { return buttons; }
        }

        public bool ButtonsPending()
        {
            foreach (KeyValuePair<string, bool> pair in buttons)
            {
                bool opened;
                if (!buttonsOpened.TryGetValue(pair.Key, out opened) || opened != pair.Value) return true;
            }
            return false;
        }

        public void Open(bool bundled, string profile, IEnumerable<KeyValuePair<string, string>> values)
        {
            buttons.Clear();
            buttonsOpened.Clear();
            pending.Clear();
            opened.Clear();
            filled.Clear();
            resetValues.Clear();
            Bundled = bundled;
            Profile = profile ?? "";
            foreach (KeyValuePair<string, string> pair in values)
            {
                opened[pair.Key] = pair.Value;
                if (pair.Value == null) continue;
                pending[pair.Key] = pair.Value;
                Offer(pair.Key, pair.Value);
            }
            Version++;
        }

        public bool TryGetPending(string key, out string value)
        {
            return pending.TryGetValue(key, out value);
        }

        public bool HasPending(string key)
        {
            return pending.ContainsKey(key);
        }

        // A row changed -- by hand, by a profile or by the reset.
        public void Change(string key, string value)
        {
            string old;
            bool had = pending.TryGetValue(key, out old);
            if (had && old == value) return;
            pending[key] = value;
            Offer(key, value);
            // A row changed by hand is the player's: whether it still holds a
            // profile's value or the reset's default is asked where it matters
            // (FilledBy), and a hand-back ends at its first change -- a slider
            // writing the same value as other text is no change.
            Filled entry;
            if (had && !SettingValues.Same(old, value) && filled.TryGetValue(key, out entry)
                && entry.From == Origin.Released)
                filled.Remove(key);
            Version++;
        }

        // What filled the row, where it still holds that value: the chosen
        // profile first, then the reset.
        public bool FilledBy(string key, string value, out Origin from)
        {
            from = Origin.Profile;
            if (value == null) return false;
            Filled entry;
            if (filled.TryGetValue(key, out entry) && SettingValues.Same(entry.Value, value))
            {
                from = entry.From;
                return true;
            }
            string reset;
            if (!resetValues.TryGetValue(key, out reset) || !SettingValues.Same(reset, value)) return false;
            from = Origin.Reset;
            return true;
        }

        // The profiles' values and hand-backs, as ProfileApplier works them out
        // against the profile applied now and the values the mods hold now.
        public void SetProfiles(Dictionary<string, Dictionary<BundledSetting, string>> values,
                                Dictionary<string, HashSet<BundledSetting>> released)
        {
            profileValues = values;
            profileReleased = released;
        }

        // Every setting's default, as choosing a profile fills its rows -- the
        // settings the window does not show included. Nothing is set until Apply,
        // and no profile is chosen.
        public void ChooseDefaults(IDictionary<BundledSetting, string> values)
        {
            foreach (KeyValuePair<BundledSetting, string> pair in values) Change(pair.Key.Key, pair.Value);
            filled.Clear();
            resetValues.Clear();
            foreach (KeyValuePair<BundledSetting, string> pair in values) resetValues[pair.Key.Key] = pair.Value;
            Bundled = true;
            Profile = "";
            Version++;
        }

        // Fills the rows of every tab with the profile's values and switches the
        // bundling on, which a profile needs; nothing is set until Apply. False
        // where the profile is not known.
        public bool ChooseProfile(string name)
        {
            Dictionary<BundledSetting, string> values;
            if (name == null || !profileValues.TryGetValue(name, out values)) return false;

            foreach (KeyValuePair<BundledSetting, string> pair in values) Change(pair.Key.Key, pair.Value);
            List<string> filledBefore = new List<string>(filled.Keys);
            filled.Clear();
            HashSet<BundledSetting> released;
            profileReleased.TryGetValue(name, out released);
            foreach (KeyValuePair<BundledSetting, string> pair in values)
            {
                Origin from = released != null && released.Contains(pair.Key) ? Origin.Released : Origin.Profile;
                filled[pair.Key.Key] = new Filled { From = from, Value = pair.Value };
            }
            // A row a profile chosen before filled and this one leaves goes back to
            // the reset's default, where a reset is chosen.
            foreach (string key in filledBefore)
            {
                string reset;
                if (!filled.ContainsKey(key) && resetValues.TryGetValue(key, out reset)) Change(key, reset);
            }
            Bundled = true;
            Profile = name;
            return true;
        }

        // A change made meanwhile outside the window -- in a mod's own window, in
        // KSP's settings: a row not changed here takes the mod's value; one changed
        // here keeps the change, which Apply sets. A row a chosen profile hands
        // back, or a profile or the reset filled and that still holds its value --
        // as Apply asks it -- is theirs, even where it holds what the mod had:
        // Apply still sets or releases it. True where the mod's value is new to the
        // window.
        public bool Sync(string key, string now)
        {
            string before, value;
            opened.TryGetValue(key, out before);
            if (now == null || SettingValues.Same(now, before)) return false;

            bool hasPending = pending.TryGetValue(key, out value);
            Origin from;
            bool theirs = hasPending && FilledBy(key, value, out from);
            bool untouched = !theirs && (!hasPending || SettingValues.Same(value, before));
            opened[key] = now;
            if (untouched)
            {
                pending[key] = now;
                Offer(key, now);
            }
            Version++;
            return true;
        }

        // Whether one row holds something other than what it was read with --
        // changed by hand, by a profile or by the reset.
        public bool Changed(string key)
        {
            string value;
            if (!pending.TryGetValue(key, out value)) return false;
            string before;
            opened.TryGetValue(key, out before);
            return !SettingValues.Same(value, before);
        }

        // Whether a row holds something other than what it was read with.
        public bool RowsPending()
        {
            if (rowsPendingVersion == Version) return rowsPending;
            rowsPendingVersion = Version;
            rowsPending = false;
            foreach (KeyValuePair<string, string> pair in pending)
            {
                string before;
                opened.TryGetValue(pair.Key, out before);
                if (SettingValues.Same(pair.Value, before)) continue;
                rowsPending = true;
                break;
            }
            return rowsPending;
        }

        // Whether anything in the window still waits for Apply.
        public bool Unapplied(bool upscalerPending, bool enabled, string applied)
        {
            return upscalerPending || Bundled != enabled || Profile != applied || Resetting || RowsPending()
                   || ButtonsPending();
        }

        // The Profiles tab's line: what the rows hold against the profile they
        // were filled from -- `chosenTitle` that profile's title, null where it is
        // not installed -- and whether any of it still waits for Apply. Asked of
        // the rows, not of the profile's name: High applied, rows changed, High
        // chosen again -- the name is the applied one, the rows are not.
        // `differences` counts the values that no longer match it.
        public string Status(string applied, bool enabled, bool upscalerPending, string chosenTitle,
                             Func<Dictionary<BundledSetting, string>, int> differences)
        {
            bool unapplied = Unapplied(upscalerPending, enabled, applied);
            string toSet = unapplied ? " -- Apply or Accept sets it" : "";
            if (Resetting && string.IsNullOrEmpty(Profile))
                return "Every setting of the bundled mods back to its default" + toSet;
            // A profile chosen after the reset sets its own rows, and the reset
            // every other one -- which the line says.
            string rest = Resetting ? ", every other setting back to its default" : "";

            if (chosenTitle == null)
            {
                if (!string.IsNullOrEmpty(Profile))
                    return "The profile '" + Profile + "' is not installed any more" + toSet;
                // Nothing applies High on its own, so it is not called the
                // default here, which would read as if it were in effect.
                return unapplied ? "No profile chosen" + toSet : "No profile chosen. High is the one to start with.";
            }

            Dictionary<BundledSetting, string> values;
            int differ = profileValues.TryGetValue(Profile, out values) ? differences(values) : 0;
            if (differ == 0) return "Profile: " + chosenTitle + rest + toSet;
            return "Custom, changed from " + chosenTitle + " in " + differ + (differ == 1 ? " setting" : " settings")
                   + rest + toSet;
        }

        // Apply, row by row in the order given, worked out as the steps are taken:
        // a row the reset filled is reset, one a profile hands back is released,
        // and one the player, a profile or a sync changed is set. Handed back even
        // where the row shows no change: in the main menu a per-save mod's row
        // shows its own answer, and the saves still hold the choice kept here. A profile's value
        // the mod holds already is set where the mod holding it is not all a
        // choice needs (`holds`).
        public IEnumerable<Step> Steps(IEnumerable<BundledSetting> settings, Func<BundledSetting, string, bool> holds)
        {
            foreach (BundledSetting setting in settings)
            {
                string value, before;
                if (!pending.TryGetValue(setting.Key, out value)) continue;
                opened.TryGetValue(setting.Key, out before);
                Origin from;
                bool isFilled = FilledBy(setting.Key, value, out from);
                bool release = isFilled && from == Origin.Released;
                bool fromProfile = isFilled && from == Origin.Profile;
                bool reset = isFilled && from == Origin.Reset;
                if (!reset && !release && SettingValues.Same(value, before) && (!fromProfile || holds(setting, value)))
                    continue;
                yield return new Step
                {
                    Setting = setting,
                    Kind = reset ? StepKind.Reset : release ? StepKind.Release : StepKind.Set,
                    Value = value,
                };
            }
        }

        private void Offer(string key, string value)
        {
            if (Offered != null) Offered(key, value);
        }
    }
}
