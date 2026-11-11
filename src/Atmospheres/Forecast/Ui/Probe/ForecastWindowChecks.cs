using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Atmospheres.Forecast
{
    // The DialogGUI checklist of the forecast window (the memory list of the pitfalls that showed only in the game), read from the dialog
    // that stands: the scroll list's pivot, the prefab's own background, labels that wrap, rows wider than the page, characters the font
    // lacks, a control that changes a setting by itself, the skin's background. Each item is a yes or a no with the count in its detail
    // and the number of the screenshot that shows it. Same items and same reading as SettingsWindowChecks; the items that do not apply
    // (a panel of the game's transition manager, a part highlight) are listed as such.
    internal static class ForecastWindowChecks
    {
        private const float WidthSlack = 2f;

        internal static List<WindowCheck> Run(GameObject window, bool settingsUnchanged, Func<string, int> shot)
        {
            var list = new List<WindowCheck>();
            ScrollRect scroll = window.GetComponentInChildren<ScrollRect>(true);
            list.Add(Item("scroll_pivot", scroll != null && scroll.content != null && SettingsWindowChecks.ContentHangsFromTheTop(scroll.content),
                scroll == null ? "no scroll list" : "pivot.y " + F(scroll.content.pivot.y)));
            float alpha = scroll != null && scroll.GetComponent<Image>() != null ? scroll.GetComponent<Image>().color.a : 0f;
            list.Add(Item("scroll_background", scroll != null && alpha < 0.01f, scroll == null ? "no scroll list" : "alpha " + F(alpha)));

            int wrapping = 0, tooWide = 0, missing = 0, labels = 0;
            foreach (TMP_Text text in window.GetComponentsInChildren<TMP_Text>(false))
            {
                if (!text.gameObject.activeInHierarchy || string.IsNullOrEmpty(text.text)) continue;
                labels++;
                if (text.preferredWidth > text.rectTransform.rect.width + WidthSlack && text.rectTransform.rect.width > 0f && text.enableWordWrapping
                    && text.preferredHeight > text.fontSize * 1.6f) wrapping++;
                if (LacksGlyphs(text)) missing++;
            }
            RectTransform page = scroll != null && scroll.viewport != null ? scroll.viewport : window.transform as RectTransform;
            foreach (HorizontalLayoutGroup row in window.GetComponentsInChildren<HorizontalLayoutGroup>(false))
            {
                RectTransform rect = row.transform as RectTransform;
                if (!row.gameObject.activeInHierarchy || rect == null || page == null) continue;
                if (LayoutUtility.GetPreferredWidth(rect) > page.rect.width + WidthSlack) tooWide++;
            }
            list.Add(Item("labels_single_line", wrapping == 0 && labels > 0, wrapping + " of " + labels + " labels wrap"));
            list.Add(Item("row_widths", tooWide == 0, tooWide + " rows wider than the page"));
            list.Add(Item("font_glyphs", missing == 0 && labels > 0, missing + " labels with a character the font lacks"));
            list.Add(Item("toggles_idempotent", settingsUnchanged, settingsUnchanged ? "no setting changed while the window was open" : "the window changed a setting by itself"));
            list.Add(Item("theme_background", HasBackground(window), "the largest image of the dialog is a sprite of the skin"));
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
