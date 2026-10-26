using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using ReDefinition.Core;
using ReDefinition.Settings;
using ReDefinition.Ui;
using ReDefinition.Window;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Probe
{
    // -redefinitionProbeWindow "step;step;...": the settings window as the player uses it, for shots of what it
    // shows -- the tooltips among them, which KSP shows where the pointer rests, so the pointer is moved there.
    // Steps:
    //   open=<tab>[@<0..1>]      the window on that tab, scrolled (1 the top, 0 the bottom)
    //   set=<module>.<key>:<value>  a row set as a click sets it (nothing applied yet)
    //   accept                   Accept, as its button
    //   hover=<text>[#n]         the pointer on the n-th button (from the top) whose text is that
    //   hoverrow=<row title>     the pointer on the control of the row of that name
    //   click=<text>             a button of that text clicked (a dialog's)
    //   shot=<name>              a shot, <shot>-win-<name>.png
    internal static class WindowProbe
    {
        // Puts the cursor onto a desktop point.
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        // Runs the steps in turn, each logged, with a pause after each for the window to follow.
        public static IEnumerator Run(string folder, string shot, string steps)
        {
            foreach (string raw in steps.Split(';'))
            {
                string step = raw.Trim();
                if (step.Length == 0) continue;
                int at = step.IndexOf('=');
                string name = at < 0 ? step : step.Substring(0, at);
                string argument = at < 0 ? "" : step.Substring(at + 1);
                string done;
                switch (name)
                {
                    case "open":
                        string[] parts = argument.Split('@');
                        SettingCategory tab;
                        if (!Enum.TryParse(parts[0], true, out tab))
                        {
                            done = "no tab " + parts[0];
                            break;
                        }
                        float scroll;
                        TabScrollList.ProbePosition = parts.Length > 1 && float.TryParse(parts[1],
                            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out scroll)
                            ? scroll : (float?)null;
                        SettingsWindow.OpenOn(tab);
                        done = "opened " + tab;
                        break;
                    case "set":
                        int dot = argument.IndexOf('.');
                        int colon = argument.IndexOf(':');
                        done = dot > 0 && colon > dot
                               && SettingsWindow.ProbeSet(argument.Substring(0, dot), argument.Substring(dot + 1, colon - dot - 1),
                                   argument.Substring(colon + 1))
                            ? "set " + argument : "could not set " + argument;
                        break;
                    case "accept":
                        SettingsWindow.ProbeAccept();
                        done = "accepted";
                        break;
                    case "hover":
                        done = Hover(argument) ? "pointer on " + argument : "no button " + argument;
                        break;
                    case "hoverrow":
                        done = HoverRow(argument) ? "pointer on the row " + argument : "no row " + argument;
                        break;
                    case "click":
                        done = Click(argument) ? "clicked " + argument : "no button " + argument;
                        break;
                    case "shot":
                        yield return new WaitForEndOfFrame();
                        ScreenCapture.CaptureScreenshot(Path.Combine(folder, shot + "-win-" + argument + ".png"));
                        done = "shot " + argument;
                        break;
                    default:
                        done = "unknown step";
                        break;
                }
                Debug.Log(Log.Tag + " Probe: window step " + step + ": " + done + ".");
                yield return new WaitForSecondsRealtime(name == "open" || name == "accept" || name == "click" ? 2f : 1.5f);
            }
            TabScrollList.ProbePosition = null;
        }

        // The buttons whose text is the one given, top first.
        private static List<Selectable> Buttons(string text)
        {
            List<Selectable> found = new List<Selectable>();
            foreach (Selectable selectable in UnityEngine.Object.FindObjectsOfType<Selectable>())
            {
                if (!selectable.isActiveAndEnabled) continue;
                TMP_Text label = selectable.GetComponentInChildren<TMP_Text>();
                if (label != null && label.text != null && StripTags(label.text).Trim() == text) found.Add(selectable);
            }
            found.Sort((a, b) => ScreenOf(b.transform).y.CompareTo(ScreenOf(a.transform).y));
            return found;
        }

        // The pointer onto the n-th button (#n, from 1, the first where none is given) whose text is the one given.
        private static bool Hover(string argument)
        {
            int index = 1;
            int hash = argument.LastIndexOf('#');
            if (hash > 0 && int.TryParse(argument.Substring(hash + 1), out index)) argument = argument.Substring(0, hash);
            List<Selectable> buttons = Buttons(argument);
            if (index < 1 || index > buttons.Count) return false;
            return PointAt(buttons[index - 1].transform);
        }

        // The slider or toggle beside a row's name.
        private static bool HoverRow(string title)
        {
            foreach (TMP_Text text in UnityEngine.Object.FindObjectsOfType<TMP_Text>())
            {
                if (!text.isActiveAndEnabled || text.text == null || StripTags(text.text).Trim() != title) continue;
                Transform row = text.transform.parent;
                for (int depth = 0; depth < 3 && row != null; depth++, row = row.parent)
                {
                    Selectable control = row.GetComponentInChildren<Selectable>();
                    if (control != null) return PointAt(control.transform);
                }
            }
            return false;
        }

        // Clicks the topmost button of that text, or switches on a toggle button of it; false where there is none.
        private static bool Click(string text)
        {
            foreach (Selectable selectable in Buttons(text))
            {
                Button button = selectable as Button;
                if (button != null)
                {
                    button.onClick.Invoke();
                    return true;
                }
                // A toggle button, as the window's rows of buttons are: switched on as a click does.
                Toggle toggle = selectable as Toggle;
                if (toggle == null) continue;
                toggle.isOn = true;
                return true;
            }
            return false;
        }

        // Where a UI element stands on the screen, Unity's way (from the bottom left).
        private static Vector2 ScreenOf(Transform element)
        {
            Camera ui = KSP.UI.UIMasterController.Instance != null ? KSP.UI.UIMasterController.Instance.uiCamera : null;
            return RectTransformUtility.WorldToScreenPoint(ui, element.position);
        }

        // The pointer onto a UI element's place on the screen.
        private static bool PointAt(Transform element)
        {
            Vector2 point = ScreenOf(element);
            // Windows counts from the top left; the game runs in full screen.
            return SetCursorPos((int)point.x, Screen.height - (int)point.y);
        }

        // A label's text without its rich-text tags.
        private static string StripTags(string text)
        {
            return System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "");
        }
    }
}
