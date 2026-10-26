using System.Collections.Generic;
using System;
using ReDefinition.Core;
using ReDefinition.Settings.Behaviours;

namespace ReDefinition.Settings
{
    // A registration's KEY blocks: the binding as the text of a KeyCombination,
    // through the mod's members, a behaviour, or kept by ReDefinition itself.
    internal sealed partial class RegisteredMod
    {
        // A KEY block: the binding travels as the text of a KeyCombination, whatever
        // the mod keeps it in.
        // The mod may keep a KeyCode, the name of one as text, or a key and its
        // modifiers in members of their own, as Scatterer keeps them.
        private void AddBinding(SettingRegistration entry, ModBehaviour behaviour, string where, List<string> problems)
        {
            Func<string> read;
            Action<string> write;
            Type type;
            // How many modifiers the mod can keep: two where ReDefinition keeps the
            // binding or a behaviour reaches it (which lowers it where its mod keeps
            // fewer), else as many as its members hold (BindingThrough).
            int maxModifiers = 2;
            bool kept = false;
            if (behaviour != null && behaviour.Reach(this, entry, out read, out write, out type))
            {
                if (read == null) return;
            }
            else if (entry.Member == null)
            {
                // ReDefinition keeps it: the mod reads it through ReDefinition.Api.
                string key = Id + "." + entry.Name;
                string fallback = entry.Default ?? KeyCombination.NoneText;
                read = () => KeptBindings.Get(key, fallback);
                write = text => KeptBindings.Set(key, text);
                kept = true;
            }
            else if (!BindingThrough(entry, where, problems, out read, out write, out maxModifiers))
            {
                return;
            }

            ApplyWindow window = entry.TakesEffect;
            if (entry.After != null) write = CalledAfter(entry, where, problems, write, ref window);

            BundledSetting setting = Add(entry.Name, Localized(entry.Title), window, Localized(entry.Tooltip),
                read, write);
            setting.Control = SettingControl.Binding;
            setting.Kind = SettingKind.Other;
            setting.MaxModifiers = maxModifiers;
            // ReDefinition's own to keep, with the bundling on or off (KeptBindings).
            if (kept)
            {
                setting.Bundled = false;
                setting.KeptHere = true;
            }
            if (!string.IsNullOrEmpty(entry.KeyGroup)) setting.Group = entry.KeyGroup;
            if (registration.Saving == SettingsSaving.PerSave && entry.PerSave != false) setting.Context = LoadedSave;
            if (behaviour != null) behaviour.Finish(this, setting, entry);
        }

        // The method the registration's `after` names, called once the binding is
        // written: a mod that takes its key only when it is told to.
        private Action<string> CalledAfter(SettingRegistration entry, string where, List<string> problems,
                                           Action<string> write, ref ApplyWindow window)
        {
            string problem;
            MemberPath call = MemberPath.Resolve(entry.After, folder, out problem);
            if (call != null && !call.IsMethod)
            {
                problem = entry.After + " is no method";
                call = null;
            }
            if (call == null)
            {
                problems.Add(where + ": after: " + problem + " -- ignored.");
                // The row promises only what the call that is there can keep.
                if (window == ApplyWindow.Live) window = ApplyWindow.NextScene;
                return write;
            }
            Action<string> inner = write;
            MemberPath method = call;
            string runKey = "after-" + Id + "-" + entry.After;
            string after = entry.After;
            string modName = ModName;
            // As for a setting: a call that fails is logged rather than thrown into
            // the write already done, which the store would otherwise take for a
            // write that did not happen -- and drop the value from before
            // ReDefinition with it.
            Action run = () =>
            {
                try
                {
                    method.Invoke();
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn(runKey, modName + "'s " + after + " did not run after a change ("
                                                  + CompatibilityLog.Reason(e) + "); the binding takes effect when "
                                                  + modName + " next reads it.");
                }
            };
            return text =>
            {
                inner(text);
                BundledSettingsAddon.AtEndOfFrame(runKey, run);
            };
        }

        // The binding's members: the key, and the modifiers where the mod keeps them
        // apart. A member holds a KeyCode or its name as text; where the mod keeps
        // the whole combination in one member, its text is read and written as it
        // stands.
        private bool BindingThrough(SettingRegistration entry, string where, List<string> problems,
                                    out Func<string> read, out Action<string> write, out int maxModifiers)
        {
            read = null;
            write = null;
            maxModifiers = 0;
            MemberPath key = BindingMember(entry, entry.Member, "member", where, problems);
            if (key == null) return false;
            MemberPath first = entry.Modifier1 != null
                ? BindingMember(entry, entry.Modifier1, "modifier1", where, problems)
                : null;
            if (entry.Modifier1 != null && first == null) return false;
            MemberPath second = entry.Modifier2 != null
                ? BindingMember(entry, entry.Modifier2, "modifier2", where, problems)
                : null;
            if (entry.Modifier2 != null && second == null) return false;
            bool all = entry.ModifiersAll;
            // A KeyCode member alone holds no modifier; text alone holds the whole
            // combination; beside modifier members, as many as there are -- and with
            // `all`, two, since one modifier goes into both.
            bool keyAsCode = key.ValueType == typeof(UnityEngine.KeyCode);
            if (first == null) maxModifiers = keyAsCode ? 0 : 2;
            else maxModifiers = second != null ? 2 : 1;

            read = () =>
            {
                string keyText = BindingText(key);
                if (keyText == null) return null;
                if (first == null) return KeyCombination.Parse(keyText).ToString();
                return new KeyCombination(KeyCombination.Parse(keyText).Key,
                    KeyCombination.ParseModifier(BindingText(first)),
                    KeyCombination.ParseModifier(BindingText(second))).ToString();
            };
            write = text =>
            {
                KeyCombination combination = KeyCombination.Parse(text);
                if (first == null)
                {
                    // One member holds the whole binding: its text as it stands, or
                    // for a KeyCode the key alone.
                    SetBinding(key, keyAsCode ? combination.Key.ToString() : combination.ToString());
                    return;
                }
                SetBinding(key, combination.Key.ToString());
                UnityEngine.KeyCode firstModifier = combination.FirstModifier;
                UnityEngine.KeyCode secondModifier = combination.SecondModifier;
                // A mod that holds every modifier member at once reads one modifier
                // only where both say it, and the key alone only where both hold the
                // key itself: a member at None is a key never held (Deferred's
                // OnGUI asks Input.GetKey of each). Read back, a modifier member
                // holding the key is no modifier, and the row shows the key alone.
                if (all && firstModifier == UnityEngine.KeyCode.None && combination.IsBound)
                    firstModifier = combination.Key;
                if (all && secondModifier == UnityEngine.KeyCode.None) secondModifier = firstModifier;
                SetBinding(first, firstModifier.ToString());
                if (second != null) SetBinding(second, secondModifier.ToString());
            };
            return true;
        }

        // A member path a KEY block names for its key or a modifier, which must hold a KeyCode or its name; null,
        // the binding left out as a missing member, where this build cannot give it.
        private MemberPath BindingMember(SettingRegistration entry, string text, string what, string where,
                                         List<string> problems)
        {
            string problem;
            MemberPath path = MemberPath.Resolve(text, folder, out problem);
            if (path != null && path.IsMethod)
            {
                problem = text + " is a method, not a value";
                path = null;
            }
            if (path != null && path.ValueType != typeof(string) && path.ValueType != typeof(UnityEngine.KeyCode)
                && path.ValueType != typeof(object))
            {
                problem = text + " holds a " + path.ValueType.Name + ", and a binding needs a KeyCode or its name as text";
                path = null;
            }
            if (path == null)
            {
                if (entry.Optional == null) problems.Add(where + ": " + what + ": " + problem + " -- left out.");
                MemberMissing(entry, problem);
            }
            else
            {
                Named(path.EndField);
            }
            return path;
        }

        // The member's key as text; None for a modifier member the binding does not have.
        private static string BindingText(MemberPath path)
        {
            if (path == null) return KeyCombination.NoneText;
            object value = path.Get();
            return value != null ? value.ToString() : null;
        }

        // The key into the member, as a KeyCode or as its name, whichever the member holds.
        private static void SetBinding(MemberPath path, string text)
        {
            if (path.ValueType == typeof(UnityEngine.KeyCode))
            {
                path.Set(SettingValues.Parse(text, typeof(UnityEngine.KeyCode)));
                return;
            }
            path.Set(text);
        }
    }
}
