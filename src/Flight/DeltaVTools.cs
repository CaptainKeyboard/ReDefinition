using System;
using System.Collections.Generic;
using System.Reflection;
using KSP.UI;
using KSP.UI.Screens;
using ReDefinition.Core;
using ReDefinition.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Flight
{
    // KSP's delta-v tools as part of the staging stack (docs/player/stage-details.md).
    // The tools' button no longer shows KSP's window (DeltaVApp.DisplayApp is
    // skipped): pressed, it opens every stage's details (StageManager.
    // ToggleInfoPanels) and, in the VAB and the SPH, a window under the lowest
    // stage, above the vessel's total delta-v; pressed again, it closes them. Every
    // value is shown (DeltaVAppValues.InfoLine.Enabled), so the tools need no choice
    // of values. A stage added while the tools are open opens with the others.
    // While the pointer is on
    // another app's button, KSP sets this one's toggle off until it leaves
    // (ApplicationLauncher's mutually exclusive hover); that is no press.
    //
    // The window is KSP's own: its app frame (DeltaVApp.appFramePrefab, titled as
    // KSP titles the tools) holding its situation panel (situationPrefab), made
    // compact: without its "Vessel Situation" heading and its row labels, the body
    // list across the row, the pressure beside the altitude. It
    // stands in the stack's bottom group (the parent of StageManager.
    // deltaVTotalSection, outside the stage list's mask), so it does not scroll with
    // the stages and the list ends above it. It is as wide as a stage with its
    // details, and grows and shrinks with them as KSP slides them in and out, the
    // frame revealed from the stages' side. In flight KSP works the stages out for
    // where the vessel is and has no situation to choose, so there the button opens
    // and closes the details only.
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
        private const string TypingLock = "ReDefinitionDeltaVToolsTyping";
        // The app frame's own height beyond its list (DeltaVApp: 27).
        private const float FrameExtra = 27f;

        private static readonly FieldInfo FramePrefabField = typeof(DeltaVApp).GetField("appFramePrefab", Private);
        private static readonly FieldInfo SituationPrefabField = typeof(DeltaVApp).GetField("situationPrefab", Private);
        private static readonly FieldInfo PanelField = typeof(StageGroup).GetField("InfoPanelLayout", Private);
        private static readonly FieldInfo PanelWidthField = typeof(StageGroup).GetField("InfoPanelWidth", Private);
        private static readonly FieldInfo ShownField = typeof(StageGroup).GetField("infoPanelShown", Private);
        private static readonly FieldInfo AppListField = typeof(ApplicationLauncher).GetField("appList", Private);
        private static readonly FieldInfo ModListField = typeof(ApplicationLauncher).GetField("appListMod", Private);
        private readonly HashSet<StageGroup> seen = new HashSet<StageGroup>();

        private bool open;
        // Closed, while the details still slide out.
        private bool closing;
        private bool failed;
        private bool typing;
        private RectTransform box;
        private LayoutElement boxLayout;
        private RectTransform frame;
        private DeltaVAppSituation situation;
        private float height;

        // The button no longer shows KSP's window; installed once as the game starts
        // (StageDetailsSetup).
        internal static void Install()
        {
            MethodInfo display = typeof(DeltaVApp).GetMethod("DisplayApp", Private);
            HarmonyHooks.Install("ReDefinition.DeltaVTools", typeof(DeltaVTools),
                new List<HarmonyHook> { new HarmonyHook(display, nameof(SkipWindow), null, null) });
        }

        private static bool SkipWindow()
        {
            return false;
        }

        private void OnDestroy()
        {
            if (typing) InputLockManager.RemoveControlLock(TypingLock);
            if (box != null) Destroy(box.gameObject);
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
                ApplicationLauncherButton button = app.appLauncherButton;
                bool wanted = button != null && button.toggleButton != null && button.toggleButton.CurrentState == UIRadioButton.State.True;
                if (wanted != open && !OtherHovered(button))
                {
                    open = wanted;
                    closing = !open;
                    manager.ToggleInfoPanels(open);
                }
                Stages(manager);
                if (HighLogic.LoadedSceneIsEditor) Window(app, manager);
                KeepTyping();
            }
            catch (Exception e)
            {
                failed = true;
                CompatibilityLog.Warn("deltav-tools", "The delta-v tools could not be shown in the staging stack ("
                                                      + CompatibilityLog.Reason(e) + ").");
            }
        }

        // A stage added while the tools are open opens as well.
        private void Stages(StageManager manager)
        {
            foreach (StageGroup group in manager.Stages)
                if (group != null && seen.Add(group) && open && !(bool)ShownField.GetValue(group)) group.ToggleInfoPanel(true);
            seen.RemoveWhere(group => group == null);
        }

        // The pointer rests on another app's button.
        private static bool OtherHovered(ApplicationLauncherButton own)
        {
            ApplicationLauncher launcher = ApplicationLauncher.Instance;
            if (launcher == null) return false;
            foreach (FieldInfo field in new[] { AppListField, ModListField })
            {
                List<ApplicationLauncherButton> buttons = field != null ? field.GetValue(launcher) as List<ApplicationLauncherButton> : null;
                if (buttons == null) continue;
                foreach (ApplicationLauncherButton other in buttons)
                    if (other != null && other != own && other.IsHovering) return true;
            }
            return false;
        }

        // Every value KSP can show beside the stages.
        private static void AllValues()
        {
            DeltaVAppValues values = DeltaVGlobals.DeltaVAppValues;
            if (values == null || values.infoLines == null) return;
            foreach (DeltaVAppValues.InfoLine line in values.infoLines)
                if (line != null && !line.Enabled) line.Enabled = true;
        }

        // ------------------------------------------------------------ window

        private void Window(DeltaVApp app, StageManager manager)
        {
            if (box == null && !Build(app, manager)) return;
            float target = 0f, now = 0f, icons = StageInfoColumns.IconsColumn;
            foreach (StageGroup group in manager.Stages)
            {
                LayoutElement panel = group != null ? PanelField.GetValue(group) as LayoutElement : null;
                if (panel == null) continue;
                target = Mathf.Max(target, (float)PanelWidthField.GetValue(group));
                if (!panel.gameObject.activeInHierarchy) continue;
                now = Mathf.Max(now, panel.preferredWidth);
                icons = Mathf.Max(icons, StageInfoColumns.IconsWidth(group));
            }
            if (target <= 0f) return;
            float full = icons + target;
            // Growing and shrinking with the details as they slide.
            float width = full * Mathf.Clamp01(now / target);
            // Only as the tools open or close: a stage opened by itself shows its
            // details alone.
            if (closing && width <= 0.5f) closing = false;
            bool shown = open || closing;
            if (box.gameObject.activeSelf != shown) box.gameObject.SetActive(shown);
            if (!shown) return;
            if (!Mathf.Approximately(boxLayout.preferredWidth, width)) boxLayout.preferredWidth = width;
            if (!Mathf.Approximately(frame.sizeDelta.x, full)) frame.sizeDelta = new Vector2(full, 0f);
            if (height <= 0f) height = Height();
            if (!Mathf.Approximately(boxLayout.preferredHeight, height)) boxLayout.preferredHeight = height;
        }

        // The box in the stack's bottom group, right before the total delta-v; the
        // frame in it, anchored to the stages' side and clipped by the box.
        private bool Build(DeltaVApp app, StageManager manager)
        {
            GenericAppFrame framePrefab = FramePrefabField != null ? FramePrefabField.GetValue(app) as GenericAppFrame : null;
            DeltaVAppSituation situationPrefab = SituationPrefabField != null ? SituationPrefabField.GetValue(app) as DeltaVAppSituation : null;
            Transform total = manager.deltaVTotalSection != null ? manager.deltaVTotalSection.transform : null;
            if (framePrefab == null || situationPrefab == null || total == null || total.parent == null || PanelField == null
                || PanelWidthField == null)
            {
                failed = true;
                CompatibilityLog.Warn("deltav-tools", "The delta-v tools could not be built into the staging stack: KSP's parts are not as expected.");
                return false;
            }

            GameObject holder = new GameObject("ReDefinitionDeltaVTools", typeof(RectTransform));
            box = (RectTransform)holder.transform;
            box.SetParent(total.parent, false);
            box.SetSiblingIndex(total.GetSiblingIndex());
            boxLayout = holder.AddComponent<LayoutElement>();
            boxLayout.flexibleWidth = 0f;
            boxLayout.flexibleHeight = 0f;
            holder.AddComponent<RectMask2D>();

            GenericAppFrame appFrame = Instantiate(framePrefab);
            frame = (RectTransform)appFrame.transform;
            frame.SetParent(box, false);
            frame.localScale = Vector3.one;
            // Pinned to the stages' side: the right in the VAB and the SPH.
            frame.anchorMin = new Vector2(1f, 0f);
            frame.anchorMax = new Vector2(1f, 1f);
            frame.pivot = new Vector2(1f, 0.5f);
            frame.anchoredPosition = Vector2.zero;
            if (appFrame.header != null) appFrame.header.text = KSP.Localization.Localizer.Format("#autoLOC_8003216");
            if (appFrame.dragHeader != null) appFrame.dragHeader.enabled = false;
            if (appFrame.dragFooter != null) appFrame.dragFooter.enabled = false;

            situation = Instantiate(situationPrefab);
            situation.transform.SetParent(appFrame.scrollList.transform, false);
            Compact(situation.transform);
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
                LayoutElement layout = pressure.GetComponent<LayoutElement>() ?? pressure.gameObject.AddComponent<LayoutElement>();
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
            float result = FrameExtra + (content > 0f ? content : 150f);
            Debug.Log(Log.Tag + " Delta-v tools: " + result.ToString("0.#") + " high.");
            return result;
        }

        // While the altitude is typed, the editor's keys are held, as KSP holds them
        // for its own tools' field (DeltaVApp.AnyTextFieldHasFocus).
        private void KeepTyping()
        {
            bool now = situation != null && box != null && box.gameObject.activeInHierarchy && situation.AltitudeHasFocus();
            if (now == typing) return;
            typing = now;
            if (typing) InputLockManager.SetControlLock(ControlTypes.All, TypingLock);
            else InputLockManager.RemoveControlLock(TypingLock);
        }
    }
}
