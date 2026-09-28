using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The planner's rows, built from KSP's dialog elements, so a UI theme reaches
    // them as it reaches KSP's dialogs. From the top: the vessel's lander and
    // capsule; the route as a line of destinations, and between each two the steps
    // that follow from it, a bar each (its length the step's delta-v, its colours
    // the stages that pay for it, red what none covers), who flies it, the choices
    // it offers, and in red what stands in its way; the fuel for the whole route;
    // and the fuel plan: for the selected step, the stages it has, what each gives
    // and keeps, and whether it burns, is saved or is dropped after the step; with
    // no step selected, what each stage gives over the route and where it ends. A
    // chosen option is lit in its text rather than by the skin's pressed state. A
    // fixed number of rows, shown as the plan needs them.
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
        private const float BarHeight = 10f;
        private const float RowHeight = 20f;
        // The fuel plan's columns: the swatch, the stage, what it gives, what it
        // keeps, and the choices for the selected step.
        private const float Swatch = 8f;
        private const float StageName_ = 32f;
        private const float GivesColumn = 72f;
        private const float LeftColumn = 132f;
        private const float BurnsButton = 56f;
        private const float SaveButton = 48f;
        private const float DropButton = 80f;
        // Where a step goes, and the room left of a bar.
        private const float Place = 145f;
        private const float VehicleButton = 84f;
        private const float StepSpeed_ = 72f;
        // A further choice's name, so that its buttons start under who flies the step.
        private const float OptionLabel = Place - BarIndent - 4f;
        private const float BarIndent = 16f;
        private const int MaxWarnings = 3;
        private const string Warning = "#f18a24";
        private const string Problem = "#e6474d";
        private const string Good = "#8fdc7a";
        private const string Dim = "#9a9a9a";

        private readonly SegmentBar[,] stepBars = new SegmentBar[MaxStops, RouteStop.Roles];
        private readonly List<DialogGUITextInput> altitudeFields = new List<DialogGUITextInput>();
        private static readonly StepRole[] Roles = { StepRole.Launch, StepRole.Transfer, StepRole.Arrive, StepRole.Land };
        // The steps whose further choices are open ("...").
        private readonly bool[,] more = new bool[MaxStops, RouteStop.Roles];
        // After each step's bar, the room its bar leaves, so that the figure stands in
        // one column: the scroll list stretches its rows under its bar, and a flexible
        // space would push the figure there.
        private readonly Sizer[,] barFillers = new Sizer[MaxStops, RouteStop.Roles];
        private SegmentBar totalBar;
        // Each fuel plan row's swatch, in the colour of the stage it shows then.
        private readonly SegmentBar[] stepSwatches = new SegmentBar[MaxStages];
        private readonly SegmentBar[] routeSwatches = new SegmentBar[MaxStages];

        internal List<DialogGUIBase> Rows()
        {
            altitudeFields.Clear();
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            rows.Add(Heading("Vessel"));
            rows.Add(VesselRow("Lander", LanderText, PickLander,
                "Select the part of the vessel that lands alone while the rest waits."));
            rows.Add(VesselRow("Capsule", CapsuleText, PickCapsule,
                "Select the part of the vessel that comes home alone at the end."));
            rows.Add(Heading("Route"));
            for (int i = 0; i < MaxStops; i++)
            {
                rows.AddRange(StopRows(i));
                foreach (StepRole role in Roles)
                    rows.AddRange(StepBlock(i, role));
            }
            rows.Add(AddRow());

            rows.AddRange(SummaryRows());
            rows.AddRange(FuelRows());
            rows.AddRange(DetailRows());
            return rows;
        }

        // ---------------------------------------------------------------- vessel

        // The lander or the capsule: "Automatic" with what the planner finds, none, or
        // a part of the vessel the player picks, from the list under the button. They
        // decide who can fly a step. The name in the column of a step's place, the
        // button in the one of who flies it, to where a stop's cross ends.
        private DialogGUIBase VesselRow(string name, System.Func<string> text, System.Action<DialogGUIBase> pick, string tooltip)
        {
            DialogGUIButton button = null;
            button = new DialogGUIButton(text, () => pick(button), PartButtonWidth, Square, false);
            button.tooltipText = tooltip;
            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, Square + 2f, 4f, new RectOffset(0, 0, 2, 0),
                TextAnchor.MiddleLeft, new DialogGUISpace(Timeline), new DialogGUILabel("<color=" + Dim + ">" + name + "</color>", Place),
                button);
            row.OptionEnabledCondition = Ready;
            return row;
        }

        // The Vessel row's buttons: from who flies a step to where a stop's cross ends.
        private float PartButtonWidth
        {
            get { return Mathf.Max(120f, width - Timeline - Place - 4f * 2f - RowEnd); }
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
            // of the rows' room. Ten elements and nine gaps: Surface and Orbit set off
            // by a line from the body before and from the atmosphere's choice after.
            const float bodyWidth = Place - LetterWidth - Divider - 8f;
            float room = Mathf.Max(0f, width - Timeline - LetterWidth - bodyWidth - Divider - 62f - 52f - Divider - 76f - Square
                                       - 4f * 9f - RowEnd);
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
                TextAnchor.MiddleLeft, dot, new DialogGUILabel(() => "<b>" + Letter(index) + "</b>", LetterWidth), body,
                PlannerWidgets.Block(Divider, RowHeight - 4f, PlannerColors.Line), surface, orbit, divider, air, noAir,
                new DialogGUISpace(room), remove);
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
            PlannerWidgets.Clickable(bar.Layout, () => SelectStep(leg, role), after);
            DialogGUIButton speed = new DialogGUIButton(() => StepSpeed(leg, role), () => SelectStep(leg, role), StepSpeed_, RowHeight,
                false);
            speed.tooltipText = "Show the stages of this step in the fuel plan.";
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
                new DialogGUILabel("<color=" + Dim + ">Docks with the rest</color>", OptionLabel), refuel);
            docks.OptionEnabledCondition = () => Docks(leg, role);
            yield return docks;

            // Behind "...", each on a line of its own, as they concern different
            // things: the way down, a payload.
            DialogGUIButton straight = Choice("Straight in", () => !OrbitFirst(leg + 1), () => SetOrbitFirst(leg + 1, false), 88f);
            straight.tooltipText = "Go straight into the atmosphere and land from there.";
            DialogGUIButton orbitFirst = Choice("Orbit first", () => OrbitFirst(leg + 1), () => SetOrbitFirst(leg + 1, true), 84f);
            orbitFirst.tooltipText = "Brake into a low orbit first and land from there.";
            yield return OptionLine(leg, role, "Way down", () => IntoTheAir(leg, role), straight, orbitFirst);

            DialogGUIButton together = Choice("Together", () => !SplitsBurns(leg + 1), () => SetSplitBurns(leg + 1, false), 88f);
            together.tooltipText = "Fly the burn that leaves and the one that arrives as one step.";
            DialogGUIButton split = Choice("Split", () => SplitsBurns(leg + 1), () => SetSplitBurns(leg + 1, true), 84f);
            split.tooltipText = "Make the burn that leaves and the one that arrives two steps, each with who flies it.";
            yield return OptionLine(leg, role, "Burns", () => role == StepRole.Transfer && CanSplit(leg), together, split);

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
            return Slot(leg, role) >= 0 && (IntoTheAir(leg, role) || Arrives(leg, role) || (role == StepRole.Transfer && CanSplit(leg)));
        }

        // The way across to the next destination has a burn that leaves and one that
        // arrives (RouteSteps.Splits).
        private bool CanSplit(int leg)
        {
            return Shown(leg + 1) && RouteSteps.Splits(bodies.Bodies, state.Stops[leg], state.Stops[leg + 1]);
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
            else if (selectedLeg == leg && selectedRole == role) speed = "<b><color=" + Good + ">" + speed + "</color></b>";
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

        // The heading, with the selected step and a way back to the whole route; the
        // columns' names; a row per stage for the selected step and one for the
        // route, of which only one kind shows.
        private IEnumerable<DialogGUIBase> FuelRows()
        {
            DialogGUIButton back = new DialogGUIButton("Whole route", SelectRoute, 100f, Square, false);
            back.tooltipText = "Show what every stage gives over the whole route.";
            back.OptionEnabledCondition = () => SelectedStep() >= 0;
            yield return new DialogGUIHorizontalLayout(width, Square + 8f, 4f, new RectOffset(0, 0, 8, 0), TextAnchor.LowerLeft,
                new DialogGUILabel(FuelTitle, width - 100f - 4f - RowEnd, RowHeight), back);

            DialogGUIHorizontalLayout names = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Swatch), new DialogGUILabel("<color=" + Dim + ">Stage</color>", StageName_),
                new DialogGUILabel(() => "<color=" + Dim + ">" + (SelectedStep() >= 0 ? "Here" : "Gives") + "</color>", GivesColumn),
                new DialogGUILabel(() => "<color=" + Dim + ">" + (SelectedStep() >= 0 ? "Left after" : "At the end") + "</color>",
                    LeftColumn));
            names.OptionEnabledCondition = () => FuelCount() > 0;
            yield return names;

            for (int i = 0; i < MaxStages; i++) yield return StepStageRow(i);
            for (int i = 0; i < MaxStages; i++) yield return RouteStageRow(i);

            DialogGUIHorizontalLayout empty = new DialogGUIHorizontalLayout(width, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel("<color=" + Dim + ">No stage with fuel is left for this step.</color>", width, RowHeight));
            empty.OptionEnabledCondition = () => SelectedStep() >= 0 && FuelCount() == 0;
            yield return empty;
        }

        // "Fuel plan", or "Fuel · To Kerbin orbit  3,446 m/s" for the selected step.
        private string FuelTitle()
        {
            int step = Worked() ? SelectedStep() : -1;
            if (step < 0) return "<b><color=#e0e0e0>Fuel plan</color></b>";
            return "<b><color=#e0e0e0>Fuel · To " + RouteSteps.Place(bodies.Bodies, steps[step].To) + "</color></b>  "
                   + EngineerFormat.Speed(budget.Needed[step]);
        }

        // The rows the fuel plan shows: the selected step's stages, or every stage
        // with delta-v.
        private int FuelCount()
        {
            if (!Worked()) return 0;
            int step = SelectedStep();
            return step >= 0 ? budget.Stages[step].Count : stages.Count;
        }

        private StageBudget.StepStage StepStageAt(int row)
        {
            int step = Worked() ? SelectedStep() : -1;
            return step >= 0 && row < budget.Stages[step].Count ? budget.Stages[step][row] : null;
        }

        // A stage of the selected step: what it gives here, what it keeps, and
        // whether it burns, is saved for later or is dropped once the step is over.
        private DialogGUIBase StepStageRow(int row)
        {
            System.Func<int> number = () =>
            {
                StageBudget.StepStage stage = StepStageAt(row);
                return stage != null ? stage.Stage : -1;
            };
            SegmentBar swatch = new SegmentBar(1, RowHeight - 2f, after);
            stepSwatches[row] = swatch;
            DialogGUIButton burns = Choice("Burns", () => ChoiceOf(number()) == StageChoice.Burns,
                () => SetChoice(number(), StageChoice.Burns), BurnsButton);
            burns.tooltipText = "Burn this stage in this step as KSP's staging comes to it.";
            DialogGUIButton save = Choice("Save", () => ChoiceOf(number()) == StageChoice.Save,
                () => SetChoice(number(), StageChoice.Save), SaveButton);
            save.tooltipText = "Keep this stage for a later step: it and the stages after it do not burn here.";
            DialogGUIButton dropAfter = Choice("Drop after", () => ChoiceOf(number()) == StageChoice.DropAfter,
                () => SetChoice(number(), StageChoice.DropAfter), DropButton);
            dropAfter.tooltipText = "Drop this stage and the ones before it at the end of this step, with what they still hold.";
            dropAfter.OptionInteractableCondition = () =>
            {
                StageBudget.StepStage stage = StepStageAt(row);
                return stage != null && stage.CanDrop;
            };
            // The choices end where a stop's cross does.
            float room = Mathf.Max(0f, width - Swatch - StageName_ - GivesColumn - LeftColumn - BurnsButton - SaveButton - DropButton
                                       - 4f * 7f - RowEnd);
            DialogGUIHorizontalLayout line = new DialogGUIHorizontalLayout(width, Square + 2f, 4f, new RectOffset(),
                TextAnchor.MiddleLeft, swatch.Layout, new DialogGUILabel(() => ColouredStage(number()), StageName_),
                new DialogGUILabel(() => GivenHere(row), GivesColumn), new DialogGUILabel(() => LeftAfter(row), LeftColumn),
                new DialogGUISpace(room), burns, save, dropAfter);
            line.OptionEnabledCondition = () => StepStageAt(row) != null;
            return line;
        }

        private string GivenHere(int row)
        {
            StageBudget.StepStage stage = StepStageAt(row);
            if (stage == null) return "";
            if (stage.Given < 0.5) return "<color=" + Dim + ">0</color>";
            return "<color=" + PlannerColors.Hex(StageColour(stage.Stage)) + ">" + EngineerFormat.Speed(stage.Given) + "</color>";
        }

        private string LeftAfter(int row)
        {
            StageBudget.StepStage stage = StepStageAt(row);
            if (stage == null) return "";
            if (stage.Dropped) return "<color=" + Warning + ">dropped, " + EngineerFormat.Speed(stage.LeftAfter) + "</color>";
            if (stage.LeftAfter < 0.5) return "<color=" + Dim + ">empty</color>";
            return "<color=" + Good + ">" + EngineerFormat.Speed(stage.LeftAfter) + "</color>";
        }

        // A stage over the whole route: what it gives, and what it has at the end,
        // or where it was dropped or left.
        private DialogGUIBase RouteStageRow(int index)
        {
            SegmentBar swatch = new SegmentBar(1, RowHeight - 2f, after);
            routeSwatches[index] = swatch;
            DialogGUIButton name = new DialogGUIButton(() => StageLabel(index), () => SelectStage(index), StageName_, RowHeight, false);
            name.tooltipText = "Show this stage's engines and what it gives to each step.";
            DialogOverWindow.ClearBackground(name);
            DialogGUIHorizontalLayout line = new DialogGUIHorizontalLayout(width, RowHeight + 2f, 4f, new RectOffset(),
                TextAnchor.MiddleLeft, swatch.Layout, name, new DialogGUILabel(() => RouteGiven(index), GivesColumn),
                new DialogGUILabel(() => StageEnd(index), width - Swatch - StageName_ - GivesColumn - 4f * 3f - RowEnd));
            line.OptionEnabledCondition = () => Worked() && SelectedStep() < 0 && index < stages.Count;
            return line;
        }

        private string RouteGiven(int index)
        {
            if (!Worked() || index >= stages.Count) return "";
            double used = 0.0;
            foreach (StageBudget.StageUse use in budget.Uses)
                if (use.Stage == stages[index].Number) used += use.DeltaV;
            if (used < 0.5) return "<color=" + Dim + ">0</color>";
            return "<color=" + PlannerColors.Hex(PlannerColors.Stage(index)) + ">" + EngineerFormat.Speed(used) + "</color>";
        }

        // "1,200 m/s left", "dropped at Kerbin orbit with 300 m/s", "stays at Mun
        // surface", "used up".
        private string StageEnd(int index)
        {
            if (!Worked() || index >= stages.Count) return "";
            int number = stages[index].Number;
            double value;
            int gone;
            string place = budget.GoneAfter.TryGetValue(number, out gone) ? GonePlace(gone) : "";
            if (budget.LeftBehind.Contains(number)) return "<color=" + Dim + ">stays at " + place + "</color>";
            if (budget.Dropped.TryGetValue(number, out value))
                return "<color=" + Warning + ">dropped at " + place + " with " + EngineerFormat.Speed(value) + "</color>";
            if (budget.Left.TryGetValue(number, out value) && value > 0.5)
                return "<color=" + Good + ">" + EngineerFormat.Speed(value) + " left</color>";
            return "<color=" + Dim + ">used up</color>";
        }

        // Where the step a stage last flew with ends; the route's start before the
        // first.
        private string GonePlace(int step)
        {
            if (steps.Count == 0) return "";
            return RouteSteps.Place(bodies.Bodies, step >= 0 && step < steps.Count ? steps[step].To : steps[0].From);
        }

        private string StageLabel(int index)
        {
            if (index >= stages.Count) return "";
            string name = "S" + stages[index].Number;
            return selectedStage == stages[index].Number ? "<b><u>" + ColouredStage(stages[index].Number) + "</u></b>" : "<b>" + name + "</b>";
        }

        // Each row's swatch in the colour of the stage it shows.
        private void UpdateSwatches()
        {
            for (int row = 0; row < MaxStages; row++)
            {
                StageBudget.StepStage stage = StepStageAt(row);
                if (stepSwatches[row] != null) stepSwatches[row].Set(Swatches(stage != null ? StageColour(stage.Stage) : Color.clear));
                if (routeSwatches[row] != null)
                    routeSwatches[row].Set(Swatches(row < stages.Count ? PlannerColors.Stage(row) : Color.clear));
            }
        }

        private static List<KeyValuePair<float, Color>> Swatches(Color color)
        {
            return new List<KeyValuePair<float, Color>> { new KeyValuePair<float, Color>(Swatch, color) };
        }

        // --------------------------------------------------------------- details

        // The selected step's or stage's details, one line to a row of a fixed
        // height: a label of several lines whose text changes is not given its
        // height by KSP's layout, and ran over the rows below it.
        private const int MaxDetailLines = 40;

        private IEnumerable<DialogGUIBase> DetailRows()
        {
            DialogGUIBase heading = new DialogGUIHorizontalLayout(width, RowHeight + 8f, 0f, new RectOffset(0, 0, 8, 0),
                TextAnchor.LowerLeft, new DialogGUILabel(() => "<b>" + detailTitle + "</b>", width, RowHeight));
            heading.OptionEnabledCondition = () => detailTitle.Length > 0;
            yield return heading;
            DialogGUISpace gap = new DialogGUISpace(8f);
            gap.OptionEnabledCondition = () => detailTitle.Length == 0 && detailLines.Length > 0;
            yield return gap;
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
            int step = Worked() ? SelectedStep() : -1;
            if (selectedStage >= 0)
            {
                detailTitle = StageTitle();
                text = StageDetail();
            }
            else if (step >= 0)
            {
                detailTitle = "";
                text = StepDetail(step);
            }
            else
            {
                detailTitle = "";
                text = "";
            }
            detailLines = text.Length > 0 ? text.Split('\n') : new string[0];
            if (detailLines.Length > MaxDetailLines) System.Array.Resize(ref detailLines, MaxDetailLines);
            UpdateSwatches();
        }

        // The step's burns, the margin, the stages that pay for it with their thrust
        // against the weight at that body, and what stands in its way.
        private string StepDetail(int index)
        {
            RouteStep step = steps[index];
            StringBuilder text = new StringBuilder();
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
            return text.ToString();
        }

        // "Lander to Mun surface", "Without lander to Kerbin's air".
        private string StepTitle(RouteStep step)
        {
            return VehicleName(step.Vehicle) + " to " + RouteSteps.Place(bodies.Bodies, step.To);
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
                    text.Append('\n').Append("To ").Append(RouteSteps.Place(bodies.Bodies, steps[use.Leg].To)).Append(" <color=" + Dim + ">")
                        .Append(VehicleName(steps[use.Leg].Vehicle)).Append("</color><pos=60%>").Append(EngineerFormat.Speed(use.DeltaV));
            text.Append('\n').Append(StageEnd(at));
            return text.ToString();
        }

        // "S3" in the colour of its stage.
        private string ColouredStage(int number)
        {
            if (number < 0) return "";
            return "<b><color=" + PlannerColors.Hex(StageColour(number)) + ">S" + number + "</color></b>";
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
                for (int role = 0; role < RouteStop.Roles; role++)
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

            UpdateSwatches();

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
