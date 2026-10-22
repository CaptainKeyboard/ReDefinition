using System.Collections.Generic;
using System.Globalization;
using System;
using ReDefinition.Settings;

namespace ReDefinition
{
    // One of ReDefinition's own features -- the upscaler, frame generation, raytracing
    // and the others in OurModules -- as the profiles and the settings windows see it
    // (docs/development/architecture.md):
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

    // What choosing a technique waits for: the text for its tooltip, and the way to it -- the download or the
    // steps -- which calls the continuation once the files are in place, and not when the player cancels.
    internal sealed class FilesNeeded
    {
        public string Text;
        public Action<Action> Begin;
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

        // Whether the row can be changed only while a graphics profile is chosen:
        // the upscaler's and frame generation's, which only a profile makes
        // active. The flight UI's and the scaling are the player's at any time.
        public bool NeedsProfile = true;

        // Set by a profile only where none is chosen yet: the profile that makes
        // ReDefinition active switches it on. Once one is, it is the player's
        // switch, which choosing another profile, or the reset, leaves as it is.
        public bool OnlyActivates;

        // When a change takes effect: at once, unless the row says otherwise.
        public ApplyWindow TakesEffect = ApplyWindow.Live;

        // Where that depends on the change (from the value before to the one after): in place of TakesEffect.
        public Func<string, string, ApplyWindow> EffectOf;

        // The heading the row stands under in its tab, where the tab groups
        // ReDefinition's own rows (Gameplay: flight; Mods / Toolbar: scaling).
        public string Section;
        public SettingControl Control = SettingControl.Toggle;

        // Slider: its range, whether it snaps to whole numbers, and into how many
        // steps a unit is cut -- 0 for none.
        public float Min;
        public float Max;
        public bool WholeNumbers;
        public float StepsPerUnit;

        // Choice: the values in the order the row shows them, left to right.
        public string[] Choices;

        // A choice drawn as a row of buttons, one lit, instead of a slider through the values (a
        // technique to pick rather than a step to move along).
        public bool Radio;

        // For a Radio row: a button's text, where the value is not it.
        public Func<string, string> ChoiceLabel;

        // For a Radio row: a button's own tooltip, in place of the row's.
        public Func<string, string> ChoiceTooltip;

        // What the value column shows where it depends on the other settings too; in place of Label.
        public Func<OwnSettings, string, string> LabelWith;

        // For a Radio row: null where the choice can be taken now, otherwise why not, which the greyed
        // button's tooltip says.
        public Func<OwnSettings, string, string> Unavailable;

        // For a Radio row: what the choice still waits for, or null. A choice of the card's reach whose files are
        // missing is lit, and choosing it opens their download first (TechniqueFiles).
        public Func<OwnSettings, string, FilesNeeded> Files;

        // What the row's value column shows for a value; null shows the value.
        public Func<string, string> Label;

        // Where its row stands under General, among every module's rows.
        public int Order;

        // A row of Detail's own group rather than of Display's basics: the single settings of a
        // feature, as the mods' are.
        public bool Detail;

        // The tab its row stands in; the hotkeys stand under Keys.
        public SettingCategory Row = SettingCategory.General;

        public Func<OwnSettings, string> Read;
        public Action<OwnSettings, string> Write;

        // Whether the row can be changed now; null is always.
        public Func<OwnSettings, bool> Interactable;

        // Whether the row stands in the window at all; null is always. A row for
        // another mod's part only where that mod is installed.
        public Func<bool> Present;

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
        // Asked for every frame the settings window is open.
        private readonly IList<ModuleSetting> readOnly;

        public GraphicsModule(string name, string title, params ModuleSetting[] settings)
        {
            Name = name;
            Title = title;
            this.settings = new List<ModuleSetting>(settings);
            readOnly = this.settings.AsReadOnly();
        }

        public string Name { get; private set; }

        public string Title { get; private set; }

        public IList<ModuleSetting> Settings
        {
            get { return readOnly; }
        }

        public ModuleSetting Setting(string key)
        {
            foreach (ModuleSetting setting in settings)
                if (setting.Key == key) return setting;
            return null;
        }
    }
}
