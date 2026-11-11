using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Reads the keys of one ConfigNode with a default for each, and turns every key that cannot be read into a warning (never an
    // exception): the value stays the default. A node that is null reads as an empty one. Numbers use the invariant culture.
    internal sealed class BodyNodeReader
    {
        private readonly ConfigNode node;
        private readonly string path;
        private readonly List<string> warnings;

        internal BodyNodeReader(ConfigNode node, string path, List<string> warnings)
        {
            this.node = node;
            this.path = path;
            this.warnings = warnings;
        }

        internal bool Present
        {
            get { return node != null; }
        }

        internal bool Has(string key)
        {
            return node != null && node.HasValue(key);
        }

        // The sub-nodes of that name, in file order.
        internal ConfigNode[] Nodes(string name)
        {
            return node == null ? new ConfigNode[0] : node.GetNodes(name);
        }

        internal BodyNodeReader Child(string name)
        {
            ConfigNode[] found = Nodes(name);
            if (found.Length > 1) Warn(name, "the node is there " + found.Length + " times; the first one counts");
            return new BodyNodeReader(found.Length == 0 ? null : found[0], path + "." + name, warnings);
        }

        internal BodyNodeReader Wrap(ConfigNode child, string name)
        {
            return new BodyNodeReader(child, path + "." + name, warnings);
        }

        // The names of the sub-nodes, in file order.
        internal List<string> NodeNames()
        {
            var names = new List<string>();
            if (node == null) return names;
            foreach (ConfigNode child in node.nodes) names.Add(child.name);
            return names;
        }

        // The names of the keys of the node, in file order.
        internal List<string> Keys()
        {
            var names = new List<string>();
            if (node == null) return names;
            foreach (ConfigNode.Value value in node.values) names.Add(value.name);
            return names;
        }

        internal string Raw(string key)
        {
            if (node == null) return null;
            string value = node.GetValue(key);
            return value == null ? null : value.Trim();
        }

        internal string String(string key, string fallback)
        {
            string value = Raw(key);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        internal double Double(string key, double fallback, double min = double.NegativeInfinity, double max = double.PositiveInfinity)
        {
            string text = Raw(key);
            if (text == null) return fallback;
            double value;
            if (!TryNumber(text, out value))
            {
                Warn(key, "'" + text + "' is not a number; " + Format(fallback) + " is used");
                return fallback;
            }
            if (value < min || value > max)
            {
                double clamped = Math.Min(max, Math.Max(min, value));
                Warn(key, Format(value) + " is outside " + Format(min) + " to " + Format(max) + "; " + Format(clamped) + " is used");
                return clamped;
            }
            return value;
        }

        // A number or the word Auto (NaN).
        internal double DoubleOrAuto(string key, double min, double max)
        {
            string text = Raw(key);
            if (text == null || text.Equals("Auto", StringComparison.OrdinalIgnoreCase)) return double.NaN;
            return Double(key, double.NaN, min, max);
        }

        internal float Float(string key, float fallback, float min = float.NegativeInfinity, float max = float.PositiveInfinity)
        {
            return (float)Double(key, fallback, min, max);
        }

        internal int Int(string key, int fallback, int min = int.MinValue, int max = int.MaxValue)
        {
            double value = Double(key, fallback, min, max);
            return (int)Math.Round(value);
        }

        internal bool Bool(string key, bool fallback)
        {
            string text = Raw(key);
            if (text == null) return fallback;
            bool value;
            if (bool.TryParse(text, out value)) return value;
            Warn(key, "'" + text + "' is not true or false; " + (fallback ? "true" : "false") + " is used");
            return fallback;
        }

        internal T Enum<T>(string key, T fallback) where T : struct
        {
            string text = Raw(key);
            if (text == null) return fallback;
            T value;
            if (System.Enum.TryParse(text, true, out value) && System.Enum.IsDefined(typeof(T), value)) return value;
            Warn(key, "'" + text + "' is not one of " + string.Join(", ", System.Enum.GetNames(typeof(T))) + "; " + fallback + " is used");
            return fallback;
        }

        internal Color? Colour(string key)
        {
            string text = Raw(key);
            if (text == null) return null;
            string[] parts = text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            double r, g, b;
            double a = 1.0;
            if ((parts.Length == 3 || parts.Length == 4) && TryNumber(parts[0], out r) && TryNumber(parts[1], out g) && TryNumber(parts[2], out b)
                && (parts.Length == 3 || TryNumber(parts[3], out a)))
            {
                return new Color((float)r, (float)g, (float)b, (float)a);
            }
            Warn(key, "'" + text + "' is not a colour (r, g, b or r, g, b, a); the default is used");
            return null;
        }

        // A comma separated list; empty where the key is missing.
        internal List<string> List(string key)
        {
            var result = new List<string>();
            string text = Raw(key);
            if (text == null) return result;
            foreach (string part in text.Split(','))
            {
                string item = part.Trim();
                if (item.Length > 0) result.Add(item);
            }
            return result;
        }

        // A comma separated list of numbers; null where the key is missing or one entry is not a number.
        internal double[] Numbers(string key)
        {
            string text = Raw(key);
            if (text == null) return null;
            string[] parts = text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!TryNumber(parts[i], out result[i]))
                {
                    Warn(key, "'" + parts[i] + "' is not a number; the key is ignored");
                    return null;
                }
            }
            return result;
        }

        internal void Warn(string key, string message)
        {
            if (warnings != null) warnings.Add(path + (key.Length == 0 ? "" : "." + key) + ": " + message);
        }

        internal static bool TryNumber(string text, out double value)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value)) return true;
            value = 0;
            return false;
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
