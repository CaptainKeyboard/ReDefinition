using System.Collections.Generic;
using System;
using UnityEngine;
using ReDefinition.Ui;

namespace ReDefinition.Engineer
{
    // The planner (docs/player/planner.md) holds a route from destination to
    // destination, A -> B -> C. It holds the steps between them with who flies each
    // (RouteSteps), and what each step costs. It holds which of the vessel's stages pays for
    // it, as KSP fires them or as the player saves or drops them on a step. This
    // part holds the plan and works it out. The vessel's sections are in
    // PlannerView.Sections.cs, the rows in PlannerView.Rows.cs.
    internal sealed partial class PlannerView
    {
        // Seconds after the last change the plan is saved, so that a run of clicks writes the file once; and the step the
        // altitude buttons take, in metres.
        private const float SaveDelay = 1.5f;
        private const double AltitudeStep = 10000.0;

        // The window the planner sits in, whose readout of the vessel the plan is worked out from.
        private readonly IPlannerHost host;
        // The rows' width, and the parts' boxes around them, a margin wider on each side.
        private readonly float width;
        private readonly float outer;
        private readonly AfterCreate after = new AfterCreate();

        // The system's bodies, read once; the vessel's plan; and the vessel's name the plan is kept under.
        private PlannerBodies bodies;
        private PlannerState state;
        private string vessel;
        // KSP loaded a vessel or started a new one: its own plan comes, not the
        // one on screen; a new one without the sections taken away from the last.
        private bool reload;
        private bool fresh;
        // Whether the plan is worked out again. That happens only where something it rests on changed:
        // the route or a setting in the planner (Changed), the vessel's plan loaded,
        // or the vessel as read anew. A new readout (EditorEngineer.Readout, read only where KSP
        // changed it) tells that, by no longer being the one last worked
        // out from.
        private bool dirty = true;
        private EditorReadout evaluated;
        private float saveAt = -1f;

        // What the plan comes to, worked out where it changed (dirty): the steps, and
        // for each leg (the way from one destination to the next) and role the step
        // there, -1 where there is none.
        private List<RouteStep> steps = new List<RouteStep>();
        private readonly int[,] slots;
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

        // How many rows are made, by PlannerPool's level, and the level the route needs: where it is above the one the rows are made for, the
        // window makes the planner again.
        private readonly int level;
        private readonly PlannerPool pool;
        private int requiredLevel;

        // The stops, steps and stages the rows are made for.
        private int MaxStops
        {
            get { return pool.Stops; }
        }

        private int MaxFuelSteps
        {
            get { return pool.Steps; }
        }

        private int MaxStepStages
        {
            get { return pool.Stages; }
        }

        internal int Level
        {
            get { return level; }
        }

        internal int RequiredLevel
        {
            get { return requiredLevel; }
        }

        // `width`: the room the panel gives the rows' boxes; `level`: how many rows are made (PlannerPool), the full size where none is given.
        internal PlannerView(IPlannerHost host, float width, int level = -1)
        {
            this.host = host;
            outer = width;
            this.width = width - 2f * BoxMargin;
            this.level = level < 0 ? PlannerPool.Full : level;
            pool = PlannerPool.Of(this.level);
            stepBars = new SegmentBar[pool.Stops, RouteStop.Roles];
            more = new bool[pool.Stops, RouteStop.Roles];
            barFillers = new Sizer[pool.Stops, RouteStop.Roles];
            slots = new int[pool.Stops, RouteStop.Roles];
            bodyButtons = new DialogGUIBase[pool.Stops];
            fuelSwatches = new SegmentBar[pool.Steps, pool.Stages];
            host.WatchVessel(OnVesselChanged, OnVesselFresh);
        }

        // The route the plan holds and the bodies it was made for; null before the plan is loaded.
        internal IList<RouteStop> RouteStops
        {
            get { return state != null ? state.Stops : null; }
        }

        internal PlannerBodies BodyList
        {
            get { return bodies; }
        }

        // Once the rows are created: symbols on the buttons, the bars' images.
        internal void Finish()
        {
            after.Run();
        }

        // The planner goes with its window: the plan saved where it waits for it, the host's events let go.
        internal void Dispose()
        {
            Flush();
            host.StopWatching();
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

        // ---------------------------------------------------------------- update

        // Every frame the planner is open, or the dashboard asks for it: loads the vessel's plan where the vessel is another,
        // saves it once SaveDelay has passed, and works it out again where something it rests on changed.
        internal void Tick()
        {
            long tick = PlannerTiming.Start();
            TickWork();
            PlannerTiming.Stop("view.Tick", tick);
        }

        private void TickWork()
        {
            // In this frame and the next: the button a click releases may answer after the
            // labels took their text in this one, in whichever order Unity runs the two.
            bool input = Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(1) || Input.anyKeyDown;
            if (input || inputLastTick) textVersion++;
            inputLastTick = input;
            string name = host.VesselName();
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
                dirty = true;
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
            NoticePlan();
            if (!dirty && host.Readout == evaluated) return;
            dirty = false;
            evaluated = host.Readout;
            Evaluate();
        }

        // The editor's keys held while an altitude is typed, by this name; and whether they are held now.
        private const string TypingLock = "ReDefinitionPlannerTyping";
        private bool typingLocked;

        // Every frame while the planner is open. While an altitude is typed, the
        // editor's keys are held, so that the digits do not reach its shortcuts.
        internal void Keep(GameObject panel)
        {
            bool typing = Typing();
            if (typing != typingLocked)
            {
                typingLocked = typing;
                if (typing) InputLockManager.SetControlLock(ControlTypes.All, TypingLock);
                else
                {
                    InputLockManager.RemoveControlLock(TypingLock);
                    // The field is still selected: Space or Return would open it
                    // again.
                    UiHygiene.Release(panel);
                }
            }
            highlight.Refresh();
            ShapeHeaders();
        }

        // The altitude fields' inputs, looked up once they exist.
        private TMPro.TMP_InputField[] inputs;

        // Whether one of the altitude fields has the keyboard.
        private bool Typing()
        {
            if (inputs == null || inputs.Length != altitudeFields.Count)
            {
                TMPro.TMP_InputField[] found = new TMPro.TMP_InputField[altitudeFields.Count];
                for (int i = 0; i < found.Length; i++)
                {
                    DialogGUITextInput field = altitudeFields[i];
                    found[i] = field != null && field.uiItem != null ? field.uiItem.GetComponent<TMPro.TMP_InputField>() : null;
                    if (found[i] == null) return false;
                }
                inputs = found;
            }
            foreach (TMPro.TMP_InputField input in inputs)
                if (input != null && input.isFocused) return true;
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

        // A counter that rises when something the rows show may have changed. The rows' texts, which
        // KSP's labels ask for in every frame they show (DialogGUILabel.Update), are worked out again
        // only then, and the string is kept until then. A change is the plan worked out anew, a change
        // of the route or a setting, or the player's click or key in the editor, which opens and folds rows.
        private int textVersion;
        private bool inputLastTick;

        // One row's text and the text version it was made for.
        private sealed class CachedText
        {
            // -1 until it is first made.
            public int Version = -1;
            public string Text;
        }

        // The text `make` gives, made again only where textVersion has moved on.
        private Func<string> Text(Func<string> make)
        {
            CachedText cached = new CachedText();
            return () =>
            {
                if (cached.Version != textVersion)
                {
                    cached.Text = make();
                    cached.Version = textVersion;
                }
                return cached.Text;
            };
        }

        // The answer `ask` gives, asked again only where textVersion has moved on, as Text does for a text. KSP asks
        // a row's enabled condition in every frame, and these conditions work out strings and lists.
        private Func<bool> Cached(Func<bool> ask)
        {
            int version = -1;
            bool answer = false;
            return () =>
            {
                if (version != textVersion)
                {
                    answer = ask();
                    version = textVersion;
                }
                return answer;
            };
        }

        // Works the plan out: the stages the planner budgets with, the vessel's sections, the steps, which stage pays for
        // which step, the thrust checks, each step's warnings, the bars and the marks on the vessel.
        private void Evaluate()
        {
            long whole = PlannerTiming.Start();
            long t = whole;
            textVersion++;
            EditorReadout readout = host.Readout ?? new EditorReadout();
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
            // A lift-off only where the route starts on a surface: from an orbit, a
            // weak first stage (an ion engine before a Spark) is not held on a pad.
            if (launch != null)
                allStages = ClampedLaunch.Parallel(allStages, readout.PartFacts, stageLabels, LaunchGravity(), LaunchBody());
            stages = allStages.FindAll(row => row.DeltaVVacuum > 0.5);

            PlannerTiming.Stop("Eval.stages", t);
            t = PlannerTiming.Start();
            Detect(readout.PartFacts);
            PlannerTiming.Stop("Eval.detect", t);
            t = PlannerTiming.Start();
            steps = RouteSteps.Build(bodies.Bodies, PickedStops(), RouteSections(), true);
            PlannerTiming.Stop("Eval.steps", t);
            t = PlannerTiming.Start();
            TakeFlightPlan();
            TakeFlightState();
            PlannerTiming.Stop("Eval.takePlan", t);
            t = PlannerTiming.Start();
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
            PlannerTiming.Stop("Eval.budget", t);
            t = PlannerTiming.Start();
            UpdateThrust();
            PlannerTiming.Stop("Eval.thrust", t);
            t = PlannerTiming.Start();
            warnings = new List<string>[steps.Count];
            for (int i = 0; i < steps.Count; i++) warnings[i] = StepWarnings(i);
            PlannerTiming.Stop("Eval.warnings", t);
            t = PlannerTiming.Start();
            UpdateBars();
            Mark();
            PlannerTiming.Stop("Eval.bars", t);
            int mostStages = 0;
            for (int i = 0; i < steps.Count; i++) mostStages = System.Math.Max(mostStages, FuelStageCount(i, RowScope.Shown));
            requiredLevel = PlannerPool.Needed(state.Stops.Count, steps.Count, mostStages);
            PlannerTiming.Stop("Eval", whole);
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

        // Whether the bodies are read and the plan loaded.
        private bool Ready()
        {
            return state != null && bodies != null;
        }

        // Whether the plan is worked out for the steps there are now.
        private bool Worked()
        {
            return Ready() && budget != null && budget.Needed.Length == steps.Count;
        }

        // The index in `steps` of the leg's step in that role; -1 where it has none or the plan is not worked out.
        private int Slot(int leg, StepRole role)
        {
            return Worked() && leg >= 0 && leg < MaxStops ? slots[leg, (int)role] : -1;
        }

        // The plan changed: the texts are made again, the plan is saved after SaveDelay and worked out again.
        // Writes the plan to the file now, for the probes, which change the route behind the rows' backs.
        internal void SaveNow()
        {
            // The sections let go at the stops are worked out with the plan: a change not yet worked out would be saved without them.
            if (dirty && Ready())
            {
                dirty = false;
                evaluated = host.Readout;
                Evaluate();
            }
            if (state != null && vessel != null && bodies != null) state.Save(vessel, bodies);
        }

        private void Changed()
        {
            textVersion++;
            saveAt = Time.unscaledTime + SaveDelay;
            dirty = true;
        }

        // ----------------------------------------------------------------- stops

        // Whether the route has a stop at `index`.
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

        // Whether the stop at `index` has a body picked.
        private bool HasBody(int index)
        {
            return Shown(index) && state.Stops[index].Body >= 0 && state.Stops[index].Body < bodies.Bodies.Count;
        }

        // The stop's letter in the route: A for the first.
        private static string Letter(int index)
        {
            return ((char)('A' + index)).ToString();
        }

        // The stop's body's name, or "Destination" as a problem where none is picked.
        private string BodyName(int index)
        {
            if (!Shown(index)) return "";
            int body = state.Stops[index].Body;
            return body >= 0 && body < bodies.Bodies.Count ? bodies.Bodies[body].Name : "<color=" + Problem + ">Destination</color>";
        }

        // The stop's kind; an orbit where there is no such stop.
        private StopKind Kind(int index)
        {
            return Shown(index) ? state.Stops[index].Kind : StopKind.Orbit;
        }

        // Whether the stop's body has air.
        private bool HasAtmosphere(int index)
        {
            return HasBody(index) && bodies.Bodies[state.Stops[index].Body].HasAtmosphere;
        }

        // Whether arriving at the stop uses its body's air.
        private bool UsesAtmosphere(int index)
        {
            return Shown(index) && state.Stops[index].UseAtmosphere && HasAtmosphere(index);
        }

        // The stop's altitude in whole kilometres, as the field shows it.
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
        private readonly DialogGUIBase[] bodyButtons;

        // Folds the list of bodies out under the stop's body button.
        private void PickBody(int index)
        {
            if (!Shown(index)) return;
            Dropdown.Show(this, bodyButtons[index], BodyChoices(index), 150f);
        }

        // The bodies, each moon under its planet.
        private List<Choice> BodyChoices(int index)
        {
            List<Choice> choices = new List<Choice>();
            if (!Shown(index)) return choices;
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
            return choices;
        }

        // The stop goes to `body`, at its low orbit; without air, the way down does not use it.
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

        // The stop becomes a surface or an orbit; an orbit with no altitude takes the body's low orbit.
        private void SetKind(int index, StopKind kind)
        {
            if (!Shown(index) || state.Stops[index].Kind == kind) return;
            state.Stops[index].Kind = kind;
            if (kind == StopKind.Orbit && state.Stops[index].Altitude <= 0.0 && HasBody(index))
                state.Stops[index].Altitude = bodies.Bodies[state.Stops[index].Body].LowOrbitAltitude;
            AutoChutes(index);
            Changed();
        }

        // One AltitudeStep up (1) or down (-1), kept between the low orbit and the edge of the sphere of influence.
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

        // Arriving at the stop uses its body's air, or no longer does.
        private void ToggleAtmosphere(int index)
        {
            if (!Shown(index) || !HasAtmosphere(index)) return;
            state.Stops[index].UseAtmosphere = !state.Stops[index].UseAtmosphere;
            Changed();
        }

        // As a press of the plus: for the probes, which have no row to press.
        internal void AddStopNow()
        {
            AddStop();
        }

        // A new stop where the route ends: the same body, in its low orbit.
        private void AddStop()
        {
            if (!Ready() || state.Stops.Count >= HardStops) return;
            RouteStop last = state.Stops[state.Stops.Count - 1];
            if (last.Body < 0) return;
            state.Stops.Add(new RouteStop
            {
                Body = last.Body, Kind = StopKind.Orbit, Altitude = bodies.Bodies[last.Body].LowOrbitAltitude,
            });
            Changed();
        }

        // Takes the stop out of the route, keeping two at least.
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

        // Its parts lit, or the vessel unmarked. Worked out again with the plan and
        // as the pointer enters or leaves an entry (ShowGroup; a list closing
        // leaves its entry): the group by its key, as the vessel's parts are read
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

        // Whether the player saved the stage on the step.
        private bool Saved(int step, int number)
        {
            StagePlan plan = PlanOf(step);
            return plan != null && plan.Save.Contains(number);
        }

        // Whether the player drops the stage after the step.
        private bool DropsAfter(int step, int number)
        {
            StagePlan plan = PlanOf(step);
            return plan != null && plan.DropAfter.Contains(number);
        }

        // Saves the stage on the step, or no longer does.
        private void ToggleSaved(int step, int number)
        {
            StagePlan plan = PlanOf(step);
            if (plan == null || number < 0) return;
            if (!plan.Save.Remove(number)) plan.Save.Add(number);
            Changed();
        }

        // Drops the stage after the step, or no longer does.
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
            Dropdown.Show(this, button, TankerChoices(leg, role), 150f);
        }

        // The list a refill's first source is picked from: the rest in KSP's order, then each of the rest's stages with fuel.
        private List<Choice> TankerChoices(int leg, StepRole role)
        {
            if (!Shown(leg + 1)) return new List<Choice>();
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
            return choices;
        }

        // The refill on the leg's step in that role comes first from `stage`, -1 for the rest in KSP's order.
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
            StageBudget.StageUse first = FirstUse(index);
            StageBudget.StageUse lastUsed = LastUse(index);
            double liftOff = first != null ? TwrAt(first.Stage, first.Mass, step.From.Body, true) : 0.0;
            bool runway = first != null && Runway(first.Stage);
            if (launch && first != null && !runway && liftOff < TwrRequired(true, false, step.From.Body))
                list.Add("TWR " + EngineerFormat.Ratio(liftOff) + " of " + StageLabel(first.Stage) + " is too low to lift off " + RouteSteps.At(bodies.Bodies, step.From).Substring(3));
            double setDown = lastUsed != null ? TwrAt(lastUsed.Stage, lastUsed.Mass, step.To.Body, true) : 0.0;
            if (landing && lastUsed != null && setDown < TwrRequired(false, false, step.To.Body))
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

        // The section docking at the end of the step is refilled from the rest, or no longer is.
        private void ToggleRefuel(int leg, StepRole role)
        {
            if (!Shown(leg + 1)) return;
            bool[] refuel = state.Stops[leg + 1].Refuel;
            refuel[(int)role] = !refuel[(int)role];
            Changed();
        }

        // Whether the way across to the stop is two steps, the burn that leaves and the one that arrives.
        private bool SplitsBurns(int index)
        {
            return Shown(index) && state.Stops[index].SplitBurns;
        }

        // Splits the way across to the stop into two steps, or joins them.
        private void SetSplitBurns(int index, bool split)
        {
            if (!Shown(index) || state.Stops[index].SplitBurns == split) return;
            state.Stops[index].SplitBurns = split;
            Changed();
        }

        // Whether the way down to the stop goes into an orbit first rather than straight into the air.
        private bool OrbitFirst(int index)
        {
            return Shown(index) && state.Stops[index].OrbitFirst;
        }

        // Has the way down to the stop go into an orbit first, or straight into the air.
        private void SetOrbitFirst(int index, bool orbit)
        {
            if (!Shown(index) || state.Stops[index].OrbitFirst == orbit) return;
            state.Stops[index].OrbitFirst = orbit;
            Changed();
        }

        // The margin, from the slider's per cent.
        private void SetMargin(float percent)
        {
            double margin = Math.Round(percent) / 100.0;
            if (!Ready() || Math.Abs(margin - state.Margin) < 1e-6) return;
            state.Margin = margin;
            Changed();
        }

        // What the route's steps need, with the margin, in m/s.
        private double TotalNeeded()
        {
            double needed = 0.0;
            if (budget != null) foreach (double value in budget.Needed) needed += value;
            return needed;
        }

        // What all stages with delta-v have in vacuum, in m/s.
        private double TotalHas()
        {
            double has = 0.0;
            foreach (StageRow row in stages) has += row.DeltaVVacuum;
            return has;
        }
    }
}
