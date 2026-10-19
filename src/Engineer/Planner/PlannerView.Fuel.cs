using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The planner's fuel plan: its rows, and the heading's summary.
    internal sealed partial class PlannerView
    {
        // ------------------------------------------------------------- fuel plan

        // The fuel plan: every step of the route in its order, with who flies it, what
        // it needs with the margin, and what what flies it has left once it is over, in
        // red what it lacks. A step opens to the stages it has with fuel as it starts,
        // each with what it gives there, what it keeps after, its thrust against the
        // weight it pushes, and whether it is used there and dropped after it. A stage
        // gone before a step, dropped or burnt out, is no longer among its stages.
        private const int MaxFuelSteps = 30;
        private const int MaxStepStages = 10;
        private const float FuelNeeds = 72f;
        private const float FuelLeft = 80f;
        private const float StageGives = 72f;
        private const float StageKeeps = 104f;
        private const float StageTwr = 40f;
        private const float UseButton = 48f;
        private readonly SegmentBar[,] fuelSwatches = new SegmentBar[MaxFuelSteps, MaxStepStages];
        // The steps open, by destination and role.
        private readonly HashSet<int> fuelOpen = new HashSet<int>();
        private bool fuelShown;

        private IEnumerable<DialogGUIBase> FuelRows()
        {
            DialogGUIHorizontalLayout names = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline), DimLabel("Step", Place), DimLabel("Flown by", VehicleButton), DimLabel("Needs", FuelNeeds),
                DimLabel("Left after", FuelLeft));
            names.OptionEnabledCondition = () => fuelShown && Worked() && steps.Count > 0;
            yield return names;
            for (int i = 0; i < MaxFuelSteps; i++)
                foreach (DialogGUIBase row in FuelStepRows(i))
                    yield return row;
            DialogGUIHorizontalLayout more = new DialogGUIHorizontalLayout(width, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline),
                new DialogGUILabel(Text(() => "<color=" + Dim + ">" + (steps.Count - MaxFuelSteps) + " more steps</color>"), width - Timeline));
            more.OptionEnabledCondition = () => fuelShown && Worked() && steps.Count > MaxFuelSteps;
            yield return more;
        }

        private static DialogGUILabel DimLabel(string text, float labelWidth)
        {
            return new DialogGUILabel("<color=" + Dim + ">" + text + "</color>", labelWidth);
        }

        private bool FuelStepShown(int step)
        {
            return fuelShown && Worked() && step < steps.Count;
        }

        private int FuelKey(int step)
        {
            return steps[step].Goal * RouteStop.Roles + (int)steps[step].Role;
        }

        private bool FuelIsOpen(int step)
        {
            return FuelStepShown(step) && fuelOpen.Contains(FuelKey(step));
        }

        private void ToggleFuel(int step)
        {
            if (!FuelStepShown(step)) return;
            int key = FuelKey(step);
            if (!fuelOpen.Remove(key)) fuelOpen.Add(key);
            textVersion++;
            UpdateSwatches();
        }

        // A stage row of an open step; null for none, and for every row of a closed
        // one, so that its texts are not written.
        private StageBudget.StepStage FuelStage(int step, int row)
        {
            if (!FuelIsOpen(step) || budget.Stages[step] == null || row >= budget.Stages[step].Count) return null;
            return budget.Stages[step][row];
        }

        private int FuelStageCount(int step)
        {
            return FuelStepShown(step) && budget.Stages[step] != null ? budget.Stages[step].Count : 0;
        }

        // A step's row, its stages' names, and a row per stage while it is open. The
        // columns are those of the steps above: where it goes under a step's place, who
        // flies it under who flies, the button to open it where "..." is.
        private IEnumerable<DialogGUIBase> FuelStepRows(int step)
        {
            DialogGUIButton open = new DialogGUIButton(() => FuelIsOpen(step) ? "<b><color=" + Good + ">-</color></b>" : "+",
                () => ToggleFuel(step), Square, Square, false);
            open.tooltipText = "Show the stages of this step.";
            open.OptionEnabledCondition = () => FuelStepShown(step);
            DialogGUISpace noOpen = new DialogGUISpace(Square);
            noOpen.OptionEnabledCondition = () => !FuelStepShown(step);
            float room = Mathf.Max(0f, width - Timeline - Place - VehicleButton - FuelNeeds - FuelLeft - Square - 4f * 6f - RowEnd);
            DialogGUIHorizontalLayout row = new DialogGUIHorizontalLayout(width, Square + 2f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline), new DialogGUILabel(Text(() => FuelPlace(step)), Place),
                new DialogGUILabel(Text(() => FuelFlyer(step)), VehicleButton), new DialogGUILabel(Text(() => FuelNeedsText(step)), FuelNeeds),
                new DialogGUILabel(Text(() => FuelLeftText(step)), FuelLeft), new DialogGUISpace(room), open, noOpen);
            row.OptionEnabledCondition = () => FuelStepShown(step);
            yield return row;

            DialogGUIHorizontalLayout names = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline + Swatch + 4f), DimLabel("Stage", StageName_), DimLabel("Gives", StageGives), DimLabel("Keeps", StageKeeps),
                DimLabel("TWR", StageTwr));
            names.OptionEnabledCondition = () => FuelIsOpen(step) && FuelStage(step, 0) != null;
            yield return names;

            for (int i = 0; i < MaxStepStages; i++)
                yield return FuelStageRow(step, i);
            // Its RCS, as a stage of its own: it gives the step nothing and keeps the
            // delta-v its propellant gives what flies it.
            DialogGUIHorizontalLayout rcs = new DialogGUIHorizontalLayout(width, Square + 2f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline + Swatch + 4f), DimLabel("RCS", StageName_), DimLabel("0", StageGives),
                new DialogGUILabel(Text(() => RcsAt(step) != null ? EngineerFormat.Speed(RcsAt(step).DeltaV) : ""), StageKeeps));
            rcs.OptionEnabledCondition = () => FuelIsOpen(step) && RcsAt(step) != null;
            yield return rcs;
            DialogGUIHorizontalLayout more = new DialogGUIHorizontalLayout(width, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline + Swatch + 4f),
                new DialogGUILabel(Text(() => "<color=" + Dim + ">" + (FuelStageCount(step) - MaxStepStages) + " more stages</color>"),
                    width - Timeline - Swatch - 4f));
            more.OptionEnabledCondition = () => FuelIsOpen(step) && FuelStageCount(step) > MaxStepStages;
            yield return more;

            // What the step is made of: its burns and the margin; what docks and refills,
            // what stays; and what stands in its way.
            for (int i = 0; i < MaxFuelLines; i++)
            {
                int line = i;
                DialogGUIHorizontalLayout detail = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(),
                    TextAnchor.MiddleLeft, new DialogGUISpace(Timeline), new DialogGUILabel(Text(() => FuelLineName(step, line)), Place + VehicleButton + 4f),
                    new DialogGUILabel(Text(() => FuelLineValue(step, line)), FuelNeeds));
                detail.OptionEnabledCondition = () => FuelIsOpen(step) && line < FuelLines(step).Count;
                yield return detail;
            }
            for (int i = 0; i < MaxWarnings; i++)
            {
                int line = i;
                DialogGUIHorizontalLayout warning = new DialogGUIHorizontalLayout(width, RowHeight, 4f, new RectOffset(),
                    TextAnchor.MiddleLeft, new DialogGUISpace(Timeline),
                    new DialogGUILabel(Text(() => "<color=" + Problem + ">" + FuelWarning(step, line) + "</color>"), width - Timeline - 4f - RowEnd));
                warning.OptionEnabledCondition = () => FuelIsOpen(step) && FuelWarning(step, line).Length > 0;
                yield return warning;
            }
        }

        private const int MaxFuelLines = 8;

        // The lines of an open step, as name and value: each burn, the margin, a
        // refill, what stays there.
        private List<KeyValuePair<string, string>> FuelLines(int step)
        {
            // Asked for every line's label every frame, shown or not.
            if (!FuelIsOpen(step)) return NoLines;
            List<KeyValuePair<string, string>> lines = new List<KeyValuePair<string, string>>();
            RouteStep routeStep = steps[step];
            foreach (LegLine line in routeStep.Leg.Lines)
                lines.Add(new KeyValuePair<string, string>("<color=" + Dim + ">" + line.Name + "</color>", EngineerFormat.Speed(line.DeltaV)));
            if (state.Margin > 0.0 && routeStep.Leg.DeltaV > 0.5)
                lines.Add(new KeyValuePair<string, string>(
                    "<color=" + Dim + ">Margin " + (state.Margin * 100.0).ToString("0", CultureInfo.InvariantCulture) + " %</color>",
                    EngineerFormat.Speed(budget.Needed[step] - routeStep.Leg.DeltaV)));
            if (budget.Refuelled[step] > 1e-3)
                lines.Add(new KeyValuePair<string, string>("<color=" + Good + ">Refuelled from the rest</color>",
                    "<color=" + Good + ">" + budget.Refuelled[step].ToString("0.00", CultureInfo.InvariantCulture) + " t</color>"));
            if (budget.Payload[step] > 1e-3)
                lines.Add(new KeyValuePair<string, string>("<color=" + Good + ">Deployed here</color>",
                    "<color=" + Good + ">" + budget.Payload[step].ToString("0.00", CultureInfo.InvariantCulture) + " t</color>"));
            return lines;
        }

        private static readonly List<KeyValuePair<string, string>> NoLines = new List<KeyValuePair<string, string>>();

        private string FuelLineName(int step, int line)
        {
            List<KeyValuePair<string, string>> lines = FuelLines(step);
            return line < lines.Count ? lines[line].Key : "";
        }

        private string FuelLineValue(int step, int line)
        {
            List<KeyValuePair<string, string>> lines = FuelLines(step);
            return line < lines.Count ? lines[line].Value : "";
        }

        private string FuelWarning(int step, int line)
        {
            if (!FuelIsOpen(step) || step >= warnings.Length || warnings[step] == null || line >= warnings[step].Count) return "";
            return warnings[step][line];
        }

        // A stage of an open step: its swatch in the stage's colour, what it gives here,
        // what it keeps after or held as it was dropped, its thrust against the weight at
        // the body of a launch or a landing; Use and Drop after, where a stop's cross
        // ends.
        private DialogGUIBase FuelStageRow(int step, int row)
        {
            SegmentBar swatch = new SegmentBar(1, RowHeight - 2f, after);
            fuelSwatches[step, row] = swatch;
            System.Func<int> number = () =>
            {
                StageBudget.StepStage stage = FuelStage(step, row);
                return stage != null ? stage.Stage : -1;
            };
            DialogGUIButton use = Choice("Use", () => Burnable(step, row), () => ToggleSaved(step, number()), UseButton);
            use.tooltipText = "Burn this stage in this step. When off, this stage and the ones after it are kept for a later step.";
            // The jets' part of the launch is no stage to keep or drop.
            use.OptionInteractableCondition = () => !AirStage(number());
            // Lit where it holds; to be switched off even where the stage can no longer
            // be dropped, the last one of what flies the step.
            DialogGUIButton drop = Choice("Drop after", () =>
            {
                StageBudget.StepStage stage = FuelStage(step, row);
                return stage != null && stage.CanDrop && DropsAfter(step, stage.Stage);
            }, () => ToggleDropAfter(step, number()), DropButton);
            drop.tooltipText = "Drop this stage and the ones before it at the end of this step, including any fuel left in them.";
            drop.OptionInteractableCondition = () =>
            {
                StageBudget.StepStage stage = FuelStage(step, row);
                return stage != null && !AirStage(stage.Stage) && (stage.CanDrop || DropsAfter(step, stage.Stage));
            };
            float room = Mathf.Max(0f, width - Timeline - Swatch - StageName_ - StageGives - StageKeeps - StageTwr - UseButton - DropButton
                                       - 4f * 8f - RowEnd);
            DialogGUIHorizontalLayout line = new DialogGUIHorizontalLayout(width, Square + 2f, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline), swatch.Layout, new DialogGUILabel(Text(() => ColouredStage(number())), StageName_),
                new DialogGUILabel(Text(() => StageGivesText(step, row)), StageGives), new DialogGUILabel(Text(() => StageKeepsText(step, row)), StageKeeps),
                new DialogGUILabel(Text(() => StageTwrText(step, row)), StageTwr), new DialogGUISpace(room), use, drop);
            line.OptionEnabledCondition = () => FuelIsOpen(step) && FuelStage(step, row) != null;
            return line;
        }

        private bool AirStage(int number)
        {
            StageRow row = allStages.Find(s => s.Number == number);
            return row != null && row.Air;
        }

        // Use is lit where the stage can burn on the step: not kept for later, nor behind
        // a stage kept for later, as KSP fires the stages in order.
        private bool Burnable(int step, int row)
        {
            StageBudget.StepStage stage = FuelStage(step, row);
            if (stage == null || Saved(step, stage.Stage)) return false;
            for (int before = 0; before < row; before++)
            {
                StageBudget.StepStage earlier = FuelStage(step, before);
                if (earlier != null && Saved(step, earlier.Stage)) return false;
            }
            return true;
        }

        // "To Mun orbit".
        private string FuelPlace(int step)
        {
            return FuelStepShown(step) ? StepName(step) : "";
        }

        private string FuelFlyer(int step)
        {
            return FuelStepShown(step) ? StepFlyer(step) : "";
        }

        // "To Mun orbit", and who flies it, for the fuel plan and the thrust section.
        private string StepName(int step)
        {
            return step >= 0 && step < steps.Count ? "To " + RouteSteps.Place(bodies.Bodies, steps[step].To) : "";
        }

        private string StepFlyer(int step)
        {
            return step >= 0 && step < steps.Count ? "<color=" + Dim + ">" + FlyerName(steps[step].Flyer) + "</color>" : "";
        }

        private string FuelNeedsText(int step)
        {
            return FuelStepShown(step) ? EngineerFormat.Speed(budget.Needed[step]) : "";
        }

        // What is left once the step is over, green; in red what the step lacks, or
        // what stands in its way.
        private string FuelLeftText(int step)
        {
            if (!FuelStepShown(step)) return "";
            if (budget.Short[step] > 0.5) return "<color=" + Problem + ">-" + EngineerFormat.Speed(budget.Short[step]) + "</color>";
            if (warnings[step].Count > 0) return "<color=" + Problem + ">" + EngineerFormat.Speed(budget.LeftAfter[step]) + "</color>";
            return "<color=" + Good + ">" + EngineerFormat.Speed(budget.LeftAfter[step]) + "</color>";
        }

        private string StageGivesText(int step, int row)
        {
            StageBudget.StepStage stage = FuelStage(step, row);
            if (stage == null) return "";
            if (stage.Given < 0.5) return "<color=" + Dim + ">0</color>";
            return "<color=" + PlannerColors.Hex(StageColour(stage.Stage)) + ">" + EngineerFormat.Speed(stage.Given) + "</color>";
        }

        // What the stage keeps after the step; in orange what it held as it was dropped.
        private string StageKeepsText(int step, int row)
        {
            StageBudget.StepStage stage = FuelStage(step, row);
            if (stage == null) return "";
            if (stage.Dropped) return "<color=" + Warning + ">dropped, " + EngineerFormat.Speed(stage.LeftAfter) + "</color>";
            if (stage.LeftAfter < 0.5) return "<color=" + Dim + ">empty</color>";
            return EngineerFormat.Speed(stage.LeftAfter);
        }

        // The stage's thrust against the weight it pushes as it starts its part of the
        // step, at the surface of the body it lifts off from or lands on; red below what
        // that needs on the stage that lifts off or sets down, the first of a launch and
        // the last of a landing, as the warnings have it. Nothing for a stage the step
        // does not burn, or away from surfaces.
        private string StageTwrText(int step, int row)
        {
            StageBudget.StepStage stage = FuelStage(step, row);
            if (stage == null) return "";
            RouteStep routeStep = steps[step];
            bool launch = routeStep.From.Kind == StopKind.Surface;
            bool landing = routeStep.To.Kind == StopKind.Surface && !Chutes(routeStep.To);
            if (!launch && !landing) return "";
            StageBudget.StageUse use = budget.Uses.Find(item => item.Leg == step && item.Stage == stage.Stage);
            if (use == null) return "";
            int body = launch ? routeStep.From.Body : routeStep.To.Body;
            double twr = TwrAt(use.Stage, use.Mass, body, true);
            double least = launch && Runway(use.Stage) ? 0.0 : launch && bodies.Bodies[body].HasAtmosphere ? 1.2 : 1.0;
            StageBudget.StageUse first = budget.Uses.Find(item => item.Leg == step);
            StageBudget.StageUse last = budget.Uses.FindLast(item => item.Leg == step);
            bool decides = launch ? use == first : use == last;
            string text = EngineerFormat.Ratio(twr);
            return decides && twr < least ? "<color=" + Problem + ">" + text + "</color>" : "<color=" + Dim + ">" + text + "</color>";
        }

        // "S3" in the colour of its stage.
        // "S4", or "S4+3" for stages that burn together at lift-off (ClampedLaunch).
        private readonly Dictionary<int, string> stageLabels = new Dictionary<int, string>();

        private string StageLabel(int number)
        {
            string label;
            return stageLabels.TryGetValue(number, out label) ? label : "S" + number;
        }

        private string ColouredStage(int number)
        {
            if (number < 0) return "";
            return "<b><color=" + PlannerColors.Hex(StageColour(number)) + ">" + StageLabel(number) + "</color></b>";
        }

        // Each open stage row's swatch in the colour of its stage.
        private void UpdateSwatches()
        {
            for (int step = 0; step < MaxFuelSteps; step++)
                for (int row = 0; row < MaxStepStages; row++)
                {
                    SegmentBar swatch = fuelSwatches[step, row];
                    if (swatch == null) continue;
                    StageBudget.StepStage stage = FuelStage(step, row);
                    swatch.Set(new List<KeyValuePair<float, Color>>
                    {
                        new KeyValuePair<float, Color>(Swatch, stage != null ? StageColour(stage.Stage) : Color.clear),
                    });
                }
        }

        // "Enough fuel for every step", or the steps short of fuel.
        private string FuelSummary()
        {
            int shortSteps = 0;
            foreach (double value in budget.Short) if (value > 0.5) shortSteps++;
            return shortSteps == 0
                ? "<color=" + Good + ">Enough fuel for every step</color>"
                : "<color=" + Problem + ">" + shortSteps + (shortSteps == 1 ? " step" : " steps") + " short of fuel</color>";
        }
    }
}
