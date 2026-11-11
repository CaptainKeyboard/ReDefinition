using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ReDefinition.Atmospheres
{
    // The writer of the dump files of the reconnaissance probe: small JSON documents, numbers in the invariant culture, no engine types.
    // A dump is written the moment its measurement is done, so that a crash later in the start keeps it.
    internal sealed class ReconJson
    {
        private readonly StringBuilder text = new StringBuilder(256);
        private readonly Stack<bool> first = new Stack<bool>();

        internal ReconJson()
        {
            first.Push(true);
        }

        internal ReconJson BeginObject(string name = null)
        {
            Prefix(name);
            text.Append('{');
            first.Push(true);
            return this;
        }

        internal ReconJson EndObject()
        {
            first.Pop();
            text.Append('}');
            return this;
        }

        internal ReconJson BeginArray(string name = null)
        {
            Prefix(name);
            text.Append('[');
            first.Push(true);
            return this;
        }

        internal ReconJson EndArray()
        {
            first.Pop();
            text.Append(']');
            return this;
        }

        internal ReconJson Text(string name, string value)
        {
            Prefix(name);
            text.Append(AtmoResult.Quote(value ?? ""));
            return this;
        }

        internal ReconJson Number(string name, double value)
        {
            Prefix(name);
            text.Append(Num(value));
            return this;
        }

        internal ReconJson Flag(string name, bool value)
        {
            Prefix(name);
            text.Append(value ? "true" : "false");
            return this;
        }

        // An array of numbers on one line, each rounded to the given decimals.
        internal ReconJson Numbers(string name, IList<float> values, int decimals)
        {
            Prefix(name);
            text.Append('[');
            string format = "0." + new string('#', Math.Max(0, decimals));
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) text.Append(',');
                float v = values[i];
                text.Append(float.IsNaN(v) || float.IsInfinity(v) ? "null" : v.ToString(decimals > 0 ? format : "0", CultureInfo.InvariantCulture));
            }
            text.Append(']');
            return this;
        }

        public override string ToString()
        {
            return text.ToString();
        }

        // Writes the document into a file, creating the folder.
        internal void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        }

        // A number as JSON: null for NaN and the infinities (JSON has none).
        internal static string Num(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "null";
            if (Math.Abs(value) < 1e15 && value == Math.Floor(value)) return value.ToString("0", CultureInfo.InvariantCulture);
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private void Prefix(string name)
        {
            bool isFirst = first.Pop();
            if (!isFirst) text.Append(',');
            first.Push(false);
            if (name != null) text.Append(AtmoResult.Quote(name)).Append(':');
        }
    }
}
