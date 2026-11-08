using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Engineer;

namespace ReDefinition.Tests
{
    // The planner's model (PlannerModel) without the game: the route's commands, the events the view listens to, and the evaluation of a
    // plain vessel of one stage on a route from Kerbin's surface to its orbit.
    [TestClass]
    public class PlannerModelTests
    {
        private const int Kerbin = PlannerModelFixture.Kerbin, Mun = PlannerModelFixture.Mun;

        // A vessel of one stage with plenty of delta-v and thrust.
        private static PlannerModel Strong(params RouteStop[] route)
        {
            return PlannerModelFixture.Model(PlannerModelFixture.OneStage(6000.0, 200.0, 180.0), route);
        }

        // Evaluating tells the view once, and moves the version on, so that the rows' texts are made again.
        [TestMethod]
        public void EvaluatingTellsTheViewOnceAndMovesTheVersionOn()
        {
            PlannerModel model = Strong(PlannerModelFixture.ToKerbinOrbit());
            int told = 0;
            model.Evaluated = () => told++;
            int version = model.Version;

            model.Evaluate();

            Assert.AreEqual(1, told);
            Assert.IsTrue(model.Version > version);
        }

        // A route from a surface to an orbit of the same body is one step, a launch, found in its leg and role.
        [TestMethod]
        public void ARouteFromTheSurfaceToTheOrbitIsOneLaunchStep()
        {
            PlannerModel model = Strong(PlannerModelFixture.ToKerbinOrbit());

            Assert.AreEqual(1, model.Steps.Count);
            Assert.AreEqual(StepRole.Launch, model.Steps[0].Role);
            Assert.AreEqual(0, model.Slot(0, StepRole.Launch));
            Assert.AreEqual(-1, model.Slot(0, StepRole.Transfer), "the leg has no other step");
            Assert.AreEqual(-1, model.Slot(1, StepRole.Launch), "no leg after the last stop");
            Assert.AreEqual(-1, model.Slot(-1, StepRole.Launch));
        }

        // The margin is the slider's per cent over the steps' delta-v, and a margin that is the same again changes nothing.
        [TestMethod]
        public void TheMarginIsAddedToEveryStepsDeltaV()
        {
            PlannerModel model = Strong(PlannerModelFixture.ToKerbinOrbit());
            double leg = model.Steps[0].Leg.DeltaV;
            int version = model.Version;

            model.SetMargin(25.4f);
            Assert.AreEqual(0.25, model.State.Margin, 1e-9, "rounded to whole per cent");
            Assert.IsTrue(model.Version > version, "a change moves the version on");
            model.Evaluate();
            Assert.AreEqual(leg * 1.25, model.Budget.Needed[0], 1e-6);

            version = model.Version;
            model.SetMargin(25f);
            Assert.AreEqual(version, model.Version, "the same margin is no change");
        }

        // An altitude is typed in kilometres, with a point or a comma, and kept between the low orbit and the edge of the sphere of influence.
        [TestMethod]
        public void AnAltitudeIsKeptBetweenTheLowOrbitAndTheEdge()
        {
            PlannerModel model = Strong(PlannerModelFixture.ToKerbinOrbit());

            model.SetAltitude(1, "150,4");
            Assert.AreEqual("150", model.AltitudeKm(1));
            Assert.AreEqual(150400.0, model.State.Stops[1].Altitude, 1e-6);

            model.SetAltitude(1, "20");
            Assert.AreEqual(80000.0, model.State.Stops[1].Altitude, 1e-6, "not under Kerbin's low orbit");

            // Kerbin has no sphere of influence in the test's bodies, so the edge is a hundred radii.
            model.SetAltitude(1, "99999");
            Assert.AreEqual(600000.0 * 100.0, model.State.Stops[1].Altitude, 1e-6);

            double before = model.State.Stops[1].Altitude;
            model.SetAltitude(1, "not a number");
            Assert.AreEqual(before, model.State.Stops[1].Altitude, "text that is no number changes nothing");
        }

        // The altitude buttons step by 10 km and stop at the low orbit.
        [TestMethod]
        public void TheAltitudeButtonsStepByTenKilometres()
        {
            PlannerModel model = Strong(PlannerModelFixture.ToKerbinOrbit());

            model.StepAltitude(1, 1);
            Assert.AreEqual(90000.0, model.State.Stops[1].Altitude, 1e-6);
            model.StepAltitude(1, -1);
            model.StepAltitude(1, -1);
            Assert.AreEqual(80000.0, model.State.Stops[1].Altitude, 1e-6, "the low orbit is the lowest");
        }

        // A destination is added after the last one, in the same body's low orbit, up to the most a route can hold.
        [TestMethod]
        public void ADestinationIsAddedInTheLastBodysLowOrbitUpToTheMost()
        {
            PlannerModel model = Strong(PlannerModelFixture.ToKerbinOrbit());
            Assert.IsTrue(model.CanAddStop());

            model.AddStop();
            Assert.AreEqual(3, model.State.Stops.Count);
            Assert.AreEqual(Kerbin, model.State.Stops[2].Body);
            Assert.AreEqual(StopKind.Orbit, model.State.Stops[2].Kind);
            Assert.AreEqual(80000.0, model.State.Stops[2].Altitude, 1e-6);

            for (int i = 0; i < 20; i++) model.AddStop();
            Assert.AreEqual(PlannerModel.HardStops, model.State.Stops.Count);
            Assert.IsFalse(model.CanAddStop());
        }

        // A destination whose body is not picked yet cannot be followed by another, and the route up to it is the route so far.
        [TestMethod]
        public void NoDestinationFollowsOneWithoutABody()
        {
            PlannerModel model = Strong(PlannerModelFixture.ToKerbinOrbit());
            model.State.Stops.Add(new RouteStop { Body = RouteStop.NoBody, Kind = StopKind.Orbit });

            Assert.IsFalse(model.CanAddStop());
            Assert.AreEqual(2, model.PickedStops().Count);
            Assert.IsFalse(model.HasBody(2));
            StringAssert.Contains(model.BodyName(2), "Destination");
            model.AddStop();
            Assert.AreEqual(3, model.State.Stops.Count, "nothing is added after a stop without a body");
        }

        // A destination is taken out of the route, two stay at least, and the view is told so that it closes its lists and folds its steps.
        [TestMethod]
        public void ADestinationIsTakenOutAndTheViewIsTold()
        {
            PlannerModel model = Strong(PlannerModelFixture.ToKerbinOrbit());
            model.AddStop();
            int closed = 0, reshaped = 0;
            model.ListsStale = () => closed++;
            model.StopsReshaped = () => reshaped++;

            model.RemoveStop(1);
            Assert.AreEqual(2, model.State.Stops.Count);
            Assert.AreEqual(1, closed);
            Assert.AreEqual(1, reshaped);

            model.RemoveStop(0);
            Assert.AreEqual(2, model.State.Stops.Count, "two destinations stay at least");
            Assert.AreEqual(1, closed, "a removal that does not happen closes nothing");
            Assert.AreEqual(1, reshaped);
        }

        // Taking out the start makes the second stop the start: the budget then counts from there, with no launch from the first.
        [TestMethod]
        public void TakingOutTheStartMakesTheSecondStopTheStartAndTheBudgetCountsFromThere()
        {
            PlannerModel model = Strong(PlannerTests.Surface(Kerbin), PlannerTests.Orbit(Kerbin, 80000), PlannerTests.Orbit(Mun, 30000));
            double whole = model.Steps.Sum(step => step.Leg.DeltaV);
            Assert.IsTrue(model.Steps.Any(step => step.Role == StepRole.Launch), "the whole route lifts off");

            model.RemoveStop(0);
            model.Evaluate();

            Assert.AreEqual(2, model.State.Stops.Count);
            Assert.AreEqual(StopKind.Orbit, model.State.Stops[0].Kind, "the orbit is the start now");
            Assert.AreEqual(Kerbin, model.State.Stops[0].Body);
            Assert.AreEqual(Mun, model.State.Stops[1].Body);
            Assert.IsFalse(model.Steps.Any(step => step.Role == StepRole.Launch), "no lift-off from an orbit");
            Assert.IsTrue(model.Steps.Sum(step => step.Leg.DeltaV) < whole, "only the transfer is left to pay for");
        }

        // The start can be another body or another kind: the budget counts the route from wherever it begins.
        [TestMethod]
        public void AnotherStartChangesWhatTheRouteNeeds()
        {
            PlannerModel model = Strong(PlannerTests.Surface(Kerbin), PlannerTests.Orbit(Mun, 30000));
            double fromKerbin = model.Steps.Sum(step => step.Leg.DeltaV);

            model.SetKind(0, StopKind.Orbit);
            model.Evaluate();

            Assert.IsTrue(model.Steps.Sum(step => step.Leg.DeltaV) < fromKerbin, "from an orbit the climb is not paid for");
        }

        // The list of bodies puts each moon under its planet, one level of 12 units deeper, and marks the stop's own.
        [TestMethod]
        public void TheListOfBodiesPutsEachMoonUnderItsPlanet()
        {
            PlannerModel model = Strong(PlannerModelFixture.ToKerbinOrbit());

            List<Choice> choices = model.BodyChoices(1);

            CollectionAssert.AreEqual(new[] { "Sun", "Kerbin", "Mun", "Minmus" }, choices.Select(choice => choice.Label).ToArray());
            CollectionAssert.AreEqual(new[] { 0f, 12f, 24f, 24f }, choices.Select(choice => choice.Indent).ToArray());
            Assert.AreEqual("Kerbin", choices.Single(choice => choice.Current).Label);
            Assert.AreEqual(0, model.BodyChoices(5).Count, "no stop, no list");
        }

        // Picking a body puts the stop in that body's low orbit, and ends the use of an air the body does not have.
        [TestMethod]
        public void PickingABodyPutsTheStopInItsLowOrbit()
        {
            PlannerModel model = Strong(PlannerModelFixture.ToKerbinOrbit());
            model.State.Stops[1].UseAtmosphere = true;

            model.BodyChoices(1).Single(choice => choice.Label == "Mun").Pick();

            Assert.AreEqual(Mun, model.State.Stops[1].Body);
            Assert.AreEqual(14000.0, model.State.Stops[1].Altitude, 1e-6, "the Mun's low orbit");
            Assert.IsFalse(model.State.Stops[1].UseAtmosphere, "the Mun has no air");
        }

        // A stop becomes a surface or an orbit; an orbit without an altitude takes the body's low orbit.
        [TestMethod]
        public void AStopBecomesASurfaceOrAnOrbit()
        {
            PlannerModel model = Strong(PlannerModelFixture.ToKerbinOrbit());
            model.State.Stops[1].Altitude = 0.0;

            model.SetKind(1, StopKind.Surface);
            Assert.AreEqual(StopKind.Surface, model.Kind(1));
            model.SetKind(1, StopKind.Orbit);
            Assert.AreEqual(StopKind.Orbit, model.Kind(1));
            Assert.AreEqual(80000.0, model.State.Stops[1].Altitude, 1e-6);
            Assert.AreEqual(StopKind.Orbit, model.Kind(7), "a stop that is not there is an orbit");
        }

        // The air of a body is used for landing or braking only where the body has some.
        [TestMethod]
        public void TheAirIsUsedOnlyWhereTheBodyHasSome()
        {
            PlannerModel model = Strong(PlannerModelFixture.ToKerbinOrbit());
            Assert.IsTrue(model.HasAtmosphere(1));
            Assert.IsFalse(model.UsesAtmosphere(1));

            model.ToggleAtmosphere(1);
            Assert.IsTrue(model.UsesAtmosphere(1));
            model.ToggleAtmosphere(1);
            Assert.IsFalse(model.UsesAtmosphere(1));

            model.BodyChoices(1).Single(choice => choice.Label == "Mun").Pick();
            Assert.IsFalse(model.HasAtmosphere(1));
            model.ToggleAtmosphere(1);
            Assert.IsFalse(model.State.Stops[1].UseAtmosphere, "a body without air takes no toggle");
        }

        // A stage is kept for a later step, or dropped after one, and a second press takes it back; a stage that is no stage is ignored.
        [TestMethod]
        public void AStageIsKeptOrDroppedAndThenTakenBack()
        {
            PlannerModel model = Strong(PlannerModelFixture.ToKerbinOrbit());
            StagePlan plan = model.State.Stops[1].Stages[(int)StepRole.Launch];

            model.ToggleSaved(0, 1);
            Assert.IsTrue(model.Saved(0, 1));
            CollectionAssert.AreEquivalent(new[] { 1 }, plan.Save.ToArray());
            model.ToggleSaved(0, 1);
            Assert.IsFalse(model.Saved(0, 1));

            model.ToggleDropAfter(0, 1);
            Assert.IsTrue(model.DropsAfter(0, 1));
            model.ToggleDropAfter(0, 1);
            Assert.IsFalse(model.DropsAfter(0, 1));

            model.ToggleSaved(0, -1);
            model.ToggleSaved(3, 1);
            Assert.AreEqual(0, plan.Save.Count, "neither a stage below zero nor a step that is not there is kept");
        }

        // A refill, a split burn and the way down are set on the stop the step leads to, and the same setting again is no change.
        [TestMethod]
        public void TheChoicesBehindTheStepsAreSetOnTheStopTheyLeadTo()
        {
            PlannerModel model = Strong(PlannerModelFixture.ToKerbinOrbit());

            model.ToggleRefuel(0, StepRole.Launch);
            Assert.IsTrue(model.Refuels(0, StepRole.Launch));
            Assert.IsTrue(model.State.Stops[1].Refuel[(int)StepRole.Launch]);

            model.SetSplitBurns(1, true);
            Assert.IsTrue(model.SplitsBurns(1));
            int version = model.Version;
            model.SetSplitBurns(1, true);
            Assert.AreEqual(version, model.Version, "the same setting is no change");

            model.SetOrbitFirst(1, true);
            Assert.IsTrue(model.OrbitFirst(1));
            Assert.IsFalse(model.OrbitFirst(9), "no such stop");
        }

        // The stock Kerbal X flown through the model gets to the Mun and home as the offline model has it (StockCraftTests): a little short with
        // the 10 % margin, and not short without one.
        [TestMethod]
        public void TheKerbalXGoesToTheMunAndHomeAsTheOfflineModelHasIt()
        {
            StockCraft craft = StockCrafts.Named("Kerbal X");
            EditorReadout readout = new EditorReadout
            {
                HasShip = true, PartFacts = craft.Parts, Sim = StockCrafts.Linked(craft.Sim), Density = StockCrafts.Density,
            };
            readout.Stages.AddRange(craft.Rows);
            PlannerModel model = PlannerModelFixture.Model(readout, PlannerTests.Surface(Kerbin), PlannerTests.Orbit(Kerbin, 80000),
                PlannerTests.Surface(Mun), PlannerTests.Surface(Kerbin, true));

            double withMargin = model.Budget.Short.Sum();
            Assert.IsTrue(withMargin > 0.5 && withMargin < 150.0, "a little short with a margin: " + withMargin);

            model.SetMargin(0f);
            model.Evaluate();
            Assert.IsTrue(model.Budget.Short.Sum() < 0.5, "not short without one");
            Assert.IsFalse(model.Warnings.Any(lines => lines.Any(line => line.Contains("too low"))), "the Kerbal X lifts off and lands");
        }

        // The model's version moves on with a touch, for the view's clicks, and with every change.
        [TestMethod]
        public void TheVersionMovesOnWithATouchAndWithEveryChange()
        {
            PlannerModel model = Strong(PlannerModelFixture.ToKerbinOrbit());
            int version = model.Version;

            model.Touch();
            Assert.AreEqual(version + 1, model.Version);
            model.Changed();
            Assert.AreEqual(version + 2, model.Version);
        }

        // The model has no dialog elements: nothing in its types names the dialog's or the UI's.
        [TestMethod]
        public void TheModelNamesNoDialogElements()
        {
            foreach (System.Type type in new[]
                     {
                         typeof(PlannerModel), typeof(PlanSections), typeof(PlanThrust), typeof(PlanFuel), typeof(PlannerDashboard),
                     })
            {
                foreach (System.Reflection.FieldInfo field in type.GetFields(System.Reflection.BindingFlags.Instance
                                                                             | System.Reflection.BindingFlags.Static
                                                                             | System.Reflection.BindingFlags.Public
                                                                             | System.Reflection.BindingFlags.NonPublic))
                    Assert.IsFalse(field.FieldType.FullName.StartsWith("UnityEngine.UI") || field.FieldType.Name.StartsWith("DialogGUI"),
                        type.Name + "." + field.Name + " is a dialog element");
            }
        }
    }
}
