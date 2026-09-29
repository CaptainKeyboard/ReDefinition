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
        internal const float CellWidth = 88f;
        internal const float Gap = 1f;
        private const float PairHeight = 64f;
        private const float SingleHeight = 42f;

        // Which profile of FlightProfiles.Current this tab shows; copied along
        // as KSP instantiates the tab.
        public int Profile;

        private sealed class Cell
        {
            internal Readout Readout;
            internal RectTransform Block;
            internal TextMeshProUGUI Value;
            internal TextMeshProUGUI Second;
            internal bool Shown;
        }

        private readonly List<Cell> cells = new List<Cell>();
        private int columns;

        public override void SetInitialValues()
        {
            Build();
            UpdateUIElements();
        }

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
            List<FlightProfile> profiles = FlightProfiles.Current;
            GameObject template = FlightPanel.BlockTemplate;
            if (template == null || Profile < 0 || Profile >= profiles.Count) return;
            foreach (string key in profiles[Profile].Keys)
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
                Anchor(cell.Block, readout.Paired ? PairHeight : SingleHeight);
                cells.Add(cell);
            }
            Layout(true);
        }

        private static void Anchor(RectTransform block, float height)
        {
            block.anchorMin = block.anchorMax = block.pivot = new Vector2(0f, 1f);
            block.sizeDelta = new Vector2(CellWidth, height);
        }

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

        private void OnEnable()
        {
            if (cells.Count > 0) FlightPanel.Resize(this, Width);
        }
    }
}
