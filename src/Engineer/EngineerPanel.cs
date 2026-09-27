using System.Collections.Generic;
using System.Text;
using System;
using KSP.UI.Screens;
using ReDefinition.Window;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Engineer
{
    // The engineer view: KSP's dialog elements built into the editor's parts
    // panel (EditorPanels.partsEditor), over the parts list, so that it slides in
    // and out with the panel, scales with KSP's UI and takes a UI theme as KSP's
    // dialogs do. Built as a PopupDialog builds its content (DialogGUIBase.Create
    // with the panel as the layout's parent, decompiled) and updated here, as a
    // PopupDialog updates its dialog every frame.
    //
    // Two tabs. Engineer, from the top: the situation the numbers are for, the
    // vessel as a whole, the stages, RCS, the resources, and the part under the
    // mouse. Planner: a route and the fuel for it (PlannerView). The situation is
    // KSP's delta-v app's own (DeltaVGlobals.DeltaVAppValues), and KSP's staging
    // stack follows it. KSP's delta-v app reads it only as it opens
    // (DeltaVAppSituation.Awake, decompiled): while it stands open it keeps
    // showing its own, and a change made in it writes all of its own back.
    internal sealed class EngineerPanel
    {
        private const int MaxStages = 80;
        private const float PartInterval = 0.25f;
        private const float RowHeight = 18f;
        private const string Warning = "#ff9a3d";
        private const string Dim = "#9a9a9a";
        private const float Padding = 8f;

        private readonly EditorEngineer owner;
        private GameObject host;
        private DialogGUIBase root;
        private float width;

        private bool planner;
        private PlannerView plannerView;

        private readonly HashSet<int> expanded = new HashSet<int>();
        private List<StageRow> shown = new List<StageRow>();
        private bool resourcesOpen;
        private bool partOpen = true;
        private Part lastPart;
        private float nextPartRead;
        private string partTitle = "";
        private List<PartDetails.Line> partLines = new List<PartDetails.Line>();

        private EngineerPanel(EditorEngineer owner)
        {
            this.owner = owner;
        }

        internal static EngineerPanel Create(EditorEngineer owner, RectTransform parent)
        {
            if (parent == null) return null;
            EngineerPanel panel = new EngineerPanel(owner);
            panel.Build(parent);
            return panel;
        }

        private void Build(RectTransform parent)
        {
            host = new GameObject("ReDefinitionEngineerView", typeof(RectTransform), typeof(Image));
            RectTransform rect = (RectTransform)host.transform;
            rect.SetParent(parent, false);
            // From the top of the parts list's sorting row down: the search
            // bar above it stays KSP's.
            Rect area = ListArea(parent);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = area.min - parent.rect.min;
            rect.offsetMax = area.max - parent.rect.max;
            rect.SetAsLastSibling();
            Image background = host.GetComponent<Image>();
            background.color = new Color(0.12f, 0.13f, 0.14f, 1f);
            background.raycastTarget = true;

            float panelWidth = Mathf.Max(200f, area.width);
            float panelHeight = Mathf.Max(200f, area.height);
            width = panelWidth - Padding * 2f - 18f;
            Debug.Log(Core.Log.Tag + " Engineer view built " + panelWidth.ToString("0") + " wide, rows " + width.ToString("0") + ".");

            UISkinDef skin = UISkinManager.GetSkin("MiniSettingsSkin") ?? HighLogic.UISkin;
            List<DialogGUIBase> engineer = new List<DialogGUIBase>();
            engineer.AddRange(SituationRows());
            engineer.AddRange(VesselRows());
            engineer.AddRange(StageRows());
            engineer.AddRange(RcsRows());
            engineer.AddRange(ResourceRows());
            engineer.AddRange(PartRows());
            engineer.AddRange(NoticeRows());
            DialogGUIVerticalLayout engineerTab = new DialogGUIVerticalLayout(width, -1f, 3f, new RectOffset(), TextAnchor.UpperLeft,
                engineer.ToArray());
            engineerTab.OptionEnabledCondition = () => !planner;

            plannerView = new PlannerView(owner, width);
            // No gap between the planner's rows, so that its route reads as one
            // line from stop to stop.
            DialogGUIVerticalLayout plannerTab = new DialogGUIVerticalLayout(width, -1f, 0f, new RectOffset(), TextAnchor.UpperLeft,
                plannerView.Rows().ToArray());
            plannerTab.OptionEnabledCondition = () => planner;

            float half = (width - 4f) / 2f;
            List<DialogGUIBase> rows = new List<DialogGUIBase>
            {
                new DialogGUIContentSizer(ContentSizeFitter.FitMode.Unconstrained, ContentSizeFitter.FitMode.PreferredSize, true),
                new DialogGUIHorizontalLayout(width, RowHeight + 6f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                    new DialogGUIToggleButton(() => !planner, "Engineer", on => { if (on) planner = false; }, half, RowHeight + 6f),
                    new DialogGUIToggleButton(() => planner, "Planner", on => { if (on) planner = true; }, half, RowHeight + 6f)),
                engineerTab,
                plannerTab,
            };

            DialogGUIVerticalLayout content = new DialogGUIVerticalLayout(width, -1f, 3f,
                new RectOffset((int)Padding, (int)Padding, 6, 10), TextAnchor.UpperLeft, rows.ToArray());
            root = new DialogGUIScrollList(new Vector2(panelWidth, panelHeight), false, true, content);

            Stack<Transform> layouts = new Stack<Transform>();
            layouts.Push(host.transform);
            root.Create(ref layouts, skin);
            plannerView.Finish();
            RectTransform scroll = root.uiItem != null ? root.uiItem.transform as RectTransform : null;
            if (scroll != null)
            {
                scroll.anchorMin = Vector2.zero;
                scroll.anchorMax = Vector2.one;
                scroll.offsetMin = Vector2.zero;
                scroll.offsetMax = Vector2.zero;
            }
            host.SetActive(false);
        }

        // The panel's width, and from the bottom of the parts list up to the top
        // of its sorting row (EditorPartList.partListScrollRect, partListSorter),
        // in the panel's own space. The whole panel where the list is not found.
        private static Rect ListArea(RectTransform parent)
        {
            Rect whole = parent.rect;
            EditorPartList list = EditorPartList.Instance;
            if (list == null || list.partListScrollRect == null) return whole;
            Rect scroll = LocalRect(parent, (RectTransform)list.partListScrollRect.transform);
            float top = scroll.yMax;
            if (list.partListSorter != null)
                top = Mathf.Max(top, LocalRect(parent, (RectTransform)list.partListSorter.transform).yMax);
            float bottom = Mathf.Max(whole.yMin, scroll.yMin);
            top = Mathf.Min(whole.yMax, top);
            if (top - bottom < 200f) return whole;
            return Rect.MinMaxRect(whole.xMin, bottom, whole.xMax, top);
        }

        private static Rect LocalRect(RectTransform parent, RectTransform child)
        {
            Vector3[] corners = new Vector3[4];
            child.GetWorldCorners(corners);
            Vector3 min = parent.InverseTransformPoint(corners[0]);
            Vector3 max = parent.InverseTransformPoint(corners[2]);
            return Rect.MinMaxRect(Mathf.Min(min.x, max.x), Mathf.Min(min.y, max.y), Mathf.Max(min.x, max.x), Mathf.Max(min.y, max.y));
        }

        internal void Show(bool on)
        {
            if (host != null) host.SetActive(on);
            if (!on && plannerView != null) plannerView.Flush();
        }

        internal void Destroy()
        {
            if (plannerView != null) plannerView.Dispose();
            if (host != null) UnityEngine.Object.Destroy(host);
            host = null;
        }

        internal void Update(OwnSettings settings)
        {
            if (host == null || root == null) return;
            EditorReadout readout = owner.Readout ?? new EditorReadout();
            shown = StageTable.Shown(readout.Stages, settings != null && settings.EngineerAllStages);
            if (!planner && (settings == null || settings.EngineerPartDetails)) TrackPart();
            if (plannerView != null)
            {
                if (planner) plannerView.Update();
                else plannerView.Idle();
            }
            root.Update();
        }

        // ------------------------------------------------------------- situation

        private IEnumerable<DialogGUIBase> SituationRows()
        {
            yield return Heading("Situation");
            float arrow = 24f;
            yield return new DialogGUIHorizontalLayout(width, RowHeight + 4f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUIButton("<", () => StepBody(-1), arrow, RowHeight + 2f, false),
                new DialogGUILabel(() => "<b>" + BodyName() + "</b>", width - arrow * 2f - 8f),
                new DialogGUIButton(">", () => StepBody(1), arrow, RowHeight + 2f, false));

            float third = (width - 8f) / 3f;
            DialogGUIToggleButton seaLevel = new DialogGUIToggleButton(() => Situation() == DeltaVSituationOptions.SeaLevel,
                "Sea level", on => { if (on) SetSituation(DeltaVSituationOptions.SeaLevel); }, third, RowHeight + 4f);
            DialogGUIToggleButton altitude = new DialogGUIToggleButton(() => Situation() == DeltaVSituationOptions.Altitude,
                "Altitude", on => { if (on) SetSituation(DeltaVSituationOptions.Altitude); }, third, RowHeight + 4f);
            DialogGUIToggleButton vacuum = new DialogGUIToggleButton(() => Situation() == DeltaVSituationOptions.Vaccum,
                "Vacuum", on => { if (on) SetSituation(DeltaVSituationOptions.Vaccum); }, third, RowHeight + 4f);
            seaLevel.OptionInteractableCondition = HasAtmosphere;
            altitude.OptionInteractableCondition = HasAtmosphere;
            seaLevel.tooltipText = "At the body's sea level: the pressure there lowers the engines' thrust and impulse.";
            altitude.tooltipText = "At the altitude below, inside the body's atmosphere.";
            vacuum.tooltipText = "In vacuum, as a stage burns above the atmosphere.";
            yield return new DialogGUIHorizontalLayout(width, RowHeight + 4f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                seaLevel, altitude, vacuum);

            // From the ground to the top of the body's atmosphere.
            DialogGUISlider slider = new DialogGUISlider(() => (float)(Altitude() / Math.Max(1.0, MaxAltitudeKm() * 1000.0)), 0f,
                1f, false, width - 90f, RowHeight, value => SetAltitude(value * MaxAltitudeKm() * 1000.0));
            DialogGUIHorizontalLayout altitudeRow = new DialogGUIHorizontalLayout(width, RowHeight + 2f, 6f, new RectOffset(),
                TextAnchor.MiddleLeft, slider, new DialogGUILabel(() => EngineerFormat.Distance(Altitude()), 80f));
            altitudeRow.OptionEnabledCondition = () => Situation() == DeltaVSituationOptions.Altitude && HasAtmosphere();
            yield return altitudeRow;
        }

        private static DeltaVAppValues AppValues()
        {
            return DeltaVGlobals.DeltaVAppValues;
        }

        private static CelestialBody Body()
        {
            DeltaVAppValues values = AppValues();
            CelestialBody body = values != null ? values.body : null;
            return body != null ? body : FlightGlobals.GetHomeBody();
        }

        private static string BodyName()
        {
            CelestialBody body = Body();
            return body != null ? body.displayName.LocalizeRemoveGender() : "";
        }

        private static bool HasAtmosphere()
        {
            CelestialBody body = Body();
            return body != null && body.atmosphere;
        }

        private static DeltaVSituationOptions Situation()
        {
            DeltaVAppValues values = AppValues();
            if (values == null || !HasAtmosphere()) return DeltaVSituationOptions.Vaccum;
            return values.situation;
        }

        private static double Altitude()
        {
            DeltaVAppValues values = AppValues();
            return values != null ? values.altitude : 0.0;
        }

        private static double MaxAltitudeKm()
        {
            CelestialBody body = Body();
            return body != null && body.atmosphere ? body.atmosphereDepth / 1000.0 : 0.0;
        }

        private static void StepBody(int direction)
        {
            List<CelestialBody> bodies = FlightGlobals.Bodies;
            if (bodies == null || bodies.Count == 0) return;
            int at = bodies.IndexOf(Body());
            int next = ((at < 0 ? 0 : at) + direction + bodies.Count) % bodies.Count;
            DeltaVAppValues values = AppValues();
            if (values == null) return;
            values.body = bodies[next];
            if (!bodies[next].atmosphere) values.situation = DeltaVSituationOptions.Vaccum;
            values.altitude = Math.Min(values.altitude, bodies[next].atmosphere ? bodies[next].atmosphereDepth : 0.0);
            Recalculate();
        }

        private static void SetSituation(DeltaVSituationOptions situation)
        {
            DeltaVAppValues values = AppValues();
            if (values == null || values.situation == situation) return;
            values.situation = situation;
            Recalculate();
        }

        private static void SetAltitude(double metres)
        {
            DeltaVAppValues values = AppValues();
            if (values == null) return;
            double clamped = Math.Max(0.0, Math.Min(metres, MaxAltitudeKm() * 1000.0));
            if (Math.Abs(values.altitude - clamped) < 1.0) return;
            values.altitude = clamped;
            Recalculate();
        }

        // As KSP's delta-v app does after a change of situation.
        private static void Recalculate()
        {
            ShipConstruct ship = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
            if (ship != null && ship.vesselDeltaV != null) ship.vesselDeltaV.SetCalcsDirty(true);
        }

        // ---------------------------------------------------------------- vessel

        private IEnumerable<DialogGUIBase> VesselRows()
        {
            yield return Heading("Vessel");
            yield return Line(() => Pair("Δv", Readout().Simulated ? EngineerFormat.Speed(Readout().TotalDeltaV) : "--"));
            yield return Line(() => Pair("TWR at launch", LaunchTwr()));
            yield return Line(() => Pair("Mass", EngineerFormat.Mass(Readout().WetMass) + " <color=" + Dim + ">(dry "
                                                   + EngineerFormat.Mass(Readout().DryMass) + ")</color>"));
            yield return Line(() => Pair("Cost", EngineerFormat.Cost(Readout().Cost)));
            yield return Line(() => Pair("Parts", EngineerFormat.Count(Readout().Parts) + " <color=" + Dim + ">in "
                                                    + EngineerFormat.Count(Readout().Stages.Count) + " stages</color>"));
        }

        private EditorReadout Readout()
        {
            return owner.Readout ?? new EditorReadout();
        }

        private string LaunchTwr()
        {
            StageRow first = StageTable.FirstBurning(Readout().Stages);
            if (first == null) return "--";
            string twr = EngineerFormat.Ratio(first.Twr);
            return first.Twr < 1.0 ? "<color=" + Warning + ">" + twr + " -- below 1, it will not lift off</color>" : twr;
        }

        // ---------------------------------------------------------------- stages

        private IEnumerable<DialogGUIBase> StageRows()
        {
            yield return Heading("Stages");
            yield return new DialogGUILabel(() => "<color=" + Dim + ">" + Columns("Stage", "Δv", "Δv left", "TWR", "Burn")
                                                  + "</color>", width, RowHeight);
            for (int i = 0; i < MaxStages; i++)
            {
                int index = i;
                DialogGUIButton fold = new DialogGUIButton(() => Expanded(index) ? "-" : "+", () => ToggleStage(index), 16f,
                    RowHeight, false);
                DialogOverWindow.ClearBackground(fold);
                fold.tooltipText = "Shows the stage's specific impulse, thrust, mass, parts, cost and thrust torque.";
                DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, RowHeight, 2f, new RectOffset(),
                    TextAnchor.MiddleLeft, fold, new DialogGUILabel(() => StageText(index), width - 18f, RowHeight));
                row.OptionEnabledCondition = () => index < shown.Count;
                yield return row;

                DialogGUILabel detail = new DialogGUILabel(() => StageDetail(index), true, true);
                DialogGUIHorizontalLayout detailRow = new DialogGUIHorizontalLayout(width, -1f, 0f, new RectOffset(18, 0, 0, 4),
                    TextAnchor.UpperLeft, detail);
                detailRow.OptionEnabledCondition = () => index < shown.Count && Expanded(index);
                yield return detailRow;
            }
            DialogGUILabel empty = new DialogGUILabel(() => Readout().HasShip
                ? "<color=" + Dim + ">No stage burns anything yet.</color>"
                : "<color=" + Dim + ">Place a part to see the vessel's stages.</color>", width, RowHeight);
            DialogGUIHorizontalLayout emptyRow = new DialogGUIHorizontalLayout(width, RowHeight, 0f, new RectOffset(),
                TextAnchor.MiddleLeft, empty);
            emptyRow.OptionEnabledCondition = () => shown.Count == 0;
            yield return emptyRow;
            DialogGUILabel more = new DialogGUILabel(() => "<color=" + Dim + ">and " + (shown.Count - MaxStages)
                                                           + " more stages, not listed</color>", width, RowHeight);
            DialogGUIHorizontalLayout moreRow = new DialogGUIHorizontalLayout(width, RowHeight, 0f, new RectOffset(),
                TextAnchor.MiddleLeft, more);
            moreRow.OptionEnabledCondition = () => shown.Count > MaxStages;
            yield return moreRow;
        }

        private static string Columns(string stage, string deltaV, string left, string twr, string burn)
        {
            return stage + "<pos=16%>" + deltaV + "<pos=42%>" + left + "<pos=66%>" + twr + "<pos=82%>" + burn;
        }

        private bool Expanded(int index)
        {
            return index < shown.Count && expanded.Contains(shown[index].Number);
        }

        private void ToggleStage(int index)
        {
            if (index >= shown.Count) return;
            int number = shown[index].Number;
            if (!expanded.Remove(number)) expanded.Add(number);
        }

        private string StageText(int index)
        {
            if (index >= shown.Count) return "";
            StageRow row = shown[index];
            StageRow first = StageTable.FirstBurning(Readout().Stages);
            string twr = row.HasEngines ? EngineerFormat.Ratio(row.Twr) : "--";
            if (row == first && row.Twr < 1.0) twr = "<color=" + Warning + ">" + twr + "</color>";
            return Columns("S" + row.Number, EngineerFormat.Speed(row.DeltaV), EngineerFormat.Speed(row.DeltaVRemaining), twr,
                row.HasEngines ? EngineerFormat.Time(row.BurnTime) : "--");
        }

        private string StageDetail(int index)
        {
            if (index >= shown.Count) return "";
            StageRow row = shown[index];
            StringBuilder text = new StringBuilder();
            if (row.HasEngines)
            {
                text.Append(Pair("Isp", EngineerFormat.Isp(row.Isp))).Append('\n');
                text.Append(Pair("Thrust", EngineerFormat.Force(row.Thrust))).Append('\n');
                text.Append(Pair("TWR at start / end", EngineerFormat.Ratio(row.Twr) + " / " + EngineerFormat.Ratio(row.TwrMax)))
                    .Append('\n');
                text.Append(Pair("Thrust torque", EngineerFormat.Torque(row.ThrustTorque))).Append('\n');
            }
            text.Append(Pair("Mass at start / end", EngineerFormat.Mass(row.StartMass) + " / " + EngineerFormat.Mass(row.EndMass)))
                .Append('\n');
            text.Append(Pair("Parts dropped / attached", EngineerFormat.Count(row.OwnParts) + " / "
                                                         + EngineerFormat.Count(row.AttachedParts))).Append('\n');
            text.Append(Pair("Cost dropped / attached", EngineerFormat.Cost(row.OwnCost) + " / "
                                                        + EngineerFormat.Cost(row.AttachedCost)));
            return text.ToString();
        }

        // ------------------------------------------------------------------- RCS

        private IEnumerable<DialogGUIBase> RcsRows()
        {
            DialogGUIBase heading = Heading("RCS, last stage");
            heading.OptionEnabledCondition = () => Readout().Rcs != null;
            yield return heading;
            DialogGUILabel lines = new DialogGUILabel(RcsText, true, true);
            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, -1f, 0f, new RectOffset(), TextAnchor.UpperLeft,
                lines);
            row.OptionEnabledCondition = () => Readout().Rcs != null;
            yield return row;
        }

        private string RcsText()
        {
            EditorReadout.RcsLine rcs = Readout().Rcs;
            if (rcs == null) return "";
            return Pair("Δv", EngineerFormat.Speed(rcs.DeltaV)) + "\n" + Pair("Isp", EngineerFormat.Isp(rcs.Isp)) + "\n"
                   + Pair("Thrust, all thrusters", EngineerFormat.Force(rcs.Thrust)) + "\n"
                   + Pair("TWR", EngineerFormat.Ratio(rcs.Twr)) + "\n" + Pair("Burn time", EngineerFormat.Time(rcs.BurnTime));
        }

        // ------------------------------------------------------------- resources

        private IEnumerable<DialogGUIBase> ResourceRows()
        {
            yield return FoldHeading("Resources", () => resourcesOpen, () => resourcesOpen = !resourcesOpen);
            DialogGUILabel lines = new DialogGUILabel(ResourceText, true, true);
            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, -1f, 0f, new RectOffset(), TextAnchor.UpperLeft,
                lines);
            row.OptionEnabledCondition = () => resourcesOpen;
            yield return row;
        }

        private string ResourceText()
        {
            List<EditorReadout.ResourceLine> resources = Readout().Resources;
            if (resources.Count == 0) return "<color=" + Dim + ">None on board.</color>";
            StringBuilder text = new StringBuilder();
            foreach (EditorReadout.ResourceLine line in resources)
            {
                if (text.Length > 0) text.Append('\n');
                string value = EngineerFormat.Amount(line.Amount) + " / " + EngineerFormat.Amount(line.MaxAmount);
                if (line.Mass > 0.0) value += " <color=" + Dim + ">(" + EngineerFormat.Mass(line.Mass) + ")</color>";
                text.Append(Pair(line.Name, value));
            }
            return text.ToString();
        }

        // ------------------------------------------------------------------ part

        private IEnumerable<DialogGUIBase> PartRows()
        {
            DialogGUIBase heading = FoldHeading("Part under the mouse", () => partOpen, () => partOpen = !partOpen);
            heading.OptionEnabledCondition = PartDetailsWanted;
            yield return heading;
            DialogGUILabel lines = new DialogGUILabel(PartText, true, true);
            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, -1f, 0f, new RectOffset(), TextAnchor.UpperLeft,
                lines);
            row.OptionEnabledCondition = () => PartDetailsWanted() && partOpen;
            yield return row;
        }

        private static bool PartDetailsWanted()
        {
            OwnSettings settings = EditorEngineer.Settings();
            return settings == null || settings.EngineerPartDetails;
        }

        // The part the mouse points at on the vessel, read again four times a
        // second -- its tanks can be drained, its engine changed -- and kept once
        // the mouse leaves it. KSP's hovered part is a raycast from the editor's
        // camera that knows nothing of the UI (Mouse.CheckHoveredPart, decompiled),
        // so over this panel or any other window it would find a part behind it.
        private void TrackPart()
        {
            UnityEngine.EventSystems.EventSystem events = UnityEngine.EventSystems.EventSystem.current;
            if (events != null && events.IsPointerOverGameObject()) return;
            Part hovered = Mouse.HoveredPart;
            if (hovered == null) return;
            if (hovered == lastPart && Time.unscaledTime < nextPartRead) return;
            nextPartRead = Time.unscaledTime + PartInterval;
            lastPart = hovered;
            partTitle = hovered.partInfo != null ? hovered.partInfo.title : hovered.name;
            partLines = PartDetails.Of(hovered);
        }

        private string PartText()
        {
            if (lastPart == null) return "<color=" + Dim + ">Point at a part of the vessel.</color>";
            StringBuilder text = new StringBuilder("<b>" + partTitle + "</b>");
            foreach (PartDetails.Line line in partLines)
            {
                text.Append('\n');
                string name = line.Detail ? "    " + line.Name : line.Name;
                text.Append(line.Value.Length == 0 ? name : Pair(name, line.Value));
            }
            return text.ToString();
        }

        // ---------------------------------------------------------------- notice

        private IEnumerable<DialogGUIBase> NoticeRows()
        {
            if (!EditorEngineer.KerbalEngineerInstalled) yield break;
            yield return new DialogGUISpace(6f);
            yield return new DialogGUILabel("<color=" + Warning + ">Kerbal Engineer Redux is installed: this view shows its build"
                                            + " information. Uninstalling it is recommended.</color>", true, true);
        }

        // ---------------------------------------------------------------- pieces

        private DialogGUIBase Heading(string title)
        {
            return new DialogGUIHorizontalLayout(width, RowHeight + 6f, 0f, new RectOffset(0, 0, 6, 0), TextAnchor.LowerLeft,
                new DialogGUILabel("<b><color=#e0e0e0>" + title + "</color></b>", width, RowHeight));
        }

        private DialogGUIBase FoldHeading(string title, Func<bool> isOpen, Callback toggle)
        {
            DialogGUIButton button = new DialogGUIButton(() => "<b>" + (isOpen() ? "- " : "+ ") + title + "</b>", toggle, width,
                RowHeight + 4f, false);
            DialogOverWindow.ClearBackground(button);
            return new DialogGUIHorizontalLayout(width, RowHeight + 8f, 0f, new RectOffset(0, 0, 6, 0), TextAnchor.LowerLeft,
                button);
        }

        private DialogGUIBase Line(Func<string> text)
        {
            return new DialogGUILabel(text, width, RowHeight);
        }

        private static string Pair(string name, string value)
        {
            return name + "<pos=52%>" + value;
        }
    }
}
