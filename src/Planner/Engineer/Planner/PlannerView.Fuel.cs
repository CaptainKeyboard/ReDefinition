using System.Collections.Generic;
using UnityEngine;
using static ReDefinition.Planner.Engineer.PlanText;

namespace ReDefinition.Planner.Engineer
{
    // The planner's fuel plan: its rows. The figures and their texts are the model's (PlanFuel); the view knows which steps are open.
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
            names.OptionEnabledCondition = () => fuelShown && model.Worked() && model.Steps.Count > 0;
            yield return names;
            for (int i = 0; i < MaxFuelSteps; i++)
                foreach (DialogGUIBase row in FuelStepRows(i))
                    yield return row;
            DialogGUIHorizontalLayout more = new GatedRow(width, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline),
                new DialogGUILabel(Text(() => "<color=" + Dim + ">" + (model.Steps.Count - MaxFuelSteps) + " more steps</color>"), width - Timeline));
            more.OptionEnabledCondition = () => fuelShown && model.Worked() && model.Steps.Count > MaxFuelSteps;
            yield return more;
        }

        // A label in the dim colour, for a column's name.
        private static DialogGUILabel DimLabel(string text, float labelWidth)
        {
            return new DialogGUILabel("<color=" + Dim + ">" + text + "</color>", labelWidth);
        }

        // Whether the fuel plan shows the step: open, and the plan worked out.
        private bool FuelStepShown(int step)
        {
            return fuelShown && model.Worked() && step < model.Steps.Count;
        }

        // Whether the step's stages are shown: opened by the player.
        private bool FuelIsOpen(int step)
        {
            return FuelStepShown(step) && fuelOpen.Contains(model.Fuel.Key(step));
        }

        // Opens the step's stages, or folds them.
        private void ToggleFuel(int step)
        {
            if (!FuelStepShown(step)) return;
            int key = model.Fuel.Key(step);
            if (!fuelOpen.Remove(key)) fuelOpen.Add(key);
            model.Touch();
            UpdateSwatches();
        }

        // A stage row of an open step; null for none, and for every row of a closed
        // one, so that its texts are not written.
        private StageBudget.StepStage FuelStage(int step, int row)
        {
            return FuelIsOpen(step) ? model.Fuel.Stage(step, row) : null;
        }

        // How many stages what flies the step has with fuel as it starts.
        private int FuelStageCount(int step)
        {
            return FuelStepShown(step) ? model.Fuel.StageCount(step) : 0;
        }

        // The lines of an open step, as name and value: each burn, the margin, a
        // refill, what stays there.
        private List<KeyValuePair<string, string>> FuelLines(int step)
        {
            return FuelIsOpen(step) ? model.Fuel.Lines(step) : PlanFuel.NoLines;
        }

        // The open step's warning on that line; empty where it has fewer.
        private string FuelWarning(int step, int line)
        {
            return FuelIsOpen(step) ? model.Fuel.WarningText(step, line) : "";
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
            float room = Mathf.Max(0f, StepLineRoom);
            DialogGUIHorizontalLayout row = new GatedRow(width, Square + 2f, Gap, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline), new DialogGUILabel(Text(() => FuelStepShown(step) ? model.StepName(step) : ""), Place),
                new DialogGUILabel(Text(() => FuelStepShown(step) ? model.StepFlyer(step) : ""), VehicleButton),
                new DialogGUILabel(Text(() => FuelStepShown(step) ? model.Fuel.NeedsText(step) : ""), FuelNeeds),
                new DialogGUILabel(Text(() => FuelStepShown(step) ? model.Fuel.LeftText(step) : ""), FuelLeft), new DialogGUISpace(room), open, noOpen);
            row.OptionEnabledCondition = () => FuelStepShown(step);
            yield return row;

            DialogGUIHorizontalLayout names = new GatedRow(width, RowHeight, Gap, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline + Swatch + 4f), DimLabel("Stage", StageName_), DimLabel("Gives", StageGives), DimLabel("Keeps", StageKeeps),
                DimLabel("TWR", StageTwr));
            names.OptionEnabledCondition = () => FuelIsOpen(step) && FuelStage(step, 0) != null;
            yield return names;

            for (int i = 0; i < MaxStepStages; i++)
                yield return FuelStageRow(step, i);
            // Its RCS, as a stage of its own: it gives the step nothing and keeps the
            // delta-v its propellant gives what flies it.
            DialogGUIHorizontalLayout rcs = new GatedRow(width, Square + 2f, Gap, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline + Swatch + 4f), DimLabel("RCS", StageName_), DimLabel("0", StageGives),
                new DialogGUILabel(Text(() => model.Thrust.RcsAt(step) != null ? EngineerFormat.Speed(model.Thrust.RcsAt(step).DeltaV) : ""), StageKeeps));
            rcs.OptionEnabledCondition = () => FuelIsOpen(step) && model.Thrust.RcsAt(step) != null;
            yield return rcs;
            DialogGUIHorizontalLayout more = new GatedRow(width, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline + Swatch + 4f),
                new DialogGUILabel(Text(() => "<color=" + Dim + ">" + (FuelStageCount(step) - MaxStepStages) + " more stages</color>"),
                    width - Timeline - Swatch - 4f));
            more.OptionEnabledCondition = () => FuelIsOpen(step) && FuelStageCount(step) > MaxStepStages;
            yield return more;

            // What the step is made of: its burns and the margin; what docks and refills,
            // what stays; and what stands in its way.
            for (int i = 0; i < PlanFuel.MaxFuelLines; i++)
            {
                int line = i;
                DialogGUIHorizontalLayout detail = new GatedRow(width, RowHeight, Gap, new RectOffset(),
                    TextAnchor.MiddleLeft, new DialogGUISpace(Timeline), new DialogGUILabel(Text(() => FuelLineName(step, line)), Place + VehicleButton + 4f),
                    new DialogGUILabel(Text(() => FuelLineValue(step, line)), FuelNeeds));
                detail.OptionEnabledCondition = () => FuelIsOpen(step) && line < FuelLines(step).Count;
                yield return detail;
            }
            for (int i = 0; i < PlannerModel.MaxWarnings; i++)
            {
                int line = i;
                DialogGUIHorizontalLayout warning = new GatedRow(width, RowHeight, Gap, new RectOffset(),
                    TextAnchor.MiddleLeft, new DialogGUISpace(Timeline),
                    new DialogGUILabel(Text(() => "<color=" + Problem + ">" + FuelWarning(step, line) + "</color>"), width - Timeline - 4f - RowEnd));
                warning.OptionEnabledCondition = () => FuelIsOpen(step) && FuelWarning(step, line).Length > 0;
                yield return warning;
            }
        }

        // The name of the open step's line; empty where it has fewer.
        private string FuelLineName(int step, int line)
        {
            List<KeyValuePair<string, string>> lines = FuelLines(step);
            return line < lines.Count ? lines[line].Key : "";
        }

        // The value of the open step's line; empty where it has fewer.
        private string FuelLineValue(int step, int line)
        {
            List<KeyValuePair<string, string>> lines = FuelLines(step);
            return line < lines.Count ? lines[line].Value : "";
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
                StageBudget.StepStage stage = FuelStage(step, row);
                return stage != null ? stage.Stage : -1;
            };
            DialogGUIButton use = Choice("Use", () => FuelIsOpen(step) && model.Fuel.Burnable(step, row), () => model.ToggleSaved(step, number()), UseButton);
            use.tooltipText = "Burn this stage in this step. When off, this stage and the ones after it are kept for a later step.";
            // The jets' part of the launch is no stage to keep or drop.
            use.OptionInteractableCondition = () => !model.AirStage(number());
            // Lit where it holds; to be switched off even where the stage can no longer
            // be dropped, the last one of what flies the step.
            DialogGUIButton drop = Choice("Drop after", () =>
            {
                StageBudget.StepStage stage = FuelStage(step, row);
                return stage != null && stage.CanDrop && model.DropsAfter(step, stage.Stage);
            }, () => model.ToggleDropAfter(step, number()), DropButton);
            drop.tooltipText = "Drop this stage and the ones before it at the end of this step, including any fuel left in them.";
            drop.OptionInteractableCondition = () =>
            {
                StageBudget.StepStage stage = FuelStage(step, row);
                return stage != null && !model.AirStage(stage.Stage) && (stage.CanDrop || model.DropsAfter(step, stage.Stage));
            };
            float room = Mathf.Max(0f, width - Timeline - Swatch - StageName_ - StageGives - StageKeeps - StageTwr - UseButton - DropButton
                                       - Gap * 8f - RowEnd);
            DialogGUIHorizontalLayout line = new GatedRow(width, Square + 2f, Gap, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline), swatch.Layout, new DialogGUILabel(Text(() => model.ColouredStage(number())), StageName_),
                new DialogGUILabel(Text(() => model.Fuel.GivesText(FuelStage(step, row))), StageGives),
                new DialogGUILabel(Text(() => model.Fuel.KeepsText(FuelStage(step, row))), StageKeeps),
                new DialogGUILabel(Text(() => model.Fuel.TwrText(step, FuelStage(step, row))), StageTwr), new DialogGUISpace(room), use, drop);
            line.OptionEnabledCondition = () => FuelIsOpen(step) && FuelStage(step, row) != null;
            return line;
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
                        new KeyValuePair<float, Color>(Swatch, stage != null ? model.StageColour(stage.Stage) : Color.clear),
                    });
                }
        }
    }
}
