using System.Collections.Generic;
using System.Globalization;
using System;
using ReDefinition.Core;
using ReDefinition.Settings;
using ReDefinition.Ui;
using UnityEngine;

namespace ReDefinition.Window
{
    // The Axes tab: KSP's joystick and gamepad axes (KspAxes), each with its
    // primary and its secondary binding.
    // A binding holds the axis bound, set by moving it
    // (AxisCapture), whether it is inverted, its sensitivity and its deadzone,
    // as KSP's own input screen shows them (SettingsInputAxis). The rows edit
    // copies; Apply writes them into KSP's bindings and KSP saves its settings.
    internal static partial class SettingsWindow
    {
        // The widths of an axis row's columns: the Primary/Secondary label, the bind button, a slider and its value.
        private const float AxisSideWidth = 70f;
        private const float AxisButtonWidth = 112f;
        private const float AxisSliderWidth = 64f;
        private const float AxisValueWidth = 34f;

        // The copies edited, by axis name, until Apply.
        private static readonly Dictionary<string, AxisBinding> axisPending = new Dictionary<string, AxisBinding>();

        // The tab's rows: a hint, then the axes in folds by their group, each with its two sides under column captions.
        private static DialogGUIBase[] AxisRows()
        {
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            rows.Add(new DialogGUILabel(RichText.Grey("Click an axis's button and move the stick, trigger or wheel to"
                                                      + " bind it; Escape cancels, x clears it. Sensitivity and deadzone as"
                                                      + " in KSP's own input screen."), true));
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
                    members.Add(Searchable(DialogRows.Row(RowHeight,
                        new DialogGUISpace(AxisSideWidth),
                        new DialogGUILabel(RichText.Grey("Axis"), AxisButtonWidth + 26f),
                        new DialogGUILabel(RichText.Grey("Invert"), 56f),
                        new DialogGUILabel(RichText.Grey("Sensitivity"), AxisSliderWidth + AxisValueWidth),
                        new DialogGUILabel(RichText.Grey("Deadzone"), AxisSliderWidth + AxisValueWidth)),
                        GroupAxisTitles(group)));
                }
                string text = axis.Title + " " + axis.Group + " axis";
                KspAxes.Axis marked = axis;
                members.Add(Searchable(new DialogGUILabel(() => Marked(marked.Title, AxisChanged(marked)),
                    NameWidth + ControlWidth), text));
                members.Add(Searchable(AxisSide(axis, false), text));
                members.Add(Searchable(AxisSide(axis, true), text));
            }
            if (members.Count > 0) AddFold(rows, SettingCategory.Axes, group, members, first);
            return rows.ToArray();
        }

        // Whether the axis's copy differs from KSP's binding.
        private static bool AxisChanged(KspAxes.Axis axis)
        {
            AxisBinding copy;
            return axisPending.TryGetValue(axis.Name, out copy) && !AxisSame(axis, copy);
        }

        // The comparisons of the axes' copies with KSP's bindings, by axis name.
        private static readonly Dictionary<string, KspAxes.Comparison> axisComparisons =
            new Dictionary<string, KspAxes.Comparison>();

        // Whether the copy holds what KSP's binding of the axis holds now (KspAxes.Same).
        private static bool AxisSame(KspAxes.Axis axis, AxisBinding copy)
        {
            KspAxes.Comparison comparison;
            if (!axisComparisons.TryGetValue(axis.Name, out comparison))
            {
                comparison = new KspAxes.Comparison();
                axisComparisons[axis.Name] = comparison;
            }
            return comparison.Of(axis.Get(), copy);
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

        // What the bind button shows while it listens.
        private const string AxisListeningText = "<color=#ffdd55>Move an axis...</color>";

        // One side of an axis: the bind button and its clear, invert, sensitivity and deadzone, each over the copy.
        private static DialogGUIBase AxisSide(KspAxes.Axis axis, bool secondary)
        {
            KspAxes.Axis shown = axis;
            string key = "axis." + shown.Name + (secondary ? ".secondary" : ".primary");

            // The label is made again only when the axis or its controller is another.
            string labelTag = null, labelName = null, labelText = null;
            Func<string> label = () =>
            {
                if (AxisCapture.Listening(key)) return AxisListeningText;
                AxisBinding_Single side = ShownSide(shown, secondary);
                if (side == null || side.idTag == "None") return "None";
                if (labelText == null || side.idTag != labelTag || side.name != labelName)
                {
                    labelTag = side.idTag;
                    labelName = side.name;
                    labelText = labelTag + "  " + labelName;
                }
                return labelText;
            };
            DialogGUIButton bind = null;
            bind = new DialogGUIButton(label, () => AxisCapture.Start(key, taken =>
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
            }, () => Showing(bind)), AxisButtonWidth, RowHeight + 4f, false);
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

            TwoDecimals sensitivityText = new TwoDecimals();
            TwoDecimals deadzoneText = new TwoDecimals();
            return DialogRows.Row(RowHeight + 4f,
                new DialogGUILabel(RichText.Grey(secondary ? "Secondary" : "Primary"), AxisSideWidth),
                bind, new DialogGUISpace(2f), clear, new DialogGUISpace(6f), invert, new DialogGUISpace(6f),
                sensitivity, new DialogGUILabel(() => sensitivityText.Of(SensitivityShown(ShownSide(shown, secondary))),
                    AxisValueWidth),
                deadzone, new DialogGUILabel(() =>
                {
                    AxisBinding_Single side = ShownSide(shown, secondary);
                    return deadzoneText.Of(side != null ? side.deadzone : 0f);
                }, AxisValueWidth));
        }

        // The sensitivity as the slider shows it, from 0 (KSP's least) to 1 (its most).
        private static float SensitivityShown(AxisBinding_Single side)
        {
            if (side == null) return 0f;
            return 1f - Mathf.InverseLerp(GameSettings.AxisSensitivityMin, GameSettings.AxisSensitivityMax, side.sensitivity);
        }

        // The copies that differ from KSP's bindings, written into them; true where
        // one was, for KSP's save at the end of Apply.
        private static bool ApplyAxes()
        {
            AxisCapture.Stop();
            if (axisPending.Count == 0) return false;
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
            return written;
        }

        // Reset to defaults: every axis to what KSP ships. Filled in only.
        private static void ResetAxes()
        {
            AxisCapture.Stop();
            axisPending.Clear();
            foreach (KspAxes.Axis axis in KspAxes.All())
            {
                // Only those that differ: every pending axis is compared with
                // KSP's own in every frame until Accept.
                AxisBinding shipped = KspAxes.Default(axis);
                if (shipped != null && !KspAxes.Same(axis.Get(), shipped)) axisPending[axis.Name] = shipped;
            }
        }
    }
}
