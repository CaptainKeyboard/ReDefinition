using System.Collections.Generic;
using System.Globalization;
using System;
using ReDefinition.Core;
using ReDefinition.Settings;
using ReDefinition.Ui;
using UnityEngine;

namespace ReDefinition.Window
{
    // A bundled mod's row: its control over the edit model, by the kind of
    // setting, and a choice row's list, which a value from outside it joins.
    internal static partial class SettingsWindow
    {
        // A choice row's list, which a value from outside the list joins.
        // The value takes its place among numbers, else the front (PlaceAmong).
        // Such a value is one a mod's own window set while this one is open, or one a profile hands back.
        private sealed class ChoiceRow
        {
            // The values the row offers, and their labels where the values are not shown as they are.
            public string[] Choices;
            public string[] Labels;
            // The setting's own list, before a requirement limited it: where a
            // value joining the front finds its label.
            public string[] AllChoices;
            public string[] AllLabels;
            // The row's slider, once it is built.
            public DialogGUISlider Control;
        }

        // The choice rows of the open window, by their setting's key.
        private static readonly Dictionary<string, ChoiceRow> choiceRows = new Dictionary<string, ChoiceRow>();

        // What a bundled row shows of its pending value: the number its slider stands at, the position among its choices
        // and the text in its value column.
        // KSP runs every row's delegates in every frame, so each is worked out once for a value.
        // It is kept until the value's text or the row's lists are another string.
        // The model hands out the same string until the row changes, and a choice row's lists are replaced,
        // never edited (OfferChoice).
        private sealed class RowShown
        {
            // The setting, and its choice row; null for a slider.
            private readonly BundledSetting setting;
            private readonly ChoiceRow choice;
            // What the answers below were worked out for.
            private bool keyed;
            private bool hasText;
            private string text;
            private string[] choices;
            private string[] labels;
            // The answers, and whether each is worked out for the value above.
            private bool numberMade, indexMade, shownMade;
            private float number, index;
            private string shown;

            // Follows that row of that setting.
            public RowShown(BundledSetting setting, ChoiceRow choice)
            {
                this.setting = setting;
                this.choice = choice;
            }

            // The slider's number: the pending value, the setting's minimum where there is none.
            public float Number()
            {
                Follow();
                if (!numberMade)
                {
                    number = PendingFloat(setting.Key, setting.Min);
                    numberMade = true;
                }
                return number;
            }

            // The choice row's slider position; 0 where the value is none of the choices.
            public float Index()
            {
                Follow();
                if (!indexMade)
                {
                    index = ChoiceIndex(setting.Key, choice.Choices);
                    indexMade = true;
                }
                return index;
            }

            // The value column's text.
            public string Shown()
            {
                Follow();
                if (!shownMade)
                {
                    shown = Display(setting, choice != null ? choice.Choices : null, choice != null ? choice.Labels : null);
                    shownMade = true;
                }
                return shown;
            }

            // Forgets the answers where the pending text or a list is not the one they were worked out for.
            private void Follow()
            {
                string now;
                bool has = model.TryGetPending(setting.Key, out now);
                string[] nowChoices = choice != null ? choice.Choices : null;
                string[] nowLabels = choice != null ? choice.Labels : null;
                if (keyed && has == hasText && ReferenceEquals(now, text) && nowChoices == choices && nowLabels == labels)
                    return;
                keyed = true;
                hasText = has;
                text = now;
                choices = nowChoices;
                labels = nowLabels;
                numberMade = false;
                indexMade = false;
                shownMade = false;
            }
        }

        // A value a choice row does not list joins its list (PlaceAmong), as one
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

        // A number in the invariant culture.
        private static bool TryNumber(string text, out double number)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }

        // The list with the value inserted at that index.
        private static string[] InsertAt(string[] list, int at, string value)
        {
            string[] all = new string[list.Length + 1];
            Array.Copy(list, 0, all, 0, at);
            all[at] = value;
            Array.Copy(list, at, all, at + 1, list.Length - at);
            return all;
        }

        // A bundled setting's row: its name, its control by the setting's kind, its value as shown and its mod; null for
        // a setting with nothing to draw.
        private static DialogGUIBase BundledRow(BundledSetting setting)
        {
            // A setting checked by its type only has no control to draw: no row,
            // whatever the layout lists -- which the check outside the game
            // refuses.
            if (setting.Control == SettingControl.Value) return null;
            if (setting.Control == SettingControl.Binding) return BundledBindingRow(setting, false);
            if (setting.Control == SettingControl.Dialog) return BundledDialogRow(setting);

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
                RowShown sliderShown = new RowShown(setting, null);
                control = new DialogGUISlider(sliderShown.Number, setting.Min, setting.Max,
                    setting.WholeNumbers, ControlWidth, -1f, f =>
                    {
                        // The slider is set from the row in every frame
                        // (DialogGUISlider.Update), and a value it clamps or
                        // rounds on the way is not the player moving it.
                        if (Mirrors(f, sliderShown.Number(), setting)) return;
                        model.Change(key, Format(Stepped(f, setting), setting.WholeNumbers));
                    });
                value = sliderShown.Shown;
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
                RowShown choiceShown = new RowShown(setting, choice);
                choice.Control = new DialogGUISlider(choiceShown.Index, 0f,
                    choice.Choices.Length - 1, true, ControlWidth, -1f,
                    f =>
                    {
                        // The slider is set from the row in every frame: only a
                        // move away from what the row shows is the player
                        // choosing. A value from outside the list joins it first
                        // (OfferChoice).
                        int index = Mathf.Clamp(Mathf.RoundToInt(f), 0, choice.Choices.Length - 1);
                        if (index == Mathf.RoundToInt(choiceShown.Index())) return;
                        model.Change(key, choice.Choices[index]);
                    });
                choiceRows[key] = choice;
                control = choice.Control;
                value = choiceShown.Shown;
            }

            // When a change arrives, where it is not at once, is said below the
            // tabs (WaitingNotice); the row's owner stands at its right.
            control.tooltipText = setting.Tooltip + Requirements.Note(setting);
            ChoiceRow shownChoice;
            choiceRows.TryGetValue(key, out shownChoice);
            // The profile's value as the row shows it, made again only for another value: BundledDefault hands out
            // the same string while it is the same one.
            string defaultFor = null;
            string defaultShown = null;
            DefaultTooltip(control, () =>
            {
                string profileValue = BundledDefault(setting);
                if (profileValue == null) return null;
                if (!ReferenceEquals(profileValue, defaultFor))
                {
                    defaultFor = profileValue;
                    defaultShown = DisplayValue(setting, shownChoice != null ? shownChoice.AllChoices : null,
                        shownChoice != null ? shownChoice.AllLabels : null, profileValue);
                }
                return defaultShown;
            });
            // The switch as it stands in the window, not as it was applied: with
            // bundling unticked the rows are left to their mods at once. A value
            // nobody could read cannot be edited sensibly.
            control.OptionInteractableCondition = () => (model.Bundled || SetDirectly(setting, false))
                                                        && model.HasPending(key) && !Requirements.Locked(setting);

            return DialogRows.Row(RowHeight,
                new DialogGUILabel(() => MarkedTitle(setting), NameWidth), control, new DialogGUISpace(10f),
                new DialogGUILabel(value, ValueWidth),
                new DialogGUILabel(RichText.Grey(setting.Owner.ModName), SourceWidth));
        }

        // A mod's dialog (DIALOG): its button where a row's control stands, usable
        // with the bundling off as well -- it sets nothing -- once what the mod's
        // method is called on is there.
        private static DialogGUIBase BundledDialogRow(BundledSetting setting)
        {
            DialogGUIButton open = new DialogGUIButton(setting.DialogButton, () => OpenModDialog(setting), ControlWidth,
                RowHeight + 6f, false);
            open.tooltipText = setting.Tooltip;
            open.OptionInteractableCondition = () => SafeApplicable(setting);
            return DialogRows.Row(RowHeight + 6f,
                new DialogGUILabel(setting.Title, NameWidth), open, new DialogGUISpace(10f),
                new DialogGUILabel("", ValueWidth),
                new DialogGUILabel(RichText.Grey(setting.Owner.ModName), SourceWidth));
        }

        // Whether the mod's dialog can be opened now; false where asking throws.
        private static bool SafeApplicable(BundledSetting setting)
        {
            try
            {
                return setting.Applicable == null || setting.Applicable();
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The mod's content, from its method; one that throws or gives nothing is logged
        // and opens no dialog.
        private static void OpenModDialog(BundledSetting setting)
        {
            DialogGUIBase[] content;
            try
            {
                content = setting.DialogContent();
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("mod-dialog-" + setting.Key, setting.Owner.ModName + "'s dialog " + setting.Title
                                      + " could not be opened (" + CompatibilityLog.Reason(e) + ").");
                return;
            }
            if (content == null || content.Length == 0)
            {
                CompatibilityLog.Warn("mod-dialog-empty-" + setting.Key, setting.Owner.ModName + "'s dialog "
                                      + setting.Title + " gave nothing to show.");
                return;
            }
            OpenDialogOverWindow(setting.Key, setting.DialogHeading, setting.DialogWidth, content);
        }

        // A dialog's content under its heading, KSP's Close below it, over the window as its own
        // dialogs stand; Escape closes it too. A mod's (DIALOG) and ReDefinition's own (ModuleDialog).
        internal static void OpenDialogOverWindow(string key, string heading, float width, DialogGUIBase[] content)
        {
            List<DialogGUIBase> rows = new List<DialogGUIBase>(content);
            rows.Add(new DialogGUIHorizontalLayout(
                new DialogGUIFlexibleSpace(),
                new DialogGUIButton(KSP.Localization.Localizer.Format("#autoLOC_149410"), () => { }, 120f, 30f, true),
                new DialogGUIFlexibleSpace()));
            TooltipText.WrapAll(rows);
            MultiOptionDialog dialog = new MultiOptionDialog("ReDefinitionModDialog." + key, "",
                heading, HighLogic.UISkin, width, rows.ToArray());
            PopupDialog shown = UnityMouseEvents.Shield(DialogOverWindow.Raise(PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), dialog, false, HighLogic.UISkin, false)));
            // Once its elements stand.
            DialogTooltips.Attach(rows);
            EscapeGuard.Own(shown);
        }

        // The setting's choices from the running mod; null where reading them throws (logged).
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

        // Whether the row's pending value is True.
        private static bool PendingBool(string key)
        {
            string text;
            bool value;
            return model.TryGetPending(key, out text) && bool.TryParse(text, out value) && value;
        }

        // The row's pending value as a number; the fallback where there is none.
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

        // The slider position of the row's pending value among the choices; 0 where it is none of them.
        private static float ChoiceIndex(string key, string[] choices)
        {
            string text;
            if (!model.TryGetPending(key, out text)) return 0f;
            for (int i = 0; i < choices.Length; i++)
                if (SettingValues.Same(choices[i], text)) return i;
            return 0f;
        }

        // The row's pending value as the row shows it; "--" where there is none.
        private static string Display(BundledSetting setting, string[] choices, string[] labels)
        {
            string text;
            if (!model.TryGetPending(setting.Key, out text)) return "--";
            return DisplayValue(setting, choices, labels, text);
        }

        // A value as the row shows it: its choice's label, a percent, a number to the
        // row's steps, on or off.
        private static string DisplayValue(BundledSetting setting, string[] choices, string[] labels, string text)
        {
            if (setting.Control == SettingControl.Toggle)
            {
                bool on;
                return bool.TryParse(text, out on) ? KspSettingsSection.StateText(on) : text;
            }
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

        // A slider's value as the model keeps it: whole, or to two decimals.
        private static string Format(float value, bool wholeNumbers)
        {
            return wholeNumbers
                ? Mathf.RoundToInt(value).ToString(CultureInfo.InvariantCulture)
                : (Mathf.Round(value * 100f) / 100f).ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
