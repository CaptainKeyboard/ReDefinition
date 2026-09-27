using System.Collections.Generic;
using System.Text;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The planner's rows, built from KSP's dialog elements like the engineer tab,
    // so a UI theme reaches them as it reaches KSP's dialogs. The route reads
    // top down as a line of stops, a bar between each two for the leg: its
    // length is the leg's delta-v, its colours the stages that pay for it, red
    // what none covers. Choices are symbols on KSP's own buttons. A fixed number
    // of rows for stops and stages, shown as the plan needs them.
    internal sealed partial class PlannerView
    {
        private const int MaxStops = 12;
        private const int MaxStages = 40;
        private const int MaxChips = 24;
        private const int MaxPieces = 24;
        private const float Icon = 24f;
        private const float Timeline = 20f;
        private const float LetterWidth = 14f;
        private const float Speed = 78f;
        private const float BarHeight = 10f;
        private const float RowHeight = 20f;
        private const string Warning = "#f18a24";
        private const string Good = "#8fdc7a";
        private const string Dim = "#9a9a9a";

        private readonly SegmentBar[] legBars = new SegmentBar[MaxStops];
        private SegmentBar totalBar;

        internal List<DialogGUIBase> Rows()
        {
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
            rows.Add(Heading("Stages"));
            for (int i = 0; i < MaxStages; i++) rows.AddRange(StageRows(i));
            return rows;
        }

        // ----------------------------------------------------------------- stops

        private IEnumerable<DialogGUIBase> StopRows(int index)
        {
            DialogGUIHorizontalLayout dot = new DialogGUIHorizontalLayout(Timeline, Icon, 0f, new RectOffset(4, 0, 0, 0),
                TextAnchor.MiddleLeft, PlannerWidgets.Icon("Dot", 12f, PlannerColors.Line));

            float bodyWidth = width - Timeline - LetterWidth - Icon * 4f - 4f * 6f - 8f;
            DialogGUIButton body = new DialogGUIButton(() => "<b>" + BodyName(index) + "</b>", () => PickBody(index), bodyWidth,
                Icon, false);

            DialogGUIToggleButton surface = PlannerWidgets.IconToggle("Surface", () => Kind(index) == StopKind.Surface,
                on => { if (on) SetKind(index, StopKind.Surface); }, Icon, "Surface", after);
            DialogGUIToggleButton orbit = PlannerWidgets.IconToggle("Orbit", () => Kind(index) == StopKind.Orbit,
                on => { if (on) SetKind(index, StopKind.Orbit); }, Icon, "Orbit", after);
            DialogGUIToggleButton chute = PlannerWidgets.IconToggle("Chute", () => UsesAtmosphere(index),
                on => SetAtmosphere(index, on), Icon, "Parachutes", after);
            chute.OptionEnabledCondition = () => index > 0 && HasAtmosphere(index) && Kind(index) == StopKind.Surface;
            DialogGUIToggleButton aerobrake = PlannerWidgets.IconToggle("Aerobrake", () => UsesAtmosphere(index),
                on => SetAtmosphere(index, on), Icon, "Aerobrake", after);
            aerobrake.OptionEnabledCondition = () => index > 0 && HasAtmosphere(index) && Kind(index) == StopKind.Orbit;
            DialogGUIButton remove = PlannerWidgets.IconButton("Remove", () => RemoveStop(index), Icon, "Remove", after);
            remove.OptionEnabledCondition = () => Ready() && state.Stops.Count > 2;

            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, Icon + 2f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                dot, new DialogGUILabel(() => "<b>" + Letter(index) + "</b>", LetterWidth), body, surface, orbit, chute, aerobrake,
                new DialogGUIFlexibleSpace(), remove);
            row.OptionEnabledCondition = () => Shown(index);
            yield return row;

            DialogGUIHorizontalLayout altitude = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                TimelineLine(RowHeight),
                new DialogGUISpace(LetterWidth),
                new DialogGUIButton("-", () => StepAltitude(index, -1), 20f, RowHeight, false),
                new DialogGUILabel(() => AltitudeText(index), 64f),
                new DialogGUIButton("+", () => StepAltitude(index, 1), 20f, RowHeight, false));
            altitude.OptionEnabledCondition = () => Shown(index) && Kind(index) == StopKind.Orbit;
            yield return altitude;
        }

        private static DialogGUIBase TimelineLine(float height)
        {
            return new DialogGUIHorizontalLayout(Timeline, height, 0f, new RectOffset(9, 0, 0, 0), TextAnchor.MiddleLeft,
                PlannerWidgets.Block(2f, height, PlannerColors.Line));
        }

        // ------------------------------------------------------------------ legs

        private IEnumerable<DialogGUIBase> LegRows(int index)
        {
            SegmentBar bar = new SegmentBar(MaxPieces, BarHeight, after);
            legBars[index] = bar;
            PlannerWidgets.Clickable(bar.Layout, () => ToggleLeg(index), after);

            DialogGUIButton speed = new DialogGUIButton(() => LegSpeed(index), () => ToggleLeg(index), Speed, RowHeight, false);
            DialogOverWindow.ClearBackground(speed);

            DialogGUIToggleButton whole = ModeToggle(index, LegMode.Whole, "Whole", "Whole vessel");
            DialogGUIToggleButton alone = ModeToggle(index, LegMode.Lander, "Lander", "Lander alone");
            DialogGUIToggleButton rest = ModeToggle(index, LegMode.WithoutLander, "Rest", "Without the lander");

            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, Icon + 2f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                TimelineLine(Icon + 2f), bar.Layout, new DialogGUIFlexibleSpace(), speed, whole, alone, rest);
            row.OptionEnabledCondition = () => Ready() && index + 1 < state.Stops.Count;
            yield return row;

            DialogGUIHorizontalLayout detail = new DialogGUIHorizontalLayout(width, -1f, 4f, new RectOffset(), TextAnchor.UpperLeft,
                TimelineLine(RowHeight),
                new DialogGUIVerticalLayout(width - Timeline - 4f, -1f, 2f, new RectOffset(0, 0, 0, 4), TextAnchor.UpperLeft,
                    new DialogGUILabel(() => Ready() && index < legDetails.Length ? legDetails[index] : "", true, true),
                    ChipRow(index)));
            detail.OptionEnabledCondition = () => Ready() && index + 1 < state.Stops.Count && openLegs.Contains(index);
            yield return detail;
        }

        private DialogGUIToggleButton ModeToggle(int leg, LegMode mode, string icon, string tip)
        {
            DialogGUIToggleButton toggle = PlannerWidgets.IconToggle(icon, () => Mode(leg) == mode,
                on => { if (on) SetMode(leg, mode); }, Icon, tip, after);
            toggle.OptionEnabledCondition = HasLander;
            return toggle;
        }

        // The stages as chips under a pin: one pressed is kept for this leg.
        private DialogGUIBase ChipRow(int leg)
        {
            List<DialogGUIBase> chips = new List<DialogGUIBase>();
            for (int i = 0; i < MaxChips; i++)
            {
                int stage = i;
                DialogGUIToggleButton chip = new DialogGUIToggleButton(() => IsKept(stage, leg), "",
                    on => SetKept(stage, leg, on), 40f, RowHeight);
                // KSP gives a toggle button a label only for a fixed text, and
                // setLabel reaches a text it does not create for one
                // (DialogGUIToggleButton.Create, decompiled): the stage's name
                // is a label of its own, laid out as KSP lays out that one.
                chip.children.Add(new DialogGUIHorizontalLayout(true, true, 0f, new RectOffset(), TextAnchor.MiddleCenter,
                    new DialogGUILabel(() => "<align=center>" + StageName(stage) + "</align>", true, false)));
                chip.tooltipText = "Keep for this leg";
                chip.OptionEnabledCondition = () => stage < stages.Count;
                chips.Add(chip);
            }
            int columns = Mathf.Max(1, (int)((width - Timeline - 30f) / 43f));
            DialogGUIGridLayout grid = new DialogGUIGridLayout(new RectOffset(), new Vector2(40f, RowHeight), new Vector2(3f, 3f),
                UnityEngine.UI.GridLayoutGroup.Corner.UpperLeft, UnityEngine.UI.GridLayoutGroup.Axis.Horizontal, TextAnchor.UpperLeft,
                UnityEngine.UI.GridLayoutGroup.Constraint.FixedColumnCount, columns, chips.ToArray());
            return new DialogGUIHorizontalLayout(width - Timeline - 4f, -1f, 6f, new RectOffset(), TextAnchor.UpperLeft,
                PlannerWidgets.Icon("Pin", 18f, new Color(0.75f, 0.75f, 0.78f)), grid);
        }

        private string LegSpeed(int index)
        {
            if (!Worked() || index >= legs.Count) return "";
            string speed = EngineerFormat.Speed(budget.Needed[index]);
            bool warn = budget.Short[index] > 0.5 || (index < meetings.Length && meetings[index] >= 0);
            if (warn) speed = "<color=" + Warning + ">" + speed + "</color>";
            else if (selectedLeg == index) speed = "<color=" + Good + ">" + speed + "</color>";
            return speed;
        }

        private string LegDetail(int index)
        {
            if (!Worked() || index >= legs.Count) return "";
            StringBuilder text = new StringBuilder();
            foreach (LegLine line in legs[index].Lines)
            {
                if (text.Length > 0) text.Append('\n');
                text.Append(line.Name).Append("<pos=66%>").Append(EngineerFormat.Speed(line.DeltaV));
            }
            if (state.Margin > 0.0)
                text.Append("\n<color=" + Dim + ">+ ").Append((state.Margin * 100.0).ToString("0")).Append(" %</color><pos=66%>")
                    .Append(EngineerFormat.Speed(budget.Needed[index] - legs[index].DeltaV));
            foreach (StageBudget.StageUse use in budget.Uses)
            {
                if (use.Leg != index) continue;
                text.Append('\n').Append(ColouredStage(use.Stage)).Append("<pos=66%>").Append(EngineerFormat.Speed(use.DeltaV));
            }
            if (budget.Short[index] > 0.5)
                text.Append("\n<color=" + Warning + ">Short</color><pos=66%><color=" + Warning + ">")
                    .Append(EngineerFormat.Speed(budget.Short[index])).Append("</color>");
            if (index < meetings.Length && meetings[index] >= 0)
                text.Append("\n<color=" + Warning + ">The rest waits at ").Append(Letter(meetings[index])).Append("</color>");
            foreach (string warning in ThrustWarnings(index))
                text.Append("\n<color=" + Warning + ">").Append(warning).Append("</color>");
            return text.ToString();
        }

        // --------------------------------------------------------------- summary

        private IEnumerable<DialogGUIBase> SummaryRows()
        {
            yield return new DialogGUISpace(6f);
            totalBar = new SegmentBar(MaxStages + 2, BarHeight + 4f, after);
            yield return new DialogGUIHorizontalLayout(width, BarHeight + 6f, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                totalBar.Layout, new DialogGUIFlexibleSpace());
            yield return new DialogGUILabel(SummaryText, true, true);

            DialogGUISlider margin = new DialogGUISlider(() => (float)(Ready() ? state.Margin * 100.0 : 10.0), 0f, 50f, true,
                width - 130f, RowHeight, SetMargin);
            yield return new DialogGUIHorizontalLayout(width, RowHeight + 2f, 6f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel("Margin", 60f), margin,
                new DialogGUILabel(() => (Ready() ? (state.Margin * 100.0).ToString("0") : "10") + " %", 50f));
            yield return new DialogGUIToggle(() => Ready() && state.PlaneChanges, "Plane changes", SetPlaneChanges, width);
        }

        private string SummaryText()
        {
            if (!Worked()) return "";
            double needed = TotalNeeded();
            double has = TotalHas();
            string balance = budget.TotalShort > 0.5
                ? "<color=" + Warning + ">-" + EngineerFormat.Speed(budget.TotalShort) + "</color>"
                : "<color=" + Good + ">+" + EngineerFormat.Speed(budget.TotalLeft) + "</color>";
            return "<b>" + balance + "</b><pos=40%>" + EngineerFormat.Speed(needed) + " <color=" + Dim + ">of</color> "
                   + EngineerFormat.Speed(has);
        }

        // ---------------------------------------------------------------- stages

        private IEnumerable<DialogGUIBase> StageRows(int index)
        {
            DialogGUIImage swatch = PlannerWidgets.Block(8f, RowHeight - 4f, PlannerColors.Stage(index));
            DialogGUIButton name = new DialogGUIButton(() => StageLabel(index), () => SelectStage(index), 44f, RowHeight, false);
            DialogOverWindow.ClearBackground(name);
            DialogGUIToggleButton landerToggle = PlannerWidgets.IconToggle("Lander",
                () => index < stages.Count && IsLanderStage(stages[index].Number), on => SetLander(index, on), Icon, "Lander",
                after);
            landerToggle.OptionEnabledCondition = () => index < stages.Count && landerChoices.Contains(stages[index].Number);

            float fixedWidth = 8f + 44f + 80f + Icon + 4f * 5f;
            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, Icon + 2f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                swatch, name,
                new DialogGUILabel(() => index < stages.Count ? EngineerFormat.Speed(stages[index].DeltaVVacuum) : "", 80f),
                new DialogGUILabel(() => StageState(index), width - fixedWidth),
                landerToggle);
            row.OptionEnabledCondition = () => index < stages.Count;
            yield return row;

            DialogGUIHorizontalLayout engines = new DialogGUIHorizontalLayout(width, -1f, 0f, new RectOffset(16, 0, 0, 2),
                TextAnchor.UpperLeft,
                new DialogGUILabel(() => index < stages.Count ? "<color=" + Dim + ">" + stages[index].Engines + "</color>" : "",
                    true, true));
            engines.OptionEnabledCondition = () => index < stages.Count && stages[index].Engines.Length > 0;
            yield return engines;
        }

        private string StageName(int index)
        {
            return index < stages.Count ? ColouredStage(stages[index].Number) : "";
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
            if (budget.LeftBehind.Contains(number)) return "<color=" + Dim + ">left behind</color>";
            if (budget.Dropped.TryGetValue(number, out value))
                return "<color=" + Dim + ">dropped, " + EngineerFormat.Speed(value) + "</color>";
            if (budget.Left.TryGetValue(number, out value) && value > 0.5)
                return "<color=" + Good + ">" + EngineerFormat.Speed(value) + " left</color>";
            bool used = budget.Uses.Exists(use => use.Stage == number);
            return used ? "<color=" + Dim + ">empty</color>" : "";
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
            float room = Mathf.Max(40f, width - Timeline - Speed - 4f * 4f - (HasLander() ? (Icon + 4f) * 3f : 0f));
            double longest = 1.0;
            foreach (double value in budget.Needed) if (value > longest) longest = value;
            for (int leg = 0; leg < MaxStops; leg++)
            {
                if (legBars[leg] == null) continue;
                List<KeyValuePair<float, Color>> pieces = new List<KeyValuePair<float, Color>>();
                if (leg < legs.Count)
                {
                    foreach (StageBudget.StageUse use in budget.Uses)
                        if (use.Leg == leg) pieces.Add(Piece(use.DeltaV / longest * room, StageColour(use.Stage)));
                    if (budget.Short[leg] > 0.5) pieces.Add(Piece(budget.Short[leg] / longest * room, PlannerColors.Short));
                }
                legBars[leg].Set(pieces);
            }

            if (totalBar == null) return;
            // What each stage burns, what is left, what is missing: scaled to
            // fit the row with the gaps between the pieces.
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

        private static KeyValuePair<float, Color> Piece(double width, Color color)
        {
            return new KeyValuePair<float, Color>((float)width, color);
        }

        private Color StageColour(int number)
        {
            int at = stages.FindIndex(row => row.Number == number);
            return at >= 0 ? PlannerColors.Stage(at) : PlannerColors.Spare;
        }

        private DialogGUIBase Heading(string title)
        {
            return new DialogGUIHorizontalLayout(width, RowHeight + 6f, 0f, new RectOffset(0, 0, 6, 0), TextAnchor.LowerLeft,
                new DialogGUILabel("<b><color=#e0e0e0>" + title + "</color></b>", width, RowHeight));
        }
    }
}
