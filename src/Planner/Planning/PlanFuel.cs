using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Planner.Engineer;
using static ReDefinition.Planner.Planning.PlanText;

namespace ReDefinition.Planner.Planning
{
    // The planner's fuel plan: every step of the route in its order, with who flies it, what it needs with the margin, and what what flies it has
    // left once it is over. A step has the stages it has with fuel as it starts, each with what it gives there, what it keeps after, and its thrust
    // against the weight it pushes. This class works out those figures and writes their texts; whether a step is open is the rows' business.
    internal sealed class PlanFuel
    {
        // The most lines under an open step: its burns, the margin, a refill and what stays there.
        internal const int MaxFuelLines = 8;

        // No lines, for a step that is not open.
        internal static readonly List<KeyValuePair<string, string>> NoLines = new List<KeyValuePair<string, string>>();

        private readonly PlannerModel model;

        // A step's lines as last made, and the text version they were made for, so that the eight line rows of
        // an open step do not each make them again in every frame.
        private sealed class LinesMade
        {
            public int Version = -1;
            public List<KeyValuePair<string, string>> Lines;
        }

        private readonly Dictionary<int, LinesMade> linesMade = new Dictionary<int, LinesMade>();

        internal PlanFuel(PlannerModel model)
        {
            this.model = model;
        }

        // Whether the plan is worked out and has that step.
        private bool Has(int step)
        {
            return model.Worked() && step >= 0 && step < model.Steps.Count;
        }

        // The step's key among the steps that are open: its destination and role, kept as steps come and go before it.
        internal int Key(int step)
        {
            return model.Steps[step].Goal * RouteStop.Roles + (int)model.Steps[step].Role;
        }

        // A stage row of a step; null for none.
        internal StageBudget.StepStage Stage(int step, int row)
        {
            if (!Has(step) || model.Budget.Stages[step] == null || row >= model.Budget.Stages[step].Count) return null;
            return model.Budget.Stages[step][row];
        }

        // How many stages what flies the step has with fuel as it starts.
        internal int StageCount(int step)
        {
            return Has(step) && model.Budget.Stages[step] != null ? model.Budget.Stages[step].Count : 0;
        }

        // Use is lit where the stage can burn on the step: not kept for later, nor behind
        // a stage kept for later, as KSP fires the stages in order.
        internal bool Burnable(int step, int row)
        {
            StageBudget.StepStage stage = Stage(step, row);
            if (stage == null || model.Saved(step, stage.Stage)) return false;
            for (int before = 0; before < row; before++)
            {
                StageBudget.StepStage earlier = Stage(step, before);
                if (earlier != null && model.Saved(step, earlier.Stage)) return false;
            }
            return true;
        }

        // The lines of a step, as name and value: each burn, the margin, a refill, what stays there. Made again only where the
        // text version has moved on.
        internal List<KeyValuePair<string, string>> Lines(int step)
        {
            if (!Has(step)) return NoLines;
            LinesMade made;
            if (!linesMade.TryGetValue(step, out made)) linesMade[step] = made = new LinesMade();
            if (made.Version != model.Version)
            {
                made.Lines = MakeLines(step);
                made.Version = model.Version;
            }
            return made.Lines;
        }

        // Makes the lines of a step.
        private List<KeyValuePair<string, string>> MakeLines(int step)
        {
            List<KeyValuePair<string, string>> lines = new List<KeyValuePair<string, string>>();
            RouteStep routeStep = model.Steps[step];
            StageBudget.Result budget = model.Budget;
            foreach (LegLine line in routeStep.Leg.Lines)
                lines.Add(new KeyValuePair<string, string>("<color=" + Dim + ">" + line.Name + "</color>", EngineerFormat.Speed(line.DeltaV)));
            if (model.State.Margin > 0.0 && routeStep.Leg.DeltaV > 0.5)
                lines.Add(new KeyValuePair<string, string>(
                    "<color=" + Dim + ">Margin " + (model.State.Margin * 100.0).ToString("0", CultureInfo.InvariantCulture) + " %</color>",
                    EngineerFormat.Speed(budget.Needed[step] - routeStep.Leg.DeltaV)));
            if (budget.Refuelled[step] > 1e-3)
                lines.Add(new KeyValuePair<string, string>("<color=" + Good + ">Refuelled from the rest</color>",
                    "<color=" + Good + ">" + budget.Refuelled[step].ToString("0.00", CultureInfo.InvariantCulture) + " t</color>"));
            if (budget.Payload[step] > 1e-3)
                lines.Add(new KeyValuePair<string, string>("<color=" + Good + ">Deployed here</color>",
                    "<color=" + Good + ">" + budget.Payload[step].ToString("0.00", CultureInfo.InvariantCulture) + " t</color>"));
            return lines;
        }

        // The step's warning on that line; empty where it has fewer.
        internal string WarningText(int step, int line)
        {
            if (step >= model.Warnings.Length || model.Warnings[step] == null || line >= model.Warnings[step].Count) return "";
            return model.Warnings[step][line];
        }

        // What the step needs, with the margin.
        internal string NeedsText(int step)
        {
            return Has(step) ? EngineerFormat.Speed(model.Budget.Needed[step]) : "";
        }

        // What is left once the step is over, green; in red what the step lacks, or
        // what stands in its way.
        internal string LeftText(int step)
        {
            if (!Has(step)) return "";
            StageBudget.Result budget = model.Budget;
            if (budget.Short[step] > 0.5) return "<color=" + Problem + ">-" + EngineerFormat.Speed(budget.Short[step]) + "</color>";
            if (model.Warnings[step].Count > 0) return "<color=" + Problem + ">" + EngineerFormat.Speed(budget.LeftAfter[step]) + "</color>";
            return "<color=" + Good + ">" + EngineerFormat.Speed(budget.LeftAfter[step]) + "</color>";
        }

        // What the stage gives the step, in its colour; a dim 0 for none.
        internal string GivesText(StageBudget.StepStage stage)
        {
            if (stage == null) return "";
            if (stage.Given < 0.5) return "<color=" + Dim + ">0</color>";
            return "<color=" + PlannerColors.Hex(model.StageColour(stage.Stage)) + ">" + EngineerFormat.Speed(stage.Given) + "</color>";
        }

        // What the stage keeps after the step; in orange what it held as it was dropped.
        internal string KeepsText(StageBudget.StepStage stage)
        {
            if (stage == null) return "";
            if (stage.Dropped) return "<color=" + Warning + ">dropped, " + EngineerFormat.Speed(stage.LeftAfter) + "</color>";
            if (stage.LeftAfter < 0.5) return "<color=" + Dim + ">empty</color>";
            return EngineerFormat.Speed(stage.LeftAfter);
        }

        // The stage's thrust against the weight it pushes as it starts its part of the
        // step, at the surface of the body it lifts off from or lands on. The text is red below what
        // that needs on the stage that lifts off or sets down, the first of a launch and
        // the last of a landing, as the warnings have it. It gives nothing for a stage the step
        // does not burn, or away from surfaces.
        internal string TwrText(int step, StageBudget.StepStage stage)
        {
            if (stage == null) return "";
            PlanThrust thrust = model.Thrust;
            RouteStep routeStep = model.Steps[step];
            bool launch = routeStep.From.Kind == StopKind.Surface;
            bool landing = routeStep.To.Kind == StopKind.Surface && !thrust.Chutes(routeStep.To);
            if (!launch && !landing) return "";
            StageBudget.StageUse use = model.Budget.Uses.Find(item => item.Leg == step && item.Stage == stage.Stage);
            if (use == null) return "";
            int body = launch ? routeStep.From.Body : routeStep.To.Body;
            double twr = thrust.TwrAt(use.Stage, use.Mass, body, true);
            double least = thrust.TwrRequired(launch, launch && thrust.Runway(use.Stage), body);
            bool decides = launch ? use == thrust.FirstUse(step) : use == thrust.LastUse(step);
            string text = EngineerFormat.Ratio(twr);
            return decides && twr < least ? "<color=" + Problem + ">" + text + "</color>" : "<color=" + Dim + ">" + text + "</color>";
        }

        // "Enough fuel for every step", or the steps short of fuel.
        internal string Summary()
        {
            int shortSteps = 0;
            foreach (double value in model.Budget.Short) if (value > 0.5) shortSteps++;
            return shortSteps == 0
                ? "<color=" + Good + ">Enough fuel for every step</color>"
                : "<color=" + Problem + ">" + shortSteps + (shortSteps == 1 ? " step" : " steps") + " short of fuel</color>";
        }
    }
}
