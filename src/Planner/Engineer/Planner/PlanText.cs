using System.Globalization;

namespace ReDefinition.Engineer
{
    // The colours the planner's texts are written in (KSP's rich text) and the small text helpers its model and its rows share.
    internal static class PlanText
    {
        // Orange for a warning, red for what is wrong, green for what is fine, grey for what only describes.
        internal const string Warning = "#f18a24";
        internal const string Problem = "#e6474d";
        internal const string Good = "#8fdc7a";
        internal const string Dim = "#9a9a9a";

        // The text with its first letter capital.
        internal static string Capital(string text)
        {
            return string.IsNullOrEmpty(text) ? "" : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        // The name as it reads inside a sentence, its first letter small.
        internal static string Lower(string name)
        {
            return string.IsNullOrEmpty(name) ? "" : char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        // A share as whole per cent: "13%".
        internal static string Percent(double share)
        {
            return (share * 100.0).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        // The stop's letter in the route: A for the first.
        internal static string Letter(int index)
        {
            return ((char)('A' + index)).ToString();
        }
    }
}
