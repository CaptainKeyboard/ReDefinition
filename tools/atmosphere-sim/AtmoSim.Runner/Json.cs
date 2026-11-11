using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AtmoSim.Runner
{
    // A small JSON writer for dictionaries, lists, arrays, numbers, strings and booleans (NaN and infinity become null).
    public static class Json
    {
        public static string Write(object o)
        {
            var sb = new StringBuilder();
            Put(sb, o, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        private static void Put(StringBuilder sb, object o, int depth)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is string s) { Str(sb, s); return; }
            if (o is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (o is double d) { Num(sb, d); return; }
            if (o is float f) { Num(sb, f); return; }
            if (o is int || o is long || o is ulong || o is uint || o is short || o is byte) { sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture)); return; }
            if (o is IDictionary dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('\n').Append(' ', 2 * (depth + 1));
                    Str(sb, e.Key.ToString()); sb.Append(": ");
                    Put(sb, e.Value, depth + 1);
                }
                sb.Append('\n').Append(' ', 2 * depth).Append('}');
                return;
            }
            if (o is double[] da)
            {
                sb.Append('[');
                for (int i = 0; i < da.Length; i++) { if (i > 0) sb.Append(','); Num(sb, da[i]); }
                sb.Append(']');
                return;
            }
            if (o is IEnumerable en)
            {
                sb.Append('[');
                bool first = true;
                foreach (object x in en)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Put(sb, x, depth + 1);
                }
                sb.Append(']');
                return;
            }
            Str(sb, o.ToString());
        }

        private static void Num(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
            else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c < 32) sb.Append("\\u" + ((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
        }
    }
}
