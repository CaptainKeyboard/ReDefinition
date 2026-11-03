using System.Collections.Generic;
using ReDefinition.Window;
using UnityEngine;
using static ReDefinition.Engineer.PlanText;

namespace ReDefinition.Engineer
{
    // The planner's thrust section: its rows. The checks and their texts are the model's (PlanThrust); the view knows which steps are open.
    internal sealed partial class PlannerView
    {
        // ---------------------------------------------------------------- thrust

        // Whether the thrust does the job, for every step that burns. Each step shows
        // what it needs and what it has. Open, a step shows each stage it burns, with its TWR as it
        // starts and as it ends its part (in the air where the body has any), its thrust
        // and how long it burns. After the stages come its RCS, each burn in orbit, its notes and the body.
        private const float MassColumn = 60f;
        private const float IspColumn = 44f;
        private const float TwrColumn = 38f;
        private const float ThrustColumn = 60f;
        private const float BurnColumn = 64f;
        private const int MaxNotes = 5;
        private const float BurnName = 180f;
        private const float TurnColumn = 56f;

        private readonly HashSet<int> thrustOpen = new HashSet<int>();
        private bool thrustShown;

        // The Thrust part's rows: the columns' names, a line where nothing burns, and each step's rows.
        private IEnumerable<DialogGUIBase> ThrustRows()
        {
            List<DialogGUIBase> heads = new List<DialogGUIBase>
            {
                new DialogGUISpace(Timeline), DimLabel("Step", Place), DimLabel("Flown by", VehicleButton), DimLabel("Needs", FuelNeeds),
                DimLabel("Has", FuelLeft),
            };
            if (TurnFits) heads.Add(DimLabel("Turns 90°", TurnColumn));
            DialogGUIHorizontalLayout names = new GatedRow(width, RowHeight, Gap, new RectOffset(), TextAnchor.MiddleLeft,
                heads.ToArray());
            names.OptionEnabledCondition = () => thrustShown && model.Worked() && model.Thrust.Entries.Count > 0;
            yield return names;
            DialogGUIHorizontalLayout none = new GatedRow(width, RowHeight, 0f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline), DimLabel("No burns on the route.", width - Timeline));
            none.OptionEnabledCondition = () => thrustShown && model.Worked() && model.Thrust.Entries.Count == 0;
            yield return none;
            for (int i = 0; i < MaxFuelSteps; i++)
                foreach (DialogGUIBase row in ThrustStepRows(i))
                    yield return row;
        }

        // The entry of a row; null for none.
        private ThrustStep ThrustAt(int row)
        {
            return thrustShown ? model.Thrust.Entry(row) : null;
        }

        // The entry of an open row; null for none, and for a closed one, so that its
        // texts are not written.
        private ThrustStep OpenThrust(int row)
        {
            ThrustStep entry = ThrustAt(row);
            return entry != null && thrustOpen.Contains(model.Fuel.Key(entry.Step)) ? entry : null;
        }

        // Opens the row's stages and burns, or folds them.
        private void ToggleThrust(int row)
        {
            ThrustStep entry = ThrustAt(row);
            if (entry == null) return;
            int key = model.Fuel.Key(entry.Step);
            if (!thrustOpen.Remove(key)) thrustOpen.Add(key);
            model.Touch();
        }

        // A step's line and, while it is open, its stages, its RCS, its burns in orbit, its notes and its body.
        private IEnumerable<DialogGUIBase> ThrustStepRows(int row)
        {
            PlanThrust thrust = model.Thrust;
            DialogGUIButton open = new DialogGUIButton(() => OpenThrust(row) != null ? "<b><color=" + Good + ">-</color></b>" : "+",
                () => ToggleThrust(row), Square, Square, false);
            open.tooltipText = "Show the stages of this step with their thrust, and each burn.";
            float room = Mathf.Max(0f, StepLineRoom - (TurnFits ? TurnColumn + Gap : 0f));
            List<DialogGUIBase> cells = new List<DialogGUIBase>
            {
                new DialogGUISpace(Timeline), new DialogGUILabel(Text(() => ThrustAt(row) != null ? model.StepName(ThrustAt(row).Step) : ""), Place),
                new DialogGUILabel(Text(() => ThrustAt(row) != null ? model.StepFlyer(ThrustAt(row).Step) : ""), VehicleButton),
                new DialogGUILabel(Text(() => thrust.NeedsText(ThrustAt(row))), FuelNeeds), new DialogGUILabel(Text(() => thrust.HasText(ThrustAt(row))), FuelLeft),
            };
            if (TurnFits) cells.Add(new DialogGUILabel(Text(() => thrust.TurnText(ThrustAt(row))), TurnColumn));
            cells.Add(new DialogGUISpace(room));
            cells.Add(open);
            DialogGUIHorizontalLayout line = new GatedRow(width, Square + 2f, Gap, new RectOffset(), TextAnchor.MiddleLeft,
                cells.ToArray());
            line.OptionEnabledCondition = () => ThrustAt(row) != null;
            yield return line;

            DialogGUIHorizontalLayout names = new GatedRow(width, RowHeight, Gap, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline + Swatch + 4f), DimLabel("Stage", StageName_), DimLabel("Mass", MassColumn),
                DimLabel("Isp", IspColumn), DimLabel("Start", TwrColumn), DimLabel("End", TwrColumn), DimLabel("In air", TwrColumn),
                DimLabel("Thrust", ThrustColumn), DimLabel("Burns", BurnColumn));
            names.OptionEnabledCondition = () => OpenThrust(row) != null && OpenThrust(row).Uses.Count > 0;
            yield return names;

            for (int i = 0; i < MaxStepStages; i++)
            {
                int stage = i;
                DialogGUIHorizontalLayout use = new GatedRow(width, RowHeight + 2f, Gap, new RectOffset(),
                    TextAnchor.MiddleLeft, new DialogGUISpace(Timeline + Swatch + 4f),
                    new DialogGUILabel(Text(() => thrust.StageText(OpenThrust(row), stage)), StageName_),
                    new DialogGUILabel(Text(() => thrust.MassText(OpenThrust(row), stage)), MassColumn),
                    new DialogGUILabel(Text(() => thrust.IspText(OpenThrust(row), stage)), IspColumn),
                    new DialogGUILabel(Text(() => thrust.TwrValue(OpenThrust(row), stage, true, false)), TwrColumn),
                    new DialogGUILabel(Text(() => thrust.TwrValue(OpenThrust(row), stage, false, false)), TwrColumn),
                    new DialogGUILabel(Text(() => thrust.TwrValue(OpenThrust(row), stage, true, true)), TwrColumn),
                    new DialogGUILabel(Text(() => thrust.ThrustText(OpenThrust(row), stage)), ThrustColumn),
                    new DialogGUILabel(Text(() => thrust.StageBurnText(OpenThrust(row), stage)), BurnColumn));
                use.OptionEnabledCondition = () => OpenThrust(row) != null && stage < OpenThrust(row).Uses.Count;
                yield return use;
            }

            // Its RCS, as a stage of its own.
            DialogGUIHorizontalLayout rcs = new GatedRow(width, RowHeight + 2f, Gap, new RectOffset(),
                TextAnchor.MiddleLeft, new DialogGUISpace(Timeline + Swatch + 4f), DimLabel("RCS", StageName_),
                new DialogGUILabel(Text(() => thrust.RcsCell(OpenThrust(row), info => EngineerFormat.Mass(info.Mass))), MassColumn),
                new DialogGUILabel(Text(() => thrust.RcsCell(OpenThrust(row), info => EngineerFormat.Isp(info.Isp))), IspColumn),
                new DialogGUILabel(Text(() => thrust.RcsCell(OpenThrust(row), info => PlanThrust.RcsRatio(info.StartTwr))), TwrColumn),
                new DialogGUILabel(Text(() => thrust.RcsCell(OpenThrust(row), info => PlanThrust.RcsRatio(info.EndTwr))), TwrColumn),
                new DialogGUISpace(TwrColumn),
                new DialogGUILabel(Text(() => thrust.RcsCell(OpenThrust(row), info => EngineerFormat.Force(info.Thrust))), ThrustColumn),
                new DialogGUILabel(Text(() => thrust.RcsCell(OpenThrust(row), info => EngineerFormat.Time(info.Seconds))), BurnColumn));
            rcs.OptionEnabledCondition = () => OpenThrust(row) != null && thrust.RcsAt(OpenThrust(row).Step) != null;
            yield return rcs;

            for (int i = 0; i < PlanFuel.MaxFuelLines; i++)
            {
                int burn = i;
                DialogGUIHorizontalLayout text = new GatedRow(width, RowHeight, Gap, new RectOffset(), TextAnchor.MiddleLeft,
                    new DialogGUISpace(Timeline + Swatch + 4f), new DialogGUILabel(Text(() => thrust.BurnNameText(OpenThrust(row), burn)), BurnName),
                    new DialogGUILabel(Text(() => thrust.BurnText(OpenThrust(row), burn)), width - Timeline - Swatch - 4f - BurnName - Gap * 2f - RowEnd));
                text.OptionEnabledCondition = () => OpenThrust(row) != null && burn < OpenThrust(row).Burns.Count;
                yield return text;
            }

            for (int i = 0; i < MaxNotes; i++)
            {
                int note = i;
                DialogGUIHorizontalLayout text = new GatedRow(width, RowHeight, Gap, new RectOffset(), TextAnchor.MiddleLeft,
                    new DialogGUISpace(Timeline + Swatch + 4f),
                    new DialogGUILabel(Text(() => OpenThrust(row) != null && note < OpenThrust(row).Notes.Count ? OpenThrust(row).Notes[note] : ""),
                        width - Timeline - Swatch - 4f - 4f - RowEnd));
                text.OptionEnabledCondition = () => OpenThrust(row) != null && note < OpenThrust(row).Notes.Count;
                yield return text;
            }

            DialogGUIHorizontalLayout body = new GatedRow(width, RowHeight, Gap, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUISpace(Timeline + Swatch + 4f),
                new DialogGUILabel(Text(() => thrust.BodyText(OpenThrust(row))), width - Timeline - Swatch - 4f - 4f - RowEnd));
            body.OptionEnabledCondition = () => OpenThrust(row) != null;
            yield return body;
        }

        // What a step's line leaves beside its cells and its button; the turn's
        // column stands there where it fits, else its note alone says it.
        private float StepLineRoom
        {
            get { return width - Timeline - Place - VehicleButton - FuelNeeds - FuelLeft - Square - Gap * 6f - RowEnd; }
        }

        // Whether the turn's column fits on a step's line.
        private bool TurnFits
        {
            get { return StepLineRoom >= TurnColumn + Gap; }
        }
    }
}
