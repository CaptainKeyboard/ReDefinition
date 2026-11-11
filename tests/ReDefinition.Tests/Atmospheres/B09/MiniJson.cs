using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ReDefinition.Tests.Atmospheres.B09
{
    // A small JSON reader for the tests of the dumps and the row file (the test project has no JSON library): objects become
    // Dictionary<string, object>, arrays List<object>, numbers double, strings string, true/false bool, null null.
    internal sealed class MiniJson
    {
        private readonly string text;
        private int at;

        private MiniJson(string text)
        {
            this.text = text;
        }

        internal static object Parse(string text)
        {
            var reader = new MiniJson(text);
            object value = reader.Value();
            reader.Space();
            if (reader.at != text.Length) throw new FormatException("trailing text at " + reader.at);
            return value;
        }

        private void Space()
        {
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
        }

        private object Value()
        {
            Space();
            if (at >= text.Length) throw new FormatException("unexpected end");
            char c = text[at];
            if (c == '{') return Object();
            if (c == '[') return Array();
            if (c == '"') return String();
            if (string.CompareOrdinal(text, at, "true", 0, 4) == 0) { at += 4; return true; }
            if (string.CompareOrdinal(text, at, "false", 0, 5) == 0) { at += 5; return false; }
            if (string.CompareOrdinal(text, at, "null", 0, 4) == 0) { at += 4; return null; }
            return Number();
        }

        private Dictionary<string, object> Object()
        {
            var result = new Dictionary<string, object>();
            at++;
            Space();
            if (text[at] == '}') { at++; return result; }
            while (true)
            {
                Space();
                string key = String();
                Space();
                if (text[at++] != ':') throw new FormatException("expected : at " + at);
                result[key] = Value();
                Space();
                char c = text[at++];
                if (c == '}') return result;
                if (c != ',') throw new FormatException("expected , or } at " + at);
            }
        }

        private List<object> Array()
        {
            var result = new List<object>();
            at++;
            Space();
            if (text[at] == ']') { at++; return result; }
            while (true)
            {
                result.Add(Value());
                Space();
                char c = text[at++];
                if (c == ']') return result;
                if (c != ',') throw new FormatException("expected , or ] at " + at);
            }
        }

        private string String()
        {
            if (text[at] != '"') throw new FormatException("expected a string at " + at);
            at++;
            var sb = new StringBuilder();
            while (text[at] != '"')
            {
                char c = text[at++];
                if (c != '\\') { sb.Append(c); continue; }
                char e = text[at++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(text.Substring(at, 4), 16)); at += 4; break;
                    default: sb.Append(e); break;
                }
            }
            at++;
            return sb.ToString();
        }

        private double Number()
        {
            int start = at;
            while (at < text.Length && "+-0123456789.eE".IndexOf(text[at]) >= 0) at++;
            if (start == at) throw new FormatException("unexpected character at " + at);
            return double.Parse(text.Substring(start, at - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
