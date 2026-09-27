using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The planner's rows, built from KSP's dialog elements, so a UI theme reaches
    // them as it reaches KSP's dialogs. From the top: the route as a line of
    // stops, a bar between each two for the leg (its length the leg's delta-v,
    // its colours the stages that pay for it, red what none covers); the fuel
    // for the whole route; and the fuel plan, a table of stages by legs: what
    // each stage gives to each leg, and the pins the player sets there. A chosen
    // option is lit in its text rather than by the skin's pressed state. A fixed
    // number of rows and cells, shown as the plan needs them.
    internal sealed partial class PlannerView
    {
        private const int MaxStops = 9;
        private const int MaxStages = 30;
        private const int MaxPieces = 24;
        private const float Icon = 24f;
        private const float Timeline = 20f;
        private const float LetterWidth = 14f;
        private const float Speed = 96f;
        private const float BarHeight = 10f;
        private const float RowHeight = 20f;
        private const float Cell = 38f;
        // The swatch, the stage's name and the lander's button, with the gaps
        // between them.
        private const float StageColumn = 72f;
        private const float LeftColumn = 56f;
        private const string Warning = "#f18a24";
        private const string Good = "#8fdc7a";
        private const string Dim = "#9a9a9a";

        private readonly SegmentBar[] legBars = new SegmentBar[MaxStops];
        private SegmentBar totalBar;
        private readonly IconMark[,] pinMarks = new IconMark[MaxStages, MaxStops];
        private Sizer columns;
        private float cellWidth = Cell;

        internal List<DialogGUIBase> Rows()
        {
            columns = new Sizer(after);
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            rows.Add(Heading("Route"));
            for (int i = 0; i < MaxStops; i++)
            {
                rows.AddRange(StopRows(i));
                rows.AddRange(LegRows(i));
            }
            DialogGUIButton add = new DialogGUIButton("+ Add destination", AddStop, width, RowHeight + 4f, false);
            add.OptionEnabledCondition = () => Ready() && state.Stops.Count < MaxStops;
            rows.Add(add);

            rows.AddRange(SummaryRows());
            rows.Add(Heading("Fuel plan"));
            rows.Add(PlanHeader());
            for (int i = 0; i < MaxStages; i++) rows.Add(PlanRow(i));
            rows.AddRange(DetailRows());
            return rows;
        }

        // ----------------------------------------------------------------- stops

        private IEnumerable<DialogGUIBase> StopRows(int index)
        {
            DialogGUIHorizontalLayout dot = new DialogGUIHorizontalLayout(Timeline, Icon, 0f, new RectOffset(4, 0, 0, 0),
                TextAnchor.MiddleLeft, PlannerWidgets.Icon("Dot", 12f, PlannerColors.Line));
            // The body's button takes the room the others leave: the dot, the
            // letter, three choices, the cross and the gaps between the seven.
            float bodyWidth = Mathf.Max(100f, width - Timeline - LetterWidth - 62f - 52f - 76f - Icon - 4f * 7f - 2f);
            DialogGUIButton body = new DialogGUIButton(() => "<b>" + BodyName(index) + "</b>", () => PickBody(index), bodyWidth,
                Icon, false);
            body.tooltipText = "Body";
            DialogGUIButton surface = Choice("Surface", () => Kind(index) == StopKind.Surface, () => SetKind(index, StopKind.Surface),
                62f);
            DialogGUIButton orbit = Choice("Orbit", () => Kind(index) == StopKind.Orbit, () => SetKind(index, StopKind.Orbit), 52f);
            DialogGUIButton air = Choice(() => Kind(index) == StopKind.Surface ? "Chutes" : "Aerobrake", () => UsesAtmosphere(index),
                () => ToggleAtmosphere(index), 76f);
            air.OptionEnabledCondition = () => index > 0 && HasAtmosphere(index);
            DialogGUIButton remove = PlannerWidgets.IconButton("Remove", () => RemoveStop(index), Icon, "Remove", after);
            remove.OptionEnabledCondition = () => Ready() && state.Stops.Count > 2;

            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, Icon + 4f, 4f, new RectOffset(0, 0, 2, 0),
                TextAnchor.MiddleLeft, dot, new DialogGUILabel(() => "<b>" + Letter(index) + "</b>", LetterWidth), body, surface,
                orbit, air, remove);
            row.OptionEnabledCondition = () => Shown(index);
            yield return row;

            DialogGUIHorizontalLayout altitude = new DialogGUIHorizontalLayout(width, RowHeight + 2f, 4f, new RectOffset(),
                TextAnchor.MiddleLeft, TimelineLine(RowHeight + 2f), new DialogGUISpace(LetterWidth),
                new DialogGUIButton("-", () => StepAltitude(index, -1), 22f, RowHeight, false),
                new DialogGUILabel(() => AltitudeText(index), 64f),
                new DialogGUIButton("+", () => StepAltitude(index, 1), 22f, RowHeight, false));
            altitude.OptionEnabledCondition = () => Shown(index) && Kind(index) == StopKind.Orbit;
            yield return altitude;
        }

        private static DialogGUIBase TimelineLine(float height)
        {
            return new DialogGUIHorizontalLayout(Timeline, height, 0f, new RectOffset(9, 0, 0, 0), TextAnchor.MiddleLeft,
                PlannerWidgets.Block(2f, height, PlannerColors.Line));
        }

        // A button of KSP's skin whose text is lit while its option is chosen.
        private static DialogGUIButton Choice(string label, System.Func<bool> chosen, Callback choose, float buttonWidth)
        {
            return Choice(() => label, chosen, choose, buttonWidth);
        }

        private static DialogGUIButton Choice(System.Func<string> label, System.Func<bool> chosen, Callback choose, float buttonWidth)
        {
            return new DialogGUIButton(() => chosen()
                ? "<b><color=" + Good + ">" + label() + "</color></b>"
                : "<color=" + Dim + ">" + label() + "</color>", choose, buttonWidth, RowHeight + 2f, false);
        }

        // ------------------------------------------------------------------ legs

        private IEnumerable<DialogGUIBase> LegRows(int index)
        {
            SegmentBar bar = new SegmentBar(MaxPieces, BarHeight, after);
            legBars[index] = bar;
            PlannerWidgets.Clickable(bar.Layout, () => SelectLeg(index), after);

            DialogGUIButton speed = new DialogGUIButton(() => LegSpeed(index), () => SelectLeg(index), Speed, RowHeight, false);
            DialogOverWindow.ClearBackground(speed);
            DialogGUIButton whole = ModeChoice(index, LegMode.Whole, "Ship", 44f);
            DialogGUIButton alone = ModeChoice(index, LegMode.Lander, "Lander", 56f);
            DialogGUIButton rest = ModeChoice(index, LegMode.WithoutLander, "Rest", 44f);

            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, Icon + 2f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                TimelineLine(Icon + 2f), bar.Layout, new DialogGUIFlexibleSpace(), speed, whole, alone, rest);
            row.OptionEnabledCondition = () => Ready() && index + 1 < state.Stops.Count;
            yield return row;
        }

        private DialogGUIButton ModeChoice(int leg, LegMode mode, string label, float buttonWidth)
        {
            DialogGUIButton button = Choice(label, () => Mode(leg) == mode, () => SetMode(leg, mode), buttonWidth);
            button.OptionEnabledCondition = HasLander;
            return button;
        }

        private string LegSpeed(int index)
        {
            if (!Worked() || index >= legs.Count) return "";
            string speed = EngineerFormat.Speed(budget.Needed[index]);
            bool warn = budget.Short[index] > 0.5 || (index < meetings.Length && meetings[index] >= 0)
                        || budget.Unreachable.Exists(pin => pin.Value == index);
            if (warn) speed = "<color=" + Warning + ">" + speed + "</color>";
            else if (selectedLeg == index) speed = "<b><color=" + Good + ">" + speed + "</color></b>";
            return speed;
        }

        // --------------------------------------------------------------- summary

        private IEnumerable<DialogGUIBase> SummaryRows()
        {
            yield return new DialogGUISpace(8f);
            totalBar = new SegmentBar(MaxStages + 2, BarHeight + 4f, after);
            yield return new DialogGUIHorizontalLayout(width, BarHeight + 6f, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                totalBar.Layout, new DialogGUIFlexibleSpace());
            yield return new DialogGUILabel(SummaryText, width, RowHeight);

            DialogGUISlider margin = new DialogGUISlider(() => (float)(Ready() ? state.Margin * 100.0 : 10.0), 0f, 50f, true,
                width - 260f, RowHeight, SetMargin);
            DialogGUIToggle planes = new DialogGUIToggle(() => Ready() && state.PlaneChanges, "Plane changes", SetPlaneChanges, 140f);
            yield return new DialogGUIHorizontalLayout(width, RowHeight + 4f, 6f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel("Margin", 56f), margin,
                new DialogGUILabel(() => (Ready() ? (state.Margin * 100.0).ToString("0") : "10") + " %", 44f), planes);
        }

        private string SummaryText()
        {
            if (!Worked()) return "";
            string balance = budget.TotalShort > 0.5
                ? "<color=" + Warning + ">-" + EngineerFormat.Speed(budget.TotalShort) + "</color>"
                : "<color=" + Good + ">+" + EngineerFormat.Speed(budget.TotalLeft) + "</color>";
            return "<b>" + balance + "</b><pos=30%>" + EngineerFormat.Speed(TotalNeeded()) + " <color=" + Dim + ">of</color> "
                   + EngineerFormat.Speed(TotalHas());
        }

        // ------------------------------------------------------------- fuel plan

        private DialogGUIBase PlanHeader()
        {
            List<DialogGUIBase> cells = new List<DialogGUIBase> { new DialogGUILabel("<color=" + Dim + ">Stage</color>", StageColumn) };
            for (int leg = 0; leg < MaxStops - 1; leg++)
            {
                int at = leg;
                DialogGUIButton header = columns.Add(new DialogGUIButton(() => HeaderText(at), () => SelectLeg(at), Cell, RowHeight,
                    false));
                DialogOverWindow.ClearBackground(header);
                header.OptionEnabledCondition = () => Ready() && at < legs.Count;
                cells.Add(header);
            }
            cells.Add(new DialogGUILabel("<color=" + Dim + ">Left</color>", LeftColumn));
            return new DialogGUIHorizontalLayout(width, RowHeight + 2f, 2f, new RectOffset(), TextAnchor.MiddleLeft, cells.ToArray());
        }

        private string HeaderText(int leg)
        {
            string name = "<size=85%>" + LegName(leg) + "</size>";
            return selectedLeg == leg ? "<b><color=" + Good + ">" + name + "</color></b>" : "<color=" + Dim + ">" + name + "</color>";
        }

        private DialogGUIBase PlanRow(int index)
        {
            List<DialogGUIBase> cells = new List<DialogGUIBase>
            {
                PlannerWidgets.Block(8f, RowHeight - 2f, PlannerColors.Stage(index)),
                StageButton(index),
                LanderButton(index),
            };
            for (int leg = 0; leg < MaxStops - 1; leg++)
            {
                int at = leg;
                DialogGUIButton cell = columns.Add(new DialogGUIButton(() => CellText(index, at), () => TogglePin(index, at), Cell,
                    RowHeight, false));
                cell.tooltipText = "Pin";
                cell.OptionEnabledCondition = () => Ready() && at < legs.Count;
                pinMarks[index, at] = new IconMark(cell, "Pin", 12f, after);
                cells.Add(cell);
            }
            cells.Add(new DialogGUILabel(() => StageState(index), LeftColumn));
            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, RowHeight + 2f, 2f, new RectOffset(),
                TextAnchor.MiddleLeft, cells.ToArray());
            row.OptionEnabledCondition = () => index < stages.Count;
            return row;
        }

        private DialogGUIButton StageButton(int index)
        {
            DialogGUIButton button = new DialogGUIButton(() => StageLabel(index), () => SelectStage(index), 36f, RowHeight, false);
            DialogOverWindow.ClearBackground(button);
            return button;
        }

        private DialogGUIButton LanderButton(int index)
        {
            DialogGUIButton button = new DialogGUIButton(() => index < stages.Count && IsLanderStage(stages[index].Number)
                ? "<b><color=" + Good + ">L</color></b>"
                : "<color=" + Dim + ">L</color>", () => ToggleLander(index), 24f, RowHeight, false);
            button.tooltipText = "Lander";
            button.OptionInteractableCondition = () => index < stages.Count && landerChoices.Contains(stages[index].Number);
            return button;
        }

        // What the stage gives to the leg, in its colour; bold where pinned.
        private string CellText(int stage, int leg)
        {
            if (!Worked() || stage >= stages.Count || leg >= legs.Count) return "";
            double used = 0.0;
            foreach (StageBudget.StageUse use in budget.Uses)
                if (use.Leg == leg && use.Stage == stages[stage].Number) used += use.DeltaV;
            bool pinned = IsPinned(stage, leg);
            if (used < 0.5) return pinned ? "<color=" + Warning + ">0</color>" : "";
            string colour = PlannerColors.Hex(PlannerColors.Stage(stage));
            string figure = cellWidth >= 64f ? EngineerFormat.Speed(used) : Short(used);
            string text = "<size=90%><color=" + colour + ">" + figure + "</color></size>";
            return pinned ? "<b>" + text + "</b>" : text;
        }

        // 850, 1.2k, 12k: the delta-v in the room of a cell.
        private static string Short(double metres)
        {
            if (metres < 1000.0) return metres.ToString("0", CultureInfo.InvariantCulture);
            if (metres < 10000.0) return (metres / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
            return (metres / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "k";
        }

        private string StageLabel(int index)
        {
            if (index >= stages.Count) return "";
            string name = "S" + stages[index].Number;
            return selectedStage == stages[index].Number ? "<b><color=" + Good + ">" + name + "</color></b>" : "<b>" + name + "</b>";
        }

        private string StageState(int index)
        {
            if (!Worked() || index >= stages.Count) return "";
            int number = stages[index].Number;
            double value;
            if (budget.LeftBehind.Contains(number)) return "<color=" + Dim + ">stays</color>";
            if (budget.Dropped.TryGetValue(number, out value)) return "<color=" + Warning + ">-" + Short(value) + "</color>";
            if (budget.Left.TryGetValue(number, out value) && value > 0.5) return "<color=" + Good + ">" + Short(value) + "</color>";
            return "<color=" + Dim + ">0</color>";
        }

        private void UpdatePins()
        {
            for (int stage = 0; stage < MaxStages; stage++)
                for (int leg = 0; leg < MaxStops - 1; leg++)
                    if (pinMarks[stage, leg] != null) pinMarks[stage, leg].Set(IsPinned(stage, leg));
        }

        // --------------------------------------------------------------- details

        private IEnumerable<DialogGUIBase> DetailRows()
        {
            DialogGUIBase stageHeading = new DialogGUIHorizontalLayout(width, RowHeight + 6f, 0f, new RectOffset(0, 0, 8, 0),
                TextAnchor.LowerLeft, new DialogGUILabel(() => "<b>" + StageTitle() + "</b>", width, RowHeight));
            stageHeading.OptionEnabledCondition = () => selectedStage >= 0;
            yield return stageHeading;
            DialogGUIHorizontalLayout stage = new DialogGUIHorizontalLayout(width, -1f, 0f, new RectOffset(), TextAnchor.UpperLeft,
                new DialogGUILabel(StageDetail, true, true));
            stage.OptionEnabledCondition = () => selectedStage >= 0;
            yield return stage;

            DialogGUIBase legHeading = new DialogGUIHorizontalLayout(width, RowHeight + 6f, 0f, new RectOffset(0, 0, 8, 0),
                TextAnchor.LowerLeft, new DialogGUILabel(() => selectedLeg >= 0 ? "<b>" + LegTitle(selectedLeg) + "</b>" : "", width,
                    RowHeight));
            legHeading.OptionEnabledCondition = () => selectedLeg >= 0 && selectedStage < 0;
            yield return legHeading;
            DialogGUIHorizontalLayout leg = new DialogGUIHorizontalLayout(width, -1f, 0f, new RectOffset(), TextAnchor.UpperLeft,
                new DialogGUILabel(() => legDetail, true, true));
            leg.OptionEnabledCondition = () => selectedLeg >= 0 && selectedStage < 0;
            yield return leg;
        }

        private string LegTitle(int leg)
        {
            if (!Ready() || leg + 1 >= state.Stops.Count) return "";
            return Letter(leg) + " " + BodyName(leg) + "  to  " + Letter(leg + 1) + " " + BodyName(leg + 1);
        }

        private string LegDetail(int index)
        {
            if (!Worked() || index >= legs.Count) return "";
            StringBuilder text = new StringBuilder();
            foreach (LegLine line in legs[index].Lines)
            {
                if (text.Length > 0) text.Append('\n');
                text.Append(line.Name).Append("<pos=60%>").Append(EngineerFormat.Speed(line.DeltaV));
            }
            if (state.Margin > 0.0)
                text.Append("\n<color=" + Dim + ">Margin ").Append((state.Margin * 100.0).ToString("0")).Append(" %</color><pos=60%>")
                    .Append(EngineerFormat.Speed(budget.Needed[index] - legs[index].DeltaV));
            foreach (StageBudget.StageUse use in budget.Uses)
            {
                if (use.Leg != index) continue;
                text.Append('\n').Append(ColouredStage(use.Stage));
                if (state.IsPinned(use.Stage, index)) text.Append(" <color=" + Dim + ">pinned</color>");
                text.Append("<pos=60%>").Append(EngineerFormat.Speed(use.DeltaV));
            }
            if (budget.Short[index] > 0.5)
                text.Append("\n<color=" + Warning + ">Short<pos=60%>").Append(EngineerFormat.Speed(budget.Short[index])).Append("</color>");
            foreach (KeyValuePair<int, int> pin in budget.Unreachable)
                if (pin.Value == index)
                    text.Append("\n<color=" + Warning + ">S").Append(pin.Key).Append(" has nothing left for this leg</color>");
            if (index < meetings.Length && meetings[index] >= 0)
                text.Append("\n<color=" + Warning + ">The rest waits at ").Append(Letter(meetings[index])).Append("</color>");
            foreach (string warning in ThrustWarnings(index))
                text.Append("\n<color=" + Warning + ">").Append(warning).Append("</color>");
            return text.ToString();
        }

        private string StageTitle()
        {
            int at = stages.FindIndex(row => row.Number == selectedStage);
            return at >= 0 ? ColouredStage(selectedStage) + "  " + EngineerFormat.Speed(stages[at].DeltaVVacuum) : "";
        }

        private string StageDetail()
        {
            int at = stages.FindIndex(s => s.Number == selectedStage);
            if (at < 0 || !Worked()) return "";
            StageRow row = stages[at];
            StringBuilder text = new StringBuilder();
            if (row.Engines.Length > 0) text.Append(row.Engines);
            foreach (StageBudget.StageUse use in budget.Uses)
                if (use.Stage == row.Number)
                    text.Append('\n').Append(LegName(use.Leg)).Append("<pos=60%>").Append(EngineerFormat.Speed(use.DeltaV));
            double value;
            if (budget.Dropped.TryGetValue(row.Number, out value))
                text.Append("\n<color=" + Warning + ">Dropped with<pos=60%>").Append(EngineerFormat.Speed(value)).Append("</color>");
            else if (budget.Left.TryGetValue(row.Number, out value) && value > 0.5)
                text.Append("\n<color=" + Good + ">Left<pos=60%>").Append(EngineerFormat.Speed(value)).Append("</color>");
            return text.ToString();
        }

        // "S3" in the colour of its stage.
        private string ColouredStage(int number)
        {
            int at = stages.FindIndex(row => row.Number == number);
            string colour = at >= 0 ? PlannerColors.Hex(PlannerColors.Stage(at)) : Dim;
            return "<b><color=" + colour + ">S" + number + "</color></b>";
        }

        // ------------------------------------------------------------------ bars

        // Each leg's bar on one scale, the longest leg filling the room left of
        // its figures; the summary's bar on the scale of what is needed or had,
        // whichever is more.
        private void UpdateBars()
        {
            if (!Worked()) return;
            // What the row leaves the bar: the line, the figure and, with a
            // lander, three choices, with the gaps between them, and a seam of
            // one unit between the bar's pieces.
            float room = Mathf.Max(40f, width - Timeline - Speed - 4f * 3f - 4f - (HasLander() ? 44f + 56f + 44f + 4f * 3f : 0f));
            double longest = 1.0;
            foreach (double value in budget.Needed) if (value > longest) longest = value;
            for (int leg = 0; leg < MaxStops; leg++)
            {
                if (legBars[leg] == null) continue;
                List<KeyValuePair<float, Color>> pieces = new List<KeyValuePair<float, Color>>();
                if (leg < legs.Count)
                {
                    int count = budget.Uses.FindAll(use => use.Leg == leg).Count + (budget.Short[leg] > 0.5 ? 1 : 0);
                    float barRoom = Mathf.Max(20f, room - count);
                    foreach (StageBudget.StageUse use in budget.Uses)
                        if (use.Leg == leg) pieces.Add(Piece(use.DeltaV / longest * barRoom, StageColour(use.Stage)));
                    if (budget.Short[leg] > 0.5) pieces.Add(Piece(budget.Short[leg] / longest * barRoom, PlannerColors.Short));
                }
                legBars[leg].Set(pieces);
            }

            // The fuel plan's columns share what the stage and left columns
            // leave, over the legs there are.
            int shown = Mathf.Max(1, legs.Count);
            cellWidth = Mathf.Clamp((width - StageColumn - LeftColumn - 2f * (shown + 1)) / shown, Cell, 140f);
            if (columns != null) columns.Set(cellWidth);

            if (totalBar == null) return;
            List<KeyValuePair<double, Color>> parts = new List<KeyValuePair<double, Color>>();
            for (int i = 0; i < stages.Count; i++)
            {
                double used = 0.0;
                foreach (StageBudget.StageUse use in budget.Uses) if (use.Stage == stages[i].Number) used += use.DeltaV;
                if (used > 0.5) parts.Add(new KeyValuePair<double, Color>(used, PlannerColors.Stage(i)));
            }
            if (budget.TotalLeft > 0.5) parts.Add(new KeyValuePair<double, Color>(budget.TotalLeft, PlannerColors.Spare));
            if (budget.TotalShort > 0.5) parts.Add(new KeyValuePair<double, Color>(budget.TotalShort, PlannerColors.Short));
            double sum = 0.0;
            foreach (KeyValuePair<double, Color> part in parts) sum += part.Key;
            double scale = System.Math.Max(1.0, System.Math.Max(sum, System.Math.Max(TotalNeeded(), TotalHas())));
            float usable = Mathf.Max(20f, width - parts.Count);
            List<KeyValuePair<float, Color>> total = new List<KeyValuePair<float, Color>>();
            foreach (KeyValuePair<double, Color> part in parts) total.Add(Piece(part.Key / scale * usable, part.Value));
            totalBar.Set(total);
        }

        private static KeyValuePair<float, Color> Piece(double pieceWidth, Color color)
        {
            return new KeyValuePair<float, Color>((float)pieceWidth, color);
        }

        private Color StageColour(int number)
        {
            int at = stages.FindIndex(row => row.Number == number);
            return at >= 0 ? PlannerColors.Stage(at) : PlannerColors.Spare;
        }

        private DialogGUIBase Heading(string title)
        {
            return new DialogGUIHorizontalLayout(width, RowHeight + 8f, 0f, new RectOffset(0, 0, 8, 0), TextAnchor.LowerLeft,
                new DialogGUILabel("<b><color=#e0e0e0>" + title + "</color></b>", width, RowHeight));
        }
    }
}
