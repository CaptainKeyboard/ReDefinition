using System;
using System.Collections.Generic;
using ReDefinition.Core;
using ReDefinition.Ui;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // A column of KSP's dialog whose children are updated only while it is shown (the settings window folds its groups): DialogGUIBase.Update
    // updates every child whether the row is shown or not and only afterwards switches the row off by its OptionEnabledCondition.
    internal sealed class FoldColumn : DialogGUIVerticalLayout
    {
        internal FoldColumn(float minWidth, float spacing, params DialogGUIBase[] list)
            : base(minWidth, -1f, spacing, new RectOffset(), TextAnchor.UpperLeft, list)
        {
        }

        public override void Update()
        {
            System.Func<bool> condition = OptionEnabledCondition;
            if (condition != null && uiItem != null)
            {
                bool on = condition();
                if (uiItem.activeSelf != on) uiItem.SetActive(on);
                if (!on) return;
            }
            base.Update();
        }
    }

    // The settings window of ReDefinition Atmospheres: KSP's own dialog (stock look, ZTheme restyles it where it is installed), groups that
    // fold, a row for every setting of the key table with its control, a tooltip that explains, and a grey that says why. The window draws
    // WindowModel and decides nothing: which control, which grey and which text are the model's. Escape closes it.
    internal sealed class SettingsWindow
    {
        private const float Width = 600f;
        // A row is the name, the control and the value: 250 + 190 + 70 = 510, within the page (Width less the list's margins, the indent
        // of 8 and the scroll bar's 18). The longest name of the key table is 33 characters, about 231 pixels in KSP's font; a test holds
        // the names to NameChars.
        internal const int NameChars = 36;
        private const float NameWidth = 250f;
        private const float ControlWidth = 190f;
        private const float ValueWidth = 70f;
        private const float PageWidth = 510f;
        private const float RowHeight = 24f;
        private const float TipSeconds = 0.5f;

        private static readonly HashSet<string> Open = new HashSet<string> { AtmospheresSettings.Categories.General, StatusGroup };
        private const string StatusGroup = "Status";
        private const string BodiesGroup = "Bodies";

        private readonly WindowModel model;
        private PopupDialog shown;
        private PopupDialog menu;
        private float resetUntil;
        private bool closing;
        private float scroll = 1f;
        private float nextTip;
        private readonly List<KeyValuePair<DialogGUIBase, SettingDef>> tipped = new List<KeyValuePair<DialogGUIBase, SettingDef>>();

        internal SettingsWindow(WindowModel model)
        {
            this.model = model;
        }

        internal bool IsOpen
        {
            get { return shown != null; }
        }

        // The object of the dialog that stands, for the series probe; null while the window is closed.
        internal GameObject Root
        {
            get { return shown != null ? shown.gameObject : null; }
        }

        internal WindowModel Model
        {
            get { return model; }
        }

        // Opens or folds a group, for the series probe.
        internal static void SetOpen(string group, bool open)
        {
            if (open) Open.Add(group);
            else Open.Remove(group);
        }

        // Opens the window, or closes it where it is open.
        internal void Toggle()
        {
            if (shown != null) Close();
            else Show();
        }

        internal void Close()
        {
            CloseMenu();
            if (shown != null)
            {
                SaveScroll();
                // The destroyed dialog tells Gone; this close is announced here, once.
                closing = true;
                shown.Dismiss();
            }
            shown = null;
            tipped.Clear();
            try
            {
                model.Flush();
            }
            catch (Exception e)
            {
                AtmospheresLog.WarnOnce("the settings could not be saved (" + CompatibilityLog.Reason(e) + ")");
            }
            if (OnClosed != null) OnClosed();
        }

        // Called after the window is closed, however it was closed.
        internal Action OnClosed;

        internal void Show()
        {
            model.Env = WindowEnvironment.Detect();
            Build();
        }

        // Builds the dialog anew (the groups' state and the scroll position stay).
        private void Build()
        {
            CloseMenu();
            if (shown != null)
            {
                SaveScroll();
                shown.Dismiss();
            }
            tipped.Clear();
            var rows = new List<DialogGUIBase>();
            rows.Add(StatusSection());
            foreach (string category in model.Categories()) rows.Add(Section(category));
            rows.Add(BodiesSection());

            TabScrollList list = new TabScrollList(new Vector2(Width - 20f, 480f),
                new DialogGUIVerticalLayout(Width - 40f, -1f, 4f, new RectOffset(4, 4, 4, 4), TextAnchor.UpperLeft, rows.ToArray()));
            DialogGUIHorizontalLayout buttons = new DialogGUIHorizontalLayout(Width - 20f, 30f, 6f, new RectOffset(), TextAnchor.MiddleCenter,
                new DialogGUIButton(() => Time.unscaledTime < resetUntil ? "Confirm Reset" : "Reset To Defaults", AskReset, 150f, 26f, false),
                new DialogGUIButton("Close", Close, 90f, 26f, false));
            MultiOptionDialog dialog = new MultiOptionDialog("ReDefinitionAtmospheresSettings", "", "Atmospheres Settings", HighLogic.UISkin,
                Width, list, buttons);
            shown = DialogWindow.Show(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), dialog, false,
                HighLogic.UISkin, true));
            ScrollListSettle.Apply(list);
            ScrollThrough.Apply(shown.gameObject);
            UiHygiene.MouseOnly(shown.gameObject);
            EscapeGuard.Own(shown);
            TooltipText.WrapAll(new DialogGUIBase[] { list, buttons });
            DialogTooltips.Attach(new DialogGUIBase[] { list, buttons });
            RestoreScroll();
            nextTip = 0f;
            // The tooltips are refreshed from the window's own update, not per element.
            shown.gameObject.AddComponent<TipDriver>().Window = this;
        }

        private void SaveScroll()
        {
            if (shown == null) return;
            UnityEngine.UI.ScrollRect rect = shown.GetComponentInChildren<UnityEngine.UI.ScrollRect>();
            if (rect != null) scroll = rect.verticalNormalizedPosition;
        }

        private void RestoreScroll()
        {
            UnityEngine.UI.ScrollRect rect = shown.GetComponentInChildren<UnityEngine.UI.ScrollRect>();
            if (rect == null) return;
            Canvas.ForceUpdateCanvases();
            rect.verticalNormalizedPosition = scroll;
        }

        // Called every frame by the dialog's object: the confirm of Reset times out, the tooltips follow the state of the rows.
        internal void Tick()
        {
            if (Time.unscaledTime < nextTip) return;
            nextTip = Time.unscaledTime + TipSeconds;
            foreach (KeyValuePair<DialogGUIBase, SettingDef> pair in tipped)
            {
                if (pair.Key.uiItem == null || !pair.Key.uiItem.activeInHierarchy) continue;
                pair.Key.tooltipText = model.Tooltip(pair.Value);
            }
        }

        private void AskReset()
        {
            if (Time.unscaledTime < resetUntil)
            {
                resetUntil = 0f;
                model.Reset();
                ScreenMessages.PostScreenMessage("Atmospheres settings reset to the defaults and the tier's budget row.", 4f,
                    ScreenMessageStyle.UPPER_CENTER);
                Build();
                return;
            }
            resetUntil = Time.unscaledTime + 4f;
        }

        // --- Sections ---

        // A group: its header folds it, its rows follow.
        private DialogGUIBase Section(string category)
        {
            var rows = new List<DialogGUIBase>();
            foreach (SettingDef def in model.Rows(category)) rows.Add(Row(def));
            return Folded(category, SettingHelp.CategoryTitle(category), rows);
        }

        private DialogGUIBase Folded(string id, string title, List<DialogGUIBase> body)
        {
            DialogGUIButton header = new DialogGUIButton(() => (Open.Contains(id) ? "-  " : "+  ") + title,
                () => { if (!Open.Remove(id)) Open.Add(id); }, PageWidth, 26f, false);
            FoldColumn column = new FoldColumn(PageWidth, 2f, body.ToArray());
            column.OptionEnabledCondition = () => Open.Contains(id);
            return new DialogGUIVerticalLayout(PageWidth, -1f, 2f, new RectOffset(), TextAnchor.UpperLeft, header, column);
        }

        // The state lines the player reads first: the tier in force, the wind, the mods that are there.
        private DialogGUIBase StatusSection()
        {
            // One line each and none longer than the page holds: a label with changing text that wraps gets no height and overlaps the next.
            var rows = new List<DialogGUIBase>
            {
                new DialogGUILabel(() => model.TierLine(), PageWidth),
                new DialogGUILabel(() => model.WindLine(), PageWidth),
                new DialogGUILabel(() => model.InjectionLine().Length > 0 ? model.InjectionLine() : " ", PageWidth),
                new DialogGUILabel(() => EnvironmentLine(1), PageWidth),
                new DialogGUILabel(() => EnvironmentLine(2), PageWidth)
            };
            return Folded(StatusGroup, "Status", rows);
        }

        private static string Found(bool found)
        {
            return found ? "found" : "not found";
        }

        // The optional mods in two lines.
        private string EnvironmentLine(int line)
        {
            WindowEnvironment env = model.Env;
            if (line == 1) return "EVE " + Found(env.EveInstalled) + ", ModularFlightIntegrator " + Found(env.ModularFlightIntegrator);
            return "Rocket Sound Enhancement " + Found(env.RocketSoundEnhancement) + ", main mod "
                   + (env.MainModPresent ? (env.HostApi ? "answers" : "present") : "not found");
        }

        // The bodies: where weather runs, how far its simulation is, and the player's year factor and frequency for the body.
        private DialogGUIBase BodiesSection()
        {
            var rows = new List<DialogGUIBase>();
            List<BodyRow> bodies = model.BodyList();
            int inactive = 0;
            foreach (BodyRow body in bodies)
            {
                if (!body.Active)
                {
                    inactive++;
                    continue;
                }
                rows.Add(BodyBlock(body));
            }
            if (rows.Count == 0) rows.Add(new DialogGUILabel("No body has weather yet.", PageWidth));
            if (inactive > 0) rows.Add(new DialogGUILabel(inactive + (inactive == 1 ? " body" : " bodies") + " without weather are not listed.", PageWidth));
            return Folded(BodiesGroup, "Bodies", rows);
        }

        private const int ReasonChars = 36;

        private DialogGUIBase BodyBlock(BodyRow body)
        {
            // The status in one line (its reason cut short), the simulation's state in the next.
            DialogGUILabel head = new DialogGUILabel(() => body.Name + ": " + Short(body.StatusText), PageWidth - 16f);
            DialogGUILabel state = new DialogGUILabel(() => body.ReadinessText, PageWidth - 16f);
            head.tooltipText = "Where weather runs on the body: " + "Full, Cover Only, Off or Broken, with the reason where there is one.";
            state.tooltipText = "How far the weather simulation of the body is along.";
            // The year factor on a log scale from 0.01 to 10, the frequency from 0 to 4. The sliders are set from the row in every frame; a
            // value the slider gives back unchanged is not the player moving it.
            DialogGUISlider year = new DialogGUISlider(() => (float)Math.Log10(body.YearFactor), -2f, 1f, false, 130f, -1f, f =>
            {
                if (Mathf.Approximately(f, (float)Math.Log10(body.YearFactor))) return;
                model.SetBody(body, Math.Pow(10.0, f), body.Frequency);
            });
            year.tooltipText = "Year Factor For This Body\nMultiplies the length of the body's seasons.";
            DialogGUISlider frequency = new DialogGUISlider(() => (float)body.Frequency, 0f, 4f, false, 130f, -1f, f =>
            {
                if (Mathf.Approximately(f, (float)body.Frequency)) return;
                model.SetBody(body, body.YearFactor, f);
            });
            frequency.tooltipText = "Weather Frequency For This Body\nMultiplies how often weather forms on the body: 0 none, 1 its own rate.";
            // 40 + 130 + 50 + 10 + 70 + 130 + 50 = 480, within the page less the indent.
            return new DialogGUIVerticalLayout(PageWidth - 8f, -1f, 2f, new RectOffset(8, 0, 0, 4), TextAnchor.UpperLeft,
                head,
                state,
                Line(RowHeight,
                    new DialogGUILabel("Year", 40f), year, new DialogGUILabel(() => ValueFormat.Number(body.YearFactor), 50f),
                    new DialogGUISpace(10f),
                    new DialogGUILabel("Frequency", 70f), frequency, new DialogGUILabel(() => ValueFormat.Number(body.Frequency), 50f)));
        }

        // A status text of one line: the reason inside the brackets cut to ReasonChars.
        private static string Short(string text)
        {
            int open = text.IndexOf('(');
            if (open < 0 || text.Length - open <= ReasonChars) return text;
            return text.Substring(0, open + ReasonChars - 3) + "...)";
        }

        // --- Rows ---

        private DialogGUIBase Row(SettingDef def)
        {
            RowState Now() => model.State(def);
            string name = def.Tooltip;
            DialogGUILabel label = new DialogGUILabel(() => Now().Enabled ? name : Grey(name), NameWidth);
            label.tooltipText = model.Tooltip(def);
            tipped.Add(new KeyValuePair<DialogGUIBase, SettingDef>(label, def));

            DialogGUIBase control = Control(def, Now);
            control.OptionInteractableCondition = () => Now().Enabled;
            control.tooltipText = label.tooltipText;
            tipped.Add(new KeyValuePair<DialogGUIBase, SettingDef>(control, def));

            // A switch and a choice show their value in the control; a typed number or text is its own value.
            bool shownInControl = def.Kind == SettingKind.Bool || def.Kind == SettingKind.Choice || def.Kind == SettingKind.Text || TypedNumber(def);
            DialogGUILabel value = new DialogGUILabel(() => shownInControl ? " " : model.Value(def), ValueWidth);
            return Line(RowHeight + 2f, label, control, value);
        }

        // A whole number with a range too wide for a slider (the seed) is typed.
        internal static bool TypedNumber(SettingDef def)
        {
            return def.Kind == SettingKind.Int && def.Max - def.Min > 5000.0;
        }

        private DialogGUIBase Control(SettingDef def, Func<RowState> state)
        {
            switch (def.Kind)
            {
                case SettingKind.Bool:
                    return new DialogGUIToggle(() => AtmospheresSettings.Number(def.Key) != 0.0, () => AtmospheresSettings.Number(def.Key) != 0.0 ? "On" : "Off",
                        on =>
                        {
                            if ((AtmospheresSettings.Number(def.Key) != 0.0) == on) return;
                            model.Set(def, on ? 1.0 : 0.0);
                        }, ControlWidth, RowHeight);
                case SettingKind.Choice:
                    return new DialogGUIButton(() => model.Value(def) + "   [v]", () => OpenMenu(def), ControlWidth, RowHeight, false);
                case SettingKind.Text:
                    return new DialogGUITextInput(AtmospheresSettings.Text(def.Key), false, 120, text =>
                    {
                        model.SetText(def, text);
                        return text;
                    }, ControlWidth, RowHeight);
                default:
                    // A number with a wide range is typed (the seed), any other is a slider.
                    if (TypedNumber(def))
                        return new DialogGUITextInput(((long)AtmospheresSettings.Number(def.Key)).ToString(System.Globalization.CultureInfo.InvariantCulture), false, 11, text =>
                        {
                            long parsed;
                            if (long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out parsed))
                                model.Set(def, parsed);
                            return text;
                        }, ControlWidth, RowHeight);
                    return new DialogGUISlider(() => (float)AtmospheresSettings.Number(def.Key), (float)def.Min, (float)def.Max, def.Kind == SettingKind.Int,
                        ControlWidth, -1f, f =>
                        {
                            // The slider is set from the row in every frame; only a move of the player's is a change.
                            float shown = Mathf.Clamp((float)AtmospheresSettings.Number(def.Key), (float)def.Min, (float)def.Max);
                            if (def.Kind == SettingKind.Int) shown = Mathf.Round(shown);
                            if (Mathf.Approximately(f, shown)) return;
                            model.Set(def, f);
                        });
            }
        }

        // The choices of a row as a small menu at the pointer; one not offered here is grey and says why when it is clicked.
        private void OpenMenu(SettingDef def)
        {
            CloseMenu();
            if (!model.State(def).Enabled) return;
            var items = new List<DialogGUIBase>();
            for (int i = 0; i < def.Choices.Length; i++)
            {
                int index = i;
                string why = Dependencies.ChoiceUnavailable(def, index, model.Env);
                string text = ValueFormat.ChoiceName(def.Choices[index]);
                bool current = (int)AtmospheresSettings.Number(def.Key) == index;
                if (current) text = "> " + text;
                if (why != null) text = Grey(text);
                DialogGUIButton item = new DialogGUIButton(text, () => Choose(def, index), 200f, RowHeight, true);
                if (why != null) item.tooltipText = model.ChoiceTooltip(def, index);
                items.Add(item);
            }
            var dialog = new MultiOptionDialog("ReDefinitionAtmospheresChoice", "", def.Tooltip, HighLogic.UISkin, 220f,
                new DialogGUIVerticalLayout(200f, -1f, 2f, new RectOffset(4, 4, 4, 4), TextAnchor.UpperLeft, items.ToArray()));
            float x = Mathf.Clamp(Input.mousePosition.x / Screen.width, 0.15f, 0.85f);
            float y = Mathf.Clamp(Input.mousePosition.y / Screen.height, 0.25f, 0.75f);
            menu = DialogWindow.Show(PopupDialog.SpawnPopupDialog(new Vector2(x, y), new Vector2(x, y), dialog, false, HighLogic.UISkin, true));
            TooltipText.WrapAll(items);
            DialogTooltips.Attach(items);
        }

        private void Choose(SettingDef def, int index)
        {
            string why = Dependencies.ChoiceUnavailable(def, index, model.Env);
            if (why != null)
            {
                ScreenMessages.PostScreenMessage(ValueFormat.ChoiceName(def.Choices[index]) + ": " + SettingHelp.ReasonText(why), 4f,
                    ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            model.Set(def, index);
        }

        private void CloseMenu()
        {
            if (menu != null) menu.Dismiss();
            menu = null;
        }

        private static DialogGUIHorizontalLayout Line(float height, params DialogGUIBase[] children)
        {
            return new DialogGUIHorizontalLayout(0f, height, 0f, new RectOffset(), TextAnchor.MiddleLeft, children);
        }

        private static string Grey(string text)
        {
            return "<color=#7a7a7a>" + text + "</color>";
        }

        // Gives the window's update to the object of its dialog.
        private sealed class TipDriver : MonoBehaviour
        {
            internal SettingsWindow Window;

            private void Update()
            {
                if (Window != null) Window.Tick();
            }

            // Escape and a scene change dismiss the dialog without Close: the changes are saved and the launcher's button put back.
            private void OnDestroy()
            {
                if (Window != null) Window.Gone();
            }
        }

        // The dialog's object is gone. Where a newer dialog stands (the window was built anew) nothing happens.
        private void Gone()
        {
            if (shown != null) return;
            if (closing)
            {
                closing = false;
                return;
            }
            tipped.Clear();
            try
            {
                model.Flush();
            }
            catch (Exception e)
            {
                AtmospheresLog.WarnOnce("the settings could not be saved (" + CompatibilityLog.Reason(e) + ")");
            }
            if (OnClosed != null) OnClosed();
        }
    }
}
