using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Atmospheres.Forecast
{
    // One item of the checklist: a yes or a no with the count in its detail, and the request for its two pictures (natural and aid view).
    internal sealed class ForecastCheck
    {
        internal string Name;
        internal bool Ok;
        internal string Detail = "";
        internal ShotRequest Shot;
    }

    // The DialogGUI checklist of the forecast window (the memory list of the pitfalls that showed only in the game), read from the dialog
    // that stands: the scroll list's pivot, the prefab's own background, labels that wrap, rows wider than the page, characters the font
    // lacks, a setting that changed by itself, the skin's background. The items that cannot apply to a popup dialog (a panel of the game's
    // transition manager, a part highlight) are not rows: a row that is true by construction proves nothing. Each item carries the
    // screen rectangles it is about, so that its natural view is the screen cropped to them and its aid view frames them (green where the
    // item holds, red where it does not).
    internal static class ForecastWindowChecks
    {
        private const float WidthSlack = 2f;
        private const float Pad = 12f;

        internal static List<ForecastCheck> Run(GameObject window, bool settingsUnchanged)
        {
            var list = new List<ForecastCheck>();
            var all = ProbeShots.ScreenRect((RectTransform)window.transform);
            ScrollRect scroll = window.GetComponentInChildren<ScrollRect>(true);
            Rect scrollRect = scroll != null ? ProbeShots.ScreenRect((RectTransform)scroll.transform) : all;

            bool pivot = scroll != null && scroll.content != null && SettingsWindowChecks.ContentHangsFromTheTop(scroll.content);
            ForecastCheck c = Item("scroll_pivot", pivot, scroll == null ? "no scroll list" : "pivot.y " + F(scroll.content.pivot.y), scrollRect);
            c.Shot.Marks.Add(new ShotMark { R = scrollRect, Bad = !pivot });
            list.Add(c);

            float alpha = scroll != null && scroll.GetComponent<Image>() != null ? scroll.GetComponent<Image>().color.a : 0f;
            bool clear = ScrollBackground.IsClear(scroll);
            c = Item("scroll_background", clear, scroll == null ? "no scroll list" : "alpha " + F(alpha), scrollRect);
            c.Shot.Marks.Add(new ShotMark { R = scrollRect, Bad = !clear });
            list.Add(c);

            var labelMarks = new List<ShotMark>();
            var glyphMarks = new List<ShotMark>();
            int wrapping = 0, missing = 0, labels = 0;
            // The first wrapping labels with preferred width / box width, so that a failing row names its cause.
            string offenders = "";
            foreach (TMP_Text text in window.GetComponentsInChildren<TMP_Text>(false))
            {
                if (!text.gameObject.activeInHierarchy || string.IsNullOrEmpty(text.text)) continue;
                labels++;
                bool wraps = text.preferredWidth > text.rectTransform.rect.width + WidthSlack && text.rectTransform.rect.width > 0f && text.enableWordWrapping
                             && text.preferredHeight > text.fontSize * 1.6f;
                bool lacks = LacksGlyphs(text);
                if (wraps)
                {
                    wrapping++;
                    if (offenders.Length < 160) offenders += (offenders.Length > 0 ? " | " : "") + text.text.Replace('\n', ' ') + " [" + F(text.preferredWidth) + "/" + F(text.rectTransform.rect.width) + "]";
                }
                if (lacks) missing++;
                Rect r = ProbeShots.ScreenRect(text.rectTransform);
                labelMarks.Add(new ShotMark { R = r, Bad = wraps });
                if (lacks) glyphMarks.Add(new ShotMark { R = r, Bad = true });
            }
            var rowMarks = new List<ShotMark>();
            int tooWide = 0;
            RectTransform page = scroll != null && scroll.viewport != null ? scroll.viewport : window.transform as RectTransform;
            foreach (HorizontalLayoutGroup row in window.GetComponentsInChildren<HorizontalLayoutGroup>(false))
            {
                RectTransform rect = row.transform as RectTransform;
                if (!row.gameObject.activeInHierarchy || rect == null || page == null) continue;
                bool wide = LayoutUtility.GetPreferredWidth(rect) > page.rect.width + WidthSlack;
                if (wide) tooWide++;
                rowMarks.Add(new ShotMark { R = ProbeShots.ScreenRect(rect), Bad = wide });
            }

            c = Item("labels_single_line", wrapping == 0 && labels > 0, wrapping + " of " + labels + " labels wrap" + (offenders.Length > 0 ? ": " + offenders : ""), all);
            c.Shot.Marks.AddRange(labelMarks);
            list.Add(c);
            c = Item("row_widths", tooWide == 0, tooWide + " rows wider than the page", all);
            c.Shot.Marks.AddRange(rowMarks);
            list.Add(c);
            c = Item("font_glyphs", missing == 0 && labels > 0, missing + " labels with a character the font lacks", all);
            c.Shot.Marks.Add(new ShotMark { R = all, Bad = missing > 0 });
            c.Shot.Marks.AddRange(glyphMarks);
            list.Add(c);
            c = Item("settings_unchanged", settingsUnchanged, settingsUnchanged ? "no setting changed while the window was open" : "the window changed a setting by itself", all);
            c.Shot.Marks.Add(new ShotMark { R = all, Bad = !settingsUnchanged });
            list.Add(c);

            bool background = HasBackground(window, out Rect backgroundRect);
            c = Item("theme_background", background, "the largest image of the dialog is a sprite of the skin", backgroundRect);
            c.Shot.Marks.Add(new ShotMark { R = backgroundRect, Bad = !background });
            list.Add(c);
            return list;
        }

        private static ForecastCheck Item(string name, bool ok, string detail, Rect crop)
        {
            var check = new ForecastCheck { Name = name, Ok = ok, Detail = detail };
            check.Shot = new ShotRequest { Name = name, Ok = ok, Crop = new Rect(crop.xMin - Pad, crop.yMin - Pad, crop.width + 2f * Pad, crop.height + 2f * Pad) };
            return check;
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

        private static bool HasBackground(GameObject window, out Rect where)
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
            where = largest != null ? ProbeShots.ScreenRect(largest.rectTransform) : ProbeShots.ScreenRect((RectTransform)window.transform);
            return largest != null && largest.sprite != null && largest.color.a > 0.5f;
        }
    }
}
