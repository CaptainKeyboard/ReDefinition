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
        private const float StageName_ = 38f;
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
            headerTints.Clear();
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            rows.Add(StockOffRow());

            List<DialogGUIBase> vessel = new List<DialogGUIBase>();
            for (int i = 0; i < MaxSections; i++) vessel.AddRange(SectionRows(i));
            vessel.Add(AddSectionRow());
            rows.AddRange(Section("Craft", () => vesselShown, () => { vesselShown = !vesselShown; textVersion++; },
                "The craft as built: its mass, the fuel in it and its delta-v in vacuum. Below, its sections.", VesselText, vessel));

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
            route.AddRange(SummaryRows());
            rows.AddRange(Section("Route", () => routeShown, () => { routeShown = !routeShown; textVersion++; },
                "The destinations and the steps between them.", RouteSummary, route));

            rows.AddRange(Section("Fuel plan", () => fuelShown, () => { fuelShown = !fuelShown; textVersion++; },
                "The delta-v each step needs and what remains after it.", FuelSummary, FuelRows()));
            rows.AddRange(Section("Thrust", () => thrustShown, () => { thrustShown = !thrustShown; textVersion++; },
                "The thrust-to-weight ratio of launches and landings and the duration of each burn.", ThrustSummary, ThrustRows()));
            return rows;
        }

        // Over everything, while KSP's delta-v simulation does not run: the planner
        // takes its stages from it (EditorReadout), and without them it stays empty.
        private DialogGUIBase StockOffRow()
        {
            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, 2f * RowHeight + 4f, 0f, new RectOffset(0, 0, 2, 2),
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

        private bool vesselShown = true;
        private bool routeShown = true;

        // Folded, what the route leaves.
        private string RouteSummary()
        {
            return routeShown ? "" : SummaryText();
        }

        // The header bars' backgrounds, rounded at the top while their part is open and
        // all round while it is folded (Keep).
        private readonly List<KeyValuePair<TintImage, System.Func<bool>>> headerTints =
            new List<KeyValuePair<TintImage, System.Func<bool>>>();

        private IEnumerable<DialogGUIBase> Section(string title, System.Func<bool> shown, Callback toggle, string tooltip,
                                                   System.Func<string> summary, IEnumerable<DialogGUIBase> rows)
        {
            yield return CollapsibleHeading(title, shown, toggle, tooltip, summary);
            DialogGUIVerticalLayout box = new DialogGUIVerticalLayout(outer, -1f, 0f,
                new RectOffset((int)BoxMargin, (int)BoxMargin, 8, 10), TextAnchor.UpperLeft, new List<DialogGUIBase>(rows).ToArray());
            box.OptionEnabledCondition = shown;
            Tint.Behind(box, BoxColour, 0f, outer, after, false, Rounded.Get(false, true));
            yield return box;
            yield return new DialogGUISpace(SectionGap);
        }

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
            DialogGUISlider margin = new DialogGUISlider(() => (float)(Ready() ? state.Margin * 100.0 : 10.0), 0f, 50f, true,
                width - 84f - 44f - 136f - 6f * 3f - RowEnd, RowHeight, SetMargin);
            margin.tooltipText = label.tooltipText;
            DialogGUIToggle planes = new DialogGUIToggle(() => Ready() && state.PlaneChanges, "Plane changes", SetPlaneChanges, 136f);
            planes.tooltipText = "Add the delta-v to match the target's orbital inclination on each transfer, such as 6° for "
                                 + "Minmus. Off, all orbits are taken as lying in one plane.";
            return new DialogGUIHorizontalLayout(width, RowHeight + 4f, 6f, new RectOffset(), TextAnchor.MiddleLeft,
                label, margin,
                new DialogGUILabel(Text(() => (Ready() ? (state.Margin * 100.0).ToString("0") : "10") + " %"), 44f), planes);
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
            group.tooltipText = "Choose which parts of the vessel form this section.";
            DialogGUIButton remove = PlannerWidgets.IconButton("Remove", () => RemoveSection(row), Square, Square, 14f,
                "Remove this section.", after);
            DialogGUIHorizontalLayout dot = new DialogGUIHorizontalLayout(Timeline, Square, 0f, new RectOffset(4, 0, 0, 0),
                TextAnchor.MiddleLeft, PlannerWidgets.Icon("Dot", 12f, PlannerColors.Line), new DialogGUISpace(Timeline - 16f));
            DialogGUIHorizontalLayout line = new DialogGUIHorizontalLayout(width, Square + 4f, 4f, new RectOffset(0, 0, 2, 0),
                TextAnchor.MiddleLeft, dot, new DialogGUILabel(Text(() => SectionLabel(row)), SectionName), group, remove);
            line.OptionEnabledCondition = () => RowSection(row) != null;
            yield return line;

            DialogGUIButton stop = null;
            stop = new DialogGUIButton(() => DeployText(row), () => PickDeployStop(row, stop), PartButtonWidth, Square, false);
            stop.tooltipText = "Choose the destination where the satellite is deployed.";
            DialogGUIHorizontalLayout deploy = new DialogGUIHorizontalLayout(width, Square + 2f, 4f, new RectOffset(0, 0, 2, 0),
                TextAnchor.MiddleLeft, new DialogGUISpace(Timeline), new DialogGUILabel("<color=" + Dim + ">Deployed at</color>", SectionName),
                stop);
            deploy.OptionEnabledCondition = () => IsKind(row, SectionKind.Satellite);
            yield return deploy;

            DialogGUIHorizontalLayout warning = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(),
                TextAnchor.MiddleLeft, new DialogGUISpace(Timeline), new DialogGUISpace(SectionName),
                new DialogGUILabel(Text(() => "<color=" + Problem + ">" + SectionWarning(row) + "</color>"), PartButtonWidth, RowHeight));
            warning.OptionEnabledCondition = () => SectionWarning(row).Length > 0;
            yield return warning;

        }

        // "Mass 45.20 t   Fuel 33.10 t   Δv 5,230 m/s".
        private string VesselText()
        {
            EditorReadout readout = owner.Readout;
            if (readout == null || !readout.HasShip) return "";
            // The planner's stages: its own, with the jets' part of the launch, where an
            // engine breathes air.
            double deltaV = 0.0;
            foreach (StageRow row in allStages) deltaV += row.DeltaVVacuum;
            return "<color=" + Dim + ">Mass</color> " + EngineerFormat.Mass(readout.WetMass) + "<pos=34%><color=" + Dim + ">Fuel</color> "
                   + EngineerFormat.Mass(readout.FuelMass) + "<pos=67%><color=" + Dim + ">Δv</color> " + EngineerFormat.Speed(deltaV);
        }

        // "+" to add a section, in the column of a section's name.
        private DialogGUIBase AddSectionRow()
        {
            DialogGUIButton add = null;
            add = new DialogGUIButton("<b>+</b>", () => PickNewSection(add), Square, Square, false);
            add.tooltipText = "Add a section: a capsule, a lander or a satellite.";
            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, Square + 2f, 4f, new RectOffset(0, 0, 2, 0),
                TextAnchor.MiddleLeft, new DialogGUISpace(Timeline), add);
            row.OptionEnabledCondition = () => Ready() && state.Sections.Count < MaxSections;
            return row;
        }

        // A section's name, then its buttons up to where a stop's cross ends.
        private const float SectionName = 90f;

        private float PartButtonWidth
        {
            get { return Mathf.Max(120f, width - Timeline - SectionName - 4f * 2f - RowEnd); }
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
            air.tooltipText = "Use the atmosphere instead of the engines: parachutes for landing, aerobraking for orbit.";
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
                TextAnchor.MiddleLeft, dot, new DialogGUILabel(Text(() => "<b>" + Letter(index) + "</b>"), LetterWidth), body,
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
            altitude.OptionEnabledCondition = () => HasBody(index) && Kind(index) == StopKind.Orbit;
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
                new DialogGUILabel(Text(() => "<b><color=" + Dim + ">" + (Ready() ? Letter(state.Stops.Count) : "") + "</color></b>"), LetterWidth),
                add);
            // Once every destination has its body.
            row.OptionEnabledCondition = () => Ready() && state.Stops.Count < MaxStops && PickedStops().Count == state.Stops.Count;
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
                new DialogGUILabel(Text(() => "<color=" + Dim + ">" + SeparationText(leg, role) + "</color>"),
                    width - Timeline - BarIndent - 8f, RowHeight));
            leaves.OptionEnabledCondition = () => SeparationText(leg, role).Length > 0;
            yield return leaves;

            DialogGUIButton vehicle = null;
            vehicle = new DialogGUIButton(() => VehicleText(leg, role), () => PickFlyer(leg, role, vehicle), VehicleButton,
                RowHeight + 2f, false);
            vehicle.tooltipText = "Choose which part of the vessel flies this step.";
            vehicle.OptionEnabledCondition = HasSections;
            SegmentBar bar = new SegmentBar(MaxPieces, BarHeight, after);
            stepBars[leg, (int)role] = bar;
            DialogGUILabel speed = new DialogGUILabel(Text(() => StepSpeed(leg, role)), StepSpeed_, RowHeight);
            DialogGUIButton open = new DialogGUIButton(() => more[leg, (int)role] ? "<b><color=" + Good + ">...</color></b>" : "...",
                () => { more[leg, (int)role] = !more[leg, (int)role]; textVersion++; }, Square, Square, false);
            open.tooltipText = "More options for this step.";
            open.OptionEnabledCondition = () => HasMore(leg, role);
            DialogGUISpace noMore = new DialogGUISpace(Square);
            noMore.OptionEnabledCondition = () => !HasMore(leg, role);
            Sizer filler = new Sizer(after);
            barFillers[leg, (int)role] = filler;
            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, Icon + 2f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                TimelineLine(Icon + 2f), new DialogGUILabel(Text(() => StepPlace(leg, role)), Place), vehicle, bar.Layout,
                filler.Add(new DialogGUISpace(1f)), speed, open, noMore);
            row.OptionEnabledCondition = shown;
            yield return row;

            DialogGUIButton refuel = Choice("Refuel", () => Refuels(leg, role), () => ToggleRefuel(leg, role), 64f);
            refuel.tooltipText = "Refuel this section from the rest of the vessel when it docks.";
            DialogGUIButton tanker = null;
            tanker = new DialogGUIButton(() => TankerText(leg, role), () => PickTanker(leg, role, tanker), 150f, RowHeight + 2f, false);
            tanker.tooltipText = "Choose the stage of the rest of the vessel to refuel from first.";
            tanker.OptionEnabledCondition = () => Refuels(leg, role);
            DialogGUIHorizontalLayout docks = new DialogGUIHorizontalLayout(width, RowHeight + 4f, 4f, new RectOffset(),
                TextAnchor.MiddleLeft, TimelineLine(RowHeight + 4f), new DialogGUISpace(BarIndent),
                new DialogGUILabel("<color=" + Dim + ">Docks with the rest</color>", OptionLabel), refuel, tanker);
            docks.OptionEnabledCondition = () => Docks(leg, role);
            yield return docks;

            DialogGUIHorizontalLayout stays = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(),
                TextAnchor.MiddleLeft, TimelineLine(RowHeight), new DialogGUISpace(BarIndent),
                new DialogGUILabel(Text(() => "<color=" + Dim + ">" + DeployedText(leg, role) + "</color>"), width - Timeline - BarIndent - 8f,
                    RowHeight));
            stays.OptionEnabledCondition = () => DeployedText(leg, role).Length > 0;
            yield return stays;

            // Behind "...", each on a line of its own, as they concern different
            // things: the way down, the burns across.
            DialogGUIButton straight = Choice("Straight in", () => !OrbitFirst(leg + 1), () => SetOrbitFirst(leg + 1, false), 88f);
            straight.tooltipText = "Enter the atmosphere directly and land.";
            DialogGUIButton orbitFirst = Choice("Orbit first", () => OrbitFirst(leg + 1), () => SetOrbitFirst(leg + 1, true), 84f);
            orbitFirst.tooltipText = "Brake into a low orbit first, then land.";
            yield return OptionLine(leg, role, "Way down", () => IntoTheAir(leg, role), straight, orbitFirst);

            DialogGUIButton together = Choice("Together", () => !SplitsBurns(leg + 1), () => SetSplitBurns(leg + 1, false), 88f);
            together.tooltipText = "Plan the departure and arrival burns as one step.";
            DialogGUIButton split = Choice("Split", () => SplitsBurns(leg + 1), () => SetSplitBurns(leg + 1, true), 84f);
            split.tooltipText = "Plan the departure and arrival burns as separate steps, each with its own flyer.";
            yield return OptionLine(leg, role, "Burns", () => role == StepRole.Transfer && CanSplit(leg), together, split);

            for (int i = 0; i < MaxWarnings; i++)
            {
                int line = i;
                DialogGUIHorizontalLayout warning = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(),
                    TextAnchor.MiddleLeft, TimelineLine(RowHeight), new DialogGUISpace(BarIndent),
                    new DialogGUILabel(Text(() => "<color=" + Problem + ">" + WarningText(leg, role, line) + "</color>"),
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

        // "The lander separates in Mun orbit", "The lander is left behind on the Mun".
        private string SeparationText(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            if (index < 0 || !steps[index].Separates) return "";
            RouteStep step = steps[index];
            string place = RouteSteps.At(bodies.Bodies, step.From);
            string leaver = Lower(FlyerName(Flyer.Alone(step.Leaver)));
            if (step.Flyer.Kind == FlyerKind.Without) return "The " + leaver + " is left behind " + place;
            if (step.LeavesFrom == 0) return "The " + leaver + " separates " + place;
            return "The " + leaver + " separates from the " + Lower(FlyerName(Flyer.Alone(step.LeavesFrom))) + " " + place;
        }

        // "The satellite is deployed here": the sections deployed at the step's end.
        private string DeployedText(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            if (index < 0 || steps[index].Deploys.Count == 0) return "";
            List<string> names = new List<string>();
            foreach (int id in steps[index].Deploys) names.Add(Lower(FlyerName(Flyer.Alone(id))));
            return "The " + string.Join(", the ", names.ToArray()) + (names.Count == 1 ? " is deployed here" : " are deployed here");
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
            return HasBody(leg) && HasBody(leg + 1) && RouteSteps.Splits(bodies.Bodies, state.Stops[leg], state.Stops[leg + 1]);
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
            yield return new DialogGUIHorizontalLayout(width, RowHeight + 2f, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel(SummaryText, width - RowEnd, RowHeight));
        }

        private string SummaryText()
        {
            if (!Worked()) return "";
            string balance = budget.TotalShort > 0.5
                ? "<color=" + Warning + ">-" + EngineerFormat.Speed(budget.TotalShort) + "</color>"
                : "<color=" + Good + ">+" + EngineerFormat.Speed(budget.TotalLeft) + "</color>";
            return "<color=" + Dim + ">Left</color> <b>" + balance + "</b><pos=40%><color=" + Dim + ">Needed</color> "
                   + EngineerFormat.Speed(TotalNeeded()) + " <color=" + Dim + ">of</color> " + EngineerFormat.Speed(TotalHas());
        }

        // A heading that opens and closes what is under it: the whole bar answers a
        // click, with its title, what is under it in short, and "+" or "-" at the right.
        private const float HeadingTitle = 90f;
        private const float SignEdge = 12f;

        private DialogGUIBase CollapsibleHeading(string title, System.Func<bool> shown, Callback toggle, string tooltip,
                                                 System.Func<string> summary)
        {
            DialogGUILabel titleLabel = new DialogGUILabel("<b><color=#e0e0e0>" + title + "</color></b>", HeadingTitle, RowHeight);
            // The sign at the bar's right end, a little in from its edge.
            DialogGUILabel sign = new DialogGUILabel(
                () => "<align=right><b><size=120%><color=#e0e0e0>" + (shown() ? "-" : "+") + "</color></size></b></align>", Square, RowHeight);
            DialogGUIHorizontalLayout heading = new DialogGUIHorizontalLayout(outer, HeaderHeight, 4f, new RectOffset((int)BoxMargin, 0, 0, 0),
                TextAnchor.MiddleLeft, titleLabel,
                new DialogGUILabel(Text(() => Worked() ? summary() : ""), outer - BoxMargin - HeadingTitle - Square - 4f * 2f - SignEdge, RowHeight),
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
