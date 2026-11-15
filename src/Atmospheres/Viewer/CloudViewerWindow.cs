using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using ReDefinition.Ui;
using UnityEngine;

namespace ReDefinition.Atmospheres.Viewer
{
    // The cloud viewer's window: KSP's own dialog in the build of the atmospheres' settings window (fold groups, single-line labels of fixed width, rows within
    // the page, the list's background cleared, tooltips in the game's style), so ZTheme restyles it where it is installed. It draws CloudViewer and decides nothing.
    // Opening it starts the viewer's session; closing it (the button, Escape, a scene change) ends it and puts everything back.
    internal sealed class CloudViewerWindow
    {
        internal static readonly CloudViewerWindow Instance = new CloudViewerWindow();

        private const float Width = 640f, PageWidth = 560f, RowHeight = 24f;
        private const string StatusGroup = "Status", SceneGroup = "Scene", OptionsGroup = "Options", CompareGroup = "Compare", KeysGroup = "All Keys";
        private static readonly HashSet<string> Open = new HashSet<string> { StatusGroup, SceneGroup };

        private readonly CloudViewer viewer = CloudViewer.Instance;
        private PopupDialog shown;
        private string search = "";
        private string presetName = "";
        private float scroll = 1f;

        private CloudViewerWindow()
        {
            viewer.Changed = () => { if (shown != null) Build(); };
        }

        internal bool IsOpen
        {
            get { return shown != null; }
        }

        // The window is open and not hidden for a picture.
        internal bool Visible
        {
            get { return shown != null && shown.gameObject.activeSelf; }
        }

        internal void Toggle()
        {
            if (shown != null) Close();
            else Show();
        }

        internal void Show()
        {
            if (shown != null) return;
            if (!viewer.Open()) return;
            Build();
        }

        internal void Close()
        {
            Close(false);
        }

        // sceneEnding: the scene is about to be saved and left (the vessel is set back at once, not held back over frames).
        internal void Close(bool sceneEnding)
        {
            PopupDialog old = shown;
            shown = null;
            if (old != null) old.Dismiss();
            viewer.Close(sceneEnding);
        }

        // Every frame (CloudViewerAddon): the window is hidden while a picture is taken.
        internal void Tick()
        {
            if (shown == null) return;
            bool hide = viewer.Capturing;
            if (shown.gameObject.activeSelf == hide) shown.gameObject.SetActive(!hide);
        }

        private void Build()
        {
            if (shown != null)
            {
                SaveScroll();
                PopupDialog old = shown;
                shown = null;
                old.Dismiss();
            }
            var rows = new List<DialogGUIBase>
            {
                Folded(StatusGroup, StatusRows()),
                Folded(SceneGroup, SceneRows()),
                Folded(OptionsGroup, OptionRows()),
                Folded(CompareGroup, CompareRows()),
                Folded(KeysGroup, KeyRows())
            };
            var list = new TabScrollList(new Vector2(Width - 20f, 520f),
                new DialogGUIVerticalLayout(Width - 40f, -1f, 4f, new RectOffset(4, 4, 4, 4), TextAnchor.UpperLeft, rows.ToArray()));
            var buttons = new DialogGUIHorizontalLayout(Width - 20f, 30f, 6f, new RectOffset(), TextAnchor.MiddleCenter,
                Tip(new DialogGUIButton("Capture", viewer.RequestCapture, 110f, 26f, false), "Capture Picture"),
                Tip(new DialogGUIButton(() => "Flip A/B (" + viewer.Flip.Showing + ")", viewer.FlipAB, 130f, 26f, false), "Flip A And B"),
                new DialogGUIButton("Close", Close, 90f, 26f, false));
            var dialog = new MultiOptionDialog("ReDefinitionCloudViewer", "", "Cloud Viewer", HighLogic.UISkin, Width, list, buttons);
            shown = DialogWindow.Show(PopupDialog.SpawnPopupDialog(new Vector2(0.75f, 0.5f), new Vector2(0.75f, 0.5f), dialog, true, HighLogic.UISkin, true));
            ScrollListSettle.Apply(list);
            ScrollBackground.Clear(shown.gameObject);
            ScrollThrough.Apply(shown.gameObject);
            UiHygiene.MouseOnly(shown.gameObject);
            EscapeGuard.Own(shown);
            TooltipText.WrapAll(new DialogGUIBase[] { list, buttons });
            DialogTooltips.Attach(new DialogGUIBase[] { list, buttons });
            RestoreScroll();
            Driver driver = shown.gameObject.AddComponent<Driver>();
            driver.Window = this;
            driver.Dialog = shown;
        }

        private void SaveScroll()
        {
            if (shown == null) return;
            UnityEngine.UI.ScrollRect rect = shown.GetComponentInChildren<UnityEngine.UI.ScrollRect>();
            if (rect != null) scroll = rect.verticalNormalizedPosition;
        }

        private void RestoreScroll()
        {
            UnityEngine.UI.ScrollRect rect = shown.GetComponentInChildren<UnityEngine.UI.ScrollRect>();
            if (rect == null) return;
            Canvas.ForceUpdateCanvases();
            rect.verticalNormalizedPosition = scroll;
        }

        private static DialogGUIBase Folded(string id, List<DialogGUIBase> body)
        {
            var header = new DialogGUIButton(() => (Open.Contains(id) ? "-  " : "+  ") + id, () => { if (!Open.Remove(id)) Open.Add(id); }, PageWidth, 26f, false);
            var column = new FoldColumn(PageWidth, 2f, body.ToArray());
            column.OptionEnabledCondition = () => Open.Contains(id);
            return new DialogGUIVerticalLayout(PageWidth, -1f, 2f, new RectOffset(), TextAnchor.UpperLeft, header, column);
        }

        private static T Tip<T>(T element, string tooltip) where T : DialogGUIBase
        {
            element.tooltipText = tooltip;
            return element;
        }

        private static DialogGUIHorizontalLayout Line(params DialogGUIBase[] children)
        {
            return new DialogGUIHorizontalLayout(0f, RowHeight + 2f, 4f, new RectOffset(), TextAnchor.MiddleLeft, children);
        }

        // --- Status ---

        private List<DialogGUIBase> StatusRows()
        {
            // One line each, none longer than the page: a label with changing text that wraps gets no height and overlaps the next.
            return new List<DialogGUIBase>
            {
                new DialogGUILabel(() => viewer.PoseText(), PageWidth),
                new DialogGUILabel(() => viewer.HoldText(), PageWidth),
                new DialogGUILabel(() => viewer.TimeText(), PageWidth),
                new DialogGUILabel(() => viewer.WindText(), PageWidth),
                new DialogGUILabel(() => "Weather " + WeatherPresets.Title(viewer.Weather) + ", pose " + PosePresets.Title(viewer.PoseName) + ", options " + viewer.Flip.Showing
                                         + (viewer.Baseline ? ", baseline: no layer drawn" : ""), PageWidth),
                new DialogGUILabel(() => viewer.WatchText(), PageWidth),
                new DialogGUILabel(() => viewer.LastCapture.Length > 0 ? "Last picture " + viewer.LastCapture : "Pictures go to PluginData/CloudViewer/Captures", PageWidth)
            };
        }

        // --- Scene ---

        private List<DialogGUIBase> SceneRows()
        {
            var rows = new List<DialogGUIBase>();
            List<ViewerLayer> layers = CloudViewer.Layers();
            var poseButtons = new List<DialogGUIBase>();
            foreach (ViewerPose pose in PosePresets.For(layers))
            {
                ViewerPose p = pose;
                poseButtons.Add(Tip(new DialogGUIButton(PosePresets.Title(p.Name), () => viewer.PlacePose(p), 134f, RowHeight, false), PosePresets.Title(p.Name) + " Pose"));
            }
            for (int i = 0; i < poseButtons.Count; i += 4) rows.Add(Line(poseButtons.GetRange(i, Math.Min(4, poseButtons.Count - i)).ToArray()));
            rows.Add(Line(Tip(new DialogGUIButton("Use Current Spot", viewer.UseCurrentSpot, 160f, RowHeight, false), "Site Here"),
                          new DialogGUILabel(() => "Site " + viewer.SiteLat.ToString("0.000") + ", " + viewer.SiteLon.ToString("0.000"), 390f)));

            // The altitude above the ground on a log scale, from 10 m to the orbit edge; a typed number in metres does the same.
            double depth = GameGlobals.ActiveVessel != null && GameGlobals.ActiveVessel.mainBody != null ? GameGlobals.ActiveVessel.mainBody.atmosphereDepth : 70000.0;
            double maxAgl = AltitudeScale.MaxAglM(depth);
            float shownAltitude = AltitudeScale.ToSlider(double.IsNaN(viewer.TargetAglM) ? 200.0 : viewer.TargetAglM);
            var altitude = new DialogGUISlider(() => shownAltitude, AltitudeScale.ToSlider(AltitudeScale.MinAglM), AltitudeScale.ToSlider(maxAgl), false, 250f, -1f, f =>
            {
                if (Mathf.Approximately(f, shownAltitude)) return;
                shownAltitude = f;
                viewer.RequestAltitude(AltitudeScale.FromSlider(f, maxAgl));
            });
            var typedAltitude = new DialogGUITextInput("", false, 8, text =>
            {
                double m;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out m) && m >= AltitudeScale.MinAglM && m <= maxAgl)
                {
                    shownAltitude = AltitudeScale.ToSlider(m);
                    viewer.RequestAltitude(m);
                }
                return text;
            }, 90f, RowHeight);
            rows.Add(Line(new DialogGUILabel("Altitude", 80f), Tip(altitude, "Height Above Ground"),
                          new DialogGUILabel(() => double.IsNaN(viewer.TargetAglM) ? "-" : Metres(viewer.TargetAglM), 110f), Tip(typedAltitude, "Height In Metres")));

            // The time of day: an offset of up to half the body's solar day either way, and the frozen clock.
            double day = GameGlobals.ActiveVessel != null && GameGlobals.ActiveVessel.mainBody != null && GameGlobals.ActiveVessel.mainBody.solarDayLength > 0.0
                ? GameGlobals.ActiveVessel.mainBody.solarDayLength : 21600.0;
            float shownOffset = viewer.Clock != null ? (float)(viewer.Clock.OffsetS / day) : 0f;
            var timeOfDay = new DialogGUISlider(() => shownOffset, -0.5f, 0.5f, false, 250f, -1f, f =>
            {
                if (Mathf.Approximately(f, shownOffset)) return;
                shownOffset = f;
                viewer.SetTimeOffset(f * day);
            });
            rows.Add(Line(new DialogGUILabel("Time", 80f), Tip(timeOfDay, "Time Of Day"),
                          new DialogGUILabel(() => (shownOffset * day / 3600.0).ToString("+0.0;-0.0") + " h", 110f),
                          Tip(new DialogGUIToggle(() => viewer.Clock != null && viewer.Clock.Frozen, "Freeze Time", on => viewer.FreezeTime(on), 100f, RowHeight), "Freeze Time")));

            var weatherButtons = new List<DialogGUIBase>();
            foreach (ViewerWeather w in Enum.GetValues(typeof(ViewerWeather)))
            {
                ViewerWeather weather = w;
                weatherButtons.Add(Tip(new DialogGUIButton(() => (viewer.Weather == weather ? "> " : "") + WeatherPresets.Title(weather), () => viewer.SetWeather(weather), 180f, RowHeight, false),
                                       WeatherPresets.Title(weather)));
            }
            for (int i = 0; i < weatherButtons.Count; i += 3) rows.Add(Line(weatherButtons.GetRange(i, Math.Min(3, weatherButtons.Count - i)).ToArray()));

            // The cloud speed on a square-root scale (fine steps near 1), 0 to 50 times the wind.
            var speed = new DialogGUISlider(() => (float)Math.Sqrt(viewer.CloudSpeed), 0f, (float)Math.Sqrt(CloudMotion.MaxScale), false, 250f, -1f, f =>
            {
                if (Mathf.Approximately(f, (float)Math.Sqrt(viewer.CloudSpeed))) return;
                viewer.SetCloudSpeed(f * f);
            });
            rows.Add(Line(new DialogGUILabel("Cloud Speed", 80f), Tip(speed, "Cloud Speed"),
                          new DialogGUILabel(() => "x" + viewer.CloudSpeed.ToString("0.##"), 50f),
                          Tip(new DialogGUIButton("1x", () => viewer.SetCloudSpeed(1f), 50f, RowHeight, false), "Normal Speed"),
                          Tip(new DialogGUIToggle(() => viewer.CloudsFrozen, "Freeze Clouds", on => viewer.FreezeClouds(on), 100f, RowHeight), "Freeze Clouds")));
            rows.Add(Line(Tip(new DialogGUIToggle(() => viewer.Baseline, "Baseline: No Layer Drawn", on => viewer.SetBaseline(on), 260f, RowHeight), "No Layer Drawn"),
                          Tip(new DialogGUIButton("Refresh Layers", Rebuild, 140f, RowHeight, false), "Refresh Layers")));
            return rows;
        }

        private void Rebuild()
        {
            viewer.RefreshKeys();
            Build();
        }

        private static string Metres(double m)
        {
            return m >= 10000.0 ? (m / 1000.0).ToString("0.0") + " km" : m.ToString("0") + " m";
        }

        // --- Options ---

        private List<DialogGUIBase> OptionRows()
        {
            var rows = new List<DialogGUIBase>();
            foreach (string key in CloudViewer.ShortSettings)
            {
                SettingDef def = AtmospheresSettings.Definition(key);
                if (def != null) rows.Add(SettingRow(def));
            }
            return rows;
        }

        private DialogGUIBase SettingRow(SettingDef def)
        {
            var label = Tip(new DialogGUILabel(def.Tooltip, 220f), def.Tooltip);
            DialogGUIBase control;
            switch (def.Kind)
            {
                case SettingKind.Bool:
                    control = new DialogGUIToggle(() => viewer.Store.Setting(def.Key) != 0.0, () => viewer.Store.Setting(def.Key) != 0.0 ? "On" : "Off",
                        on => { if ((viewer.Store.Setting(def.Key) != 0.0) != on) viewer.SetSetting(def.Key, on ? 1.0 : 0.0); }, 220f, RowHeight);
                    break;
                case SettingKind.Choice:
                    // A choice steps to the next name on each click.
                    control = new DialogGUIButton(() => ValueFormat.ChoiceName(def.Choices[(int)viewer.Store.Setting(def.Key)]) + "   [>]",
                        () => viewer.SetSetting(def.Key, ((int)viewer.Store.Setting(def.Key) + 1) % def.Choices.Length), 220f, RowHeight, false);
                    break;
                default:
                    control = new DialogGUISlider(() => (float)viewer.Store.Setting(def.Key), (float)def.Min, (float)def.Max, def.Kind == SettingKind.Int, 220f, -1f, f =>
                    {
                        float now = (float)viewer.Store.Setting(def.Key);
                        if (Mathf.Approximately(f, now)) return;
                        viewer.SetSetting(def.Key, f);
                    });
                    break;
            }
            control.tooltipText = def.Tooltip;
            var value = new DialogGUILabel(() => def.Kind == SettingKind.Bool || def.Kind == SettingKind.Choice ? " " : def.Format(viewer.Store.Setting(def.Key)), 90f);
            return Line(label, control, value);
        }

        // --- Compare and presets ---

        private List<DialogGUIBase> CompareRows()
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
            var presetButtons = new List<DialogGUIBase>();
            foreach (string name in CloudViewer.PresetNames())
            {
                string n = name;
                presetButtons.Add(Tip(new DialogGUIButton(n, () => viewer.LoadPreset(n), 134f, RowHeight, false), "Load Preset"));
            }
            for (int i = 0; i < presetButtons.Count; i += 4) rows.Add(Line(presetButtons.GetRange(i, Math.Min(4, presetButtons.Count - i)).ToArray()));
            if (presetButtons.Count == 0) rows.Add(new DialogGUILabel("No preset saved yet.", PageWidth));
            return rows;
        }

        // --- All keys ---

        private List<DialogGUIBase> KeyRows()
        {
            var rows = new List<DialogGUIBase>
            {
                Line(new DialogGUILabel("Search", 80f), Tip(new DialogGUITextInput(search, false, 40, text => { search = text ?? ""; return text; }, 300f, RowHeight), "Search Keys"),
                     new DialogGUILabel(() => viewer.Keys.Count + " keys", 150f)),
                new DialogGUILabel("Key, value, default, when it is read; R resets, ? checks the reads.", PageWidth)
            };
            foreach (CloudKey key in viewer.Keys) rows.Add(KeyRow(key));
            return rows;
        }

        private DialogGUIBase KeyRow(CloudKey key)
        {
            string tip = key.Key + (key.Note.Length > 0 ? ": " + key.Note : "");
            var name = Tip(new DialogGUILabel(() => (Overridden(key) ? "* " : "") + key.Key, 220f), tip);
            var input = new DialogGUITextInput(Format(viewer.Store.Effective(key.Key, key.Default)), false, 14, text =>
            {
                float v;
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !float.IsNaN(v) && !float.IsInfinity(v)
                    && v != viewer.Store.Effective(key.Key, key.Default)) viewer.SetKey(key.Key, v);
                return text;
            }, 90f, RowHeight);
            var def = Tip(new DialogGUILabel(Format(key.Default), 70f), "Default");
            var live = Tip(new DialogGUILabel(Short(key.Liveness), 70f), CloudKeyRegistry.LivenessText(key.Liveness));
            var reset = Tip(new DialogGUIButton("R", () => { viewer.ResetKey(key.Key); Build(); }, 36f, RowHeight, false), "Reset To Default");
            var check = Tip(new DialogGUIButton("?", () => viewer.WatchKey(key.Key), 36f, RowHeight, false), "Check Reads");
            var column = new FoldColumn(PageWidth, 0f, Line(name, input, def, live, reset, check));
            column.OptionEnabledCondition = () => CloudKeyRegistry.Matches(key, search);
            return column;
        }

        private bool Overridden(CloudKey key)
        {
            float v;
            return viewer.Store.TryOverride(key.Key, out v);
        }

        private static string Short(KeyLiveness liveness)
        {
            return liveness == KeyLiveness.FrameHistory ? "history" : liveness == KeyLiveness.Change ? "change" : "frame";
        }

        private static string Format(float v)
        {
            return v.ToString("0.#####", CultureInfo.InvariantCulture);
        }

        // Gives the window's frame to the object of its dialog and tells when the dialog is gone.
        private sealed class Driver : MonoBehaviour
        {
            internal CloudViewerWindow Window;
            internal PopupDialog Dialog;

            private void OnDestroy()
            {
                if (Window != null) Window.Gone(Dialog);
            }
        }

        // A dialog's object is gone (Escape, a scene change): the session ends. A dialog that was replaced by a new build, or dismissed by Close, is no longer
        // the one shown and changes nothing (Unity destroys the object at the end of the frame, after the new one stands).
        private void Gone(PopupDialog dialog)
        {
            if (dialog == null || !ReferenceEquals(dialog, shown)) return;
            shown = null;
            try
            {
                viewer.Close();
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("cloud viewer", "the viewer could not be closed", e);
            }
        }
    }
}
