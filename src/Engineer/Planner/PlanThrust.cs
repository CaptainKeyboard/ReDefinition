using System;
using System.Collections.Generic;
using System.Globalization;
using static ReDefinition.Engineer.PlanText;

namespace ReDefinition.Engineer
{
    // A step as the thrust check shows it, worked out with the budget.
    internal sealed class ThrustStep
    {
        // The step's index in the plan's steps.
        public int Step;
        // Lifts off or sets down on the engines: its TWR decides.
        public bool Twr;
        // The body its TWR is against: the one it lifts off from or sets down on,
        // else the one of its first burn in orbit.
        public int Body;
        // The stages' burns on the step, as the budget gives them and as the burn check takes them, and the step's burns
        // in a circular orbit.
        public readonly List<StageBudget.StageUse> Uses = new List<StageBudget.StageUse>();
        public readonly List<BurnStage> Stages = new List<BurnStage>();
        public List<Burn> Burns = new List<Burn>();
        // The burn that takes the largest share of its orbit; null where none.
        public Burn Longest;
        // Lines under its burns, coloured: how fast it turns, the RCS of what docks,
        // the electric charge of its ion engines.
        public readonly List<string> Notes = new List<string>();
        // Whether one of the notes warns, for the summary.
        public bool Warns;
        // The seconds what flies it takes to turn by 90 degrees (Turning), NaN
        // where unknown; more than TurnLimit is slow.
        public double Turn = double.NaN;
        public double TurnLimit;
    }

    // What flies a step has of RCS, as the step starts, or as it docks at its
    // end: its mass, its thrusters' thrust and combined Isp in vacuum, the RCS
    // propellant on it and the delta-v that gives (SectionRcs).
    internal sealed class RcsInfo
    {
        // In tonnes, kN, seconds, tonnes and m/s.
        public double Mass, Thrust, Isp, Propellant, DeltaV;

        // Thrust against weight at standard gravity, as KSP states a TWR in the
        // editor, full and with the propellant spent.
        public double StartTwr
        {
            get { return Mass > 0.0 ? Thrust / (Mass * StageTable.StandardGravity) : 0.0; }
        }

        // With the RCS propellant spent.
        public double EndTwr
        {
            get { return Mass > Propellant ? Thrust / ((Mass - Propellant) * StageTable.StandardGravity) : 0.0; }
        }

        // At full thrust until the propellant is spent.
        public double Seconds
        {
            get { return Thrust > 0.0 && Isp > 0.0 ? Propellant * Isp * StageTable.StandardGravity / Thrust : 0.0; }
        }
    }

    // Whether the thrust does the job, for every step that burns. It checks the thrust against the weight where it decides, a launch and a
    // landing on the engines, and the time each burn in orbit takes against the orbit it is in. Per step it works out its stages and burns, how fast
    // what flies it turns, its RCS and its electric charge, and writes the texts the rows show. It holds no dialog elements.
    internal sealed class PlanThrust
    {
        // How long a turn by 90 degrees may take: at launch and at landing, where the
        // vessel steers as it climbs or sets down, and in orbit, where it turns to a
        // burn before it lights.
        private const double TurnLimitLanding = 10.0;
        private const double TurnLimitOrbit = 30.0;

        // The share of an orbit a burn may take before it loses MaxLoss of its
        // delta-v.
        private static readonly double MaxShare = ShareAt(BurnCheck.MaxLoss);

        private readonly PlannerModel model;

        // What KSP.log last said of the turns, so that it says a change once.
        private readonly System.Text.StringBuilder turnLog = new System.Text.StringBuilder();
        private string turnLogged = "";

        // Per step, what the route says of how it turns and docks; null where nothing.
        private List<string>[] turnAlerts = new List<string>[0];
        // The steps that burn, in the route's order, one row each.
        private readonly List<ThrustStep> entries = new List<ThrustStep>();
        // Per step, its entry; null where it burns nothing.
        private ThrustStep[] entryOf = new ThrustStep[0];
        // Per step, what flies it has of RCS; null where it has none.
        private RcsInfo[] rcsOf = new RcsInfo[0];

        internal PlanThrust(PlannerModel model)
        {
            this.model = model;
        }

        // The steps that burn, in the route's order.
        internal List<ThrustStep> Entries
        {
            get { return entries; }
        }

        // ---------------------------------------------------------------- working out

        // The steps that burn, each with its stages and its burns in orbit; before the
        // warnings, which take the burns that last too long from them.
        internal void Update()
        {
            List<RouteStep> steps = model.Steps;
            turnLog.Length = 0;
            turnAlerts = new List<string>[steps.Count];
            rcsOf = new RcsInfo[steps.Count];
            entries.Clear();
            entryOf = new ThrustStep[steps.Count];
            for (int i = 0; i < steps.Count; i++)
            {
                RouteStep step = steps[i];
                ThrustStep entry = new ThrustStep { Step = i, Twr = Launches(step) || LandsOnEngines(step) };
                foreach (StageBudget.StageUse use in model.Budget.Uses)
                {
                    if (use.Leg != i) continue;
                    entry.Uses.Add(use);
                    StageRow row = model.Stages.Find(item => item.Number == use.Stage);
                    entry.Stages.Add(new BurnStage
                    {
                        Stage = use.Stage, DeltaV = use.DeltaV, Mass = use.Mass, EndMass = use.EndMass,
                        Thrust = row != null ? row.ThrustVacuum : 0.0,
                    });
                }
                entry.Burns = BurnCheck.Burns(model.Bodies.Bodies, step.Leg, entry.Stages);
                foreach (Burn burn in entry.Burns)
                    if (entry.Longest == null || burn.Share > entry.Longest.Share) entry.Longest = burn;
                entry.Body = Launches(step) ? step.From.Body
                    : entry.Twr ? step.To.Body
                    : entry.Burns.Count > 0 ? entry.Burns[0].Line.Body : step.From.Body;
                AddTurn(entry, step);
                AddNotes(entry);
                // A step shows for its launch or landing, its burns, and its notes other
                // than one that says how it turns without a warning.
                int notes = entry.Notes.Count - (!double.IsNaN(entry.Turn) && !TurnSlow(entry) ? 1 : 0);
                if (step.Leg.DeltaV <= 0.5 || (!entry.Twr && entry.Burns.Count == 0 && notes == 0)) continue;
                entryOf[i] = entry;
                if (entries.Count < PlannerModel.HardFuelSteps) entries.Add(entry);
            }
            string turns = turnLog.ToString();
            if (turns != turnLogged)
            {
                turnLogged = turns;
                if (turns.Length > 0) model.Say("Planner: how fast each step turns by 90°:" + turns);
            }
        }

        // Adds a line under the step on the route about how it turns or docks.
        private void Alert(int step, string text)
        {
            if (turnAlerts[step] == null) turnAlerts[step] = new List<string>();
            turnAlerts[step].Add(text);
        }

        // Works out how fast what flies the step turns. At launch it is as its first stage lights,
        // its gimbals counting. At landing it is as its last stage lights, likewise. In
        // orbit it is as its first stage starts, before it lights, on its reaction wheels
        // and RCS alone. A note says it, and one in orange where it is slower than the
        // step allows.
        private void AddTurn(ThrustStep entry, RouteStep step)
        {
            if (entry.Uses.Count == 0) return;
            bool launch = Launches(step), landing = !launch && LandsOnEngines(step);
            StageBudget.StageUse use = landing ? entry.Uses[entry.Uses.Count - 1] : entry.Uses[0];
            List<PartFacts> parts = model.Sections.Parts;
            List<int> members = model.Sections.FlyingParts(step);
            bool fins = false;
            foreach (int member in members)
                if (parts[member].Fin && parts[member].AttachedIn(use.Stage)) fins = true;
            // Fins steer in the air by as much as the air allows, which is not counted:
            // where they fly through it, a slow turn is no warning.
            bool air = (launch || landing) && fins && model.Bodies.Bodies[entry.Body].HasAtmosphere;
            entry.Turn = Turning.Seconds(parts, members, use.Stage, launch || landing ? use.Stage : -1);
            entry.TurnLimit = launch || landing ? TurnLimitLanding : TurnLimitOrbit;
            turnLog.Append(" " + model.StepName(entry.Step) + ": " + members.Count + " parts, stage " + use.Stage + ", "
                           + (double.IsInfinity(entry.Turn) ? "none" : entry.Turn.ToString("0.0")) + " s;");
            if (double.IsNaN(entry.Turn)) return;
            LiveSection flyer = step.Flyer.Kind == FlyerKind.Alone ? model.Sections.SectionById(step.Flyer.Section) : null;
            string who = flyer != null ? "the " + Lower(flyer.Name) : "the vessel";
            string how = launch ? "as it climbs" : landing ? "as it lands" : "to its burn";
            if (TurnSlow(entry) && air)
            {
                entry.TurnLimit = double.PositiveInfinity;
                entry.Notes.Add("<color=" + Dim + ">" + Capital(who) + (double.IsInfinity(entry.Turn)
                    ? " steers " + how + " with its fins alone"
                    : " turns 90° in " + EngineerFormat.Time(entry.Turn) + " " + how + " without its fins, which steer in the air") + "</color>");
            }
            else if (TurnSlow(entry))
            {
                entry.Notes.Add("<color=" + Warning + ">" + (double.IsInfinity(entry.Turn)
                    ? Capital(who) + " has nothing to turn " + how + " with: no reaction wheels, RCS or gimballing engines"
                    : Capital(who) + " turns 90° in " + EngineerFormat.Time(entry.Turn) + " " + how + ": slow, more than "
                      + EngineerFormat.Time(entry.TurnLimit)) + "</color>");
                Alert(entry.Step, double.IsInfinity(entry.Turn)
                    ? "Nothing on " + who + " to turn " + how + " with"
                    : Capital(who) + " turns slowly " + how + ": 90° in " + EngineerFormat.Time(entry.Turn) + ", add reaction wheels or RCS");
            }
            else
                entry.Notes.Add("<color=" + Dim + ">" + Capital(who) + " turns 90° in " + EngineerFormat.Time(entry.Turn) + " " + how + "</color>");
        }

        // Whether what flies the step takes longer than its limit to turn by 90 degrees.
        private static bool TurnSlow(ThrustStep entry)
        {
            return entry != null && !double.IsNaN(entry.Turn) && entry.Turn > entry.TurnLimit;
        }

        // Adds the notes of a step on the RCS of what flies it, as the step starts. They give the delta-v
        // its RCS propellant gives it, its thrusters' thrust and combined Isp in vacuum and
        // their TWR at standard gravity, as KSP states a TWR in the editor. Where it
        // docks, for the last metres, a note in orange and on the route says it has no RCS
        // or no RCS propellant. The electric charge an ion engine burns over the
        // step is set against what the vessel holds.
        private void AddNotes(ThrustStep entry)
        {
            RouteStep step = model.Steps[entry.Step];
            ElectricNote(entry);
            bool docks = step.Docks && step.Flyer.Kind == FlyerKind.Alone;
            LiveSection section = step.Flyer.Kind == FlyerKind.Alone ? model.Sections.SectionById(step.Flyer.Section) : null;
            string who = section != null ? "the " + Lower(section.Name) : "the vessel";
            StageBudget.StageUse use = docks ? (entry.Uses.Count > 0 ? entry.Uses[entry.Uses.Count - 1] : null)
                : entry.Uses.Count > 0 ? entry.Uses[0] : null;
            if (docks && (section == null || section.Group == null)) return;
            if (!docks && use == null) return;
            PartGroupChoice flying = new PartGroupChoice();
            foreach (int at in model.Sections.FlyingParts(step)) flying.Members.Add(at);
            double mass = use != null ? (docks ? use.EndMass : use.Mass) : section.Group.Mass;
            int stage = use != null ? use.Stage : -1;
            double thrust, overIsp, propellant, deltaV;
            SectionRcs.Of(model.Sections.Parts, flying.Members, stage, mass, out thrust, out overIsp, out propellant, out deltaV);
            if (docks && (thrust <= 0.0 || propellant <= 0.0))
            {
                string text = (thrust <= 0.0 ? "No RCS on " : "No RCS propellant on ") + who + " to dock with";
                entry.Notes.Add("<color=" + Warning + ">" + Capital(text) + "</color>");
                entry.Warns = true;
                Alert(entry.Step, Capital(text));
                return;
            }
            if (thrust <= 0.0) return;
            rcsOf[entry.Step] = new RcsInfo
            {
                Mass = mass, Thrust = thrust, Propellant = propellant, DeltaV = deltaV,
                Isp = overIsp > 0.0 ? thrust / overIsp : 0.0,
            };
        }

        // Where the step's engines burn electric charge (ion engines): what they burn over the step against what the vessel
        // holds, a note in orange where it holds less.
        private void ElectricNote(ThrustStep entry)
        {
            double charge = 0.0, rate = 0.0;
            foreach (StageBudget.StageUse use in entry.Uses)
            {
                StageRow row = model.Stages.Find(s => s.Number == use.Stage);
                if (row == null || row.ElectricRate <= 0.0 || row.ThrustVacuum <= 0.0) continue;
                double exhaust = row.StartMass > row.EndMass && row.EndMass > 0.0
                    ? row.DeltaVVacuum / Math.Log(row.StartMass / row.EndMass)
                    : 0.0;
                double seconds = exhaust * (use.Mass - use.EndMass) / row.ThrustVacuum;
                charge += row.ElectricRate * seconds;
                rate = Math.Max(rate, row.ElectricRate);
            }
            if (charge <= 0.0) return;
            double held = model.Host.Readout != null ? model.Host.Readout.ElectricCharge : 0.0;
            string text = "Electric charge: " + rate.ToString("0.0", CultureInfo.InvariantCulture) + "/s, "
                          + charge.ToString("N0", CultureInfo.InvariantCulture) + " over the burns; the vessel holds "
                          + held.ToString("N0", CultureInfo.InvariantCulture);
            if (charge > held)
            {
                entry.Notes.Add("<color=" + Warning + ">" + text + ", so it needs power while it burns</color>");
                entry.Warns = true;
            }
            else
                entry.Notes.Add("<color=" + Dim + ">" + text + "</color>");
        }

        // ---------------------------------------------------------------- what the plan asks

        // The entry of a row of the thrust check; null for none.
        internal ThrustStep Entry(int row)
        {
            return model.Worked() && row < entries.Count && entries[row].Step < model.Steps.Count ? entries[row] : null;
        }

        // The longest burn in orbit of the step; null where it has none or there is no such step.
        internal Burn LongestBurn(int step)
        {
            return step < entryOf.Length && entryOf[step] != null ? entryOf[step].Longest : null;
        }

        // Adds the lines about how the step turns and docks, where it has any, to the list.
        internal void AddAlerts(int step, List<string> into)
        {
            if (step < turnAlerts.Length && turnAlerts[step] != null) into.AddRange(turnAlerts[step]);
        }

        // The step's RCS; null where it has none or there is no such step.
        internal RcsInfo RcsAt(int step)
        {
            return step >= 0 && step < rcsOf.Length ? rcsOf[step] : null;
        }

        // Whether the step lifts off from a surface.
        internal static bool Launches(RouteStep step)
        {
            return step.From.Kind == StopKind.Surface;
        }

        // Whether the step lands on its engines, not on parachutes in thick air.
        internal bool LandsOnEngines(RouteStep step)
        {
            return step.To.Kind == StopKind.Surface && !Chutes(step.To);
        }

        // Parachutes that leave nothing to burn: air thick enough to land on them.
        internal bool Chutes(RouteStop stop)
        {
            PlannerBody body = model.Bodies.Bodies[stop.Body];
            return stop.UseAtmosphere && body.HasAtmosphere && body.SurfacePressure >= 0.2;
        }

        // What a lift-off or a set-down needs: 1.2 from a body with air, 1 otherwise;
        // nothing along a runway.
        internal double TwrNeeded(ThrustStep entry)
        {
            bool launch = Launches(model.Steps[entry.Step]);
            StageBudget.StageUse deciding = Deciding(entry);
            return TwrRequired(launch, launch && deciding != null && Runway(deciding.Stage), entry.Body);
        }

        // What a lift-off or a set-down needs of a stage's thrust over weight. One rule serves the warnings, the fuel
        // plan's stage rows and the thrust part. Nothing along a runway; from a body (an index in the bodies) 1.2
        // with air, else 1. `runway` only counts for a launch.
        internal double TwrRequired(bool launch, bool runway, int body)
        {
            if (launch && runway) return 0.0;
            return launch && model.Bodies.Bodies[body].HasAtmosphere ? 1.2 : 1.0;
        }

        // The stage's thrust over the weight of `mass` at the body's surface: in vacuum,
        // or in the air there where it has any (StageRow.ThrustAtSurface).
        internal double TwrAt(int stage, double mass, int body, bool air)
        {
            StageRow row = model.Stages.Find(s => s.Number == stage);
            PlannerBody planner = model.Bodies.Bodies[body];
            double gravity = planner.Mu / (planner.Radius * planner.Radius);
            if (row == null || mass <= 0.0 || gravity <= 0.0) return 0.0;
            double thrust = row.ThrustVacuum;
            double surface;
            if (air && planner.HasAtmosphere && row.ThrustAtSurface.TryGetValue(planner.Id, out surface)) thrust = surface;
            return thrust / (mass * gravity);
        }

        // Lifting off on air-breathing engines with wings and wheels: along a runway,
        // at any TWR.
        internal bool Runway(int stage)
        {
            StageRow row = model.Stages.Find(s => s.Number == stage);
            return row != null && row.Air && VesselShape.Runway(model.Sections.Parts);
        }

        // The air at a body's surface, in atmospheres; 0 where it has none.
        internal double SurfaceAir(int body)
        {
            PlannerBody planner = model.Bodies.Bodies[body];
            return planner.HasAtmosphere ? planner.SurfacePressure : 0.0;
        }

        // The first stage the budget burns on the step, null for none; the lift-off stage of a launch.
        internal StageBudget.StageUse FirstUse(int step)
        {
            return model.Budget.Uses.Find(use => use.Leg == step);
        }

        // The last stage the budget burns on the step, null for none; the set-down stage of a landing.
        internal StageBudget.StageUse LastUse(int step)
        {
            return model.Budget.Uses.FindLast(use => use.Leg == step);
        }

        // The stage that lifts off or sets down: the first of a launch, the last of a
        // landing.
        internal StageBudget.StageUse Deciding(ThrustStep entry)
        {
            if (entry.Uses.Count == 0) return null;
            return Launches(model.Steps[entry.Step]) ? entry.Uses[0] : entry.Uses[entry.Uses.Count - 1];
        }

        // ---------------------------------------------------------------- texts

        // What the step needs: a TWR, "Runway", or burns under MaxShare of their orbit.
        internal string NeedsText(ThrustStep entry)
        {
            if (entry == null) return "";
            string text = !entry.Twr ? "< " + Percent(MaxShare) + " orbit"
                : TwrNeeded(entry) <= 0.0 ? "Runway" : "TWR " + EngineerFormat.Ratio(TwrNeeded(entry));
            return "<color=" + Dim + ">" + text + "</color>";
        }

        // A launch or landing: the deciding stage's TWR at the surface, in the air
        // where the body has any. Otherwise the burn that takes the largest share of
        // its orbit: its time and share.
        internal string HasText(ThrustStep entry)
        {
            if (entry == null) return "";
            if (entry.Twr)
            {
                StageBudget.StageUse use = Deciding(entry);
                if (use == null) return "<color=" + Dim + ">No stage</color>";
                double twr = TwrAt(use.Stage, use.Mass, entry.Body, true);
                return "<color=" + (twr < TwrNeeded(entry) ? Problem : Good) + ">" + model.StageLabel(use.Stage) + "  " + EngineerFormat.Ratio(twr) + "</color>";
            }
            if (entry.Longest == null) return "";
            return "<color=" + BurnColour(entry.Longest) + ">" + EngineerFormat.Time(entry.Longest.Seconds) + " "
                   + Percent(entry.Longest.Share) + "</color>";
        }

        // The share of an orbit a burn may take before it loses `loss` of its delta-v, found by halving.
        private static double ShareAt(double loss)
        {
            double low = 0.0, high = 1.0;
            for (int i = 0; i < 40; i++)
            {
                double middle = (low + high) / 2.0;
                if (new Burn { Seconds = middle, Period = 1.0 }.Loss < loss) low = middle;
                else high = middle;
            }
            return low;
        }

        // Red for a burn too short of thrust or too long, orange for one over WarnLoss, else green.
        internal static string BurnColour(Burn burn)
        {
            if (burn.Short || burn.Loss > BurnCheck.MaxLoss) return Problem;
            return burn.Loss > BurnCheck.WarnLoss ? Warning : Good;
        }

        // The coloured name of the step's stage in that row.
        internal string StageText(ThrustStep entry, int stage)
        {
            return entry != null && stage < entry.Uses.Count ? model.ColouredStage(entry.Uses[stage].Stage) : "";
        }

        // As the stage starts or ends its part, in vacuum, or in the air at the surface
        // on a launch or landing where the body has any.
        internal string TwrValue(ThrustStep entry, int stage, bool start, bool air)
        {
            if (entry == null || stage >= entry.Uses.Count) return "";
            if (air && (!entry.Twr || SurfaceAir(entry.Body) <= 0.0)) return "";
            StageBudget.StageUse use = entry.Uses[stage];
            double twr = TwrAt(use.Stage, start ? use.Mass : use.EndMass, entry.Body, air);
            return "<color=" + Dim + ">" + EngineerFormat.Ratio(twr) + "</color>";
        }

        // What the vessel weighs as the stage starts its part of the step.
        internal string MassText(ThrustStep entry, int stage)
        {
            if (entry == null || stage >= entry.Stages.Count) return "";
            return "<color=" + Dim + ">" + EngineerFormat.Mass(entry.Stages[stage].Mass) + "</color>";
        }

        // In vacuum: the exhaust speed the stage's delta-v comes from, over g0.
        internal string IspText(ThrustStep entry, int stage)
        {
            if (entry == null || stage >= entry.Stages.Count) return "";
            double exhaust = BurnCheck.Exhaust(entry.Stages[stage]);
            return exhaust > 0.0 ? "<color=" + Dim + ">" + EngineerFormat.Isp(exhaust / StageTable.StandardGravity) + "</color>" : "";
        }

        // The stage's thrust in vacuum, in whole kN.
        internal string ThrustText(ThrustStep entry, int stage)
        {
            if (entry == null || stage >= entry.Stages.Count) return "";
            return "<color=" + Dim + ">" + entry.Stages[stage].Thrust.ToString("0", CultureInfo.InvariantCulture) + " kN</color>";
        }

        // How long the stage burns for its part of the step.
        internal string StageBurnText(ThrustStep entry, int stage)
        {
            if (entry == null || stage >= entry.Stages.Count) return "";
            return "<color=" + Dim + ">" + EngineerFormat.Time(entry.Stages[stage].Seconds) + "</color>";
        }

        // A burn in orbit: its name, then "4m 10s, 13% of 32m 05s, 20 m/s lost".
        internal string BurnNameText(ThrustStep entry, int index)
        {
            if (entry == null || index >= entry.Burns.Count) return "";
            return "<color=" + Dim + ">" + entry.Burns[index].Line.Name + "</color>";
        }

        // The burn's time ("+" where the stages fall short), its share of the orbit, the orbit's period, and the delta-v lost.
        internal string BurnText(ThrustStep entry, int index)
        {
            if (entry == null || index >= entry.Burns.Count) return "";
            Burn burn = entry.Burns[index];
            string text = EngineerFormat.Time(burn.Seconds) + (burn.Short ? "+" : "") + ", " + Percent(burn.Share) + " of "
                          + EngineerFormat.Time(burn.Period);
            if (burn.Loss * burn.Line.DeltaV >= 0.5) text += ", " + EngineerFormat.Speed(burn.Loss * burn.Line.DeltaV) + " lost";
            return "<color=" + BurnColour(burn) + ">" + text + "</color>";
        }

        // "Kerbin: surface gravity 9.81 m/s², atmosphere 1.00 atm".
        internal string BodyText(ThrustStep entry)
        {
            if (entry == null) return "";
            PlannerBody body = model.Bodies.Bodies[entry.Body];
            double gravity = body.Mu / (body.Radius * body.Radius);
            string air = SurfaceAir(entry.Body) > 0.0
                ? "atmosphere " + SurfaceAir(entry.Body).ToString("0.00", CultureInfo.InvariantCulture) + " atm"
                : "no atmosphere";
            return "<color=" + Dim + ">" + body.Name + ": surface gravity " + gravity.ToString("0.00", CultureInfo.InvariantCulture)
                   + " m/s², " + air + "</color>";
        }

        // "12 s", in orange where slow; "None" where it cannot turn.
        internal string TurnText(ThrustStep entry)
        {
            if (entry == null || double.IsNaN(entry.Turn)) return "";
            // Steered by its fins in the air, which are not counted.
            if (double.IsInfinity(entry.TurnLimit))
                return "<color=" + Dim + ">" + (double.IsInfinity(entry.Turn) ? "Fins" : EngineerFormat.Time(entry.Turn)) + "</color>";
            string text = double.IsInfinity(entry.Turn) ? "None" : EngineerFormat.Time(entry.Turn);
            return "<color=" + (TurnSlow(entry) ? Warning : Good) + ">" + text + "</color>";
        }

        // A cell of the RCS row, dim; empty where the step has no RCS.
        internal string RcsCell(ThrustStep entry, Func<RcsInfo, string> text)
        {
            RcsInfo info = entry != null ? RcsAt(entry.Step) : null;
            return info != null ? "<color=" + Dim + ">" + text(info) + "</color>" : "";
        }

        // An RCS TWR, far below 1: to three places where it is under 0.1.
        internal static string RcsRatio(double value)
        {
            return value < 0.1 ? value.ToString("0.000", CultureInfo.InvariantCulture) : EngineerFormat.Ratio(value);
        }

        // "Enough thrust on every step", or the steps where it is too little or a burn
        // too long.
        internal string Summary()
        {
            int low = 0, slow = 0, noted = 0, turning = 0;
            foreach (ThrustStep entry in entries)
            {
                if (TurnSlow(entry)) turning++;
                if (entry.Warns) noted++;
                if (entry.Twr)
                {
                    StageBudget.StageUse use = Deciding(entry);
                    if (use != null && TwrAt(use.Stage, use.Mass, entry.Body, true) < TwrNeeded(entry)) low++;
                }
                else if (entry.Longest != null && BurnColour(entry.Longest) != Good) slow++;
            }
            if (low == 0 && slow == 0 && noted == 0 && turning == 0) return "<color=" + Good + ">Enough thrust on every step</color>";
            string text = "";
            if (low > 0) text = "TWR too low on " + low + (low == 1 ? " step" : " steps");
            if (slow > 0) text += (text.Length > 0 ? ", " : "") + slow + (slow == 1 ? " burn" : " burns") + " too long";
            if (turning > 0) text += (text.Length > 0 ? ", " : "") + turning + (turning == 1 ? " step turns" : " steps turn") + " slowly";
            if (noted > 0) text += (text.Length > 0 ? ", " : "") + noted + (noted == 1 ? " note" : " notes");
            return "<color=" + (low > 0 ? Problem : Warning) + ">" + text + "</color>";
        }
    }
}
