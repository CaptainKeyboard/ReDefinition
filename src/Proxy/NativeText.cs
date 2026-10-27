using System.Text;

namespace ReDefinition.Proxy
{
    // The text the proxy answers into a StringBuilder (KspAmdUpscalerProvider,
    // KspXessUpscalerVersion, KspLowLatency), kept as a string that is made anew only
    // when the answer changes. The settings rows ask every frame, and a string made
    // from the builder each time would be garbage in each of them.
    internal static class NativeText
    {
        // Whether the builder holds the same text as the string, compared without making a string of it.
        internal static bool Same(StringBuilder text, string other)
        {
            if (text.Length != other.Length) return false;
            for (int i = 0; i < other.Length; i++)
            {
                if (text[i] != other[i]) return false;
            }
            return true;
        }

        // Makes `source` the builder's text where that differs from it; true when it did.
        internal static bool Refresh(StringBuilder buffer, ref string source)
        {
            if (source != null && Same(buffer, source)) return false;
            source = buffer.ToString();
            return true;
        }
    }
}
