using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace ReDefinition.Framework
{
    // Distant Object Enhancement /L (SKL 1.0 or GPL-2.0; Rubber Ducky, MOARdV,
    // TheDarkBadger, LisiasT)
    //
    // Its settings are the singleton DistantObject.Settings.Instance, whose groups
    // are plain objects with public fields, kept per save: Load reads the loaded
    // save's file, with none loaded the one in its PluginData, and Save writes that
    // same file. Its registration reaches the fields; here is what a path cannot say:
    //   * the three switches and the debug mode go through Commit, which enables or
    //     disables its drawing components -- at once, as its window does;
    //   * its window works on a copy of the settings (SettingsGui.buffer), taken as
    //     it opens and written back at its Apply: a value set here goes into that
    //     copy too, and only that value;
    //   * its names' font, among the dynamic fonts loaded;
    //   * two Harmony postfixes keep both windows alike: on Load, whatever file it
    //     has just read, the choices kept here go back over it and into that file;
    //     on its window's ApplySettings, a value changed there becomes the choice
    //     here.
    internal sealed class DistantObjectBehaviour : ModBehaviour
    {
        private const string HarmonyId = "ReDefinition.DistantObjectHooks";
        private const string Root = "DistantObject.Settings.Instance.";

        private static readonly string[] Commits = { "flaresEnabled", "renderVessels", "changeSkybox", "debugMode" };

        // The mod the hooks report to; one per run.
        private static RegisteredMod hooked;

        private Type settingsType;
        private PropertyInfo instance;
        private MethodInfo commit;
        private MethodInfo load;
        private MethodInfo windowApply;
        private FieldInfo windowBuffer;
        private readonly List<FieldInfo> windowHolders = new List<FieldInfo>();
        private readonly List<object> buffers = new List<object>();
        private int buffersFrame = -1;

        public override bool Attach(RegisteredMod mod)
        {
            settingsType = TypeLookup.Find("DistantObject.Settings");
            Type window = TypeLookup.Find("DistantObject.SettingsGui");
            if (settingsType == null) return false;

            const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance;
            instance = settingsType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            commit = settingsType.GetMethod("Commit", Public, null, Type.EmptyTypes, null);
            load = settingsType.GetMethod("Load", Public, null, Type.EmptyTypes, null);
            windowApply = window != null
                ? window.GetMethod("ApplySettings", BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null)
                : null;
            // Its window is no MonoBehaviour: SettingsGuiOnMainMenu and
            // SettingsGuiOnGameScenes each hold one (decompiled).
            windowBuffer = window != null ? window.GetField("buffer", BindingFlags.NonPublic | BindingFlags.Instance) : null;
            foreach (string holderName in new[] { "DistantObject.SettingsGuiOnMainMenu", "DistantObject.SettingsGuiOnGameScenes" })
            {
                Type holder = TypeLookup.Find(holderName);
                FieldInfo gui = holder != null && typeof(UnityEngine.Object).IsAssignableFrom(holder)
                    ? holder.GetField("settingsGui", BindingFlags.NonPublic | BindingFlags.Instance)
                    : null;
                if (gui != null && gui.FieldType == window) windowHolders.Add(gui);
            }
            if (instance == null || commit == null || load == null) return false;
            if (window != null && (windowBuffer == null || windowHolders.Count == 0))
                mod.Drop("SettingsGui.buffer", "this build of Distant Object keeps its window's values elsewhere, so its"
                                                     + " window, when open, shows a change made here only once opened again");
            if (windowApply == null)
                mod.Drop("SettingsGui.ApplySettings", "this build of Distant Object applies its window elsewhere, so a"
                                                            + " change made there does not reach ReDefinition's window");
            return true;
        }

        // Its assembly names no version; its Globals do ("2.2.1.7 /L") -- whose
        // other fields ask KSPe for paths, which only the game can answer.
        public override string Version(RegisteredMod mod)
        {
            try
            {
                Type globals = TypeLookup.Find("DistantObject.Globals");
                FieldInfo named = globals != null ? globals.GetField("DistantObjectVersion", HostStack.Any) : null;
                string version = named != null && named.IsStatic ? named.GetValue(null) as string : null;
                return string.IsNullOrEmpty(version) ? null : version;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public override void Finish(RegisteredMod mod, BundledSetting setting, SettingRegistration registration)
        {
            FieldInfo group, field;
            if (!Fields(registration.Member, out group, out field)) return;

            if (registration.Name == "fontName" && !FontChoice(mod, setting, group)) return;

            bool commits = Array.IndexOf(Commits, registration.Name) >= 0;
            Action<string> write = setting.Write;
            setting.Write = text =>
            {
                write(text);
                object parsed = SettingValues.Parse(text, field.FieldType);
                object current = instance.GetValue(null, null);
                if (commits && current != null) commit.Invoke(current, null);
                IntoWindows(group, field, parsed);
            };
        }

        public override void InstallHooks(RegisteredMod mod)
        {
            HarmonyHooks.Install(HarmonyId + ".Load", typeof(DistantObjectBehaviour), new[]
            {
                new HarmonyHook(load, null, nameof(LoadPostfix), null),
            });
            hooked = mod;
            if (windowApply == null) return;
            HarmonyHooks.Install(HarmonyId + ".Window", typeof(DistantObjectBehaviour), new[]
            {
                new HarmonyHook(windowApply, null, nameof(WindowApplyPostfix), null),
            });
        }

        // The group and field a member path under Settings.Instance names: a group's
        // field, or a field of the settings themselves.
        private bool Fields(string member, out FieldInfo group, out FieldInfo field)
        {
            group = null;
            field = null;
            if (member == null || !member.StartsWith(Root, StringComparison.Ordinal)) return false;
            string[] names = member.Substring(Root.Length).Split('.');
            if (names.Length == 2)
            {
                group = settingsType.GetField(names[0], HostStack.Any);
                field = group != null ? group.FieldType.GetField(names[1], HostStack.Any) : null;
            }
            else if (names.Length == 1)
            {
                field = settingsType.GetField(names[0], HostStack.Any);
            }
            return field != null;
        }

        // Its font by name, among the dynamic fonts loaded
        // (SettingsBuffer.FlyOverClass.fonts, decompiled), which is how its window
        // offers them.
        private bool FontChoice(RegisteredMod mod, BundledSetting setting, FieldInfo group)
        {
            PropertyInfo fonts = group != null ? group.FieldType.GetProperty("fonts", HostStack.Any) : null;
            if (fonts == null)
            {
                mod.Settings.Remove(setting);
                mod.Drop("fontName", "this build of Distant Object has no list of fonts to choose from");
                return false;
            }

            setting.Control = SettingControl.Choice;
            setting.ValueType = null;
            setting.ChoicesSource = () =>
            {
                object current = instance.GetValue(null, null);
                object values = current != null ? group.GetValue(current) : null;
                IEnumerable list = values != null ? fonts.GetValue(values, null) as IEnumerable : null;
                List<string> names = new List<string>();
                if (list != null)
                {
                    foreach (object font in list)
                    {
                        UnityEngine.Object named = font as UnityEngine.Object;
                        if (named != null && !names.Contains(named.name)) names.Add(named.name);
                    }
                }
                names.Sort(StringComparer.OrdinalIgnoreCase);
                return names.ToArray();
            };
            return true;
        }

        // Into an open window's copy of the settings -- of the same groups, since
        // Settings is a SettingsBuffer -- looked for once a frame: a save as it loads
        // writes most of the settings at once.
        private void IntoWindows(FieldInfo group, FieldInfo field, object value)
        {
            if (windowBuffer == null) return;
            if (buffersFrame != UnityEngine.Time.frameCount)
            {
                buffersFrame = UnityEngine.Time.frameCount;
                buffers.Clear();
                foreach (FieldInfo holder in windowHolders)
                    foreach (UnityEngine.Object behaviour in UnityEngine.Object.FindObjectsOfType(holder.DeclaringType))
                    {
                        object gui = holder.GetValue(behaviour);
                        object buffer = gui != null ? windowBuffer.GetValue(gui) : null;
                        if (buffer != null) buffers.Add(buffer);
                    }
            }
            foreach (object buffer in buffers)
            {
                // A switch of the settings themselves has no group.
                FieldInfo owner = group ?? field;
                if (!owner.DeclaringType.IsInstanceOfType(buffer)) continue;
                object values = group != null ? group.GetValue(buffer) : buffer;
                if (values != null) field.SetValue(values, value);
            }
        }

        // Whatever file Load has just read -- a save's, as it loads, or its window
        // reading for itself: the choices kept here go back over it, and into that
        // file.
        private static void LoadPostfix()
        {
            RegisteredMod mod = hooked;
            if (mod == null) return;
            HarmonyHooks.Safely("doe-load", "The settings Distant Object read from its file could not be brought in line"
                                            + " with ReDefinition's",
                () => BundledSettings.ReapplyStored("Distant Object read its settings", mod));
        }

        // Its window's Apply has set, committed and saved the window's values.
        private static void WindowApplyPostfix()
        {
            RegisteredMod mod = hooked;
            if (mod == null) return;
            HarmonyHooks.Safely("doe-window", "What was changed in Distant Object's window did not reach ReDefinition's"
                                              + " window", () =>
            {
                // bundled.cfg once, not once per setting.
                foreach (BundledSetting setting in mod.Settings)
                    BundledSettings.TakeFromMod(setting, BundledSettings.SafeRead(setting), false);
                BundledSettings.SaveIfDirty();
            });
        }
    }
}
