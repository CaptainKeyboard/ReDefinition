using System.Collections.Generic;
using UnityEngine;
using static ReDefinition.Planner.Engineer.PlanText;

namespace ReDefinition.Planner.Engineer
{
    // What the plan says of its steps and stages, in the texts the rows show it in (KSP's rich text), and the questions the rows ask of a step.
    internal sealed partial class PlannerModel
    {
        // "To Mun orbit", for the fuel plan and the thrust part.
        internal string StepName(int step)
        {
            return step >= 0 && step < Steps.Count ? "To " + RouteSteps.Place(Bodies.Bodies, Steps[step].To) : "";
        }

        // Who flies the step, dim.
        internal string StepFlyer(int step)
        {
            return step >= 0 && step < Steps.Count ? "<color=" + Dim + ">" + Sections.FlyerName(Steps[step].Flyer) + "</color>" : "";
        }

        // Who flies the step: green, bold and underlined where the player chose it.
        internal string VehicleText(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            if (index < 0) return "";
            string name = Sections.FlyerName(Steps[index].Flyer);
            return Steps[index].Chosen ? "<b><u><color=" + Good + ">" + name + "</color></u></b>" : "<color=" + Good + ">" + name + "</color>";
        }

        // "To Mun surface", "To Kerbin's air", "To Kerbin orbit".
        internal string StepPlace(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            return index < 0 ? "" : "To " + RouteSteps.Place(Bodies.Bodies, Steps[index].To);
        }

        // "The lander separates in Mun orbit", "The lander is left behind on the Mun".
        internal string SeparationText(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            if (index < 0 || !Steps[index].Separates) return "";
            RouteStep step = Steps[index];
            string place = RouteSteps.At(Bodies.Bodies, step.From);
            string leaver = Lower(Sections.FlyerName(Flyer.Alone(step.Leaver)));
            if (step.Flyer.Kind == FlyerKind.Without) return "The " + leaver + " is left behind " + place;
            if (step.LeavesFrom == 0) return "The " + leaver + " separates " + place;
            return "The " + leaver + " separates from the " + Lower(Sections.FlyerName(Flyer.Alone(step.LeavesFrom))) + " " + place;
        }

        // "The satellite is deployed here": the sections deployed at the step's end.
        internal string DeployedText(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            if (index < 0 || Steps[index].Deploys.Count == 0) return "";
            List<string> names = new List<string>();
            foreach (int id in Steps[index].Deploys) names.Add(Lower(Sections.FlyerName(Flyer.Alone(id))));
            return "The " + string.Join(", the ", names.ToArray()) + (names.Count == 1 ? " is deployed here" : " are deployed here");
        }

        // Whether the section flying the step docks with the rest at its end.
        internal bool Docks(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            return index >= 0 && Steps[index].Docks;
        }

        // The way across ends in the air, for a landing on parachutes, or would
        // without "Orbit first".
        internal bool IntoTheAir(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            if (role != StepRole.Transfer || index < 0 || !Shown(leg + 1)) return false;
            RouteStop goal = State.Stops[leg + 1];
            return goal.Kind == StopKind.Surface && goal.UseAtmosphere && Bodies.Bodies[goal.Body].HasAtmosphere;
        }

        // Whether the step has further choices behind "...".
        internal bool HasMore(int leg, StepRole role)
        {
            return Slot(leg, role) >= 0 && (IntoTheAir(leg, role) || (role == StepRole.Transfer && CanSplit(leg)));
        }

        // The way across to the next destination has a burn that leaves and one that
        // arrives (RouteSteps.Splits).
        internal bool CanSplit(int leg)
        {
            return HasBody(leg) && HasBody(leg + 1) && RouteSteps.Splits(Bodies.Bodies, State.Stops[leg], State.Stops[leg + 1]);
        }

        // The step's warning on that line; empty where it has fewer.
        internal string WarningText(int leg, StepRole role, int line)
        {
            int index = Slot(leg, role);
            if (index < 0 || index >= Warnings.Length || Warnings[index] == null || line >= Warnings[index].Count) return "";
            return Warnings[index][line];
        }

        // What the step needs, with the margin; in red where something stands in its way.
        internal string StepSpeed(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            if (index < 0) return "";
            string speed = EngineerFormat.Speed(Budget.Needed[index]);
            if (Warnings[index].Count > 0) speed = "<color=" + Problem + ">" + speed + "</color>";
            return speed;
        }

        // "Mass 45.20 t   Fuel 33.10 t   Δv 5,230 m/s".
        internal string VesselText()
        {
            EditorReadout readout = Host.Readout;
            if (readout == null || !readout.HasShip) return "";
            // The planner's stages: its own, with the jets' part of the launch, where an
            // engine breathes air.
            double deltaV = 0.0;
            foreach (StageRow row in AllStages) deltaV += row.DeltaVVacuum;
            return "<color=" + Dim + ">Mass</color> " + EngineerFormat.Mass(readout.WetMass) + "<pos=34%><color=" + Dim + ">Fuel</color> "
                   + EngineerFormat.Mass(readout.FuelMass) + "<pos=67%><color=" + Dim + ">Δv</color> " + EngineerFormat.Speed(deltaV);
        }

        // "Left +1,200 m/s   Needed 4,500 m/s of 5,700 m/s"; in orange what the stages miss instead of what is left.
        internal string SummaryText()
        {
            if (!Worked()) return "";
            string balance = Budget.TotalShort > 0.5
                ? "<color=" + Warning + ">-" + EngineerFormat.Speed(Budget.TotalShort) + "</color>"
                : "<color=" + Good + ">+" + EngineerFormat.Speed(Budget.TotalLeft) + "</color>";
            return "<color=" + Dim + ">Left</color> <b>" + balance + "</b><pos=40%><color=" + Dim + ">Needed</color> "
                   + EngineerFormat.Speed(TotalNeeded()) + " <color=" + Dim + ">of</color> " + EngineerFormat.Speed(TotalHas());
        }

        // The stage's name: its label where it has one ("Air", "S4+3"), else "S" and its number.
        internal string StageLabel(int number)
        {
            string label;
            return StageLabels.TryGetValue(number, out label) ? label : "S" + number;
        }

        // "S3" in the colour of its stage, bold; empty for no stage.
        internal string ColouredStage(int number)
        {
            if (number < 0) return "";
            return "<b><color=" + PlannerColors.Hex(StageColour(number)) + ">" + StageLabel(number) + "</color></b>";
        }

        // The stage's colour on the bars, by its place among the stages with delta-v; the spare colour for another.
        internal Color StageColour(int number)
        {
            int at = Stages.FindIndex(row => row.Number == number);
            return at >= 0 ? PlannerColors.Stage(at) : PlannerColors.Spare;
        }

        // Whether the stage is the air-breathing engines' row, which cannot be kept or dropped.
        internal bool AirStage(int number)
        {
            StageRow row = AllStages.Find(s => s.Number == number);
            return row != null && row.Air;
        }
    }
}
