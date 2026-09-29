using System;
using System.Collections.Generic;
using System.Reflection;
using KSP.UI.Screens;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Flight
{
    // The stage details of KSP's staging stack in two columns once more than four
    // are chosen in the delta-v tools, so that each stage grows wider rather than
    // taller (docs/player/stage-details.md). KSP puts one line per chosen value
    // under the stage's info panel (StageGroup.InfoPanelLayout, its lines
    // StageGroupInfoItem in the private list stageGroupInfos), one below the
    // other, the panel as wide as InfoPanelWidth (GameSettings.
    // STAGE_GROUP_INFO_WIDTH_EDITOR or _FLIGHT, 100) and each line's name and
    // value sized from that width (StageGroupInfoItem.Setup); decompiled. Here,
    // with more than four lines, the panel is twice as wide (InfoPanelWidth, which
    // KSP's slide in and out also takes), its vertical layout gives way to a grid
    // of two columns filled top to bottom, and each line is sized for one column.
    // With four or fewer, the panel's own layout comes back. The columns follow
    // the panel's width as KSP slides it in and out (StageGroup.ToggleInfoPanel,
    // preferredWidth from 0 to InfoPanelWidth), so the lines slide in as before.
    // Checked before the canvases draw, after KSP's own updates of the lines.
    //
    // KSP's delta-v tools (DeltaVApp) stand beside their button, where the wider
    // stages run under them: the window is moved left of the stages, frame by
    // frame, so that it follows them as they slide; back where KSP puts it once
    // they leave room. And the stages' delta-v, which KSP writes under each stage's
    // number anyway, is no value to choose: its line (DeltaVAppValues.infoLines,
    // "DELTAV") is taken out as the game starts (StageDetailsSetup).
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class StageInfoColumnsEditor : StageInfoColumns
    {
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class StageInfoColumnsFlight : StageInfoColumns
    {
    }

    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class StageDetailsSetup : MonoBehaviour
    {
        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        // Once KSP has made its lines (DeltaVGlobals.InitInfoLines), before any scene
        // builds its delta-v tools.
        private void Update()
        {
            if (StageInfoColumns.TakeOutDeltaV()) Destroy(this);
        }
    }

    public abstract class StageInfoColumns : MonoBehaviour
    {
        private const int MostInOneColumn = 4;
        private const float Gap = 4f;
        private const string DeltaVLine = "DELTAV";
        private static readonly FieldInfo AppFrameField = typeof(DeltaVApp).GetField("appFrame", Private);
        private RectTransform movedFrame;
        private Vector2 placedAt;
        private Vector2 shiftedTo;

        // KSP's delta-v line taken out of the values to choose; true once done.
        internal static bool TakeOutDeltaV()
        {
            DeltaVAppValues values = DeltaVGlobals.DeltaVAppValues;
            if (values == null || values.infoLines == null || values.infoLines.Count == 0) return false;
            int removed = values.infoLines.RemoveAll(line => line != null && line.name == DeltaVLine);
            if (removed > 0) Debug.Log(Log.Tag + " Stage details: the delta-v line taken out of KSP's values to choose.");
            return true;
        }
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly FieldInfo InfosField = typeof(StageGroup).GetField("stageGroupInfos", Private);
        private static readonly FieldInfo WidthField = typeof(StageGroup).GetField("InfoPanelWidth", Private);
        private static readonly FieldInfo ShownField = typeof(StageGroup).GetField("infoPanelShown", Private);
        private static readonly FieldInfo SlidingField = typeof(StageGroup).GetField("infoPanelSliding", Private);
        private static readonly FieldInfo PanelField = typeof(StageGroup).GetField("InfoPanelLayout", Private);

        // The panel's own vertical layout, to be put back.
        private sealed class Column
        {
            public RectOffset Padding;
            public float Spacing;
            public TextAnchor Alignment;
            public bool ControlWidth, ControlHeight, ExpandWidth, ExpandHeight;
            public float LineHeight;
        }

        private readonly Dictionary<GameObject, Column> columns = new Dictionary<GameObject, Column>();
        private bool noted;
        private bool failed;

        private void Start()
        {
            if (InfosField == null || WidthField == null || ShownField == null || SlidingField == null || PanelField == null)
            {
                CompatibilityLog.Warn("stage-info-columns", "The stage details stay in one column: KSP's staging stack is not as expected.");
                enabled = false;
                return;
            }
            Canvas.willRenderCanvases += Arrange;
        }

        private void OnDestroy()
        {
            Canvas.willRenderCanvases -= Arrange;
        }

        private void Arrange()
        {
            if (failed) return;
            StageManager manager = StageManager.Instance;
            if (manager == null || manager.Stages == null) return;
            float column = HighLogic.LoadedSceneIsEditor ? GameSettings.STAGE_GROUP_INFO_WIDTH_EDITOR : GameSettings.STAGE_GROUP_INFO_WIDTH_FLIGHT;
            try
            {
                TakeOutDeltaV();
                foreach (StageGroup group in manager.Stages)
                    if (group != null) Arrange(group, column);
                KeepAppClear(manager);
            }
            catch (Exception e)
            {
                failed = true;
                CompatibilityLog.Warn("stage-info-columns", "The stage details stay in one column (" + CompatibilityLog.Reason(e) + ").");
            }
        }

        private void Arrange(StageGroup group, float column)
        {
            LayoutElement panel = PanelField.GetValue(group) as LayoutElement;
            if (panel == null) return;
            List<StageGroupInfoItem> lines = InfosField.GetValue(group) as List<StageGroupInfoItem>;
            int count = lines != null ? lines.Count : 0;
            GameObject host = panel.gameObject;
            // Only a panel laid out one below the other, as KSP's is, or already in
            // two columns here.
            bool known = columns.ContainsKey(host) || host.GetComponent<VerticalLayoutGroup>() != null;
            bool two = known && count > MostInOneColumn;
            float width = two ? 2f * column : column;
            if (!Mathf.Approximately((float)WidthField.GetValue(group), width))
            {
                WidthField.SetValue(group, width);
                if ((bool)ShownField.GetValue(group) && !(bool)SlidingField.GetValue(group)) panel.preferredWidth = width;
            }
            if (two) Grid(host, lines, width, count, panel);
            else List(host, lines, column);
        }

        private void Grid(GameObject host, List<StageGroupInfoItem> lines, float width, int count, LayoutElement panel)
        {
            GridLayoutGroup grid = host.GetComponent<GridLayoutGroup>();
            Column column;
            if (grid == null)
            {
                VerticalLayoutGroup list = host.GetComponent<VerticalLayoutGroup>();
                if (list == null) return;
                column = new Column
                {
                    Padding = new RectOffset(list.padding.left, list.padding.right, list.padding.top, list.padding.bottom),
                    Spacing = list.spacing, Alignment = list.childAlignment, ControlWidth = list.childControlWidth,
                    ControlHeight = list.childControlHeight, ExpandWidth = list.childForceExpandWidth,
                    ExpandHeight = list.childForceExpandHeight, LineHeight = LineHeight(lines),
                };
                if (column.LineHeight <= 0f) return;
                columns[host] = column;
                DestroyImmediate(list);
                grid = host.AddComponent<GridLayoutGroup>();
                grid.padding = new RectOffset(column.Padding.left, column.Padding.right, column.Padding.top, column.Padding.bottom);
                grid.childAlignment = column.Alignment;
                grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
                grid.startAxis = GridLayoutGroup.Axis.Vertical;
                grid.constraint = GridLayoutGroup.Constraint.FixedRowCount;
                grid.spacing = new Vector2(0f, column.Spacing);
                if (!noted)
                {
                    noted = true;
                    Debug.Log(Log.Tag + " Stage details in two columns from " + (MostInOneColumn + 1) + " values: lines "
                              + column.LineHeight.ToString("0.#") + " high, spacing " + column.Spacing.ToString("0.#") + ", padding "
                              + column.Padding + ".");
                }
            }
            else if (!columns.TryGetValue(host, out column))
            {
                return;
            }
            int rows = (count + 1) / 2;
            if (grid.constraintCount != rows) grid.constraintCount = rows;
            // As wide as the panel is now: KSP slides it in from nothing.
            float now = Mathf.Min(width, Mathf.Max(0f, panel.preferredWidth));
            Vector2 cell = new Vector2(Mathf.Max(0f, (now - column.Padding.left - column.Padding.right) / 2f), column.LineHeight);
            if (grid.cellSize != cell) grid.cellSize = cell;
            Size(lines, cell.x);
        }

        private void List(GameObject host, List<StageGroupInfoItem> lines, float width)
        {
            GridLayoutGroup grid = host.GetComponent<GridLayoutGroup>();
            Column column;
            if (grid == null || !columns.TryGetValue(host, out column)) return;
            DestroyImmediate(grid);
            VerticalLayoutGroup list = host.AddComponent<VerticalLayoutGroup>();
            list.padding = new RectOffset(column.Padding.left, column.Padding.right, column.Padding.top, column.Padding.bottom);
            list.spacing = column.Spacing;
            list.childAlignment = column.Alignment;
            list.childControlWidth = column.ControlWidth;
            list.childControlHeight = column.ControlHeight;
            list.childForceExpandWidth = column.ExpandWidth;
            list.childForceExpandHeight = column.ExpandHeight;
            columns.Remove(host);
            Size(lines, width);
        }

        // As tall as KSP's layout made a line.
        private static float LineHeight(List<StageGroupInfoItem> lines)
        {
            if (lines == null) return 0f;
            foreach (StageGroupInfoItem line in lines)
            {
                RectTransform rect = line != null ? line.transform as RectTransform : null;
                if (rect == null) continue;
                float height = rect.rect.height;
                if (height <= 0f) height = LayoutUtility.GetPreferredHeight(rect);
                if (height > 0f) return height;
            }
            return 0f;
        }

        // Each line's name and value in the width of its column, in the shares KSP
        // gives them (GameSettings.STAGE_GROUP_INFO_NAME_PERCENTAGE).
        private static void Size(List<StageGroupInfoItem> lines, float width)
        {
            if (lines == null) return;
            foreach (StageGroupInfoItem line in lines)
            {
                if (line == null || line.hLayout == null || line.infoNameLayout == null || line.infoValueLayout == null) continue;
                float inner = width - line.hLayout.padding.left - line.hLayout.padding.right;
                float name = inner * GameSettings.STAGE_GROUP_INFO_NAME_PERCENTAGE;
                if (!Mathf.Approximately(line.infoNameLayout.preferredWidth, name)) line.infoNameLayout.preferredWidth = name;
                if (!Mathf.Approximately(line.infoValueLayout.preferredWidth, inner - name)) line.infoValueLayout.preferredWidth = inner - name;
            }
        }

        // The delta-v tools' window left of the stages where they reach under it: its
        // right edge a gap left of the leftmost stage, shifted from where KSP placed it
        // (GenericAppFrame.Reposition), and back there once the stages leave room.
        private void KeepAppClear(StageManager manager)
        {
            DeltaVApp app = DeltaVApp.Instance;
            MonoBehaviour frame = app != null && AppFrameField != null ? AppFrameField.GetValue(app) as MonoBehaviour : null;
            RectTransform rect = frame != null ? frame.transform as RectTransform : null;
            if (rect == null || !rect.gameObject.activeInHierarchy)
            {
                movedFrame = null;
                return;
            }
            if (rect != movedFrame || rect.anchoredPosition != shiftedTo)
            {
                // KSP placed it anew.
                movedFrame = rect;
                placedAt = rect.anchoredPosition;
            }
            Vector3[] corners = new Vector3[4];
            float stagesLeft = float.MaxValue, stagesRight = float.MinValue, stagesBottom = float.MaxValue, stagesTop = float.MinValue;
            foreach (StageGroup group in manager.Stages)
            {
                RectTransform groupRect = group != null ? group.transform as RectTransform : null;
                if (groupRect == null || !groupRect.gameObject.activeInHierarchy) continue;
                groupRect.GetWorldCorners(corners);
                stagesLeft = Mathf.Min(stagesLeft, corners[0].x);
                stagesRight = Mathf.Max(stagesRight, corners[2].x);
                stagesBottom = Mathf.Min(stagesBottom, corners[0].y);
                stagesTop = Mathf.Max(stagesTop, corners[1].y);
            }
            Vector2 wanted = placedAt;
            if (stagesLeft < float.MaxValue && rect.parent != null)
            {
                rect.anchoredPosition = placedAt;
                rect.GetWorldCorners(corners);
                bool beside = corners[0].y < stagesTop && corners[1].y > stagesBottom && corners[0].x < stagesRight;
                float overlap = corners[2].x - (stagesLeft - Gap);
                float scale = rect.parent.lossyScale.x;
                if (beside && overlap > 0f && scale > 0f) wanted = placedAt - new Vector2(overlap / scale, 0f);
            }
            rect.anchoredPosition = wanted;
            shiftedTo = wanted;
        }
    }
}
