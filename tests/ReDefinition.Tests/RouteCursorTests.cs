using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Planner.Autopilot.Planning;

namespace ReDefinition.Tests
{
    // Where a vessel is on its route, and what the next step is as far as the facts decide it: the logic of the planner without the game.
    [TestClass]
    public class RouteCursorTests
    {
        private static RouteCursor Locate(bool[] passed, bool[] repeats, ref int reached, bool counted = true, bool onFirstSurface = false, bool onTheWay = true, int headedFor = -1)
        {
            return RouteCursor.Locate(passed, repeats, ref reached, counted, onFirstSurface, stop => onTheWay, () => headedFor);
        }

        private static readonly bool[] Never = { false, false, false };

        // A route that starts on the pad: the vessel on the pad is at the first stop, and goes to the second.
        [TestMethod]
        public void AVesselOnThePadIsAtTheFirstStop()
        {
            int reached = -1;
            RouteCursor cursor = Locate(new[] { true, false, false }, Never, ref reached, onFirstSurface: true);
            Assert.AreEqual(0, cursor.At);
            Assert.AreEqual(0, cursor.Last);
            Assert.AreEqual(1, cursor.Next);
            Assert.AreEqual(0, reached);
        }

        // The first stop, a surface, counts as passed anywhere round its body: back at that body from the second stop, the vessel is on its way from
        // the second, to the third.
        [TestMethod]
        public void AVesselBackAtTheFirstBodyContinuesFromTheStopItReached()
        {
            int reached = 1;
            RouteCursor cursor = Locate(new[] { true, false, false }, Never, ref reached);
            Assert.AreEqual(0, cursor.At);
            Assert.AreEqual(1, cursor.Last);
            Assert.AreEqual(2, cursor.Next);
            Assert.IsFalse(cursor.Done);
        }

        // A vessel that stands on the first surface again is at the first stop, whatever count is kept.
        [TestMethod]
        public void AVesselOnTheFirstSurfaceAgainStaysThere()
        {
            int reached = 1;
            RouteCursor cursor = Locate(new[] { true, false, false }, Never, ref reached, onFirstSurface: true);
            Assert.AreEqual(0, cursor.Last);
            Assert.AreEqual(1, cursor.Next);
        }

        // A route that starts in an orbit: the vessel on the way back is at no stop, and goes on from the one it reached.
        [TestMethod]
        public void AVesselAtNoStopGoesOnFromTheOneItReached()
        {
            int reached = 1;
            RouteCursor cursor = Locate(Never, Never, ref reached);
            Assert.AreEqual(-1, cursor.At);
            Assert.AreEqual(1, cursor.Last);
            Assert.AreEqual(2, cursor.Next);
        }

        // Where it cannot be on its way from that stop, the count does not hold, and the stop it is headed for is asked.
        [TestMethod]
        public void AVesselThatCannotBeOnItsWayIsPlannedForAgain()
        {
            int reached = 1;
            RouteCursor cursor = Locate(Never, Never, ref reached, onTheWay: false, headedFor: 1);
            Assert.AreEqual(-1, cursor.Last);
            Assert.AreEqual(1, cursor.Next);
        }

        // Without a count nothing is known of where the vessel came from.
        [TestMethod]
        public void WithoutACountTheStopsPassedAreAllThereIs()
        {
            int reached = 5;
            RouteCursor cursor = Locate(new[] { true, false, false }, Never, ref reached, counted: false);
            Assert.AreEqual(0, cursor.Last);
            Assert.AreEqual(1, cursor.Next);
            Assert.AreEqual(5, reached);
        }

        // A stop that comes again in the route is passed only once the vessel has been through the stops between.
        [TestMethod]
        public void AStopThatComesAgainIsPassedOnlyOnTheWayBack()
        {
            var passed = new[] { true, false, true, false };
            var repeats = new[] { false, false, true, false };
            int before = 0;
            Assert.AreEqual(0, Locate(passed, repeats, ref before).Last);
            int after = 1;
            RouteCursor back = Locate(passed, repeats, ref after);
            Assert.AreEqual(2, back.Last);
            Assert.AreEqual(3, back.Next);
            Assert.AreEqual(2, after);
        }

        [TestMethod]
        public void TheLastStopEndsTheRoute()
        {
            int reached = 2;
            RouteCursor cursor = Locate(new[] { false, false, true }, Never, ref reached);
            Assert.IsTrue(cursor.Done);
            Assert.AreEqual(-1, cursor.Next);
        }

        [TestMethod]
        public void ARouteThatIsNotBegunHasNoStopBehind()
        {
            int reached = -1;
            RouteCursor cursor = Locate(Never, Never, ref reached, headedFor: 1);
            Assert.AreEqual(-1, cursor.Last);
            Assert.AreEqual(1, cursor.Next);
            Assert.AreEqual(-1, reached);
        }

        private static StepFacts Facts(bool surface = false, bool sameBody = true)
        {
            return new StepFacts { StopSurface = surface, SameBody = sameBody, IsChildOfBody = !sameBody };
        }

        [TestMethod]
        public void AVesselOnTheGroundLaunchesUnlessTheStopIsASurfaceOfThatBody()
        {
            StepFacts facts = Facts(sameBody: false);
            facts.OnGround = true;
            Assert.AreEqual(StepIntent.Ascent, facts.Intend());
            StepFacts hop = Facts(surface: true);
            hop.OnGround = true;
            Assert.AreEqual(StepIntent.HopNotPlanned, hop.Intend());
        }

        [TestMethod]
        public void AVesselThatClimbsOrFliesKeepsClimbingUnlessItIsLanding()
        {
            StepFacts climbing = Facts(sameBody: false);
            climbing.Climbing = true;
            Assert.AreEqual(StepIntent.Ascent, climbing.Intend());
            StepFacts landing = Facts(surface: true);
            landing.Flying = true;
            Assert.AreEqual(StepIntent.Land, landing.Intend());
        }

        [TestMethod]
        public void ALandingDependsOnTheOrbitAndTheWayTheDescentIsFlown()
        {
            StepFacts open = Facts(surface: true);
            open.Open = true;
            Assert.AreEqual(StepIntent.LandByCapture, open.Intend());
            open.Aerobrake = true;
            Assert.AreEqual(StepIntent.LandByAir, open.Intend());
            open.Direct = true;
            Assert.AreEqual(StepIntent.LandStraightIn, open.Intend());
            StepFacts closed = Facts(surface: true);
            closed.ForPlayer = true;
            closed.PeriapsisOverGround = true;
            Assert.AreEqual(StepIntent.LandByDeorbit, closed.Intend());
            closed.PeriapsisOverGround = false;
            Assert.AreEqual(StepIntent.Land, closed.Intend());
        }

        [TestMethod]
        public void AnOrbitStopRoundTheBodyIsMetChangedOrBrakedInto()
        {
            StepFacts meet = Facts();
            meet.HasMeetTarget = true;
            Assert.AreEqual(StepIntent.Meet, meet.Intend());
            Assert.AreEqual(StepIntent.ChangeOrbit, Facts().Intend());
            StepFacts wide = Facts();
            wide.Aerobrake = true;
            wide.ApoapsisAboveOrbit = true;
            Assert.AreEqual(StepIntent.AirPassFromOrbit, wide.Intend());
            wide.PlanEndsAtPass = true;
            Assert.AreEqual(StepIntent.AirPassEndsPlan, wide.Intend());
            wide.ApoapsisAboveOrbit = false;
            Assert.AreEqual(StepIntent.ChangeOrbit, wide.Intend());
        }

        [TestMethod]
        public void AnOpenOrbitRoundTheBodyIsCapturedOrBrakedByTheAir()
        {
            StepFacts open = Facts();
            open.Open = true;
            Assert.AreEqual(StepIntent.Capture, open.Intend());
            open.Aerobrake = true;
            Assert.AreEqual(StepIntent.AirOnApproach, open.Intend());
        }

        [TestMethod]
        public void TheWayToAnotherBodyIsLeftEjectedTransferredAndCorrected()
        {
            StepFacts away = Facts(sameBody: false);
            away.Open = true;
            away.Escaping = true;
            Assert.AreEqual(StepIntent.Leaving, away.Intend());
            StepFacts eject = Facts(sameBody: false);
            eject.EjectPossible = true;
            Assert.AreEqual(StepIntent.Eject, eject.Intend());
            StepFacts noWay = Facts(sameBody: false);
            noWay.IsChildOfBody = false;
            Assert.AreEqual(StepIntent.NoWay, noWay.Intend());
            StepFacts transfer = Facts(sameBody: false);
            Assert.AreEqual(StepIntent.Transfer, transfer.Intend());
            transfer.HasEncounter = true;
            Assert.AreEqual(StepIntent.CorrectCourse, transfer.Intend());
        }
    }
}
