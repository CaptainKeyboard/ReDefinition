using System.Collections.Generic;
using ReDefinition.Window;
using UnityEngine;
using static ReDefinition.Engineer.PlanText;

namespace ReDefinition.Engineer
{
    // The planner's rows, built from KSP's dialog elements, so a UI theme reaches
    // them as it reaches KSP's dialogs. From the top come the vessel's sections. Next is the
    // route as a line of destinations, and between each two the steps that follow
    // from it. Each step shows a bar (its length the step's delta-v, its colours the stages that
    // pay for it, red what none covers). It also shows who flies it, the choices it offers, and in
    // red what stands in its way. Under the route is the bar of the whole route. Then come the flight plan the Plan button
    // works out in flight, the fuel plan, the steps with what each needs and leaves, each opening to its stages, and the thrust part. A chosen
    // option is lit in its text rather than by the skin's pressed state. A fixed
    // number of rows, shown as the plan needs them.
    internal sealed partial class PlannerView
    {
        // How many stops, stages and bar pieces the fixed rows are made for; in units, the icons' size, the timeline's
        // column and a stop's letter.
        private const int MaxStages = 30;
        private const int MaxPieces = 24;
        private const float Icon = 24f;
        private const float Timeline = 20f;
        private const float LetterWidth = 14f;
        // Every button's height, and the size of the square ones: the cross, "..." and +.
        private const float Square = RowHeight + 2f;
        // What a stop's and a step's rows leave free at their right end, and the gap between the cells of a row.
        private const float RowEnd = 2f;
        private const float Gap = 4f;
        // The line between a stop's Surface and Orbit and a further choice.
        private const float Divider = 1f;
        private const float BarHeight = 10f;
        private const float RowHeight = 20f;
        // The fuel plan's stage rows: the swatch, the stage's name, Drop after.
        private const float Swatch = 8f;
        private const float StageName_ = 38f;
        private const float DropButton = 80f;
        // Where a step goes, and the room left of a bar.
        private const float Place = 145f;
        private const float VehicleButton = 84f;
        private const float StepSpeed_ = 72f;
        // A further choice's name, so that its buttons start under who flies the step.
        private const float OptionLabel = Place - BarIndent - 4f;
        private const float BarIndent = 16f;

        // Each step's bar, by leg and role; the altitude fields, for the typing lock; the roles in the order their steps come.
        private readonly SegmentBar[,] stepBars;
        private readonly List<DialogGUITextInput> altitudeFields = new List<DialogGUITextInput>();
        private static readonly StepRole[] Roles = { StepRole.Launch, StepRole.Transfer, StepRole.Arrive, StepRole.Land };
        // The steps whose further choices are open ("...").
        private readonly bool[,] more;
        // After each step's bar, the room its bar leaves, so that the figure stands in
        // one column: the scroll list stretches its rows under its bar, and a flexible
        // space would push the figure there.
        private readonly Sizer[,] barFillers;
        private SegmentBar totalBar;

        // Every row of the planner, made once: the note while KSP's delta-v simulation is off, then the Craft, Route, Fuel
        // plan and Thrust parts, each row shown as the plan needs it.
        internal List<DialogGUIBase> Rows()
        {
            altitudeFields.Clear();
            headerTints.Clear();
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            rows.Add(StockOffRow());
            if (Controls != null)
                rows.AddRange(Section("Guidance", () => controlsShown, () => { controlsShown = !controlsShown; model.Touch(); },
                    "What flies the route: the navigation keeps the plan up to date while you fly, the autopilot flies it.", GuidanceSummary, GuidanceRows()));

            List<DialogGUIBase> vessel = new List<DialogGUIBase>();
            for (int i = 0; i < PlanSections.MaxSections; i++) vessel.AddRange(SectionRows(i));
            vessel.Add(AddSectionRow());
            rows.AddRange(Section("Craft", () => vesselShown, () => { vesselShown = !vesselShown; model.Touch(); },
                "The craft as built: its mass, the fuel in it and its delta-v in vacuum. Below, its sections.", model.VesselText, vessel));

            List<DialogGUIBase> route = new List<DialogGUIBase>();
            route.Add(SettingsRow());
            route.Add(new DialogGUISpace(6f));
            for (int i = 0; i < MaxStops; i++)
            {
                route.AddRange(StopRows(i));
                foreach (StepRole role in Roles)
                    route.AddRange(StepBlock(i, role));
            }
            route.Add(AddRow());
            route.Add(ClearRow());
            route.AddRange(SummaryRows());
            rows.AddRange(Section("Route", () => routeShown, () => { routeShown = !routeShown; model.Touch(); },
                "The destinations and the steps between them.", RouteSummary, route));

            rows.AddRange(Section("Flight plan", () => planShown, () => { planShown = !planShown; model.Touch(); },
                "The burns and orbits of the route as the Plan button in flight works them out from the vessel.", PlanSummary, FlightPlanRows()));
            rows.AddRange(Section("Fuel plan", () => fuelShown, () => { fuelShown = !fuelShown; model.Touch(); },
                "The delta-v each step needs and what remains after it.", model.Fuel.Summary, FuelRows()));
            rows.AddRange(Section("Thrust", () => thrustShown, () => { thrustShown = !thrustShown; model.Touch(); },
                "The thrust-to-weight ratio of launches and landings and the duration of each burn.", model.Thrust.Summary, ThrustRows()));
            return rows;
        }

        // Over everything, while KSP's delta-v simulation does not run: the planner
        // takes its stages from it (EditorReadout), and without them it stays empty.
        private DialogGUIBase StockOffRow()
        {
            DialogGUIHorizontalLayout row = new GatedRow(width, 2f * RowHeight + 4f, 0f, new RectOffset(0, 0, 2, 2),
                TextAnchor.MiddleLeft,
                new DialogGUILabel("<color=" + Warning + ">KSP's delta-v calculations are off (DELTAV_CALCULATIONS_ENABLED in "
                                   + "settings.cfg, or another mod): the planner has no stages to plan with.</color>",
                    width - RowEnd, 2f * RowHeight));
            row.OptionEnabledCondition = () => EditorReadout.StockOff;
            return row;
        }

        // A part of the planner as KSP's crew and cargo panels show theirs: a lighter
        // header bar, and under it its rows on a darker box, hidden with `shown` where
        // it folds; a gap before the next.
        private const float SectionGap = 10f;
        private const float HeaderHeight = 26f;
        // Between a box's edge and its rows, on each side.
        private const float BoxMargin = 8f;
        private static readonly Color HeaderColour = new Color(1f, 1f, 1f, 0.10f);
        private static readonly Color BoxColour = new Color(0f, 0f, 0f, 0.22f);

        // Whether the Craft and Route parts are open.
        private bool vesselShown = true;
        private bool routeShown = true;

        // Folded, what the route leaves.
        private string RouteSummary()
        {
            return routeShown ? "" : model.SummaryText();
        }

        // The header bars' backgrounds, rounded at the top while their part is open and
        // all round while it is folded (Keep).
        private readonly List<KeyValuePair<TintImage, System.Func<bool>>> headerTints =
            new List<KeyValuePair<TintImage, System.Func<bool>>>();

        // One part of the planner: its heading, which folds it, and its rows on a darker box.
        private IEnumerable<DialogGUIBase> Section(string title, System.Func<bool> shown, Callback toggle, string tooltip,
                                                   System.Func<string> summary, IEnumerable<DialogGUIBase> rows)
        {
            yield return CollapsibleHeading(title, shown, toggle, tooltip, summary);
            DialogGUIVerticalLayout box = new GatedColumn(outer, -1f, 0f,
                new RectOffset((int)BoxMargin, (int)BoxMargin, 8, 10), TextAnchor.UpperLeft, new List<DialogGUIBase>(rows).ToArray());
            box.OptionEnabledCondition = shown;
            Tint.Behind(box, BoxColour, 0f, outer, after, false, Rounded.Get(false, true));
            yield return box;
            yield return new DialogGUISpace(SectionGap);
        }

        // Every frame: each heading's corners as its part is open or folded.
        private void ShapeHeaders()
        {
            foreach (KeyValuePair<TintImage, System.Func<bool>> header in headerTints)
                header.Key.Shape(Rounded.Get(true, !header.Value()));
        }

        // The route's settings: the fuel margin and the plane changes.
        private DialogGUIBase SettingsRow()
        {
            DialogGUILabel label = new DialogGUILabel("Fuel margin", 84f, RowHeight);
            label.tooltipText = "Extra delta-v added to every step as a reserve for piloting losses and corrections.";
            DialogGUISlider margin = new DialogGUISlider(() => (float)(model.Ready() ? model.State.Margin * 100.0 : 10.0), 0f, 50f, true,
                width - 84f - 44f - 6f * 2f - RowEnd, RowHeight, model.SetMargin);
            margin.tooltipText = label.tooltipText;
            return new GatedRow(width, RowHeight + 4f, 6f, new RectOffset(), TextAnchor.MiddleLeft,
                label, margin,
                new DialogGUILabel(Text(() => (model.Ready() ? (model.State.Margin * 100.0).ToString("0") : "10") + " %"), 44f));
        }

        // -------------------------------------------------------------- sections

        // The rows of one section. They show what it is in the column of a step's place, its parts
        // on a button from the column of who flies a step, and its cross where a stop's cross is.
        // Under a satellite they show where it stays, and in red what is wrong with it.
        private IEnumerable<DialogGUIBase> SectionRows(int row)
        {
            PlanSections sections = model.Sections;
            DialogGUIButton group = null;
            group = new DialogGUIButton(() => sections.SectionText(row), () =>
            {
                LiveSection section = sections.RowSection(row);
                if (section != null) PickSectionParts(section.Plan, group);
            }, PartButtonWidth - Square - 4f, Square, false);
            group.tooltipText = "Choose which parts of the vessel form this section.";
            DialogGUIButton remove = PlannerWidgets.IconButton("Remove", () => sections.RemoveSection(row), Square, Square, 14f,
                "Remove this section.", after);
            DialogGUIHorizontalLayout dot = new GatedRow(Timeline, Square, 0f, new RectOffset(4, 0, 0, 0),
                TextAnchor.MiddleLeft, PlannerWidgets.Icon("Dot", 12f, PlannerColors.Line), new DialogGUISpace(Timeline - 16f));
            DialogGUIHorizontalLayout line = new GatedRow(width, Square + 4f, 4f, new RectOffset(0, 0, 2, 0),
                TextAnchor.MiddleLeft, dot, new DialogGUILabel(Text(() => sections.SectionLabel(row)), SectionName), group, remove);
            line.OptionEnabledCondition = () => sections.RowSection(row) != null;
            yield return line;

            DialogGUIButton stop = null;
            stop = new DialogGUIButton(() => sections.DeployText(row), () => PickDeployStop(row, stop), PartButtonWidth, Square, false);
            stop.tooltipText = "Choose the destination where the satellite is deployed.";
            DialogGUIHorizontalLayout deploy = new GatedRow(width, Square + 2f, 4f, new RectOffset(0, 0, 2, 0),
                TextAnchor.MiddleLeft, new DialogGUISpace(Timeline), new DialogGUILabel("<color=" + Dim + ">Deployed at</color>", SectionName),
                stop);
            deploy.OptionEnabledCondition = () => sections.IsKind(row, SectionKind.Satellite);
            yield return deploy;

            DialogGUIHorizontalLayout warning = new GatedRow(width, RowHeight, Gap, new RectOffset(),
                TextAnchor.MiddleLeft, new DialogGUISpace(Timeline), new DialogGUISpace(SectionName),
                new DialogGUILabel(Text(() => "<color=" + Problem + ">" + sections.SectionWarning(row) + "</color>"), PartButtonWidth, RowHeight));
            warning.OptionEnabledCondition = () => sections.SectionWarning(row).Length > 0;
            yield return warning;

        }

        // "+" to add a section, in the column of a section's name.
        private DialogGUIBase AddSectionRow()
        {
            DialogGUIButton add = null;
            add = new DialogGUIButton("<b>+</b>", () => PickNewSection(add), Square, Square, false);
            add.tooltipText = "Add a section: a capsule, a lander or a satellite.";
            DialogGUIHorizontalLayout row = new GatedRow(width, Square + 2f, 4f, new RectOffset(0, 0, 2, 0),
                TextAnchor.MiddleLeft, new DialogGUISpace(Timeline), add);
            row.OptionEnabledCondition = () => model.Ready() && model.State.Sections.Count < PlanSections.MaxSections;
            return row;
        }

        // A section's name, then its buttons up to where a stop's cross ends.
        private const float SectionName = 90f;

        // A section's parts button, from the column of who flies a step to the rows' end; 120 wide at least.
        private float PartButtonWidth
        {
            get { return Mathf.Max(120f, width - Timeline - SectionName - Gap * 2f - RowEnd); }
        }

        // ----------------------------------------------------------------- stops

        // A stop's rows: its dot, letter and body, Surface or Orbit, the use of its air and its cross; under an orbit, its
        // altitude.
        private IEnumerable<DialogGUIBase> StopRows(int index)
        {
            // KSP's layouts take no width of their own (DialogGUILayoutBase,
            // decompiled): the space after the dot makes the column as wide as
            // the timeline's.
            DialogGUIHorizontalLayout dot = new GatedRow(Timeline, Icon, 0f, new RectOffset(4, 0, 0, 0),
                TextAnchor.MiddleLeft, PlannerWidgets.Icon("Dot", 12f, PlannerColors.Line), new DialogGUISpace(Timeline - 16f));
            // The body's button ends where a step's place does, so that Surface stands
            // over who flies a step; the room the others leave stands after the
            // choices, so that the cross ends where a step's "..." does: 2 units short
            // of the rows' room. Ten elements and nine gaps: Surface and Orbit set off
            // by a line from the body before and from the atmosphere's choice after.
            const float bodyWidth = Place - LetterWidth - Divider - 8f;
            float room = Mathf.Max(0f, width - Timeline - LetterWidth - bodyWidth - Divider - 62f - 52f - Divider - 76f - Square
                                       - Gap * 9f - RowEnd);
            DialogGUIButton body = new DialogGUIButton(() => "<b>" + model.BodyName(index) + "</b>", () => PickBody(index), bodyWidth,
                RowHeight + 2f, false);
            bodyButtons[index] = body;
            body.tooltipText = "Choose the body of this destination.";
            DialogGUIButton surface = Choice("Surface", () => model.Kind(index) == StopKind.Surface, () => model.SetKind(index, StopKind.Surface),
                62f);
            surface.tooltipText = "Land on the body's surface.";
            DialogGUIButton orbit = Choice("Orbit", () => model.Kind(index) == StopKind.Orbit, () => model.SetKind(index, StopKind.Orbit), 52f);
            orbit.tooltipText = "Stay in orbit around the body.";
            DialogGUIButton air = Choice(() => model.Kind(index) == StopKind.Surface ? "Chutes" : "Aerobrake", () => model.UsesAtmosphere(index),
                () => model.ToggleAtmosphere(index), 76f);
            air.OptionEnabledCondition = () => index > 0 && model.HasAtmosphere(index);
            air.tooltipText = "Use the atmosphere instead of the engines: parachutes for landing, aerobraking for orbit.";
            DialogGUIBase divider = PlannerWidgets.Block(Divider, RowHeight - 4f, PlannerColors.Line);
            divider.OptionEnabledCondition = air.OptionEnabledCondition;
            // Its room and the line's where it is hidden, so that the cross stands in
            // one column on every stop.
            DialogGUISpace noAir = new DialogGUISpace(Divider + 4f + 76f);
            noAir.OptionEnabledCondition = () => !(index > 0 && model.HasAtmosphere(index));
            // The last destination's cross empties the route, a flight under way stopped first: the plan can always be deleted where it is chosen.
            DialogGUIButton remove = PlannerWidgets.IconButton("Remove", () => RemoveOrClear(index), Square, Square, 14f,
                "Remove this destination; the last one empties the route.", after);
            remove.OptionEnabledCondition = () => model.Ready() && (model.State.Stops.Count > 2 || (index == 1 && !model.RouteEmpty));

            DialogGUIHorizontalLayout row = new GatedRow(width, Icon + 4f, 4f, new RectOffset(0, 0, 2, 0),
                TextAnchor.MiddleLeft, dot, new DialogGUILabel(Text(() => "<b>" + Letter(index) + "</b>"), LetterWidth), body,
                PlannerWidgets.Block(Divider, RowHeight - 4f, PlannerColors.Line), surface, orbit, divider, air, noAir,
                new DialogGUISpace(room), remove);
            row.OptionEnabledCondition = () => model.Shown(index);
            yield return row;

            // The orbit's altitude: typed in kilometres, or stepped by 10 km.
            DialogGUITextInput typed = new DialogGUITextInput("", false, 7, text =>
            {
                model.SetAltitude(index, text);
                return text;
            }, () => model.AltitudeKm(index), TMPro.TMP_InputField.ContentType.DecimalNumber, Square);
            typed.size = new Vector2(72f, Square);
            DialogGUIButton lower = new DialogGUIButton("-", () => model.StepAltitude(index, -1), Square, Square, false);
            lower.tooltipText = "Lower the orbit by 10 km.";
            DialogGUIButton raise = new DialogGUIButton("+", () => model.StepAltitude(index, 1), Square, Square, false);
            raise.tooltipText = "Raise the orbit by 10 km.";
            altitudeFields.Add(typed);
            DialogGUIHorizontalLayout altitude = new GatedRow(width, RowHeight + 2f, Gap, new RectOffset(),
                TextAnchor.MiddleLeft, TimelineLine(RowHeight + 2f), new DialogGUISpace(LetterWidth),
                lower, typed, new DialogGUILabel("<color=" + Dim + ">km</color>", 24f), raise);
            altitude.OptionEnabledCondition = () => model.HasBody(index) && model.Kind(index) == StopKind.Orbit;
            yield return altitude;

            // The orbit's shape: an apoapsis above the altitude (the altitude is then the periapsis) and an inclination, both optional.
            DialogGUITextInput apoapsisField = new DialogGUITextInput("", false, 7, text =>
            {
                model.SetApoapsis(index, text);
                return text;
            }, () => model.ApoapsisKm(index), TMPro.TMP_InputField.ContentType.DecimalNumber, Square);
            apoapsisField.size = new Vector2(72f, Square);
            DialogGUITextInput inclinationField = new DialogGUITextInput("", false, 6, text =>
            {
                model.SetInclination(index, text);
                return text;
            }, () => model.InclinationText(index), TMPro.TMP_InputField.ContentType.DecimalNumber, Square);
            inclinationField.size = new Vector2(60f, Square);
            altitudeFields.Add(apoapsisField);
            altitudeFields.Add(inclinationField);
            DialogGUIHorizontalLayout shape = new GatedRow(width, RowHeight + 2f, Gap, new RectOffset(),
                TextAnchor.MiddleLeft, TimelineLine(RowHeight + 2f), new DialogGUISpace(LetterWidth),
                new DialogGUILabel("<color=" + Dim + ">Ap</color>", 18f), apoapsisField, new DialogGUILabel("<color=" + Dim + ">km</color>", 24f),
                new DialogGUILabel("<color=" + Dim + ">Inc</color>", 24f), inclinationField, new DialogGUILabel("<color=" + Dim + ">deg</color>", 30f));
            shape.OptionEnabledCondition = () => model.HasBody(index) && model.Kind(index) == StopKind.Orbit && index > 0;
            yield return shape;
        }

        // After the last stop: its dot, the next letter and "+" to add it there.
        private DialogGUIBase AddRow()
        {
            DialogGUIHorizontalLayout dot = new GatedRow(Timeline, Icon, 0f, new RectOffset(4, 0, 0, 0),
                TextAnchor.MiddleLeft, PlannerWidgets.Icon("Dot", 12f, PlannerColors.Line), new DialogGUISpace(Timeline - 16f));
            DialogGUIButton add = new DialogGUIButton("<b>+</b>", model.AddStop, Square, Square, false);
            add.tooltipText = "Add a destination at the end of the route.";
            DialogGUIHorizontalLayout row = new GatedRow(width, Icon + 4f, 4f, new RectOffset(0, 0, 2, 0),
                TextAnchor.MiddleLeft, dot,
                new DialogGUILabel(Text(() => "<b><color=" + Dim + ">" + (model.Ready() ? Letter(model.State.Stops.Count) : "") + "</color></b>"), LetterWidth),
                add);
            // Once every destination has its body.
            row.OptionEnabledCondition = model.CanAddStop;
            return row;
        }

        // Under the destinations: one button that removes them all. It stays in view and is dimmed where there is nothing to remove.
        private DialogGUIBase ClearRow()
        {
            DialogGUIButton clear = new DialogGUIButton("Clear Route", ClearPlan, 110f, Square, false);
            clear.tooltipText = "Remove every destination and start the route over; a flight under way is stopped.";
            clear.OptionInteractableCondition = () => model.Ready() && (!model.RouteEmpty || model.State.Stops.Count > 2);
            return new GatedRow(width, Square + 2f, 4f, new RectOffset(0, 0, 2, 0), TextAnchor.MiddleLeft, new DialogGUISpace(Timeline), clear);
        }

        // The timeline's line through a row of that height.
        private static DialogGUIBase TimelineLine(float height)
        {
            return new GatedRow(Timeline, height, 0f, new RectOffset(9, 0, 0, 0), TextAnchor.MiddleLeft,
                PlannerWidgets.Block(2f, height, PlannerColors.Line), new DialogGUISpace(Timeline - 11f));
        }

        // A button of KSP's skin whose text is lit while its option is chosen.
        private DialogGUIButton Choice(string label, System.Func<bool> chosen, Callback choose, float buttonWidth)
        {
            return Choice(() => label, chosen, choose, buttonWidth);
        }

        // As above, with a label that may change. The text is made again only where the model's version has moved on: a click or a new plan moves it.
        private DialogGUIButton Choice(System.Func<string> label, System.Func<bool> chosen, Callback choose, float buttonWidth)
        {
            return new DialogGUIButton(Text(() => chosen()
                ? "<b><color=" + Good + ">" + label() + "</color></b>"
                : "<color=" + Dim + ">" + label() + "</color>"), choose, buttonWidth, RowHeight + 2f, false);
        }

        // ----------------------------------------------------------------- steps

        // A step's rows on a lighter background, right of the timeline, with a gap before
        // it through which the timeline runs on: one block per step between two
        // destinations.
        private IEnumerable<DialogGUIBase> StepBlock(int leg, StepRole role)
        {
            System.Func<bool> shown = () => model.Slot(leg, role) >= 0;
            DialogGUIHorizontalLayout gap = new GatedRow(width, 4f, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                TimelineLine(4f));
            gap.OptionEnabledCondition = shown;
            yield return gap;
            List<DialogGUIBase> inner = new List<DialogGUIBase>(StepRows(leg, role));
            DialogGUIVerticalLayout block = new GatedColumn(width, -1f, 0f, new RectOffset(), TextAnchor.UpperLeft,
                inner.ToArray());
            block.OptionEnabledCondition = shown;
            Tint.Behind(block, PlannerColors.StepBackground, Timeline, width, after);
            yield return block;
        }

        // A step on the way from destination `leg` to the next, on one line in fixed
        // columns. The line shows where it goes, who flies it (a button opening the list of who can),
        // its bar, its delta-v and "..." for its further choices. Around it only where
        // there is something: a grey line where something leaves at its start or the
        // lander docks at its end, the further choices while "..." is open, and in red
        // what blocks it.
        private IEnumerable<DialogGUIBase> StepRows(int leg, StepRole role)
        {
            System.Func<bool> shown = () => model.Slot(leg, role) >= 0;

            DialogGUIHorizontalLayout leaves = new GatedRow(width, RowHeight, Gap, new RectOffset(),
                TextAnchor.MiddleLeft, TimelineLine(RowHeight), new DialogGUISpace(BarIndent),
                new DialogGUILabel(Text(() => "<color=" + Dim + ">" + model.SeparationText(leg, role) + "</color>"),
                    width - Timeline - BarIndent - 8f, RowHeight));
            leaves.OptionEnabledCondition = Cached(() => model.SeparationText(leg, role).Length > 0);
            yield return leaves;

            DialogGUIButton vehicle = null;
            vehicle = new DialogGUIButton(() => model.VehicleText(leg, role), () => PickFlyer(leg, role, vehicle), VehicleButton,
                RowHeight + 2f, false);
            vehicle.tooltipText = "Choose which part of the vessel flies this step.";
            vehicle.OptionEnabledCondition = model.Sections.HasSections;
            SegmentBar bar = new SegmentBar(MaxPieces, BarHeight, after);
            stepBars[leg, (int)role] = bar;
            DialogGUILabel speed = new DialogGUILabel(Text(() => model.StepSpeed(leg, role)), StepSpeed_, RowHeight);
            DialogGUIButton open = new DialogGUIButton(() => more[leg, (int)role] ? "<b><color=" + Good + ">...</color></b>" : "...",
                () => { more[leg, (int)role] = !more[leg, (int)role]; model.Touch(); }, Square, Square, false);
            open.tooltipText = "More options for this step.";
            System.Func<bool> hasMore = Cached(() => model.HasMore(leg, role));
            open.OptionEnabledCondition = hasMore;
            DialogGUISpace noMore = new DialogGUISpace(Square);
            noMore.OptionEnabledCondition = () => !hasMore();
            Sizer filler = new Sizer(after);
            barFillers[leg, (int)role] = filler;
            DialogGUIHorizontalLayout row = new GatedRow(width, Icon + 2f, Gap, new RectOffset(), TextAnchor.MiddleLeft,
                TimelineLine(Icon + 2f), new DialogGUILabel(Text(() => model.StepPlace(leg, role)), Place), vehicle, bar.Layout,
                filler.Add(new DialogGUISpace(1f)), speed, open, noMore);
            row.OptionEnabledCondition = shown;
            yield return row;

            DialogGUIButton refuel = Choice("Refuel", () => model.Refuels(leg, role), () => model.ToggleRefuel(leg, role), 64f);
            refuel.tooltipText = "Refuel this section from the rest of the vessel when it docks.";
            DialogGUIButton tanker = null;
            tanker = new DialogGUIButton(() => model.TankerText(leg, role), () => PickTanker(leg, role, tanker), 150f, RowHeight + 2f, false);
            tanker.tooltipText = "Choose the stage of the rest of the vessel to refuel from first.";
            tanker.OptionEnabledCondition = () => model.Refuels(leg, role);
            DialogGUIHorizontalLayout docks = new GatedRow(width, RowHeight + 4f, Gap, new RectOffset(),
                TextAnchor.MiddleLeft, TimelineLine(RowHeight + 4f), new DialogGUISpace(BarIndent),
                new DialogGUILabel("<color=" + Dim + ">Docks with the rest</color>", OptionLabel), refuel, tanker);
            docks.OptionEnabledCondition = () => model.Docks(leg, role);
            yield return docks;

            DialogGUIHorizontalLayout stays = new GatedRow(width, RowHeight, Gap, new RectOffset(),
                TextAnchor.MiddleLeft, TimelineLine(RowHeight), new DialogGUISpace(BarIndent),
                new DialogGUILabel(Text(() => "<color=" + Dim + ">" + model.DeployedText(leg, role) + "</color>"), width - Timeline - BarIndent - 8f,
                    RowHeight));
            stays.OptionEnabledCondition = Cached(() => model.DeployedText(leg, role).Length > 0);
            yield return stays;

            // Behind "...", each on a line of its own, as they concern different
            // things: the way down, the burns across.
            DialogGUIButton straight = Choice("Straight in", () => !model.OrbitFirst(leg + 1), () => model.SetOrbitFirst(leg + 1, false), 88f);
            straight.tooltipText = "Enter the atmosphere directly and land.";
            DialogGUIButton orbitFirst = Choice("Orbit first", () => model.OrbitFirst(leg + 1), () => model.SetOrbitFirst(leg + 1, true), 84f);
            orbitFirst.tooltipText = "Brake into a low orbit first, then land.";
            yield return OptionLine(leg, role, "Way down", () => model.IntoTheAir(leg, role), straight, orbitFirst);

            DialogGUIButton together = Choice("Together", () => !model.SplitsBurns(leg + 1), () => model.SetSplitBurns(leg + 1, false), 88f);
            together.tooltipText = "Plan the departure and arrival burns as one step.";
            DialogGUIButton split = Choice("Split", () => model.SplitsBurns(leg + 1), () => model.SetSplitBurns(leg + 1, true), 84f);
            split.tooltipText = "Plan the departure and arrival burns as separate steps, each with its own flyer.";
            yield return OptionLine(leg, role, "Burns", Cached(() => role == StepRole.Transfer && model.CanSplit(leg)), together, split);

            for (int i = 0; i < PlannerModel.MaxWarnings; i++)
            {
                int line = i;
                DialogGUIHorizontalLayout warning = new GatedRow(width, RowHeight, Gap, new RectOffset(),
                    TextAnchor.MiddleLeft, TimelineLine(RowHeight), new DialogGUISpace(BarIndent),
                    new DialogGUILabel(Text(() => "<color=" + Problem + ">" + model.WarningText(leg, role, line) + "</color>"),
                        width - Timeline - BarIndent - 8f, RowHeight));
                warning.OptionEnabledCondition = () => model.WarningText(leg, role, line).Length > 0;
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
            DialogGUIHorizontalLayout line = new GatedRow(width, RowHeight + 4f, Gap, new RectOffset(),
                TextAnchor.MiddleLeft, elements.ToArray());
            line.OptionEnabledCondition = () => more[leg, (int)role] && model.Slot(leg, role) >= 0 && applies();
            return line;
        }

        // --------------------------------------------------------------- summary

        // Under the route: the bar of the whole route's stages, and what is left and needed.
        private IEnumerable<DialogGUIBase> SummaryRows()
        {
            yield return new DialogGUISpace(8f);
            totalBar = new SegmentBar(MaxStages + 2, BarHeight + 4f, after);
            yield return new GatedRow(width, BarHeight + 6f, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                totalBar.Layout, new DialogGUIFlexibleSpace());
            yield return new GatedRow(width, RowHeight + 2f, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel(model.SummaryText, width - RowEnd, RowHeight));
        }

        // A heading that opens and closes what is under it: the whole bar answers a
        // click, with its title, what is under it in short, and "+" or "-" at the right.
        private const float HeadingTitle = 90f;
        private const float SignEdge = 12f;

        // A part's heading: its title, its summary where the plan is worked out, and the sign; the whole bar folds the part.
        private DialogGUIBase CollapsibleHeading(string title, System.Func<bool> shown, Callback toggle, string tooltip,
                                                 System.Func<string> summary)
        {
            DialogGUILabel titleLabel = new DialogGUILabel("<b><color=#e0e0e0>" + title + "</color></b>", HeadingTitle, RowHeight);
            // The sign at the bar's right end, a little in from its edge.
            DialogGUILabel sign = new DialogGUILabel(
                () => "<align=right><b><size=120%><color=#e0e0e0>" + (shown() ? "-" : "+") + "</color></size></b></align>", Square, RowHeight);
            DialogGUIHorizontalLayout heading = new GatedRow(outer, HeaderHeight, 4f, new RectOffset((int)BoxMargin, 0, 0, 0),
                TextAnchor.MiddleLeft, titleLabel,
                new DialogGUILabel(Text(() => model.Worked() ? summary() : ""), outer - BoxMargin - HeadingTitle - Square - Gap * 2f - SignEdge, RowHeight),
                sign);
            // On the bar itself: the pointer enters its background, not its labels.
            heading.tooltipText = tooltip;
            headerTints.Add(new KeyValuePair<TintImage, System.Func<bool>>(
                Tint.Behind(heading, HeaderColour, 0f, outer, after, true, Rounded.Get(true, false)), shown));
            PlannerWidgets.Clickable(heading, toggle, after);
            return heading;
        }
    }
}
