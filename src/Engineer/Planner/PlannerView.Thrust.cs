using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The planner's thrust section: its rows, and the heading's summary.
    internal sealed partial class PlannerView
    {
        // ---------------------------------------------------------------- thrust

        // Whether the thrust does the job, for every step that burns: the thrust
        // against the weight where it decides, a launch and a landing on the engines;
        // the time each burn in orbit takes against the orbit it is in. Each step with
        // what it needs and what it has; open, each stage it burns with its TWR as it
        // starts and as it ends its part, in the air where the body has any, its thrust
        // and how long it burns, then each burn in orbit and the body.
        private const float MassColumn = 60f;
        private const float IspColumn = 44f;
        private const float TwrColumn = 38f;
        private const float ThrustColumn = 60f;
        private const float BurnColumn = 64f;
        private const int MaxNotes = 5;
        private const float BurnName = 180f;

        // A step as the thrust section shows it, worked out with the budget.
        private sealed class ThrustStep
        {
            public int Step;
            // Lifts off or sets down on the engines: its TWR decides.
            public bool Twr;
            // The body its TWR is against: the one it lifts off from or sets down on,
            // else the one of its first burn in orbit.
            public int Body;
            public readonly List<StageBudget.StageUse> Uses = new List<StageBudget.StageUse>();
            public readonly List<BurnStage> Stages = new List<BurnStage>();
            public List<Burn> Burns = new List<Burn>();
            // The burn that takes the largest share of its orbit; null where none.
            public Burn Longest;
            // Lines under its burns, coloured: the RCS of what docks, thrust off the
            // centre of mass at launch.
            public readonly List<string> Notes = new List<string>();
            public bool Warns;
            // The seconds what flies it takes to turn by 90 degrees (Turning), NaN
            // where unknown; more than TurnLimit is slow.
            public double Turn = double.NaN;
            public double TurnLimit;
        }

        // How long a turn by 90 degrees may take: at launch and at landing, where the
        // vessel steers as it climbs or sets down, and in orbit, where it turns to a
        // burn before it lights.
        private const double TurnLimitLanding = 10.0;
        private const double TurnLimitOrbit = 30.0;
        private const float TurnColumn = 56f;

        private readonly List<ThrustStep> thrustSteps = new List<ThrustStep>();
        // Per step, its entry; null where it burns nothing.
        private ThrustStep[] thrustOf = new ThrustStep[0];
        private readonly HashSet<int> thrustOpen = new HashSet<int>();
        private bool thrustShown;

        private IEnumerable<DialogGUIBase> ThrustRows()
        {
            List<DialogGUIBase> heads = new List<DialogGUIBase>
            {
                new DialogGUISpace(Timeline), DimLabel("Step", Place), DimLabel("Flown by", VehicleButton), DimLabel("Needs", FuelNeeds),
                DimLabel("Has", FuelLeft),
            };
            if (TurnFits) heads.Add(DimLabel("Turns 90°", TurnColumn));
            DialogGUIHorizontalLayout names = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                heads.ToArray());
            names.OptionEnabledCondition = () => thrustShown && Worked() && thrustSteps.Count > 0;
            yield return names;
            DialogGUIHorizontalLayout none = new DialogGUIHorizontalLayout(width, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline), DimLabel("No burns on the route.", width - Timeline));
            none.OptionEnabledCondition = () => thrustShown && Worked() && thrustSteps.Count == 0;
            yield return none;
            for (int i = 0; i < MaxFuelSteps; i++)
                foreach (DialogGUIBase row in ThrustStepRows(i))
                    yield return row;
        }

        // The entry of a row; null for none.
        private ThrustStep ThrustAt(int row)
        {
            return thrustShown && Worked() && row < thrustSteps.Count && thrustSteps[row].Step < steps.Count ? thrustSteps[row] : null;
        }

        // The entry of an open row; null for none, and for a closed one, so that its
        // texts are not written.
        private ThrustStep OpenThrust(int row)
        {
            ThrustStep entry = ThrustAt(row);
            return entry != null && thrustOpen.Contains(FuelKey(entry.Step)) ? entry : null;
        }

        private void ToggleThrust(int row)
        {
            ThrustStep entry = ThrustAt(row);
            if (entry == null) return;
            int key = FuelKey(entry.Step);
            if (!thrustOpen.Remove(key)) thrustOpen.Add(key);
        }

        private IEnumerable<DialogGUIBase> ThrustStepRows(int row)
        {
            DialogGUIButton open = new DialogGUIButton(() => OpenThrust(row) != null ? "<b><color=" + Good + ">-</color></b>" : "+",
                () => ToggleThrust(row), Square, Square, false);
            open.tooltipText = "Show the stages of this step with their thrust, and each burn.";
            float room = Mathf.Max(0f, ThrustRoom - (TurnFits ? TurnColumn + 4f : 0f));
            List<DialogGUIBase> cells = new List<DialogGUIBase>
            {
                new DialogGUISpace(Timeline), new DialogGUILabel(Text(() => ThrustAt(row) != null ? StepName(ThrustAt(row).Step) : ""), Place),
                new DialogGUILabel(Text(() => ThrustAt(row) != null ? StepFlyer(ThrustAt(row).Step) : ""), VehicleButton),
                new DialogGUILabel(Text(() => ThrustNeedsText(ThrustAt(row))), FuelNeeds), new DialogGUILabel(Text(() => ThrustHasText(ThrustAt(row))), FuelLeft),
            };
            if (TurnFits) cells.Add(new DialogGUILabel(Text(() => TurnText(ThrustAt(row))), TurnColumn));
            cells.Add(new DialogGUISpace(room));
            cells.Add(open);
            DialogGUIHorizontalLayout line = new DialogGUIHorizontalLayout(width, Square + 2f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                cells.ToArray());
            line.OptionEnabledCondition = () => ThrustAt(row) != null;
            yield return line;

            DialogGUIHorizontalLayout names = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline + Swatch + 4f), DimLabel("Stage", StageName_), DimLabel("Mass", MassColumn),
                DimLabel("Isp", IspColumn), DimLabel("Start", TwrColumn), DimLabel("End", TwrColumn), DimLabel("In air", TwrColumn),
                DimLabel("Thrust", ThrustColumn), DimLabel("Burns", BurnColumn));
            names.OptionEnabledCondition = () => OpenThrust(row) != null && OpenThrust(row).Uses.Count > 0;
            yield return names;

            for (int i = 0; i < MaxStepStages; i++)
            {
                int stage = i;
                DialogGUIHorizontalLayout use = new DialogGUIHorizontalLayout(width, RowHeight + 2f, 4f, new RectOffset(),
                    TextAnchor.MiddleLeft, new DialogGUISpace(Timeline + Swatch + 4f),
                    new DialogGUILabel(Text(() => ThrustStageName(OpenThrust(row), stage)), StageName_),
                    new DialogGUILabel(Text(() => StageMassText(OpenThrust(row), stage)), MassColumn),
                    new DialogGUILabel(Text(() => StageIspText(OpenThrust(row), stage)), IspColumn),
                    new DialogGUILabel(Text(() => TwrValue(OpenThrust(row), stage, true, false)), TwrColumn),
                    new DialogGUILabel(Text(() => TwrValue(OpenThrust(row), stage, false, false)), TwrColumn),
                    new DialogGUILabel(Text(() => TwrValue(OpenThrust(row), stage, true, true)), TwrColumn),
                    new DialogGUILabel(Text(() => StageThrustText(OpenThrust(row), stage)), ThrustColumn),
                    new DialogGUILabel(Text(() => StageBurnText(OpenThrust(row), stage)), BurnColumn));
                use.OptionEnabledCondition = () => OpenThrust(row) != null && stage < OpenThrust(row).Uses.Count;
                yield return use;
            }

            // Its RCS, as a stage of its own.
            DialogGUIHorizontalLayout rcs = new DialogGUIHorizontalLayout(width, RowHeight + 2f, 4f, new RectOffset(),
                TextAnchor.MiddleLeft, new DialogGUISpace(Timeline + Swatch + 4f), DimLabel("RCS", StageName_),
                new DialogGUILabel(Text(() => RcsCell(OpenThrust(row), info => EngineerFormat.Mass(info.Mass))), MassColumn),
                new DialogGUILabel(Text(() => RcsCell(OpenThrust(row), info => EngineerFormat.Isp(info.Isp))), IspColumn),
                new DialogGUILabel(Text(() => RcsCell(OpenThrust(row), info => RcsRatio(info.StartTwr))), TwrColumn),
                new DialogGUILabel(Text(() => RcsCell(OpenThrust(row), info => RcsRatio(info.EndTwr))), TwrColumn),
                new DialogGUISpace(TwrColumn),
                new DialogGUILabel(Text(() => RcsCell(OpenThrust(row), info => EngineerFormat.Force(info.Thrust))), ThrustColumn),
                new DialogGUILabel(Text(() => RcsCell(OpenThrust(row), info => EngineerFormat.Time(info.Seconds))), BurnColumn));
            rcs.OptionEnabledCondition = () => OpenThrust(row) != null && RcsAt(OpenThrust(row).Step) != null;
            yield return rcs;

            for (int i = 0; i < MaxFuelLines; i++)
            {
                int burn = i;
                DialogGUIHorizontalLayout text = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                    new DialogGUISpace(Timeline + Swatch + 4f), new DialogGUILabel(Text(() => BurnName_(OpenThrust(row), burn)), BurnName),
                    new DialogGUILabel(Text(() => BurnText(OpenThrust(row), burn)), width - Timeline - Swatch - 4f - BurnName - 4f * 2f - RowEnd));
                text.OptionEnabledCondition = () => OpenThrust(row) != null && burn < OpenThrust(row).Burns.Count;
                yield return text;
            }

            for (int i = 0; i < MaxNotes; i++)
            {
                int note = i;
                DialogGUIHorizontalLayout text = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                    new DialogGUISpace(Timeline + Swatch + 4f),
                    new DialogGUILabel(Text(() => OpenThrust(row) != null && note < OpenThrust(row).Notes.Count ? OpenThrust(row).Notes[note] : ""),
                        width - Timeline - Swatch - 4f - 4f - RowEnd));
                text.OptionEnabledCondition = () => OpenThrust(row) != null && note < OpenThrust(row).Notes.Count;
                yield return text;
            }

            DialogGUIHorizontalLayout body = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline + Swatch + 4f),
                new DialogGUILabel(Text(() => ThrustBodyText(OpenThrust(row))), width - Timeline - Swatch - 4f - 4f - RowEnd));
            body.OptionEnabledCondition = () => OpenThrust(row) != null;
            yield return body;
        }

        // What a step's line leaves beside its cells and its button; the turn's
        // column stands there where it fits, else its note alone says it.
        private float ThrustRoom
        {
            get { return width - Timeline - Place - VehicleButton - FuelNeeds - FuelLeft - Square - 4f * 6f - RowEnd; }
        }

        private bool TurnFits
        {
            get { return ThrustRoom >= TurnColumn + 4f; }
        }

        // What a lift-off or a set-down needs: 1.2 from a body with air, 1 otherwise;
        // nothing along a runway.
        private double TwrNeeded(ThrustStep entry)
        {
            bool launch = Launches(steps[entry.Step]);
            StageBudget.StageUse deciding = Deciding(entry);
            if (launch && deciding != null && Runway(deciding.Stage)) return 0.0;
            return launch && bodies.Bodies[entry.Body].HasAtmosphere ? 1.2 : 1.0;
        }

        // The stage that lifts off or sets down: the first of a launch, the last of a
        // landing.
        private StageBudget.StageUse Deciding(ThrustStep entry)
        {
            if (entry.Uses.Count == 0) return null;
            return Launches(steps[entry.Step]) ? entry.Uses[0] : entry.Uses[entry.Uses.Count - 1];
        }

        private string ThrustNeedsText(ThrustStep entry)
        {
            if (entry == null) return "";
            string text = !entry.Twr ? "< " + Percent(MaxShare) + " orbit"
                : TwrNeeded(entry) <= 0.0 ? "Runway" : "TWR " + EngineerFormat.Ratio(TwrNeeded(entry));
            return "<color=" + Dim + ">" + text + "</color>";
        }

        // A launch or landing: the deciding stage's TWR at the surface, in the air
        // where the body has any. Otherwise the burn that takes the largest share of
        // its orbit: its time and share.
        private string ThrustHasText(ThrustStep entry)
        {
            if (entry == null) return "";
            if (entry.Twr)
            {
                StageBudget.StageUse use = Deciding(entry);
                if (use == null) return "<color=" + Dim + ">No stage</color>";
                double twr = TwrAt(use.Stage, use.Mass, entry.Body, true);
                return "<color=" + (twr < TwrNeeded(entry) ? Problem : Good) + ">" + StageLabel(use.Stage) + "  " + EngineerFormat.Ratio(twr) + "</color>";
            }
            if (entry.Longest == null) return "";
            return "<color=" + BurnColour(entry.Longest) + ">" + EngineerFormat.Time(entry.Longest.Seconds) + " "
                   + Percent(entry.Longest.Share) + "</color>";
        }

        // The share of an orbit a burn may take before it loses MaxLoss of its
        // delta-v.
        private static readonly double MaxShare = ShareAt(BurnCheck.MaxLoss);

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

        private static string Percent(double share)
        {
            return (share * 100.0).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        private static string BurnColour(Burn burn)
        {
            if (burn.Short || burn.Loss > BurnCheck.MaxLoss) return Problem;
            return burn.Loss > BurnCheck.WarnLoss ? Warning : Good;
        }

        private string ThrustStageName(ThrustStep entry, int stage)
        {
            return entry != null && stage < entry.Uses.Count ? ColouredStage(entry.Uses[stage].Stage) : "";
        }

        // As the stage starts or ends its part, in vacuum, or in the air at the surface
        // on a launch or landing where the body has any.
        private string TwrValue(ThrustStep entry, int stage, bool start, bool air)
        {
            if (entry == null || stage >= entry.Uses.Count) return "";
            if (air && (!entry.Twr || SurfaceAir(entry.Body) <= 0.0)) return "";
            StageBudget.StageUse use = entry.Uses[stage];
            double twr = TwrAt(use.Stage, start ? use.Mass : use.EndMass, entry.Body, air);
            return "<color=" + Dim + ">" + EngineerFormat.Ratio(twr) + "</color>";
        }

        // What the vessel weighs as the stage starts its part of the step.
        private string StageMassText(ThrustStep entry, int stage)
        {
            if (entry == null || stage >= entry.Stages.Count) return "";
            return "<color=" + Dim + ">" + EngineerFormat.Mass(entry.Stages[stage].Mass) + "</color>";
        }

        // In vacuum: the exhaust speed the stage's delta-v comes from, over g0.
        private string StageIspText(ThrustStep entry, int stage)
        {
            if (entry == null || stage >= entry.Stages.Count) return "";
            double exhaust = BurnCheck.Exhaust(entry.Stages[stage]);
            return exhaust > 0.0 ? "<color=" + Dim + ">" + EngineerFormat.Isp(exhaust / StageTable.StandardGravity) + "</color>" : "";
        }

        private string StageThrustText(ThrustStep entry, int stage)
        {
            if (entry == null || stage >= entry.Stages.Count) return "";
            return "<color=" + Dim + ">" + entry.Stages[stage].Thrust.ToString("0", CultureInfo.InvariantCulture) + " kN</color>";
        }

        private string StageBurnText(ThrustStep entry, int stage)
        {
            if (entry == null || stage >= entry.Stages.Count) return "";
            return "<color=" + Dim + ">" + EngineerFormat.Time(entry.Stages[stage].Seconds) + "</color>";
        }

        // A burn in orbit: its name, then "4m 10s, 13% of 32m 05s, 20 m/s lost".
        private string BurnName_(ThrustStep entry, int index)
        {
            if (entry == null || index >= entry.Burns.Count) return "";
            return "<color=" + Dim + ">" + entry.Burns[index].Line.Name + "</color>";
        }

        private string BurnText(ThrustStep entry, int index)
        {
            if (entry == null || index >= entry.Burns.Count) return "";
            Burn burn = entry.Burns[index];
            string text = EngineerFormat.Time(burn.Seconds) + (burn.Short ? "+" : "") + ", " + Percent(burn.Share) + " of "
                          + EngineerFormat.Time(burn.Period);
            if (burn.Loss * burn.Line.DeltaV >= 0.5) text += ", " + EngineerFormat.Speed(burn.Loss * burn.Line.DeltaV) + " lost";
            return "<color=" + BurnColour(burn) + ">" + text + "</color>";
        }

        // "Kerbin: surface gravity 9.81 m/s², atmosphere 1.00 atm".
        private string ThrustBodyText(ThrustStep entry)
        {
            if (entry == null) return "";
            PlannerBody body = bodies.Bodies[entry.Body];
            double gravity = body.Mu / (body.Radius * body.Radius);
            string air = SurfaceAir(entry.Body) > 0.0
                ? "atmosphere " + SurfaceAir(entry.Body).ToString("0.00", CultureInfo.InvariantCulture) + " atm"
                : "no atmosphere";
            return "<color=" + Dim + ">" + body.Name + ": surface gravity " + gravity.ToString("0.00", CultureInfo.InvariantCulture)
                   + " m/s², " + air + "</color>";
        }

        // "Enough thrust on every step", or the steps where it is too little or a burn
        // too long.
        private string ThrustSummary()
        {
            int low = 0, slow = 0, noted = 0, turning = 0;
            foreach (ThrustStep entry in thrustSteps)
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
