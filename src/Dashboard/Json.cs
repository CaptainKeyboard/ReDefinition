using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ReDefinition.Dashboard
{
    /// <summary>
    /// The little JSON this mod needs: values out, objects and arrays among them, and flat objects of
    /// strings, numbers and arrays of those in. KSP's Mono has no JSON library worth depending on.
    /// </summary>
    internal static class Json
    {
        // An object as JSON text, its values written as WriteValue does.
        public static string Encode(IDictionary<string, object> values)
        {
            var sb = new StringBuilder(256);
            WriteObject(sb, values);
            return sb.ToString();
        }

        // An object written to `sb`; a nested one goes through the same builder.
        private static void WriteObject(StringBuilder sb, IDictionary<string, object> values)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in values)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, kv.Key);
                sb.Append(':');
                WriteValue(sb, kv.Value);
            }
            sb.Append('}');
        }

        /// <summary>JSON already written, put in as it is.</summary>
        public sealed class Raw
        {
            // The JSON text itself.
            public readonly string Text;
            // Wraps JSON text written before, for instance a value cached between updates.
            public Raw(string text) { Text = text; }
        }

        // One value as JSON text; the dashboard's server compares these to send only what changed.
        public static string Value(object v)
        {
            var sb = new StringBuilder(24);
            WriteValue(sb, v);
            return sb.ToString();
        }

        // Writes null, Raw, bools, strings, doubles, floats, ints, longs, string-keyed dictionaries as objects and any
        // other enumerable as an array; anything else, other number types included, as its ToString in quotes.
        private static void WriteValue(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case Raw raw: sb.Append(raw.Text); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string s: WriteString(sb, s); break;
                case double d: WriteNumber(sb, d); break;
                case float f: WriteNumber(sb, f); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case IDictionary<string, object> map: WriteObject(sb, map); break;
                case IEnumerable e:
                    sb.Append('[');
                    bool first = true;
                    foreach (var x in e)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteValue(sb, x);
                    }
                    sb.Append(']');
                    break;
                default: WriteString(sb, v.ToString()); break;
            }
        }

        // NaN and Infinity are not JSON; KSP produces them (e.g. an orbit without an encounter).
        private static void WriteNumber(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
            else sb.Append(d.ToString("G10", CultureInfo.InvariantCulture)); // drops the noise of a rounded double (38123.400000000001)
        }

        // A string in quotes, with quotes, backslashes and control characters escaped.
        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>Parses {"key": value, ...} where a value is a string, a number or an array of those.</summary>
        public static Dictionary<string, object> DecodeObject(string text)
        {
            int i = 0;
            var result = new Dictionary<string, object>();
            SkipWs(text, ref i);
            Expect(text, ref i, '{');
            SkipWs(text, ref i);
            if (Peek(text, i) == '}') return result;
            while (true)
            {
                SkipWs(text, ref i);
                string key = ReadString(text, ref i);
                SkipWs(text, ref i);
                Expect(text, ref i, ':');
                result[key] = ReadValue(text, ref i);
                SkipWs(text, ref i);
                char c = Peek(text, i);
                i++;
                if (c == '}') return result;
                if (c != ',') throw new FormatException("expected , or }");
            }
        }

        // A string, a number (as double), an array of values, or true, false and null, which come back as
        // those words in strings.
        private static object ReadValue(string t, ref int i)
        {
            SkipWs(t, ref i);
            char c = Peek(t, i);
            if (c == '"') return ReadString(t, ref i);
            if (c == '[')
            {
                i++;
                var list = new List<object>();
                SkipWs(t, ref i);
                if (Peek(t, i) == ']') { i++; return list; }
                while (true)
                {
                    list.Add(ReadValue(t, ref i));
                    SkipWs(t, ref i);
                    char n = Peek(t, i);
                    i++;
                    if (n == ']') return list;
                    if (n != ',') throw new FormatException("expected , or ]");
                }
            }
            int start = i;
            while (i < t.Length && "+-0123456789.eE".IndexOf(t[i]) >= 0) i++;
            if (start == i)
            {
                foreach (string lit in new[] { "true", "false", "null" })
                    if (string.CompareOrdinal(t, i, lit, 0, lit.Length) == 0) { i += lit.Length; return lit; }
                throw new FormatException("unexpected value");
            }
            return double.Parse(t.Substring(start, i - start), CultureInfo.InvariantCulture);
        }

        // A quoted string at i with its escapes resolved; i ends past the closing quote.
        private static string ReadString(string t, ref int i)
        {
            Expect(t, ref i, '"');
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= t.Length) throw new FormatException("unterminated string");
                char c = t[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = t[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(t.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
        }

        // The character at i, or a zero character past the end.
        private static char Peek(string t, int i) { return i < t.Length ? t[i] : '\0'; }
        // Moves i past any white space.
        private static void SkipWs(string t, ref int i) { while (i < t.Length && char.IsWhiteSpace(t[i])) i++; }
        // Steps past c at i, or throws where another character stands there.
        private static void Expect(string t, ref int i, char c)
        {
            if (Peek(t, i) != c) throw new FormatException("expected " + c);
            i++;
        }
    }
}
