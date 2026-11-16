using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Ui;
using UnityEngine;

namespace ReDefinition.Atmospheres.Viewer
{
    // The viewer's modes, weather lab, cost readout, presets and the grouped key table (VIEW3). Like the rest of the window it draws CloudViewer and decides nothing; every control is
    // KSP's own dialog element so that ZTheme restyles it, each tooltip is a name or a few words, and a greyed control names its reason in its tooltip.
    internal sealed partial class CloudViewerWindow
    {
        private const string CostGroup = "Cost", ModesGroup = "Modes", LabGroup = "Weather Lab", PresetsGroup = "Presets";
        private string seedText = "";
        private DialogGUITextInput seedInput;

        // --- Cost ---

        private List<DialogGUIBase> CostRows()
        {
            CostReadout cost = viewer.Cost;
            var now = Tip(new DialogGUILabel(() => cost.NowLine(GpuCostSource.ProfilerOn), PageWidth), "Cost Of The Cloud Pass: " + CostReadout.SourceNote);
            var change = Tip(new DialogGUILabel(() => cost.ChangeLine(), PageWidth), "Cost Of The Last Change: " + CostReadout.SourceNote);
            return new List<DialogGUIBase>
            {
                now,
                change,
                new DialogGUILabel("Hover a line for where its numbers come from.", PageWidth)
            };
        }

        // --- Modes ---

        private List<DialogGUIBase> ModeRows()
        {
            var rows = new List<DialogGUIBase>
            {
                new DialogGUILabel(() => "Cloud pass: " + CloudPassStatus(), PageWidth)
            };
            WindowEnvironment env = WindowEnvironment.Detect();
            foreach (ViewerMode mode in ViewerModes.All)
            {
                SettingDef def = AtmospheresSettings.Definition(mode.Key);
                if (def != null) rows.Add(ModeRow(def, mode, env));
            }
            rows.Add(new DialogGUILabel("Live: acts at once or within a second. Greyed: the reason is in the tooltip.", PageWidth));
            return rows;
        }

        private static string CloudPassStatus()
        {
            OwnCloudModule m = OwnCloudModule.Current;
            if (m == null) return "not running";
            return OwnCloudModule.Drawing ? "drawing" : "not drawing (" + m.Stats.Why + ")";
        }

        private DialogGUIBase ModeRow(SettingDef def, ViewerMode mode, WindowEnvironment env)
        {
            CelestialBody body = FlightGlobals.currentMainBody;
            BodyClimate climate = body == null ? null : Bodies.Get(body);
            bool statesSource = climate != null && climate.Clouds.Source.HasValue;
            string why = ViewerModes.GreyReason(def, viewer.Store.Setting, env, statesSource);
            string reasonText = why;
            string name = def.Tooltip;
            var label = new DialogGUILabel(() => why == null ? name : "<color=#7a7a7a>" + name + "</color>", 200f);
            string tip = why == null ? name : name + ": " + reasonText;
            label.tooltipText = tip;
            DialogGUIBase control;
            switch (def.Kind)
            {
                case SettingKind.Bool:
                    control = new DialogGUIToggle(() => viewer.Store.Setting(def.Key) != 0.0, () => viewer.Store.Setting(def.Key) != 0.0 ? "On" : "Off",
                        on => { if ((viewer.Store.Setting(def.Key) != 0.0) != on) { viewer.SetSetting(def.Key, on ? 1.0 : 0.0); Build(); } }, 130f, RowHeight);
                    break;
                case SettingKind.Choice:
                    control = new DialogGUIButton(() => ValueFormat.ChoiceName(def.Choices[(int)viewer.Store.Setting(def.Key)]) + "   [>]",
                        () => { viewer.SetSetting(def.Key, ((int)viewer.Store.Setting(def.Key) + 1) % def.Choices.Length); Build(); }, 130f, RowHeight, false);
                    break;
                default:
                    control = new DialogGUISlider(() => (float)viewer.Store.Setting(def.Key), (float)def.Min, (float)def.Max, def.Kind == SettingKind.Int, 130f, -1f, f =>
                    {
                        if (Mathf.Approximately(f, (float)viewer.Store.Setting(def.Key))) return;
                        viewer.SetSetting(def.Key, f);
                    });
                    break;
            }
            control.tooltipText = tip;
            control.OptionInteractableCondition = () => why == null;
            var value = new DialogGUILabel(() => def.Kind == SettingKind.Bool || def.Kind == SettingKind.Choice ? " " : def.Format(viewer.Store.Setting(def.Key)), 56f);
            var marker = Tip(new DialogGUILabel(CloudKeyRegistry.MarkerShort(mode.Marker), 40f), CloudKeyRegistry.MarkerText(mode.Marker) + ": " + mode.Where);
            return Line(label, control, value, marker);
        }

        // --- Weather lab ---

        private List<DialogGUIBase> LabRows()
        {
            WeatherLab lab = viewer.Lab;
            var rows = new List<DialogGUIBase>
            {
                new DialogGUILabel("Every lever acts after Rebuild Weather; the old weather shows until the new maps are there.", PageWidth),
                Line(Tip(new DialogGUIButton(() => lab.Pending ? "Rebuild Weather (changes waiting)" : "Rebuild Weather", () => { viewer.RebuildWeather(); Build(); }, 260f, RowHeight, false),
                         "Rebuild Weather"),
                     Tip(new DialogGUIButton("Refresh Readout", () => { viewer.RefreshReadout(); Build(); }, 160f, RowHeight, false), "Count Cells And Sources")),
                new DialogGUILabel(() => "Rebuild: " + WeatherRebuild.Describe(), PageWidth)
            };
            // The seed: typed, staged like the levers.
            seedText = lab.Seed.ToString(CultureInfo.InvariantCulture);
            seedInput = new DialogGUITextInput(seedText, false, 11, text =>
            {
                int parsed;
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) lab.SetSeed(parsed);
                return text;
            }, 110f, RowHeight);
            rows.Add(Line(new DialogGUILabel("Seed", 80f), Tip(seedInput, "Weather Seed"),
                          Tip(new DialogGUIButton("Next Seed", () => { lab.SetSeed(lab.Seed + 1); Build(); }, 100f, RowHeight, false), "Try Another Seed"),
                          Tip(new DialogGUIButton("Reset", () => { lab.SetSeed(lab.SeedAtOpen); Build(); }, 60f, RowHeight, false), "Reset To Game Seed"),
                          new DialogGUILabel(() => "In force " + AtmospheresSettings.GenSeed + (lab.Seed != AtmospheresSettings.GenSeed ? ", staged " + lab.Seed : ""), 190f)));
            foreach (LabKnob knob in LabKnobs.All) rows.Add(LabRow(knob));
            foreach (string line in LabReadoutRows()) rows.Add(new DialogGUILabel(line, PageWidth));
            return rows;
        }

        private List<string> LabReadoutRows()
        {
            var lines = new List<string>();
            lines.Add("Readout (Refresh Readout):");
            foreach (string l in viewer.ReadoutLines) lines.Add(l);
            lines.Add(viewer.ReadoutNote);
            return lines;
        }

        private DialogGUIBase LabRow(LabKnob knob)
        {
            WeatherLab lab = viewer.Lab;
            var name = Tip(new DialogGUILabel(() => (lab.Changed(knob) ? "* " : "") + knob.Title, 190f), knob.Title + ": " + knob.Note);
            DialogGUIBase control;
            if (knob.Flag)
                control = new DialogGUIToggle(() => lab.Value(knob) != 0.0, () => lab.Value(knob) != 0.0 ? "On" : "Off", on => lab.Set(knob, on ? 1.0 : 0.0), 120f, RowHeight);
            else
                control = new DialogGUISlider(() => (float)(double.IsNaN(lab.Value(knob)) ? knob.Min : lab.Value(knob)), (float)knob.Min, (float)knob.Max, false, 120f, -1f, f =>
                {
                    double now = lab.Value(knob);
                    if (!double.IsNaN(now) && Math.Abs(f - now) < 1e-9) return;
                    lab.Set(knob, f);
                });
            control.tooltipText = knob.Title + (knob.RangeEstimated ? " (Slider Range Estimated)" : "");
            var value = new DialogGUILabel(() => double.IsNaN(lab.Value(knob)) ? "-" : WeatherLab.Format(knob, lab.Value(knob)), 60f);
            var def = Tip(new DialogGUILabel(() => double.IsNaN(lab.Baseline(knob)) ? "-" : WeatherLab.Format(knob, lab.Baseline(knob)), 50f), "Default");
            var marker = Tip(new DialogGUILabel("rebuild", 46f), "After Rebuild Weather");
            var reset = Tip(new DialogGUIButton("R", () => { lab.Reset(knob); Build(); }, 30f, RowHeight, false), "Reset To Default");
            return Line(name, control, value, def, marker, reset);
        }

        // --- Presets ---

        private List<DialogGUIBase> PresetRows()
        {
            var rows = new List<DialogGUIBase>
            {
                Line(Tip(new DialogGUIButton("Store As A", viewer.StoreA, 130f, RowHeight, false), "Store Options As A"),
                     Tip(new DialogGUIButton(() => "Flip A/B (" + viewer.Flip.Showing + ")", viewer.FlipAB, 130f, RowHeight, false), "Flip A And B"),
                     Tip(new DialogGUIToggle(() => viewer.KeepOptions, "Keep Options On Close", on => viewer.KeepOptions = on, 260f, RowHeight), "Keep Options On Close")),
                Line(new DialogGUILabel("Preset", 80f),
                     Tip(new DialogGUITextInput(presetName, false, 40, text => { presetName = text; return text; }, 200f, RowHeight), "Preset Name"),
                     Tip(new DialogGUIButton("Save", () => { if (viewer.SavePreset(presetName)) Build(); }, 90f, RowHeight, false), "Save Preset"))
            };
            // A/B between two named presets: each button steps to the next name, Start loads A, keeps it and shows B; the flip button or key swaps them.
            rows.Add(Line(new DialogGUILabel("A/B", 80f),
                          Tip(new DialogGUIButton(() => "A: " + viewer.CompareA, () => { viewer.CycleCompare(true); }, 160f, RowHeight, false), "Preset A"),
                          Tip(new DialogGUIButton(() => "B: " + viewer.CompareB, () => { viewer.CycleCompare(false); }, 160f, RowHeight, false), "Preset B"),
                          Tip(new DialogGUIButton("Start", () => { if (viewer.StartCompare()) Build(); }, 90f, RowHeight, false), "Compare A And B")));
            var presetButtons = new List<DialogGUIBase>();
            foreach (PresetEntry entry in viewer.PresetEntries())
            {
                PresetEntry e = entry;
                string tip = e.Name + (e.Note.Length > 0 ? ": " + e.Note : "");
                presetButtons.Add(Tip(new DialogGUIButton((e.BuiltIn ? "[" + e.Name + "]" : e.Name), () => viewer.LoadEntry(e), 134f, RowHeight, false), tip));
            }
            for (int i = 0; i < presetButtons.Count; i += 4) rows.Add(Line(presetButtons.GetRange(i, Math.Min(4, presetButtons.Count - i)).ToArray()));
            rows.Add(new DialogGUILabel("[Brackets]: built in, never saved; a preset never changes a default and closing the viewer puts everything back.", PageWidth));
            return rows;
        }

        // --- Keys ---

        private List<DialogGUIBase> KeySections()
        {
            var sections = new List<DialogGUIBase>
            {
                Line(new DialogGUILabel("Search Keys", 90f), Tip(new DialogGUITextInput(search, false, 40, text => { search = text ?? ""; return text; }, 300f, RowHeight), "Search Keys"),
                     new DialogGUILabel(() => viewer.Keys.Count + " keys", 100f)),
                new DialogGUILabel("Slider, value, type a value, default, when it acts; R resets, ? checks the reads. * marks a changed key.", PageWidth)
            };
            foreach (KeySection section in Enum.GetValues(typeof(KeySection)))
            {
                if (section == KeySection.Modes) continue;
                var rows = new List<DialogGUIBase>();
                foreach (CloudKey key in viewer.Keys) if (key.Section == section) rows.Add(KeyRow(key));
                if (rows.Count == 0) continue;
                sections.Add(Folded(CloudKeyRegistry.SectionTitle(section) + " Keys", rows));
            }
            return sections;
        }

        private DialogGUIBase KeyRow(CloudKey key)
        {
            string tip = key.Key + (key.Note.Length > 0 ? ": " + key.Note : "");
            var name = Tip(new DialogGUILabel(() => (Overridden(key) ? "* " : "") + key.Key, 170f), tip);
            DialogGUIBase control;
            if (key.Flag)
                control = new DialogGUIToggle(() => viewer.Store.Effective(key.Key, key.Default) != 0f, () => viewer.Store.Effective(key.Key, key.Default) != 0f ? "On" : "Off",
                    on => { if ((viewer.Store.Effective(key.Key, key.Default) != 0f) != on) viewer.SetKey(key.Key, on ? 1f : 0f); }, 90f, RowHeight);
            else
                control = new DialogGUISlider(() => viewer.Store.Effective(key.Key, key.Default), key.Min, key.Max, false, 90f, -1f, f =>
                {
                    if (Mathf.Approximately(f, viewer.Store.Effective(key.Key, key.Default))) return;
                    viewer.SetKey(key.Key, f);
                });
            control.tooltipText = key.Flag ? "Switch" : key.RangeEstimated ? "Drag To Change (Range Estimated)" : "Drag To Change";
            var value = new DialogGUILabel(() => Format(viewer.Store.Effective(key.Key, key.Default)), 52f);
            var input = Tip(new DialogGUITextInput("", false, 14, text =>
            {
                float v;
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !float.IsNaN(v) && !float.IsInfinity(v)
                    && v != viewer.Store.Effective(key.Key, key.Default)) viewer.SetKey(key.Key, v);
                return text;
            }, 56f, RowHeight), "Type A Value");
            var def = Tip(new DialogGUILabel(Format(key.Default), 44f), "Default");
            var marker = Tip(new DialogGUILabel(CloudKeyRegistry.MarkerShort(key.Marker), 36f), CloudKeyRegistry.MarkerText(key.Marker) + " (" + CloudKeyRegistry.LivenessText(key.Liveness) + ")");
            var reset = Tip(new DialogGUIButton("R", () => { viewer.ResetKey(key.Key); }, 26f, RowHeight, false), "Reset To Default");
            var check = Tip(new DialogGUIButton("?", () => viewer.WatchKey(key.Key), 26f, RowHeight, false), "Check Reads");
            var column = new FoldColumn(PageWidth, 0f, Line(name, control, value, input, def, marker, reset, check));
            column.OptionEnabledCondition = () => CloudKeyRegistry.Matches(key, search);
            return column;
        }
    }
}
