using System.Collections.Generic;
using System.Globalization;
using System;
using ReDefinition.Settings;

namespace ReDefinition
{
    // One of ReDefinition's own features -- the upscaler, frame generation -- as the
    // profiles and the settings windows see it (docs/development/architecture.md):
    // the name a profile's MODULE node gives, a title, and its settings over
    // ReDefinition's settings file (OwnSettings), which the settings window and KSP's settings
    // dialog edit as a copy and commit through the add-on. Modelled on Community
    // Shaders' Feature (src/Feature.h there), cut to what KSP needs. The modules
    // are OurModules.
    internal interface IGraphicsModule
    {
        // The name in a profile's MODULE node, e.g. "upscaler". Stable: profiles name it.
        string Name { get; }

        string Title { get; }

        IList<ModuleSetting> Settings { get; }

        // A setting by its key in the MODULE node; null where the module has none.
        ModuleSetting Setting(string key);
    }

    // One setting of a module: what a profile's MODULE node sets, and a row under
    // General. Its value travels as text in the invariant culture, as a ConfigNode
    // holds it.
    internal sealed class ModuleSetting
    {
        // The key in a profile's MODULE node. Stable: profiles name it.
        public string Key;
        public string Title;
        public string Tooltip = "";

        // A profile sets quality only, as for the other mods.
        public SettingKind Kind = SettingKind.Other;
        public SettingControl Control = SettingControl.Toggle;

        // Slider: its range, whether it snaps to whole numbers, and into how many
        // steps a unit is cut -- 0 for none.
        public float Min;
        public float Max;
        public bool WholeNumbers;
        public float StepsPerUnit;

        // Choice: the values in the order the row shows them, left to right.
        public string[] Choices;

        // What the row's value column shows for a value; null shows the value.
        public Func<string, string> Label;

        // Where its row stands under General, among every module's rows.
        public int Order;

        // The tab its row stands in; the hotkeys stand under Keys.
        public SettingCategory Row = SettingCategory.General;

        public Func<OwnSettings, string> Read;
        public Action<OwnSettings, string> Write;

        // Whether the row can be changed now; null is always.
        public Func<OwnSettings, bool> Interactable;

        // Null where the setting can take the value; otherwise why not. A choice
        // by its name, case aside -- never a number, which Enum.Parse would
        // take.
        public string Refusal(string value)
        {
            if (value == null) return "no value";
            switch (Control)
            {
                case SettingControl.Toggle:
                    bool on;
                    return bool.TryParse(value, out on) ? null : "not True or False";

                case SettingControl.Binding:
                    return KeyCombination.IsText(value) ? null : "not a key binding";

                case SettingControl.Slider:
                    double number;
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                        || double.IsNaN(number) || double.IsInfinity(number))
                        return "not a number";
                    if (number < Min - 1e-6 || number > Max + 1e-6)
                        return "outside " + Min.ToString(CultureInfo.InvariantCulture) + " .. "
                               + Max.ToString(CultureInfo.InvariantCulture);
                    if (WholeNumbers && Math.Abs(number - Math.Round(number)) > 1e-6) return "not a whole number";
                    return null;

                default:
                    return Choice(value) != null ? null : "not one of " + string.Join(", ", Choices ?? new string[0]);
            }
        }

        // The value as the setting holds it: a choice in its own spelling, True or
        // False, a number as a ConfigNode writes it.
        public string Normalize(string value)
        {
            switch (Control)
            {
                case SettingControl.Toggle:
                    bool on;
                    return bool.TryParse(value, out on) ? (on ? "True" : "False") : value;

                case SettingControl.Binding:
                    return KeyCombination.IsText(value) ? KeyCombination.Parse(value).ToString() : value;

                case SettingControl.Slider:
                    double number;
                    return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                        ? ((float)number).ToString("R", CultureInfo.InvariantCulture)
                        : value;

                default:
                    return Choice(value) ?? value;
            }
        }

        private string Choice(string value)
        {
            if (Choices == null || value == null) return null;
            foreach (string choice in Choices)
                if (string.Equals(choice, value, StringComparison.OrdinalIgnoreCase)) return choice;
            return null;
        }
    }

    // A module made of its settings.
    internal sealed class GraphicsModule : IGraphicsModule
    {
        private readonly List<ModuleSetting> settings;

        public GraphicsModule(string name, string title, params ModuleSetting[] settings)
        {
            Name = name;
            Title = title;
            this.settings = new List<ModuleSetting>(settings);
        }

        public string Name { get; private set; }

        public string Title { get; private set; }

        public IList<ModuleSetting> Settings
        {
            get { return settings.AsReadOnly(); }
        }

        public ModuleSetting Setting(string key)
        {
            foreach (ModuleSetting setting in settings)
                if (setting.Key == key) return setting;
            return null;
        }
    }
}
