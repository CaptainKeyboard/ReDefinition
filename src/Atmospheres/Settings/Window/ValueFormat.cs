using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ReDefinition.Atmospheres
{
    // How a value is written in the window: switches as On and Off, a choice by its name in words, a number with its unit.
    internal static class ValueFormat
    {
        private static readonly Dictionary<string, string> Units = new Dictionary<string, string>
        {
            { AtmospheresSettings.Keys.SimSliceMs, " ms" },
            { AtmospheresSettings.Keys.AlertWindMs, " m/s" },
            { AtmospheresSettings.Keys.LaunchMaxWindMs, " m/s" },
            { AtmospheresSettings.Keys.BlizzardVisibilityM, " m" },
            { AtmospheresSettings.Keys.LaunchMinVisibilityM, " m" },
            { AtmospheresSettings.Keys.IceDrawDistanceKm, " km" },
            { AtmospheresSettings.Keys.AlertLeadHours, " h" },
            { AtmospheresSettings.Keys.ForecastHorizonHours, " h" },
            { AtmospheresSettings.Keys.LaunchLeadHours, " h" },
            { AtmospheresSettings.Keys.OrbitMapWidth, " px" }
        };

        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            { "Rse", "RSE" },
            { "Eve", "EVE" },
            { "Shell2D", "Shell 2D" },
            { "Own", "Built In" },
            { "Ours", "Ours" }
        };

        // The unit a key's number is written with, or empty.
        internal static string Unit(string key)
        {
            string unit;
            return Units.TryGetValue(key, out unit) ? unit : "";
        }

        // A choice's name in words: "OrbitAndGround" is "Orbit And Ground".
        internal static string ChoiceName(string name)
        {
            string special;
            if (name != null && Names.TryGetValue(name, out special)) return special;
            if (string.IsNullOrEmpty(name)) return "";
            var text = new StringBuilder(name.Length + 4);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]))) text.Append(' ');
                text.Append(c);
            }
            return text.ToString();
        }

        // The value of a setting as the window shows it.
        internal static string Of(SettingDef def, double value)
        {
            switch (def.Kind)
            {
                case SettingKind.Bool: return value != 0.0 ? "On" : "Off";
                case SettingKind.Choice: return ChoiceName(def.Choices[(int)value]);
                case SettingKind.Int: return ((long)value).ToString(CultureInfo.InvariantCulture) + Unit(def.Key);
                case SettingKind.Float: return Number(value) + Unit(def.Key);
                default: return "";
            }
        }

        // A number to at most three digits after the point, without trailing zeros.
        internal static string Number(double value)
        {
            string text = value.ToString("0.###", CultureInfo.InvariantCulture);
            return text == "-0" ? "0" : text;
        }

        // The range of a number as the tooltip says it.
        internal static string Range(SettingDef def)
        {
            switch (def.Kind)
            {
                case SettingKind.Float: return Number(def.Min) + " to " + Number(def.Max) + Unit(def.Key);
                case SettingKind.Int: return ((long)def.Min).ToString(CultureInfo.InvariantCulture) + " to " + ((long)def.Max).ToString(CultureInfo.InvariantCulture) + Unit(def.Key);
                default: return "";
            }
        }

        // The default of a setting as the window shows it.
        internal static string DefaultOf(SettingDef def)
        {
            return def.Kind == SettingKind.Text ? (def.DefaultText.Length == 0 ? "empty" : def.DefaultText) : Of(def, def.Default);
        }
    }
}
