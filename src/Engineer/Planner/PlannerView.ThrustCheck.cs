using System.Collections.Generic;

namespace ReDefinition.Engineer
{
    // The thrust section's figures and notes, worked out with the budget: per step
    // its stages and burns, how fast what flies it turns, its RCS and electric
    // charge.
    internal sealed partial class PlannerView
    {
        // What KSP.log last said of the turns, so that it says a change once.
        private readonly System.Text.StringBuilder turnLog = new System.Text.StringBuilder();
        private string turnLogged = "";

        // Per step, what the route says of how it turns and docks; null where nothing.
        private List<string>[] turnAlerts = new List<string>[0];

        // Adds a line under the step on the route about how it turns or docks.
        private void Alert(int step, string text)
        {
            if (turnAlerts[step] == null) turnAlerts[step] = new List<string>();
            turnAlerts[step].Add(text);
        }

        // The steps that burn, each with its stages and its burns in orbit; before the
        // warnings, which take the burns that last too long from them.
        private void UpdateThrust()
        {
            turnLog.Length = 0;
            turnAlerts = new List<string>[steps.Count];
            rcsOf = new RcsInfo[steps.Count];
            thrustSteps.Clear();
            thrustOf = new ThrustStep[steps.Count];
            for (int i = 0; i < steps.Count; i++)
            {
                RouteStep step = steps[i];
                ThrustStep entry = new ThrustStep { Step = i, Twr = Launches(step) || LandsOnEngines(step) };
                foreach (StageBudget.StageUse use in budget.Uses)
                {
                    if (use.Leg != i) continue;
                    entry.Uses.Add(use);
                    StageRow row = stages.Find(item => item.Number == use.Stage);
                    entry.Stages.Add(new BurnStage
                    {
                        Stage = use.Stage, DeltaV = use.DeltaV, Mass = use.Mass, EndMass = use.EndMass,
                        Thrust = row != null ? row.ThrustVacuum : 0.0,
                    });
                }
                entry.Burns = BurnCheck.Burns(bodies.Bodies, step.Leg, entry.Stages);
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
                thrustOf[i] = entry;
                if (thrustSteps.Count < MaxFuelSteps) thrustSteps.Add(entry);
            }
            string turns = turnLog.ToString();
            if (turns != turnLogged)
            {
                turnLogged = turns;
                if (turns.Length > 0) UnityEngine.Debug.Log(ReDefinition.Core.Log.Tag + " Planner: how fast each step turns by 90°:" + turns);
            }
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
            List<int> members = new List<int>();
            bool fins = false;
            for (int i = 0; i < parts.Count && i < partGroups.Length; i++)
            {
                int group = partGroups[i];
                int id = group == 0 ? 0 : valid[group - 1].Plan.Id;
                if (!step.Flying.Contains(id)) continue;
                members.Add(i);
                if (parts[i].Fin && parts[i].AttachedIn(use.Stage)) fins = true;
            }
            // Fins steer in the air by as much as the air allows, which is not counted:
            // where they fly through it, a slow turn is no warning.
            bool air = (launch || landing) && fins && bodies.Bodies[entry.Body].HasAtmosphere;
            entry.Turn = Turning.Seconds(parts, members, use.Stage, launch || landing ? use.Stage : -1);
            entry.TurnLimit = launch || landing ? TurnLimitLanding : TurnLimitOrbit;
            turnLog.Append(" " + StepName(entry.Step) + ": " + members.Count + " parts, stage " + use.Stage + ", "
                           + (double.IsInfinity(entry.Turn) ? "none" : entry.Turn.ToString("0.0")) + " s;");
            if (double.IsNaN(entry.Turn)) return;
            string who = step.Flyer.Kind == FlyerKind.Alone && SectionById(step.Flyer.Section) != null
                ? "the " + Lower(SectionById(step.Flyer.Section).Name) : "the vessel";
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

        // The text with its first letter capital.
        private static string Capital(string text)
        {
            return string.IsNullOrEmpty(text) ? "" : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        // "12 s", in orange where slow; "None" where it cannot turn.
        private string TurnText(ThrustStep entry)
        {
            if (entry == null || double.IsNaN(entry.Turn)) return "";
            // Steered by its fins in the air, which are not counted.
            if (double.IsInfinity(entry.TurnLimit))
                return "<color=" + Dim + ">" + (double.IsInfinity(entry.Turn) ? "Fins" : EngineerFormat.Time(entry.Turn)) + "</color>";
            string text = double.IsInfinity(entry.Turn) ? "None" : EngineerFormat.Time(entry.Turn);
            return "<color=" + (TurnSlow(entry) ? Warning : Good) + ">" + text + "</color>";
        }

        // Adds the notes of a step on the RCS of what flies it, as the step starts. They give the delta-v
        // its RCS propellant gives it, its thrusters' thrust and combined Isp in vacuum and
        // their TWR at standard gravity, as KSP states a TWR in the editor. Where it
        // docks, for the last metres, a note in orange and on the route says it has no RCS
        // or no RCS propellant. The electric charge an ion engine burns over the
        // step is set against what the vessel holds.
        private void AddNotes(ThrustStep entry)
        {
            RouteStep step = steps[entry.Step];
            ElectricNote(entry);
            bool docks = step.Docks && step.Flyer.Kind == FlyerKind.Alone;
            LiveSection section = step.Flyer.Kind == FlyerKind.Alone ? SectionById(step.Flyer.Section) : null;
            string who = section != null ? "the " + Lower(section.Name) : "the vessel";
            StageBudget.StageUse use = docks ? (entry.Uses.Count > 0 ? entry.Uses[entry.Uses.Count - 1] : null)
                : entry.Uses.Count > 0 ? entry.Uses[0] : null;
            if (docks && (section == null || section.Group == null)) return;
            if (!docks && use == null) return;
            PartGroupChoice flying = new PartGroupChoice();
            foreach (int at in FlyingParts(step)) flying.Members.Add(at);
            double mass = use != null ? (docks ? use.EndMass : use.Mass) : section.Group.Mass;
            int stage = use != null ? use.Stage : -1;
            double thrust, overIsp, propellant, deltaV;
            SectionRcs.Of(parts, flying.Members, stage, mass, out thrust, out overIsp, out propellant, out deltaV);
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

        // What flies a step has of RCS, as the step starts, or as it docks at its
        // end: its mass, its thrusters' thrust and combined Isp in vacuum, the RCS
        // propellant on it and the delta-v that gives (SectionRcs).
        private sealed class RcsInfo
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

        // Per step, what flies it has of RCS; null where it has none.
        private RcsInfo[] rcsOf = new RcsInfo[0];

        // The step's RCS; null where it has none or there is no such step.
        private RcsInfo RcsAt(int step)
        {
            return step >= 0 && step < rcsOf.Length ? rcsOf[step] : null;
        }

        // A cell of the RCS row, dim; empty where the step has no RCS.
        private string RcsCell(ThrustStep entry, System.Func<RcsInfo, string> text)
        {
            RcsInfo info = entry != null ? RcsAt(entry.Step) : null;
            return info != null ? "<color=" + Dim + ">" + text(info) + "</color>" : "";
        }

        // An RCS TWR, far below 1: to three places where it is under 0.1.
        private static string RcsRatio(double value)
        {
            return value < 0.1 ? value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : EngineerFormat.Ratio(value);
        }

        // The parts of what flies a step, by index.
        private List<int> FlyingParts(RouteStep step)
        {
            List<int> members = new List<int>();
            for (int i = 0; i < parts.Count && i < partGroups.Length; i++)
            {
                int group = partGroups[i];
                int id = group == 0 ? 0 : valid[group - 1].Plan.Id;
                if (step.Flying.Contains(id)) members.Add(i);
            }
            return members;
        }

        // Where the step's engines burn electric charge (ion engines): what they burn over the step against what the vessel
        // holds, a note in orange where it holds less.
        private void ElectricNote(ThrustStep entry)
        {
            double charge = 0.0, rate = 0.0;
            foreach (StageBudget.StageUse use in entry.Uses)
            {
                StageRow row = stages.Find(s => s.Number == use.Stage);
                if (row == null || row.ElectricRate <= 0.0 || row.ThrustVacuum <= 0.0) continue;
                double exhaust = row.StartMass > row.EndMass && row.EndMass > 0.0
                    ? row.DeltaVVacuum / System.Math.Log(row.StartMass / row.EndMass)
                    : 0.0;
                double seconds = exhaust * (use.Mass - use.EndMass) / row.ThrustVacuum;
                charge += row.ElectricRate * seconds;
                rate = System.Math.Max(rate, row.ElectricRate);
            }
            if (charge <= 0.0) return;
            double held = host.Readout != null ? host.Readout.ElectricCharge : 0.0;
            string text = "Electric charge: " + rate.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "/s, "
                          + charge.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " over the burns; the vessel holds "
                          + held.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            if (charge > held)
            {
                entry.Notes.Add("<color=" + Warning + ">" + text + ", so it needs power while it burns</color>");
                entry.Warns = true;
            }
            else
                entry.Notes.Add("<color=" + Dim + ">" + text + "</color>");
        }

        // Whether the step lifts off from a surface.
        private bool Launches(RouteStep step)
        {
            return step.From.Kind == StopKind.Surface;
        }

        // Whether the step lands on its engines, not on parachutes in thick air.
        private bool LandsOnEngines(RouteStep step)
        {
            return step.To.Kind == StopKind.Surface && !Chutes(step.To);
        }
    }
}
