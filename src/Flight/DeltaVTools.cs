using System;
using System.Collections.Generic;
using System.Reflection;
using KSP.UI;
using KSP.UI.Screens;
using ReDefinition.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Flight
{
    // KSP's delta-v tools as part of the staging stack (docs/player/stage-details.md).
    // The tools' button no longer shows KSP's window (DeltaVApp.DisplayApp is
    // skipped): pressed, it opens every stage's details (StageManager.
    // ToggleInfoPanels) and, in the VAB and the SPH, a window under the lowest
    // stage, above the vessel's total delta-v; pressed again, it closes them. The
    // button counts as pressed while the launcher holds it as its pinned button
    // (ApplicationLauncher.lastPinnedButton): KSP turns its toggle off while the
    // pointer rests on another app's button and on again a frame after it leaves,
    // which is no press. The total delta-v's button, which opens and closes every
    // stage too (StageManager.DeltaVButtonClicked), presses the tools' button
    // instead. Every value is shown (DeltaVAppValues.InfoLine, set without writing
    // KSP's settings file), so the tools need no choice of values. A stage added
    // while the tools are open opens with the others, and so do the stages of a
    // craft loaded or started anew (GameEvents.onEditorStarted, which closes every
    // stage in KSP).
    //
    // The window is KSP's own: its app frame (DeltaVApp.appFramePrefab, titled as
    // KSP titles the tools) holding its situation panel (situationPrefab), made
    // compact: without its "Vessel Situation" heading and its row labels, the body
    // list across the row, the pressure beside the altitude. It stands in the
    // stack's bottom group (the parent of StageManager.deltaVTotalSection, outside
    // the stage list's mask), so it does not scroll with the stages and the list
    // ends above it. Its box is as wide as the widest open stage at every moment of
    // the slide, and grows in height with it; the frame, at its final size, is
    // pinned to the box's corner on the stages' side, so it slides in sideways and
    // from below. The editor's keys wait while its altitude is typed, as for KSP's
    // own tools' field (a postfix on DeltaVApp.TextFieldHasFocus). In flight KSP
    // works the stages out for where the vessel is and has no situation to choose,
    // so there the button opens and closes the details only.
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class DeltaVToolsEditor : DeltaVTools
    {
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class DeltaVToolsFlight : DeltaVTools
    {
    }

    public abstract class DeltaVTools : MonoBehaviour
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        // The app frame's own height beyond its list (DeltaVApp: 27).
        private const float FrameExtra = 27f;

        private static readonly FieldInfo FramePrefabField = typeof(DeltaVApp).GetField("appFramePrefab", Private);
        private static readonly FieldInfo SituationPrefabField = typeof(DeltaVApp).GetField("situationPrefab", Private);
        private static readonly FieldInfo PinnedField = typeof(ApplicationLauncher).GetField("lastPinnedButton", Private);
        private static readonly MethodInfo ClickField = typeof(ApplicationLauncher).GetMethod("MutuallyExclusiveStockApplistCLICK", Private);
        private static readonly MethodInfo InitEnabled = typeof(DeltaVAppValues.InfoLine).GetMethod("InitEnabledValue", Private);

        // The situation panel of the scene's tools, for the typing postfix.
        private static DeltaVAppSituation typedIn;

        private readonly HashSet<StageGroup> seen = new HashSet<StageGroup>();
        private bool open;
        // Closed, while the details still slide out.
        private bool closing;
        private bool failed;
        // A craft was loaded or started anew: every stage opens again a frame later.
        private int reopenAt = -1;
        private RectTransform box;
        private LayoutElement boxLayout;
        private RectTransform frame;
        private DeltaVAppSituation situation;
        private float height;

        // As the game starts (StageDetailsSetup): the button no longer shows KSP's
        // window, the total delta-v presses the button, and the altitude field holds
        // the editor's keys.
        internal static void Install()
        {
            HarmonyHooks.Install("ReDefinition.DeltaVTools", typeof(DeltaVTools), new List<HarmonyHook>
            {
                new HarmonyHook(typeof(DeltaVApp).GetMethod("DisplayApp", Private), nameof(SkipWindow), null, null),
                new HarmonyHook(typeof(StageManager).GetMethod("DeltaVButtonClicked", Private), nameof(PressButton), null, null),
                new HarmonyHook(typeof(DeltaVApp).GetMethod("TextFieldHasFocus", BindingFlags.Instance | BindingFlags.Public), null,
                    nameof(AlsoTyped), null),
            });
        }

        // The tools stand in the staging stack in this scene (OwnSettings.DeltaVTools);
        // KSP's own window and buttons otherwise.
        private static bool active = true;

        private static bool SkipWindow()
        {
            return !active;
        }

        // The total delta-v's button presses the tools' button, so that both open
        // and close the same. Runs inside KSP's StageManager.DeltaVButtonClicked:
        // nothing may escape; where it fails before the button is pressed, KSP's own
        // click runs.
        private static bool PressButton()
        {
            if (!active) return true;
            bool pressed = false;
            try
            {
                ApplicationLauncherButton button = DeltaVApp.Instance != null ? DeltaVApp.Instance.appLauncherButton : null;
                if (button == null || button.toggleButton == null) return true;
                bool pinned = Pinned(button);
                pressed = true;
                if (pinned) button.SetFalse();
                else button.SetTrue();
                // As a click on the button does: the launcher's pinned button follows
                // (ApplicationLauncher.MutuallyExclusiveStockApplistCLICK), and another
                // pinned app closes.
                ApplicationLauncher launcher = ApplicationLauncher.Instance;
                if (launcher != null && ClickField != null) ClickField.Invoke(launcher, new object[] { button.toggleButton });
                return false;
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("deltav-tools-press", "The total delta-v's button could not press the delta-v tools'"
                                                            + " button (" + CompatibilityLog.Reason(e) + ").");
                return !pressed;
            }
        }

        private static void AlsoTyped(ref bool __result)
        {
            if (!__result && typedIn != null && typedIn.isActiveAndEnabled) __result = typedIn.AltitudeHasFocus();
        }

        private void Start()
        {
            active = FlightUiSwitch.On(s => s.DeltaVTools);
            if (!active)
            {
                enabled = false;
                return;
            }
            GameEvents.onEditorStarted.Add(EditorStarted);
            StageInfoColumns.Arranged += Follow;
        }

        private void OnDestroy()
        {
            GameEvents.onEditorStarted.Remove(EditorStarted);
            StageInfoColumns.Arranged -= Follow;
            if (typedIn == situation) typedIn = null;
            if (box != null) Destroy(box.gameObject);
        }

        private void EditorStarted()
        {
            if (open) reopenAt = Time.frameCount + 1;
        }

        private void Update()
        {
            if (failed) return;
            DeltaVApp app = DeltaVApp.Instance;
            StageManager manager = StageManager.Instance;
            if (app == null || !DeltaVApp.Ready || manager == null) return;
            try
            {
                AllValues();
                bool wanted = Pinned(app.appLauncherButton);
                if (wanted != open)
                {
                    open = wanted;
                    closing = !open;
                    manager.ToggleInfoPanels(open);
                }
                if (reopenAt >= 0 && Time.frameCount >= reopenAt)
                {
                    reopenAt = -1;
                    if (open) manager.ToggleInfoPanels(true);
                }
                Stages(manager);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // Once the stages are arranged for the frame (StageInfoColumns, in its
        // LateUpdate): the box follows them in the same frame.
        private void Follow()
        {
            if (failed || !HighLogic.LoadedSceneIsEditor) return;
            DeltaVApp app = DeltaVApp.Instance;
            StageManager manager = StageManager.Instance;
            if (app == null || !DeltaVApp.Ready || manager == null) return;
            try
            {
                Window(app, manager);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private void Fail(Exception e)
        {
            failed = true;
            if (box != null) box.gameObject.SetActive(false);
            CompatibilityLog.Warn("deltav-tools", "The delta-v tools could not be shown in the staging stack ("
                                                  + CompatibilityLog.Reason(e) + ").");
        }

        // The launcher holds the tools' button as its pinned one; a stock app's
        // hover elsewhere turns its toggle off without changing that.
        private static bool Pinned(ApplicationLauncherButton button)
        {
            if (button == null || button.toggleButton == null) return false;
            if (button.toggleButton.CurrentState == UIRadioButton.State.True) return true;
            ApplicationLauncher launcher = ApplicationLauncher.Instance;
            return PinnedField != null && launcher != null && ReferenceEquals(PinnedField.GetValue(launcher), button.toggleButton);
        }

        // A stage added while the tools are open opens as well.
        private void Stages(StageManager manager)
        {
            foreach (StageGroup group in manager.Stages)
                if (group != null && seen.Add(group) && open && !StageGroupFields.PanelShown(group)) group.ToggleInfoPanel(true);
            seen.RemoveWhere(IsGone);
        }

        // Made once: a method group passed as is would be a new delegate every frame.
        private static readonly Predicate<StageGroup> IsGone = group => group == null;

        // Every value KSP can show beside the stages, without writing its settings
        // file (InfoLine.InitEnabledValue; the Enabled setter saves settings.cfg).
        private static void AllValues()
        {
            DeltaVAppValues values = DeltaVGlobals.DeltaVAppValues;
            if (values == null || values.infoLines == null) return;
            bool changed = false;
            foreach (DeltaVAppValues.InfoLine line in values.infoLines)
            {
                if (line == null || line.Enabled) continue;
                if (InitEnabled != null) InitEnabled.Invoke(line, new object[] { true });
                else line.Enabled = true;
                changed = true;
            }
            if (changed) GameEvents.onDeltaVAppInfoItemsChanged.Fire();
        }

        // ------------------------------------------------------------ window

        private void Window(DeltaVApp app, StageManager manager)
        {
            if (box == null && !Build(app, manager)) return;
            float target = 0f, now = 0f;
            foreach (StageGroup group in manager.Stages)
            {
                LayoutElement panel = group != null ? StageGroupFields.Panel(group) : null;
                if (panel == null) continue;
                target = Mathf.Max(target, StageGroupFields.PanelWidth(group));
                if (panel.gameObject.activeInHierarchy)
                    now = Mathf.Max(now, StageInfoColumns.IconsWidth(group) + Mathf.Max(0f, panel.preferredWidth));
            }
            float full = StageInfoColumns.TwoColumnIcons + target;
            float share = target > 0f ? Mathf.Clamp01((now - StageInfoColumns.IconsColumn) / (full - StageInfoColumns.IconsColumn)) : 0f;
            // As wide as the widest open stage now, in whole pixels, so that what
            // stands below does not shift by a fraction.
            float width = now > 0f ? Mathf.Round(now) : 0f;
            // Only as the tools open or close: a stage opened by itself shows its
            // details alone.
            if (closing && (width <= 0.5f || target <= 0f)) closing = false;
            bool shown = (open || closing) && target > 0f;
            if (box.gameObject.activeSelf != shown) box.gameObject.SetActive(shown);
            if (!shown) return;
            if (height <= 0f) height = Height();
            if (!Mathf.Approximately(boxLayout.preferredWidth, width)) boxLayout.preferredWidth = width;
            float tall = Mathf.Round(height * share);
            if (!Mathf.Approximately(boxLayout.preferredHeight, tall)) boxLayout.preferredHeight = tall;
            Vector2 size = new Vector2(Mathf.Round(full), height);
            if (frame.sizeDelta != size) frame.sizeDelta = size;
        }

        // The box in the stack's bottom group, right before the total delta-v; the
        // frame in it, anchored to the stages' side and clipped by the box.
        private bool Build(DeltaVApp app, StageManager manager)
        {
            GenericAppFrame framePrefab = FramePrefabField != null ? FramePrefabField.GetValue(app) as GenericAppFrame : null;
            DeltaVAppSituation situationPrefab = SituationPrefabField != null ? SituationPrefabField.GetValue(app) as DeltaVAppSituation : null;
            Transform total = manager.deltaVTotalSection != null ? manager.deltaVTotalSection.transform : null;
            if (framePrefab == null || situationPrefab == null || total == null || total.parent == null || StageGroupFields.Panel == null
                || StageGroupFields.PanelWidth == null)
            {
                failed = true;
                CompatibilityLog.Warn("deltav-tools", "The delta-v tools could not be built into the staging stack: KSP's parts are not as expected.");
                return false;
            }

            GameObject holder = ReDefinition.Window.UiHygiene.Child("ReDefinitionDeltaVTools", total.parent, typeof(RectTransform));
            box = (RectTransform)holder.transform;
            box.SetSiblingIndex(total.GetSiblingIndex());
            boxLayout = holder.AddComponent<LayoutElement>();
            boxLayout.flexibleWidth = 0f;
            boxLayout.flexibleHeight = 0f;
            holder.AddComponent<RectMask2D>();

            GenericAppFrame appFrame = Instantiate(framePrefab);
            frame = (RectTransform)appFrame.transform;
            frame.SetParent(box, false);
            frame.localScale = Vector3.one;
            // Pinned to the stages' side, the right in the VAB and the SPH, and to the
            // box's top: as the box grows, the frame rises into it from below.
            frame.anchorMin = new Vector2(1f, 1f);
            frame.anchorMax = new Vector2(1f, 1f);
            frame.pivot = new Vector2(1f, 1f);
            frame.anchoredPosition = Vector2.zero;
            if (appFrame.header != null) appFrame.header.text = KSP.Localization.Localizer.Format("#autoLOC_8003216");
            if (appFrame.dragHeader != null) appFrame.dragHeader.enabled = false;
            if (appFrame.dragFooter != null) appFrame.dragFooter.enabled = false;

            situation = Instantiate(situationPrefab);
            situation.transform.SetParent(appFrame.scrollList.transform, false);
            Compact(situation.transform);
            TMP_Dropdown body = situation.GetComponentInChildren<TMP_Dropdown>(true);
            if (body != null) ReDefinition.Window.UiHygiene.Tooltip(body.gameObject, "Celestial Body");
            typedIn = situation;
            holder.SetActive(false);
            Debug.Log(Log.Tag + " Delta-v tools built into the staging stack.");
            return true;
        }

        // The situation panel in three rows and a slider (the prefab, read with
        // UnityPy): its heading and the labels of the body, the atmosphere, the
        // altitude and the pressure go, the body list takes the row, and the pressure
        // stands at the end of the altitude's row.
        private static void Compact(Transform panel)
        {
            Hide(panel, "Header");
            Hide(panel, "Body/Body/LabelText");
            Hide(panel, "Body/Atmosphere");
            Hide(panel, "Body/AtmosphereControls/Altitude/LabelText");
            RectTransform dropdown = panel.Find("Body/Body/BodyDropdown") as RectTransform;
            if (dropdown != null)
            {
                dropdown.offsetMin = new Vector2(0f, dropdown.offsetMin.y);
                dropdown.offsetMax = new Vector2(0f, dropdown.offsetMax.y);
            }
            Transform row = panel.Find("Body/AtmosphereControls/Altitude");
            Transform pressure = panel.Find("Body/Pressure/pressureValue");
            if (row != null && pressure != null)
            {
                pressure.SetParent(row, false);
                pressure.SetAsLastSibling();
                LayoutElement layout = pressure.GetComponent<LayoutElement>();
                if (layout == null) layout = pressure.gameObject.AddComponent<LayoutElement>();
                layout.flexibleWidth = 1f;
                TextMeshProUGUI text = pressure.GetComponent<TextMeshProUGUI>();
                if (text != null) text.alignment = TextAlignmentOptions.MidlineRight;
                Hide(panel, "Body/Pressure");
            }
        }

        private static void Hide(Transform panel, string path)
        {
            Transform found = panel.Find(path);
            if (found != null) found.gameObject.SetActive(false);
        }

        // The frame's height for its list: the situation panel without its heading.
        private float Height()
        {
            RectTransform list = situation != null ? situation.transform.parent as RectTransform : null;
            if (list == null) return FrameExtra + 150f;
            LayoutRebuilder.ForceRebuildLayoutImmediate(list);
            float content = LayoutUtility.GetPreferredHeight(list);
            float result = Mathf.Round(FrameExtra + (content > 0f ? content : 150f));
            Debug.Log(Log.Tag + " Delta-v tools: " + result.ToString("0.#") + " high.");
            return result;
        }
    }
}
