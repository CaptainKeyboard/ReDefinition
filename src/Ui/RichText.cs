namespace ReDefinition.Ui
{
    // The grey every dialog uses for the quiet text of a row: the owner column, a hint, a download's progress.
    // The texts built from it that a label asks for in every frame are made once and handed back
    // until what they are made of changes.
    internal static class RichText
    {
        // The colour of a row's quiet text, as the markup writes it.
        public const string GreyColor = "#9a9a9a";

        // The text in grey.
        public static string Grey(string text)
        {
            return "<color=" + GreyColor + ">" + text + "</color>";
        }
    }

    // A text in grey that stays one string until its source changes: a label asks every frame, and joining the
    // markup again each time would make garbage.
    internal sealed class GreyText
    {
        // The source the text was made from, and the text; empty before one.
        private string source;
        private string text = "";

        // The source in grey; empty for none.
        public string Of(string now)
        {
            if (now != source)
            {
                source = now;
                text = now == null ? "" : RichText.Grey(now);
            }
            return text;
        }
    }

    // The parts that say something, one to a line, made anew only when a part changes.
    internal sealed class JoinedLines
    {
        // The parts the text was made from, and the text; empty before one.
        private string first;
        private string second;
        private string third;
        private string text = "";

        // The parts joined with a line break between them, those that are null or empty left out.
        public string Of(string a, string b, string c = null)
        {
            if (a != first || b != second || c != third)
            {
                first = a;
                second = b;
                third = c;
                System.Text.StringBuilder joined = new System.Text.StringBuilder();
                Append(joined, a);
                Append(joined, b);
                Append(joined, c);
                text = joined.ToString();
            }
            return text;
        }

        // Adds the part after a line break, unless it is the first; nothing for an empty one.
        private static void Append(System.Text.StringBuilder joined, string part)
        {
            if (string.IsNullOrEmpty(part)) return;
            if (joined.Length > 0) joined.Append('\n');
            joined.Append(part);
        }
    }

    // A number with two decimals that stays one string until the number changes.
    internal sealed class TwoDecimals
    {
        // The number the text was made for, and the text; none before the first.
        private float number;
        private string text;

        // The number with two decimals, in the invariant culture.
        public string Of(float value)
        {
            // A zero keeps its sign, which the text may show.
            if (text == null || value != number || float.IsNaN(value) || (value == 0f && 1f / value != 1f / number))
            {
                number = value;
                text = value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            }
            return text;
        }
    }
}
