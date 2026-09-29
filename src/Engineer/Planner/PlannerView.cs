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
        // The rows' width, and the parts' boxes around them, a margin wider on each side.
        private readonly float width;
        private readonly float outer;
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
        private readonly PartHighlight highlight = new PartHighlight();
        // The stages AirLaunch.Plan gave for this readout and launch.
        private EditorReadout planned;
        private PlannerBody plannedLaunch;
        private string plannedLaunchId;
        private List<StageRow> plannedStages = new List<StageRow>();
        private int plannedAir = -1;

        internal PlannerView(EditorEngineer owner, float width)
        {
            this.owner = owner;
            outer = width;
            this.width = width - 2f * BoxMargin;
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
                foundLander = null;
                fuelOpen.Clear();
                thrustOpen.Clear();
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
            ShapeHeaders();
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
            stageLabels.Clear();
            // The planner's own simulation only where the vessel or the launch changed.
            PlannerBody launch = LaunchSurface();
            string launchId = LaunchBody();
            if (readout != planned || launch != plannedLaunch || launchId != plannedLaunchId)
            {
                plannedStages = AirLaunch.Plan(readout.Stages, readout.PartFacts, readout.Sim, readout.Density, launch, launchId,
                    out plannedAir);
                planned = readout;
                plannedLaunch = launch;
                plannedLaunchId = launchId;
            }
            int airRow = plannedAir;
            allStages = new List<StageRow>(plannedStages);
            allStages.Sort((a, b) => b.Number.CompareTo(a.Number));
            if (airRow >= 0) stageLabels[airRow] = "Air";
            allStages = ClampedLaunch.Parallel(allStages, readout.PartFacts, stageLabels, LaunchGravity(), LaunchBody());
            stages = allStages.FindAll(row => row.DeltaVVacuum > 0.5);

            Detect(readout.PartFacts);
            steps = RouteSteps.Build(bodies.Bodies, PickedStops(), RouteSections(), state.PlaneChanges);
            for (int leg = 0; leg < MaxStops; leg++)
                for (int role = 0; role < RouteStop.Roles; role++) slots[leg, role] = -1;
            for (int i = 0; i < steps.Count; i++)
                if (steps[i].Segment < MaxStops) slots[steps[i].Segment, (int)steps[i].Role] = i;

            List<StagePlan> plans = new List<StagePlan>();
            foreach (RouteStep step in steps) plans.Add(state.Stops[step.Goal].Stages[(int)step.Role]);
            List<BudgetStage> available = new List<BudgetStage>();
            foreach (StageRow row in allStages)
                available.Add(new BudgetStage
                {
                    Number = row.Number, StartMass = row.StartMass, EndMass = row.EndMass, DeltaV = row.DeltaVVacuum,
                    Solid = row.Solid, FirstStepOnly = row.Air,
                });
            budget = StageBudget.Allocate(available, BudgetSteps(), state.Margin, plans,
                VesselShape.Split(available, parts, valid.ConvertAll(section => section.Group)));
            UpdateThrust();
            warnings = new List<string>[steps.Count];
            for (int i = 0; i < steps.Count; i++) warnings[i] = StepWarnings(i);
            UpdateBars();
            Mark();
        }

        // At the surface of the route's first stop, where the vessel lifts off.
        private double LaunchGravity()
        {
            if (!Ready() || state.Stops.Count == 0 || state.Stops[0].Body < 0) return StageTable.StandardGravity;
            PlannerBody body = bodies.Bodies[state.Stops[0].Body];
            return body.Mu / (body.Radius * body.Radius);
        }

        // The body the route lifts off from, where it starts on a surface; null else.
        private PlannerBody LaunchSurface()
        {
            if (!Ready() || state.Stops.Count == 0 || state.Stops[0].Body < 0 || state.Stops[0].Kind != StopKind.Surface) return null;
            return bodies.Bodies[state.Stops[0].Body];
        }

        // The name of the route's first body, whose air the vessel lifts off in; null
        // for none picked.
        private string LaunchBody()
        {
            if (!Ready() || state.Stops.Count == 0 || state.Stops[0].Body < 0) return null;
            return bodies.Bodies[state.Stops[0].Body].Id;
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

        // The stops up to the first whose body is not picked yet: the route so far.
        private List<RouteStop> PickedStops()
        {
            List<RouteStop> picked = new List<RouteStop>();
            foreach (RouteStop stop in state.Stops)
            {
                if (stop.Body < 0) break;
                picked.Add(stop);
            }
            return picked;
        }

        private bool HasBody(int index)
        {
            return Shown(index) && state.Stops[index].Body >= 0 && state.Stops[index].Body < bodies.Bodies.Count;
        }

        private static string Letter(int index)
        {
            return ((char)('A' + index)).ToString();
        }

        private string BodyName(int index)
        {
            if (!Shown(index)) return "";
            int body = state.Stops[index].Body;
            return body >= 0 && body < bodies.Bodies.Count ? bodies.Bodies[body].Name : "<color=" + Problem + ">Destination</color>";
        }

        private StopKind Kind(int index)
        {
            return Shown(index) ? state.Stops[index].Kind : StopKind.Orbit;
        }

        private bool HasAtmosphere(int index)
        {
            return HasBody(index) && bodies.Bodies[state.Stops[index].Body].HasAtmosphere;
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
            if (!HasBody(index) || !double.TryParse((text ?? "").Replace(',', '.'), System.Globalization.NumberStyles.Float,
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
            if (kind == StopKind.Orbit && state.Stops[index].Altitude <= 0.0 && HasBody(index))
                state.Stops[index].Altitude = bodies.Bodies[state.Stops[index].Body].LowOrbitAltitude;
            AutoChutes(index);
            Changed();
        }

        private void StepAltitude(int index, int direction)
        {
            if (!HasBody(index)) return;
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
            if (!HasBody(index)) return;
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
            if (last.Body < 0) return;
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
            Dropdown.Close(this);
            state.Stops.RemoveAt(index);
            state.RemoveStop(index);
            System.Array.Clear(more, 0, more.Length);
            fuelOpen.Clear();
            Changed();
        }

        // -------------------------------------------------------------- choosing

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

        // The stages the player keeps from a step, or drops after it (StagePlan, kept
        // on the stop the step leads to, by role).
        private StagePlan PlanOf(int step)
        {
            return step >= 0 && step < steps.Count ? state.Stops[steps[step].Goal].Stages[(int)steps[step].Role] : null;
        }

        private bool Saved(int step, int number)
        {
            StagePlan plan = PlanOf(step);
            return plan != null && plan.Save.Contains(number);
        }

        private bool DropsAfter(int step, int number)
        {
            StagePlan plan = PlanOf(step);
            return plan != null && plan.DropAfter.Contains(number);
        }

        private void ToggleSaved(int step, int number)
        {
            StagePlan plan = PlanOf(step);
            if (plan == null || number < 0) return;
            if (!plan.Save.Remove(number)) plan.Save.Add(number);
            Changed();
        }

        private void ToggleDropAfter(int step, int number)
        {
            StagePlan plan = PlanOf(step);
            if (plan == null || number < 0) return;
            if (!plan.DropAfter.Remove(number)) plan.DropAfter.Add(number);
            Changed();
        }

        // The rest's stages that hold fuel as the step ends, for its refill.
        private List<int> Tankers(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            return index >= 0 && budget.Tankers[index] != null ? budget.Tankers[index] : new List<int>();
        }

        // "from S3", "from the rest": where a refill comes from first; in orange a stage
        // that holds nothing then, so that the rest gives it in KSP's order.
        private string TankerText(int leg, StepRole role)
        {
            if (!Shown(leg + 1)) return "";
            int stage = state.Stops[leg + 1].RefuelFrom[(int)role];
            if (stage < 0) return "<color=" + Good + ">from the rest</color>";
            if (!Tankers(leg, role).Contains(stage)) return "<color=" + Warning + ">" + StageLabel(stage) + " is empty</color>";
            return "<b><u><color=" + Good + ">from S" + stage + "</color></u></b>";
        }

        // The rest's stages with fuel at that point, for the refill to come from first.
        private void PickTanker(int leg, StepRole role, DialogGUIBase button)
        {
            if (!Shown(leg + 1)) return;
            int[] from = state.Stops[leg + 1].RefuelFrom;
            List<Choice> choices = new List<Choice>
            {
                new Choice
                {
                    Label = "Rest, in staging order", Tooltip = "Refuel from the rest of the vessel's stages in the order KSP fires them.",
                    Current = from[(int)role] < 0, Pick = () => SetTanker(leg, role, -1),
                },
            };
            foreach (int tanker in Tankers(leg, role))
            {
                int number = tanker;
                choices.Add(new Choice
                {
                    Label = StageLabel(number), Tooltip = "Refill from this stage first, then from the others.",
                    Current = from[(int)role] == number, Pick = () => SetTanker(leg, role, number),
                });
            }
            Dropdown.Show(this, button, choices, 150f);
        }

        private void SetTanker(int leg, StepRole role, int stage)
        {
            if (!Shown(leg + 1) || state.Stops[leg + 1].RefuelFrom[(int)role] == stage) return;
            state.Stops[leg + 1].RefuelFrom[(int)role] = stage;
            Changed();
        }

        // What stands in the way of a step, for a line under it; empty where
        // nothing does.
        private List<string> StepWarnings(int index)
        {
            List<string> list = new List<string>();
            RouteStep step = steps[index];
            if (step.Problem != null) list.Add(step.Problem);
            if (budget.Short[index] > 0.5) list.Add(EngineerFormat.Speed(budget.Short[index]) + " short");

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
            double liftOff = first != null ? TwrAt(first.Stage, first.Mass, step.From.Body, true) : 0.0;
            bool runway = first != null && Runway(first.Stage);
            if (launch && first != null && !runway && liftOff < (bodies.Bodies[step.From.Body].HasAtmosphere ? 1.2 : 1.0))
                list.Add("TWR " + EngineerFormat.Ratio(liftOff) + " of " + StageLabel(first.Stage) + " is too low to lift off " + RouteSteps.At(bodies.Bodies, step.From).Substring(3));
            double setDown = lastUsed != null ? TwrAt(lastUsed.Stage, lastUsed.Mass, step.To.Body, true) : 0.0;
            if (landing && lastUsed != null && setDown < 1.0)
                list.Add("TWR " + EngineerFormat.Ratio(setDown) + " of " + StageLabel(lastUsed.Stage) + " is too low to land " + RouteSteps.At(bodies.Bodies, step.To));

            // Parachutes for a landing on them; a heat shield where it comes down
            // from another body.
            if (step.To.Kind == StopKind.Surface && step.To.UseAtmosphere && bodies.Bodies[step.To.Body].HasAtmosphere)
            {
                string who = step.Flyer.Kind == FlyerKind.Alone ? "the " + Lower(FlyerName(step.Flyer)) : "the vessel";
                if (!Carries(part => part.Parachute, step)) list.Add("No parachutes on " + who);
                if (FromAfar(index) && !Carries(part => part.HeatShield, step)) list.Add("No heat shield on " + who);
            }
            // The burn in orbit that takes so long that too much of it pushes the wrong
            // way, the longest of the step.
            Burn longest = index < thrustOf.Length && thrustOf[index] != null ? thrustOf[index].Longest : null;
            if (longest != null && longest.Loss > BurnCheck.MaxLoss)
                list.Add(longest.Line.Name + " too long: " + EngineerFormat.Time(longest.Seconds) + ", " + Percent(longest.Share)
                         + " of the orbit");
            // What it turns and docks with (UpdateThrust): too slow a turn, no RCS or
            // no RCS propellant to dock.
            if (index < turnAlerts.Length && turnAlerts[index] != null) list.AddRange(turnAlerts[index]);
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

        // The stage's thrust over the weight of `mass` at the body's surface: in vacuum,
        // or in the air there where it has any (StageRow.ThrustAtSurface).
        private double TwrAt(int stage, double mass, int body, bool air)
        {
            StageRow row = stages.Find(s => s.Number == stage);
            PlannerBody planner = bodies.Bodies[body];
            double gravity = planner.Mu / (planner.Radius * planner.Radius);
            if (row == null || mass <= 0.0 || gravity <= 0.0) return 0.0;
            double thrust = row.ThrustVacuum;
            double surface;
            if (air && planner.HasAtmosphere && row.ThrustAtSurface.TryGetValue(planner.Id, out surface)) thrust = surface;
            return thrust / (mass * gravity);
        }

        // Lifting off on air-breathing engines with wings and wheels: along a runway,
        // at any TWR.
        private bool Runway(int stage)
        {
            StageRow row = stages.Find(s => s.Number == stage);
            return row != null && row.Air && VesselShape.Runway(parts);
        }

        // The air at a body's surface, in atmospheres; 0 where it has none.
        private double SurfaceAir(int body)
        {
            PlannerBody planner = bodies.Bodies[body];
            return planner.HasAtmosphere ? planner.SurfacePressure : 0.0;
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
