using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace ReDefinition.Framework
{
    // What a mod needs of a setting, enforced whatever profile is chosen
    // (docs/reference/requirements.md): the last layer over the defaults, a
    // profile and the window alike. Each rule is a mod's REQUIRES
    // (docs/modders/registering-a-mod.md), in force while that mod -- and the one its
    // `whenInstalled` names -- is loaded: the mod errors or warns about the setting
    // whether or not ReDefinition bundles it. Enforced only with the bundling on. A mod
    // that enforces a setting itself -- Firefly, Deferred, Kopernicus -- is left to it;
    // its registration or behaviour leaves out or limits the row. Several mods may
    // require the same setting: a value passes only what all of them allow.
    internal static class Requirements
    {
        internal sealed class Rule
        {
            // "<mod>: <setting>": for the log, and the message once per run.
            public string Id;
            // The mod whose registration requires it.
            public string Owner;
            public string Key;
            public bool Locks;
            public string Reason;
            public RequirementRegistration Registration;
        }

        private static readonly Dictionary<string, List<Rule>> None = new Dictionary<string, List<Rule>>();

        // Rules one message was shown for this run.
        private static readonly HashSet<string> told = new HashSet<string>();

        // Enforce runs when KSP's settings are applied, and its corrections to
        // KSP's settings fire that event again (KspBehaviour) -- at once, where no
        // add-on waits for the end of the frame.
        private static bool enforcing;

        // The rules in force, by key: worked out once the mods are built -- neither
        // the mods nor their builds change in a run, and the window asks in every
        // frame. Whether the bundling is on is asked where it matters: Enforce acts
        // only with it on, while a profile's values and a row's lock are worked out
        // as with it on -- a profile switches it on, and Apply may do so in the
        // same frame as it sets them.
        private static Dictionary<string, List<Rule>> active;

        // The rules of the registered mods among `mods`; `inForce`: only those whose
        // mod, and the one their `whenInstalled` names, are loaded. The check
        // outside the game asks the same of the mods it builds.
        internal static List<Rule> RulesOf(IEnumerable<IBundledMod> mods, bool inForce)
        {
            List<RegisteredMod> registered = new List<RegisteredMod>();
            HashSet<string> loaded = new HashSet<string>();
            foreach (IBundledMod mod in mods)
            {
                RegisteredMod one = mod as RegisteredMod;
                if (one == null) continue;
                registered.Add(one);
                if (one.Detected) loaded.Add(one.Id);
            }

            List<Rule> rules = new List<Rule>();
            foreach (RegisteredMod mod in registered)
            {
                if (inForce && !mod.Detected) continue;
                foreach (RequirementRegistration requirement in mod.Registration.Requirements)
                {
                    if (inForce && requirement.WhenInstalled != null && !loaded.Contains(requirement.WhenInstalled)) continue;
                    rules.Add(new Rule
                    {
                        Id = mod.Id + ": " + requirement.Setting,
                        Owner = mod.Id,
                        Key = requirement.Setting,
                        Locks = requirement.Lock,
                        Reason = requirement.Reason,
                        Registration = requirement,
                    });
                }
            }
            return rules;
        }

        // The value the setting takes: `value` where it passes, else a fix that
        // passes every rule, where one is found.
        internal static string Adjust(BundledSetting setting, string value)
        {
            List<Rule> rules = RulesFor(setting);
            if (rules == null || value == null || PassesAll(rules, setting, value)) return value;
            return FixFor(rules, setting, value) ?? value;
        }

        // Whether a value passes what the mods require of its setting -- asked of
        // the rules themselves: a rule with no fix here still forbids.
        internal static bool Allows(BundledSetting setting, string value)
        {
            List<Rule> rules = RulesFor(setting);
            return rules == null || value == null || PassesAll(rules, setting, value);
        }

        // The reasons of the rules a value refused outside Enforce fails, on screen,
        // once per run each.
        internal static void Tell(BundledSetting setting, string value)
        {
            List<Rule> rules = RulesFor(setting);
            if (rules == null) return;
            foreach (Rule rule in rules)
                if (!Passes(rule, setting, value)) Say(rule);
        }

        internal static bool Locked(BundledSetting setting)
        {
            List<Rule> rules = RulesFor(setting);
            if (rules == null) return false;
            foreach (Rule rule in rules)
                if (rule.Locks) return true;
            return false;
        }

        // For a row's tooltip: why it is locked or limited, or nothing.
        internal static string Note(BundledSetting setting)
        {
            List<Rule> rules = RulesFor(setting);
            if (rules == null) return "";
            StringBuilder note = new StringBuilder();
            foreach (Rule rule in rules) note.Append("\n").Append(rule.Locks ? "Locked: " : "Limited: ").Append(rule.Reason);
            return note.ToString();
        }

        // A choice row offers only the values the rules allow -- all of them where
        // they would allow none.
        internal static void Limit(BundledSetting setting, ref string[] choices, ref string[] labels)
        {
            List<Rule> rules = RulesFor(setting);
            if (rules == null || choices == null) return;

            List<string> keptChoices = new List<string>();
            List<string> keptLabels = new List<string>();
            for (int i = 0; i < choices.Length; i++)
            {
                if (!PassesAll(rules, setting, choices[i])) continue;
                keptChoices.Add(choices[i]);
                if (labels != null && i < labels.Length) keptLabels.Add(labels[i]);
            }
            if (keptChoices.Count == 0) return;
            choices = keptChoices.ToArray();
            if (labels != null) labels = keptLabels.ToArray();
        }

        // At every scene load and when KSP's own settings are applied, with the
        // bundling on: a value set outside this mod -- KSP's settings screen, a
        // mod's own window -- that a rule forbids is put right, saved as any
        // change is, and said once per run. A per-save value in the save loaded
        // now only, unless a choice for every save is kept (BundledSettings.
        // Correct).
        internal static void Enforce(string when)
        {
            if (enforcing || !BundledSettings.Enabled) return;
            enforcing = true;
            try
            {
                bool changed = false;
                foreach (KeyValuePair<string, List<Rule>> pair in new List<KeyValuePair<string, List<Rule>>>(Active()))
                {
                    try
                    {
                        BundledSetting setting = BundledSettings.Find(pair.Key);
                        if (setting == null || (setting.Applicable != null && !setting.Applicable())) continue;
                        string now = BundledSettings.SafeRead(setting);
                        if (now == null || PassesAll(pair.Value, setting, now)) continue;

                        List<Rule> failing = new List<Rule>();
                        foreach (Rule rule in pair.Value)
                            if (!Passes(rule, setting, now)) failing.Add(rule);
                        string reasons = Reasons(failing);
                        string fix = FixFor(pair.Value, setting, now);
                        if (fix == null)
                        {
                            CompatibilityLog.Warn("requirement-" + pair.Key, reasons + " No value here satisfies it.");
                            continue;
                        }
                        if (!BundledSettings.Correct(setting, fix)) continue;
                        changed = true;
                        Debug.Log(UpscalerProbe.Tag + " " + setting.Owner.ModName + ", " + setting.Title + ": " + now
                                  + " -> " + fix + " (" + when + "). " + reasons);
                        foreach (Rule rule in failing) Say(rule);
                    }
                    catch (Exception e)
                    {
                        CompatibilityLog.Warn("requirement-" + pair.Key, "A requirement could not be checked (" + pair.Key
                                                                         + ": " + CompatibilityLog.Reason(e) + ").");
                    }
                }
                if (changed) BundledSettings.SaveNow();
            }
            finally
            {
                enforcing = false;
            }
        }

        // Whether `value` passes the rule; a value that cannot be judged here -- a
        // name among choices not known yet, a check its mod cannot answer now --
        // passes.
        internal static bool Passes(Rule rule, BundledSetting setting, string value)
        {
            try
            {
                RequirementRegistration requirement = rule.Registration;
                double number;
                switch (requirement.Test)
                {
                    case RequirementTest.Equals:
                        return SettingValues.Same(value, requirement.Value);
                    case RequirementTest.AtMost:
                        return !Number(value, out number) || number <= Bound(requirement);
                    case RequirementTest.AtLeast:
                        return !Number(value, out number) || number >= Bound(requirement);
                    case RequirementTest.Highest:
                        // By position among the choices, as Parallax asks for KSP's
                        // terrain detail: KSP selects a preset by the first of its
                        // name, so a name the list holds twice never reaches the
                        // last.
                        string[] choices = setting != null ? setting.CurrentChoices() : null;
                        if (choices == null || choices.Length == 0) return true;
                        int index = IndexOf(choices, value);
                        return index < 0 || index == choices.Length - 1;
                    default:
                        return Check(rule, setting, value) != false;
                }
            }
            catch (Exception)
            {
                return true;
            }
        }

        // A value that passes the rule; null where none can be found here.
        internal static string FixOf(Rule rule, BundledSetting setting)
        {
            try
            {
                RequirementRegistration requirement = rule.Registration;
                switch (requirement.Test)
                {
                    case RequirementTest.Highest:
                        string[] choices = setting != null ? setting.CurrentChoices() : null;
                        if (choices == null || choices.Length == 0) return null;
                        string last = choices[choices.Length - 1];
                        return IndexOf(choices, last) == choices.Length - 1 ? last : null;
                    case RequirementTest.Check:
                        return requirement.Fix != null && Check(rule, setting, requirement.Fix) == true ? requirement.Fix : null;
                    default:
                        return requirement.Value;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Dictionary<string, List<Rule>> Active()
        {
            if (active != null) return active;
            IList<IBundledMod> mods = BundledSettings.Mods;
            if (!BundledSettings.Built) return None;

            Dictionary<string, List<Rule>> found = new Dictionary<string, List<Rule>>();
            foreach (Rule rule in RulesOf(mods, true))
            {
                List<Rule> list;
                if (!found.TryGetValue(rule.Key, out list)) found[rule.Key] = list = new List<Rule>();
                list.Add(rule);
            }
            active = found;
            foreach (List<Rule> list in found.Values)
                foreach (Rule rule in list)
                    if (rule.Registration.Test == RequirementTest.Check) SayUnknownCheck(rule);
            return active;
        }

        private static List<Rule> RulesFor(BundledSetting setting)
        {
            if (setting == null) return null;
            List<Rule> rules;
            return Active().TryGetValue(setting.Key, out rules) ? rules : null;
        }

        private static bool PassesAll(List<Rule> rules, BundledSetting setting, string value)
        {
            foreach (Rule rule in rules)
                if (!Passes(rule, setting, value)) return false;
            return true;
        }

        // The fix of a rule `value` fails that every rule allows; null where there
        // is none.
        private static string FixFor(List<Rule> rules, BundledSetting setting, string value)
        {
            foreach (Rule rule in rules)
            {
                if (Passes(rule, setting, value)) continue;
                string fix = FixOf(rule, setting);
                if (fix != null && PassesAll(rules, setting, fix)) return fix;
            }
            return null;
        }

        // A check a REQUIRES names, answered by the behaviour of the setting's mod;
        // null where it cannot be told.
        private static bool? Check(Rule rule, BundledSetting setting, string value)
        {
            RegisteredMod owner = setting != null ? setting.Owner as RegisteredMod : null;
            ModBehaviour behaviour = owner != null ? owner.MainBehaviour : null;
            return behaviour != null ? behaviour.Check(owner, rule.Registration.Value, value) : null;
        }

        // A check the setting's mod does not provide passes every value: said once.
        private static void SayUnknownCheck(Rule rule)
        {
            BundledSetting setting = BundledSettings.Find(rule.Key);
            RegisteredMod owner = setting != null ? setting.Owner as RegisteredMod : null;
            if (owner == null || (owner.MainBehaviour != null && owner.MainBehaviour.Provides(rule.Registration.Value))) return;
            CompatibilityLog.Warn("requirement-check-" + rule.Id, rule.Owner + " requires " + rule.Key + " to pass the check '"
                                                                  + rule.Registration.Value + "', which ReDefinition has no"
                                                                  + " answer to for " + owner.ModName + " -- not enforced.");
        }

        private static void Say(Rule rule)
        {
            if (told.Add(rule.Id)) ScreenMessages.PostScreenMessage("ReDefinition: " + rule.Reason, 8f);
        }

        private static string Reasons(List<Rule> rules)
        {
            List<string> reasons = new List<string>();
            foreach (Rule rule in rules) reasons.Add(rule.Reason);
            return string.Join(" ", reasons.ToArray());
        }

        private static int IndexOf(string[] choices, string value)
        {
            for (int i = 0; i < choices.Length; i++)
                if (SettingValues.Same(choices[i], value)) return i;
            return -1;
        }

        private static bool Number(string value, out double number)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }

        // The reader has made sure it is a number.
        private static double Bound(RequirementRegistration requirement)
        {
            return double.Parse(requirement.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
