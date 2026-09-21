using System.Collections.Generic;
using System.Text;

namespace ReDefinition.Window
{
    // KSP's tooltip draws its text as one line across the screen: a text breaks
    // its own lines. The longest line any of ReDefinition's tooltips may have is
    // the first line of the Upscaler row's (OurModules), 83 characters; a longer
    // line is broken at the last space before that.
    internal static class TooltipText
    {
        internal const int MaxLine = 83;

        internal static string Wrap(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string[] lines = text.Split('\n');
            StringBuilder wrapped = new StringBuilder(text.Length + 8);
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) wrapped.Append('\n');
                string line = lines[i];
                while (line.Length > MaxLine)
                {
                    int cut = line.LastIndexOf(' ', MaxLine);
                    if (cut <= 0) break;
                    wrapped.Append(line, 0, cut).Append('\n');
                    line = line.Substring(cut + 1);
                }
                wrapped.Append(line);
            }
            return wrapped.ToString();
        }

        // Every tooltip of a dialog's elements, the rows of a scroll list's layout
        // included.
        internal static void WrapAll(IEnumerable<DialogGUIBase> elements)
        {
            if (elements == null) return;
            foreach (DialogGUIBase element in elements)
            {
                if (element == null) continue;
                element.tooltipText = Wrap(element.tooltipText);
                WrapAll(element.children);
                DialogGUIScrollList scroll = element as DialogGUIScrollList;
                if (scroll != null && scroll.layout != null) WrapAll(new DialogGUIBase[] { scroll.layout });
            }
        }
    }
}
