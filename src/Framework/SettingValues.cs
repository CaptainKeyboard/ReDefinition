using System;
using System.Globalization;
using UnityEngine;

namespace ReDefinition.Framework
{
    // Values as text, the way the window, the store and bundled.cfg hold them: a
    // member's value turned into the invariant text a ConfigNode holds and back,
    // and two texts compared -- callable without the game, for the decisions,
    // their tests and the check outside the game.
    internal static class SettingValues
    {
        // As the values they are -- "0.9" is "0.899999976", "true" is "True" --
        // and otherwise exactly: TUFX's profile names are case-sensitive keys.
        public static bool Same(string a, string b)
        {
            if (a == null || b == null) return a == b;
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;

            bool p, q;
            if (bool.TryParse(a, out p) && bool.TryParse(b, out q)) return p == q;

            double x, y;
            if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                return Math.Abs(x - y) <= 1e-5 * Math.Max(1.0, Math.Abs(x));

            // A list of numbers, as Scatterer's cascade splits: item by item.
            // Text with a comma in it is no such list, and compares exactly.
            string[] listA = a.Split(',');
            string[] listB = b.Split(',');
            if (listA.Length < 2 || listA.Length != listB.Length) return false;
            for (int i = 0; i < listA.Length; i++)
            {
                if (!double.TryParse(listA[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                    || !double.TryParse(listB[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                    || Math.Abs(x - y) > 1e-5 * Math.Max(1.0, Math.Abs(x)))
                    return false;
            }
            return true;
        }

        // A value as a ConfigNode holds it: invariant, floats round-tripping.
        public static string Text(object value)
        {
            if (value == null) return null;
            if (value is float) return ((float)value).ToString("R", CultureInfo.InvariantCulture);
            if (value is double) return ((double)value).ToString("R", CultureInfo.InvariantCulture);
            if (value is bool) return (bool)value ? "True" : "False";
            // As a ConfigNode holds a vector: its components, comma-separated.
            if (value is Vector3)
            {
                Vector3 v = (Vector3)value;
                return Text(v.x) + "," + Text(v.y) + "," + Text(v.z);
            }
            IFormattable formattable = value as IFormattable;
            return formattable != null ? formattable.ToString(null, CultureInfo.InvariantCulture) : value.ToString();
        }

        // Whether Parse takes that type -- a member of any other has no text it
        // could be set from.
        public static bool CanParse(Type type)
        {
            return type == typeof(bool) || type == typeof(int) || type == typeof(float) || type == typeof(double)
                   || type == typeof(string) || type == typeof(Vector3) || (type != null && type.IsEnum);
        }

        // The text back into the member's type.
        public static object Parse(string text, Type type)
        {
            if (text == null) throw new ArgumentNullException("text");
            if (type == typeof(bool)) return bool.Parse(text);
            if (type == typeof(int))
            {
                // A file may write 128 as 128.0; a fraction, or a number past an
                // int's range, is no value the field can hold -- refused, not
                // rounded or wrapped.
                double number = Finite(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture), text);
                if (Math.Abs(number - Math.Round(number)) > 1e-6 || number < int.MinValue || number > int.MaxValue)
                    throw new FormatException("'" + text + "' is not a whole number an int holds.");
                return (int)Math.Round(number);
            }
            if (type == typeof(float))
                return (float)Finite(float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture), text);
            if (type == typeof(double))
                return Finite(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture), text);
            if (type.IsEnum) return Enum.Parse(type, text, true);
            if (type == typeof(string)) return text;
            if (type == typeof(Vector3))
            {
                string[] parts = text.Split(',');
                if (parts.Length != 3) throw new FormatException("A vector needs three components: '" + text + "'.");
                // Its components as any float.
                return new Vector3((float)Parse(parts[0].Trim(), typeof(float)), (float)Parse(parts[1].Trim(), typeof(float)),
                    (float)Parse(parts[2].Trim(), typeof(float)));
            }
            throw new NotSupportedException("A " + type.Name + " setting cannot be set from text.");
        }

        // The same text for the same value, however the mod's file wrote it.
        public static string Normalize(string text, Type type)
        {
            try
            {
                return Text(Parse(text, type));
            }
            catch (Exception)
            {
                return text;
            }
        }

        public static string Invert(string text)
        {
            return text == null ? null : Text(!bool.Parse(text));
        }

        // NaN and the infinities parse, and every comparison lets NaN through:
        // no value for a setting.
        private static double Finite(double value, string text)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new FormatException("'" + text + "' is not a finite number.");
            return value;
        }
    }
}
