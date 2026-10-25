using System.Collections.Generic;
using ReDefinition.Flight.Readouts;
using TMPro;
using UnityEngine;

namespace ReDefinition.Flight.Panel
{
    // One profile of the flight panel, as a tab of KSP's maneuver panel: its
    // readouts in KSP's own blocks, copies of the orbit tab's apoapsis block
    // (ResultApoapsis: 88 by 64, KSP's emboss frame, a title and two value fields;
    // sharedassets7). A readout with two values takes the whole block; one with a
    // single value the block without its second field, 42 high. They fill columns
    // top down, as many as the tab's height takes, and the columns run left to
    // right; the panel grows to the right for more than two (FlightPanel.Resize).
    // Readouts that do not apply now (a target's, without one) leave their place.
    // KSP calls UpdateUIElements while its panel is open, every
    // updateCooldownSeconds.
    public class ProfileTab : ManeuverNodeEditorTab
    {
        // In KSP's UI units: a block's width, the gap between columns, and the height of a block with two values and
        // with one.
        internal const float CellWidth = 88f;
        internal const float Gap = 1f;
        private const float PairHeight = 64f;
        private const float SingleHeight = 42f;

        // Which profile of FlightProfiles.Current this tab shows as KSP builds it;
        // copied along as KSP instantiates the tab. From then on the tab holds the
        // profile itself (Bound), so that deleting or adding one moves no tab to
        // another's readouts; a tab whose profile is gone stays empty.
        public int Profile;

        // The profile the tab shows, once bound (SetInitialValues).
        internal FlightProfile Bound;

        // One readout's block on the tab.
        private sealed class Cell
        {
            // The readout; its block; its value field and its second one (null for none); whether it shows now.
            internal Readout Readout;
            internal RectTransform Block;
            internal TextMeshProUGUI Value;
            internal TextMeshProUGUI Second;
            internal bool Shown;
        }

        // The tab's blocks in the profile's order, and how many columns they fill.
        private readonly List<Cell> cells = new List<Cell>();
        private int columns;

        // KSP sets the tab up: bound to its profile, its blocks built and filled.
        public override void SetInitialValues()
        {
            if (Bound == null && Profile >= 0 && Profile < FlightProfiles.Current.Count) Bound = FlightProfiles.Current[Profile];
            Build();
            UpdateUIElements();
        }

        // The tab can always be picked.
        public override bool IsTabInteractable()
        {
            return true;
        }

        // The blocks anew, for the profile as it stands.
        internal void Build()
        {
            foreach (Cell cell in cells)
                if (cell.Block != null) Destroy(cell.Block.gameObject);
            cells.Clear();
            GameObject template = FlightPanel.BlockTemplate;
            if (template == null || Bound == null || !FlightProfiles.Current.Contains(Bound))
            {
                Layout(true);
                return;
            }
            foreach (string key in Bound.Keys)
            {
                Readout readout = ReadoutCatalog.Find(key);
                if (readout == null) continue;
                GameObject block = Instantiate(template, transform, false);
                block.name = "ReDefinitionReadout_" + key;
                block.SetActive(true);
                Cell cell = new Cell { Readout = readout, Block = (RectTransform)block.transform };
                TextMeshProUGUI title = null;
                foreach (TextMeshProUGUI text in block.GetComponentsInChildren<TextMeshProUGUI>(true))
                {
                    if (text.transform.parent == block.transform) title = text;
                    else if (text.transform.parent.name == "TextBack") cell.Value = text;
                    else if (text.transform.parent.name == "TextBackT") cell.Second = text;
                }
                if (title != null) title.text = readout.Title;
                if (!readout.Paired)
                {
                    Transform second = block.transform.Find("TextBackT");
                    if (second != null) Destroy(second.gameObject);
                    cell.Second = null;
                }
                foreach (TextMeshProUGUI text in new[] { cell.Value, cell.Second })
                {
                    if (text == null) continue;
                    // Longer values, such as a biome's name, shrink to the field.
                    text.enableAutoSizing = true;
                    text.fontSizeMax = text.fontSize;
                    text.fontSizeMin = text.fontSize * 0.65f;
                    text.text = "";
                }
                if (cell.Value == null)
                {
                    Destroy(block);
                    continue;
                }
                Anchor(cell.Block, readout.Paired ? PairHeight : SingleHeight);
                cells.Add(cell);
            }
            Layout(true);
        }

        // The block hangs from its top left, CellWidth wide and `height` high.
        private static void Anchor(RectTransform block, float height)
        {
            block.anchorMin = block.anchorMax = block.pivot = new Vector2(0f, 1f);
            block.sizeDelta = new Vector2(CellWidth, height);
        }

        // Every block's values anew, a block hidden where its readout does not apply now; laid out again where that changed.
        public override void UpdateUIElements()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) return;
            ReadoutFrame frame = new ReadoutFrame(vessel);
            if (!frame.Ready) return;
            bool changed = false;
            foreach (Cell cell in cells)
            {
                bool shown;
                try
                {
                    shown = cell.Readout.Applies == null || cell.Readout.Applies(frame);
                }
                catch (System.Exception)
                {
                    shown = false;
                }
                if (shown != cell.Shown)
                {
                    cell.Shown = shown;
                    changed = true;
                }
                if (!shown) continue;
                cell.Value.text = Text(cell.Readout.Value, frame);
                if (cell.Second != null) cell.Second.text = Text(cell.Readout.Second, frame);
            }
            if (changed) Layout(false);
        }

        // The readout's text; "-" where reading it fails.
        private static string Text(System.Func<ReadoutFrame, string> value, ReadoutFrame frame)
        {
            try
            {
                return value(frame);
            }
            catch (System.Exception)
            {
                return "-";
            }
        }

        // The shown blocks top down in columns as high as the tab, left to right;
        // the panel as wide as they need.
        private void Layout(bool first)
        {
            float height = ((RectTransform)transform).rect.height;
            if (height <= 1f) height = PairHeight + 25f;
            float x = 0f, y = 0f;
            columns = 0;
            foreach (Cell cell in cells)
            {
                if (first) cell.Shown = cell.Readout.Applies == null;
                cell.Block.gameObject.SetActive(cell.Shown);
                if (!cell.Shown) continue;
                float blockHeight = cell.Block.sizeDelta.y;
                if (columns == 0) columns = 1;
                else if (y + blockHeight > height + 0.5f)
                {
                    x += CellWidth + Gap;
                    y = 0f;
                    columns++;
                }
                cell.Block.anchoredPosition = new Vector2(x, -y);
                y += blockHeight;
            }
            FlightPanel.Resize(this, Width);
        }

        // How wide the tab's shown blocks stand.
        internal float Width
        {
            get { return columns <= 0 ? 0f : columns * CellWidth + (columns - 1) * Gap; }
        }

        // Shown again: the panel takes the tab's width.
        private void OnEnable()
        {
            if (cells.Count > 0) FlightPanel.Resize(this, Width);
        }
    }
}
