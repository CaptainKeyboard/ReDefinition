using System.Collections.Generic;
using System;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The planner (docs/player/planner.md): a route from stop to stop, A -> B ->
    // C, what each leg costs, and which of the vessel's stages pays for it, by
    // itself or as the player pins them. This half holds the plan and works it
    // out; the rows are in PlannerView.Rows.cs.
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

        // What the plan comes to, worked out a few times a second.
        private List<Leg> legs = new List<Leg>();
        private StageBudget.Result budget;
        // The stages with delta-v, in firing order, and all of them.
        private List<StageRow> stages = new List<StageRow>();
        private List<StageRow> allStages = new List<StageRow>();
        private int[] meetings = new int[0];
        // The lander as far as the vessel has that stage now. The plan keeps
        // what the player set, for a stage that is back after the vessel was
        // taken apart; so it does with the pins.
        private int lander = -1;
        private List<int> landerChoices = new List<int>();
        private Dictionary<int, List<Part>> burningParts = new Dictionary<int, List<Part>>();
        // The selected leg's details, written once per evaluation.
        private string legDetail = "";

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

            legs = RoutePlanner.Legs(bodies.Bodies, state.Stops, state.PlaneChanges);
            if (selectedLeg >= legs.Count) selectedLeg = -1;
            if (selectedStage >= 0 && !stages.Exists(row => row.Number == selectedStage)) selectedStage = -1;
            List<double> needs = new List<double>();
            foreach (Leg leg in legs) needs.Add(leg.DeltaV);
            List<BudgetStage> available = new List<BudgetStage>();
            foreach (StageRow row in allStages)
                available.Add(new BudgetStage
                {
                    Number = row.Number, StartMass = row.StartMass, EndMass = row.EndMass, DeltaV = row.DeltaVVacuum,
                });
            // A pin to a stage the vessel does not have now, or to a leg the
            // route does not have, counts for nothing; nor does a lander that is
            // not a stage of it.
            Dictionary<int, HashSet<int>> pins = new Dictionary<int, HashSet<int>>();
            foreach (KeyValuePair<int, HashSet<int>> pair in state.Pins)
            {
                if (!stages.Exists(row => row.Number == pair.Key)) continue;
                HashSet<int> legsOf = new HashSet<int>();
                foreach (int leg in pair.Value) if (leg < legs.Count) legsOf.Add(leg);
                if (legsOf.Count > 0) pins[pair.Key] = legsOf;
            }
            landerChoices = LanderChoices();
            lander = state.Lander >= 0 && landerChoices.Contains(state.Lander) ? state.Lander : -1;
            List<LegMode> modes = HasLander() ? state.ModeList(legs.Count) : null;
            budget = StageBudget.Allocate(available, needs, state.Margin, pins, lander, modes);
            meetings = modes != null ? RoutePlanner.Meetings(state.Stops, modes) : new int[legs.Count];
            if (modes == null) for (int i = 0; i < meetings.Length; i++) meetings[i] = -1;
            burningParts = readout.BurningParts;
            legDetail = selectedLeg >= 0 ? LegDetail(selectedLeg) : "";
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
            return Ready() && budget != null;
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
            return Letter(leg) + "→" + Letter(leg + 1);
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
            if (!Ready() || leg >= legs.Count) return;
            selectedLeg = selectedLeg == leg ? -1 : leg;
            selectedStage = -1;
            legDetail = selectedLeg >= 0 ? LegDetail(selectedLeg) : "";
            Mark();
        }

        private void SelectStage(int index)
        {
            if (index >= stages.Count) return;
            int number = stages[index].Number;
            selectedStage = selectedStage == number ? -1 : number;
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
                if (selectedStage < 0 && selectedLeg >= 0 && budget != null
                    && !budget.Uses.Exists(use => use.Stage == number && use.Leg == selectedLeg)) continue;
                List<Part> list;
                if (!burningParts.TryGetValue(number, out list)) continue;
                Color color = PlannerColors.Stage(i);
                foreach (Part part in list)
                    if (part != null) parts[part] = color;
            }
            highlight.Show(parts);
        }

        // The player pins a stage to a leg, or takes the pin away.
        private void TogglePin(int stageIndex, int leg)
        {
            if (!Ready() || stageIndex >= stages.Count || leg >= legs.Count) return;
            int number = stages[stageIndex].Number;
            state.Pin(number, leg, !state.IsPinned(number, leg));
            Changed();
        }

        private bool IsPinned(int stageIndex, int leg)
        {
            return Ready() && stageIndex < stages.Count && state.IsPinned(stages[stageIndex].Number, leg);
        }

        // A launch or a powered landing needs the thrust to lift the vessel at
        // that body: the first stage the leg burns, at its surface gravity, and
        // the last one for the landing.
        private List<string> ThrustWarnings(int leg)
        {
            List<string> warnings = new List<string>();
            if (leg >= legs.Count) return warnings;
            RouteStop from = state.Stops[leg];
            RouteStop to = state.Stops[leg + 1];
            bool launch = from.Kind == StopKind.Surface;
            bool landing = to.Kind == StopKind.Surface && !(to.UseAtmosphere && bodies.Bodies[to.Body].HasAtmosphere
                                                             && bodies.Bodies[to.Body].SurfacePressure >= 0.2);
            if (!launch && !landing) return warnings;
            StageBudget.StageUse first = null;
            StageBudget.StageUse lastUsed = null;
            foreach (StageBudget.StageUse use in budget.Uses)
            {
                if (use.Leg != leg) continue;
                if (first == null) first = use;
                lastUsed = use;
            }
            if (launch && first != null) AddTwrWarning(warnings, first, from.Body, true);
            if (landing && lastUsed != null) AddTwrWarning(warnings, lastUsed, to.Body, false);
            return warnings;
        }

        // The thrust in vacuum over the weight the stage pushes as it starts its
        // part of the leg.
        private void AddTwrWarning(List<string> warnings, StageBudget.StageUse use, int body, bool launch)
        {
            StageRow row = stages.Find(s => s.Number == use.Stage);
            PlannerBody planner = bodies.Bodies[body];
            double gravity = planner.Mu / (planner.Radius * planner.Radius);
            if (row == null || use.Mass <= 0.0 || gravity <= 0.0) return;
            double twr = row.ThrustVacuum / (use.Mass * gravity);
            double needed = planner.HasAtmosphere && launch ? 1.2 : 1.0;
            if (twr < needed) warnings.Add("S" + row.Number + " TWR " + EngineerFormat.Ratio(twr) + " at " + planner.Name);
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

        private LegMode Mode(int leg)
        {
            LegMode mode;
            return Ready() && state.Modes.TryGetValue(leg, out mode) ? mode : LegMode.Whole;
        }

        private void SetMode(int leg, LegMode mode)
        {
            if (!Ready() || Mode(leg) == mode) return;
            if (mode == LegMode.Whole) state.Modes.Remove(leg);
            else state.Modes[leg] = mode;
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
