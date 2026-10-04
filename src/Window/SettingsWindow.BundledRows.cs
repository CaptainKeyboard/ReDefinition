using System.Collections.Generic;
using System.Globalization;
using System;
using ReDefinition.Core;
using ReDefinition.Settings;
using UnityEngine;

namespace ReDefinition.Window
{
    // A bundled mod's row: its control over the edit model, by the kind of
    // setting, and a choice row's list, which a value from outside it joins.
    internal static partial class SettingsWindow
    {
        // A choice row's list, which a value from outside the list joins at its
        // front: one a mod's own window set while this one is open, or one a
        // profile hands back.
        private sealed class ChoiceRow
        {
            public string[] Choices;
            public string[] Labels;
            // The setting's own list, before a requirement limited it: where a
            // value joining the front finds its label.
            public string[] AllChoices;
            public string[] AllLabels;
            public DialogGUISlider Control;
        }

        private static readonly Dictionary<string, ChoiceRow> choiceRows = new Dictionary<string, ChoiceRow>();

        // A value a choice row does not list goes in front of its list, as one
        // found as the window opened does.
        private static void OfferChoice(string key, string value)
        {
            ChoiceRow row;
            if (value == null || !choiceRows.TryGetValue(key, out row)) return;
            OfferChoice(row, value);
        }

        // Where a value joins the choices: in order among numbers that only rise or
        // only fall, else at the front.
        private static int PlaceAmong(string[] choices, string value)
        {
            double number;
            if (choices.Length < 2 || !TryNumber(value, out number)) return 0;
            double[] numbers = new double[choices.Length];
            for (int i = 0; i < choices.Length; i++)
                if (!TryNumber(choices[i], out numbers[i])) return 0;
            bool rising = numbers[1] > numbers[0];
            for (int i = 1; i < numbers.Length; i++)
                if (numbers[i] == numbers[i - 1] || (numbers[i] > numbers[i - 1]) != rising) return 0;
            int at = 0;
            while (at < numbers.Length && (rising ? numbers[at] < number : numbers[at] > number)) at++;
            return at;
        }

        private static bool TryNumber(string text, out double number)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }

        private static string[] InsertAt(string[] list, int at, string value)
        {
            string[] all = new string[list.Length + 1];
            Array.Copy(list, 0, all, 0, at);
            all[at] = value;
            Array.Copy(list, at, all, at + 1, list.Length - at);
            return all;
        }

        private static DialogGUIBase BundledRow(BundledSetting setting)
        {
            // A setting checked by its type only has no control to draw: no row,
            // whatever the layout lists -- which the check outside the game
            // refuses.
            if (setting.Control == SettingControl.Value) return null;
            if (setting.Control == SettingControl.Binding) return BundledBindingRow(setting, false);

            string key = setting.Key;
            DialogGUIBase control;
            Func<string> value = () => "";

            if (setting.Control == SettingControl.Toggle)
            {
                control = new DialogGUIToggle(() => PendingBool(key),
                    () => KspSettingsSection.StateText(PendingBool(key)),
                    b => model.Change(key, b ? "True" : "False"), ControlWidth);
            }
            else if (setting.Control == SettingControl.Slider)
            {
                control = new DialogGUISlider(() => PendingFloat(key, setting.Min), setting.Min, setting.Max,
                    setting.WholeNumbers, ControlWidth, -1f, f =>
                    {
                        // The slider is set from the row in every frame
                        // (DialogGUISlider.Update), and a value it clamps or
                        // rounds on the way is not the player moving it.
                        if (Mirrors(f, PendingFloat(key, setting.Min), setting)) return;
                        model.Change(key, Format(Stepped(f, setting), setting.WholeNumbers));
                    });
                value = () => Display(setting, null, null);
            }
            else
            {
                // Taken as the window opens: TUFX's profiles are known only from
                // the running mod. Nothing to choose from, no row.
                string[] choices = SafeChoices(setting);
                if (choices == null || choices.Length == 0) return null;
                string[] labels = setting.ChoiceLabels;
                ChoiceRow choice = new ChoiceRow { AllChoices = choices, AllLabels = labels };
                // What an installed mod requires limits what can be chosen -- but for a
                // row it locks, which cannot be moved anyway: its whole list keeps the
                // handle where the value stands, where a list of one would put it at
                // the left end (Slider.normalizedValue is 0 where the minimum is the
                // maximum).
                if (!Requirements.Locked(setting)) Requirements.Limit(setting, ref choices, ref labels);
                choice.Choices = choices;
                choice.Labels = labels;

                // A value the list does not hold -- a profile pack removed since,
                // or one a requirement is about to put right -- stays in front,
                // rather than being shown as another.
                string now;
                if (model.TryGetPending(key, out now)) OfferChoice(choice, now);
                choice.Control = new DialogGUISlider(() => ChoiceIndex(key, choice.Choices), 0f,
                    choice.Choices.Length - 1, true, ControlWidth, -1f,
                    f =>
                    {
                        // The slider is set from the row in every frame: only a
                        // move away from what the row shows is the player
                        // choosing. A value from outside the list joins it first
                        // (OfferChoice).
                        int index = Mathf.Clamp(Mathf.RoundToInt(f), 0, choice.Choices.Length - 1);
                        if (index == Mathf.RoundToInt(ChoiceIndex(key, choice.Choices))) return;
                        model.Change(key, choice.Choices[index]);
                    });
                choiceRows[key] = choice;
                control = choice.Control;
                value = () => Display(setting, choice.Choices, choice.Labels);
            }

            // When a change arrives, where it is not at once, is said below the
            // tabs (WaitingNotice); the row's owner stands at its right.
            control.tooltipText = setting.Tooltip + Requirements.Note(setting);
            // The switch as it stands in the window, not as it was applied: with
            // bundling unticked the rows are left to their mods at once. A value
            // nobody could read cannot be edited sensibly.
            control.OptionInteractableCondition = () => (model.Bundled || SetDirectly(setting, false))
                                                        && model.HasPending(key) && !Requirements.Locked(setting);

            return new DialogGUIHorizontalLayout(0f, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel(() => MarkedTitle(setting), NameWidth), control, new DialogGUISpace(10f),
                new DialogGUILabel(value, ValueWidth),
                new DialogGUILabel("<color=#9a9a9a>" + setting.Owner.ModName + "</color>", SourceWidth));
        }

        private static string[] SafeChoices(BundledSetting setting)
        {
            try
            {
                return setting.CurrentChoices();
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("bundled-choices-" + setting.Key, setting.Owner.ModName + ", " + setting.Title
                                      + ": its choices could not be read (" + CompatibilityLog.Reason(e) + ").");
                return null;
            }
        }

        private static bool PendingBool(string key)
        {
            string text;
            bool value;
            return model.TryGetPending(key, out text) && bool.TryParse(text, out value) && value;
        }

        private static float PendingFloat(string key, float fallback)
        {
            string text;
            float value;
            return model.TryGetPending(key, out text)
                   && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value
                : fallback;
        }

        // Whether the slider's value is only what it was set to from the row:
        // Unity's slider clamps, then rounds whole numbers (Slider.ClampValue).
        private static bool Mirrors(float value, float shown, BundledSetting setting)
        {
            float expected = Mathf.Clamp(shown, setting.Min, setting.Max);
            if (setting.WholeNumbers) expected = Mathf.Round(expected);
            return Mathf.Approximately(value, expected);
        }

        // The slider's value on the setting's step from Min, within its range.
        private static float Stepped(float value, BundledSetting setting)
        {
            if (setting.Step <= 0f) return value;
            float stepped = setting.Min + Mathf.Round((value - setting.Min) / setting.Step) * setting.Step;
            return Mathf.Clamp(stepped, setting.Min, setting.Max);
        }

        private static float ChoiceIndex(string key, string[] choices)
        {
            string text;
            if (!model.TryGetPending(key, out text)) return 0f;
            for (int i = 0; i < choices.Length; i++)
                if (SettingValues.Same(choices[i], text)) return i;
            return 0f;
        }

        private static string Display(BundledSetting setting, string[] choices, string[] labels)
        {
            string text;
            if (!model.TryGetPending(setting.Key, out text)) return "--";
            if (choices != null)
            {
                for (int i = 0; i < choices.Length; i++)
                {
                    if (!SettingValues.Same(choices[i], text)) continue;
                    return labels != null && i < labels.Length ? labels[i] : choices[i];
                }
                return text;
            }

            float value;
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return text;
            if (setting.Percent)
                return Mathf.RoundToInt(value * 100f).ToString(CultureInfo.InvariantCulture) + " %";
            return setting.WholeNumbers
                ? Mathf.RoundToInt(value).ToString(CultureInfo.InvariantCulture)
                : value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string Format(float value, bool wholeNumbers)
        {
            return wholeNumbers
                ? Mathf.RoundToInt(value).ToString(CultureInfo.InvariantCulture)
                : (Mathf.Round(value * 100f) / 100f).ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
