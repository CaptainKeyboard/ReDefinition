using System.Collections.Generic;
using System;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The engineer view's second tab (docs/player/planner.md): a route from stop
    // to stop, A -> B -> C, what each leg costs, and which of the vessel's stages
    // pays for it. This half holds the plan and works it out; the rows are in
    // PlannerView.Rows.cs.
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
        // The lander and the kept stages as far as the vessel has those stages
        // now. The plan keeps what the player set, for a stage that is back
        // after the vessel was taken apart.
        private int lander = -1;
        private List<int> landerChoices = new List<int>();
        private Dictionary<int, int> kept = new Dictionary<int, int>();
        // Each leg's details, written once per evaluation rather than each frame.
        private string[] legDetails = new string[0];
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

        // Once the rows are created: symbols on the buttons, the bars' images.
        internal void Finish()
        {
            after.Run();
        }

        internal void Dispose()
        {
            Flush();
            BodyPicker.Close();
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
            BodyPicker.Close();
        }

        internal void Flush()
        {
            highlight.Clear();
            BodyPicker.Close();
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
            landerChoices = LanderChoices();
            lander = state.Lander >= 0 && landerChoices.Contains(state.Lander) ? state.Lander : -1;
            List<LegMode> modes = HasLander() ? state.ModeList(legs.Count) : null;
            budget = StageBudget.Allocate(available, needs, state.Margin, kept, lander, modes);
            meetings = modes != null ? RoutePlanner.Meetings(state.Stops, modes) : new int[legs.Count];
            if (modes == null) for (int i = 0; i < meetings.Length; i++) meetings[i] = -1;
            burningParts = readout.BurningParts;
            legDetails = new string[legs.Count];
            for (int leg = 0; leg < legs.Count; leg++) legDetails[leg] = LegDetail(leg);
            UpdateBars();
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
            BodyPicker.Show(bodies, state.Stops[index].Body, body => SetBody(index, body));
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

        private void SetAtmosphere(int index, bool on)
        {
            if (!Shown(index) || state.Stops[index].UseAtmosphere == on) return;
            state.Stops[index].UseAtmosphere = on && HasAtmosphere(index);
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
            BodyPicker.Close();
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

        private bool IsKept(int stageIndex, int leg)
        {
            int keptLeg;
            return Ready() && stageIndex < stages.Count && state.Kept.TryGetValue(stages[stageIndex].Number, out keptLeg)
                   && keptLeg == leg;
        }

        // A toggle calls this whenever its value differs from the plan's, after
        // a click and when it is shown again: only a change counts.
        private void SetKept(int stageIndex, int leg, bool on)
        {
            if (!Ready() || stageIndex >= stages.Count || on == IsKept(stageIndex, leg)) return;
            int stage = stages[stageIndex].Number;
            if (on) state.Kept[stage] = leg;
            else if (IsKept(stageIndex, leg)) state.Kept.Remove(stage);
            else return;
            Changed();
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

        // Switched on, the stage and every stage KSP fires after it become the
        // lander. Switched off, the lander's first stage with delta-v leaves no
        // lander; a stage further down makes the lander start there. A toggle
        // calls this whenever its value differs from the plan's, after a click
        // and when it is shown again: only a change counts.
        private void SetLander(int stageIndex, bool on)
        {
            if (!Ready() || stageIndex >= stages.Count) return;
            int number = stages[stageIndex].Number;
            if (!landerChoices.Contains(number) || on == IsLanderStage(number)) return;
            state.Lander = !on && number == FirstBurning(lander) ? -1 : LanderTop(number);
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
            double margin = System.Math.Round(percent) / 100.0;
            if (!Ready() || System.Math.Abs(margin - state.Margin) < 1e-6) return;
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
