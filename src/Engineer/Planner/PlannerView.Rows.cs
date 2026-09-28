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
        private const int MaxStops = 11;
        private const int MaxStages = 30;
        private const int MaxPieces = 24;
        private const float Icon = 24f;
        private const float Timeline = 20f;
        private const float LetterWidth = 14f;
        // Every button's height, and the size of the square ones: the cross, "..." and +.
        private const float Square = RowHeight + 2f;
        // What a stop's and a step's rows leave free at their right end.
        private const float RowEnd = 2f;
        // The line between a stop's Surface and Orbit and a further choice.
        private const float Divider = 1f;
        private const float Speed = 96f;
        private const float BarHeight = 10f;
        private const float RowHeight = 20f;
        private const float Cell = 28f;
        // The swatch, the stage's name and the lander's and the capsule's buttons,
        // with the gaps between them.
        private const float StageColumn = 98f;
        private const int MaxTail = 4;
        private const float LeftColumn = 56f;
        // Where a step goes, and the room left of a bar.
        private const float Place = 140f;
        private const float VehicleButton = 84f;
        private const float StepSpeed_ = 72f;
        private const float OptionLabel = 84f;
        private const float BarIndent = 16f;
        private const int MaxWarnings = 3;
        private const string Warning = "#f18a24";
        private const string Problem = "#e6474d";
        private const string Good = "#8fdc7a";
        private const string Dim = "#9a9a9a";

        private readonly SegmentBar[,] stepBars = new SegmentBar[MaxStops, 3];
        private readonly List<DialogGUITextInput> altitudeFields = new List<DialogGUITextInput>();
        private static readonly StepRole[] Roles = { StepRole.Launch, StepRole.Transfer, StepRole.Land };
        // The steps whose further choices are open ("...").
        private readonly bool[,] more = new bool[MaxStops, 3];
        // After each step's bar, the room its bar leaves, so that the figure stands in
        // one column: the scroll list stretches its rows under its bar, and a flexible
        // space would push the figure there.
        private readonly Sizer[,] barFillers = new Sizer[MaxStops, 3];
        private SegmentBar totalBar;
        private readonly IconMark[,] pinMarks = new IconMark[MaxStages, MaxStops];
        private Sizer columns;
        private float cellWidth = Cell;

        internal List<DialogGUIBase> Rows()
        {
            columns = new Sizer(after);
            altitudeFields.Clear();
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            rows.Add(Heading("Route"));
            for (int i = 0; i < MaxStops; i++)
            {
                rows.AddRange(StopRows(i));
                foreach (StepRole role in Roles)
                    rows.AddRange(StepBlock(i, role));
            }
            rows.Add(AddRow());

            rows.AddRange(SummaryRows());
            rows.Add(Heading("Fuel plan"));
            rows.Add(PlanHeader());
            for (int i = 0; i < MaxStages; i++) rows.Add(PlanRow(i));
            for (int i = 0; i < MaxTail; i++) rows.Add(TailRow(i));
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
            // The body's button ends where a step's place does, so that Surface stands
            // over who flies a step; the room the others leave stands after the
            // choices, so that the cross ends where a step's "..." does: 2 units short
            // of the rows' room. Nine elements and eight gaps with the atmosphere's
            // choice and the line before it, set off from Surface and Orbit.
            const float bodyWidth = Place - LetterWidth - 4f;
            float room = Mathf.Max(0f, width - Timeline - LetterWidth - bodyWidth - 62f - 52f - Divider - 76f - Square - 4f * 8f
                                       - RowEnd);
            DialogGUIButton body = new DialogGUIButton(() => "<b>" + BodyName(index) + "</b>", () => PickBody(index), bodyWidth,
                RowHeight + 2f, false);
            bodyButtons[index] = body;
            body.tooltipText = "Choose the body of this destination.";
            DialogGUIButton surface = Choice("Surface", () => Kind(index) == StopKind.Surface, () => SetKind(index, StopKind.Surface),
                62f);
            surface.tooltipText = "Land on the body's surface.";
            DialogGUIButton orbit = Choice("Orbit", () => Kind(index) == StopKind.Orbit, () => SetKind(index, StopKind.Orbit), 52f);
            orbit.tooltipText = "Stay in orbit around the body.";
            DialogGUIButton air = Choice(() => Kind(index) == StopKind.Surface ? "Chutes" : "Aerobrake", () => UsesAtmosphere(index),
                () => ToggleAtmosphere(index), 76f);
            air.OptionEnabledCondition = () => index > 0 && HasAtmosphere(index);
            air.tooltipText = "Use the atmosphere: parachutes to land, or aerobraking into orbit, instead of the engines.";
            DialogGUIBase divider = PlannerWidgets.Block(Divider, RowHeight - 4f, PlannerColors.Line);
            divider.OptionEnabledCondition = air.OptionEnabledCondition;
            // Its room and the line's where it is hidden, so that the cross stands in
            // one column on every stop.
            DialogGUISpace noAir = new DialogGUISpace(Divider + 4f + 76f);
            noAir.OptionEnabledCondition = () => !(index > 0 && HasAtmosphere(index));
            DialogGUIButton remove = PlannerWidgets.IconButton("Remove", () => RemoveStop(index), Square, Square, 14f,
                "Remove this destination.", after);
            remove.OptionEnabledCondition = () => Ready() && state.Stops.Count > 2;

            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, Icon + 4f, 4f, new RectOffset(0, 0, 2, 0),
                TextAnchor.MiddleLeft, dot, new DialogGUILabel(() => "<b>" + Letter(index) + "</b>", LetterWidth), body, surface,
                orbit, divider, air, noAir, new DialogGUISpace(room), remove);
            row.OptionEnabledCondition = () => Shown(index);
            yield return row;

            // The orbit's altitude: typed in kilometres, or stepped by 10 km.
            DialogGUITextInput typed = new DialogGUITextInput("", false, 7, text =>
            {
                SetAltitude(index, text);
                return text;
            }, () => AltitudeKm(index), TMPro.TMP_InputField.ContentType.DecimalNumber, Square);
            typed.size = new Vector2(72f, Square);
            DialogGUIButton lower = new DialogGUIButton("-", () => StepAltitude(index, -1), Square, Square, false);
            lower.tooltipText = "Lower the orbit by 10 km.";
            DialogGUIButton raise = new DialogGUIButton("+", () => StepAltitude(index, 1), Square, Square, false);
            raise.tooltipText = "Raise the orbit by 10 km.";
            altitudeFields.Add(typed);
            DialogGUIHorizontalLayout altitude = new DialogGUIHorizontalLayout(width, RowHeight + 2f, 4f, new RectOffset(),
                TextAnchor.MiddleLeft, TimelineLine(RowHeight + 2f), new DialogGUISpace(LetterWidth),
                lower, typed, new DialogGUILabel("<color=" + Dim + ">km</color>", 24f), raise);
            altitude.OptionEnabledCondition = () => Shown(index) && Kind(index) == StopKind.Orbit;
            yield return altitude;
        }

        // After the last stop: its dot, the next letter and "+" to add it there.
        private DialogGUIBase AddRow()
        {
            DialogGUIHorizontalLayout dot = new DialogGUIHorizontalLayout(Timeline, Icon, 0f, new RectOffset(4, 0, 0, 0),
                TextAnchor.MiddleLeft, PlannerWidgets.Icon("Dot", 12f, PlannerColors.Line), new DialogGUISpace(Timeline - 16f));
            DialogGUIButton add = new DialogGUIButton("<b>+</b>", AddStop, Square, Square, false);
            add.tooltipText = "Add a destination at the end of the route.";
            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, Icon + 4f, 4f, new RectOffset(0, 0, 2, 0),
                TextAnchor.MiddleLeft, dot,
                new DialogGUILabel(() => "<b><color=" + Dim + ">" + (Ready() ? Letter(state.Stops.Count) : "") + "</color></b>", LetterWidth),
                add);
            row.OptionEnabledCondition = () => Ready() && state.Stops.Count < MaxStops;
            return row;
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

        // A step's rows on a lighter background, right of the timeline, with a gap before
        // it through which the timeline runs on: one block per step between two
        // destinations.
        private IEnumerable<DialogGUIBase> StepBlock(int leg, StepRole role)
        {
            System.Func<bool> shown = () => Slot(leg, role) >= 0;
            DialogGUIHorizontalLayout gap = new DialogGUIHorizontalLayout(width, 4f, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                TimelineLine(4f));
            gap.OptionEnabledCondition = shown;
            yield return gap;
            List<DialogGUIBase> inner = new List<DialogGUIBase>(StepRows(leg, role));
            DialogGUIVerticalLayout block = new DialogGUIVerticalLayout(width, -1f, 0f, new RectOffset(), TextAnchor.UpperLeft,
                inner.ToArray());
            block.OptionEnabledCondition = shown;
            Tint.Behind(block, PlannerColors.StepBackground, Timeline, width, after);
            yield return block;
        }

        // A step on the way from destination `leg` to the next, on one line in fixed
        // columns: where it goes, who flies it (a button opening the list of who can),
        // its bar, its delta-v and "..." for its further choices. Around it only where
        // there is something: a grey line where something leaves at its start or the
        // lander docks at its end, the further choices while "..." is open, and in red
        // what blocks it.
        private IEnumerable<DialogGUIBase> StepRows(int leg, StepRole role)
        {
            System.Func<bool> shown = () => Slot(leg, role) >= 0;

            DialogGUIHorizontalLayout leaves = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(),
                TextAnchor.MiddleLeft, TimelineLine(RowHeight), new DialogGUISpace(BarIndent),
                new DialogGUILabel(() => "<color=" + Dim + ">" + SeparationText(leg, role) + "</color>",
                    width - Timeline - BarIndent - 8f, RowHeight));
            leaves.OptionEnabledCondition = () => SeparationText(leg, role).Length > 0;
            yield return leaves;

            DialogGUIButton vehicle = null;
            vehicle = new DialogGUIButton(() => VehicleText(leg, role), () => PickVehicle(leg, role, vehicle), VehicleButton,
                RowHeight + 2f, false);
            vehicle.tooltipText = "Select which part of the vessel flies this step.";
            vehicle.OptionEnabledCondition = () => HasLander() || HasCapsule();
            SegmentBar bar = new SegmentBar(MaxPieces, BarHeight, after);
            stepBars[leg, (int)role] = bar;
            PlannerWidgets.Clickable(bar.Layout, () => SelectLeg(leg), after);
            DialogGUIButton speed = new DialogGUIButton(() => StepSpeed(leg, role), () => SelectLeg(leg), StepSpeed_, RowHeight, false);
            DialogOverWindow.ClearBackground(speed);
            DialogGUIButton open = new DialogGUIButton(() => more[leg, (int)role] ? "<b><color=" + Good + ">...</color></b>" : "...",
                () => more[leg, (int)role] = !more[leg, (int)role], Square, Square, false);
            open.tooltipText = "More options for this step.";
            open.OptionEnabledCondition = () => HasMore(leg, role);
            DialogGUISpace noMore = new DialogGUISpace(Square);
            noMore.OptionEnabledCondition = () => !HasMore(leg, role);
            Sizer filler = new Sizer(after);
            barFillers[leg, (int)role] = filler;
            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, Icon + 2f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                TimelineLine(Icon + 2f), new DialogGUILabel(() => StepPlace(leg, role), Place), vehicle, bar.Layout,
                filler.Add(new DialogGUISpace(1f)), speed, open, noMore);
            row.OptionEnabledCondition = shown;
            yield return row;

            DialogGUIButton refuel = Choice("Refuel", () => Refuels(leg, role), () => ToggleRefuel(leg, role), 64f);
            refuel.tooltipText = "Fill the lander's tanks from the rest of the vessel when it docks.";
            DialogGUIHorizontalLayout docks = new DialogGUIHorizontalLayout(width, RowHeight + 4f, 4f, new RectOffset(),
                TextAnchor.MiddleLeft, TimelineLine(RowHeight + 4f), new DialogGUISpace(BarIndent),
                new DialogGUILabel("<color=" + Dim + ">The lander docks with the rest</color>", 200f), refuel);
            docks.OptionEnabledCondition = () => Docks(leg, role);
            yield return docks;

            // Behind "...", each on a line of its own, as they concern different
            // things: the way down, a payload.
            DialogGUIButton straight = Choice("Straight in", () => !OrbitFirst(leg + 1), () => SetOrbitFirst(leg + 1, false), 88f);
            straight.tooltipText = "Go straight into the atmosphere and land from there.";
            DialogGUIButton orbitFirst = Choice("Orbit first", () => OrbitFirst(leg + 1), () => SetOrbitFirst(leg + 1, true), 84f);
            orbitFirst.tooltipText = "Brake into a low orbit first and land from there.";
            yield return OptionLine(leg, role, "Way down", () => IntoTheAir(leg, role), straight, orbitFirst);

            DialogGUIButton payload = Choice("Leave payload", () => LeavesPayload(leg + 1), () => ToggleLeavesPayload(leg + 1), 112f);
            payload.tooltipText = "Leave the payload here, such as a satellite; the stage carrying it flies on lighter.";
            yield return OptionLine(leg, role, "Payload", () => Arrives(leg, role), payload);

            for (int i = 0; i < MaxWarnings; i++)
            {
                int line = i;
                DialogGUIHorizontalLayout warning = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(),
                    TextAnchor.MiddleLeft, TimelineLine(RowHeight), new DialogGUISpace(BarIndent),
                    new DialogGUILabel(() => "<color=" + Problem + ">" + WarningText(leg, role, line) + "</color>",
                        width - Timeline - BarIndent - 8f, RowHeight));
                warning.OptionEnabledCondition = () => WarningText(leg, role, line).Length > 0;
                yield return warning;
            }
        }

        // A group of further choices, with its name, while "..." is open and it
        // concerns the step.
        private DialogGUIBase OptionLine(int leg, StepRole role, string name, System.Func<bool> applies,
                                         params DialogGUIBase[] choices)
        {
            List<DialogGUIBase> elements = new List<DialogGUIBase>
            {
                TimelineLine(RowHeight + 4f), new DialogGUISpace(BarIndent),
                new DialogGUILabel("<color=" + Dim + ">" + name + "</color>", OptionLabel),
            };
            elements.AddRange(choices);
            DialogGUIHorizontalLayout line = new DialogGUIHorizontalLayout(width, RowHeight + 4f, 4f, new RectOffset(),
                TextAnchor.MiddleLeft, elements.ToArray());
            line.OptionEnabledCondition = () => more[leg, (int)role] && Slot(leg, role) >= 0 && applies();
            return line;
        }

        // Who flies the step: green, bold and underlined where the player chose it.
        private string VehicleText(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            if (index < 0) return "";
            string name = VehicleName(steps[index].Vehicle);
            return steps[index].Chosen ? "<b><u><color=" + Good + ">" + name + "</color></u></b>" : "<color=" + Good + ">" + name + "</color>";
        }

        private bool StepChosen(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            return index >= 0 && steps[index].Chosen;
        }

        // The last step to a destination: it arrives there.
        private bool Arrives(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            return index >= 0 && (index + 1 >= steps.Count || steps[index + 1].Goal != steps[index].Goal);
        }

        // "To Mun surface", "To Kerbin's air", "To Kerbin orbit".
        private string StepPlace(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            return index < 0 ? "" : "To " + RouteSteps.Place(bodies.Bodies, steps[index].To);
        }

        private string SeparationText(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            if (index < 0 || !steps[index].Separates) return "";
            string place = RouteSteps.At(bodies.Bodies, steps[index].From);
            switch (steps[index].Vehicle)
            {
                case Vehicle.Lander: return "The lander leaves the rest " + place;
                case Vehicle.Capsule:
                    return (steps[index].From == LanderPlace(index) ? "The capsule leaves the lander " : "The capsule leaves the vessel ") + place;
                default: return "The vessel leaves the lander " + place;
            }
        }

        // Where the lander was when the step started, for the capsule's line: the
        // step before is the lander's.
        private RouteStop LanderPlace(int index)
        {
            return index > 0 && steps[index - 1].Vehicle == Vehicle.Lander ? steps[index - 1].To : null;
        }

        private bool Docks(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            return index >= 0 && steps[index].Docks;
        }

        // The way across ends in the air, for a landing on parachutes, or would
        // without "Orbit first".
        private bool IntoTheAir(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            if (role != StepRole.Transfer || index < 0 || !Shown(leg + 1)) return false;
            RouteStop goal = state.Stops[leg + 1];
            return goal.Kind == StopKind.Surface && goal.UseAtmosphere && bodies.Bodies[goal.Body].HasAtmosphere;
        }

        private bool HasMore(int leg, StepRole role)
        {
            return Slot(leg, role) >= 0 && (IntoTheAir(leg, role) || Arrives(leg, role));
        }

        // Who flies the step, as a name.
        private string StepName(int index)
        {
            if (!Worked() || index < 0) return "";
            return steps[index].Vehicle.ToString();
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
                CapsuleButton(() => index < stages.Count ? stages[index].Number : -1),
            };
            for (int leg = 0; leg < MaxStops - 1; leg++)
            {
                int at = leg;
                DialogGUIButton cell = columns.Add(new DialogGUIButton(() => CellText(index, at), () => TogglePin(index, at), Cell,
                    RowHeight, false));
                cell.tooltipText = "Pin this stage to this leg: only pinned stages burn on it.";
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
            button.tooltipText = "Make this stage and every stage after it the lander.";
            button.OptionInteractableCondition = () => index < stages.Count && landerChoices.Contains(stages[index].Number);
            return button;
        }

        // The capsule's button, on a stage with delta-v or one without.
        private DialogGUIButton CapsuleButton(System.Func<int> number)
        {
            DialogGUIButton button = new DialogGUIButton(() => IsCapsuleStage(number())
                ? "<b><color=" + Good + ">C</color></b>"
                : "<color=" + Dim + ">C</color>", () => ToggleCapsule(number()), 24f, RowHeight, false);
            button.tooltipText = "Make this stage and every stage after it the capsule.";
            button.OptionInteractableCondition = () => CapsuleChoice(number());
            return button;
        }

        // A stage after the last one with delta-v: the capsule can start here.
        private DialogGUIBase TailRow(int index)
        {
            System.Func<int> number = () => index < tailStages.Count ? tailStages[index].Number : -1;
            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, RowHeight + 2f, 2f, new RectOffset(),
                TextAnchor.MiddleLeft, PlannerWidgets.Block(8f, RowHeight - 2f, PlannerColors.Spare),
                new DialogGUILabel(() => "<b>S" + number() + "</b>", 36f), new DialogGUISpace(24f), CapsuleButton(number),
                new DialogGUILabel("<color=" + Dim + ">no delta-v</color>", 120f));
            row.OptionEnabledCondition = () => index < tailStages.Count;
            return row;
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
            foreach (StepRole role in Roles)
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
                if (budget.Payload[index] > 1e-3)
                    text.Append("\n<color=" + Good + ">Payload left here<pos=60%>")
                        .Append(budget.Payload[index].ToString("0.00", CultureInfo.InvariantCulture)).Append(" t</color>");
                if (budget.Refuelled[index] > 1e-3)
                    text.Append("\n<color=" + Good + ">Refuelled from the rest<pos=60%>")
                        .Append(budget.Refuelled[index].ToString("0.00", CultureInfo.InvariantCulture)).Append(" t</color>");
                foreach (string warning in warnings[index])
                    text.Append("\n<color=" + Problem + ">").Append(warning).Append("</color>");
            }
            return text.ToString();
        }

        // "Lander to Mun surface", "Ship to Kerbin's air".
        private string StepTitle(RouteStep step)
        {
            return step.Vehicle + " to " + RouteSteps.Place(bodies.Bodies, step.To);
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
            // What the step's row leaves the bar: the line, the place, who flies, the
            // figure and "...", with the gaps between them, and a seam of one unit
            // between its pieces.
            // Six elements and five gaps besides who flies, to end where a stop's
            // cross does.
            float room = Mathf.Max(40f, width - Timeline - Place - (HasLander() || HasCapsule() ? VehicleButton + 4f : 0f)
                                        - StepSpeed_ - Square - 4f * 5f - RowEnd);
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
                    // The bar's pieces of a unit or more, with a seam of one between them.
                    float used = 0f;
                    int drawn = 0;
                    foreach (KeyValuePair<float, Color> piece in pieces)
                        if (piece.Key >= 1f) { used += piece.Key; drawn++; }
                    used += Mathf.Max(0, drawn - 1);
                    if (barFillers[leg, role] != null) barFillers[leg, role].Set(Mathf.Max(1f, room - used));
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
