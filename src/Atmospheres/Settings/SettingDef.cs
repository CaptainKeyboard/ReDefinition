using System;
using System.Globalization;

namespace ReDefinition.Atmospheres
{
    // The kinds of value a setting holds. All numbers are held as doubles; a choice is the index of its name.
    internal enum SettingKind
    {
        Bool,
        Float,
        Int,
        Choice,
        Text
    }

    // One setting: its key, kind, default, range, tooltip and where it belongs. The table of them is AtmospheresSettings.Definitions and
    // is the one list the file, the settings window, the profiles and the tests read.
    internal sealed class SettingDef
    {
        private SettingDef(string key, SettingKind kind, string category, string tooltip, double defaultValue, string defaultText, double min, double max,
            string[] choices, string greyReason, string profileRow, Func<double, double> normalise)
        {
            Key = key;
            Kind = kind;
            Category = category;
            Tooltip = tooltip;
            Default = defaultValue;
            DefaultText = defaultText;
            Min = min;
            Max = max;
            Choices = choices;
            GreyReason = greyReason ?? "";
            ProfileRow = profileRow ?? "";
            Normalise = normalise;
        }

        // The key in settings.cfg (lowerCamelCase).
        internal string Key { get; }
        internal SettingKind Kind { get; }
        // The group in the settings window (one of AtmospheresSettings.Categories).
        internal string Category { get; }
        // The tooltip in the style of the game's own: Title Case words.
        internal string Tooltip { get; }
        // The default, and for a Text setting its default text. Reset puts a setting back to this value.
        internal double Default { get; }
        internal string DefaultText { get; }
        // The range of a number; for a Choice 0 to the last index.
        internal double Min { get; }
        internal double Max { get; }
        // The names of a Choice, in the order of its enum.
        internal string[] Choices { get; }
        // The id of the reason the setting is greyed out where its prerequisite is missing (one of AtmospheresSettings.GreyReasons), or empty.
        internal string GreyReason { get; }
        // The id of the row of the quality profiles that sets it (one of AtmospheresSettings.ProfileRows), or empty.
        internal string ProfileRow { get; }
        // A special rule applied after the range, or null.
        internal Func<double, double> Normalise { get; }

        // The value in range: NaN stays out (the caller ignores it), a Bool is 0 or 1, an Int is whole.
        internal double Limit(double value)
        {
            if (Normalise != null) value = Normalise(value);
            if (Kind == SettingKind.Bool) return value != 0.0 ? 1.0 : 0.0;
            if (Kind == SettingKind.Int || Kind == SettingKind.Choice) value = Math.Round(value);
            value = value < Min ? Min : value > Max ? Max : value;
            return Kind == SettingKind.Float ? (double)(float)value : value;
        }

        // The text a number is written as.
        internal string Format(double value)
        {
            switch (Kind)
            {
                case SettingKind.Bool: return value != 0.0 ? "True" : "False";
                case SettingKind.Int: return ((long)value).ToString(CultureInfo.InvariantCulture);
                case SettingKind.Choice: return Choices[(int)value];
                case SettingKind.Float: return ((float)value).ToString("R", CultureInfo.InvariantCulture);
                default: return value.ToString("R", CultureInfo.InvariantCulture);
            }
        }

        // The index of a choice name, ignoring case; -1 where it is not one.
        internal int ChoiceIndex(string name)
        {
            for (int i = 0; i < Choices.Length; i++)
            {
                if (string.Equals(Choices[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        internal static SettingDef Bool(string key, bool defaultValue, string category, string tooltip, string grey = "", string row = "")
        {
            return new SettingDef(key, SettingKind.Bool, category, tooltip, defaultValue ? 1.0 : 0.0, "", 0.0, 1.0, null, grey, row, null);
        }

        internal static SettingDef Float(string key, double defaultValue, double min, double max, string category, string tooltip, string grey = "",
            string row = "", Func<double, double> normalise = null)
        {
            // A float is held as the float it is written as, so that the file and the default compare equal.
            return new SettingDef(key, SettingKind.Float, category, tooltip, (float)defaultValue, "", (float)min, (float)max, null, grey, row, normalise);
        }

        internal static SettingDef Int(string key, int defaultValue, int min, int max, string category, string tooltip, string grey = "", string row = "")
        {
            return new SettingDef(key, SettingKind.Int, category, tooltip, defaultValue, "", min, max, null, grey, row, null);
        }

        // A choice among the names of an enum; the default is one of its values, which must be numbered from 0 in order.
        internal static SettingDef Choice(string key, Type enumType, object defaultValue, string category, string tooltip, string grey = "", string row = "")
        {
            string[] names = Enum.GetNames(enumType);
            return new SettingDef(key, SettingKind.Choice, category, tooltip, Convert.ToInt32(defaultValue), "", 0.0, names.Length - 1, names, grey, row, null);
        }

        internal static SettingDef Text(string key, string defaultText, string category, string tooltip, string grey = "", string row = "")
        {
            return new SettingDef(key, SettingKind.Text, category, tooltip, 0.0, defaultText, 0.0, 0.0, null, grey, row, null);
        }
    }
}
