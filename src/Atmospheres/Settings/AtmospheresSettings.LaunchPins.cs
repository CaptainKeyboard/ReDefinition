using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Settings given on the game's command line for one start (-atmoSet key=value,key=value), applied in Load after the file was read, so
    // that a series can start the game with a setting already as it wants it (a start with the master switch off from the first frame) where
    // the series' file pins cannot reach: the settings.cfg of the Atmospheres is not in a fresh install, and a pin needs the key in the file.
    // A test start's switch: a player never gives it. The values are held in memory only; the file is written by Load with what the file or
    // the defaults gave and the runner puts the files back after the start.
    internal static partial class AtmospheresSettings
    {
        internal const string LaunchSwitch = "-atmoSet";

        // The pairs of the switch as 'key=value' words; empty where the switch is not given.
        internal static IList<KeyValuePair<string, string>> LaunchPairs(string[] args)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            string list = CommandLine.After(args, LaunchSwitch, true);
            if (string.IsNullOrEmpty(list)) return pairs;
            foreach (string part in list.Split(','))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0 || eq == part.Length - 1) continue;
                pairs.Add(new KeyValuePair<string, string>(part.Substring(0, eq).Trim(), part.Substring(eq + 1).Trim()));
            }
            return pairs;
        }

        // Applies the pairs; returns the pairs that took effect as 'key=value' words, for the log. A key that is not a setting, or a value
        // that does not fit its kind, is named in the log and left out.
        internal static IList<string> ApplyLaunchPairs(IList<KeyValuePair<string, string>> pairs)
        {
            var applied = new List<string>();
            foreach (KeyValuePair<string, string> pair in pairs)
            {
                SettingDef def = Definition(pair.Key);
                if (def == null)
                {
                    AtmospheresLog.Say("launch setting " + pair.Key + " is not a setting and is left out");
                    continue;
                }
                bool ok;
                string value = pair.Value;
                switch (def.Kind)
                {
                    case SettingKind.Text:
                        ok = SetText(def.Key, value);
                        break;
                    case SettingKind.Bool:
                        bool flag;
                        string low = value.ToLowerInvariant();
                        if (low == "1" || low == "true") flag = true;
                        else if (low == "0" || low == "false") flag = false;
                        else
                        {
                            AtmospheresLog.Say("launch setting " + pair.Key + "=" + value + " is not a switch value and is left out");
                            continue;
                        }
                        ok = SetNumber(def.Key, flag ? 1.0 : 0.0);
                        break;
                    case SettingKind.Choice:
                        int index = def.ChoiceIndex(value);
                        ok = index >= 0 && SetNumber(def.Key, index);
                        break;
                    default:
                        double number;
                        ok = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && SetNumber(def.Key, number);
                        break;
                }
                if (ok) applied.Add(def.Key + "=" + value);
                else AtmospheresLog.Say("launch setting " + pair.Key + "=" + value + " does not fit the setting and is left out");
            }
            return applied;
        }

        // Reads the game's command line and applies its pairs; the log names what was set.
        internal static void ApplyLaunchPins(string[] args)
        {
            IList<string> applied = ApplyLaunchPairs(LaunchPairs(args));
            if (applied.Count > 0) AtmospheresLog.Say("launch settings from the command line: " + string.Join(", ", new List<string>(applied).ToArray()));
        }
    }
}
