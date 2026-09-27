using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ReDefinition.Engineer
{
    // The engineer window in the VAB and the SPH (docs/player/engineer.md): the
    // vessel's stages as a table, as Kerbal Engineer Redux's build window shows
    // them, beside the parts list while the vessel is built. It stands at the
    // bottom of the screen until the player drags it elsewhere, and folds to one
    // line. Built from KSP's dialog elements on the skin's window background, so
    // a UI theme reaches it as it reaches KSP's dialogs.
    internal sealed class EngineerWindow
    {
        private const int MaxStages = 40;
        private const float RowHeight = 18f;
        private const float FullWidth = 660f;
        private const float CompactWidth = 330f;
        private const float FoldedWidth = 300f;
        private const float TableHeight = 260f;
        private const string Good = "#8fdc7a";
        private const string Warning = "#f18a24";
        private const string Dim = "#9a9a9a";

        private readonly EditorEngineer owner;
        private readonly WindowState view = WindowState.Load();
        private GameObject host;
        private DialogGUIBase root;
        private UISkinDef skin;
        private bool rebuild = true;
        private int selectedStage = -1;
        private readonly PartHighlight highlight = new PartHighlight();
        private PartCard card;
        private List<StageRow> shown = new List<StageRow>();
        // The rows' texts, written again only when the readout, the selection
        // or the form of the table changes.
        private readonly string[] rowTexts = new string[MaxStages];
        private EditorReadout lastReadout;
        private int lastSelected = -2;
        private bool lastCompact;
        private bool lastAllStages;
        private readonly AfterCreate after = new AfterCreate();

        private EngineerWindow(EditorEngineer owner)
        {
            this.owner = owner;
        }

        internal static EngineerWindow Create(EditorEngineer owner, RectTransform parent)
        {
            if (parent == null) return null;
            EngineerWindow window = new EngineerWindow(owner);
            window.skin = UISkinManager.GetSkin("MiniSettingsSkin") ?? HighLogic.UISkin;
            window.host = new GameObject("ReDefinitionEngineerWindow", typeof(RectTransform), typeof(Image));
            RectTransform rect = (RectTransform)window.host.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = window.view.Position;
            Image background = window.host.GetComponent<Image>();
            UIStyleState style = window.skin.window != null ? window.skin.window.normal : null;
            if (style != null && style.background != null)
            {
                background.sprite = style.background;
                background.type = Image.Type.Sliced;
            }
            else
            {
                background.color = new Color(0.12f, 0.13f, 0.14f, 0.96f);
            }
            WindowDrag drag = window.host.AddComponent<WindowDrag>();
            drag.Moved = window.Moved;
            drag.Ended = window.view.Save;
            ContentSizeFitter fitter = window.host.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            VerticalLayoutGroup layout = window.host.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 6, 8);
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;
            window.card = PartCard.Create(parent, window.skin);
            return window;
        }

        internal void Destroy()
        {
            highlight.Clear();
            if (card != null) card.Destroy();
            if (host != null) UnityEngine.Object.Destroy(host);
            host = null;
            view.Save();
        }

        internal static bool Wanted(OwnSettings settings)
        {
            return settings == null || settings.EngineerWindow;
        }

        // `visible`: whether the window belongs on the screen now, the parts
        // list being shown; `plannerOpen`: the planner colours the parts
        // itself, so the window marks none.
        internal void Update(OwnSettings settings, bool visible, bool plannerOpen)
        {
            if (host == null) return;
            bool wanted = visible && Wanted(settings);
            if (host.activeSelf != wanted) host.SetActive(wanted);
            if (!wanted)
            {
                ClearMark();
                card.Hide();
                return;
            }
            if (rebuild) Build();
            if (plannerOpen || view.Folded) selectedStage = -1;
            EditorReadout readout = owner.Readout;
            if (readout == null) return;
            bool allStages = settings != null && settings.EngineerAllStages;
            if (readout != lastReadout || selectedStage != lastSelected || view.Compact != lastCompact || allStages != lastAllStages)
            {
                lastReadout = readout;
                lastSelected = selectedStage;
                lastCompact = view.Compact;
                lastAllStages = allStages;
                shown = StageTable.Shown(readout.Stages, allStages);
                for (int i = 0; i < MaxStages; i++) rowTexts[i] = StageText(i, readout);
                if (!plannerOpen) MarkSelected(readout);
            }
            Clamp();
            if (root != null) root.Update();
            card.Update(!view.Folded && (settings == null || settings.EngineerPartDetails));
        }

        // The planner colours the parts itself while it stands open.
        internal void ClearMark()
        {
            selectedStage = -1;
            lastSelected = -2;
            highlight.Clear();
        }

        internal void ToggleFolded()
        {
            view.Folded = !view.Folded;
            view.Save();
            rebuild = true;
        }

        private void Moved(Vector2 delta)
        {
            RectTransform rect = (RectTransform)host.transform;
            rect.anchoredPosition = Inside(rect.anchoredPosition + delta);
            view.Position = rect.anchoredPosition;
        }

        // On the screen, whatever the window's size, the screen's or the UI
        // scale was when its place was saved.
        private void Clamp()
        {
            RectTransform rect = (RectTransform)host.transform;
            Vector2 inside = Inside(rect.anchoredPosition);
            if (inside != rect.anchoredPosition) rect.anchoredPosition = inside;
        }

        private Vector2 Inside(Vector2 position)
        {
            RectTransform rect = (RectTransform)host.transform;
            RectTransform parent = rect.parent as RectTransform;
            if (parent == null) return position;
            float halfRoom = Mathf.Max(0f, (parent.rect.width - rect.rect.width) / 2f);
            position.x = Mathf.Clamp(position.x, -halfRoom, halfRoom);
            position.y = Mathf.Clamp(position.y, 0f, Mathf.Max(0f, parent.rect.height - rect.rect.height));
            return position;
        }

        // ------------------------------------------------------------------ rows

        private void Build()
        {
            rebuild = false;
            if (root != null && root.uiItem != null) UnityEngine.Object.Destroy(root.uiItem);
            float width = view.Folded ? FoldedWidth : view.Compact ? CompactWidth : FullWidth;
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            if (view.Folded)
            {
                rows.Add(new DialogGUIHorizontalLayout(width, RowHeight + 4f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                    new DialogGUILabel(FoldedText, width - 36f, RowHeight),
                    TextButton(() => "+", ToggleFolded, 28f, "Open")));
            }
            else
            {
                rows.Add(Toolbar(width));
                rows.Add(AltitudeRow(width));
                rows.Add(new DialogGUILabel(TotalsText, width, RowHeight));
                rows.Add(new DialogGUILabel(() => "<color=" + Dim + ">" + Header() + "</color>", width - 18f, RowHeight));
                List<DialogGUIBase> table = new List<DialogGUIBase>
                {
                    new DialogGUIContentSizer(ContentSizeFitter.FitMode.Unconstrained, ContentSizeFitter.FitMode.PreferredSize, true),
                };
                for (int i = 0; i < MaxStages; i++) table.Add(StageRowFor(i, width - 18f));
                DialogGUILabel empty = new DialogGUILabel(() => "<color=" + Dim + ">"
                                                                + ((owner.Readout ?? new EditorReadout()).HasShip ? "No stage burns anything yet."
                                                                    : "Place a part to see the stages.") + "</color>", width - 18f, RowHeight);
                empty.OptionEnabledCondition = () => shown.Count == 0;
                table.Add(empty);
                rows.Add(new DialogGUIScrollList(new Vector2(width, TableHeight), false, true,
                    new DialogGUIVerticalLayout(width - 18f, -1f, 0f, new RectOffset(), TextAnchor.UpperLeft, table.ToArray())));
                DialogGUILabel rcs = new DialogGUILabel(RcsText, width, RowHeight);
                rcs.OptionEnabledCondition = () => view.Rcs && !view.Compact;
                rows.Add(rcs);
                DialogGUILabel resources = new DialogGUILabel(ResourceText, true, false);
                DialogGUIHorizontalLayout resourceRow = new DialogGUIHorizontalLayout(width, -1f, 0f, new RectOffset(), TextAnchor.UpperLeft,
                    resources);
                resourceRow.OptionEnabledCondition = () => view.Resources && !view.Compact;
                rows.Add(resourceRow);
            }
            root = new DialogGUIVerticalLayout(width, -1f, 3f, new RectOffset(), TextAnchor.UpperLeft, rows.ToArray());
            Stack<Transform> layouts = new Stack<Transform>();
            layouts.Push(host.transform);
            root.Create(ref layouts, skin);
            after.Run();
            lastSelected = -2;
        }

        private DialogGUIBase Toolbar(float width)
        {
            List<DialogGUIBase> items = new List<DialogGUIBase>
            {
                TextButton(() => "<b>" + EngineerSituation.BodyName() + "</b>", EngineerSituation.PickBody, 86f, "Body"),
                Choice("Sea level", DeltaVSituationOptions.SeaLevel, 66f),
                Choice("Altitude", DeltaVSituationOptions.Altitude, 62f),
                Choice("Vacuum", DeltaVSituationOptions.Vaccum, 60f),
            };
            if (!view.Compact)
            {
                items.Add(Switch("RCS", () => view.Rcs, () => { view.Rcs = !view.Rcs; view.Save(); }, 44f));
                items.Add(Switch("Resources", () => view.Resources, () => { view.Resources = !view.Resources; view.Save(); }, 80f));
            }
            items.Add(new DialogGUIFlexibleSpace());
            items.Add(TextButton(() => view.Compact ? "Full" : "Compact", () =>
            {
                view.Compact = !view.Compact;
                view.Save();
                rebuild = true;
            }, 64f, null));
            items.Add(TextButton(() => "-", ToggleFolded, 24f, "Fold"));
            return new DialogGUIHorizontalLayout(width, RowHeight + 6f, 3f, new RectOffset(), TextAnchor.MiddleLeft, items.ToArray());
        }

        // One choice of three, the chosen one lit: a button of KSP's skin with
        // its text in the highlight colour.
        private DialogGUIButton Choice(string label, DeltaVSituationOptions situation, float width)
        {
            DialogGUIButton button = TextButton(() => EngineerSituation.Situation() == situation
                ? "<b><color=" + Good + ">" + label + "</color></b>"
                : "<color=" + Dim + ">" + label + "</color>", () => EngineerSituation.SetSituation(situation), width, null);
            if (situation != DeltaVSituationOptions.Vaccum) button.OptionInteractableCondition = EngineerSituation.HasAtmosphere;
            return button;
        }

        private DialogGUIButton Switch(string label, Func<bool> on, Callback toggle, float width)
        {
            return TextButton(() => on() ? "<b><color=" + Good + ">" + label + "</color></b>" : "<color=" + Dim + ">" + label + "</color>",
                toggle, width, null);
        }

        private static DialogGUIButton TextButton(Func<string> text, Callback click, float width, string tip)
        {
            DialogGUIButton button = new DialogGUIButton(text, click, width, RowHeight + 4f, false);
            if (tip != null) button.tooltipText = tip;
            return button;
        }

        private DialogGUIBase AltitudeRow(float width)
        {
            DialogGUISlider slider = new DialogGUISlider(
                () => (float)(EngineerSituation.Altitude() / Math.Max(1.0, EngineerSituation.MaxAltitude())), 0f, 1f, false,
                width - 100f, RowHeight, value => EngineerSituation.SetAltitude(value * EngineerSituation.MaxAltitude()));
            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, RowHeight + 2f, 6f, new RectOffset(),
                TextAnchor.MiddleLeft, slider, new DialogGUILabel(() => EngineerFormat.Distance(EngineerSituation.Altitude()), 90f));
            row.OptionEnabledCondition = () => EngineerSituation.Situation() == DeltaVSituationOptions.Altitude;
            return row;
        }

        // A label as the header is, so the columns line up, that a click on it
        // selects.
        private DialogGUIBase StageRowFor(int index, float width)
        {
            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel(() => rowTexts[index] ?? "", width, RowHeight));
            row.OptionEnabledCondition = () => index < shown.Count;
            PlannerWidgets.Clickable(row, () => Select(index), after);
            return row;
        }

        private void Select(int index)
        {
            if (index >= shown.Count) return;
            int number = shown[index].Number;
            selectedStage = selectedStage == number ? -1 : number;
        }

        // The selected stage's engines and the tanks it burns, green on the
        // vessel.
        private void MarkSelected(EditorReadout readout)
        {
            Dictionary<Part, Color> parts = new Dictionary<Part, Color>();
            List<Part> list;
            if (selectedStage >= 0 && readout.BurningParts.TryGetValue(selectedStage, out list))
                foreach (Part part in list)
                    if (part != null) parts[part] = new Color(0.35f, 1f, 0.35f);
            highlight.Show(parts);
        }

        // ------------------------------------------------------------------ text

        private string Header()
        {
            return view.Compact
                ? Columns(new[] { "Stage", "Δv", "TWR", "Burn" })
                : Columns(new[] { "Stage", "Parts", "Cost", "Mass", "Isp", "Thrust", "Torque", "TWR", "Δv", "Burn" });
        }

        // KER's columns, and the four of its compact mode (KerbalEngineer's
        // BuildAdvanced, read).
        private static readonly int[] FullAt = { 0, 8, 17, 28, 40, 49, 60, 70, 80, 91 };
        private static readonly int[] CompactAt = { 0, 22, 52, 74 };

        private string Columns(string[] cells)
        {
            int[] at = view.Compact ? CompactAt : FullAt;
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < cells.Length && i < at.Length; i++)
            {
                if (i > 0) text.Append("<pos=").Append(at[i].ToString(CultureInfo.InvariantCulture)).Append("%>");
                text.Append(cells[i]);
            }
            return text.ToString();
        }

        private string StageText(int index, EditorReadout readout)
        {
            if (index >= shown.Count) return "";
            StageRow row = shown[index];
            StageRow first = StageTable.FirstBurning(readout.Stages);
            string twr = row.HasEngines ? EngineerFormat.Ratio(row.Twr) : "--";
            if (row == first && row.Twr < 1.0) twr = "<color=" + Warning + ">" + twr + "</color>";
            string name = selectedStage == row.Number ? "<b><color=" + Good + ">S" + row.Number + "</color></b>" : "<b>S" + row.Number + "</b>";
            string burn = row.HasEngines ? EngineerFormat.Time(row.BurnTime) : "--";
            if (view.Compact) return Columns(new[] { name, EngineerFormat.Speed(row.DeltaV), twr, burn });
            string twrBoth = row.HasEngines ? twr + " <color=" + Dim + ">(" + EngineerFormat.Ratio(row.TwrMax) + ")</color>" : "--";
            return Columns(new[]
            {
                name,
                EngineerFormat.Count(row.AttachedParts) + " <color=" + Dim + ">(" + EngineerFormat.Count(row.OwnParts) + ")</color>",
                EngineerFormat.Cost(row.OwnCost),
                EngineerFormat.Mass(row.StartMass),
                row.HasEngines ? EngineerFormat.Isp(row.Isp) : "--",
                row.HasEngines ? EngineerFormat.Force(row.Thrust) : "--",
                row.HasEngines ? EngineerFormat.Torque(row.ThrustTorque) : "--",
                twrBoth,
                EngineerFormat.Speed(row.DeltaV),
                burn,
            });
        }

        private string TotalsText()
        {
            EditorReadout readout = owner.Readout ?? new EditorReadout();
            StageRow first = StageTable.FirstBurning(readout.Stages);
            string twr = first == null ? "--" : EngineerFormat.Ratio(first.Twr);
            if (first != null && first.Twr < 1.0) twr = "<color=" + Warning + ">" + twr + "</color>";
            string deltaV = readout.Simulated ? EngineerFormat.Speed(readout.TotalDeltaV) : "--";
            if (view.Compact) return "<b>Δv " + deltaV + "</b>    TWR " + twr + "    " + EngineerFormat.Mass(readout.WetMass);
            return "<b>Δv " + deltaV + "</b>    TWR " + twr + "    " + EngineerFormat.Mass(readout.WetMass) + " <color=" + Dim
                   + ">(dry " + EngineerFormat.Mass(readout.DryMass) + ")</color>    " + EngineerFormat.Cost(readout.Cost) + "    "
                   + EngineerFormat.Count(readout.Parts) + " parts";
        }

        private string FoldedText()
        {
            EditorReadout readout = owner.Readout ?? new EditorReadout();
            StageRow first = StageTable.FirstBurning(readout.Stages);
            string twr = first == null ? "--" : EngineerFormat.Ratio(first.Twr);
            if (first != null && first.Twr < 1.0) twr = "<color=" + Warning + ">" + twr + "</color>";
            return "<b>Δv " + (readout.Simulated ? EngineerFormat.Speed(readout.TotalDeltaV) : "--") + "</b>    TWR " + twr;
        }

        private string RcsText()
        {
            EditorReadout.RcsLine rcs = (owner.Readout ?? new EditorReadout()).Rcs;
            if (rcs == null) return "<color=" + Dim + ">RCS: none on the last stage</color>";
            return "RCS, last stage    Δv " + EngineerFormat.Speed(rcs.DeltaV) + "    Isp " + EngineerFormat.Isp(rcs.Isp)
                   + "    " + EngineerFormat.Force(rcs.Thrust) + "    TWR " + EngineerFormat.Ratio(rcs.Twr) + "    "
                   + EngineerFormat.Time(rcs.BurnTime);
        }

        private string ResourceText()
        {
            List<EditorReadout.ResourceLine> resources = (owner.Readout ?? new EditorReadout()).Resources;
            if (resources.Count == 0) return "<color=" + Dim + ">No resources on board</color>";
            StringBuilder text = new StringBuilder();
            foreach (EditorReadout.ResourceLine line in resources)
            {
                if (text.Length > 0) text.Append('\n');
                text.Append(line.Name).Append("<pos=34%>").Append(EngineerFormat.Amount(line.Amount)).Append(" / ")
                    .Append(EngineerFormat.Amount(line.MaxAmount));
                if (line.Mass > 0.0) text.Append("<pos=70%><color=" + Dim + ">").Append(EngineerFormat.Mass(line.Mass)).Append("</color>");
            }
            return text.ToString();
        }

        // ------------------------------------------------------------ the state

        // Where the window stands and how it is shown: kept in
        // GameData/ReDefinition/PluginData/engineer.cfg.
        private sealed class WindowState
        {
            private const string FileName = "engineer.cfg";

            public Vector2 Position = new Vector2(0f, 64f);
            public bool Compact;
            public bool Folded;
            public bool Rcs;
            public bool Resources;

            internal static WindowState Load()
            {
                WindowState state = new WindowState();
                try
                {
                    string path = PluginData.Path(FileName);
                    ConfigNode root = File.Exists(path) ? ConfigNode.Load(path) : null;
                    ConfigNode node = root != null ? root.GetNode("ENGINEER") : null;
                    if (node == null) return state;
                    float x, y;
                    if (float.TryParse(node.GetValue("x"), NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                        && float.TryParse(node.GetValue("y"), NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                        state.Position = new Vector2(x, y);
                    bool.TryParse(node.GetValue("compact"), out state.Compact);
                    bool.TryParse(node.GetValue("folded"), out state.Folded);
                    bool.TryParse(node.GetValue("rcs"), out state.Rcs);
                    bool.TryParse(node.GetValue("resources"), out state.Resources);
                }
                catch (Exception e)
                {
                    Debug.LogWarning(Log.Tag + " The engineer window's place could not be read: " + e.Message);
                }
                return state;
            }

            internal void Save()
            {
                try
                {
                    ConfigNode root = new ConfigNode();
                    ConfigNode node = root.AddNode("ENGINEER");
                    node.AddValue("x", Position.x.ToString("0", CultureInfo.InvariantCulture));
                    node.AddValue("y", Position.y.ToString("0", CultureInfo.InvariantCulture));
                    node.AddValue("compact", Compact);
                    node.AddValue("folded", Folded);
                    node.AddValue("rcs", Rcs);
                    node.AddValue("resources", Resources);
                    string path = PluginData.Path(FileName);
                    string directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
                    root.Save(path);
                }
                catch (Exception e)
                {
                    Debug.LogWarning(Log.Tag + " The engineer window's place could not be saved: " + e.Message);
                }
            }
        }
    }

    // Drags a window by any place that is not a control of its own: Unity hands
    // the drag to the first handler above the pointer, and a button takes no
    // drags, a scroll list does.
    internal sealed class WindowDrag : MonoBehaviour, IDragHandler, IEndDragHandler
    {
        internal Action<Vector2> Moved;
        internal Action Ended;

        public void OnDrag(PointerEventData data)
        {
            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas != null) canvas = canvas.rootCanvas;
            float scale = canvas != null && canvas.scaleFactor > 0f ? canvas.scaleFactor : 1f;
            if (Moved != null) Moved(data.delta / scale);
        }

        public void OnEndDrag(PointerEventData data)
        {
            if (Ended != null) Ended();
        }
    }
}
