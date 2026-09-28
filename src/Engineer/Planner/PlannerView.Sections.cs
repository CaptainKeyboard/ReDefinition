using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The planner's sections (docs/player/planner.md): the parts of the vessel the
    // player adds as a capsule, a lander or a satellite, and the capsule of a crewed
    // vessel and the one satellite of an uncrewed one that the planner adds by itself.
    // This part resolves them against the vessel's parts, names them, hands them to
    // the route and the budget, and holds what the rows ask of them.
    internal sealed partial class PlannerView
    {
        private const int MaxSections = 6;

        // A section as the plan works with it now.
        private sealed class LiveSection
        {
            public PlannedSection Plan;
            // Its parts, null where none are picked or the vessel does not have them.
            public PartGroupChoice Group;
            public string Name = "";
            // It overlaps a section before it, so it counts for nothing.
            public string Problem;
            public int DeployAt = -1;
            public bool Docks;

            public bool Valid
            {
                get { return Group != null && Problem == null; }
            }
        }

        private List<PartFacts> parts = new List<PartFacts>();
        private List<PartGroupChoice> groups = new List<PartGroupChoice>();
        private readonly List<LiveSection> live = new List<LiveSection>();
        // The valid sections, in the order of their budget groups (group i + 1).
        private readonly List<LiveSection> valid = new List<LiveSection>();
        // Per part, the budget group it falls in.
        private int[] partGroups = new int[0];
        private PartGroupChoice detectedCapsule;
        private PartGroupChoice detectedSatellite;
        private PartGroupChoice detectedLander;
        // The capsule, the satellite and the lander the planner finds, shown while it
        // finds them and kept in the plan only once the player sets something on them.
        private PlannedSection foundCapsule;
        private PlannedSection foundSatellite;
        private PlannedSection foundLander;
        // What the log last said was found, so that it says it once.
        private string detectedLogged;
        // The groups' keys and sizes as last read.
        private string groupKeys;

        // ------------------------------------------------------------ resolving

        // The sections against the vessel's parts: the planner's own added where the
        // vessel calls for them, each with its parts, its name, and what it does.
        private void Detect(List<PartFacts> facts)
        {
            parts = facts;
            groups = VesselShape.Candidates(facts);
            // The vessel changed under an open list, as with undo: its entries and
            // their parts are no longer the vessel's.
            string keys = string.Join(" ", groups.ConvertAll(group => group.Key + "=" + group.Members.Count).ToArray());
            if (keys != groupKeys)
            {
                if (groupKeys != null) Dropdown.Close(this);
                groupKeys = keys;
            }
            bool crewed = VesselShape.Crewed(facts);
            detectedCapsule = crewed ? VesselShape.AutoCapsule(groups) : null;
            detectedSatellite = crewed ? null : VesselShape.AutoSatellite(facts, groups);
            detectedLander = VesselShape.AutoLander(facts, groups, detectedCapsule, detectedSatellite);
            List<PlannedSection> plans = new List<PlannedSection>(state.Sections);
            Found(plans, SectionKind.Capsule, detectedCapsule, ref foundCapsule, PlannerState.FoundCapsule);
            Found(plans, SectionKind.Satellite, detectedSatellite, ref foundSatellite, PlannerState.FoundSatellite);
            Found(plans, SectionKind.Lander, detectedLander, ref foundLander, PlannerState.FoundLander);
            // Capsules first, then the others as the player added them; what the planner
            // found first of each.
            List<PlannedSection> ordered = new List<PlannedSection>();
            foreach (bool capsules in new[] { true, false })
                foreach (bool planners in new[] { true, false })
                    foreach (PlannedSection plan in plans)
                        if ((plan.Kind == SectionKind.Capsule) == capsules && (plan.Id >= PlannerState.FoundCapsule) == planners)
                            ordered.Add(plan);
            plans = ordered;

            live.Clear();
            valid.Clear();
            foreach (PlannedSection plan in plans) live.Add(new LiveSection { Plan = plan });
            foreach (LiveSection section in live)
            {
                List<LiveSection> same = live.FindAll(other => other.Plan.Kind == section.Plan.Kind);
                section.Name = section.Plan.Kind + (same.Count > 1 ? " " + (same.IndexOf(section) + 1) : "");
            }
            foreach (LiveSection section in live)
            {
                PlannedSection plan = section.Plan;
                section.Group = plan.Group == PlannerState.Automatic ? Detected(plan.Kind) : groups.Find(group => group.Key == plan.Group);
                foreach (LiveSection before in valid)
                    if (section.Group != null && !VesselShape.Fits(section.Group, before.Group))
                        section.Problem = "Overlaps the " + Lower(before.Name);
                if (section.Valid) valid.Add(section);
            }
            foreach (LiveSection section in live)
            {
                section.DeployAt = DeployStop(section.Plan);
                // A lander docks again by itself where it can, with docking ports on it
                // and on the rest; flying on alone is the player's choice.
                section.Docks = section.Plan.Kind == SectionKind.Lander && VesselShape.CanDock(facts, section.Group);
            }

            partGroups = new int[facts.Count];
            for (int i = 0; i < facts.Count; i++)
            {
                int best = -1;
                for (int s = 0; s < valid.Count; s++)
                    if (valid[s].Group.Members.Contains(i) && (best < 0 || valid[s].Group.Members.Count < valid[best].Group.Members.Count))
                        best = s;
                partGroups[i] = best + 1;
            }

            string found = "capsule " + GroupName(detectedCapsule) + ", satellite " + GroupName(detectedSatellite) + ", lander "
                           + GroupName(detectedLander) + ", "
                           + (crewed ? "crewed" : "uncrewed") + ", " + groups.Count + " separable groups, " + valid.Count + " sections";
            if (facts.Count > 0 && found != detectedLogged)
            {
                detectedLogged = found;
                Debug.Log(Core.Log.Tag + " Planner found from " + facts.Count + " parts: " + found + ".");
            }
        }

        // A capsule, satellite or lander the planner found is a section while it finds
        // it: unless the plan has one of that kind or one it overlaps, the player took
        // that kind away, or the list is full. It goes into the plan only once the player sets something
        // on it (Keep).
        private void Found(List<PlannedSection> plans, SectionKind kind, PartGroupChoice found, ref PlannedSection section, int id)
        {
            if (found == null || state.Dismissed.Contains(kind) || plans.Count >= MaxSections
                || plans.Exists(plan => plan.Kind == kind))
                return;
            // A guess gives way to the sections the plan has.
            foreach (PlannedSection plan in plans)
            {
                PartGroupChoice group = plan.Group == PlannerState.Automatic ? Detected(plan.Kind) : Group(plan.Group);
                if (group != null && !VesselShape.Fits(found, group)) return;
            }
            if (section == null) section = new PlannedSection { Id = id, Kind = kind, Group = PlannerState.Automatic };
            plans.Add(section);
        }

        // The planner's own section becomes the plan's as the player sets something on
        // it.
        private void Keep(PlannedSection plan)
        {
            state.Keep(plan);
            Changed();
        }

        private PartGroupChoice Detected(SectionKind kind)
        {
            switch (kind)
            {
                case SectionKind.Capsule: return detectedCapsule;
                case SectionKind.Satellite: return detectedSatellite;
                case SectionKind.Lander: return detectedLander;
                default: return null;
            }
        }

        // Where a satellite stays: the player's destination, or the first orbit the
        // route reaches before it lands or ends.
        private int DeployStop(PlannedSection plan)
        {
            if (plan.Kind != SectionKind.Satellite) return -1;
            if (plan.DeployAt >= 1 && plan.DeployAt < state.Stops.Count) return plan.DeployAt;
            return AutoDeployStop();
        }

        private int AutoDeployStop()
        {
            for (int i = 1; i < state.Stops.Count; i++)
                if (state.Stops[i].Kind == StopKind.Orbit && (i == state.Stops.Count - 1 || state.Stops[i + 1].Kind == StopKind.Surface))
                    return i;
            return -1;
        }

        private PartGroupChoice Group(string key)
        {
            return groups.Find(group => group.Key == key);
        }

        private static string GroupName(PartGroupChoice group)
        {
            return group != null ? group.Name : "none";
        }

        private static string Lower(string name)
        {
            return string.IsNullOrEmpty(name) ? "" : char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        private bool HasSections()
        {
            return Ready() && valid.Count > 0;
        }

        private LiveSection SectionById(int id)
        {
            return live.Find(section => section.Plan.Id == id);
        }

        // ----------------------------------------------------- route and budget

        // The valid sections as the route needs them.
        private List<RouteSection> RouteSections()
        {
            List<RouteSection> list = new List<RouteSection>();
            List<PartGroupChoice> chosen = valid.ConvertAll(section => section.Group);
            for (int i = 0; i < valid.Count; i++)
            {
                int parent = VesselShape.Parent(chosen, i);
                list.Add(new RouteSection
                {
                    Id = valid[i].Plan.Id, Kind = valid[i].Plan.Kind, Name = valid[i].Name,
                    Parent = parent >= 0 ? valid[parent].Plan.Id : 0, Docks = valid[i].Docks, DeployAt = valid[i].DeployAt,
                });
            }
            return list;
        }

        // A section's budget group: its place among the valid sections, plus one; 0
        // for the rest.
        private int BudgetGroup(int id)
        {
            if (id == 0) return 0;
            int at = valid.FindIndex(section => section.Plan.Id == id);
            return at >= 0 ? at + 1 : -1;
        }

        private List<BudgetStep> BudgetSteps()
        {
            List<BudgetStep> list = new List<BudgetStep>();
            foreach (RouteStep step in steps)
            {
                BudgetStep budgetStep = new BudgetStep { Need = step.Leg.DeltaV, Side = new bool[valid.Count + 1] };
                foreach (int id in step.Flying)
                {
                    int group = BudgetGroup(id);
                    if (group >= 0) budgetStep.Side[group] = true;
                }
                foreach (GoneSection gone in step.Gone)
                {
                    int group = BudgetGroup(gone.Section);
                    if (group >= 0) budgetStep.Gone.Add(new GoneGroup { Group = group, Stays = gone.Stays, Waiting = gone.Waiting });
                }
                if (step.RefuelAfter && step.Flyer.Kind == FlyerKind.Alone)
                {
                    budgetStep.RefuelGroup = BudgetGroup(step.Flyer.Section);
                    budgetStep.RefuelFrom = state.Stops[step.Goal].RefuelFrom[(int)step.Role];
                }
                foreach (int id in step.Deploys)
                {
                    int group = BudgetGroup(id);
                    if (group > 0) budgetStep.Deploy.Add(group);
                }
                list.Add(budgetStep);
            }
            return list;
        }

        // A part of that kind on what flies a step.
        private bool Carries(System.Func<PartFacts, bool> kind, RouteStep step)
        {
            for (int i = 0; i < parts.Count && i < partGroups.Length; i++)
            {
                if (!kind(parts[i])) continue;
                int group = partGroups[i];
                int id = group == 0 ? 0 : valid[group - 1].Plan.Id;
                if (step.Flying.Contains(id)) return true;
            }
            return false;
        }

        // A part of that kind on the first capsule, or on the vessel where it has none.
        private bool CapsuleCarries(System.Func<PartFacts, bool> kind)
        {
            LiveSection capsule = valid.Find(section => section.Plan.Kind == SectionKind.Capsule);
            for (int i = 0; i < parts.Count; i++)
                if (kind(parts[i]) && (capsule == null || capsule.Group.Members.Contains(i))) return true;
            return false;
        }

        // ------------------------------------------------------------ who flies

        // "Ship", "Lander", "Without lander".
        private string FlyerName(Flyer flyer)
        {
            if (flyer.Kind == FlyerKind.Ship) return "Ship";
            LiveSection section = SectionById(flyer.Section);
            string name = section != null ? section.Name : "Section";
            return flyer.Kind == FlyerKind.Without ? "Without " + Lower(name) : name;
        }

        // Who can fly a step, for the list the step's button opens: the whole vessel,
        // each section alone, a lander left behind; or back to the planner's choice.
        private void PickFlyer(int leg, StepRole role, DialogGUIBase button)
        {
            int index = Slot(leg, role);
            if (index < 0) return;
            RouteStep step = steps[index];
            List<Choice> choices = new List<Choice>();
            AddFlyer(choices, leg, role, step, Flyer.Ship, "The whole vessel flies this step.");
            foreach (LiveSection section in valid)
            {
                AddFlyer(choices, leg, role, step, Flyer.Alone(section.Plan.Id), FlyTooltip(section.Plan.Kind));
                if (section.Plan.Kind == SectionKind.Lander)
                    AddFlyer(choices, leg, role, step, Flyer.Without(section.Plan.Id),
                        "The vessel flies on without the " + Lower(section.Name) + ", which stays where it is for good.");
            }
            if (step.Chosen)
                choices.Add(new Choice
                {
                    Label = "Planner's choice", Tooltip = "Let the planner choose who flies this step again.",
                    Pick = () => ChooseFlyer(leg, role, step.Flyer),
                });
            Dropdown.Show(this, button, choices, 150f);
        }

        private static string FlyTooltip(SectionKind kind)
        {
            switch (kind)
            {
                case SectionKind.Capsule: return "The capsule flies this step alone and leaves the rest of the vessel behind.";
                case SectionKind.Satellite: return "The satellite flies this step alone and stays where it ends.";
                default: return "The lander flies this step alone while the rest waits.";
            }
        }

        private void AddFlyer(List<Choice> choices, int leg, StepRole role, RouteStep step, Flyer flyer, string tooltip)
        {
            choices.Add(new Choice
            {
                Label = FlyerName(flyer), Tooltip = tooltip, Current = step.Flyer.Code == flyer.Code,
                Pick = () =>
                {
                    int index = Slot(leg, role);
                    if (index >= 0 && steps[index].Chosen && steps[index].Flyer.Code == flyer.Code) return;
                    ChooseFlyer(leg, role, flyer);
                },
            });
        }

        // The player's choice of who flies a step; the same choice again gives it
        // back to the planner.
        private void ChooseFlyer(int leg, StepRole role, Flyer flyer)
        {
            int index = Slot(leg, role);
            if (index < 0 || !Shown(leg + 1)) return;
            int[] by = state.Stops[leg + 1].By;
            by[(int)role] = steps[index].Chosen && steps[index].Flyer.Code == flyer.Code ? -1 : flyer.Code;
            Changed();
        }

        // ------------------------------------------------------------ the rows

        private LiveSection RowSection(int row)
        {
            return Ready() && row < live.Count ? live[row] : null;
        }

        // "Automatic (Mk1-3 Command Pod, 4 parts)", "Mk2 Lander Can, 9 parts": the
        // section's button; the player's own pick bold and underlined.
        private string SectionText(int row)
        {
            LiveSection section = RowSection(row);
            if (section == null) return "";
            if (section.Plan.Group == PlannerState.Automatic)
                return "<color=" + Good + ">Automatic</color> <color=" + Dim + ">(" + GroupName(section.Group) + ")</color>";
            if (string.IsNullOrEmpty(section.Plan.Group)) return "<color=" + Warning + ">Pick its parts</color>";
            if (section.Group == null) return "<color=" + Dim + ">Not on the vessel</color>";
            return "<b><u><color=" + Good + ">" + section.Group.Name + "</color></u></b>";
        }

        private string SectionLabel(int row)
        {
            LiveSection section = RowSection(row);
            return section == null ? "" : "<color=" + Dim + ">" + section.Name + "</color>";
        }

        // What a section's line under it says: it overlaps another, or a capsule
        // without parachutes.
        private string SectionWarning(int row)
        {
            LiveSection section = RowSection(row);
            if (section == null || section.Group == null) return "";
            if (section.Problem != null) return section.Problem;
            if (section.Plan.Kind == SectionKind.Capsule && !section.Group.Parachute) return "No parachutes on the capsule";
            return "";
        }

        // The parts that can be the section, each lit on the vessel while the pointer
        // is on it: with a command part, a lander's with engines too; none overlapping
        // another section.
        private void PickSectionParts(PlannedSection plan, DialogGUIBase button)
        {
            if (!Ready() || plan == null) return;
            List<Choice> choices = new List<Choice>();
            PartGroupChoice found = Detected(plan.Kind);
            AddPart(choices, "Automatic", AutomaticTooltip(plan.Kind), plan.Group == PlannerState.Automatic,
                () => SetSectionGroup(plan, PlannerState.Automatic), found);
            foreach (PartGroupChoice group in groups)
            {
                if (!group.Command || (plan.Kind == SectionKind.Lander && !group.Engine)) continue;
                if (valid.Exists(other => other.Plan != plan && !VesselShape.Fits(group, other.Group))) continue;
                string key = group.Key;
                AddPart(choices, group.Name, PartsTooltip(plan.Kind), plan.Group == key, () => SetSectionGroup(plan, key), group);
            }
            // Nothing that fits: the list says so rather than not opening.
            if (choices.Count == 1 && found == null)
                choices.Add(new Choice
                {
                    Label = "<color=" + Dim + ">No separable part fits</color>",
                    Tooltip = plan.Kind == SectionKind.Lander
                        ? "A lander needs a command part and engines that a decoupler or docking port separates."
                        : "A section needs a command part that a decoupler or docking port separates.",
                    Pick = () => { },
                });
            Dropdown.Show(this, button, choices, PartButtonWidth);
        }

        private static string AutomaticTooltip(SectionKind kind)
        {
            switch (kind)
            {
                case SectionKind.Capsule: return "The smallest part with a crewed command part.";
                case SectionKind.Satellite: return "The one part of an uncrewed vessel with a command part that leaves the launch stage.";
                default: return "The smallest part with a command part, engines and landing legs whose engines do not fire at launch.";
            }
        }

        private static string PartsTooltip(SectionKind kind)
        {
            switch (kind)
            {
                case SectionKind.Capsule: return "These parts come home alone at the end.";
                case SectionKind.Satellite: return "These parts stay at their destination.";
                default: return "These parts land as the lander.";
            }
        }

        private void AddPart(List<Choice> choices, string label, string tooltip, bool current, Callback pick, PartGroupChoice group)
        {
            choices.Add(new Choice
            {
                Label = label, Tooltip = tooltip, Current = current, Pick = pick,
                Hover = on => ShowGroup(on ? group : null),
            });
        }

        private void SetSectionGroup(PlannedSection plan, string key)
        {
            if (plan.Group == key) return;
            plan.Group = key;
            Keep(plan);
        }

        // The kinds of section to add, for the list the "+" opens; the new section's
        // parts to pick right after.
        private void PickNewSection(DialogGUIBase button)
        {
            if (!Ready() || state.Sections.Count >= MaxSections) return;
            List<Choice> choices = new List<Choice>();
            AddKind(choices, SectionKind.Capsule, "A part that comes home alone at the end, usually on its parachutes.", button);
            AddKind(choices, SectionKind.Lander, "A part that lands while the rest waits.", button);
            AddKind(choices, SectionKind.Satellite, "A part that stays at a destination.", button);
            Dropdown.Show(this, button, choices, 150f);
        }

        private void AddKind(List<Choice> choices, SectionKind kind, string tooltip, DialogGUIBase button)
        {
            choices.Add(new Choice
            {
                Label = kind.ToString(), Tooltip = tooltip,
                Pick = () =>
                {
                    PlannedSection plan = state.AddSection(kind, "");
                    Changed();
                    PickSectionParts(plan, button);
                },
            });
        }

        private void RemoveSection(int row)
        {
            LiveSection section = RowSection(row);
            if (section == null) return;
            Dropdown.Close(this);
            if (state.Sections.Contains(section.Plan)) state.RemoveSection(section.Plan);
            else state.Dismissed.Add(section.Plan.Kind);
            // A found section comes back new, not with what was set on it.
            if (section.Plan == foundCapsule) foundCapsule = null;
            if (section.Plan == foundSatellite) foundSatellite = null;
            if (section.Plan == foundLander) foundLander = null;
            Changed();
        }

        private bool IsKind(int row, SectionKind kind)
        {
            LiveSection section = RowSection(row);
            return section != null && section.Valid && section.Plan.Kind == kind;
        }

        // "C  Kerbin orbit": where a satellite stays; the planner's choice plain, the
        // player's bold and underlined.
        private string DeployText(int row)
        {
            LiveSection section = RowSection(row);
            if (section == null) return "";
            string place = section.DeployAt >= 1 ? StopText(section.DeployAt) : "no orbit on the route";
            return section.Plan.DeployAt >= 1 && section.Plan.DeployAt < state.Stops.Count
                ? "<b><u><color=" + Good + ">" + place + "</color></u></b>"
                : "<color=" + Good + ">Automatic</color> <color=" + Dim + ">(" + place + ")</color>";
        }

        // "C  Kerbin orbit, 2,863 km".
        private string StopText(int index)
        {
            RouteStop stop = state.Stops[index];
            string text = Letter(index) + "  " + RouteSteps.Place(bodies.Bodies, stop);
            if (stop.Kind == StopKind.Orbit) text += ", " + AltitudeKm(index) + " km";
            return text;
        }

        private void PickDeployStop(int row, DialogGUIBase button)
        {
            LiveSection section = RowSection(row);
            if (section == null) return;
            PlannedSection plan = section.Plan;
            List<Choice> choices = new List<Choice>
            {
                new Choice
                {
                    Label = "Automatic", Tooltip = "The first orbit the route reaches before it lands or ends.",
                    Current = plan.DeployAt < 1, Pick = () => SetDeployStop(plan, -1),
                },
            };
            for (int i = 1; i < state.Stops.Count; i++)
            {
                int stop = i;
                choices.Add(new Choice
                {
                    Label = StopText(i), Tooltip = "The satellite stays here; the rest flies on without it.",
                    Current = plan.DeployAt == i, Pick = () => SetDeployStop(plan, stop),
                });
            }
            Dropdown.Show(this, button, choices, PartButtonWidth);
        }

        private void SetDeployStop(PlannedSection plan, int stop)
        {
            if (plan.DeployAt == stop) return;
            plan.DeployAt = stop;
            Keep(plan);
        }
    }
}
