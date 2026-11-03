using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Engineer;

namespace ReDefinition.Tests
{
    // The planner's fuel plan (PlanFuel): the figures and texts of a launch from Kerbin's surface to its orbit on a vessel of one stage
    // with `deltaV` m/s, and of stages that are made up by hand.
    [TestClass]
    public class PlanFuelTests
    {
        // The vessel's one stage with plenty of thrust.
        private static PlannerModel Launch(double deltaV)
        {
            return PlannerModelFixture.Model(
                PlannerModelFixture.OneStage(deltaV, 200.0, 180.0), PlannerModelFixture.ToKerbinOrbit());
        }

        // A vessel that has more delta-v than the step needs has some left after it, in green.
        [TestMethod]
        public void AVesselWithDeltaVToSpareHasSomeLeftInGreen()
        {
            PlannerModel model = Launch(6000.0);
            PlanFuel fuel = model.Fuel;

            Assert.AreEqual(6000.0 - model.Budget.Needed[0], model.Budget.LeftAfter[0], 1e-6);
            Assert.AreEqual("<color=#8fdc7a>Enough fuel for every step</color>", fuel.Summary());
            Assert.AreEqual(Speed(model.Budget.Needed[0]), fuel.NeedsText(0));
            Assert.AreEqual("<color=#8fdc7a>" + Speed(6000.0 - model.Budget.Needed[0]) + "</color>", fuel.LeftText(0));
        }

        // A vessel that has less than the step needs lacks the difference, in red, and the summary counts the steps that lack some.
        [TestMethod]
        public void AVesselShortOfDeltaVLacksTheDifferenceInRed()
        {
            PlannerModel model = Launch(1000.0);
            PlanFuel fuel = model.Fuel;

            Assert.AreEqual(model.Budget.Needed[0] - 1000.0, model.Budget.Short[0], 1e-6);
            Assert.AreEqual("<color=#e6474d>-" + Speed(model.Budget.Short[0]) + "</color>", fuel.LeftText(0));
            Assert.AreEqual("<color=#e6474d>1 step short of fuel</color>", fuel.Summary());
        }

        // The step's lines end with the margin and what it adds to the leg; without a margin there is no such line.
        [TestMethod]
        public void TheLinesEndWithTheMarginWhereThereIsOne()
        {
            PlannerModel model = Launch(6000.0);
            int legLines = model.Steps[0].Leg.Lines.Count;

            List<KeyValuePair<string, string>> lines = model.Fuel.Lines(0);
            Assert.AreEqual(legLines + 1, lines.Count);
            Assert.AreEqual("<color=#9a9a9a>Margin 10 %</color>", lines.Last().Key);
            Assert.AreEqual(Speed(model.Budget.Needed[0] - model.Steps[0].Leg.DeltaV), lines.Last().Value);

            model.SetMargin(0f);
            model.Evaluate();
            Assert.AreEqual(legLines, model.Fuel.Lines(0).Count);
        }

        // The lines of a step are made once for a version of the texts and again when the version moves on.
        [TestMethod]
        public void TheLinesAreMadeOnceForAVersionOfTheTexts()
        {
            PlannerModel model = Launch(6000.0);

            List<KeyValuePair<string, string>> first = model.Fuel.Lines(0);
            Assert.AreSame(first, model.Fuel.Lines(0));
            model.Touch();
            Assert.AreNotSame(first, model.Fuel.Lines(0));
        }

        // A step that is not there has no stages, no lines and no texts.
        [TestMethod]
        public void AStepThatIsNotThereHasNothing()
        {
            PlanFuel fuel = Launch(6000.0).Fuel;

            Assert.AreSame(PlanFuel.NoLines, fuel.Lines(5));
            Assert.IsNull(fuel.Stage(5, 0));
            Assert.AreEqual(0, fuel.StageCount(5));
            Assert.AreEqual("", fuel.NeedsText(5));
            Assert.AreEqual("", fuel.LeftText(5));
            Assert.AreEqual("", fuel.WarningText(5, 0));
        }

        // A step's key tells its destination and its role, so that an open step stays open as steps come and go before it.
        [TestMethod]
        public void AStepsKeyIsItsDestinationAndRole()
        {
            PlannerModel model = Launch(6000.0);

            Assert.AreEqual(1 * RouteStop.Roles + (int)StepRole.Launch, model.Fuel.Key(0));
        }

        // The one stage of the vessel is the step's one stage row, and it can be used.
        [TestMethod]
        public void TheOneStageIsTheStepsOneStageRow()
        {
            PlannerModel model = Launch(6000.0);

            StageBudget.StepStage stage = model.Fuel.Stage(0, 0);

            Assert.IsNotNull(stage);
            Assert.AreEqual(1, stage.Stage);
            Assert.AreEqual(1, model.Fuel.StageCount(0));
            Assert.IsNull(model.Fuel.Stage(0, 1));
            Assert.IsTrue(model.Fuel.Burnable(0, 0));
        }

        // A stage kept for a later step is not burned on this one, nor is any stage behind it, as KSP fires them in order.
        [TestMethod]
        public void AStageKeptForLaterHoldsBackTheStagesBehindIt()
        {
            StageRow upper = PlannerModelFixture.Stage(2, 3000.0, 300.0, 280.0);
            upper.StartMass = 20.0;
            upper.EndMass = 12.0;
            StageRow lower = PlannerModelFixture.Stage(1, 3000.0, 100.0, 90.0);
            lower.StartMass = 8.0;
            lower.EndMass = 4.0;
            PlannerModel model = PlannerModelFixture.Model(PlannerModelFixture.Readout(new[] { upper, lower },
                PlannerModelFixture.Tank(12.0, 4.0, 2, 1, -1), PlannerModelFixture.Tank(8.0, 4.0, 1, -1, 0)),
                PlannerTests.Orbit(PlannerModelFixture.Kerbin, 80000), PlannerTests.Orbit(PlannerModelFixture.Mun, 14000));
            Assert.AreEqual(2, model.Fuel.StageCount(0));
            Assert.IsTrue(model.Fuel.Burnable(0, 0));
            Assert.IsTrue(model.Fuel.Burnable(0, 1));

            model.ToggleSaved(0, model.Fuel.Stage(0, 0).Stage);
            model.Evaluate();

            Assert.IsFalse(model.Fuel.Burnable(0, 0), "the kept stage is not burned");
            Assert.IsFalse(model.Fuel.Burnable(0, 1), "nor the one behind it");
        }

        // What a stage gives and keeps, in words: a dim 0 for none, what a dropped stage held in orange, "empty" for what is spent.
        [TestMethod]
        public void WhatAStageGivesAndKeepsIsWrittenInWords()
        {
            PlanFuel fuel = Launch(6000.0).Fuel;

            Assert.AreEqual("<color=#9a9a9a>0</color>", fuel.GivesText(new StageBudget.StepStage { Given = 0.2 }));
            Assert.AreEqual("<color=#f18a24>dropped, 1,234 m/s</color>", fuel.KeepsText(new StageBudget.StepStage { Dropped = true, LeftAfter = 1234.0 }));
            Assert.AreEqual("<color=#9a9a9a>empty</color>", fuel.KeepsText(new StageBudget.StepStage { LeftAfter = 0.2 }));
            Assert.AreEqual("950 m/s", fuel.KeepsText(new StageBudget.StepStage { LeftAfter = 950.0 }));
            Assert.AreEqual("", fuel.GivesText(null));
            Assert.AreEqual("", fuel.KeepsText(null));
            Assert.AreEqual("", fuel.TwrText(0, null));
        }

        // A speed as the planner writes it.
        private static string Speed(double metresPerSecond)
        {
            return EngineerFormat.Speed(metresPerSecond);
        }
    }
}
