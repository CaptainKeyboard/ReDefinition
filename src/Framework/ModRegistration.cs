using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ReDefinition.Framework
{
    // How a requirement tests a setting's value (REQUIRES).
    internal enum RequirementTest
    {
        Equals,
        AtMost,
        AtLeast,
        Highest,
        Check,
    }

    // One setting of a registered mod, as its SETTING node gives it.
    internal sealed class SettingRegistration
    {
        public string Name = "";
        public string Member;
        public string Title = "";
        public string Tooltip = "";
        public string Default;
        public SettingKind Kind = SettingKind.Other;
        // The tab its row is shown in; null: not shown.
        public SettingCategory? Row;
        public int? Order;
        public ApplyWindow TakesEffect = ApplyWindow.NextScene;
        // Both or neither: a slider.
        public float? Min;
        public float? Max;
        // Null: whole numbers for an int, fractions otherwise.
        public bool? Whole;
        public string[] Choices;
        public string[] Labels;
        public bool Invert;
        // A reason: where a build lacks the member, only this setting goes; True:
        // it goes without a word.
        public string Optional;
        // Where a build lacks the member, the mod goes as a whole.
        public bool Required;
        // A reason this setting is never bundled, though the mod has it.
        public string LeftOut;
        public string After;
        public string ShaderGlobal;
        // Null: as the mod's saving.
        public bool? PerSave;
        public string LeftOutWith;
        public string RowUnless;
        public string Behaviour;
    }

    // A build of a mod, told by a member it has.
    internal sealed class BuildRegistration
    {
        public string Name = "";
        public string Has = "";
    }

    // Values of settings for one build (none: every build): a DEFAULTS, PROFILE or
    // ALL_PROFILES block, taken by ModDefaults.Fitting.
    internal abstract class ValueBlock
    {
        public string Build;
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>();

        public bool ForEveryBuild
        {
            get { return string.IsNullOrEmpty(Build); }
        }

        // The block's key besides `build` that names no setting.
        public abstract string OwnKey { get; }

        // What the block is called where a node inside it is reported.
        public abstract string Kind { get; }

        // How a problem names the block, after its mod.
        public abstract string Where(string mod);

        protected string ForBuild()
        {
            return ForEveryBuild ? "" : " for build " + Build;
        }
    }

    // Defaults for one build, over the settings' own.
    internal sealed class DefaultsRegistration : ValueBlock
    {
        public string Version;

        public override string OwnKey
        {
            get { return "version"; }
        }

        public override string Kind
        {
            get { return "defaults"; }
        }

        public override string Where(string mod)
        {
            return mod + ", defaults" + ForBuild();
        }
    }

    // What a graphics profile sets for one build, over the defaults: quality
    // settings only.
    internal sealed class ProfileRegistration : ValueBlock
    {
        public string Name = "";

        public override string OwnKey
        {
            get { return "name"; }
        }

        public override string Kind
        {
            get { return "a profile block"; }
        }

        public override string Where(string mod)
        {
            return mod + ", profile '" + Name + "'" + ForBuild();
        }
    }

    // What every graphics profile sets for one build, whatever the setting's kind:
    // what makes the mod fit ReDefinition -- its own antialiasing off, say, since
    // the upscaler antialiases the image.
    internal sealed class AllProfilesRegistration : ValueBlock
    {
        public override string OwnKey
        {
            get { return null; }
        }

        public override string Kind
        {
            get { return "a block for every profile"; }
        }

        public override string Where(string mod)
        {
            return mod + ", every profile" + ForBuild();
        }
    }

    // What a mod needs of another setting while it is installed.
    internal sealed class RequirementRegistration
    {
        public string Setting = "";
        public RequirementTest Test;
        // The value of equals, atMost or atLeast; the check's name for check.
        public string Value;
        public string Fix;
        public bool Lock;
        public string Reason = "";
        public string WhenInstalled;
    }

    // A mod's registration: everything ReDefinition needs to bundle its settings,
    // from a MOD_SETTINGS node in the GameDatabase -- docs/modders/registering-a-mod.md
    // is the specification this reads by. Read and checked for its own sake only:
    // whether its members exist is the registry's to find out, with the mod
    // loaded.
    //
    // As GraphicsProfile: every problem goes into `problems`, naming the mod and
    // the setting, and what is usable is kept. A key with a problem is ignored; a
    // setting without its name or member, a build, a requirement that cannot be
    // read, is left out; a mod without its name or detect is skipped.
    internal sealed class ModRegistration
    {
        public const string NodeName = "MOD_SETTINGS";
        public const string SettingNodeName = "SETTING";
        public const string BuildNodeName = "BUILD";
        public const string DefaultsNodeName = "DEFAULTS";
        public const string RequiresNodeName = "REQUIRES";
        public const string ProfileNodeName = "PROFILE";
        public const string AllProfilesNodeName = "ALL_PROFILES";

        private static readonly string[] ModKeys =
        {
            "name", "title", "detect", "needs", "version", "save", "ready", "saving", "window", "button", "toolbarControl",
            "tab", "behaviour",
        };

        private static readonly string[] SettingKeys =
        {
            "name", "member", "title", "tooltip", "default", "kind", "row", "order", "takesEffect", "min", "max",
            "whole", "choices", "labels", "invert", "optional", "after", "shaderGlobal", "perSave", "leftOutWith",
            "rowUnless", "behaviour", "leftOut", "required",
        };

        private static readonly string[] BuildKeys = { "name", "has" };

        private static readonly string[] RequiresKeys =
        {
            "setting", "equals", "atMost", "atLeast", "highest", "check", "fix", "lock", "reason", "whenInstalled",
        };

        private static readonly string[] TestKeys = { "equals", "atMost", "atLeast", "highest", "check" };

        // Where a row or an Advanced button can be.
        private static readonly SettingCategory[] Tabs =
        {
            SettingCategory.General, SettingCategory.ShadowsAndReflections, SettingCategory.Planets,
            SettingCategory.Effects,
        };

        private const string TabNames = "General, ShadowsAndReflections, Planets or Effects";

        // When a change can take effect, as the guide names them.
        private static readonly ApplyWindow[] Windows = { ApplyWindow.Live, ApplyWindow.NextScene, ApplyWindow.Restart };

        public string Name = "";
        public string Title = "";
        public string Detect = "";
        // Member paths its settings cannot be trusted without.
        public string[] Needs = new string[0];
        public string Version;
        public string Save;
        // A member path that holds nothing, or False, while the mod cannot take
        // values yet.
        public string Ready;
        public SettingsSaving Saving = SettingsSaving.AtEveryStart;
        public string Window;
        public string Button;
        public string ToolbarControl;
        public SettingCategory? Tab;
        public string Behaviour;

        public readonly List<SettingRegistration> Settings = new List<SettingRegistration>();
        public readonly List<BuildRegistration> Builds = new List<BuildRegistration>();
        public readonly List<DefaultsRegistration> Defaults = new List<DefaultsRegistration>();
        public readonly List<ProfileRegistration> Profiles = new List<ProfileRegistration>();
        public readonly List<AllProfilesRegistration> AllProfiles = new List<AllProfilesRegistration>();
        public readonly List<RequirementRegistration> Requirements = new List<RequirementRegistration>();

        public SettingRegistration Setting(string name)
        {
            foreach (SettingRegistration setting in Settings)
                if (setting.Name == name) return setting;
            return null;
        }

        // Null only where the node has no usable name or detect.
        public static ModRegistration FromConfigNode(ConfigNode node, List<string> problems)
        {
            if (node == null) return null;

            string name = Last(node, "name", NodeName, problems);
            if (name == null)
            {
                problems.Add(NodeName + " without a name -- skipped.");
                return null;
            }
            string where = "Mod '" + name + "'";
            if (!IsId(name, false))
            {
                problems.Add(where + ": the name may hold only lower-case letters, digits and _ -- skipped.");
                return null;
            }
            string detect = Last(node, "detect", where, problems);
            if (detect == null)
            {
                problems.Add(where + ": no detect -- skipped.");
                return null;
            }

            ModRegistration mod = new ModRegistration { Name = name, Detect = detect };
            mod.Title = Last(node, "title", where, problems) ?? name;
            string needs = Last(node, "needs", where, problems);
            if (needs != null)
            {
                List<string> paths = new List<string>(List(needs));
                if (paths.RemoveAll(path => path.Length == 0) > 0) problems.Add(where + ": an empty entry in needs -- ignored.");
                mod.Needs = paths.ToArray();
            }
            mod.Version = Last(node, "version", where, problems);
            mod.Save = Last(node, "save", where, problems);
            mod.Ready = Last(node, "ready", where, problems);
            mod.Saving = mod.Save != null ? SettingsSaving.InModFiles : SettingsSaving.AtEveryStart;
            string saving = Last(node, "saving", where, problems);
            if (saving != null)
            {
                SettingsSaving parsed;
                if (TryName(saving, out parsed)) mod.Saving = parsed;
                else problems.Add(where + ": saving '" + saving + "' is not InModFiles, PerSave or AtEveryStart -- ignored.");
            }
            mod.Window = Last(node, "window", where, problems);
            mod.Button = Last(node, "button", where, problems);
            mod.ToolbarControl = Last(node, "toolbarControl", where, problems);
            if ((mod.Button != null || mod.ToolbarControl != null) && mod.Window == null)
                problems.Add(where + ": a toolbar button without a window -- the button stays, since ReDefinition's"
                             + " window could not open the mod's own.");
            mod.Behaviour = Last(node, "behaviour", where, problems);
            string tab = Last(node, "tab", where, problems);
            if (tab != null)
            {
                SettingCategory parsed;
                if (TryTab(tab, out parsed)) mod.Tab = parsed;
                else problems.Add(where + ": tab '" + tab + "' is not " + TabNames + " -- ignored.");
            }
            Unknown(node, ModKeys, where, problems);

            foreach (ConfigNode child in node.GetNodes())
            {
                switch (child.name)
                {
                    case SettingNodeName:
                        ReadSetting(mod, child, where, problems);
                        break;
                    case BuildNodeName:
                        ReadBuild(mod, child, where, problems);
                        break;
                    case DefaultsNodeName:
                        ReadDefaults(mod, child, where, problems);
                        break;
                    case RequiresNodeName:
                        ReadRequirement(mod, child, where, problems);
                        break;
                    case ProfileNodeName:
                        ReadProfile(mod, child, where, problems);
                        break;
                    case AllProfilesNodeName:
                        ReadAllProfiles(mod, child, where, problems);
                        break;
                    default:
                        problems.Add(where + ": unknown node '" + child.name + "' -- ignored.");
                        break;
                }
            }

            // Once every setting is known, wherever the blocks stand: a key that is no
            // setting, and in a profile one that is no quality setting, is left out.
            List<ValueBlock> blocks = new List<ValueBlock>(mod.Defaults);
            blocks.AddRange(mod.Profiles);
            blocks.AddRange(mod.AllProfiles);
            foreach (ValueBlock block in blocks)
            {
                string blockWhere = block.Where(where);
                foreach (string key in new List<string>(block.Values.Keys))
                {
                    SettingRegistration setting = mod.Setting(key);
                    if (setting == null)
                        problems.Add(blockWhere + ": '" + key + "', which is no setting of it -- ignored.");
                    else if (block is ProfileRegistration && setting.Kind != SettingKind.Quality)
                        problems.Add(blockWhere + ": '" + key + "' is no quality setting, and a profile sets quality only -- ignored.");
                    else
                        continue;
                    block.Values.Remove(key);
                }

                // A block for a build no BUILD names never applies -- unless the
                // registration has no BUILD and its behaviour tells the build, as
                // TUFX's does.
                if (block.ForEveryBuild || mod.Builds.Exists(build => build.Name == block.Build)) continue;
                if (mod.Behaviour != null && mod.Builds.Count == 0) continue;
                problems.Add(blockWhere + ": no " + BuildNodeName + " is named '" + block.Build + "' -- the block never applies.");
            }
            return mod;
        }

        private static void ReadProfile(ModRegistration mod, ConfigNode node, string where, List<string> problems)
        {
            string name = Last(node, "name", where, problems);
            if (name == null)
            {
                problems.Add(where + ": a " + ProfileNodeName + " without a name -- left out.");
                return;
            }
            ProfileRegistration block = new ProfileRegistration { Name = name };
            ReadValues(node, block, where, problems);
            mod.Profiles.Add(block);
        }

        private static void ReadAllProfiles(ModRegistration mod, ConfigNode node, string where, List<string> problems)
        {
            AllProfilesRegistration block = new AllProfilesRegistration();
            ReadValues(node, block, where, problems);
            mod.AllProfiles.Add(block);
        }

        // A DEFAULTS, PROFILE or ALL_PROFILES block's build and values: every key but `build` and
        // the block's own is a setting's name.
        private static void ReadValues(ConfigNode node, ValueBlock block, string where, List<string> problems)
        {
            block.Build = Last(node, "build", where, problems);
            string blockWhere = block.Where(where);
            foreach (ConfigNode.Value value in node.values)
            {
                if (value.name == "build" || value.name == block.OwnKey) continue;
                if (block.Values.ContainsKey(value.name))
                    problems.Add(blockWhere + ": '" + value.name + "' twice -- the last one counts.");
                block.Values[value.name] = value.value.Trim();
            }
            foreach (ConfigNode child in node.GetNodes())
                problems.Add(blockWhere + ": node '" + child.name + "' inside " + block.Kind + " -- ignored.");
        }

        private static void ReadSetting(ModRegistration mod, ConfigNode node, string modWhere, List<string> problems)
        {
            string name = Last(node, "name", modWhere, problems);
            if (name == null)
            {
                problems.Add(modWhere + ": a " + SettingNodeName + " without a name -- left out.");
                return;
            }
            string where = modWhere + ", setting '" + name + "'";
            if (!IsId(name, true))
            {
                problems.Add(where + ": the name may hold only letters, digits, _ and . -- left out.");
                return;
            }

            SettingRegistration setting = new SettingRegistration { Name = name };
            setting.Behaviour = Last(node, "behaviour", where, problems);
            setting.Member = Last(node, "member", where, problems);
            setting.LeftOut = Text(Last(node, "leftOut", where, problems));
            if (setting.Member == null && setting.Behaviour == null && mod.Behaviour == null && setting.LeftOut == null)
            {
                problems.Add(where + ": no member -- left out.");
                return;
            }
            setting.Title = Last(node, "title", where, problems) ?? name;
            setting.Tooltip = Text(Last(node, "tooltip", where, problems)) ?? "";
            setting.Default = Last(node, "default", where, problems);

            string kind = Last(node, "kind", where, problems);
            if (kind != null)
            {
                SettingKind parsed;
                if (TryName(kind, out parsed)) setting.Kind = parsed;
                else problems.Add(where + ": kind '" + kind + "' is not Quality, Taste or Other -- ignored.");
            }
            string row = Last(node, "row", where, problems);
            if (row != null)
            {
                SettingCategory parsed;
                if (TryTab(row, out parsed)) setting.Row = parsed;
                else problems.Add(where + ": row '" + row + "' is not " + TabNames + " -- ignored.");
            }
            string order = Last(node, "order", where, problems);
            if (order != null)
            {
                int parsed;
                if (int.TryParse(order, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) setting.Order = parsed;
                else problems.Add(where + ": order '" + order + "' is not a whole number -- ignored.");
            }
            string effect = Last(node, "takesEffect", where, problems);
            if (effect != null)
            {
                ApplyWindow parsed;
                if (TryName(effect, out parsed) && Array.IndexOf(Windows, parsed) >= 0) setting.TakesEffect = parsed;
                else problems.Add(where + ": takesEffect '" + effect + "' is not Live, NextScene or Restart -- ignored.");
            }

            ReadSlider(setting, node, where, problems);
            ReadChoices(setting, node, where, problems);

            setting.Whole = Bool(node, "whole", where, problems);
            setting.Invert = Bool(node, "invert", where, problems) ?? false;
            setting.PerSave = Bool(node, "perSave", where, problems);
            // `optional = False` is no reason: the same as none.
            string optional = Last(node, "optional", where, problems);
            bool optionalSwitch;
            if (optional != null && bool.TryParse(optional, out optionalSwitch)) optional = optionalSwitch ? "True" : null;
            setting.Optional = optional;
            setting.Required = Bool(node, "required", where, problems) ?? false;
            if (setting.Required && setting.Optional != null)
            {
                problems.Add(where + ": optional and required go against each other -- required is ignored.");
                setting.Required = false;
            }
            setting.After = Last(node, "after", where, problems);
            setting.ShaderGlobal = Last(node, "shaderGlobal", where, problems);
            setting.LeftOutWith = ModName(node, "leftOutWith", where, problems);
            setting.RowUnless = ModName(node, "rowUnless", where, problems);
            Unknown(node, SettingKeys, where, problems);
            foreach (ConfigNode child in node.GetNodes())
                problems.Add(where + ": node '" + child.name + "' inside a setting -- ignored.");

            SettingRegistration earlier = mod.Setting(name);
            if (earlier != null)
            {
                problems.Add(modWhere + ": setting '" + name + "' twice -- the last one counts.");
                mod.Settings.Remove(earlier);
            }
            mod.Settings.Add(setting);
        }

        private static void ReadSlider(SettingRegistration setting, ConfigNode node, string where, List<string> problems)
        {
            string minText = Last(node, "min", where, problems);
            string maxText = Last(node, "max", where, problems);
            if (minText == null && maxText == null) return;
            if (minText == null || maxText == null)
            {
                problems.Add(where + ": min and max go together -- the slider is left out.");
                return;
            }
            float min, max;
            if (!float.TryParse(minText, NumberStyles.Float, CultureInfo.InvariantCulture, out min)
                || !float.TryParse(maxText, NumberStyles.Float, CultureInfo.InvariantCulture, out max)
                || float.IsNaN(min) || float.IsNaN(max) || float.IsInfinity(min) || float.IsInfinity(max))
            {
                problems.Add(where + ": min '" + minText + "' or max '" + maxText + "' is not a number -- the slider is left out.");
                return;
            }
            if (min > max)
            {
                problems.Add(where + ": min is above max -- the slider is left out.");
                return;
            }
            setting.Min = min;
            setting.Max = max;
        }

        private static void ReadChoices(SettingRegistration setting, ConfigNode node, string where, List<string> problems)
        {
            string choices = Last(node, "choices", where, problems);
            string labels = Last(node, "labels", where, problems);
            if (choices == null)
            {
                if (labels != null) problems.Add(where + ": labels without choices -- ignored.");
                return;
            }
            string[] list = List(choices);
            if (Array.IndexOf(list, "") >= 0)
            {
                problems.Add(where + ": an empty choice -- the list is left out.");
                return;
            }
            if (setting.Min != null)
            {
                problems.Add(where + ": both a slider and choices -- the choices count.");
                setting.Min = null;
                setting.Max = null;
            }
            setting.Choices = list;
            if (labels == null) return;
            string[] shown = List(labels);
            if (shown.Length != list.Length)
            {
                problems.Add(where + ": " + shown.Length + " labels for " + list.Length + " choices -- the labels are left out.");
                return;
            }
            setting.Labels = shown;
        }

        private static void ReadBuild(ModRegistration mod, ConfigNode node, string where, List<string> problems)
        {
            BuildRegistration build = new BuildRegistration
            {
                Name = Last(node, "name", where, problems),
                Has = Last(node, "has", where, problems),
            };
            Unknown(node, BuildKeys, where + ", build", problems);
            if (build.Name == null || build.Has == null)
            {
                problems.Add(where + ": a " + BuildNodeName + " without name or has -- left out.");
                return;
            }
            mod.Builds.Add(build);
        }

        private static void ReadDefaults(ModRegistration mod, ConfigNode node, string where, List<string> problems)
        {
            DefaultsRegistration block = new DefaultsRegistration { Version = Last(node, "version", where, problems) };
            ReadValues(node, block, where, problems);
            mod.Defaults.Add(block);
        }

        private static void ReadRequirement(ModRegistration mod, ConfigNode node, string modWhere, List<string> problems)
        {
            string setting = Last(node, "setting", modWhere, problems);
            if (setting == null)
            {
                problems.Add(modWhere + ": a " + RequiresNodeName + " without a setting -- left out.");
                return;
            }
            string where = modWhere + ", requirement on '" + setting + "'";
            Unknown(node, RequiresKeys, where, problems);

            List<string> tests = new List<string>();
            foreach (string key in TestKeys)
                if (node.HasValue(key)) tests.Add(key);
            if (tests.Count != 1)
            {
                problems.Add(where + ": needs one of equals, atMost, atLeast, highest or check -- left out.");
                return;
            }

            RequirementRegistration requirement = new RequirementRegistration { Setting = setting };
            string test = tests[0];
            string value = Last(node, test, where, problems);
            switch (test)
            {
                case "equals":
                    requirement.Test = RequirementTest.Equals;
                    break;
                case "atMost":
                    requirement.Test = RequirementTest.AtMost;
                    break;
                case "atLeast":
                    requirement.Test = RequirementTest.AtLeast;
                    break;
                case "highest":
                    requirement.Test = RequirementTest.Highest;
                    break;
                default:
                    requirement.Test = RequirementTest.Check;
                    break;
            }
            if (value == null
                || ((requirement.Test == RequirementTest.AtMost || requirement.Test == RequirementTest.AtLeast)
                    && !IsNumber(value))
                || (requirement.Test == RequirementTest.Highest && !string.Equals(value, "True", StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add(where + ": " + test + " '" + value + "' cannot be tested"
                             + (requirement.Test == RequirementTest.Highest ? " -- highest takes True" : "") + " -- left out.");
                return;
            }
            requirement.Value = value;
            requirement.Fix = Last(node, "fix", where, problems);
            if (requirement.Fix != null && requirement.Test != RequirementTest.Check)
            {
                problems.Add(where + ": fix goes with check only -- ignored.");
                requirement.Fix = null;
            }
            requirement.Lock = Bool(node, "lock", where, problems) ?? false;
            requirement.Reason = Text(Last(node, "reason", where, problems)) ?? "";
            if (requirement.Reason.Length == 0)
                problems.Add(where + ": no reason -- the player would not be told why.");
            requirement.WhenInstalled = ModName(node, "whenInstalled", where, problems);
            mod.Requirements.Add(requirement);
        }

        // The last of a key's values, trimmed; null where it has none or an empty
        // one. More than one is reported: a patch meant to change it added one.
        private static string Last(ConfigNode node, string key, string where, List<string> problems)
        {
            string[] values = node.GetValues(key);
            if (values == null || values.Length == 0) return null;
            if (values.Length > 1)
                problems.Add(where + ": '" + key + "' given " + values.Length + " times -- the last one counts.");
            string value = values[values.Length - 1].Trim();
            return value.Length > 0 ? value : null;
        }

        private static bool? Bool(ConfigNode node, string key, string where, List<string> problems)
        {
            string text = Last(node, key, where, problems);
            if (text == null) return null;
            bool value;
            if (bool.TryParse(text, out value)) return value;
            problems.Add(where + ": " + key + " '" + text + "' is not True or False -- ignored.");
            return null;
        }

        private static string ModName(ConfigNode node, string key, string where, List<string> problems)
        {
            string text = Last(node, key, where, problems);
            if (text == null || IsId(text, false)) return text;
            problems.Add(where + ": " + key + " '" + text + "' is no mod's name -- ignored.");
            return null;
        }

        // Each key a node may hold, reported once however often it stands.
        private static void Unknown(ConfigNode node, string[] known, string where, List<string> problems)
        {
            HashSet<string> said = new HashSet<string>();
            foreach (ConfigNode.Value value in node.values)
            {
                if (Array.IndexOf(known, value.name) >= 0 || !said.Add(value.name)) continue;
                problems.Add(where + ": unknown key '" + value.name + "' -- ignored.");
            }
        }

        // By name only, case aside: Enum.Parse also takes numbers and lists of
        // names.
        private static bool TryName<T>(string text, out T value)
        {
            foreach (string name in Enum.GetNames(typeof(T)))
            {
                if (!string.Equals(name, text, StringComparison.OrdinalIgnoreCase)) continue;
                value = (T)Enum.Parse(typeof(T), name);
                return true;
            }
            value = default(T);
            return false;
        }

        private static bool TryTab(string text, out SettingCategory tab)
        {
            return TryName(text, out tab) && Array.IndexOf(Tabs, tab) >= 0;
        }

        private static bool IsNumber(string text)
        {
            double number;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                   && !double.IsNaN(number) && !double.IsInfinity(number);
        }

        // A mod's name: lower-case letters, digits and _; a setting's also
        // upper-case letters and . -- KSP's own keys, EVE's light volume.
        private static bool IsId(string text, bool setting)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'
                          || (setting && ((c >= 'A' && c <= 'Z') || c == '.'));
                if (!ok) return false;
            }
            return true;
        }

        // A list separated by commas; `\,` is a comma inside an entry.
        private static string[] List(string text)
        {
            List<string> entries = new List<string>();
            StringBuilder entry = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == ',')
                {
                    entry.Append(',');
                    i++;
                }
                else if (text[i] == ',')
                {
                    entries.Add(entry.ToString().Trim());
                    entry.Length = 0;
                }
                else
                {
                    entry.Append(text[i]);
                }
            }
            entries.Add(entry.ToString().Trim());
            return entries.ToArray();
        }

        // A config file's line holds no line break: `\n` stands for one.
        private static string Text(string text)
        {
            return text == null ? null : text.Replace("\\n", "\n");
        }
    }
}
