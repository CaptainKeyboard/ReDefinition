using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The planner's rows, built from KSP's dialog elements, so a UI theme reaches
    // them as it reaches KSP's dialogs. From the top: the vessel's sections; the
    // route as a line of destinations, and between each two the steps that follow
    // from it, a bar each (its length the step's delta-v, its colours the stages that
    // pay for it, red what none covers), who flies it, the choices it offers, and in
    // red what stands in its way; the fuel for the whole route; and the fuel plan,
    // the steps with what each needs and leaves, each opening to its stages. A chosen
    // option is lit in its text rather than by the skin's pressed state. A fixed
    // number of rows, shown as the plan needs them.
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
        // The fuel plan's stage rows: the swatch, the stage's name, Drop after.
        private const float Swatch = 8f;
        private const float StageName_ = 32f;
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

        internal List<DialogGUIBase> Rows()
        {
            altitudeFields.Clear();
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            rows.Add(Heading("Vessel"));
            for (int i = 0; i < MaxSections; i++) rows.AddRange(SectionRows(i));
            rows.Add(AddSectionRow());
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
            rows.AddRange(ThrustRows());
            return rows;
        }

        // -------------------------------------------------------------- sections

        // A section: what it is in the column of a step's place, its parts on a button
        // from the column of who flies a step, its cross where a stop's cross is; under
        // a satellite where it stays; and in red what is wrong with it.
        private IEnumerable<DialogGUIBase> SectionRows(int row)
        {
            DialogGUIButton group = null;
            group = new DialogGUIButton(() => SectionText(row), () =>
            {
                LiveSection section = RowSection(row);
                if (section != null) PickSectionParts(section.Plan, group);
            }, PartButtonWidth - Square - 4f, Square, false);
            group.tooltipText = "Select the parts of the vessel this section is.";
            DialogGUIButton remove = PlannerWidgets.IconButton("Remove", () => RemoveSection(row), Square, Square, 14f,
                "Remove this section.", after);
            DialogGUIHorizontalLayout line = new DialogGUIHorizontalLayout(width, Square + 2f, 4f, new RectOffset(0, 0, 2, 0),
                TextAnchor.MiddleLeft, new DialogGUISpace(Timeline), new DialogGUILabel(() => SectionLabel(row), Place), group, remove);
            line.OptionEnabledCondition = () => RowSection(row) != null;
            yield return line;

            DialogGUIButton stop = null;
            stop = new DialogGUIButton(() => DeployText(row), () => PickDeployStop(row, stop), PartButtonWidth, Square, false);
            stop.tooltipText = "Select the destination where the satellite stays.";
            DialogGUIHorizontalLayout deploy = new DialogGUIHorizontalLayout(width, Square + 2f, 4f, new RectOffset(0, 0, 2, 0),
                TextAnchor.MiddleLeft, new DialogGUISpace(Timeline), new DialogGUILabel("<color=" + Dim + ">Deployed at</color>", Place),
                stop);
            deploy.OptionEnabledCondition = () => IsKind(row, SectionKind.Satellite);
            yield return deploy;

            DialogGUIHorizontalLayout warning = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(),
                TextAnchor.MiddleLeft, new DialogGUISpace(Timeline), new DialogGUISpace(Place),
                new DialogGUILabel(() => "<color=" + Problem + ">" + SectionWarning(row) + "</color>", PartButtonWidth, RowHeight));
            warning.OptionEnabledCondition = () => SectionWarning(row).Length > 0;
            yield return warning;

            DialogGUIHorizontalLayout note = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(),
                TextAnchor.MiddleLeft, new DialogGUISpace(Timeline), new DialogGUISpace(Place),
                new DialogGUILabel(() => "<color=" + Dim + ">" + SectionNote(row) + "</color>", PartButtonWidth, RowHeight));
            note.OptionEnabledCondition = () => SectionNote(row).Length > 0;
            yield return note;
        }

        // "+" to add a section, in the column of a section's name.
        private DialogGUIBase AddSectionRow()
        {
            DialogGUIButton add = null;
            add = new DialogGUIButton("<b>+</b>", () => PickNewSection(add), Square, Square, false);
            add.tooltipText = "Add a section of the vessel: a capsule, a lander or a satellite.";
            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, Square + 2f, 4f, new RectOffset(0, 0, 2, 0),
                TextAnchor.MiddleLeft, new DialogGUISpace(Timeline), add);
            row.OptionEnabledCondition = () => Ready() && state.Sections.Count < MaxSections;
            return row;
        }

        // A section's buttons: from who flies a step to where a stop's cross ends.
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
            vehicle = new DialogGUIButton(() => VehicleText(leg, role), () => PickFlyer(leg, role, vehicle), VehicleButton,
                RowHeight + 2f, false);
            vehicle.tooltipText = "Select which part of the vessel flies this step.";
            vehicle.OptionEnabledCondition = HasSections;
            SegmentBar bar = new SegmentBar(MaxPieces, BarHeight, after);
            stepBars[leg, (int)role] = bar;
            DialogGUILabel speed = new DialogGUILabel(() => StepSpeed(leg, role), StepSpeed_, RowHeight);
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
            refuel.tooltipText = "Fill this section's tanks from the rest of the vessel when it docks.";
            DialogGUIButton tanker = null;
            tanker = new DialogGUIButton(() => TankerText(leg, role), () => PickTanker(leg, role, tanker), 150f, RowHeight + 2f, false);
            tanker.tooltipText = "Select the stage of the rest the refill comes from first.";
            tanker.OptionEnabledCondition = () => Refuels(leg, role);
            DialogGUIHorizontalLayout docks = new DialogGUIHorizontalLayout(width, RowHeight + 4f, 4f, new RectOffset(),
                TextAnchor.MiddleLeft, TimelineLine(RowHeight + 4f), new DialogGUISpace(BarIndent),
                new DialogGUILabel("<color=" + Dim + ">Docks with the rest</color>", OptionLabel), refuel, tanker);
            docks.OptionEnabledCondition = () => Docks(leg, role);
            yield return docks;

            DialogGUIHorizontalLayout stays = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(),
                TextAnchor.MiddleLeft, TimelineLine(RowHeight), new DialogGUISpace(BarIndent),
                new DialogGUILabel(() => "<color=" + Dim + ">" + DeployedText(leg, role) + "</color>", width - Timeline - BarIndent - 8f,
                    RowHeight));
            stays.OptionEnabledCondition = () => DeployedText(leg, role).Length > 0;
            yield return stays;

            // Behind "...", each on a line of its own, as they concern different
            // things: the way down, the burns across.
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
            string name = FlyerName(steps[index].Flyer);
            return steps[index].Chosen ? "<b><u><color=" + Good + ">" + name + "</color></u></b>" : "<color=" + Good + ">" + name + "</color>";
        }

        // "To Mun surface", "To Kerbin's air", "To Kerbin orbit".
        private string StepPlace(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            return index < 0 ? "" : "To " + RouteSteps.Place(bodies.Bodies, steps[index].To);
        }

        // "The lander leaves the vessel in Mun orbit", "The vessel leaves the lander on
        // Mun surface".
        private string SeparationText(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            if (index < 0 || !steps[index].Separates) return "";
            RouteStep step = steps[index];
            string place = RouteSteps.At(bodies.Bodies, step.From);
            string leaver = Lower(FlyerName(Flyer.Alone(step.Leaver)));
            if (step.Flyer.Kind == FlyerKind.Without) return "The vessel leaves the " + leaver + " " + place;
            string from = step.LeavesFrom == 0 ? "the vessel" : "the " + Lower(FlyerName(Flyer.Alone(step.LeavesFrom)));
            return "The " + leaver + " leaves " + from + " " + place;
        }

        // "The satellite stays here": the sections deployed at the step's end.
        private string DeployedText(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            if (index < 0 || steps[index].Deploys.Count == 0) return "";
            List<string> names = new List<string>();
            foreach (int id in steps[index].Deploys) names.Add(Lower(FlyerName(Flyer.Alone(id))));
            return "The " + string.Join(", the ", names.ToArray()) + (names.Count == 1 ? " stays here" : " stay here");
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
            return Slot(leg, role) >= 0 && (IntoTheAir(leg, role) || (role == StepRole.Transfer && CanSplit(leg)));
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

        // The fuel plan: every step of the route in its order, with who flies it, what
        // it needs with the margin, and what what flies it has left once it is over, in
        // red what it lacks. A step opens to the stages it has with fuel as it starts,
        // each with what it gives there, what it keeps after, its thrust against the
        // weight it pushes, and whether it is used there and dropped after it. A stage
        // gone before a step, dropped or burnt out, is no longer among its stages.
        private const int MaxFuelSteps = 30;
        private const int MaxStepStages = 10;
        private const float FuelNeeds = 72f;
        private const float FuelLeft = 96f;
        private const float StageGives = 72f;
        private const float StageKeeps = 110f;
        private const float StageTwr = 40f;
        private const float UseButton = 48f;
        private readonly SegmentBar[,] fuelSwatches = new SegmentBar[MaxFuelSteps, MaxStepStages];
        // The steps open, by destination and role.
        private readonly HashSet<int> fuelOpen = new HashSet<int>();
        private bool fuelShown = true;

        private IEnumerable<DialogGUIBase> FuelRows()
        {
            yield return CollapsibleHeading("Fuel plan", () => fuelShown, () => fuelShown = !fuelShown,
                "Show or hide what each step needs and leaves.", FuelSummary);
            DialogGUIHorizontalLayout names = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline), DimLabel("Step", Place), DimLabel("Flown by", VehicleButton), DimLabel("Needs", FuelNeeds),
                DimLabel("Left after", FuelLeft));
            names.OptionEnabledCondition = () => fuelShown && Worked() && steps.Count > 0;
            yield return names;
            for (int i = 0; i < MaxFuelSteps; i++)
                foreach (DialogGUIBase row in FuelStepRows(i))
                    yield return row;
            DialogGUIHorizontalLayout more = new DialogGUIHorizontalLayout(width, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline),
                new DialogGUILabel(() => "<color=" + Dim + ">" + (steps.Count - MaxFuelSteps) + " more steps</color>", width - Timeline));
            more.OptionEnabledCondition = () => fuelShown && Worked() && steps.Count > MaxFuelSteps;
            yield return more;
        }

        private static DialogGUILabel DimLabel(string text, float labelWidth)
        {
            return new DialogGUILabel("<color=" + Dim + ">" + text + "</color>", labelWidth);
        }

        private bool FuelStepShown(int step)
        {
            return fuelShown && Worked() && step < steps.Count;
        }

        private int FuelKey(int step)
        {
            return steps[step].Goal * RouteStop.Roles + (int)steps[step].Role;
        }

        private bool FuelIsOpen(int step)
        {
            return FuelStepShown(step) && fuelOpen.Contains(FuelKey(step));
        }

        private void ToggleFuel(int step)
        {
            if (!FuelStepShown(step)) return;
            int key = FuelKey(step);
            if (!fuelOpen.Remove(key)) fuelOpen.Add(key);
            UpdateSwatches();
        }

        // A stage row of an open step; null for none, and for every row of a closed
        // one, so that its texts are not written.
        private StageBudget.StepStage FuelStage(int step, int row)
        {
            if (!FuelIsOpen(step) || budget.Stages[step] == null || row >= budget.Stages[step].Count) return null;
            return budget.Stages[step][row];
        }

        private int FuelStageCount(int step)
        {
            return FuelStepShown(step) && budget.Stages[step] != null ? budget.Stages[step].Count : 0;
        }

        // A step's row, its stages' names, and a row per stage while it is open. The
        // columns are those of the steps above: where it goes under a step's place, who
        // flies it under who flies, the button to open it where "..." is.
        private IEnumerable<DialogGUIBase> FuelStepRows(int step)
        {
            DialogGUIButton open = new DialogGUIButton(() => FuelIsOpen(step) ? "<b><color=" + Good + ">-</color></b>" : "+",
                () => ToggleFuel(step), Square, Square, false);
            open.tooltipText = "Show the stages of this step.";
            open.OptionEnabledCondition = () => FuelStepShown(step);
            DialogGUISpace noOpen = new DialogGUISpace(Square);
            noOpen.OptionEnabledCondition = () => !FuelStepShown(step);
            float room = Mathf.Max(0f, width - Timeline - Place - VehicleButton - FuelNeeds - FuelLeft - Square - 4f * 6f - RowEnd);
            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, Square + 2f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline), new DialogGUILabel(() => FuelPlace(step), Place),
                new DialogGUILabel(() => FuelFlyer(step), VehicleButton), new DialogGUILabel(() => FuelNeedsText(step), FuelNeeds),
                new DialogGUILabel(() => FuelLeftText(step), FuelLeft), new DialogGUISpace(room), open, noOpen);
            row.OptionEnabledCondition = () => FuelStepShown(step);
            yield return row;

            DialogGUIHorizontalLayout names = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline + Swatch + 4f), DimLabel("Stage", StageName_), DimLabel("Gives", StageGives), DimLabel("Keeps", StageKeeps),
                DimLabel("TWR", StageTwr));
            names.OptionEnabledCondition = () => FuelIsOpen(step) && FuelStage(step, 0) != null;
            yield return names;

            for (int i = 0; i < MaxStepStages; i++)
                yield return FuelStageRow(step, i);
            DialogGUIHorizontalLayout more = new DialogGUIHorizontalLayout(width, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline + Swatch + 4f),
                new DialogGUILabel(() => "<color=" + Dim + ">" + (FuelStageCount(step) - MaxStepStages) + " more stages</color>",
                    width - Timeline - Swatch - 4f));
            more.OptionEnabledCondition = () => FuelIsOpen(step) && FuelStageCount(step) > MaxStepStages;
            yield return more;

            // What the step is made of: its burns and the margin; what docks and refills,
            // what stays; and what stands in its way.
            for (int i = 0; i < MaxFuelLines; i++)
            {
                int line = i;
                DialogGUIHorizontalLayout detail = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(),
                    TextAnchor.MiddleLeft, new DialogGUISpace(Timeline), new DialogGUILabel(() => FuelLineName(step, line), Place + VehicleButton + 4f),
                    new DialogGUILabel(() => FuelLineValue(step, line), FuelNeeds));
                detail.OptionEnabledCondition = () => FuelIsOpen(step) && line < FuelLines(step).Count;
                yield return detail;
            }
            for (int i = 0; i < MaxWarnings; i++)
            {
                int line = i;
                DialogGUIHorizontalLayout warning = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(),
                    TextAnchor.MiddleLeft, new DialogGUISpace(Timeline),
                    new DialogGUILabel(() => "<color=" + Problem + ">" + FuelWarning(step, line) + "</color>", width - Timeline - 4f - RowEnd));
                warning.OptionEnabledCondition = () => FuelIsOpen(step) && FuelWarning(step, line).Length > 0;
                yield return warning;
            }
        }

        private const int MaxFuelLines = 8;

        // The lines of an open step, as name and value: each burn, the margin, a
        // refill, what stays there.
        private List<KeyValuePair<string, string>> FuelLines(int step)
        {
            List<KeyValuePair<string, string>> lines = new List<KeyValuePair<string, string>>();
            if (!FuelIsOpen(step)) return lines;
            RouteStep routeStep = steps[step];
            foreach (LegLine line in routeStep.Leg.Lines)
                lines.Add(new KeyValuePair<string, string>("<color=" + Dim + ">" + line.Name + "</color>", EngineerFormat.Speed(line.DeltaV)));
            if (state.Margin > 0.0 && routeStep.Leg.DeltaV > 0.5)
                lines.Add(new KeyValuePair<string, string>(
                    "<color=" + Dim + ">Margin " + (state.Margin * 100.0).ToString("0", CultureInfo.InvariantCulture) + " %</color>",
                    EngineerFormat.Speed(budget.Needed[step] - routeStep.Leg.DeltaV)));
            if (budget.Refuelled[step] > 1e-3)
                lines.Add(new KeyValuePair<string, string>("<color=" + Good + ">Refilled from the rest</color>",
                    "<color=" + Good + ">" + budget.Refuelled[step].ToString("0.00", CultureInfo.InvariantCulture) + " t</color>"));
            if (budget.Payload[step] > 1e-3)
                lines.Add(new KeyValuePair<string, string>("<color=" + Good + ">Left here</color>",
                    "<color=" + Good + ">" + budget.Payload[step].ToString("0.00", CultureInfo.InvariantCulture) + " t</color>"));
            return lines;
        }

        private string FuelLineName(int step, int line)
        {
            List<KeyValuePair<string, string>> lines = FuelLines(step);
            return line < lines.Count ? lines[line].Key : "";
        }

        private string FuelLineValue(int step, int line)
        {
            List<KeyValuePair<string, string>> lines = FuelLines(step);
            return line < lines.Count ? lines[line].Value : "";
        }

        private string FuelWarning(int step, int line)
        {
            if (!FuelIsOpen(step) || step >= warnings.Length || warnings[step] == null || line >= warnings[step].Count) return "";
            return warnings[step][line];
        }

        // A stage of an open step: its swatch in the stage's colour, what it gives here,
        // what it keeps after or held as it was dropped, its thrust against the weight at
        // the body of a launch or a landing; Use and Drop after, where a stop's cross
        // ends.
        private DialogGUIBase FuelStageRow(int step, int row)
        {
            SegmentBar swatch = new SegmentBar(1, RowHeight - 2f, after);
            fuelSwatches[step, row] = swatch;
            System.Func<int> number = () =>
            {
                StageBudget.StepStage stage = FuelStage(step, row);
                return stage != null ? stage.Stage : -1;
            };
            DialogGUIButton use = Choice("Use", () => Burnable(step, row), () => ToggleSaved(step, number()), UseButton);
            use.tooltipText = "Burn this stage on this step; off, it and the stages after it wait for a later step.";
            // Lit where it holds; to be switched off even where the stage can no longer
            // be dropped, the last one of what flies the step.
            DialogGUIButton drop = Choice("Drop after", () =>
            {
                StageBudget.StepStage stage = FuelStage(step, row);
                return stage != null && stage.CanDrop && DropsAfter(step, stage.Stage);
            }, () => ToggleDropAfter(step, number()), DropButton);
            drop.tooltipText = "Drop this stage and the ones before it at the end of this step, with what they still hold.";
            drop.OptionInteractableCondition = () =>
            {
                StageBudget.StepStage stage = FuelStage(step, row);
                return stage != null && (stage.CanDrop || DropsAfter(step, stage.Stage));
            };
            float room = Mathf.Max(0f, width - Timeline - Swatch - StageName_ - StageGives - StageKeeps - StageTwr - UseButton - DropButton
                                       - 4f * 8f - RowEnd);
            DialogGUIHorizontalLayout line = new DialogGUIHorizontalLayout(width, Square + 2f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline), swatch.Layout, new DialogGUILabel(() => ColouredStage(number()), StageName_),
                new DialogGUILabel(() => StageGivesText(step, row), StageGives), new DialogGUILabel(() => StageKeepsText(step, row), StageKeeps),
                new DialogGUILabel(() => StageTwrText(step, row), StageTwr), new DialogGUISpace(room), use, drop);
            line.OptionEnabledCondition = () => FuelIsOpen(step) && FuelStage(step, row) != null;
            return line;
        }

        // Use is lit where the stage can burn on the step: not kept for later, nor behind
        // a stage kept for later, as KSP fires the stages in order.
        private bool Burnable(int step, int row)
        {
            StageBudget.StepStage stage = FuelStage(step, row);
            if (stage == null || Saved(step, stage.Stage)) return false;
            for (int before = 0; before < row; before++)
            {
                StageBudget.StepStage earlier = FuelStage(step, before);
                if (earlier != null && Saved(step, earlier.Stage)) return false;
            }
            return true;
        }

        // "To Mun orbit".
        private string FuelPlace(int step)
        {
            return FuelStepShown(step) ? "To " + RouteSteps.Place(bodies.Bodies, steps[step].To) : "";
        }

        private string FuelFlyer(int step)
        {
            return FuelStepShown(step) ? "<color=" + Dim + ">" + FlyerName(steps[step].Flyer) + "</color>" : "";
        }

        private string FuelNeedsText(int step)
        {
            return FuelStepShown(step) ? EngineerFormat.Speed(budget.Needed[step]) : "";
        }

        // What is left once the step is over, green; in red what the step lacks, or
        // what stands in its way.
        private string FuelLeftText(int step)
        {
            if (!FuelStepShown(step)) return "";
            if (budget.Short[step] > 0.5) return "<color=" + Problem + ">-" + EngineerFormat.Speed(budget.Short[step]) + "</color>";
            if (warnings[step].Count > 0) return "<color=" + Problem + ">" + EngineerFormat.Speed(budget.LeftAfter[step]) + "</color>";
            return "<color=" + Good + ">" + EngineerFormat.Speed(budget.LeftAfter[step]) + "</color>";
        }

        private string StageGivesText(int step, int row)
        {
            StageBudget.StepStage stage = FuelStage(step, row);
            if (stage == null) return "";
            if (stage.Given < 0.5) return "<color=" + Dim + ">0</color>";
            return "<color=" + PlannerColors.Hex(StageColour(stage.Stage)) + ">" + EngineerFormat.Speed(stage.Given) + "</color>";
        }

        // What the stage keeps after the step; in orange what it held as it was dropped.
        private string StageKeepsText(int step, int row)
        {
            StageBudget.StepStage stage = FuelStage(step, row);
            if (stage == null) return "";
            if (stage.Dropped) return "<color=" + Warning + ">dropped, " + EngineerFormat.Speed(stage.LeftAfter) + "</color>";
            if (stage.LeftAfter < 0.5) return "<color=" + Dim + ">empty</color>";
            return EngineerFormat.Speed(stage.LeftAfter);
        }

        // The stage's thrust against the weight it pushes as it starts its part of the
        // step, at the surface of the body it lifts off from or lands on; red below what
        // that needs on the stage that lifts off or sets down, the first of a launch and
        // the last of a landing, as the warnings have it. Nothing for a stage the step
        // does not burn, or away from surfaces.
        private string StageTwrText(int step, int row)
        {
            StageBudget.StepStage stage = FuelStage(step, row);
            if (stage == null) return "";
            RouteStep routeStep = steps[step];
            bool launch = routeStep.From.Kind == StopKind.Surface;
            bool landing = routeStep.To.Kind == StopKind.Surface && !Chutes(routeStep.To);
            if (!launch && !landing) return "";
            StageBudget.StageUse use = budget.Uses.Find(item => item.Leg == step && item.Stage == stage.Stage);
            if (use == null) return "";
            int body = launch ? routeStep.From.Body : routeStep.To.Body;
            double twr = TwrAt(use.Stage, use.Mass, body, true);
            double least = launch && bodies.Bodies[body].HasAtmosphere ? 1.2 : 1.0;
            StageBudget.StageUse first = budget.Uses.Find(item => item.Leg == step);
            StageBudget.StageUse last = budget.Uses.FindLast(item => item.Leg == step);
            bool decides = launch ? use == first : use == last;
            string text = EngineerFormat.Ratio(twr);
            return decides && twr < least ? "<color=" + Problem + ">" + text + "</color>" : "<color=" + Dim + ">" + text + "</color>";
        }

        // "S3" in the colour of its stage.
        private string ColouredStage(int number)
        {
            if (number < 0) return "";
            return "<b><color=" + PlannerColors.Hex(StageColour(number)) + ">S" + number + "</color></b>";
        }

        // Each open stage row's swatch in the colour of its stage.
        private void UpdateSwatches()
        {
            for (int step = 0; step < MaxFuelSteps; step++)
                for (int row = 0; row < MaxStepStages; row++)
                {
                    SegmentBar swatch = fuelSwatches[step, row];
                    if (swatch == null) continue;
                    StageBudget.StepStage stage = FuelStage(step, row);
                    swatch.Set(new List<KeyValuePair<float, Color>>
                    {
                        new KeyValuePair<float, Color>(Swatch, stage != null ? StageColour(stage.Stage) : Color.clear),
                    });
                }
        }

        // ---------------------------------------------------------------- thrust

        // Whether the thrust does the job, for every step that burns: the thrust
        // against the weight where it decides, a launch and a landing on the engines;
        // the time each burn in orbit takes against the orbit it is in. Each step with
        // what it needs and what it has; open, each stage it burns with its TWR as it
        // starts and as it ends its part, in the air where the body has any, its thrust
        // and how long it burns, then each burn in orbit and the body.
        private const float TwrColumn = 56f;
        private const float ThrustColumn = 72f;
        private const float BurnColumn = 64f;
        private const float BurnName = 180f;

        // A step as the thrust section shows it, worked out with the budget.
        private sealed class ThrustStep
        {
            public int Step;
            // Lifts off or sets down on the engines: its TWR decides.
            public bool Twr;
            // The body its TWR is against: the one it lifts off from or sets down on,
            // else the one of its first burn in orbit.
            public int Body;
            public readonly List<StageBudget.StageUse> Uses = new List<StageBudget.StageUse>();
            public readonly List<BurnStage> Stages = new List<BurnStage>();
            public List<Burn> Burns = new List<Burn>();
            // The burn that takes the largest share of its orbit; null where none.
            public Burn Longest;
        }

        private readonly List<ThrustStep> thrustSteps = new List<ThrustStep>();
        // Per step, its entry; null where it burns nothing.
        private ThrustStep[] thrustOf = new ThrustStep[0];
        private readonly HashSet<int> thrustOpen = new HashSet<int>();
        private bool thrustShown = true;

        private IEnumerable<DialogGUIBase> ThrustRows()
        {
            yield return CollapsibleHeading("Thrust", () => thrustShown, () => thrustShown = !thrustShown,
                "Show or hide whether the thrust lifts the vessel and how long each burn takes.", ThrustSummary);
            DialogGUIHorizontalLayout names = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline), DimLabel("Step", Place), DimLabel("Flown by", VehicleButton), DimLabel("Needs", FuelNeeds),
                DimLabel("Has", FuelLeft));
            names.OptionEnabledCondition = () => thrustShown && Worked() && thrustSteps.Count > 0;
            yield return names;
            DialogGUIHorizontalLayout none = new DialogGUIHorizontalLayout(width, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline), DimLabel("No burns on the route.", width - Timeline));
            none.OptionEnabledCondition = () => thrustShown && Worked() && thrustSteps.Count == 0;
            yield return none;
            for (int i = 0; i < MaxFuelSteps; i++)
                foreach (DialogGUIBase row in ThrustStepRows(i))
                    yield return row;
        }

        // The steps that burn, each with its stages and its burns in orbit; before the
        // warnings, which take the burns that last too long from them.
        private void UpdateThrust()
        {
            thrustSteps.Clear();
            thrustOf = new ThrustStep[steps.Count];
            for (int i = 0; i < steps.Count; i++)
            {
                RouteStep step = steps[i];
                ThrustStep entry = new ThrustStep { Step = i, Twr = Launches(step) || LandsOnEngines(step) };
                foreach (StageBudget.StageUse use in budget.Uses)
                {
                    if (use.Leg != i) continue;
                    entry.Uses.Add(use);
                    StageRow row = stages.Find(item => item.Number == use.Stage);
                    entry.Stages.Add(new BurnStage
                    {
                        Stage = use.Stage, DeltaV = use.DeltaV, Mass = use.Mass, EndMass = use.EndMass,
                        Thrust = row != null ? row.ThrustVacuum : 0.0,
                    });
                }
                entry.Burns = BurnCheck.Burns(bodies.Bodies, step.Leg, entry.Stages);
                foreach (Burn burn in entry.Burns)
                    if (entry.Longest == null || burn.Share > entry.Longest.Share) entry.Longest = burn;
                entry.Body = Launches(step) ? step.From.Body
                    : entry.Twr ? step.To.Body
                    : entry.Burns.Count > 0 ? entry.Burns[0].Line.Body : step.From.Body;
                if (step.Leg.DeltaV <= 0.5 || (!entry.Twr && entry.Burns.Count == 0)) continue;
                thrustOf[i] = entry;
                if (thrustSteps.Count < MaxFuelSteps) thrustSteps.Add(entry);
            }
        }

        private bool Launches(RouteStep step)
        {
            return step.From.Kind == StopKind.Surface;
        }

        private bool LandsOnEngines(RouteStep step)
        {
            return step.To.Kind == StopKind.Surface && !Chutes(step.To);
        }

        // The entry of a row; null for none.
        private ThrustStep ThrustAt(int row)
        {
            return thrustShown && Worked() && row < thrustSteps.Count && thrustSteps[row].Step < steps.Count ? thrustSteps[row] : null;
        }

        // The entry of an open row; null for none, and for a closed one, so that its
        // texts are not written.
        private ThrustStep OpenThrust(int row)
        {
            ThrustStep entry = ThrustAt(row);
            return entry != null && thrustOpen.Contains(FuelKey(entry.Step)) ? entry : null;
        }

        private void ToggleThrust(int row)
        {
            ThrustStep entry = ThrustAt(row);
            if (entry == null) return;
            int key = FuelKey(entry.Step);
            if (!thrustOpen.Remove(key)) thrustOpen.Add(key);
        }

        private IEnumerable<DialogGUIBase> ThrustStepRows(int row)
        {
            DialogGUIButton open = new DialogGUIButton(() => OpenThrust(row) != null ? "<b><color=" + Good + ">-</color></b>" : "+",
                () => ToggleThrust(row), Square, Square, false);
            open.tooltipText = "Show the stages of this step with their thrust, and its burns.";
            float room = Mathf.Max(0f, width - Timeline - Place - VehicleButton - FuelNeeds - FuelLeft - Square - 4f * 6f - RowEnd);
            DialogGUIHorizontalLayout line = new DialogGUIHorizontalLayout(width, Square + 2f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline), new DialogGUILabel(() => ThrustAt(row) != null ? FuelPlace(ThrustAt(row).Step) : "", Place),
                new DialogGUILabel(() => ThrustAt(row) != null ? FuelFlyer(ThrustAt(row).Step) : "", VehicleButton),
                new DialogGUILabel(() => ThrustNeedsText(ThrustAt(row)), FuelNeeds), new DialogGUILabel(() => ThrustHasText(ThrustAt(row)), FuelLeft),
                new DialogGUISpace(room), open);
            line.OptionEnabledCondition = () => ThrustAt(row) != null;
            yield return line;

            DialogGUIHorizontalLayout names = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline + Swatch + 4f), DimLabel("Stage", StageName_), DimLabel("Start", TwrColumn),
                DimLabel("End", TwrColumn), DimLabel("In air", TwrColumn), DimLabel("Thrust", ThrustColumn), DimLabel("Burns", BurnColumn));
            names.OptionEnabledCondition = () => OpenThrust(row) != null && OpenThrust(row).Uses.Count > 0;
            yield return names;

            for (int i = 0; i < MaxStepStages; i++)
            {
                int stage = i;
                DialogGUIHorizontalLayout use = new DialogGUIHorizontalLayout(width, RowHeight + 2f, 4f, new RectOffset(),
                    TextAnchor.MiddleLeft, new DialogGUISpace(Timeline + Swatch + 4f),
                    new DialogGUILabel(() => ThrustStageName(OpenThrust(row), stage), StageName_),
                    new DialogGUILabel(() => TwrValue(OpenThrust(row), stage, true, false), TwrColumn),
                    new DialogGUILabel(() => TwrValue(OpenThrust(row), stage, false, false), TwrColumn),
                    new DialogGUILabel(() => TwrValue(OpenThrust(row), stage, true, true), TwrColumn),
                    new DialogGUILabel(() => StageThrustText(OpenThrust(row), stage), ThrustColumn),
                    new DialogGUILabel(() => StageBurnText(OpenThrust(row), stage), BurnColumn));
                use.OptionEnabledCondition = () => OpenThrust(row) != null && stage < OpenThrust(row).Uses.Count;
                yield return use;
            }

            for (int i = 0; i < MaxFuelLines; i++)
            {
                int burn = i;
                DialogGUIHorizontalLayout text = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                    new DialogGUISpace(Timeline + Swatch + 4f), new DialogGUILabel(() => BurnName_(OpenThrust(row), burn), BurnName),
                    new DialogGUILabel(() => BurnText(OpenThrust(row), burn), width - Timeline - Swatch - 4f - BurnName - 4f * 2f - RowEnd));
                text.OptionEnabledCondition = () => OpenThrust(row) != null && burn < OpenThrust(row).Burns.Count;
                yield return text;
            }

            DialogGUIHorizontalLayout body = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline + Swatch + 4f),
                new DialogGUILabel(() => ThrustBodyText(OpenThrust(row)), width - Timeline - Swatch - 4f - 4f - RowEnd));
            body.OptionEnabledCondition = () => OpenThrust(row) != null;
            yield return body;
        }

        // What a lift-off or a set-down needs: 1.2 from a body with air, 1 otherwise.
        private double TwrNeeded(ThrustStep entry)
        {
            return Launches(steps[entry.Step]) && bodies.Bodies[entry.Body].HasAtmosphere ? 1.2 : 1.0;
        }

        // The stage that lifts off or sets down: the first of a launch, the last of a
        // landing.
        private StageBudget.StageUse Deciding(ThrustStep entry)
        {
            if (entry.Uses.Count == 0) return null;
            return Launches(steps[entry.Step]) ? entry.Uses[0] : entry.Uses[entry.Uses.Count - 1];
        }

        private string ThrustNeedsText(ThrustStep entry)
        {
            if (entry == null) return "";
            string text = entry.Twr ? "TWR " + EngineerFormat.Ratio(TwrNeeded(entry)) : "< " + Percent(MaxShare) + " orbit";
            return "<color=" + Dim + ">" + text + "</color>";
        }

        // A launch or landing: the deciding stage's TWR at the surface, in the air
        // where the body has any. Otherwise the burn that takes the largest share of
        // its orbit: its time and share.
        private string ThrustHasText(ThrustStep entry)
        {
            if (entry == null) return "";
            if (entry.Twr)
            {
                StageBudget.StageUse use = Deciding(entry);
                if (use == null) return "<color=" + Dim + ">no stage</color>";
                double twr = TwrAt(use.Stage, use.Mass, entry.Body, true);
                return "<color=" + (twr < TwrNeeded(entry) ? Problem : Good) + ">S" + use.Stage + "  " + EngineerFormat.Ratio(twr) + "</color>";
            }
            if (entry.Longest == null) return "";
            return "<color=" + BurnColour(entry.Longest) + ">" + EngineerFormat.Time(entry.Longest.Seconds) + " "
                   + Percent(entry.Longest.Share) + "</color>";
        }

        // The share of an orbit a burn may take before it loses MaxLoss of its
        // delta-v.
        private static readonly double MaxShare = ShareAt(BurnCheck.MaxLoss);

        private static double ShareAt(double loss)
        {
            double low = 0.0, high = 1.0;
            for (int i = 0; i < 40; i++)
            {
                double middle = (low + high) / 2.0;
                if (new Burn { Seconds = middle, Period = 1.0 }.Loss < loss) low = middle;
                else high = middle;
            }
            return low;
        }

        private static string Percent(double share)
        {
            return (share * 100.0).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        private static string BurnColour(Burn burn)
        {
            if (burn.Short || burn.Loss > BurnCheck.MaxLoss) return Problem;
            return burn.Loss > BurnCheck.WarnLoss ? Warning : Good;
        }

        private string ThrustStageName(ThrustStep entry, int stage)
        {
            return entry != null && stage < entry.Uses.Count ? ColouredStage(entry.Uses[stage].Stage) : "";
        }

        // As the stage starts or ends its part, in vacuum, or in the air at the surface
        // on a launch or landing where the body has any.
        private string TwrValue(ThrustStep entry, int stage, bool start, bool air)
        {
            if (entry == null || stage >= entry.Uses.Count) return "";
            if (air && (!entry.Twr || SurfaceAir(entry.Body) <= 0.0)) return "";
            StageBudget.StageUse use = entry.Uses[stage];
            double twr = TwrAt(use.Stage, start ? use.Mass : use.EndMass, entry.Body, air);
            return "<color=" + Dim + ">" + EngineerFormat.Ratio(twr) + "</color>";
        }

        private string StageThrustText(ThrustStep entry, int stage)
        {
            if (entry == null || stage >= entry.Stages.Count) return "";
            return "<color=" + Dim + ">" + entry.Stages[stage].Thrust.ToString("0", CultureInfo.InvariantCulture) + " kN</color>";
        }

        private string StageBurnText(ThrustStep entry, int stage)
        {
            if (entry == null || stage >= entry.Stages.Count) return "";
            return "<color=" + Dim + ">" + EngineerFormat.Time(entry.Stages[stage].Seconds) + "</color>";
        }

        // A burn in orbit: its name, then "4m 10s, 13% of 32m 05s, 20 m/s lost".
        private string BurnName_(ThrustStep entry, int index)
        {
            if (entry == null || index >= entry.Burns.Count) return "";
            return "<color=" + Dim + ">" + entry.Burns[index].Line.Name + "</color>";
        }

        private string BurnText(ThrustStep entry, int index)
        {
            if (entry == null || index >= entry.Burns.Count) return "";
            Burn burn = entry.Burns[index];
            string text = EngineerFormat.Time(burn.Seconds) + (burn.Short ? "+" : "") + ", " + Percent(burn.Share) + " of "
                          + EngineerFormat.Time(burn.Period);
            if (burn.Loss * burn.Line.DeltaV >= 0.5) text += ", " + EngineerFormat.Speed(burn.Loss * burn.Line.DeltaV) + " lost";
            return "<color=" + BurnColour(burn) + ">" + text + "</color>";
        }

        // "Mun: 1.63 m/s², no air", "Kerbin: 9.81 m/s², air 1.00 atm".
        private string ThrustBodyText(ThrustStep entry)
        {
            if (entry == null) return "";
            PlannerBody body = bodies.Bodies[entry.Body];
            double gravity = body.Mu / (body.Radius * body.Radius);
            string air = SurfaceAir(entry.Body) > 0.0
                ? "air " + SurfaceAir(entry.Body).ToString("0.00", CultureInfo.InvariantCulture) + " atm"
                : "no air";
            return "<color=" + Dim + ">TWR at " + body.Name + ": " + gravity.ToString("0.00", CultureInfo.InvariantCulture)
                   + " m/s², " + air + "</color>";
        }

        // A heading that opens and closes what is under it: its button before the
        // title, the whole heading answering a click, and what is under it in short.
        private const float HeadingTitle = 90f;

        private DialogGUIBase CollapsibleHeading(string title, System.Func<bool> shown, Callback toggle, string tooltip,
                                                 System.Func<string> summary)
        {
            DialogGUIButton button = new DialogGUIButton(() => shown() ? "<b><color=" + Good + ">-</color></b>" : "+", toggle, Square,
                Square, false);
            button.tooltipText = tooltip;
            DialogGUIHorizontalLayout heading = new DialogGUIHorizontalLayout(width, Square + 8f, 4f, new RectOffset(0, 0, 8, 0),
                TextAnchor.LowerLeft, button, new DialogGUILabel("<b><color=#e0e0e0>" + title + "</color></b>", HeadingTitle, RowHeight),
                new DialogGUILabel(() => Worked() ? summary() : "", width - Square - HeadingTitle - 4f * 2f - RowEnd, RowHeight));
            PlannerWidgets.Clickable(heading, toggle, after);
            return heading;
        }

        // "Enough for every step", or the steps short of fuel.
        private string FuelSummary()
        {
            int shortSteps = 0;
            foreach (double value in budget.Short) if (value > 0.5) shortSteps++;
            return shortSteps == 0
                ? "<color=" + Good + ">Enough for every step</color>"
                : "<color=" + Problem + ">" + shortSteps + (shortSteps == 1 ? " step" : " steps") + " short of fuel</color>";
        }

        // "Enough thrust", or the steps where it is too little or a burn too long.
        private string ThrustSummary()
        {
            int low = 0, slow = 0;
            foreach (ThrustStep entry in thrustSteps)
            {
                if (entry.Twr)
                {
                    StageBudget.StageUse use = Deciding(entry);
                    if (use != null && TwrAt(use.Stage, use.Mass, entry.Body, true) < TwrNeeded(entry)) low++;
                }
                else if (entry.Longest != null && BurnColour(entry.Longest) != Good) slow++;
            }
            if (low == 0 && slow == 0) return "<color=" + Good + ">Enough thrust on every step</color>";
            string text = "";
            if (low > 0) text = low + (low == 1 ? " step" : " steps") + " with too little TWR";
            if (slow > 0) text += (text.Length > 0 ? ", " : "") + slow + (slow == 1 ? " long burn" : " long burns");
            return "<color=" + (low > 0 ? Problem : Warning) + ">" + text + "</color>";
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
            float room = Mathf.Max(40f, width - Timeline - Place - (HasSections() ? VehicleButton + 4f : 0f)
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
