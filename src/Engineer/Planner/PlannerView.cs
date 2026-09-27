using System.Collections.Generic;
using System.Text;
using System;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The engineer view's second tab (docs/player/planner.md): a route from stop
    // to stop, A -> B -> C, what each leg costs, and which of the vessel's stages
    // pays for it. Built from KSP's dialog elements like the first tab, with a
    // fixed number of rows for the stops and stages, shown as the plan needs
    // them.
    internal sealed class PlannerView
    {
        private const int MaxStops = 12;
        private const int MaxStages = 60;
        private const float RowHeight = 18f;
        private const string Warning = "#ff9a3d";
        private const string Good = "#8fdc7a";
        private const string Dim = "#9a9a9a";
        private const float Interval = 0.25f;
        private const float SaveDelay = 1.5f;
        private const double AltitudeStep = 10000.0;

        private readonly EditorEngineer owner;
        private readonly float width;

        private PlannerBodies bodies;
        private PlannerState state;
        private string vessel;
        // KSP loaded a vessel or started a new one: its own plan comes, not the
        // one on screen.
        private bool reload;
        private float nextEvaluation;
        private float saveAt = -1f;

        // What the plan comes to, worked out a few times a second.
        private List<Leg> legs = new List<Leg>();
        private StageBudget.Result budget;
        private List<StageRow> stages = new List<StageRow>();
        private List<StageRow> allStages = new List<StageRow>();
        private int[] meetings = new int[0];
        // The lander and the kept stages as far as the vessel has those stages
        // now. The plan keeps what the player set, for a stage that is back
        // after the vessel was taken apart.
        private int lander = -1;
        private Dictionary<int, int> kept = new Dictionary<int, int>();
        private readonly HashSet<int> openLegs = new HashSet<int>();
        private Dictionary<int, List<Part>> burningParts = new Dictionary<int, List<Part>>();

        // The leg or the stage whose parts are marked on the vessel; -1 for none.
        private int selectedLeg = -1;
        private int selectedStage = -1;
        private readonly PartHighlight highlight = new PartHighlight(new Color(0.35f, 1f, 0.35f));

        internal PlannerView(EditorEngineer owner, float width)
        {
            this.owner = owner;
            this.width = width;
            GameEvents.onEditorLoad.Add(OnEditorLoad);
            GameEvents.onEditorRestart.Add(OnEditorRestart);
        }

        internal void Dispose()
        {
            Flush();
            GameEvents.onEditorLoad.Remove(OnEditorLoad);
            GameEvents.onEditorRestart.Remove(OnEditorRestart);
        }

        // A craft merged into the vessel keeps the plan.
        private void OnEditorLoad(ShipConstruct ship, KSP.UI.Screens.CraftBrowserDialog.LoadType type)
        {
            if (type != KSP.UI.Screens.CraftBrowserDialog.LoadType.Merge) reload = true;
        }

        private void OnEditorRestart()
        {
            reload = true;
        }

        // ------------------------------------------------------------------ rows

        internal List<DialogGUIBase> Rows()
        {
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            rows.Add(Heading("Route"));
            rows.Add(Note("Add destinations in the order you fly them. Each leg shows what it costs and which stages pay for it."));
            for (int i = 0; i < MaxStops; i++)
            {
                rows.AddRange(StopRows(i));
                rows.AddRange(LegRows(i));
            }
            DialogGUIButton add = new DialogGUIButton("+ Add destination", AddStop, width, RowHeight + 6f, false);
            add.OptionEnabledCondition = () => Ready() && state.Stops.Count < MaxStops;
            rows.Add(add);

            rows.Add(Heading("Fuel"));
            DialogGUISlider margin = new DialogGUISlider(() => (float)(Ready() ? state.Margin * 100.0 : 10.0), 0f, 50f, true,
                width - 150f, RowHeight, value => { if (Ready()) { state.Margin = value / 100.0; Changed(); } });
            rows.Add(new DialogGUIHorizontalLayout(width, RowHeight + 2f, 6f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel("Margin", 60f), margin,
                new DialogGUILabel(() => (Ready() ? (state.Margin * 100.0).ToString("0") : "10") + " %", 70f)));
            DialogGUIToggle planes = new DialogGUIToggle(() => Ready() && state.PlaneChanges, "Count plane changes",
                on => { if (Ready()) { state.PlaneChanges = on; Changed(); } }, width);
            planes.tooltipText = "Adds the burn that matches the orbits' tilt between two planets or two moons.\n"
                                 + "Leave it off if you launch in the window and correct on the way.";
            rows.Add(planes);
            rows.Add(new DialogGUILabel(SummaryText, true, true));

            rows.Add(Heading("Stages"));
            rows.Add(Note("Stages burn in KSP's order. Keep a stage for a leg and the legs before it do not touch it."));
            DialogGUIButton lander = new DialogGUIButton(LanderText, StepLander, width, RowHeight + 4f, false);
            lander.tooltipText = "A lander that flies down and back up alone while the rest of the vessel waits in orbit,\n"
                                 + "and is carried on to the next destination. Pick the lander's highest stage:\n"
                                 + "it and the stages after it are the lander. Its decoupler or docking port\n"
                                 + "has to be in that stage, so that KSP's staging separates it there.";
            lander.OptionEnabledCondition = () => Ready() && allStages.Count > 1;
            rows.Add(lander);
            for (int i = 0; i < MaxStages; i++) rows.AddRange(StageRows(i));
            rows.Add(Note("The planner counts each stage's delta-v in vacuum. Launches and landings are estimates from the body's gravity, rotation and atmosphere."));
            return rows;
        }

        private IEnumerable<DialogGUIBase> StopRows(int index)
        {
            float arrow = 24f;
            float remove = 22f;
            DialogGUIButton previous = new DialogGUIButton("<", () => StepBody(index, -1), arrow, RowHeight + 2f, false);
            DialogGUIButton next = new DialogGUIButton(">", () => StepBody(index, 1), arrow, RowHeight + 2f, false);
            DialogGUIButton delete = new DialogGUIButton("x", () => RemoveStop(index), remove, RowHeight + 2f, false);
            delete.tooltipText = "Removes this stop from the route.";
            delete.OptionInteractableCondition = () => Ready() && state.Stops.Count > 2;
            DialogGUIHorizontalLayout first = new DialogGUIHorizontalLayout(width, RowHeight + 4f, 4f, new RectOffset(0, 0, 4, 0),
                TextAnchor.MiddleLeft,
                new DialogGUILabel(() => "<b>" + Letter(index) + "</b>", 18f),
                previous,
                new DialogGUILabel(() => "<b>" + BodyName(index) + "</b>", width - 18f - arrow * 2f - remove - 20f),
                next, delete);
            first.OptionEnabledCondition = () => Shown(index);
            yield return first;

            float kind = 64f;
            DialogGUIToggleButton surface = new DialogGUIToggleButton(() => Kind(index) == StopKind.Surface, "Surface",
                on => { if (on) SetKind(index, StopKind.Surface); }, kind, RowHeight + 2f);
            DialogGUIToggleButton orbit = new DialogGUIToggleButton(() => Kind(index) == StopKind.Orbit, "Orbit",
                on => { if (on) SetKind(index, StopKind.Orbit); }, kind, RowHeight + 2f);
            DialogGUIButton lower = new DialogGUIButton("-", () => StepAltitude(index, -1), 20f, RowHeight + 2f, false);
            DialogGUIButton higher = new DialogGUIButton("+", () => StepAltitude(index, 1), 20f, RowHeight + 2f, false);
            lower.OptionEnabledCondition = () => Kind(index) == StopKind.Orbit;
            higher.OptionEnabledCondition = () => Kind(index) == StopKind.Orbit;
            lower.tooltipText = "Lowers the orbit by 10 km, down to the lowest safe one.";
            higher.tooltipText = "Raises the orbit by 10 km.";
            DialogGUIToggle air = new DialogGUIToggle(() => UsesAtmosphere(index), () => Kind(index) == StopKind.Surface ? "Chutes" : "Aerobrake",
                on => SetAtmosphere(index, on), 90f);
            air.tooltipText = "Arriving here uses the atmosphere: aerobraking into the orbit,\n"
                              + "or a heat shield and parachutes for the landing. Needs an atmosphere.";
            air.OptionInteractableCondition = () => HasAtmosphere(index) && index > 0;
            DialogGUIHorizontalLayout second = new DialogGUIHorizontalLayout(width, RowHeight + 4f, 4f, new RectOffset(22, 0, 0, 0),
                TextAnchor.MiddleLeft, surface, orbit, lower,
                new DialogGUILabel(() => Kind(index) == StopKind.Orbit ? AltitudeText(index) : "", 60f), higher, air);
            second.OptionEnabledCondition = () => Shown(index);
            yield return second;
        }

        private IEnumerable<DialogGUIBase> LegRows(int index)
        {
            DialogGUIButton line = new DialogGUIButton(() => LegText(index), () => ToggleLeg(index), width, RowHeight + 2f, false);
            DialogOverWindow.ClearBackground(line);
            line.tooltipText = "Shows the burns of this leg and the stages that pay for them,\n"
                               + "and marks their engines and tanks green on the vessel.";
            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, RowHeight + 2f, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                line);
            row.OptionEnabledCondition = () => Ready() && index + 1 < state.Stops.Count;
            yield return row;

            DialogGUIButton mode = new DialogGUIButton(() => ModeText(index), () => StepMode(index), width - 26f, RowHeight + 2f, false);
            mode.tooltipText = "Who flies this leg: the whole vessel, the lander alone while the rest waits,\n"
                               + "or the rest without the lander, which stays behind for good.";
            DialogGUIHorizontalLayout modeRow = new DialogGUIHorizontalLayout(width, RowHeight + 2f, 0f, new RectOffset(26, 0, 0, 0),
                TextAnchor.MiddleLeft, mode);
            modeRow.OptionEnabledCondition = () => Ready() && index + 1 < state.Stops.Count && HasLander();
            yield return modeRow;

            DialogGUIHorizontalLayout detail = new DialogGUIHorizontalLayout(width, -1f, 0f, new RectOffset(26, 0, 0, 4),
                TextAnchor.UpperLeft, new DialogGUILabel(() => LegDetail(index), true, true));
            detail.OptionEnabledCondition = () => Ready() && index + 1 < state.Stops.Count && openLegs.Contains(index);
            yield return detail;
        }

        private IEnumerable<DialogGUIBase> StageRows(int index)
        {
            DialogGUIButton keep = new DialogGUIButton(() => KeepText(index), () => StepKeep(index), 96f, RowHeight + 2f, false);
            keep.tooltipText = "Keeps this stage for a leg: the legs before it do not use it, nor any stage after it.\n"
                               + "Auto burns it in KSP's order.";
            DialogGUIButton label = new DialogGUIButton(() => StageText(index), () => SelectStage(index), width - 100f, RowHeight + 2f,
                false);
            DialogOverWindow.ClearBackground(label);
            label.tooltipText = "Marks this stage's engines and the tanks it burns green on the vessel.";
            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, RowHeight + 2f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                label, keep);
            row.OptionEnabledCondition = () => index < stages.Count;
            yield return row;
            DialogGUIHorizontalLayout detail = new DialogGUIHorizontalLayout(width, -1f, 0f, new RectOffset(26, 0, 0, 2),
                TextAnchor.UpperLeft, new DialogGUILabel(() => StageDetail(index), true, true));
            detail.OptionEnabledCondition = () => index < stages.Count;
            yield return detail;
        }

        // ---------------------------------------------------------------- update

        internal void Update()
        {
            string name = VesselName();
            if (bodies == null)
            {
                bodies = PlannerBodies.Read();
                if (bodies.Bodies.Count == 0) { bodies = null; return; }
            }
            if (state == null || reload)
            {
                // A vessel loaded or new brings its own plan, or the default.
                if (state != null && vessel != null && saveAt > 0f) state.Save(vessel, bodies);
                saveAt = -1f;
                state = PlannerState.Load(name, bodies);
                vessel = name;
                reload = false;
                openLegs.Clear();
                selectedLeg = -1;
                selectedStage = -1;
                nextEvaluation = 0f;
            }
            else if (name != vessel)
            {
                // A vessel renamed takes its plan along under the new name.
                vessel = name;
                Changed();
            }
            if (saveAt > 0f && Time.unscaledTime >= saveAt)
            {
                state.Save(vessel, bodies);
                saveAt = -1f;
            }
            if (Time.unscaledTime < nextEvaluation) return;
            nextEvaluation = Time.unscaledTime + Interval;
            Evaluate();
        }

        // The tab is not shown: nothing stays marked on the vessel.
        internal void Idle()
        {
            highlight.Clear();
        }

        internal void Flush()
        {
            highlight.Clear();
            if (state != null && vessel != null && bodies != null && saveAt > 0f) state.Save(vessel, bodies);
            saveAt = -1f;
        }

        private void Evaluate()
        {
            EditorReadout readout = owner.Readout ?? new EditorReadout();
            allStages = new List<StageRow>(readout.Stages);
            allStages.Sort((a, b) => b.Number.CompareTo(a.Number));
            stages = allStages.FindAll(row => row.DeltaVVacuum > 0.5);

            legs = RoutePlanner.Legs(bodies.Bodies, state.Stops, state.PlaneChanges);
            List<double> needs = new List<double>();
            foreach (Leg leg in legs) needs.Add(leg.DeltaV);
            List<BudgetStage> available = new List<BudgetStage>();
            foreach (StageRow row in allStages)
                available.Add(new BudgetStage
                {
                    Number = row.Number, StartMass = row.StartMass, EndMass = row.EndMass, DeltaV = row.DeltaVVacuum,
                });
            // A kept stage the vessel does not have now, or a leg the route does
            // not have, counts for nothing; nor does a lander that is not a stage
            // of it.
            kept = new Dictionary<int, int>();
            foreach (KeyValuePair<int, int> pair in state.Kept)
                if (stages.Exists(row => row.Number == pair.Key) && pair.Value < legs.Count) kept[pair.Key] = pair.Value;
            lander = state.Lander >= 0 && LanderChoices().Contains(state.Lander) ? state.Lander : -1;
            List<LegMode> modes = HasLander() ? state.ModeList(legs.Count) : null;
            budget = StageBudget.Allocate(available, needs, state.Margin, kept, lander, modes);
            meetings = modes != null ? RoutePlanner.Meetings(state.Stops, modes) : new int[legs.Count];
            if (modes == null) for (int i = 0; i < meetings.Length; i++) meetings[i] = -1;
            burningParts = readout.BurningParts;
            Mark();
        }

        private bool Ready()
        {
            return state != null && bodies != null;
        }

        private static string VesselName()
        {
            ShipConstruct ship = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
            return ship != null && !string.IsNullOrEmpty(ship.shipName) ? ship.shipName : "Untitled";
        }

        private void Changed()
        {
            saveAt = Time.unscaledTime + SaveDelay;
            nextEvaluation = 0f;
        }

        // ----------------------------------------------------------------- stops

        private bool Shown(int index)
        {
            return Ready() && index < state.Stops.Count;
        }

        private static string Letter(int index)
        {
            return ((char)('A' + index)).ToString();
        }

        private string BodyName(int index)
        {
            if (!Shown(index)) return "";
            int body = state.Stops[index].Body;
            return body >= 0 && body < bodies.Bodies.Count ? bodies.Bodies[body].Name : "?";
        }

        private StopKind Kind(int index)
        {
            return Shown(index) ? state.Stops[index].Kind : StopKind.Orbit;
        }

        private bool HasAtmosphere(int index)
        {
            return Shown(index) && bodies.Bodies[state.Stops[index].Body].HasAtmosphere;
        }

        private bool UsesAtmosphere(int index)
        {
            return Shown(index) && state.Stops[index].UseAtmosphere && HasAtmosphere(index);
        }

        private string AltitudeText(int index)
        {
            return Shown(index) ? RoutePlanner.Km(state.Stops[index].Altitude) : "";
        }

        private void StepBody(int index, int direction)
        {
            if (!Shown(index)) return;
            RouteStop stop = state.Stops[index];
            stop.Body = bodies.Step(stop.Body, direction);
            stop.Altitude = bodies.Bodies[stop.Body].LowOrbitAltitude;
            if (!bodies.Bodies[stop.Body].HasAtmosphere) stop.UseAtmosphere = false;
            Changed();
        }

        private void SetKind(int index, StopKind kind)
        {
            if (!Shown(index) || state.Stops[index].Kind == kind) return;
            state.Stops[index].Kind = kind;
            if (kind == StopKind.Orbit && state.Stops[index].Altitude <= 0.0)
                state.Stops[index].Altitude = bodies.Bodies[state.Stops[index].Body].LowOrbitAltitude;
            Changed();
        }

        private void StepAltitude(int index, int direction)
        {
            if (!Shown(index)) return;
            RouteStop stop = state.Stops[index];
            double lowest = bodies.Bodies[stop.Body].LowOrbitAltitude;
            double altitude = stop.Altitude + direction * AltitudeStep;
            stop.Altitude = Math.Max(lowest, Math.Min(altitude, bodies.MaxAltitude(stop.Body)));
            Changed();
        }

        private void SetAtmosphere(int index, bool on)
        {
            if (!Shown(index) || state.Stops[index].UseAtmosphere == on) return;
            state.Stops[index].UseAtmosphere = on && HasAtmosphere(index);
            Changed();
        }

        // A new stop where the route ends: the same body, in its low orbit, ready
        // to be moved on with < and >.
        private void AddStop()
        {
            if (!Ready() || state.Stops.Count >= MaxStops) return;
            RouteStop last = state.Stops[state.Stops.Count - 1];
            state.Stops.Add(new RouteStop
            {
                Body = last.Body, Kind = StopKind.Orbit, Altitude = bodies.Bodies[last.Body].LowOrbitAltitude,
            });
            Changed();
        }

        private void RemoveStop(int index)
        {
            if (!Shown(index) || state.Stops.Count <= 2) return;
            state.Stops.RemoveAt(index);
            // A kept stage follows its leg, or lets go where the leg went.
            Dictionary<int, int> moved = new Dictionary<int, int>();
            foreach (KeyValuePair<int, int> pair in state.Kept)
            {
                if (pair.Value == index || pair.Value == index - 1) continue;
                moved[pair.Key] = pair.Value > index ? pair.Value - 1 : pair.Value;
            }
            state.Kept.Clear();
            foreach (KeyValuePair<int, int> pair in moved) state.Kept[pair.Key] = pair.Value;
            // The leg into the removed stop goes, the one out of it becomes the
            // leg into the next.
            Dictionary<int, LegMode> modes = new Dictionary<int, LegMode>();
            foreach (KeyValuePair<int, LegMode> pair in state.Modes)
            {
                if (pair.Key == index - 1 || (index == 0 && pair.Key == 0)) continue;
                modes[pair.Key >= index ? pair.Key - 1 : pair.Key] = pair.Value;
            }
            state.Modes.Clear();
            foreach (KeyValuePair<int, LegMode> pair in modes) state.Modes[pair.Key] = pair.Value;
            openLegs.Clear();
            selectedLeg = -1;
            Changed();
        }

        // ------------------------------------------------------------------ legs

        private string LegName(int leg)
        {
            return Letter(leg) + " → " + Letter(leg + 1);
        }

        // Opening a leg marks its parts; closing the marked one clears the mark.
        private void ToggleLeg(int index)
        {
            if (openLegs.Remove(index))
            {
                if (selectedLeg == index) selectedLeg = -1;
            }
            else
            {
                openLegs.Add(index);
                selectedLeg = index;
                selectedStage = -1;
            }
            Mark();
        }

        private void SelectStage(int index)
        {
            if (index >= stages.Count) return;
            int number = stages[index].Number;
            selectedStage = selectedStage == number ? -1 : number;
            if (selectedStage >= 0) selectedLeg = -1;
            Mark();
        }

        // The engines and tanks of the selected stage, or of every stage that
        // pays for the selected leg.
        private void Mark()
        {
            HashSet<int> numbers = new HashSet<int>();
            if (selectedStage >= 0) numbers.Add(selectedStage);
            else if (selectedLeg >= 0 && budget != null && selectedLeg < legs.Count)
                foreach (StageBudget.StageUse use in budget.Uses)
                    if (use.Leg == selectedLeg) numbers.Add(use.Stage);
            HashSet<Part> parts = new HashSet<Part>();
            foreach (int number in numbers)
            {
                List<Part> list;
                if (burningParts.TryGetValue(number, out list))
                    foreach (Part part in list)
                        if (part != null) parts.Add(part);
            }
            highlight.Show(parts);
        }

        private string LegText(int index)
        {
            if (!Ready() || index >= legs.Count || budget == null) return "";
            double need = budget.Needed[index];
            string speed = EngineerFormat.Speed(legs[index].DeltaV);
            if (selectedLeg == index) speed = "<color=" + Good + ">" + speed + "</color>";
            string text = "      ↓  " + (openLegs.Contains(index) ? "- " : "+ ") + speed;
            if (state.Margin > 0.0) text += " <color=" + Dim + ">(" + EngineerFormat.Speed(need) + " with margin)</color>";
            string by = StagesOf(index);
            if (by.Length > 0) text += "  <color=" + Dim + ">" + by + "</color>";
            if (budget.Short[index] > 0.5) text += "  <color=" + Warning + ">short " + EngineerFormat.Speed(budget.Short[index]) + "</color>";
            if (index < meetings.Length && meetings[index] >= 0) text += "  <color=" + Warning + ">!</color>";
            return text;
        }

        private string StagesOf(int leg)
        {
            List<string> names = new List<string>();
            foreach (StageBudget.StageUse use in budget.Uses)
                if (use.Leg == leg && !names.Contains("S" + use.Stage)) names.Add("S" + use.Stage);
            return string.Join(", ", names.ToArray());
        }

        private string LegDetail(int index)
        {
            if (!Ready() || index >= legs.Count || budget == null) return "";
            StringBuilder text = new StringBuilder();
            foreach (LegLine line in legs[index].Lines)
            {
                if (text.Length > 0) text.Append('\n');
                text.Append(line.Name).Append("<pos=62%>").Append(EngineerFormat.Speed(line.DeltaV));
            }
            foreach (StageBudget.StageUse use in budget.Uses)
            {
                if (use.Leg != index) continue;
                text.Append('\n').Append("<color=" + Dim + ">from S").Append(use.Stage).Append("</color><pos=62%>")
                    .Append(EngineerFormat.Speed(use.DeltaV));
            }
            if (index < meetings.Length && meetings[index] >= 0)
                text.Append('\n').Append("<color=" + Warning + ">The rest of the vessel waits at ").Append(Letter(meetings[index]))
                    .Append(": this leg has to start there.</color>");
            string warning = ThrustWarning(index);
            if (warning.Length > 0) text.Append('\n').Append("<color=" + Warning + ">").Append(warning).Append("</color>");
            if (budget.Short[index] > 0.5)
                text.Append('\n').Append("<color=" + Warning + ">No stage covers the last ")
                    .Append(EngineerFormat.Speed(budget.Short[index])).Append(" of this leg.</color>");
            return text.ToString();
        }

        // A launch or a powered landing needs the thrust to lift the vessel at
        // that body: the first stage the leg burns, full, at its surface gravity.
        private string ThrustWarning(int leg)
        {
            if (leg >= legs.Count) return "";
            RouteStop from = state.Stops[leg];
            RouteStop to = state.Stops[leg + 1];
            bool launch = from.Kind == StopKind.Surface;
            bool landing = to.Kind == StopKind.Surface && !(to.UseAtmosphere && bodies.Bodies[to.Body].HasAtmosphere
                                                             && bodies.Bodies[to.Body].SurfacePressure >= 0.2);
            if (!launch && !landing) return "";
            StageBudget.StageUse first = null;
            StageBudget.StageUse lastUsed = null;
            foreach (StageBudget.StageUse use in budget.Uses)
            {
                if (use.Leg != leg) continue;
                if (first == null) first = use;
                lastUsed = use;
            }
            List<string> warnings = new List<string>();
            if (launch && first != null) AddTwrWarning(warnings, first, from.Body, "launch");
            if (landing && lastUsed != null) AddTwrWarning(warnings, lastUsed, to.Body, "landing");
            return string.Join("\n", warnings.ToArray());
        }

        // The thrust in vacuum over the weight the stage pushes as it starts its
        // part of the leg.
        private void AddTwrWarning(List<string> warnings, StageBudget.StageUse use, int body, string what)
        {
            StageRow row = stages.Find(s => s.Number == use.Stage);
            PlannerBody planner = bodies.Bodies[body];
            double gravity = planner.Mu / (planner.Radius * planner.Radius);
            if (row == null || use.Mass <= 0.0 || gravity <= 0.0) return;
            double twr = row.ThrustVacuum / (use.Mass * gravity);
            double needed = planner.HasAtmosphere && what == "launch" ? 1.2 : 1.0;
            if (twr < needed)
                warnings.Add("S" + row.Number + " has a TWR of " + EngineerFormat.Ratio(twr) + " at " + planner.Name
                             + ": too weak for the " + what + ".");
        }

        // ---------------------------------------------------------------- stages

        private string StageText(int index)
        {
            if (index >= stages.Count || budget == null) return "";
            StageRow row = stages[index];
            string lander = IsLanderStage(row.Number) ? " <color=" + Dim + ">lander</color>" : "";
            string name = selectedStage == row.Number ? "<color=" + Good + ">S" + row.Number + "</color>" : "S" + row.Number;
            StringBuilder text = new StringBuilder("<b>" + name + "</b>" + lander + "  " + EngineerFormat.Speed(row.DeltaVVacuum));
            double left;
            double dropped;
            bool behind = budget.LeftBehind.Contains(row.Number);
            if (budget.Dropped.TryGetValue(row.Number, out dropped))
                text.Append("  <color=" + Dim + ">" + (behind ? "left behind" : "dropped") + " with " + EngineerFormat.Speed(dropped) + "</color>");
            else if (behind)
                text.Append("  <color=" + Dim + ">left behind</color>");
            else if (budget.Left.TryGetValue(row.Number, out left) && left > 0.5)
                text.Append("  <color=" + Good + ">" + EngineerFormat.Speed(left) + " left</color>");
            return text.ToString();
        }

        private string StageDetail(int index)
        {
            if (index >= stages.Count || budget == null) return "";
            StageRow row = stages[index];
            StringBuilder text = new StringBuilder();
            if (row.Engines.Length > 0) text.Append("<color=" + Dim + ">").Append(row.Engines).Append("</color>");
            List<string> uses = new List<string>();
            foreach (StageBudget.StageUse use in budget.Uses)
                if (use.Stage == row.Number) uses.Add(LegName(use.Leg) + " " + EngineerFormat.Speed(use.DeltaV));
            if (uses.Count > 0) text.Append(text.Length > 0 ? "\n" : "").Append(string.Join(",  ", uses.ToArray()));
            else text.Append(text.Length > 0 ? "\n" : "").Append("<color=" + Dim + ">not needed for the route</color>");
            return text.ToString();
        }

        private string KeepText(int index)
        {
            if (index >= stages.Count || !Ready()) return "";
            int leg;
            return state.Kept.TryGetValue(stages[index].Number, out leg) && leg < legs.Count ? "For " + LegName(leg) : "Auto";
        }

        // Auto, then each leg in turn.
        private void StepKeep(int index)
        {
            if (index >= stages.Count || !Ready() || legs.Count == 0) return;
            int stage = stages[index].Number;
            int leg;
            if (!state.Kept.TryGetValue(stage, out leg)) state.Kept[stage] = 0;
            else if (leg + 1 < legs.Count) state.Kept[stage] = leg + 1;
            else state.Kept.Remove(stage);
            Changed();
        }

        // ---------------------------------------------------------------- lander

        private bool HasLander()
        {
            return Ready() && lander >= 0;
        }

        private bool IsLanderStage(int number)
        {
            return HasLander() && number <= lander;
        }

        // A lander can start at any stage but the first: something has to wait
        // for it.
        private List<int> LanderChoices()
        {
            List<int> choices = new List<int>();
            for (int i = 1; i < allStages.Count; i++) choices.Add(allStages[i].Number);
            return choices;
        }

        private string LanderText()
        {
            if (!HasLander()) return "Lander: <b>none</b>";
            return "Lander: <b>" + (lander == 0 ? "S0" : "S" + lander + " to S0") + "</b>";
        }

        // None, then the smallest lander, then each larger one.
        private void StepLander()
        {
            if (!Ready()) return;
            List<int> choices = LanderChoices();
            choices.Sort();
            int at = choices.IndexOf(lander);
            state.Lander = at + 1 < choices.Count ? choices[at + 1] : -1;
            lander = state.Lander;
            Changed();
        }

        private LegMode Mode(int leg)
        {
            LegMode mode;
            return Ready() && state.Modes.TryGetValue(leg, out mode) ? mode : LegMode.Whole;
        }

        private string ModeText(int leg)
        {
            switch (Mode(leg))
            {
                case LegMode.Lander: return "<color=" + Dim + ">flown by</color> the lander alone";
                case LegMode.WithoutLander: return "<color=" + Dim + ">flown by</color> the rest, the lander stays";
                default: return "<color=" + Dim + ">flown by</color> the whole vessel";
            }
        }

        private void StepMode(int leg)
        {
            if (!Ready()) return;
            LegMode next = (LegMode)(((int)Mode(leg) + 1) % 3);
            if (next == LegMode.Whole) state.Modes.Remove(leg);
            else state.Modes[leg] = next;
            Changed();
        }

        // --------------------------------------------------------------- summary

        private string SummaryText()
        {
            if (!Ready() || budget == null) return "";
            double needed = 0.0;
            foreach (double value in budget.Needed) needed += value;
            double has = 0.0;
            foreach (StageRow row in stages) has += row.DeltaVVacuum;
            StringBuilder text = new StringBuilder();
            text.Append("The route needs<pos=52%>").Append(EngineerFormat.Speed(needed));
            text.Append("\nThe vessel has<pos=52%>").Append(EngineerFormat.Speed(has));
            if (stages.Count == 0)
            {
                text.Append("\n<color=" + Warning + ">The vessel burns nothing yet.</color>");
                return text.ToString();
            }
            double shortBy = budget.TotalShort;
            if (shortBy > 0.5)
            {
                int first = Array.FindIndex(budget.Short, value => value > 0.5);
                text.Append("\n<color=" + Warning + "><b>Short by ").Append(EngineerFormat.Speed(shortBy)).Append("</b>, from ")
                    .Append(LegName(first)).Append(" on.</color>");
            }
            else
            {
                text.Append("\n<color=" + Good + "><b>Enough fuel</b>, ").Append(EngineerFormat.Speed(budget.TotalLeft))
                    .Append(" left over.</color>");
            }
            return text.ToString();
        }

        // ---------------------------------------------------------------- pieces

        private DialogGUIBase Heading(string title)
        {
            return new DialogGUIHorizontalLayout(width, RowHeight + 6f, 0f, new RectOffset(0, 0, 6, 0), TextAnchor.LowerLeft,
                new DialogGUILabel("<b><color=#e0e0e0>" + title + "</color></b>", width, RowHeight));
        }

        private DialogGUIBase Note(string text)
        {
            return new DialogGUILabel("<color=" + Dim + ">" + text + "</color>", true, true);
        }
    }
}
