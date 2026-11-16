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
    internal sealed partial class CloudViewerWindow
    {
        internal static readonly CloudViewerWindow Instance = new CloudViewerWindow();

        private const float Width = 640f, PageWidth = 560f, RowHeight = 24f;
        private const string StatusGroup = "Status", SceneGroup = "Scene";
        private static readonly HashSet<string> Open = new HashSet<string> { StatusGroup, SceneGroup, CostGroup };

        private readonly CloudViewer viewer = CloudViewer.Instance;
        private PopupDialog shown;
        private string search = "";
        private string presetName = "";
        private float scroll = 1f;
        private readonly TypedAltitude typedHeight = new TypedAltitude();
        private DialogGUITextInput typedAltitude;
        private double maxTypedAgl = 300000.0;

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

        // Keeps the window inside the screen (ViewerWindowFit) and answers where it stands.
        private ViewerWindowFit fit;

        // The window's box on the screen in pixels; false where there is no window or it cannot be measured.
        internal bool WindowBox(out ScreenBox box)
        {
            box = default(ScreenBox);
            return shown != null && fit != null && fit.Box(out box);
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
            typedHeight.Clear();
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
            typedHeight.Clear();
            if (old != null) old.Dismiss();
            viewer.Close(sceneEnding);
        }

        // Every frame (CloudViewerAddon): the window is hidden while a picture is taken, and a typed height that has stood for a moment is placed.
        internal void Tick()
        {
            if (shown == null) return;
            bool hide = viewer.Capturing;
            if (shown.gameObject.activeSelf == hide) shown.gameObject.SetActive(!hide);
            if (typedHeight.Pending) PlaceTyped(false);
        }

        // The typed height where one is due: ended is true where the field's edit has ended (Enter, focus lost).
        private void PlaceTyped(bool ended)
        {
            double agl;
            if (typedHeight.TryTake(Time.unscaledTime, ended, maxTypedAgl, out agl)) viewer.RequestAltitude(agl);
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
                Folded(CostGroup, CostRows()),
                Folded(SceneGroup, SceneRows()),
                Folded(ModesGroup, ModeRows()),
                Folded(LabGroup, LabRows()),
                Folded(PresetsGroup, PresetRows())
            };
            rows.AddRange(KeySections());
            // The list is as high as the screen leaves room for at KSP's UI scale and scrolls where its content is longer; the title and the buttons stay in view.
            var list = new TabScrollList(new Vector2(Width - 20f, WindowPlacement.ListHeight(Screen.height, GameSettings.UI_SCALE)),
                new DialogGUIVerticalLayout(Width - 40f, -1f, 4f, new RectOffset(4, 4, 4, 4), TextAnchor.UpperLeft, rows.ToArray()));
            var buttons = new DialogGUIHorizontalLayout(Width - 20f, 30f, 6f, new RectOffset(), TextAnchor.MiddleCenter,
                Tip(new DialogGUIButton("Capture", viewer.RequestCapture, 100f, 26f, false), "Capture Picture"),
                Tip(new DialogGUIButton(() => "Flip A/B (" + viewer.Flip.Showing + ")", viewer.FlipAB, 120f, 26f, false), "Flip A And B"),
                Tip(new DialogGUIButton(() => WeatherRebuild.Active ? "Rebuilding..." : "Rebuild Weather", () => viewer.RebuildWeather(), 140f, 26f, false), "Rebuild Weather"),
                new DialogGUIButton("Close", Close, 90f, 26f, false));
            var dialog = new MultiOptionDialog("ReDefinitionCloudViewer", "", "Cloud Viewer", HighLogic.UISkin, Width, list, buttons);
            shown = DialogWindow.Show(PopupDialog.SpawnPopupDialog(new Vector2(0.75f, 0.5f), new Vector2(0.75f, 0.5f), dialog, true, HighLogic.UISkin, true));
            fit = ViewerWindowFit.Add(shown);
            ScrollListSettle.Apply(list);
            ScrollBackground.Clear(shown.gameObject);
            ScrollThrough.Apply(shown.gameObject);
            UiHygiene.MouseOnly(shown.gameObject);
            EscapeGuard.Own(shown);
            TooltipText.WrapAll(new DialogGUIBase[] { list, buttons });
            DialogTooltips.Attach(new DialogGUIBase[] { list, buttons });
            RestoreScroll();
            TMPro.TMP_InputField typedField = typedAltitude != null && typedAltitude.uiItem != null ? typedAltitude.uiItem.GetComponent<TMPro.TMP_InputField>() : null;
            if (typedField != null) typedField.onEndEdit.AddListener(text => PlaceTyped(true));
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
                new DialogGUILabel(() => viewer.SoloText(), PageWidth),
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
            // The slider shows the height the viewer holds, so that a pose button moves it too.
            var altitude = new DialogGUISlider(() => viewer.AltitudeSlider, AltitudeScale.ToSlider(AltitudeScale.MinAglM), AltitudeScale.ToSlider(maxAgl), false, 250f, -1f, f =>
            {
                if (Mathf.Approximately(f, viewer.AltitudeSlider)) return;
                viewer.RequestAltitude(AltitudeScale.FromSlider(f, maxAgl));
            });
            // The typed height is placed when the edit ends (Enter, the field loses the keyboard) or the typing stops, not on every keystroke.
            maxTypedAgl = maxAgl;
            typedAltitude = new DialogGUITextInput("", false, 8, text =>
            {
                typedHeight.Typed(text, Time.unscaledTime);
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
            rows.AddRange(SoloRows());
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

        // The modes, the weather lab, the presets and the key table are in CloudViewerWindow.Lab.cs.

        private bool Overridden(CloudKey key)
        {
            float v;
            return viewer.Store.TryOverride(key.Key, out v);
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
