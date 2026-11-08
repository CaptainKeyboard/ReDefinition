using System;
using System.Collections.Generic;
using ReDefinition.Autopilot.Planning;
using static ReDefinition.Engineer.PlanText;

namespace ReDefinition.Engineer
{
    // The planner's plan (docs/player/planner.md): a route from destination to destination, A -> B -> C. It holds the steps between
    // them with who flies each (RouteSteps), what each costs, and which of the vessel's stages pays for it, as KSP fires them or as the
    // player saves or drops them on a step. This part holds the plan, loads and saves it and works it out. The vessel's sections are in
    // PlanSections, the thrust check in PlanThrust, the fuel plan's figures in PlanFuel, the route's editing in PlannerModel.Route.cs and
    // the texts of the steps in PlannerModel.Texts.cs. The model holds no dialog elements, so it is tested without the game; the rows are
    // PlannerView's.
    internal sealed partial class PlannerModel
    {
        // The most stops a route has, the most steps the fuel plan and the thrust check show, and the most lines of warning under a step.
        internal const int HardStops = 11;
        internal const int HardFuelSteps = 30;
        internal const int MaxWarnings = 3;

        // Seconds after the last change the plan is saved, so that a run of clicks writes the file once; and the step the
        // altitude buttons take, in metres.
        private const float SaveDelay = 1.5f;
        private const double AltitudeStep = 10000.0;

        // The window the planner sits in, whose readout of the vessel the plan is worked out from.
        internal IPlannerHost Host { get; private set; }
        // How many rows are made, by PlannerPool's level.
        internal PlannerPool Pool { get; private set; }

        // What the window gives the part "Guidance"; null in the editor, which has no flight to plan.
        internal IPlanControls Controls;

        // Where the model writes its log lines, with the tag in front, and where it reports how long a part of its work took (PlannerTiming.Stop);
        // nowhere until the view gives them a place. And the time the save delay is counted in.
        internal Action<string> Write;
        internal Action<string, long> Timer;
        internal Func<float> Clock = () => UnityEngine.Time.unscaledTime;

        // Called where the vessel's plan was loaded, where the host holds another flight plan, where the plan was worked out, where
        // the lists folded out under the rows are for rows that change, and where a stop went. The view drops what it holds for rows.
        internal Action Reloaded;
        internal Action PlanArrived;
        internal Action Evaluated;
        internal Action ListsStale;
        internal Action StopsReshaped;

        // The system's bodies, read once; the vessel's plan; and the vessel's name the plan is kept under.
        internal PlannerBodies Bodies { get; private set; }
        internal PlannerState State { get; private set; }
        internal string Vessel { get; private set; }
        // KSP loaded a vessel or started a new one: its own plan comes, not the
        // one on screen; a new one without the sections taken away from the last.
        private bool reload;
        private bool fresh;
        // Whether the plan is worked out again. That happens only where something it rests on changed:
        // the route or a setting in the planner (Changed), the vessel's plan loaded,
        // or the vessel as read anew. A new readout (the host's, read only where KSP
        // changed it) tells that, by no longer being the one last worked
        // out from.
        private bool dirty = true;
        private EditorReadout evaluated;
        private float saveAt = -1f;
        // The flight plan the heading's summary was made for.
        private RoutePlan noticedPlan;

        // A counter that rises when something the rows show may have changed. The rows' texts, which
        // KSP's labels ask for in every frame they show (DialogGUILabel.Update), are worked out again
        // only then, and the string is kept until then. A change is the plan worked out anew, a change
        // of the route or a setting, or the player's click or key in the editor, which opens and folds rows.
        internal int Version { get; private set; }

        // What the plan comes to, worked out where it changed (dirty): the steps, and
        // for each leg (the way from one destination to the next) and role the step
        // there, -1 where there is none.
        internal List<RouteStep> Steps { get; private set; }
        private readonly int[,] slots;
        internal List<string>[] Warnings { get; private set; }
        internal StageBudget.Result Budget { get; private set; }
        // The stages with delta-v, in firing order, and all of them.
        internal List<StageRow> Stages { get; private set; }
        internal List<StageRow> AllStages { get; private set; }
        // "S4", or "S4+3" for stages that burn together at lift-off (ClampedLaunch), "Air" for the jets' part of the launch.
        internal readonly Dictionary<int, string> StageLabels = new Dictionary<int, string>();
        // The stages AirLaunch.Plan gave for this readout and launch.
        private EditorReadout planned;
        private PlannerBody plannedLaunch;
        private string plannedLaunchId;
        private List<StageRow> plannedStages = new List<StageRow>();
        private int plannedAir = -1;

        // The vessel's sections, the thrust check and the fuel plan's figures.
        internal PlanSections Sections { get; private set; }
        internal PlanThrust Thrust { get; private set; }
        internal PlanFuel Fuel { get; private set; }

        // `level`: how many rows the window is made with (PlannerPool); the plan is worked out for as many stops, steps and stages.
        internal PlannerModel(IPlannerHost host, int level)
        {
            Host = host;
            Pool = PlannerPool.Of(level);
            Steps = new List<RouteStep>();
            Warnings = new List<string>[0];
            Stages = new List<StageRow>();
            AllStages = new List<StageRow>();
            slots = new int[Pool.Stops, RouteStop.Roles];
            Sections = new PlanSections(this);
            Thrust = new PlanThrust(this);
            Fuel = new PlanFuel(this);
            host.WatchVessel(OnVesselChanged, OnVesselFresh);
        }

        // The host's events are let go.
        internal void Release()
        {
            Host.StopWatching();
        }

        // Another vessel is in the window: its plan is loaded.
        private void OnVesselChanged()
        {
            reload = true;
        }

        // A new vessel was begun: its plan is loaded, and the planner adds the sections it finds by itself again.
        private void OnVesselFresh()
        {
            reload = true;
            fresh = true;
        }

        // Reports that a part of the work that began at `start` is over.
        private void Lap(string part, long start)
        {
            if (Timer != null) Timer(part, start);
        }

        // Writes a line to the log, with the tag.
        internal void Say(string text)
        {
            if (Write != null) Write(Core.Log.Tag + " " + text);
        }

        // Closes the lists folded out under the rows.
        internal void CloseLists()
        {
            if (ListsStale != null) ListsStale();
        }

        // The bodies and the vessel's plan, in place of what Tick reads and loads.
        internal void Install(PlannerBodies bodies, PlannerState state, string vessel)
        {
            Bodies = bodies;
            State = state;
            Vessel = vessel;
            dirty = true;
        }

        // Whether the bodies are read and the plan loaded.
        internal bool Ready()
        {
            return State != null && Bodies != null;
        }

        // Whether the plan is worked out for the steps there are now.
        internal bool Worked()
        {
            return Ready() && Budget != null && Budget.Needed.Length == Steps.Count;
        }

        // The index in `Steps` of the leg's step in that role; -1 where it has none or the plan is not worked out.
        internal int Slot(int leg, StepRole role)
        {
            return Worked() && leg >= 0 && leg < Pool.Stops ? slots[leg, (int)role] : -1;
        }

        // Something the rows show may have changed: their texts are made again.
        internal void Touch()
        {
            Version++;
        }

        // The plan changed: the texts are made again, the plan is saved after SaveDelay and worked out again.
        internal void Changed()
        {
            Version++;
            saveAt = Clock() + SaveDelay;
            dirty = true;
        }

        // ---------------------------------------------------------------- update

        // Loads the vessel's plan where the vessel is another, saves it once SaveDelay has passed, and works the plan out again where
        // something it rests on changed.
        internal void Tick()
        {
            string name = Host.VesselName();
            if (Bodies == null)
            {
                Bodies = PlannerBodies.Read();
                if (Bodies.Bodies.Count == 0) { Bodies = null; return; }
            }
            if (State == null || reload)
            {
                // A vessel loaded or new brings its own plan, or the default.
                if (State != null && Vessel != null && saveAt > 0f) State.Save(Vessel, Bodies);
                saveAt = -1f;
                State = PlannerState.Load(name, Bodies);
                if (fresh) State.Dismissed.Clear();
                Vessel = name;
                reload = false;
                fresh = false;
                Sections.ForgetFound();
                dirty = true;
                if (Reloaded != null) Reloaded();
            }
            else if (name != Vessel)
            {
                // A vessel renamed takes its plan along under the new name.
                Vessel = name;
                Changed();
            }
            if (saveAt > 0f && Clock() >= saveAt)
            {
                State.Save(Vessel, Bodies);
                saveAt = -1f;
            }
            NoticePlan();
            if (!dirty && Host.Readout == evaluated) return;
            dirty = false;
            evaluated = Host.Readout;
            Evaluate();
        }

        // Every frame: where the host holds another flight plan, the heading's summary is made again and the view opens the part to show it.
        private void NoticePlan()
        {
            RoutePlan plan = Host.Plan;
            if (plan == noticedPlan) return;
            noticedPlan = plan;
            Version++;
            dirty = true;
            if (plan != null && PlanArrived != null) PlanArrived();
        }

        // Writes the plan to the file now, for the probes, which change the route behind the rows' backs.
        internal void SaveNow()
        {
            // The sections let go at the stops are worked out with the plan: a change not yet worked out would be saved without them.
            if (dirty && Ready())
            {
                dirty = false;
                evaluated = Host.Readout;
                Evaluate();
            }
            if (State != null && Vessel != null && Bodies != null) State.Save(Vessel, Bodies);
        }

        // The plan is saved where a change waits for its delay; the delay is over.
        internal void SaveIfPending()
        {
            if (State != null && Vessel != null && Bodies != null && saveAt > 0f) State.Save(Vessel, Bodies);
            saveAt = -1f;
        }

        // Works the plan out: the stages the planner budgets with, the vessel's sections, the steps, which stage pays for
        // which step, the thrust checks and each step's warnings. Then it tells the view, which draws the bars and marks the vessel.
        internal void Evaluate()
        {
            long whole = PlannerTiming.Start();
            long t = whole;
            Version++;
            FollowVessel();
            EditorReadout readout = Host.Readout ?? new EditorReadout();
            StageLabels.Clear();
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
            AllStages = new List<StageRow>(plannedStages);
            AllStages.Sort((a, b) => b.Number.CompareTo(a.Number));
            if (airRow >= 0) StageLabels[airRow] = "Air";
            // A lift-off only where the route starts on a surface: from an orbit, a
            // weak first stage (an ion engine before a Spark) is not held on a pad.
            if (launch != null)
                AllStages = ClampedLaunch.Parallel(AllStages, readout.PartFacts, StageLabels, LaunchGravity(), LaunchBody());
            Stages = AllStages.FindAll(row => row.DeltaVVacuum > 0.5);

            Lap("Eval.stages", t);
            t = PlannerTiming.Start();
            Sections.Detect(readout.PartFacts);
            Lap("Eval.detect", t);
            t = PlannerTiming.Start();
            Steps = RouteSteps.Build(Bodies.Bodies, PickedStops(), Sections.RouteSections(), true);
            Lap("Eval.steps", t);
            t = PlannerTiming.Start();
            TakeFlightPlan();
            TakeFlightState();
            Lap("Eval.takePlan", t);
            t = PlannerTiming.Start();
            for (int leg = 0; leg < Pool.Stops; leg++)
                for (int role = 0; role < RouteStop.Roles; role++) slots[leg, role] = -1;
            for (int i = 0; i < Steps.Count; i++)
                if (Steps[i].Segment < Pool.Stops) slots[Steps[i].Segment, (int)Steps[i].Role] = i;

            List<StagePlan> plans = new List<StagePlan>();
            foreach (RouteStep step in Steps) plans.Add(State.Stops[step.Goal].Stages[(int)step.Role]);
            List<BudgetStage> available = new List<BudgetStage>();
            foreach (StageRow row in AllStages)
                available.Add(new BudgetStage
                {
                    Number = row.Number, StartMass = row.StartMass, EndMass = row.EndMass, DeltaV = row.DeltaVVacuum,
                    Solid = row.Solid, FirstStepOnly = row.Air,
                });
            Budget = StageBudget.Allocate(available, Sections.BudgetSteps(), State.Margin, plans,
                VesselShape.Split(available, Sections.Parts, Sections.Valid.ConvertAll(section => section.Group)));
            Lap("Eval.budget", t);
            t = PlannerTiming.Start();
            Thrust.Update();
            Lap("Eval.thrust", t);
            t = PlannerTiming.Start();
            Warnings = new List<string>[Steps.Count];
            for (int i = 0; i < Steps.Count; i++) Warnings[i] = StepWarnings(i);
            Lap("Eval.warnings", t);
            if (Evaluated != null) Evaluated();
            Lap("Eval", whole);
        }

        // At the surface of the route's first stop, where the vessel lifts off.
        private double LaunchGravity()
        {
            if (!Ready() || State.Stops.Count == 0 || State.Stops[0].Body < 0) return StageTable.StandardGravity;
            PlannerBody body = Bodies.Bodies[State.Stops[0].Body];
            return body.Mu / (body.Radius * body.Radius);
        }

        // The body the route lifts off from, where it starts on a surface; null else.
        private PlannerBody LaunchSurface()
        {
            if (!Ready() || State.Stops.Count == 0 || State.Stops[0].Body < 0 || State.Stops[0].Kind != StopKind.Surface) return null;
            return Bodies.Bodies[State.Stops[0].Body];
        }

        // The name of the route's first body, whose air the vessel lifts off in; null
        // for none picked.
        private string LaunchBody()
        {
            if (!Ready() || State.Stops.Count == 0 || State.Stops[0].Body < 0) return null;
            return Bodies.Bodies[State.Stops[0].Body].Id;
        }

        // What stands in the way of a step, for a line under it; empty where
        // nothing does.
        private List<string> StepWarnings(int index)
        {
            List<string> list = new List<string>();
            RouteStep step = Steps[index];
            if (step.Problem != null) list.Add(step.Problem);
            if (Budget.Short[index] > 0.5) list.Add(EngineerFormat.Speed(Budget.Short[index]) + " short");

            // A launch or a powered landing needs the thrust to lift the vessel at
            // that body: the first stage the step burns, and the last one for the
            // landing.
            bool launch = step.From.Kind == StopKind.Surface;
            bool landing = step.To.Kind == StopKind.Surface && !Thrust.Chutes(step.To);
            StageBudget.StageUse first = Thrust.FirstUse(index);
            StageBudget.StageUse lastUsed = Thrust.LastUse(index);
            double liftOff = first != null ? Thrust.TwrAt(first.Stage, first.Mass, step.From.Body, true) : 0.0;
            bool runway = first != null && Thrust.Runway(first.Stage);
            if (launch && first != null && !runway && liftOff < Thrust.TwrRequired(true, false, step.From.Body))
                list.Add("TWR " + EngineerFormat.Ratio(liftOff) + " of " + StageLabel(first.Stage) + " is too low to lift off " + RouteSteps.At(Bodies.Bodies, step.From).Substring(3));
            double setDown = lastUsed != null ? Thrust.TwrAt(lastUsed.Stage, lastUsed.Mass, step.To.Body, true) : 0.0;
            if (landing && lastUsed != null && setDown < Thrust.TwrRequired(false, false, step.To.Body))
                list.Add("TWR " + EngineerFormat.Ratio(setDown) + " of " + StageLabel(lastUsed.Stage) + " is too low to land " + RouteSteps.At(Bodies.Bodies, step.To));

            // Parachutes for a landing on them; a heat shield where it comes down
            // from another body.
            if (step.To.Kind == StopKind.Surface && step.To.UseAtmosphere && Bodies.Bodies[step.To.Body].HasAtmosphere)
            {
                string who = step.Flyer.Kind == FlyerKind.Alone ? "the " + Lower(Sections.FlyerName(step.Flyer)) : "the vessel";
                if (!Sections.Carries(part => part.Parachute, step)) list.Add("No parachutes on " + who);
                if (FromAfar(index) && !Sections.Carries(part => part.HeatShield, step)) list.Add("No heat shield on " + who);
            }
            // The burn in orbit that takes so long that too much of it pushes the wrong
            // way, the longest of the step.
            Burn longest = Thrust.LongestBurn(index);
            if (longest != null && longest.Loss > BurnCheck.MaxLoss)
                list.Add(longest.Line.Name + " too long: " + EngineerFormat.Time(longest.Seconds) + ", " + Percent(longest.Share)
                         + " of the orbit");
            // What it turns and docks with (PlanThrust.Update): too slow a turn, no RCS or
            // no RCS propellant to dock.
            Thrust.AddAlerts(index, list);
            return list;
        }

        // The landing comes in from another body: straight, or carried by the rest
        // just before.
        private bool FromAfar(int index)
        {
            RouteStep step = Steps[index];
            if (step.From.Body != step.To.Body) return true;
            int across = slots[step.Segment, (int)StepRole.Transfer];
            return step.Role == StepRole.Land && across >= 0 && Steps[across].From.Body != Steps[across].To.Body;
        }

        // What the route's steps need, with the margin, in m/s.
        internal double TotalNeeded()
        {
            double needed = 0.0;
            if (Budget != null) foreach (double value in Budget.Needed) needed += value;
            return needed;
        }

        // What all stages with delta-v have in vacuum, in m/s.
        internal double TotalHas()
        {
            double has = 0.0;
            foreach (StageRow row in Stages) has += row.DeltaVVacuum;
            return has;
        }

        // ---------------------------------------------------------------- flight

        // Where the flight has a plan made as nodes for this route, the way across to a stop takes the delta-v of the plan's burns that lead to it.
        // The closed form's lines are scaled to their sum, so that their names and orbits stay. The launch and the landing are not nodes and stay
        // as they are. So does a step flown by a section, a step a section leaves or docks on, and a way split in two burns. A plan made for another
        // route is not used.
        private void TakeFlightPlan()
        {
            RoutePlan plan = Host.Plan;
            if (plan == null || plan.Failed || plan.Steps.Count == 0 || Bodies == null) return;
            List<RouteStop> picked = PickedStops();
            List<Destination> route = RouteSource.FromStops(picked, Bodies);
            if (route == null || RoutePlan.SignatureOf(route) != plan.Signature) return;
            // The index of a stop in the route the plan was made for: the entries and transfers the autopilot flies through are not stops of it.
            var destination = new int[picked.Count];
            int count = 0;
            for (int i = 0; i < picked.Count; i++)
                destination[i] = picked[i].Kind == StopKind.Entry || picked[i].Kind == StopKind.Transfer ? -1 : count++;
            foreach (RouteStep step in Steps)
            {
                if (step.Role != StepRole.Transfer || step.Flyer.Kind != FlyerKind.Ship || step.Separates || step.Docks || step.Gone.Count > 0) continue;
                if (step.Goal >= picked.Count || destination[step.Goal] < 0 || Steps.Exists(other => other.Goal == step.Goal && other.Role == StepRole.Arrive)) continue;
                if (destination[step.Goal] == plan.FirstGoal && !plan.FirstGoalWhole) continue;
                double nodes = plan.DeltaVOf(destination[step.Goal]);
                double closed = step.Leg.DeltaV;
                if (nodes <= 0.0 || closed <= 0.0) continue;
                foreach (LegLine line in step.Leg.Lines) line.DeltaV *= nodes / closed;
            }
        }

        // Where the vessel is on its route: the steps to the stops it has reached are done and need nothing more, and the climb it is in the middle
        // of needs what is left of it. The closed form counts every step from its start, which is where the vessel stood on the pad: in flight it
        // told the vessel it lacked the fuel for a climb it had flown. What is left of the climb is the part of the orbital speed not yet reached.
        private void TakeFlightState()
        {
            if (Controls == null || Bodies == null) return;
            List<RouteStop> picked = PickedStops();
            var destination = new int[picked.Count];
            int count = 0;
            for (int i = 0; i < picked.Count; i++)
                destination[i] = picked[i].Kind == StopKind.Entry || picked[i].Kind == StopKind.Transfer ? -1 : count++;
            double speed = Controls.LaunchSpeed;
            int reached = Controls.Reached;
            // Up from the first stop, a surface, before the count has been made.
            if (reached < 0 && speed > 0.0 && picked.Count > 0 && picked[0].Kind == StopKind.Surface) reached = 0;
            if (reached < 0) return;
            foreach (RouteStep step in Steps)
            {
                if (step.Goal <= 0 || step.Goal >= picked.Count || destination[step.Goal] < 0) continue;
                int stop = destination[step.Goal];
                double share = 1.0;
                if (stop <= reached) share = 0.0;
                else if (step.Role == StepRole.Launch && stop == reached + 1 && speed > 0.0)
                {
                    RouteStop from = picked[step.Goal - 1];
                    if (from.Body < 0 || from.Body >= Bodies.Bodies.Count) continue;
                    PlannerBody body = Bodies.Bodies[from.Body];
                    double circular = Math.Sqrt(body.Mu / (body.Radius + body.LowOrbitAltitude));
                    share = Math.Max(0.0, Math.Min(1.0, 1.0 - speed / circular));
                }
                if (share >= 1.0) continue;
                foreach (LegLine line in step.Leg.Lines) line.DeltaV *= share;
            }
        }
    }
}
