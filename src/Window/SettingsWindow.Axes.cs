using System.Collections.Generic;
using System.Globalization;
using System;
using ReDefinition.Core;
using ReDefinition.Settings;
using UnityEngine;

namespace ReDefinition.Window
{
    // The Axes tab: KSP's joystick and gamepad axes (KspAxes), each with its
    // primary and its secondary binding -- the axis bound, set by moving it
    // (AxisCapture), whether it is inverted, its sensitivity and its deadzone,
    // as KSP's own input screen shows them (SettingsInputAxis). The rows edit
    // copies; Apply writes them into KSP's bindings and KSP saves its settings.
    internal static partial class SettingsWindow
    {
        private const float AxisSideWidth = 70f;
        private const float AxisButtonWidth = 112f;
        private const float AxisSliderWidth = 64f;
        private const float AxisValueWidth = 34f;

        // The copies edited, by axis name, until Apply.
        private static readonly Dictionary<string, AxisBinding> axisPending = new Dictionary<string, AxisBinding>();

        private static DialogGUIBase[] AxisRows()
        {
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            rows.Add(new DialogGUILabel("<color=#9a9a9a>Click an axis's button and move the stick, trigger or wheel to"
                                        + " bind it; Escape cancels, x clears it. Sensitivity and deadzone as in KSP's"
                                        + " own input screen.</color>", true));
            string group = null;
            List<DialogGUIBase> members = new List<DialogGUIBase>();
            bool first = true;
            foreach (KspAxes.Axis axis in KspAxes.All())
            {
                if (axis.Group != group)
                {
                    if (members.Count > 0)
                    {
                        AddFold(rows, SettingCategory.Axes, group, members, first);
                        first = false;
                        members = new List<DialogGUIBase>();
                    }
                    group = axis.Group;
                    members.Add(Searchable(new DialogGUIHorizontalLayout(0f, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                        new DialogGUISpace(AxisSideWidth),
                        new DialogGUILabel("<color=#9a9a9a>Axis</color>", AxisButtonWidth + 26f),
                        new DialogGUILabel("<color=#9a9a9a>Invert</color>", 56f),
                        new DialogGUILabel("<color=#9a9a9a>Sensitivity</color>", AxisSliderWidth + AxisValueWidth),
                        new DialogGUILabel("<color=#9a9a9a>Deadzone</color>", AxisSliderWidth + AxisValueWidth)),
                        GroupAxisTitles(group)));
                }
                string text = axis.Title + " " + axis.Group + " axis";
                members.Add(Searchable(new DialogGUILabel(axis.Title, NameWidth + ControlWidth), text));
                members.Add(Searchable(AxisSide(axis, false), text));
                members.Add(Searchable(AxisSide(axis, true), text));
            }
            if (members.Count > 0) AddFold(rows, SettingCategory.Axes, group, members, first);
            return rows.ToArray();
        }

        // The column captions stand wherever an axis of their group is found.
        private static string GroupAxisTitles(string group)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder(group);
            foreach (KspAxes.Axis axis in KspAxes.All())
                if (axis.Group == group) text.Append(' ').Append(axis.Title);
            return text.Append(" axis").ToString();
        }

        // What the row shows: the copy edited, or KSP's binding as it stands.
        private static AxisBinding_Single ShownSide(KspAxes.Axis axis, bool secondary)
        {
            AxisBinding binding;
            if (!axisPending.TryGetValue(axis.Name, out binding)) binding = axis.Get();
            if (binding == null) return null;
            return secondary ? binding.secondary : binding.primary;
        }

        // The copy to change, made on the first change.
        private static AxisBinding_Single EditedSide(KspAxes.Axis axis, bool secondary)
        {
            AxisBinding binding;
            if (!axisPending.TryGetValue(axis.Name, out binding))
            {
                binding = KspAxes.Copy(axis.Get());
                if (binding == null) return null;
                axisPending[axis.Name] = binding;
            }
            return secondary ? binding.secondary : binding.primary;
        }

        private static DialogGUIBase AxisSide(KspAxes.Axis axis, bool secondary)
        {
            KspAxes.Axis shown = axis;
            string key = "axis." + shown.Name + (secondary ? ".secondary" : ".primary");

            Func<string> label = () =>
            {
                if (AxisCapture.Listening(key)) return ListeningText;
                AxisBinding_Single side = ShownSide(shown, secondary);
                return side == null || side.idTag == "None" ? "None" : side.idTag + "  " + side.name;
            };
            DialogGUIButton bind = new DialogGUIButton(label, () => AxisCapture.Start(key, taken =>
            {
                AxisBinding_Single side = EditedSide(shown, secondary);
                if (side == null) return;
                // As KSP's own screen sets it (SettingsInputBinding.SetAxis).
                side.idTag = taken.IdTag;
                side.name = taken.Device;
                side.title = KSP.Localization.Localizer.Format("#autoLOC_6001495", taken.Device,
                    taken.AxisIndex.ToString(CultureInfo.InvariantCulture));
                side.deviceIdx = taken.DeviceIndex;
                side.axisIdx = taken.AxisIndex;
            }), AxisButtonWidth, RowHeight + 4f, false);
            bind.tooltipText = (secondary ? "The second axis for " : "The axis for ") + shown.Title
                               + ".\nClick, then move the axis on the controller. Escape cancels; x clears it.";

            DialogGUIButton clear = new DialogGUIButton("x", () =>
            {
                if (AxisCapture.Listening(key)) AxisCapture.Stop();
                AxisBinding_Single side = EditedSide(shown, secondary);
                if (side == null) return;
                side.idTag = "None";
                side.name = "None";
                side.title = "None";
                side.deviceIdx = -1;
                side.axisIdx = -1;
            }, 20f, RowHeight + 4f, false);

            DialogGUIToggle invert = new DialogGUIToggle(() =>
                {
                    AxisBinding_Single side = ShownSide(shown, secondary);
                    return side != null && side.inverted;
                }, () => "", b =>
                {
                    AxisBinding_Single now = ShownSide(shown, secondary);
                    if (now == null || now.inverted == b) return;
                    AxisBinding_Single side = EditedSide(shown, secondary);
                    if (side != null) side.inverted = b;
                }, 50f);

            // KSP shows the sensitivity the other way round: 1 is its least,
            // 0 its most (SettingsInputAxis).
            DialogGUISlider sensitivity = new DialogGUISlider(() => SensitivityShown(ShownSide(shown, secondary)), 0f, 1f,
                false, AxisSliderWidth, -1f, f =>
                {
                    AxisBinding_Single now = ShownSide(shown, secondary);
                    if (now == null || Mathf.Abs(SensitivityShown(now) - f) < 0.005f) return;
                    AxisBinding_Single side = EditedSide(shown, secondary);
                    if (side != null)
                        side.sensitivity = Mathf.Lerp(GameSettings.AxisSensitivityMin, GameSettings.AxisSensitivityMax, 1f - f);
                });
            DialogGUISlider deadzone = new DialogGUISlider(() =>
                {
                    AxisBinding_Single side = ShownSide(shown, secondary);
                    return side != null ? side.deadzone : 0f;
                }, 0f, 1f, false, AxisSliderWidth, -1f, f =>
                {
                    AxisBinding_Single now = ShownSide(shown, secondary);
                    if (now == null || Mathf.Abs(now.deadzone - f) < 0.005f) return;
                    AxisBinding_Single side = EditedSide(shown, secondary);
                    if (side != null) side.deadzone = Mathf.Round(f * 100f) / 100f;
                });

            return new DialogGUIHorizontalLayout(0f, RowHeight + 4f, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel("<color=#9a9a9a>" + (secondary ? "Secondary" : "Primary") + "</color>", AxisSideWidth),
                bind, new DialogGUISpace(2f), clear, new DialogGUISpace(6f), invert, new DialogGUISpace(6f),
                sensitivity, new DialogGUILabel(() => Number(SensitivityShown(ShownSide(shown, secondary))), AxisValueWidth),
                deadzone, new DialogGUILabel(() =>
                {
                    AxisBinding_Single side = ShownSide(shown, secondary);
                    return Number(side != null ? side.deadzone : 0f);
                }, AxisValueWidth));
        }

        private static float SensitivityShown(AxisBinding_Single side)
        {
            if (side == null) return 0f;
            return 1f - Mathf.InverseLerp(GameSettings.AxisSensitivityMin, GameSettings.AxisSensitivityMax, side.sensitivity);
        }

        private static string Number(float value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        // The copies that differ from KSP's bindings, written into them, and KSP
        // saves its settings once.
        private static void ApplyAxes()
        {
            AxisCapture.Stop();
            if (axisPending.Count == 0) return;
            bool written = false;
            foreach (KspAxes.Axis axis in KspAxes.All())
            {
                AxisBinding copy;
                if (!axisPending.TryGetValue(axis.Name, out copy)) continue;
                try
                {
                    AxisBinding live = axis.Get();
                    if (live == null || KspAxes.Same(live, copy)) continue;
                    KspAxes.Write(live, copy);
                    written = true;
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn("ksp-axis-" + axis.Name, axis.Title + ": the axis could not be set ("
                                                                   + CompatibilityLog.Reason(e) + ").");
                }
            }
            axisPending.Clear();
            if (!written) return;
            try
            {
                GameSettings.SaveSettings();
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " KSP's axes could not be saved: " + e);
            }
        }

        // Reset to defaults: every axis to what KSP ships. Filled in only.
        private static void ResetAxes()
        {
            AxisCapture.Stop();
            axisPending.Clear();
            foreach (KspAxes.Axis axis in KspAxes.All())
            {
                AxisBinding shipped = KspAxes.Default(axis);
                if (shipped != null) axisPending[axis.Name] = shipped;
            }
        }
    }
}
