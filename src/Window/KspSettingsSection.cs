using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System;
using FidelityFX.FSR3;
using KSP.Localization;
using ReDefinition.Core;
using ReDefinition.Settings;
using UnityEngine;

namespace ReDefinition.Window
{
    // A ReDefinition section in KSP's own settings dialog -- the one the pause
    // menus of flight and the space centre open -- at the end of its graphics
    // part.
    //
    // Added the way KSPCommunityFixes adds its section to the gameplay part
    // (Internal/PatchSettings.cs there): a Harmony postfix on the section's
    // DrawMiniSettings that appends KSP's own dialog elements, laid out like
    // KSP's own graphics rows. Built from those rather than drawn with IMGUI,
    // so a UI theme such as ZTheme, which replaces KSP's UI textures by name,
    // covers it as it covers the rest of the dialog.
    //
    // KSP's own graphics section copies its values in GetSettings, edits the
    // copies, and commits them in ApplySettings, which the dialog calls on Apply
    // and Accept and never on Cancel. This section does the same, and keeps its
    // copies per section instance, so a dialog never sees another dialog's
    // half-edited values.
    //
    // The main menu's settings screen has no section: its controls are prefabs
    // that bind by name to fields of GameSettings; the one stock exception, the
    // TrackIR toggle, has a control class and prefab of its own.
    //
    // Harmony is a requirement (src/KspAssemblyInfo.cs): KSP does not load this
    // mod without it.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class KspSettingsSection : MonoBehaviour
    {
        private const string HarmonyId = "ReDefinition.SettingsSection";

        // KSP's graphics rows (VideoSettings.DrawMiniSettings): a 150 wide
        // name, the control, and for sliders a 30 gap and an 80 wide value.
        // Toggles at 150 with their state as text, as KSPCF's are.
        private const float NameWidth = 150f;
        private const float ControlWidth = 150f;
        private const float ValueGap = 30f;
        private const float ValueWidth = 80f;
        private const float RowHeight = 18f;

        // KSP's own strings for a toggle's state, as KSPCF uses them.
        private const string LocEnabled = "#autoLOC_6001072";
        private const string LocDisabled = "#autoLOC_6001071";

        // What the dialog was given and what the player has made of it so far.
        // The controls read and write After through this object, so After can
        // be replaced after an Apply without rebuilding the controls.
        // Also the settings window's (SettingsWindow), which edits the same way.
        internal sealed class Edit
        {
            public OwnSettings Before;
            public OwnSettings After;

            // Whether a graphics profile is chosen for these rows -- here the one
            // applied; in the settings window the one its rows were filled from.
            // Without one, ReDefinition's own rows cannot be changed.
            public Func<bool> ProfileChosen = () => BundledSettings.ProfileChosen;
        }

        private static readonly ConditionalWeakTable<VideoSettings, Edit> Edits =
            new ConditionalWeakTable<VideoSettings, Edit>();

        private void Awake()
        {
            try
            {
                Patch();
                Debug.Log(Log.Tag + " Section added to the graphics part of KSP's settings dialog.");
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Could not add the section to KSP's settings dialog: " + e);
            }

            Destroy(gameObject);
        }

        // All three or none, so a section that is drawn is also committed: every
        // target is looked up before the first patch, and a failure half way takes
        // back what was done.
        private static void Patch()
        {
            string[][] pairs =
            {
                new[] { "GetSettings", nameof(GetSettingsPostfix) },
                new[] { "DrawMiniSettings", nameof(DrawMiniSettingsPostfix) },
                new[] { "ApplySettings", nameof(ApplySettingsPostfix) },
            };

            MethodInfo[] originals = new MethodInfo[pairs.Length];
            MethodInfo[] postfixes = new MethodInfo[pairs.Length];
            for (int i = 0; i < pairs.Length; i++)
            {
                originals[i] = typeof(VideoSettings).GetMethod(pairs[i][0],
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                postfixes[i] = typeof(KspSettingsSection).GetMethod(pairs[i][1],
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (originals[i] == null)
                    throw new MissingMethodException("VideoSettings", pairs[i][0]);
                if (postfixes[i] == null)
                    throw new MissingMethodException("KspSettingsSection", pairs[i][1]);
            }

            HarmonyLib.Harmony harmony = new HarmonyLib.Harmony(HarmonyId);
            try
            {
                for (int i = 0; i < pairs.Length; i++)
                    harmony.Patch(originals[i], postfix: new HarmonyLib.HarmonyMethod(postfixes[i]));
            }
            catch
            {
                // ReDefinition's own id only: UnpatchAll without one removes every mod's
                // patches.
                harmony.UnpatchAll(HarmonyId);
                throw;
            }
        }

        // The postfixes run inside KSP's dialog code. An exception escaping
        // them would take KSP's own graphics settings down with ReDefinition's -- in
        // ApplySettings it would even skip KSP saving its settings -- so each
        // one catches everything.

        private static void GetSettingsPostfix(VideoSettings __instance)
        {
            try
            {
                Edits.Remove(__instance);
                ReDefinitionAddon addon = ReDefinitionAddon.Instance;
                if (addon == null) return;

                OwnSettings current = addon.Current();
                Edits.Add(__instance, new Edit { Before = current, After = current.Clone() });
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Settings section not loaded: " + e);
            }
        }

        private static void DrawMiniSettingsPostfix(VideoSettings __instance, ref DialogGUIBase[] __result)
        {
            try
            {
                Edit edit;
                if (__result == null || !Edits.TryGetValue(__instance, out edit)) return;

                DialogGUIBase[] rows = Rows(edit);
                DialogGUIBase[] combined = new DialogGUIBase[__result.Length + rows.Length];
                Array.Copy(__result, combined, __result.Length);
                Array.Copy(rows, 0, combined, __result.Length, rows.Length);
                __result = combined;
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Settings section not drawn: " + e);
            }
        }

        private static void ApplySettingsPostfix(VideoSettings __instance)
        {
            try
            {
                Edit edit;
                ReDefinitionAddon addon = ReDefinitionAddon.Instance;
                if (addon == null || !Edits.TryGetValue(__instance, out edit)) return;

                try
                {
                    addon.Apply(edit.Before, edit.After);
                }
                finally
                {
                    // Apply leaves the dialog open, and what was just committed is
                    // the new starting point -- as it is after the commit, which is
                    // not always as asked: the other mods set up only in part, a
                    // setter that threw half way. So it is read back.
                    OwnSettings now = addon.Current();
                    edit.Before = now;
                    edit.After = now.Clone();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Settings from KSP's dialog applied only in part: " + e);
            }
        }

        // The widths of a row: KSP's own graphics rows, or those of ReDefinition's
        // settings window, whose rows end in a grey column naming where each
        // comes from -- so that these line up with the other mods' rows there.
        internal struct Layout
        {
            public float Name;
            public float Control;
            public float ValueGap;
            public float Value;
            // Null: no source column.
            public string Source;
            public float SourceWidth;
        }

        private static readonly Layout DialogLayout = new Layout
        {
            Name = NameWidth,
            Control = ControlWidth,
            ValueGap = ValueGap,
            Value = ValueWidth,
        };

        // For KSP's own dialog, under a heading of its own and over a button to
        // the settings window, where the bundled mods' rows, the profiles and the
        // key bindings are. This dialog holds ReDefinition's own rows only.
        internal static DialogGUIBase[] Rows(Edit edit, bool header = true)
        {
            DialogGUIBase[] rows = Rows(edit, DialogLayout);
            if (!header) return rows;

            DialogGUIButton all = new DialogGUIButton("All settings ...", OpenWindow, ControlWidth, RowHeight + 6f, false);
            all.tooltipText = "The window with the graphics mods' settings, the profiles and the key bindings.";

            DialogGUIBase[] withHeader = new DialogGUIBase[rows.Length + 2];
            withHeader[0] = new DialogGUIBox("ReDefinition", -1f, RowHeight, null);
            Array.Copy(rows, 0, withHeader, 1, rows.Length);
            withHeader[rows.Length + 1] = new DialogGUIHorizontalLayout(0f, RowHeight + 6f, 0f, new RectOffset(),
                TextAnchor.MiddleLeft, new DialogGUISpace(NameWidth), all);
            return withHeader;
        }

        // The dialog stays where it is: closing the window leaves the player in
        // KSP's settings, as they were.
        private static void OpenWindow()
        {
            try
            {
                if (!SettingsWindow.Visible) SettingsWindow.Toggle();
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The settings window could not be opened from KSP's dialog: " + e);
            }
        }

        // Shared with the settings window, which lays them out its own way: every
        // setting of ReDefinition's modules (OurModules) in the order their rows stand, a
        // switch without a value column, a slider or a list with one.
        internal static DialogGUIBase[] Rows(Edit edit, Layout layout)
        {
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            foreach (ModuleSetting setting in OurModules.Rows())
            {
                ModuleSetting shown = setting;
                Func<string> value = shown.Control == SettingControl.Toggle ? null : (Func<string>)(() => Shown(edit, shown));
                rows.Add(Row(layout, shown.Title, Control(edit, shown, layout.Control), value));
            }
            return rows.ToArray();
        }

        // Read from and written into edit.After, which an Apply replaces without
        // the controls being built again.
        private static DialogGUIBase Control(Edit edit, ModuleSetting setting, float width)
        {
            DialogGUIBase control;
            if (setting.Control == SettingControl.Toggle)
            {
                // A toggle's Label, where it has one, says more than on or off.
                control = new DialogGUIToggle(() => IsOn(edit, setting),
                    () => setting.Label != null ? setting.Label(IsOn(edit, setting) ? "True" : "False")
                                                : StateText(IsOn(edit, setting)),
                    b => setting.Write(edit.After, b ? "True" : "False"), width);
            }
            else if (setting.Control == SettingControl.Slider)
            {
                control = new DialogGUISlider(() => Number(edit, setting), setting.Min, setting.Max, setting.WholeNumbers,
                    width, -1f, f => setting.Write(edit.After, Snapped(f, setting)));
            }
            else
            {
                // A step through the choices, left to right.
                int last = setting.Choices.Length - 1;
                control = new DialogGUISlider(() => ChoiceIndex(edit, setting), 0f, last, true, width, -1f,
                    f => setting.Write(edit.After, setting.Choices[Mathf.Clamp(Mathf.RoundToInt(f), 0, last)]));
            }
            control.tooltipText = setting.Tooltip + "\nLocked while no graphics profile is chosen in ReDefinition's window.";
            control.OptionInteractableCondition = () => edit.ProfileChosen()
                                                        && (setting.Interactable == null || setting.Interactable(edit.After));
            return control;
        }

        private static bool IsOn(Edit edit, ModuleSetting setting)
        {
            bool on;
            return bool.TryParse(setting.Read(edit.After), out on) && on;
        }

        private static float Number(Edit edit, ModuleSetting setting)
        {
            float value;
            return float.TryParse(setting.Read(edit.After), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value
                : setting.Min;
        }

        // Cut into the setting's steps as the slider moves: the sharpness in
        // twentieths.
        private static string Snapped(float value, ModuleSetting setting)
        {
            float snapped = setting.StepsPerUnit > 0f ? Mathf.Round(value * setting.StepsPerUnit) / setting.StepsPerUnit : value;
            return SettingValues.Text(snapped);
        }

        private static float ChoiceIndex(Edit edit, ModuleSetting setting)
        {
            string now = setting.Read(edit.After);
            for (int i = 0; i < setting.Choices.Length; i++)
                if (setting.Choices[i] == now) return i;
            return 0f;
        }

        private static string Shown(Edit edit, ModuleSetting setting)
        {
            string now = setting.Read(edit.After);
            return setting.Label != null ? setting.Label(now) : now;
        }

        // Looked up once rather than each time the dialog refreshes a label.
        private static string enabledText;
        private static string disabledText;

        internal static string StateText(bool on)
        {
            if (enabledText == null)
            {
                enabledText = Localizer.Format(LocEnabled);
                disabledText = Localizer.Format(LocDisabled);
            }
            return on ? enabledText : disabledText;
        }

        private static DialogGUIHorizontalLayout Row(Layout layout, string name, DialogGUIBase control,
                                                     Func<string> value = null)
        {
            DialogGUILabel label = new DialogGUILabel(name, layout.Name);

            // With a source column every row keeps its value column, empty or
            // not, so that the source lines up under the other rows' sources.
            if (layout.Source != null)
            {
                return new DialogGUIHorizontalLayout(0f, RowHeight, 0f, new RectOffset(),
                    TextAnchor.MiddleLeft, label, control,
                    new DialogGUISpace(layout.ValueGap), new DialogGUILabel(value ?? NoValue, layout.Value),
                    new DialogGUILabel("<color=#9a9a9a>" + layout.Source + "</color>", layout.SourceWidth));
            }
            if (value == null)
            {
                return new DialogGUIHorizontalLayout(0f, RowHeight, 0f, new RectOffset(),
                    TextAnchor.MiddleLeft, label, control);
            }

            return new DialogGUIHorizontalLayout(0f, RowHeight, 0f, new RectOffset(),
                TextAnchor.MiddleLeft, label, control,
                new DialogGUISpace(layout.ValueGap), new DialogGUILabel(value, layout.Value));
        }

        private static string NoValue()
        {
            return "";
        }

        // Shared with the diagnostics window, so both views name a mode alike.
        internal static string ModeName(Fsr3Upscaler.QualityMode mode)
        {
            if (mode == Fsr3Upscaler.QualityMode.NativeAA) return "AA only";
            return Fsr3Upscaler.GetUpscaleRatioFromQualityMode(mode)
                       .ToString("0.0", CultureInfo.InvariantCulture) + "x";
        }
    }
}
