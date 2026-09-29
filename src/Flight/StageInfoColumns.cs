using System;
using System.Collections.Generic;
using System.Reflection;
using KSP.UI.Screens;
using ReDefinition.Core;
using ReDefinition.Settings;
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
    // While a stage's details are out, its part icons stand in two columns as
    // well (StageGroup.IconsUIList, a vertical layout, swapped for a grid of two
    // columns; its column, StageIcons, and the list as wide as two icons). While
    // anything is dragged in the stack (StageGroup.IsBeingDragged, StageIcon.
    // underDrag) every stage keeps KSP's one column: KSP works out where a dragged
    // icon or stage lands from the heights of the icons one below the other
    // (StageGroup.GetStageGroupIndex, StageManager.GetStageGroupInsertionIndex).
    //
    // The stage list clips what it shows to the stack's own width (its ScrollRect's
    // Mask; the stack root 250 wide in the editor, 265 in flight, read from the
    // prefabs), which a stage with details in two columns outgrows: the root is
    // widened to a stage's full width while details are out, and back to KSP's once
    // they are in. And the stages' delta-v, which KSP writes under each stage's
    // number anyway, is no value: its line (DeltaVAppValues.infoLines, "DELTAV") is
    // taken out as the game starts (StageDetailsSetup).
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
            HarmonyHooks.Safely("deltav-tools", "The delta-v tools' button shows KSP's window as before", DeltaVTools.Install);
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
        // The add and delete buttons beside a stage (StageManager.plusMinusButtonWidth).
        private const float Buttons = 25f;
        // KSP's icons column (StageGroup's StageIcons, LayoutElement width 50).
        internal const float IconsColumn = 50f;
        private RectTransform stack;
        private float stackWidth = -1f;

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

        // An icon list's own vertical layout and widths, to be put back.
        private sealed class IconColumn
        {
            public RectOffset Padding;
            public float Spacing;
            public TextAnchor Alignment;
            public bool ControlWidth, ControlHeight, ExpandWidth, ExpandHeight;
            public float ListWidth, ColumnWidth;
        }

        private readonly Dictionary<GameObject, IconColumn> iconColumns = new Dictionary<GameObject, IconColumn>();
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
                bool dragging = Dragging(manager);
                foreach (StageGroup group in manager.Stages)
                    if (group != null) Arrange(group, column, dragging);
                KeepStackWide(manager);
            }
            catch (Exception e)
            {
                failed = true;
                CompatibilityLog.Warn("stage-info-columns", "The stage details stay in one column (" + CompatibilityLog.Reason(e) + ").");
            }
        }

        private void Arrange(StageGroup group, float column, bool dragging)
        {
            LayoutElement panel = PanelField.GetValue(group) as LayoutElement;
            if (panel == null) return;
            Icons(group, panel.gameObject.activeInHierarchy && !dragging);
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

        // Something in the stack is dragged: a stage or an icon.
        private static bool Dragging(StageManager manager)
        {
            foreach (StageGroup group in manager.Stages)
            {
                if (group == null) continue;
                if (group.IsBeingDragged) return true;
                foreach (StageIcon icon in group.Icons)
                    if (icon != null && icon.underDrag) return true;
            }
            return false;
        }

        // The width of a stage's icons column now, KSP's or two icons wide.
        internal static float IconsWidth(StageGroup group)
        {
            LayoutElement column = ColumnOf(group);
            return column != null && column.preferredWidth > 0f ? column.preferredWidth : IconsColumn;
        }

        private static LayoutElement ColumnOf(StageGroup group)
        {
            Transform list = group != null ? group.IconsUIList : null;
            Transform column = list != null && list.parent != null ? list.parent.parent : null;
            return column != null ? column.GetComponent<LayoutElement>() : null;
        }

        // The part icons in two columns (`two`) or in KSP's one.
        private void Icons(StageGroup group, bool two)
        {
            RectTransform list = group.IconsUIList;
            if (list == null) return;
            GameObject host = list.gameObject;
            LayoutElement listLayout = host.GetComponent<LayoutElement>();
            LayoutElement columnLayout = ColumnOf(group);
            GridLayoutGroup grid = host.GetComponent<GridLayoutGroup>();
            IconColumn saved;
            if (!two)
            {
                if (grid == null || !iconColumns.TryGetValue(host, out saved)) return;
                DestroyImmediate(grid);
                VerticalLayoutGroup vertical = host.AddComponent<VerticalLayoutGroup>();
                vertical.padding = new RectOffset(saved.Padding.left, saved.Padding.right, saved.Padding.top, saved.Padding.bottom);
                vertical.spacing = saved.Spacing;
                vertical.childAlignment = saved.Alignment;
                vertical.childControlWidth = saved.ControlWidth;
                vertical.childControlHeight = saved.ControlHeight;
                vertical.childForceExpandWidth = saved.ExpandWidth;
                vertical.childForceExpandHeight = saved.ExpandHeight;
                if (listLayout != null) listLayout.preferredWidth = saved.ListWidth;
                if (columnLayout != null) columnLayout.preferredWidth = saved.ColumnWidth;
                iconColumns.Remove(host);
                return;
            }
            if (grid != null) return;
            VerticalLayoutGroup list1 = host.GetComponent<VerticalLayoutGroup>();
            Vector2 cell = IconSize(list);
            if (list1 == null || cell.x <= 0f || cell.y <= 0f) return;
            saved = new IconColumn
            {
                Padding = new RectOffset(list1.padding.left, list1.padding.right, list1.padding.top, list1.padding.bottom),
                Spacing = list1.spacing, Alignment = list1.childAlignment, ControlWidth = list1.childControlWidth,
                ControlHeight = list1.childControlHeight, ExpandWidth = list1.childForceExpandWidth,
                ExpandHeight = list1.childForceExpandHeight, ListWidth = listLayout != null ? listLayout.preferredWidth : -1f,
                ColumnWidth = columnLayout != null ? columnLayout.preferredWidth : -1f,
            };
            iconColumns[host] = saved;
            DestroyImmediate(list1);
            grid = host.AddComponent<GridLayoutGroup>();
            grid.padding = new RectOffset(saved.Padding.left, saved.Padding.right, saved.Padding.top, saved.Padding.bottom);
            grid.childAlignment = saved.Alignment;
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 2;
            grid.spacing = new Vector2(0f, saved.Spacing);
            grid.cellSize = cell;
            float width = Mathf.Max(IconsColumn, saved.Padding.left + saved.Padding.right + 2f * cell.x);
            if (listLayout != null) listLayout.preferredWidth = width;
            if (columnLayout != null) columnLayout.preferredWidth = width;
        }

        // As large as KSP's layout made an icon.
        private static Vector2 IconSize(RectTransform list)
        {
            for (int i = 0; i < list.childCount; i++)
            {
                RectTransform child = list.GetChild(i) as RectTransform;
                if (child == null || !child.gameObject.activeSelf || child.GetComponent<StageIcon>() == null) continue;
                Vector2 size = child.rect.size;
                if (size.x > 0f && size.y > 0f) return size;
            }
            return Vector2.zero;
        }

        // The stack at least as wide as its widest stage with its details out, so that
        // the list's mask clips nothing of it; KSP's own width otherwise.
        private void KeepStackWide(StageManager manager)
        {
            RectTransform root = manager.transform as RectTransform;
            if (root == null) return;
            if (root != stack)
            {
                stack = root;
                stackWidth = root.sizeDelta.x;
            }
            float needed = 0f;
            foreach (StageGroup group in manager.Stages)
            {
                LayoutElement panel = group != null ? PanelField.GetValue(group) as LayoutElement : null;
                if (panel == null || !panel.gameObject.activeInHierarchy) continue;
                needed = Mathf.Max(needed, (float)WidthField.GetValue(group) + IconsWidth(group) + Buttons);
            }
            float width = Mathf.Max(stackWidth, needed);
            if (!Mathf.Approximately(root.sizeDelta.x, width)) root.sizeDelta = new Vector2(width, root.sizeDelta.y);
        }
    }
}
