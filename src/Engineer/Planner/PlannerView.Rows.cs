using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The planner's rows, built from KSP's dialog elements, so a UI theme reaches
    // them as it reaches KSP's dialogs. From the top: the route as a line of
    // destinations, each with who flies there, and between each two the steps
    // that follow from it, a bar each (its length the step's delta-v, its colours
    // the stages that pay for it, red what none covers), the choices a step
    // offers, and in red what stands in its way; the fuel for the whole route; and
    // the fuel plan, a table of stages by legs: what each stage gives to each leg,
    // and the pins the player sets there. A chosen option is lit in its text
    // rather than by the skin's pressed state. A fixed number of rows and cells,
    // shown as the plan needs them.
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
        // Who flies a step, left of its bar, where the vessel carries a lander.
        private const float StepLabel = 44f;
        private const int MaxWarnings = 3;
        private const string Warning = "#f18a24";
        private const string Problem = "#e6474d";
        private const string Good = "#8fdc7a";
        private const string Dim = "#9a9a9a";

        private readonly SegmentBar[,] stepBars = new SegmentBar[MaxStops, 3];
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
                foreach (StepRole role in new[] { StepRole.Return, StepRole.Approach, StepRole.Main })
                    rows.AddRange(StepRows(i, role));
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
            // KSP's layouts take no width of their own (DialogGUILayoutBase,
            // decompiled): the space after the dot makes the column as wide as
            // the timeline's.
            DialogGUIHorizontalLayout dot = new DialogGUIHorizontalLayout(Timeline, Icon, 0f, new RectOffset(4, 0, 0, 0),
                TextAnchor.MiddleLeft, PlannerWidgets.Icon("Dot", 12f, PlannerColors.Line), new DialogGUISpace(Timeline - 16f));
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
            // Its room where it is hidden, so that the cross stands in one
            // column on every stop.
            DialogGUISpace noAir = new DialogGUISpace(76f);
            noAir.OptionEnabledCondition = () => !(index > 0 && HasAtmosphere(index));
            DialogGUIButton remove = PlannerWidgets.IconButton("Remove", () => RemoveStop(index), Icon, "Remove", after);
            remove.OptionEnabledCondition = () => Ready() && state.Stops.Count > 2;

            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, Icon + 4f, 4f, new RectOffset(0, 0, 2, 0),
                TextAnchor.MiddleLeft, dot, new DialogGUILabel(() => "<b>" + Letter(index) + "</b>", LetterWidth), body, surface,
                orbit, air, noAir, remove);
            row.OptionEnabledCondition = () => Shown(index);
            yield return row;

            // The orbit's altitude, and who flies there where the vessel carries a
            // lander.
            DialogGUIHorizontalLayout altitude = new DialogGUIHorizontalLayout(false, false, 4f, new RectOffset(),
                TextAnchor.MiddleLeft, new DialogGUIButton("-", () => StepAltitude(index, -1), 22f, RowHeight, false),
                new DialogGUILabel(() => AltitudeText(index), 64f),
                new DialogGUIButton("+", () => StepAltitude(index, 1), 22f, RowHeight, false));
            altitude.OptionEnabledCondition = () => Shown(index) && Kind(index) == StopKind.Orbit;
            DialogGUIButton ship = Choice("Ship", () => VehicleOf(index) == Vehicle.Ship, () => SetVehicle(index, Vehicle.Ship), 44f);
            DialogGUIButton byLander = Choice("Lander", () => VehicleOf(index) == Vehicle.Lander,
                () => SetVehicle(index, Vehicle.Lander), 56f);
            ship.OptionEnabledCondition = () => index > 0 && HasLander();
            byLander.OptionEnabledCondition = () => index > 0 && HasLander();
            DialogGUIHorizontalLayout second = new DialogGUIHorizontalLayout(width, RowHeight + 2f, 4f, new RectOffset(),
                TextAnchor.MiddleLeft, TimelineLine(RowHeight + 2f), new DialogGUISpace(LetterWidth), altitude,
                new DialogGUIFlexibleSpace(), ship, byLander, new DialogGUISpace(Icon));
            second.OptionEnabledCondition = () => Shown(index) && (Kind(index) == StopKind.Orbit || (index > 0 && HasLander()));
            yield return second;
        }

        private static DialogGUIBase TimelineLine(float height)
        {
            return new DialogGUIHorizontalLayout(Timeline, height, 0f, new RectOffset(9, 0, 0, 0), TextAnchor.MiddleLeft,
                PlannerWidgets.Block(2f, height, PlannerColors.Line), new DialogGUISpace(Timeline - 11f));
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

        // ----------------------------------------------------------------- steps

        // A step between destination `leg` and the next: its bar and delta-v, the
        // choices it offers, and what stands in its way.
        private IEnumerable<DialogGUIBase> StepRows(int leg, StepRole role)
        {
            SegmentBar bar = new SegmentBar(MaxPieces, BarHeight, after);
            stepBars[leg, (int)role] = bar;
            PlannerWidgets.Clickable(bar.Layout, () => SelectLeg(leg), after);

            DialogGUIButton speed = new DialogGUIButton(() => StepSpeed(leg, role), () => SelectLeg(leg), Speed, RowHeight, false);
            DialogOverWindow.ClearBackground(speed);
            DialogGUILabel who = new DialogGUILabel(() => "<color=" + Dim + ">" + StepName(Slot(leg, role)) + "</color>", StepLabel);
            who.OptionEnabledCondition = HasLander;

            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, Icon + 2f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                TimelineLine(Icon + 2f), who, bar.Layout, new DialogGUIFlexibleSpace(), speed);
            row.OptionEnabledCondition = () => Slot(leg, role) >= 0;
            yield return row;

            int goal = leg + 1;
            List<DialogGUIBase> options = new List<DialogGUIBase> { TimelineLine(RowHeight + 4f), new DialogGUISpace(StepLabel) };
            if (role == StepRole.Approach)
            {
                // Straight into the air above the destination, or into an orbit
                // first, for a landing on parachutes.
                options.Add(Choice("Direct", () => !OrbitFirst(goal), () => SetOrbitFirst(goal, false), 60f));
                options.Add(Choice("Orbit first", () => OrbitFirst(goal), () => SetOrbitFirst(goal, true), 84f));
            }
            else
            {
                // The lander away comes back and docks, or stays; docked, it is
                // refilled from the rest.
                DialogGUIButton dock = Choice("Dock", () => !LanderStays(goal), () => SetLanderStays(goal, false), 52f);
                DialogGUIButton stays = Choice("Stays", () => LanderStays(goal), () => SetLanderStays(goal, true), 52f);
                DialogGUIButton refuel = Choice("Refuel", () => Refuels(goal), () => ToggleRefuel(goal), 64f);
                // To a destination of the lander, the lander always docks first.
                dock.OptionEnabledCondition = () => VehicleOf(goal) == Vehicle.Ship;
                stays.OptionEnabledCondition = () => VehicleOf(goal) == Vehicle.Ship;
                refuel.OptionEnabledCondition = () => !LanderStays(goal) || VehicleOf(goal) == Vehicle.Lander;
                options.Add(dock);
                options.Add(stays);
                options.Add(refuel);
            }
            DialogGUIHorizontalLayout choices = new DialogGUIHorizontalLayout(width, RowHeight + 4f, 4f, new RectOffset(),
                TextAnchor.MiddleLeft, options.ToArray());
            choices.OptionEnabledCondition = () => HasChoices(leg, role);
            yield return choices;

            for (int i = 0; i < MaxWarnings; i++)
            {
                int line = i;
                DialogGUIHorizontalLayout warning = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(),
                    TextAnchor.MiddleLeft, TimelineLine(RowHeight), new DialogGUISpace(StepLabel),
                    new DialogGUILabel(() => "<color=" + Problem + ">" + WarningText(leg, role, line) + "</color>",
                        width - Timeline - StepLabel - 8f, RowHeight));
                warning.OptionEnabledCondition = () => WarningText(leg, role, line).Length > 0;
                yield return warning;
            }
        }

        // Who flies the step: the lander back to the rest, the whole vessel, the
        // lander alone, or the rest without it.
        private string StepName(int index)
        {
            if (!Worked() || index < 0) return "";
            RouteStep step = steps[index];
            if (step.Role == StepRole.Return) return "Dock";
            switch (step.Mode)
            {
                case LegMode.Lander: return "Lander";
                case LegMode.WithoutLander: return "Rest";
                default: return "Ship";
            }
        }

        // Only where the choice changes something: the way back while the lander is
        // away, and the way down for a landing on parachutes.
        private bool HasChoices(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            if (index < 0) return false;
            RouteStep step = steps[index];
            if (role == StepRole.Return) return true;
            if (role == StepRole.Approach)
                return step.To.Kind == StopKind.Entry || (step.To.Kind == StopKind.Orbit && OrbitFirst(leg + 1));
            // The lander stayed: no step back to choose it on.
            return step.LanderAway && Slot(leg, StepRole.Return) < 0;
        }

        private string WarningText(int leg, StepRole role, int line)
        {
            int index = Slot(leg, role);
            if (index < 0 || index >= warnings.Length || warnings[index] == null || line >= warnings[index].Count) return "";
            return warnings[index][line];
        }

        private string StepSpeed(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            if (index < 0) return "";
            string speed = EngineerFormat.Speed(budget.Needed[index]);
            if (warnings[index].Count > 0) speed = "<color=" + Problem + ">" + speed + "</color>";
            else if (selectedLeg == leg) speed = "<b><color=" + Good + ">" + speed + "</color></b>";
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
                header.OptionEnabledCondition = () => Ready() && at < Legs;
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
                cell.OptionEnabledCondition = () => Ready() && at < Legs;
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
            if (!Worked() || stage >= stages.Count || leg >= Legs) return "";
            double used = 0.0;
            foreach (StageBudget.StageUse use in budget.Uses)
                if (steps[use.Leg].Segment == leg && use.Stage == stages[stage].Number) used += use.DeltaV;
            bool pinned = IsPinned(stage, leg);
            if (used < 0.5) return pinned ? "<color=" + Warning + ">0</color>" : "";
            string colour = PlannerColors.Hex(PlannerColors.Stage(stage));
            string figure = cellWidth >= 72f ? EngineerFormat.Speed(used) : Short(used);
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

        // The selected leg's or stage's details, one line to a row of a fixed
        // height: a label of several lines whose text changes is not given its
        // height by KSP's layout, and ran over the rows below it.
        private const int MaxDetailLines = 40;

        private IEnumerable<DialogGUIBase> DetailRows()
        {
            DialogGUIBase heading = new DialogGUIHorizontalLayout(width, RowHeight + 8f, 0f, new RectOffset(0, 0, 8, 0),
                TextAnchor.LowerLeft, new DialogGUILabel(() => "<b>" + detailTitle + "</b>", width, RowHeight));
            heading.OptionEnabledCondition = () => detailTitle.Length > 0;
            yield return heading;
            for (int i = 0; i < MaxDetailLines; i++)
            {
                int line = i;
                DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                    new DialogGUILabel(() => line < detailLines.Length ? detailLines[line] : "", width, RowHeight));
                row.OptionEnabledCondition = () => line < detailLines.Length;
                yield return row;
            }
        }

        private void RefreshDetail()
        {
            string text;
            if (selectedStage >= 0)
            {
                detailTitle = StageTitle();
                text = StageDetail();
            }
            else if (selectedLeg >= 0)
            {
                detailTitle = LegTitle(selectedLeg);
                text = LegDetail(selectedLeg);
            }
            else
            {
                detailTitle = "";
                text = "";
            }
            detailLines = text.Length > 0 ? text.Split('\n') : new string[0];
            if (detailLines.Length > MaxDetailLines) System.Array.Resize(ref detailLines, MaxDetailLines);
        }

        private string LegTitle(int leg)
        {
            if (!Ready() || leg + 1 >= state.Stops.Count) return "";
            return Letter(leg) + " " + BodyName(leg) + "  to  " + Letter(leg + 1) + " " + BodyName(leg + 1);
        }

        // Each step of the leg: who flies where, its burns, the margin, the stages
        // that pay for it with their thrust against the weight at that body, and
        // what stands in its way.
        private string LegDetail(int leg)
        {
            if (!Worked() || leg >= Legs) return "";
            StringBuilder text = new StringBuilder();
            foreach (StepRole role in new[] { StepRole.Return, StepRole.Approach, StepRole.Main })
            {
                int index = Slot(leg, role);
                if (index < 0) continue;
                RouteStep step = steps[index];
                if (text.Length > 0) text.Append('\n');
                text.Append("<b>").Append(StepTitle(step)).Append("</b>");
                foreach (LegLine line in step.Leg.Lines)
                    text.Append('\n').Append(line.Name).Append("<pos=60%>").Append(EngineerFormat.Speed(line.DeltaV));
                if (state.Margin > 0.0 && step.Leg.DeltaV > 0.5)
                    text.Append("\n<color=" + Dim + ">Margin ").Append((state.Margin * 100.0).ToString("0")).Append(" %</color><pos=60%>")
                        .Append(EngineerFormat.Speed(budget.Needed[index] - step.Leg.DeltaV));
                int body = step.From.Kind == StopKind.Surface ? step.From.Body : step.To.Body;
                foreach (StageBudget.StageUse use in budget.Uses)
                {
                    if (use.Leg != index) continue;
                    text.Append('\n').Append(ColouredStage(use.Stage));
                    if (state.IsPinned(use.Stage, leg)) text.Append(" <color=" + Dim + ">pinned</color>");
                    text.Append("<pos=36%><color=" + Dim + ">TWR ").Append(EngineerFormat.Ratio(Twr(use, body))).Append("</color>")
                        .Append("<pos=60%>").Append(EngineerFormat.Speed(use.DeltaV));
                }
                if (budget.Refuelled[index] > 1e-3)
                    text.Append("\n<color=" + Good + ">Refuelled from the rest<pos=60%>")
                        .Append(budget.Refuelled[index].ToString("0.00", CultureInfo.InvariantCulture)).Append(" t</color>");
                foreach (string warning in warnings[index])
                    text.Append("\n<color=" + Problem + ">").Append(warning).Append("</color>");
            }
            return text.ToString();
        }

        // "Lander to D Mun", "Ship into Kerbin's air".
        private string StepTitle(RouteStep step)
        {
            string body = bodies.Bodies[step.To.Body].Name;
            if (step.Role == StepRole.Return) return "Lander back to the rest, " + body;
            if (step.Role == StepRole.Approach)
                return step.To.Kind == StopKind.Entry ? "Ship into the air of " + body : "Ship to " + body + " orbit";
            string who = step.Mode == LegMode.Lander ? "Lander" : step.Mode == LegMode.WithoutLander ? "Rest" : "Ship";
            return who + " to " + Letter(step.Goal) + " " + body;
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
            // One engine type to a line.
            if (row.Engines.Length > 0) text.Append("<color=" + Dim + ">").Append(row.Engines.Replace(", ", "</color>\n<color=" + Dim + ">"))
                .Append("</color>");
            foreach (StageBudget.StageUse use in budget.Uses)
                if (use.Stage == row.Number)
                    text.Append('\n').Append(LegName(steps[use.Leg].Segment)).Append(" <color=" + Dim + ">")
                        .Append(StepName(use.Leg)).Append("</color><pos=60%>").Append(EngineerFormat.Speed(use.DeltaV));
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
            // lander, who flies, with the gaps between them, and a seam of one
            // unit between the bar's pieces.
            float room = Mathf.Max(40f, width - Timeline - Speed - (HasLander() ? StepLabel + 4f : 0f) - 4f * 3f - 8f);
            double longest = 1.0;
            foreach (double value in budget.Needed) if (value > longest) longest = value;
            for (int leg = 0; leg < MaxStops; leg++)
                for (int role = 0; role < 3; role++)
                {
                    SegmentBar bar = stepBars[leg, role];
                    if (bar == null) continue;
                    List<KeyValuePair<float, Color>> pieces = new List<KeyValuePair<float, Color>>();
                    int index = Slot(leg, (StepRole)role);
                    if (index >= 0)
                    {
                        int count = budget.Uses.FindAll(use => use.Leg == index).Count + (budget.Short[index] > 0.5 ? 1 : 0);
                        float barRoom = Mathf.Max(20f, room - count);
                        foreach (StageBudget.StageUse use in budget.Uses)
                            if (use.Leg == index) pieces.Add(Piece(use.DeltaV / longest * barRoom, StageColour(use.Stage)));
                        if (budget.Short[index] > 0.5) pieces.Add(Piece(budget.Short[index] / longest * barRoom, PlannerColors.Short));
                    }
                    bar.Set(pieces);
                }

            // The fuel plan's columns share what the stage and left columns
            // leave, over the legs there are.
            int shown = Mathf.Max(1, Legs);
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
