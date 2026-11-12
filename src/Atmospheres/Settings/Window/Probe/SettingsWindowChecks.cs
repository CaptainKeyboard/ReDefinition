using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Atmospheres
{
    // One yes/no item of the DialogGUI checklist, with the number of the screenshot that shows it.
    internal sealed class WindowCheck
    {
        internal string Name;
        internal bool Ok;
        internal string Detail = "";
        internal int Shot;
    }

    // The DialogGUI checklist of the settings window, read from the dialog that stands in the game: the items that went wrong in earlier
    // panels (the content sliding down a scroll list, the lighter background of the scroll prefab, a label that wraps and overlaps the next,
    // a row wider than its page, a character the font lacks, a toggle that fires by itself). Each item is a yes or a no with the offending
    // count in its detail. The items that do not apply to this window (a part highlight, a panel that slides in) are not listed.
    internal static class SettingsWindowChecks
    {
        // The slack a width may exceed by before it counts as wrapping, in pixels.
        private const float WidthSlack = 2f;

        // Every item. `window` is the dialog's object; `shot` takes a screenshot and returns its number.
        internal static List<WindowCheck> Run(GameObject window, WindowModel model, Func<string, int> shot, bool settingsUnchanged)
        {
            var list = new List<WindowCheck>();
            ScrollRect scroll = window.GetComponentInChildren<ScrollRect>(true);
            list.Add(Item("scroll_pivot", scroll != null && scroll.content != null && ContentHangsFromTheTop(scroll.content),
                scroll == null ? "no scroll list" : "pivot.y " + F(scroll.content.pivot.y) + ", anchors.y " + F(scroll.content.anchorMin.y) + "/" + F(scroll.content.anchorMax.y)));
            list.Add(Item("scroll_background", ScrollBackgroundClear(scroll), scroll == null ? "no scroll list" : "alpha " + F(BackgroundAlpha(scroll))));

            int wrapping = 0, tooWide = 0, missing = 0, labels = 0;
            string offenders = "";
            foreach (TMP_Text text in window.GetComponentsInChildren<TMP_Text>(false))
            {
                if (!text.gameObject.activeInHierarchy || string.IsNullOrEmpty(text.text)) continue;
                labels++;
                if (Wraps(text))
                {
                    wrapping++;
                    if (offenders.Length < 160) offenders += (offenders.Length > 0 ? " | " : "") + text.text.Replace('\n', ' ') + " [" + F(text.preferredWidth) + "/" + F(text.rectTransform.rect.width) + "]";
                }
                if (LacksGlyphs(text)) missing++;
            }
            foreach (HorizontalLayoutGroup row in window.GetComponentsInChildren<HorizontalLayoutGroup>(false))
            {
                if (!row.gameObject.activeInHierarchy) continue;
                RectTransform rect = row.transform as RectTransform;
                RectTransform page = scroll != null && scroll.viewport != null ? scroll.viewport : (scroll != null ? scroll.transform as RectTransform : null);
                if (rect == null || page == null) continue;
                if (LayoutUtility.GetPreferredWidth(rect) > page.rect.width + WidthSlack) tooWide++;
            }
            list.Add(Item("labels_single_line", wrapping == 0 && labels > 0, wrapping + " of " + labels + " labels wrap" + (offenders.Length > 0 ? ": " + offenders : "")));
            list.Add(Item("row_widths", tooWide == 0, tooWide + " rows wider than the page"));
            list.Add(Item("font_glyphs", missing == 0 && labels > 0, missing + " labels with a character the font lacks"));
            list.Add(Item("toggles_idempotent", !model.Dirty && settingsUnchanged,
                model.Dirty || !settingsUnchanged ? "the window changed a setting by itself" : "no setting changed while the window was open"));
            list.Add(Item("theme_background", HasBackground(window), "the largest image of the dialog is a sprite of the skin"));
            // Not applicable to this window, listed so that the table holds every item of the checklist: it is a popup dialog, not a panel
            // that slides in with the game's panels, and it highlights no part.
            list.Add(Item("panels", true, "not applicable: a popup dialog, no panel of the game's transition manager"));
            list.Add(Item("highlight", true, "not applicable: the window highlights no part"));
            foreach (WindowCheck check in list) check.Shot = shot(check.Name);
            return list;
        }

        private static WindowCheck Item(string name, bool ok, string detail)
        {
            return new WindowCheck { Name = name, Ok = ok, Detail = detail };
        }

        private static string F(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        // The content is held at the top edge: anchors and pivot at 1 in y, no offset.
        internal static bool ContentHangsFromTheTop(RectTransform content)
        {
            return Mathf.Approximately(content.pivot.y, 1f) && Mathf.Approximately(content.anchorMin.y, 1f) && Mathf.Approximately(content.anchorMax.y, 1f);
        }

        private static float BackgroundAlpha(ScrollRect scroll)
        {
            Image image = scroll.GetComponent<Image>();
            return image == null ? 0f : image.color.a;
        }

        // The scroll prefab's own lighter background is gone.
        private static bool ScrollBackgroundClear(ScrollRect scroll)
        {
            return ScrollBackground.IsClear(scroll);
        }

        // A label whose text is wider than its box wraps, and a wrapped label with changing text gets no height.
        private static bool Wraps(TMP_Text text)
        {
            return text.preferredWidth > text.rectTransform.rect.width + WidthSlack && text.rectTransform.rect.width > 0f && text.enableWordWrapping
                   && text.preferredHeight > text.fontSize * 1.6f;
        }

        // A character of the text that the label's font does not have: it shows as a box.
        private static bool LacksGlyphs(TMP_Text text)
        {
            if (text.font == null) return false;
            foreach (char c in text.text)
            {
                if (c == '\n' || c == '\r' || c == ' ') continue;
                if (!text.font.HasCharacter(c)) return true;
            }
            return false;
        }

        // The dialog's background is its largest image: a sprite of the skin with an opaque colour, which ZTheme replaces by name.
        private static bool HasBackground(GameObject window)
        {
            Image largest = null;
            float area = 0f;
            foreach (Image image in window.GetComponentsInChildren<Image>(true))
            {
                Rect rect = image.rectTransform.rect;
                if (rect.width * rect.height <= area) continue;
                area = rect.width * rect.height;
                largest = image;
            }
            return largest != null && largest.sprite != null && largest.color.a > 0.5f;
        }
    }
}
