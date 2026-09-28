using System.Collections.Generic;
using System;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The planner (docs/player/planner.md): a route from destination to
    // destination, A -> B -> C, the steps between them with who flies each
    // (RouteSteps), what each step costs, and which of the vessel's stages pays for
    // it, as KSP fires them or as the player saves or drops them on a step. This
    // part holds the plan and works it out; the vessel's sections are in
    // PlannerView.Sections.cs, the rows in PlannerView.Rows.cs.
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
        // one on screen; a new one without the sections taken away from the last.
        private bool reload;
        private bool fresh;
        private float nextEvaluation;
        private float saveAt = -1f;

        // What the plan comes to, worked out a few times a second: the steps, and
        // for each leg (the way from one destination to the next) and role the step
        // there, -1 where there is none.
        private List<RouteStep> steps = new List<RouteStep>();
        private readonly int[,] slots = new int[MaxStops, RouteStop.Roles];
        private List<string>[] warnings = new List<string>[0];
        private StageBudget.Result budget;
        // The stages with delta-v, in firing order, and all of them.
        private List<StageRow> stages = new List<StageRow>();
        private List<StageRow> allStages = new List<StageRow>();
        // The selected leg's or stage's details, written once per evaluation
        // and on each selection.
        private string detailTitle = "";
        private string[] detailLines = new string[0];

        // The step, by its leg and role, or the stage whose parts alone are
        // coloured on the vessel; -1 for none, when every stage shows its colour.
        private int selectedLeg = -1;
        private StepRole selectedRole;
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
            fresh = true;
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
                if (fresh) state.Dismissed.Clear();
                vessel = name;
                reload = false;
                fresh = false;
                foundCapsule = null;
                foundSatellite = null;
                selectedLeg = -1;
                selectedStage = -1;
                nextEvaluation = 0f;
                System.Array.Clear(more, 0, more.Length);
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

        private const string TypingLock = "ReDefinitionPlannerTyping";
        private bool typingLocked;

        // Every frame while the planner is open. While an altitude is typed, the
        // editor's keys are held, so that the digits do not reach its shortcuts.
        internal void Keep()
        {
            highlight.Refresh();
            bool typing = Typing();
            if (typing == typingLocked) return;
            typingLocked = typing;
            if (typing) InputLockManager.SetControlLock(ControlTypes.All, TypingLock);
            else InputLockManager.RemoveControlLock(TypingLock);
        }

        private bool Typing()
        {
            foreach (DialogGUITextInput field in altitudeFields)
            {
                TMPro.TMP_InputField input = field != null && field.uiItem != null ? field.uiItem.GetComponent<TMPro.TMP_InputField>() : null;
                if (input != null && input.isFocused) return true;
            }
            return false;
        }

        // The planner closes: the lists go, then the marks, since a list closing
        // takes its entry's marks away through the highlight.
        internal void Flush()
        {
            BodyPicker.Close(this);
            Dropdown.Close(this);
            highlight.Clear();
            if (typingLocked) InputLockManager.RemoveControlLock(TypingLock);
            typingLocked = false;
            if (state != null && vessel != null && bodies != null && saveAt > 0f) state.Save(vessel, bodies);
            saveAt = -1f;
        }

        private void Evaluate()
        {
            EditorReadout readout = owner.Readout ?? new EditorReadout();
            allStages = new List<StageRow>(readout.Stages);
            allStages.Sort((a, b) => b.Number.CompareTo(a.Number));
            stages = allStages.FindAll(row => row.DeltaVVacuum > 0.5);

            Detect(readout.PartFacts);
            steps = RouteSteps.Build(bodies.Bodies, state.Stops, RouteSections(), state.PlaneChanges);
            for (int leg = 0; leg < MaxStops; leg++)
                for (int role = 0; role < RouteStop.Roles; role++) slots[leg, role] = -1;
            for (int i = 0; i < steps.Count; i++)
                if (steps[i].Segment < MaxStops) slots[steps[i].Segment, (int)steps[i].Role] = i;
            if (selectedLeg >= MaxStops || (selectedLeg >= 0 && slots[selectedLeg, (int)selectedRole] < 0)) selectedLeg = -1;
            if (selectedStage >= 0 && !stages.Exists(row => row.Number == selectedStage)) selectedStage = -1;

            List<StagePlan> plans = new List<StagePlan>();
            foreach (RouteStep step in steps) plans.Add(state.Stops[step.Goal].Stages[(int)step.Role]);
            List<BudgetStage> available = new List<BudgetStage>();
            foreach (StageRow row in allStages)
                available.Add(new BudgetStage
                {
                    Number = row.Number, StartMass = row.StartMass, EndMass = row.EndMass, DeltaV = row.DeltaVVacuum,
                });
            budget = StageBudget.Allocate(available, BudgetSteps(), state.Margin, plans,
                VesselShape.Split(available, parts, valid.ConvertAll(section => section.Group)));
            warnings = new List<string>[steps.Count];
            for (int i = 0; i < steps.Count; i++) warnings[i] = StepWarnings(i);
            RefreshDetail();
            UpdateBars();
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

        private int Slot(int leg, StepRole role)
        {
            return Worked() && leg >= 0 && leg < MaxStops ? slots[leg, (int)role] : -1;
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

        private string AltitudeKm(int index)
        {
            return Shown(index) ? (state.Stops[index].Altitude / 1000.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "";
        }

        // Typed in kilometres, kept between the lowest safe orbit and the edge of the
        // body's sphere of influence.
        private void SetAltitude(int index, string text)
        {
            double km;
            if (!Shown(index) || !double.TryParse((text ?? "").Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out km)) return;
            RouteStop stop = state.Stops[index];
            double altitude = Math.Max(bodies.Bodies[stop.Body].LowOrbitAltitude, Math.Min(km * 1000.0, bodies.MaxAltitude(stop.Body)));
            if (Math.Abs(altitude - stop.Altitude) < 0.5) return;
            stop.Altitude = altitude;
            Changed();
        }

        // The body buttons of the stops, for the list to fold out under.
        private readonly DialogGUIBase[] bodyButtons = new DialogGUIBase[MaxStops];

        private void PickBody(int index)
        {
            if (!Shown(index)) return;
            // As a list under the body's button, each moon under its planet.
            List<Choice> choices = new List<Choice>();
            foreach (int body in bodies.Order)
            {
                int picked = body;
                int depth = 0;
                for (int at = bodies.Bodies[body].Parent; at >= 0 && depth < 8; at = bodies.Bodies[at].Parent) depth++;
                choices.Add(new Choice
                {
                    Label = bodies.Bodies[body].Name, Current = body == state.Stops[index].Body, Indent = depth * 12f,
                    Pick = () => SetBody(index, picked),
                });
            }
            Dropdown.Show(this, bodyButtons[index], choices, 150f);
        }

        private void SetBody(int index, int body)
        {
            if (!Shown(index) || body < 0 || body >= bodies.Bodies.Count) return;
            RouteStop stop = state.Stops[index];
            stop.Body = body;
            stop.Altitude = bodies.Bodies[body].LowOrbitAltitude;
            if (!bodies.Bodies[body].HasAtmosphere) stop.UseAtmosphere = false;
            AutoChutes(index);
            Changed();
        }

        private void SetKind(int index, StopKind kind)
        {
            if (!Shown(index) || state.Stops[index].Kind == kind) return;
            state.Stops[index].Kind = kind;
            if (kind == StopKind.Orbit && state.Stops[index].Altitude <= 0.0)
                state.Stops[index].Altitude = bodies.Bodies[state.Stops[index].Body].LowOrbitAltitude;
            AutoChutes(index);
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

        // The last destination, a surface with air: home on the parachutes, where
        // what lands there has any, the capsule's where there is one.
        private void AutoChutes(int index)
        {
            RouteStop stop = state.Stops[index];
            if (index != state.Stops.Count - 1 || index == 0 || stop.Kind != StopKind.Surface
                || !bodies.Bodies[stop.Body].HasAtmosphere) return;
            if (CapsuleCarries(part => part.Parachute)) stop.UseAtmosphere = true;
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
            Dropdown.Close(this);
            state.Stops.RemoveAt(index);
            state.RemoveStop(index);
            System.Array.Clear(more, 0, more.Length);
            selectedLeg = -1;
            Changed();
        }

        // -------------------------------------------------------------- choosing

        // A step's fuel plan, or the whole route's again on the same step.
        private void SelectStep(int leg, StepRole role)
        {
            if (!Ready() || Slot(leg, role) < 0) return;
            bool same = selectedLeg == leg && selectedRole == role;
            selectedLeg = same ? -1 : leg;
            selectedRole = role;
            selectedStage = -1;
            RefreshDetail();
            Mark();
        }

        private void SelectRoute()
        {
            selectedLeg = -1;
            selectedStage = -1;
            RefreshDetail();
            Mark();
        }

        // The selected step's index, -1 for none.
        private int SelectedStep()
        {
            return selectedLeg >= 0 ? Slot(selectedLeg, selectedRole) : -1;
        }

        private void SelectStage(int index)
        {
            if (index >= stages.Count) return;
            int number = stages[index].Number;
            selectedStage = selectedStage == number ? -1 : number;
            RefreshDetail();
            Mark();
        }

        // The part group under the pointer in a section's list, the only thing the
        // planner marks on the vessel; null for none.
        private PartGroupChoice litGroup;

        // Its parts lit, or the vessel unmarked. Worked out again with the plan, a
        // few times a second: the group by its key, as the vessel's parts are read
        // anew each time.
        private void Mark()
        {
            PartGroupChoice group = litGroup != null ? Group(litGroup.Key) : null;
            if (group == null || !Dropdown.Open)
            {
                litGroup = null;
                highlight.Clear();
                return;
            }
            Dictionary<Part, Color> lit = new Dictionary<Part, Color>();
            foreach (int at in group.Members)
            {
                Part part = at < parts.Count ? parts[at].Tag as Part : null;
                if (part != null) lit[part] = Picked;
            }
            highlight.Show(lit);
        }

        // The pointer onto a list's entry, or off it.
        private void ShowGroup(PartGroupChoice group)
        {
            litGroup = group;
            Mark();
        }

        // The colour of the parts a list's entry names.
        private static readonly Color Picked = new Color(0.56f, 0.86f, 0.48f);

        // What the player chose for a stage on the selected step.
        private enum StageChoice
        {
            Burns,
            Save,
            DropAfter,
        }

        private StagePlan SelectedPlan()
        {
            int step = SelectedStep();
            return step >= 0 ? state.Stops[steps[step].Goal].Stages[(int)steps[step].Role] : null;
        }

        private StageChoice ChoiceOf(int number)
        {
            StagePlan plan = SelectedPlan();
            if (plan == null) return StageChoice.Burns;
            return plan.Save.Contains(number) ? StageChoice.Save : plan.DropAfter.Contains(number) ? StageChoice.DropAfter : StageChoice.Burns;
        }

        private void SetChoice(int number, StageChoice choice)
        {
            StagePlan plan = SelectedPlan();
            if (plan == null || number < 0 || ChoiceOf(number) == choice) return;
            plan.Save.Remove(number);
            plan.DropAfter.Remove(number);
            if (choice == StageChoice.Save) plan.Save.Add(number);
            else if (choice == StageChoice.DropAfter) plan.DropAfter.Add(number);
            Changed();
        }

        // What stands in the way of a step, for a line under it; empty where
        // nothing does.
        private List<string> StepWarnings(int index)
        {
            List<string> list = new List<string>();
            RouteStep step = steps[index];
            if (step.Problem != null) list.Add(step.Problem);
            if (budget.Short[index] > 0.5) list.Add("Short of " + EngineerFormat.Speed(budget.Short[index]));

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
                string who = step.Flyer.Kind == FlyerKind.Alone ? "the " + Lower(FlyerName(step.Flyer)) : "the vessel";
                if (!Carries(part => part.Parachute, step)) list.Add("No parachutes on " + who);
                if (FromAfar(index) && !Carries(part => part.HeatShield, step)) list.Add("No heat shield on " + who);
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
            int across = slots[step.Segment, (int)StepRole.Transfer];
            return step.Role == StepRole.Land && across >= 0 && steps[across].From.Body != steps[across].To.Body;
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

        // A section refilled from the rest when it docks at the end of the step.
        private bool Refuels(int leg, StepRole role)
        {
            return Shown(leg + 1) && state.Stops[leg + 1].Refuel[(int)role];
        }

        private void ToggleRefuel(int leg, StepRole role)
        {
            if (!Shown(leg + 1)) return;
            bool[] refuel = state.Stops[leg + 1].Refuel;
            refuel[(int)role] = !refuel[(int)role];
            Changed();
        }

        private bool SplitsBurns(int index)
        {
            return Shown(index) && state.Stops[index].SplitBurns;
        }

        private void SetSplitBurns(int index, bool split)
        {
            if (!Shown(index) || state.Stops[index].SplitBurns == split) return;
            state.Stops[index].SplitBurns = split;
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
