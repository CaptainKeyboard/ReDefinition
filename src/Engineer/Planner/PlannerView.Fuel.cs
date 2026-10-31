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
        private const float FuelNeeds = 72f;
        private const float FuelLeft = 80f;
        private const float StageGives = 72f;
        private const float StageKeeps = 104f;
        private const float StageTwr = 40f;
        private const float UseButton = 48f;
        private readonly SegmentBar[,] fuelSwatches;
        // The steps open, by destination and role.
        private readonly HashSet<int> fuelOpen = new HashSet<int>();
        private bool fuelShown;

        // The fuel plan's rows: the columns' names, each step's rows, and how many steps more than it shows.
        private IEnumerable<DialogGUIBase> FuelRows()
        {
            DialogGUIHorizontalLayout names = new GatedRow(width, RowHeight, Gap, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline), DimLabel("Step", Place), DimLabel("Flown by", VehicleButton), DimLabel("Needs", FuelNeeds),
                DimLabel("Left after", FuelLeft));
            names.OptionEnabledCondition = () => fuelShown && Worked() && steps.Count > 0;
            yield return names;
            for (int i = 0; i < MaxFuelSteps; i++)
                foreach (DialogGUIBase row in FuelStepRows(i))
                    yield return row;
            DialogGUIHorizontalLayout more = new GatedRow(width, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline),
                new DialogGUILabel(Text(() => "<color=" + Dim + ">" + (steps.Count - MaxFuelSteps) + " more steps</color>"), width - Timeline));
            more.OptionEnabledCondition = () => fuelShown && Worked() && steps.Count > MaxFuelSteps;
            yield return more;
        }

        // A label in the dim colour, for a column's name.
        private static DialogGUILabel DimLabel(string text, float labelWidth)
        {
            return new DialogGUILabel("<color=" + Dim + ">" + text + "</color>", labelWidth);
        }

        // Whether the fuel plan shows the step: open, or every row asked for by the dashboard, and the plan worked out.
        private bool FuelStepShown(int step, RowScope scope)
        {
            return (fuelShown || scope == RowScope.Every) && Worked() && step < steps.Count;
        }

        // The step's key in fuelOpen: its destination and role, kept as steps come and go before it.
        private int FuelKey(int step)
        {
            return steps[step].Goal * RouteStop.Roles + (int)steps[step].Role;
        }

        // Whether the step's stages are shown: opened by the player, or every row asked for.
        private bool FuelIsOpen(int step, RowScope scope)
        {
            return FuelStepShown(step, scope) && (scope == RowScope.Every || fuelOpen.Contains(FuelKey(step)));
        }

        // Opens the step's stages, or folds them.
        private void ToggleFuel(int step)
        {
            if (!FuelStepShown(step, RowScope.Shown)) return;
            int key = FuelKey(step);
            if (!fuelOpen.Remove(key)) fuelOpen.Add(key);
            textVersion++;
            UpdateSwatches();
        }

        // A stage row of an open step; null for none, and for every row of a closed
        // one, so that its texts are not written.
        private StageBudget.StepStage FuelStage(int step, int row, RowScope scope)
        {
            if (!FuelIsOpen(step, scope) || budget.Stages[step] == null || row >= budget.Stages[step].Count) return null;
            return budget.Stages[step][row];
        }

        // How many stages what flies the step has with fuel as it starts.
        private int FuelStageCount(int step, RowScope scope)
        {
            return FuelStepShown(step, scope) && budget.Stages[step] != null ? budget.Stages[step].Count : 0;
        }

        // A step's row, its stages' names, and a row per stage while it is open. The
        // columns are those of the steps above: where it goes under a step's place, who
        // flies it under who flies, the button to open it where "..." is.
        private IEnumerable<DialogGUIBase> FuelStepRows(int step)
        {
            DialogGUIButton open = new DialogGUIButton(() => FuelIsOpen(step, RowScope.Shown) ? "<b><color=" + Good + ">-</color></b>" : "+",
                () => ToggleFuel(step), Square, Square, false);
            open.tooltipText = "Show the stages of this step.";
            open.OptionEnabledCondition = () => FuelStepShown(step, RowScope.Shown);
            DialogGUISpace noOpen = new DialogGUISpace(Square);
            noOpen.OptionEnabledCondition = () => !FuelStepShown(step, RowScope.Shown);
            float room = Mathf.Max(0f, StepLineRoom);
            DialogGUIHorizontalLayout row = new GatedRow(width, Square + 2f, Gap, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline), new DialogGUILabel(Text(() => FuelPlace(step, RowScope.Shown)), Place),
                new DialogGUILabel(Text(() => FuelFlyer(step, RowScope.Shown)), VehicleButton), new DialogGUILabel(Text(() => FuelNeedsText(step, RowScope.Shown)), FuelNeeds),
                new DialogGUILabel(Text(() => FuelLeftText(step, RowScope.Shown)), FuelLeft), new DialogGUISpace(room), open, noOpen);
            row.OptionEnabledCondition = () => FuelStepShown(step, RowScope.Shown);
            yield return row;

            DialogGUIHorizontalLayout names = new GatedRow(width, RowHeight, Gap, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline + Swatch + 4f), DimLabel("Stage", StageName_), DimLabel("Gives", StageGives), DimLabel("Keeps", StageKeeps),
                DimLabel("TWR", StageTwr));
            names.OptionEnabledCondition = () => FuelIsOpen(step, RowScope.Shown) && FuelStage(step, 0, RowScope.Shown) != null;
            yield return names;

            for (int i = 0; i < MaxStepStages; i++)
                yield return FuelStageRow(step, i);
            // Its RCS, as a stage of its own: it gives the step nothing and keeps the
            // delta-v its propellant gives what flies it.
            DialogGUIHorizontalLayout rcs = new GatedRow(width, Square + 2f, Gap, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline + Swatch + 4f), DimLabel("RCS", StageName_), DimLabel("0", StageGives),
                new DialogGUILabel(Text(() => RcsAt(step) != null ? EngineerFormat.Speed(RcsAt(step).DeltaV) : ""), StageKeeps));
            rcs.OptionEnabledCondition = () => FuelIsOpen(step, RowScope.Shown) && RcsAt(step) != null;
            yield return rcs;
            DialogGUIHorizontalLayout more = new GatedRow(width, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline + Swatch + 4f),
                new DialogGUILabel(Text(() => "<color=" + Dim + ">" + (FuelStageCount(step, RowScope.Shown) - MaxStepStages) + " more stages</color>"),
                    width - Timeline - Swatch - 4f));
            more.OptionEnabledCondition = () => FuelIsOpen(step, RowScope.Shown) && FuelStageCount(step, RowScope.Shown) > MaxStepStages;
            yield return more;

            // What the step is made of: its burns and the margin; what docks and refills,
            // what stays; and what stands in its way.
            for (int i = 0; i < MaxFuelLines; i++)
            {
                int line = i;
                DialogGUIHorizontalLayout detail = new GatedRow(width, RowHeight, Gap, new RectOffset(),
                    TextAnchor.MiddleLeft, new DialogGUISpace(Timeline), new DialogGUILabel(Text(() => FuelLineName(step, line, RowScope.Shown)), Place + VehicleButton + 4f),
                    new DialogGUILabel(Text(() => FuelLineValue(step, line, RowScope.Shown)), FuelNeeds));
                detail.OptionEnabledCondition = () => FuelIsOpen(step, RowScope.Shown) && line < FuelLines(step, RowScope.Shown).Count;
                yield return detail;
            }
            for (int i = 0; i < MaxWarnings; i++)
            {
                int line = i;
                DialogGUIHorizontalLayout warning = new GatedRow(width, RowHeight, Gap, new RectOffset(),
                    TextAnchor.MiddleLeft, new DialogGUISpace(Timeline),
                    new DialogGUILabel(Text(() => "<color=" + Problem + ">" + FuelWarning(step, line, RowScope.Shown) + "</color>"), width - Timeline - 4f - RowEnd));
                warning.OptionEnabledCondition = () => FuelIsOpen(step, RowScope.Shown) && FuelWarning(step, line, RowScope.Shown).Length > 0;
                yield return warning;
            }
        }

        // The most lines under an open step: its burns, the margin, a refill and what stays there.
        private const int MaxFuelLines = 8;

        // The lines of an open step, as name and value: each burn, the margin, a
        // refill, what stays there.
        private List<KeyValuePair<string, string>> FuelLines(int step, RowScope scope)
        {
            // Asked for every line's label every frame, shown or not.
            if (!FuelIsOpen(step, scope)) return NoLines;
            FuelLinesMade made;
            if (!fuelLines.TryGetValue(step, out made)) fuelLines[step] = made = new FuelLinesMade();
            if (made.Version != textVersion || made.Scope != scope)
            {
                made.Lines = MakeFuelLines(step);
                made.Version = textVersion;
                made.Scope = scope;
            }
            return made.Lines;
        }

        // A step's lines as last made, and the text version and scope they were made for, so that the eight line rows of
        // an open step do not each make them again in every frame.
        private sealed class FuelLinesMade
        {
            public int Version = -1;
            public RowScope Scope;
            public List<KeyValuePair<string, string>> Lines;
        }

        private readonly Dictionary<int, FuelLinesMade> fuelLines = new Dictionary<int, FuelLinesMade>();

        // Makes the lines of an open step.
        private List<KeyValuePair<string, string>> MakeFuelLines(int step)
        {
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

        // No lines, for a step that is not open.
        private static readonly List<KeyValuePair<string, string>> NoLines = new List<KeyValuePair<string, string>>();

        // The name of the open step's line; empty where it has fewer.
        private string FuelLineName(int step, int line, RowScope scope)
        {
            List<KeyValuePair<string, string>> lines = FuelLines(step, scope);
            return line < lines.Count ? lines[line].Key : "";
        }

        // The value of the open step's line; empty where it has fewer.
        private string FuelLineValue(int step, int line, RowScope scope)
        {
            List<KeyValuePair<string, string>> lines = FuelLines(step, scope);
            return line < lines.Count ? lines[line].Value : "";
        }

        // The open step's warning on that line; empty where it has fewer.
        private string FuelWarning(int step, int line, RowScope scope)
        {
            if (!FuelIsOpen(step, scope) || step >= warnings.Length || warnings[step] == null || line >= warnings[step].Count) return "";
            return warnings[step][line];
        }

        // A stage of an open step. The row holds its swatch in the stage's colour, what it gives here,
        // and what it keeps after or held as it was dropped. It holds its thrust against the weight at
        // the body of a launch or a landing. Use and Drop come after, where a stop's cross
        // ends.
        private DialogGUIBase FuelStageRow(int step, int row)
        {
            SegmentBar swatch = new SegmentBar(1, RowHeight - 2f, after);
            fuelSwatches[step, row] = swatch;
            System.Func<int> number = () =>
            {
                StageBudget.StepStage stage = FuelStage(step, row, RowScope.Shown);
                return stage != null ? stage.Stage : -1;
            };
            DialogGUIButton use = Choice("Use", () => Burnable(step, row, RowScope.Shown), () => ToggleSaved(step, number()), UseButton);
            use.tooltipText = "Burn this stage in this step. When off, this stage and the ones after it are kept for a later step.";
            // The jets' part of the launch is no stage to keep or drop.
            use.OptionInteractableCondition = () => !AirStage(number());
            // Lit where it holds; to be switched off even where the stage can no longer
            // be dropped, the last one of what flies the step.
            DialogGUIButton drop = Choice("Drop after", () =>
            {
                StageBudget.StepStage stage = FuelStage(step, row, RowScope.Shown);
                return stage != null && stage.CanDrop && DropsAfter(step, stage.Stage);
            }, () => ToggleDropAfter(step, number()), DropButton);
            drop.tooltipText = "Drop this stage and the ones before it at the end of this step, including any fuel left in them.";
            drop.OptionInteractableCondition = () =>
            {
                StageBudget.StepStage stage = FuelStage(step, row, RowScope.Shown);
                return stage != null && !AirStage(stage.Stage) && (stage.CanDrop || DropsAfter(step, stage.Stage));
            };
            float room = Mathf.Max(0f, width - Timeline - Swatch - StageName_ - StageGives - StageKeeps - StageTwr - UseButton - DropButton
                                       - Gap * 8f - RowEnd);
            DialogGUIHorizontalLayout line = new GatedRow(width, Square + 2f, Gap, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline), swatch.Layout, new DialogGUILabel(Text(() => ColouredStage(number())), StageName_),
                new DialogGUILabel(Text(() => StageGivesText(step, row, RowScope.Shown)), StageGives), new DialogGUILabel(Text(() => StageKeepsText(step, row, RowScope.Shown)), StageKeeps),
                new DialogGUILabel(Text(() => StageTwrText(step, row, RowScope.Shown)), StageTwr), new DialogGUISpace(room), use, drop);
            line.OptionEnabledCondition = () => FuelIsOpen(step, RowScope.Shown) && FuelStage(step, row, RowScope.Shown) != null;
            return line;
        }

        // Whether the stage is the air-breathing engines' row, which cannot be kept or dropped.
        private bool AirStage(int number)
        {
            StageRow row = allStages.Find(s => s.Number == number);
            return row != null && row.Air;
        }

        // Use is lit where the stage can burn on the step: not kept for later, nor behind
        // a stage kept for later, as KSP fires the stages in order.
        private bool Burnable(int step, int row, RowScope scope)
        {
            StageBudget.StepStage stage = FuelStage(step, row, scope);
            if (stage == null || Saved(step, stage.Stage)) return false;
            for (int before = 0; before < row; before++)
            {
                StageBudget.StepStage earlier = FuelStage(step, before, scope);
                if (earlier != null && Saved(step, earlier.Stage)) return false;
            }
            return true;
        }

        // "To Mun orbit".
        // Where the step goes, while the fuel plan shows it.
        private string FuelPlace(int step, RowScope scope)
        {
            return FuelStepShown(step, scope) ? StepName(step) : "";
        }

        // Who flies the step, while the fuel plan shows it.
        private string FuelFlyer(int step, RowScope scope)
        {
            return FuelStepShown(step, scope) ? StepFlyer(step) : "";
        }

        // "To Mun orbit", and who flies it, for the fuel plan and the thrust section.
        private string StepName(int step)
        {
            return step >= 0 && step < steps.Count ? "To " + RouteSteps.Place(bodies.Bodies, steps[step].To) : "";
        }

        // Who flies the step, dim.
        private string StepFlyer(int step)
        {
            return step >= 0 && step < steps.Count ? "<color=" + Dim + ">" + FlyerName(steps[step].Flyer) + "</color>" : "";
        }

        // What the step needs, with the margin, while the fuel plan shows it.
        private string FuelNeedsText(int step, RowScope scope)
        {
            return FuelStepShown(step, scope) ? EngineerFormat.Speed(budget.Needed[step]) : "";
        }

        // What is left once the step is over, green; in red what the step lacks, or
        // what stands in its way.
        private string FuelLeftText(int step, RowScope scope)
        {
            if (!FuelStepShown(step, scope)) return "";
            if (budget.Short[step] > 0.5) return "<color=" + Problem + ">-" + EngineerFormat.Speed(budget.Short[step]) + "</color>";
            if (warnings[step].Count > 0) return "<color=" + Problem + ">" + EngineerFormat.Speed(budget.LeftAfter[step]) + "</color>";
            return "<color=" + Good + ">" + EngineerFormat.Speed(budget.LeftAfter[step]) + "</color>";
        }

        // What the stage gives the step, in its colour; a dim 0 for none.
        private string StageGivesText(int step, int row, RowScope scope)
        {
            StageBudget.StepStage stage = FuelStage(step, row, scope);
            if (stage == null) return "";
            if (stage.Given < 0.5) return "<color=" + Dim + ">0</color>";
            return "<color=" + PlannerColors.Hex(StageColour(stage.Stage)) + ">" + EngineerFormat.Speed(stage.Given) + "</color>";
        }

        // What the stage keeps after the step; in orange what it held as it was dropped.
        private string StageKeepsText(int step, int row, RowScope scope)
        {
            StageBudget.StepStage stage = FuelStage(step, row, scope);
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
        private string StageTwrText(int step, int row, RowScope scope)
        {
            StageBudget.StepStage stage = FuelStage(step, row, scope);
            if (stage == null) return "";
            RouteStep routeStep = steps[step];
            bool launch = routeStep.From.Kind == StopKind.Surface;
            bool landing = routeStep.To.Kind == StopKind.Surface && !Chutes(routeStep.To);
            if (!launch && !landing) return "";
            StageBudget.StageUse use = budget.Uses.Find(item => item.Leg == step && item.Stage == stage.Stage);
            if (use == null) return "";
            int body = launch ? routeStep.From.Body : routeStep.To.Body;
            double twr = TwrAt(use.Stage, use.Mass, body, true);
            double least = TwrRequired(launch, launch && Runway(use.Stage), body);
            bool decides = launch ? use == FirstUse(step) : use == LastUse(step);
            string text = EngineerFormat.Ratio(twr);
            return decides && twr < least ? "<color=" + Problem + ">" + text + "</color>" : "<color=" + Dim + ">" + text + "</color>";
        }

        // "S4", or "S4+3" for stages that burn together at lift-off (ClampedLaunch).
        private readonly Dictionary<int, string> stageLabels = new Dictionary<int, string>();

        // The stage's name: its label where it has one ("Air", "S4+3"), else "S" and its number.
        private string StageLabel(int number)
        {
            string label;
            return stageLabels.TryGetValue(number, out label) ? label : "S" + number;
        }

        // "S3" in the colour of its stage, bold; empty for no stage.
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
                    StageBudget.StepStage stage = FuelStage(step, row, RowScope.Shown);
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
