using System.Collections.Generic;
using System;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The planner (docs/player/planner.md): a route from destination to
    // destination, A -> B -> C, the steps between them with who flies each
    // (RouteSteps), what each step costs, and which of the vessel's stages pays for
    // it, as KSP fires them or as the player saves or drops them on a step. This
    // half holds the plan and works it out; the rows are in PlannerView.Rows.cs.
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
        // The lander as far as the vessel has that stage now: the player's, or
        // what the parts give (VesselShape). The plan keeps what the player set,
        // for a stage that is back after the vessel was taken apart.
        private int lander = -1;
        private int detectedLander = -1;
        private List<int> landerChoices = new List<int>();
        // The capsule, as the lander; it can start at a stage without delta-v.
        private int capsule = -1;
        private int detectedCapsule = -1;
        // What the log last said was found, so that it says it once.
        private string detectedLogged;
        private Dictionary<int, List<Part>> burningParts = new Dictionary<int, List<Part>>();
        private List<int> parachuteStages = new List<int>();
        private List<int> heatShieldStages = new List<int>();
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

        // The planner closes: nothing stays coloured, the list of bodies goes.
        internal void Flush()
        {
            highlight.Clear();
            if (typingLocked) InputLockManager.RemoveControlLock(TypingLock);
            typingLocked = false;
            BodyPicker.Close(this);
            Dropdown.Close(this);
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
            Detect(readout.PartFacts);
            parachuteStages = readout.ParachuteStages;
            heatShieldStages = readout.HeatShieldStages;
            steps = RouteSteps.Build(bodies.Bodies, state.Stops, HasLander(), state.PlaneChanges, HasCapsule());
            for (int leg = 0; leg < MaxStops; leg++)
                for (int role = 0; role < 3; role++) slots[leg, role] = -1;
            for (int i = 0; i < steps.Count; i++)
                if (steps[i].Segment < MaxStops) slots[steps[i].Segment, (int)steps[i].Role] = i;
            if (selectedLeg >= MaxStops || (selectedLeg >= 0 && slots[selectedLeg, (int)selectedRole] < 0)) selectedLeg = -1;
            if (selectedStage >= 0 && !stages.Exists(row => row.Number == selectedStage)) selectedStage = -1;

            List<double> needs = new List<double>();
            List<LegMode> modes = new List<LegMode>();
            List<bool> refuel = new List<bool>();
            List<bool> payload = new List<bool>();
            List<StagePlan> plans = new List<StagePlan>();
            foreach (RouteStep step in steps)
            {
                needs.Add(step.Leg.DeltaV);
                modes.Add(step.Mode);
                refuel.Add(step.RefuelAfter);
                payload.Add(step.PayloadAfter);
                plans.Add(state.Stops[step.Goal].Stages[(int)step.Role]);
            }
            List<BudgetStage> available = new List<BudgetStage>();
            foreach (StageRow row in allStages)
                available.Add(new BudgetStage
                {
                    Number = row.Number, StartMass = row.StartMass, EndMass = row.EndMass, DeltaV = row.DeltaVVacuum,
                });
            budget = StageBudget.Allocate(available, needs, state.Margin, plans, lander, modes, refuel, payload, capsule);
            burningParts = readout.BurningParts;
            warnings = new List<string>[steps.Count];
            for (int i = 0; i < steps.Count; i++) warnings[i] = StepWarnings(i);
            RefreshDetail();
            UpdateBars();
            Mark();
        }

        // The lander and the capsule: the player's where they are stages of the
        // vessel now, or what its parts give where the player left them to that.
        private void Detect(List<PartFacts> parts)
        {
            List<int> numbers = allStages.ConvertAll(row => row.Number);
            int chosenLander = state.Lander >= 0 && landerChoices.Contains(state.Lander) ? state.Lander : -1;
            // A capsule the vessel does not have now counts for nothing, here too.
            int chosenCapsule = state.Capsule >= 0 && allStages.FindIndex(row => row.Number == state.Capsule) >= 1 ? state.Capsule : -1;
            detectedCapsule = VesselShape.Capsule(parts, numbers);
            detectedLander = VesselShape.Lander(parts, numbers, state.Capsule == PlannerState.Automatic ? detectedCapsule : chosenCapsule);
            if (detectedLander >= 0) detectedLander = LanderTop(detectedLander);
            if (!landerChoices.Contains(detectedLander)) detectedLander = -1;
            lander = state.Lander == PlannerState.Automatic ? detectedLander : chosenLander;
            if (detectedCapsule >= 0 && !CapsuleChoice(detectedCapsule)) detectedCapsule = -1;
            capsule = state.Capsule == PlannerState.Automatic ? detectedCapsule : chosenCapsule >= 0 && CapsuleChoice(chosenCapsule) ? chosenCapsule : -1;

            string found = "lander " + StageName(detectedLander) + ", capsule " + StageName(detectedCapsule);
            if (parts.Count > 0 && found != detectedLogged)
            {
                detectedLogged = found;
                Debug.Log(Core.Log.Tag + " Planner found from " + parts.Count + " parts: " + found + ".");
            }
        }

        private static string StageName(int number)
        {
            return number >= 0 ? "S" + number : "none";
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
            if (Carries(parachuteStages, HasCapsule() ? LegMode.Capsule : LegMode.Whole)) stop.UseAtmosphere = true;
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

        // Each stage's engines and the tanks it burns, in the stage's colour: all
        // of them, the ones the selected step has, or the selected stage.
        private void Mark()
        {
            Dictionary<Part, Color> parts = new Dictionary<Part, Color>();
            int step = Worked() ? SelectedStep() : -1;
            for (int i = 0; i < stages.Count; i++)
            {
                int number = stages[i].Number;
                if (selectedStage >= 0 && number != selectedStage) continue;
                if (selectedStage < 0 && step >= 0 && !budget.Stages[step].Exists(stage => stage.Stage == number)) continue;
                List<Part> list;
                if (!burningParts.TryGetValue(number, out list)) continue;
                Color color = PlannerColors.Vivid(PlannerColors.Stage(i));
                foreach (Part part in list)
                    if (part != null) parts[part] = color;
            }
            highlight.Show(parts);
        }

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
            if (step.PayloadAfter && budget.Payload[index] < 1e-3)
                list.Add("No payload: a stage without engines that drops it has to follow the burning one");

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
                string who = step.Mode == LegMode.Capsule ? "the capsule" : step.Mode == LegMode.Lander ? "the lander" : "the vessel";
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
            int across = slots[step.Segment, (int)StepRole.Transfer];
            return step.Role == StepRole.Land && across >= 0 && steps[across].From.Body != steps[across].To.Body;
        }

        // A part of that kind on what lands: the lander's where it lands alone, the
        // rest's once the lander stayed behind, anywhere on the vessel otherwise. The
        // rest's parts are decoupled above the lander's top stage.
        private bool Carries(List<int> decoupleStages, LegMode mode)
        {
            foreach (int stage in decoupleStages)
            {
                if (mode == LegMode.Capsule && stage > capsule) continue;
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

        private bool HasCapsule()
        {
            return Ready() && capsule >= 0;
        }

        private bool IsCapsuleStage(int number)
        {
            return HasCapsule() && number <= capsule;
        }

        // The capsule can start at any stage but the first, below the lander's top.
        private bool CapsuleChoice(int number)
        {
            int at = allStages.FindIndex(row => row.Number == number);
            return at >= 1 && (lander < 0 || number < lander);
        }

        // A lander can start at any stage but the first, something has to wait for
        // it, and with the stages without delta-v KSP fires just before it: the one
        // with the lander's decoupler is among them. Without them, the decoupler's
        // stage would count to the rest, and the rest would be dropped with it.
        private List<int> LanderChoices()
        {
            List<int> choices = new List<int>();
            for (int i = 1; i < allStages.Count; i++)
                if (LanderTop(allStages[i].Number) == allStages[i].Number) choices.Add(allStages[i].Number);
            return choices;
        }

        // The stage the lander starts at, for a stage with delta-v: the stages
        // without delta-v fired just before it belong to it.
        private int LanderTop(int number)
        {
            int at = allStages.FindIndex(row => row.Number == number);
            while (at > 1 && allStages[at - 1].DeltaVVacuum <= 0.5) at--;
            return at >= 0 ? allStages[at].Number : number;
        }

        // "Automatic (S2)", "None", "S3": what the Vessel row's buttons show.
        private string LanderText()
        {
            return Ready() ? ChoiceText(state.Lander, detectedLander, lander) : "";
        }

        private string CapsuleText()
        {
            return Ready() ? ChoiceText(state.Capsule, detectedCapsule, capsule) : "";
        }

        private static string ChoiceText(int chosen, int detected, int now)
        {
            if (chosen == PlannerState.Automatic)
                return "<color=" + Good + ">Automatic</color> <color=" + Dim + ">(" + (detected >= 0 ? "S" + detected : "none") + ")</color>";
            // A stage the vessel does not have now counts for nothing until it is back.
            if (chosen >= 0 && now < 0) return "<color=" + Dim + ">S" + chosen + "</color>";
            if (now < 0) return "<b><u><color=" + Good + ">None</color></u></b>";
            return "<b><u><color=" + Good + ">S" + now + "</color></u></b>";
        }

        private void PickLander(DialogGUIBase button)
        {
            if (!Ready()) return;
            List<Choice> choices = new List<Choice>();
            AddPart(choices, "Automatic", "Find the lander by its engines and landing legs.", state.Lander == PlannerState.Automatic,
                () => SetLander(PlannerState.Automatic));
            AddPart(choices, "None", "The vessel has no lander.", state.Lander == PlannerState.None, () => SetLander(PlannerState.None));
            foreach (int number in landerChoices)
            {
                int picked = number;
                AddPart(choices, "S" + number, "This stage and every stage after it are the lander.", state.Lander == number,
                    () => SetLander(picked));
            }
            Dropdown.Show(this, button, choices, 132f);
        }

        private void PickCapsule(DialogGUIBase button)
        {
            if (!Ready()) return;
            List<Choice> choices = new List<Choice>();
            AddPart(choices, "Automatic", "Find the capsule by the command part that stays to the end and its parachutes.",
                state.Capsule == PlannerState.Automatic, () => SetCapsule(PlannerState.Automatic));
            AddPart(choices, "None", "The vessel has no capsule.", state.Capsule == PlannerState.None,
                () => SetCapsule(PlannerState.None));
            foreach (StageRow row in allStages)
            {
                int picked = row.Number;
                if (!CapsuleChoice(picked)) continue;
                AddPart(choices, "S" + picked, "This stage and every stage after it are the capsule.", state.Capsule == picked,
                    () => SetCapsule(picked));
            }
            Dropdown.Show(this, button, choices, 132f);
        }

        private static void AddPart(List<Choice> choices, string label, string tooltip, bool current, Callback pick)
        {
            choices.Add(new Choice { Label = label, Tooltip = tooltip, Current = current, Pick = pick });
        }

        private void SetLander(int number)
        {
            if (!Ready() || state.Lander == number) return;
            state.Lander = number;
            Changed();
        }

        private void SetCapsule(int number)
        {
            if (!Ready() || state.Capsule == number) return;
            state.Capsule = number;
            Changed();
        }

        // Who flies a step, as worked out or chosen.
        private Vehicle StepVehicle(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            return index >= 0 ? steps[index].Vehicle : Vehicle.Ship;
        }

        // Who can fly a step, for the list the step's button opens: the player's
        // choice, or back to the planner's.
        private void PickVehicle(int leg, StepRole role, DialogGUIBase button)
        {
            int index = Slot(leg, role);
            if (index < 0) return;
            RouteStep step = steps[index];
            List<Choice> choices = new List<Choice>();
            AddVehicle(choices, leg, role, step, Vehicle.Ship, "The whole vessel flies this step.");
            if (HasLander())
            {
                AddVehicle(choices, leg, role, step, Vehicle.Lander, "The lander flies this step alone while the rest waits.");
                AddVehicle(choices, leg, role, step, Vehicle.Rest,
                    "The vessel flies on without the lander, which stays where it is for good.");
            }
            if (HasCapsule())
                AddVehicle(choices, leg, role, step, Vehicle.Capsule,
                    "The capsule flies this step alone and leaves the rest of the vessel behind.");
            if (step.Chosen)
                choices.Add(new Choice
                {
                    Label = "Planner's choice", Tooltip = "Let the planner choose who flies this step again.",
                    Pick = () => ChooseVehicle(leg, role, step.Vehicle),
                });
            Dropdown.Show(this, button, choices, 132f);
        }

        private void AddVehicle(List<Choice> choices, int leg, StepRole role, RouteStep step, Vehicle vehicle, string tooltip)
        {
            choices.Add(new Choice
            {
                Label = VehicleName(vehicle), Tooltip = tooltip, Current = step.Vehicle == vehicle,
                Pick = () =>
                {
                    int index = Slot(leg, role);
                    if (index >= 0 && steps[index].Chosen && steps[index].Vehicle == vehicle) return;
                    ChooseVehicle(leg, role, vehicle);
                },
            });
        }

        internal static string VehicleName(Vehicle vehicle)
        {
            return vehicle == Vehicle.Rest ? "Without lander" : vehicle.ToString();
        }

        // The player's choice of who flies a step; the same choice again gives it
        // back to the planner.
        private void ChooseVehicle(int leg, StepRole role, Vehicle vehicle)
        {
            int index = Slot(leg, role);
            if (index < 0 || !Shown(leg + 1)) return;
            int[] by = state.Stops[leg + 1].By;
            by[(int)role] = steps[index].Chosen && steps[index].Vehicle == vehicle ? -1 : (int)vehicle;
            Changed();
        }

        // The lander refilled from the rest when it docks at the end of the step.
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

        // A payload stays at the destination.
        private bool LeavesPayload(int index)
        {
            return Shown(index) && state.Stops[index].LeavesPayload;
        }

        private void ToggleLeavesPayload(int index)
        {
            if (!Shown(index)) return;
            state.Stops[index].LeavesPayload = !state.Stops[index].LeavesPayload;
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
