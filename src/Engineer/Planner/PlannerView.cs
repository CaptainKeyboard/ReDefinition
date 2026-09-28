using System.Collections.Generic;
using System;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The planner (docs/player/planner.md): a route from destination to
    // destination, A -> B -> C, who flies to each, the steps that follow from it
    // between them (RouteSteps), what each step costs, and which of the vessel's
    // stages pays for it, by itself or as the player pins them to a leg. This half
    // holds the plan and works it out; the rows are in PlannerView.Rows.cs.
    internal sealed partial class PlannerView
    {
        private const float Interval = 0.25f;
        private const float SaveDelay = 1.5f;
        private const double AltitudeStep = 10000.0;

        private readonly EditorEngineer owner;
        private readonly float width;
        private readonly AfterCreate after = new AfterCreate();

        private PlannerBodies bodies;
        private PlannerState state;
        private string vessel;
        // KSP loaded a vessel or started a new one: its own plan comes, not the
        // one on screen.
        private bool reload;
        private float nextEvaluation;
        private float saveAt = -1f;

        // What the plan comes to, worked out a few times a second: the steps, and
        // for each leg (the way from one destination to the next) and role the step
        // there, -1 where there is none.
        private List<RouteStep> steps = new List<RouteStep>();
        private readonly int[,] slots = new int[MaxStops, 3];
        private List<string>[] warnings = new List<string>[0];
        private StageBudget.Result budget;
        // The stages with delta-v, in firing order, and all of them.
        private List<StageRow> stages = new List<StageRow>();
        private List<StageRow> allStages = new List<StageRow>();
        // The lander as far as the vessel has that stage now. The plan keeps
        // what the player set, for a stage that is back after the vessel was
        // taken apart; so it does with the pins.
        private int lander = -1;
        private List<int> landerChoices = new List<int>();
        private Dictionary<int, List<Part>> burningParts = new Dictionary<int, List<Part>>();
        private List<int> parachuteStages = new List<int>();
        private List<int> heatShieldStages = new List<int>();
        // The selected leg's or stage's details, written once per evaluation
        // and on each selection.
        private string detailTitle = "";
        private string[] detailLines = new string[0];

        // The leg or the stage whose parts alone are coloured on the vessel; -1
        // for none, when every stage shows its colour.
        private int selectedLeg = -1;
        private int selectedStage = -1;
        private readonly PartHighlight highlight = new PartHighlight();

        internal PlannerView(EditorEngineer owner, float width)
        {
            this.owner = owner;
            this.width = width;
            GameEvents.onEditorLoad.Add(OnEditorLoad);
            GameEvents.onEditorRestart.Add(OnEditorRestart);
        }

        // Once the rows are created: symbols on the buttons, the bars' images.
        internal void Finish()
        {
            after.Run();
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

        // Every frame while the planner is open.
        internal void Keep()
        {
            highlight.Refresh();
        }

        // The planner closes: nothing stays coloured, the list of bodies goes.
        internal void Flush()
        {
            highlight.Clear();
            BodyPicker.Close(this);
            if (state != null && vessel != null && bodies != null && saveAt > 0f) state.Save(vessel, bodies);
            saveAt = -1f;
        }

        private void Evaluate()
        {
            EditorReadout readout = owner.Readout ?? new EditorReadout();
            allStages = new List<StageRow>(readout.Stages);
            allStages.Sort((a, b) => b.Number.CompareTo(a.Number));
            stages = allStages.FindAll(row => row.DeltaVVacuum > 0.5);

            // A lander that is not a stage of the vessel now counts for nothing.
            landerChoices = LanderChoices();
            lander = state.Lander >= 0 && landerChoices.Contains(state.Lander) ? state.Lander : -1;
            steps = RouteSteps.Build(bodies.Bodies, state.Stops, HasLander(), state.PlaneChanges);
            for (int leg = 0; leg < MaxStops; leg++)
                for (int role = 0; role < 3; role++) slots[leg, role] = -1;
            for (int i = 0; i < steps.Count; i++)
                if (steps[i].Segment < MaxStops) slots[steps[i].Segment, (int)steps[i].Role] = i;
            if (selectedLeg >= Legs) selectedLeg = -1;
            if (selectedStage >= 0 && !stages.Exists(row => row.Number == selectedStage)) selectedStage = -1;

            List<double> needs = new List<double>();
            List<LegMode> modes = new List<LegMode>();
            List<bool> refuel = new List<bool>();
            foreach (RouteStep step in steps)
            {
                needs.Add(step.Leg.DeltaV);
                modes.Add(step.Mode);
                refuel.Add(step.RefuelAfter);
            }
            List<BudgetStage> available = new List<BudgetStage>();
            foreach (StageRow row in allStages)
                available.Add(new BudgetStage
                {
                    Number = row.Number, StartMass = row.StartMass, EndMass = row.EndMass, DeltaV = row.DeltaVVacuum,
                });
            budget = StageBudget.Allocate(available, needs, state.Margin, StepPins(), lander, modes, refuel);
            burningParts = readout.BurningParts;
            parachuteStages = readout.ParachuteStages;
            heatShieldStages = readout.HeatShieldStages;
            warnings = new List<string>[steps.Count];
            for (int i = 0; i < steps.Count; i++) warnings[i] = StepWarnings(i);
            RefreshDetail();
            UpdateBars();
            UpdatePins();
            Mark();
        }

        private bool Ready()
        {
            return state != null && bodies != null;
        }

        private bool Worked()
        {
            return Ready() && budget != null && budget.Needed.Length == steps.Count;
        }

        // The ways from one destination to the next: the fuel plan's columns.
        private int Legs
        {
            get { return Ready() ? state.Stops.Count - 1 : 0; }
        }

        private int Slot(int leg, StepRole role)
        {
            return Worked() && leg >= 0 && leg < MaxStops ? slots[leg, (int)role] : -1;
        }

        // The pins, set on a leg, on its steps: the lander's stages on the lander's
        // steps, the rest's on the others; on a leg the lander has no step of, every
        // pinned stage on it. A pin to a stage the vessel does not have now, or to a
        // leg the route does not have, counts for nothing.
        private Dictionary<int, HashSet<int>> StepPins()
        {
            Dictionary<int, HashSet<int>> pins = new Dictionary<int, HashSet<int>>();
            foreach (KeyValuePair<int, HashSet<int>> pair in state.Pins)
            {
                if (!stages.Exists(row => row.Number == pair.Key)) continue;
                bool ofLander = IsLanderStage(pair.Key);
                HashSet<int> onSteps = new HashSet<int>();
                for (int i = 0; i < steps.Count; i++)
                {
                    RouteStep step = steps[i];
                    if (!pair.Value.Contains(step.Segment)) continue;
                    bool landerFlies = steps.Exists(other => other.Segment == step.Segment && other.Mode == LegMode.Lander);
                    if (step.Mode == LegMode.Lander ? ofLander : !landerFlies || !ofLander) onSteps.Add(i);
                }
                if (onSteps.Count > 0) pins[pair.Key] = onSteps;
            }
            return pins;
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

        private static string LegName(int leg)
        {
            // KSP's font has no arrow.
            return Letter(leg) + "-" + Letter(leg + 1);
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

        private void PickBody(int index)
        {
            if (!Shown(index)) return;
            BodyPicker.Show(this, bodies, state.Stops[index].Body, body => SetBody(index, body));
        }

        private void SetBody(int index, int body)
        {
            if (!Shown(index) || body < 0 || body >= bodies.Bodies.Count) return;
            RouteStop stop = state.Stops[index];
            stop.Body = body;
            stop.Altitude = bodies.Bodies[body].LowOrbitAltitude;
            if (!bodies.Bodies[body].HasAtmosphere) stop.UseAtmosphere = false;
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

        private void ToggleAtmosphere(int index)
        {
            if (!Shown(index) || !HasAtmosphere(index)) return;
            state.Stops[index].UseAtmosphere = !state.Stops[index].UseAtmosphere;
            Changed();
        }

        // A new stop where the route ends: the same body, in its low orbit.
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
            // The list of bodies is for a stop that may be another one now.
            BodyPicker.Close(this);
            state.Stops.RemoveAt(index);
            state.RemoveStop(index);
            selectedLeg = -1;
            Changed();
        }

        // -------------------------------------------------------------- choosing

        private void SelectLeg(int leg)
        {
            if (!Ready() || leg >= Legs) return;
            selectedLeg = selectedLeg == leg ? -1 : leg;
            selectedStage = -1;
            RefreshDetail();
            Mark();
        }

        private void SelectStage(int index)
        {
            if (index >= stages.Count) return;
            int number = stages[index].Number;
            selectedStage = selectedStage == number ? -1 : number;
            RefreshDetail();
            Mark();
        }

        // Each stage's engines and the tanks it burns, in the stage's colour: all
        // of them, the ones that pay for the selected leg, or the selected stage.
        private void Mark()
        {
            Dictionary<Part, Color> parts = new Dictionary<Part, Color>();
            for (int i = 0; i < stages.Count; i++)
            {
                int number = stages[i].Number;
                if (selectedStage >= 0 && number != selectedStage) continue;
                if (selectedStage < 0 && selectedLeg >= 0 && Worked()
                    && !budget.Uses.Exists(use => use.Stage == number && steps[use.Leg].Segment == selectedLeg)) continue;
                List<Part> list;
                if (!burningParts.TryGetValue(number, out list)) continue;
                Color color = PlannerColors.Vivid(PlannerColors.Stage(i));
                foreach (Part part in list)
                    if (part != null) parts[part] = color;
            }
            highlight.Show(parts);
        }

        // The player pins a stage to a leg, or takes the pin away.
        private void TogglePin(int stageIndex, int leg)
        {
            if (!Ready() || stageIndex >= stages.Count || leg >= Legs) return;
            int number = stages[stageIndex].Number;
            state.Pin(number, leg, !state.IsPinned(number, leg));
            Changed();
        }

        private bool IsPinned(int stageIndex, int leg)
        {
            return Ready() && stageIndex < stages.Count && state.IsPinned(stages[stageIndex].Number, leg);
        }

        // What stands in the way of a step, for a line under it; empty where
        // nothing does.
        private List<string> StepWarnings(int index)
        {
            List<string> list = new List<string>();
            RouteStep step = steps[index];
            if (step.Problem != null) list.Add(step.Problem);
            if (budget.Short[index] > 0.5) list.Add("Short of " + EngineerFormat.Speed(budget.Short[index]));
            foreach (KeyValuePair<int, int> pin in budget.Unreachable)
                if (pin.Value == index) list.Add("S" + pin.Key + " has nothing left here");

            // A launch or a powered landing needs the thrust to lift the vessel at
            // that body: the first stage the step burns, and the last one for the
            // landing.
            bool launch = step.From.Kind == StopKind.Surface;
            bool landing = step.To.Kind == StopKind.Surface && !Chutes(step.To);
            StageBudget.StageUse first = null;
            StageBudget.StageUse lastUsed = null;
            foreach (StageBudget.StageUse use in budget.Uses)
            {
                if (use.Leg != index) continue;
                if (first == null) first = use;
                lastUsed = use;
            }
            if (launch && first != null && Twr(first, step.From.Body) < (bodies.Bodies[step.From.Body].HasAtmosphere ? 1.2 : 1.0))
                list.Add("S" + first.Stage + " cannot lift off " + bodies.Bodies[step.From.Body].Name + ", TWR "
                         + EngineerFormat.Ratio(Twr(first, step.From.Body)));
            if (landing && lastUsed != null && Twr(lastUsed, step.To.Body) < 1.0)
                list.Add("S" + lastUsed.Stage + " cannot land on " + bodies.Bodies[step.To.Body].Name + ", TWR "
                         + EngineerFormat.Ratio(Twr(lastUsed, step.To.Body)));

            // Parachutes for a landing on them; a heat shield where it comes down
            // from another body.
            if (step.To.Kind == StopKind.Surface && step.To.UseAtmosphere && bodies.Bodies[step.To.Body].HasAtmosphere)
            {
                string who = step.Mode == LegMode.Lander ? "the lander" : "the vessel";
                if (!Carries(parachuteStages, step.Mode)) list.Add("No parachutes on " + who);
                if (FromAfar(index) && !Carries(heatShieldStages, step.Mode)) list.Add("No heat shield on " + who);
            }
            return list;
        }

        // Parachutes that leave nothing to burn: air thick enough to land on them.
        private bool Chutes(RouteStop stop)
        {
            PlannerBody body = bodies.Bodies[stop.Body];
            return stop.UseAtmosphere && body.HasAtmosphere && body.SurfacePressure >= 0.2;
        }

        // The landing comes in from another body: straight, or carried by the rest
        // just before.
        private bool FromAfar(int index)
        {
            RouteStep step = steps[index];
            if (step.From.Body != step.To.Body) return true;
            int approach = slots[step.Segment, (int)StepRole.Approach];
            return step.Role == StepRole.Main && approach >= 0 && steps[approach].From.Body != steps[approach].To.Body;
        }

        // A part of that kind on what lands: the lander's where it lands alone, the
        // rest's once the lander stayed behind, anywhere on the vessel otherwise. The
        // rest's parts are decoupled above the lander's top stage.
        private bool Carries(List<int> decoupleStages, LegMode mode)
        {
            foreach (int stage in decoupleStages)
            {
                if (mode == LegMode.Lander && stage > lander) continue;
                if (mode == LegMode.WithoutLander && stage <= lander) continue;
                return true;
            }
            return false;
        }

        // The thrust in vacuum over the weight the stage pushes as it starts its
        // part of the step, at the body's surface.
        private double Twr(StageBudget.StageUse use, int body)
        {
            StageRow row = stages.Find(s => s.Number == use.Stage);
            PlannerBody planner = bodies.Bodies[body];
            double gravity = planner.Mu / (planner.Radius * planner.Radius);
            if (row == null || use.Mass <= 0.0 || gravity <= 0.0) return 0.0;
            return row.ThrustVacuum / (use.Mass * gravity);
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

        // The stage and every stage KSP fires after it become the lander; on the
        // lander's first stage with delta-v again, there is no lander.
        private void ToggleLander(int stageIndex)
        {
            if (!Ready() || stageIndex >= stages.Count) return;
            int number = stages[stageIndex].Number;
            if (!landerChoices.Contains(number)) return;
            state.Lander = number == FirstBurning(lander) ? -1 : LanderTop(number);
            lander = state.Lander;
            Changed();
        }

        // The stage the lander starts at, for its first stage with delta-v: the
        // stages without delta-v fired just before it, the one with the
        // lander's decoupler among them, belong to it. Without them, the
        // decoupler's stage would count to the rest, and the rest would be
        // dropped with it.
        private int LanderTop(int number)
        {
            int at = allStages.FindIndex(row => row.Number == number);
            while (at > 1 && allStages[at - 1].DeltaVVacuum <= 0.5) at--;
            return at >= 0 ? allStages[at].Number : number;
        }

        // The lander's first stage with delta-v, -1 for none.
        private int FirstBurning(int top)
        {
            if (top < 0) return -1;
            foreach (StageRow row in stages)
                if (row.Number <= top) return row.Number;
            return -1;
        }

        private Vehicle VehicleOf(int index)
        {
            return Shown(index) ? state.Stops[index].Vehicle : Vehicle.Ship;
        }

        private void SetVehicle(int index, Vehicle vehicle)
        {
            if (!Shown(index) || state.Stops[index].Vehicle == vehicle) return;
            state.Stops[index].Vehicle = vehicle;
            Changed();
        }

        // The choices for the steps before a destination (RouteStop).
        private bool LanderStays(int index)
        {
            return Shown(index) && state.Stops[index].LanderStays;
        }

        private void SetLanderStays(int index, bool stays)
        {
            if (!Shown(index) || state.Stops[index].LanderStays == stays) return;
            state.Stops[index].LanderStays = stays;
            Changed();
        }

        private bool Refuels(int index)
        {
            return Shown(index) && state.Stops[index].Refuel;
        }

        private void ToggleRefuel(int index)
        {
            if (!Shown(index)) return;
            state.Stops[index].Refuel = !state.Stops[index].Refuel;
            Changed();
        }

        private bool OrbitFirst(int index)
        {
            return Shown(index) && state.Stops[index].OrbitFirst;
        }

        private void SetOrbitFirst(int index, bool orbit)
        {
            if (!Shown(index) || state.Stops[index].OrbitFirst == orbit) return;
            state.Stops[index].OrbitFirst = orbit;
            Changed();
        }

        private void SetMargin(float percent)
        {
            double margin = Math.Round(percent) / 100.0;
            if (!Ready() || Math.Abs(margin - state.Margin) < 1e-6) return;
            state.Margin = margin;
            Changed();
        }

        private void SetPlaneChanges(bool on)
        {
            if (!Ready() || on == state.PlaneChanges) return;
            state.PlaneChanges = on;
            Changed();
        }

        private double TotalNeeded()
        {
            double needed = 0.0;
            if (budget != null) foreach (double value in budget.Needed) needed += value;
            return needed;
        }

        private double TotalHas()
        {
            double has = 0.0;
            foreach (StageRow row in stages) has += row.DeltaVVacuum;
            return has;
        }
    }
}
