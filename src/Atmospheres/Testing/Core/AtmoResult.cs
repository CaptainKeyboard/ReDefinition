using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The result lines of a series: ATMORESULT {"series":"K-pre","metric":"name","value":1.0} in KSP.log. tools/atmo_series.py reads them
    // and judges the rows of tests/series. A line carries the series, the metric and the value (a number, or a string where the value
    // is text) and, inside a start that switches variants, the variant.
    internal static class AtmoResult
    {
        internal const string Tag = "ATMORESULT ";

        // Where the lines go: the game's log. A test replaces it.
        internal static Action<string> Sink = line => Debug.Log(line);

        // Writes one number.
        internal static void Emit(string series, string metric, double value, string variant = null)
        {
            Sink(Format(series, metric, NumberText(value), variant));
        }

        // Writes one condition as 1 or 0.
        internal static void Emit(string series, string metric, bool value, string variant = null)
        {
            Sink(Format(series, metric, value ? "1" : "0", variant));
        }

        // Writes one text.
        internal static void EmitText(string series, string metric, string value, string variant = null)
        {
            Sink(Format(series, metric, Quote(value ?? ""), variant));
        }

        // The line for a value already written as JSON.
        internal static string Format(string series, string metric, string jsonValue, string variant)
        {
            var sb = new StringBuilder(96);
            sb.Append(Tag).Append("{\"series\":").Append(Quote(series)).Append(",\"metric\":").Append(Quote(metric));
            sb.Append(",\"value\":").Append(jsonValue);
            if (!string.IsNullOrEmpty(variant)) sb.Append(",\"variant\":").Append(Quote(variant));
            sb.Append('}');
            return sb.ToString();
        }

        // A number as JSON: 1.0 for a whole number, the shortest round-trip form otherwise; NaN and the infinities as strings (JSON has
        // none), which the runner judges as failing a numeric threshold.
        internal static string NumberText(double value)
        {
            if (double.IsNaN(value)) return "\"NaN\"";
            if (double.IsPositiveInfinity(value)) return "\"Infinity\"";
            if (double.IsNegativeInfinity(value)) return "\"-Infinity\"";
            if (Math.Abs(value) < 1e15 && value == Math.Floor(value)) return value.ToString("0.0", CultureInfo.InvariantCulture);
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        // A JSON string.
        internal static string Quote(string text)
        {
            var sb = new StringBuilder(text.Length + 2);
            sb.Append('"');
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
