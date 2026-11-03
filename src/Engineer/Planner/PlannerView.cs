using System.Collections.Generic;
using System;
using UnityEngine;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Ui;
using static ReDefinition.Engineer.PlanText;

namespace ReDefinition.Engineer
{
    // The planner (docs/player/planner.md) as KSP's dialog elements show it. The plan itself, with the steps, the sections, the stage budget and
    // every text, is the PlannerModel's. The view holds the model and builds the rows from it: what is open and what is folded, the bars, the
    // lists folded out under the buttons and the marks on the vessel. The rows are in PlannerView.Rows.cs and the files beside it.
    internal sealed partial class PlannerView
    {
        // The model the rows show, and the dashboard's access to it.
        private readonly PlannerModel model;
        private readonly PlannerDashboard dashboard;
        // The rows' width, and the parts' boxes around them, a margin wider on each side.
        private readonly float width;
        private readonly float outer;
        private readonly AfterCreate after = new AfterCreate();
        private readonly PartHighlight highlight = new PartHighlight();

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
            outer = width;
            this.width = width - 2f * BoxMargin;
            this.level = level < 0 ? PlannerPool.Full : level;
            model = new PlannerModel(host, this.level);
            pool = model.Pool;
            dashboard = new PlannerDashboard(model, () => Dropdown.Close(this));
            stepBars = new SegmentBar[pool.Stops, RouteStop.Roles];
            more = new bool[pool.Stops, RouteStop.Roles];
            barFillers = new Sizer[pool.Stops, RouteStop.Roles];
            bodyButtons = new DialogGUIBase[pool.Stops];
            fuelSwatches = new SegmentBar[pool.Steps, pool.Stages];
            model.Write = text => Debug.Log(text);
            model.Timer = PlannerTiming.Stop;
            model.Reloaded = OnReloaded;
            model.PlanArrived = OnPlanArrived;
            model.Evaluated = OnEvaluated;
            model.ListsStale = () => Dropdown.Close(this);
            model.StopsReshaped = OnStopsReshaped;
            model.Sections.GroupLit = Mark;
        }

        // The route the plan holds and the bodies it was made for; null before the plan is loaded.
        internal IList<RouteStop> RouteStops
        {
            get { return model.State != null ? model.State.Stops : null; }
        }

        internal PlannerBodies BodyList
        {
            get { return model.Bodies; }
        }

        // What the window gives the part "Guidance"; null in the editor, which has no flight to plan.
        internal IPlanControls Controls
        {
            get { return model.Controls; }
            set { model.Controls = value; }
        }

        // The sections that stay at the stop at this index and are let go there.
        internal List<SectionRelease> ReleasesAt(int stop)
        {
            return model.Sections.ReleasesAt(stop);
        }

        // As a press of the plus: for the probes, which have no row to press.
        internal void AddStopNow()
        {
            model.AddStop();
        }

        // Writes the plan to the file now, for the probes, which change the route behind the rows' backs.
        internal void SaveNow()
        {
            model.SaveNow();
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
            model.Release();
        }

        // The vessel's plan was loaded: what was open for the last plan's steps is folded.
        private void OnReloaded()
        {
            fuelOpen.Clear();
            thrustOpen.Clear();
            Array.Clear(more, 0, more.Length);
        }

        // The host holds another flight plan: its part opens to show it.
        private void OnPlanArrived()
        {
            planShown = true;
        }

        // A stop went: the steps are other ones now.
        private void OnStopsReshaped()
        {
            Array.Clear(more, 0, more.Length);
            fuelOpen.Clear();
        }

        // The plan was worked out: the bars follow, the marks on the vessel are made again, and the rows the route now needs are counted.
        private void OnEvaluated()
        {
            long t = PlannerTiming.Start();
            UpdateBars();
            Mark();
            PlannerTiming.Stop("Eval.bars", t);
            int mostStages = 0;
            for (int i = 0; i < model.Steps.Count; i++) mostStages = Math.Max(mostStages, FuelStageCount(i));
            requiredLevel = PlannerPool.Needed(model.State.Stops.Count, model.Steps.Count, mostStages);
        }

        // ---------------------------------------------------------------- update

        // Every frame the planner is open, or the dashboard asks for it: loads the vessel's plan where the vessel is another,
        // saves it once the delay has passed, and works it out again where something it rests on changed.
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
            if (input || inputLastTick) model.Touch();
            inputLastTick = input;
            model.Tick();
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
            model.SaveIfPending();
        }

        // Whether the player's click or key came in the last frame or this one; see TickWork.
        private bool inputLastTick;

        // One row's text and the text version it was made for.
        private sealed class CachedText
        {
            // -1 until it is first made.
            public int Version = -1;
            public string Text;
        }

        // The text `make` gives, made again only where the model's version has moved on.
        private Func<string> Text(Func<string> make)
        {
            CachedText cached = new CachedText();
            return () =>
            {
                if (cached.Version != model.Version)
                {
                    cached.Text = make();
                    cached.Version = model.Version;
                }
                return cached.Text;
            };
        }

        // The answer `ask` gives, asked again only where the model's version has moved on, as Text does for a text. KSP asks
        // a row's enabled condition in every frame, and these conditions work out strings and lists.
        private Func<bool> Cached(Func<bool> ask)
        {
            int version = -1;
            bool answer = false;
            return () =>
            {
                if (version != model.Version)
                {
                    answer = ask();
                    version = model.Version;
                }
                return answer;
            };
        }

        // ----------------------------------------------------------------- lists

        // The body buttons of the stops, for the list to fold out under.
        private readonly DialogGUIBase[] bodyButtons;

        // Folds the list of bodies out under the stop's body button.
        private void PickBody(int index)
        {
            if (!model.Shown(index)) return;
            Dropdown.Show(this, bodyButtons[index], model.BodyChoices(index), 150f);
        }

        // The rest's stages with fuel at that point, for the refill to come from first.
        private void PickTanker(int leg, StepRole role, DialogGUIBase button)
        {
            if (!model.Shown(leg + 1)) return;
            Dropdown.Show(this, button, model.TankerChoices(leg, role), 150f);
        }

        // Who can fly a step, for the list the step's button opens: the whole vessel,
        // each section alone, a lander left behind; or back to the planner's choice.
        private void PickFlyer(int leg, StepRole role, DialogGUIBase button)
        {
            if (model.Slot(leg, role) < 0) return;
            Dropdown.Show(this, button, model.Sections.FlyerChoices(leg, role), 150f);
        }

        // The parts that can be the section, each lit on the vessel while the pointer
        // is on it: with a command part, a lander's with engines too; none overlapping
        // another section.
        private void PickSectionParts(PlannedSection plan, DialogGUIBase button)
        {
            if (!model.Ready() || plan == null) return;
            Dropdown.Show(this, button, model.Sections.PartChoices(plan), PartButtonWidth);
        }

        // The kinds of section to add, for the list the "+" opens; the new section's
        // parts to pick right after.
        private void PickNewSection(DialogGUIBase button)
        {
            if (!model.Ready() || model.State.Sections.Count >= PlanSections.MaxSections) return;
            Dropdown.Show(this, button, model.Sections.NewSectionChoices(plan => PickSectionParts(plan, button)), 150f);
        }

        // Folds the satellite's destinations out under the button.
        private void PickDeployStop(int row, DialogGUIBase button)
        {
            if (model.Sections.RowSection(row) == null) return;
            Dropdown.Show(this, button, model.Sections.DeployChoices(row), PartButtonWidth);
        }

        // ---------------------------------------------------------------- marks

        // The colour of the parts a list's entry names.
        private static readonly Color Picked = new Color(0.56f, 0.86f, 0.48f);

        // The part group under the pointer in a section's list, its parts lit, or the vessel unmarked. Worked out again with the
        // plan and as the pointer enters or leaves an entry (the model tells); a list closing leaves its entry: the group by
        // its key, as the vessel's parts are read anew each time.
        private void Mark()
        {
            PlanSections sections = model.Sections;
            PartGroupChoice group = sections.LitGroup != null ? sections.Group(sections.LitGroup.Key) : null;
            if (group == null || !Dropdown.Open)
            {
                sections.LitGroup = null;
                highlight.Clear();
                return;
            }
            List<PartFacts> parts = sections.Parts;
            Dictionary<Part, Color> lit = new Dictionary<Part, Color>();
            foreach (int at in group.Members)
            {
                Part part = at < parts.Count ? parts[at].Tag as Part : null;
                if (part != null) lit[part] = Picked;
            }
            highlight.Show(lit);
        }
    }
}
