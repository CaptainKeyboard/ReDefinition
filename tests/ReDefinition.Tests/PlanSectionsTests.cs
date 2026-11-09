using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Planner.Autopilot.Planning;
using ReDefinition.Planner.Engineer;

namespace ReDefinition.Tests
{
    // The planner's sections (PlanSections) found in a stock craft's parts and put into the plan: what the planner finds by itself, what the
    // player takes away or sets, and what stays in orbit.
    [TestClass]
    public class PlanSectionsTests
    {
        private const int Kerbin = PlannerModelFixture.Kerbin;

        // The readout of a stock craft: its stages, its parts and its simulation.
        private static EditorReadout Readout(string name)
        {
            StockCraft craft = StockCrafts.Named(name);
            EditorReadout readout = new EditorReadout
            {
                HasShip = true, PartFacts = craft.Parts, Sim = StockCrafts.Linked(craft.Sim), Density = StockCrafts.Density,
            };
            readout.Stages.AddRange(craft.Rows);
            return readout;
        }

        // The Kerbal X's pod with its parachute is found as the capsule, named by its kind and valid.
        [TestMethod]
        public void TheKerbalXsCapsuleIsFoundByItself()
        {
            PlannerModel model = PlannerModelFixture.Model(Readout("Kerbal X"), PlannerModelFixture.ToKerbinOrbit());

            LiveSection capsule = model.Sections.Live.Single(section => section.Plan.Kind == SectionKind.Capsule);
            Assert.AreEqual("Capsule", capsule.Name);
            Assert.IsTrue(capsule.Valid);
            Assert.AreEqual(PlannerState.Automatic, capsule.Plan.Group);
            Assert.IsTrue(capsule.Group.Parachute);
            Assert.AreEqual("", model.Sections.SectionWarning(model.Sections.Live.IndexOf(capsule)));
            Assert.AreEqual(0, model.State.Sections.Count, "a section the planner found is not the plan's until the player sets something on it");
        }

        // A capsule without parachutes is still the capsule, with a line under it that says so.
        [TestMethod]
        public void ACapsuleWithoutParachutesIsWarnedOf()
        {
            EditorReadout readout = Readout("Kerbal X");
            foreach (PartFacts part in readout.PartFacts) part.Parachute = false;
            PlannerModel model = PlannerModelFixture.Model(readout, PlannerModelFixture.ToKerbinOrbit());

            int row = model.Sections.Live.FindIndex(section => section.Plan.Kind == SectionKind.Capsule);
            Assert.IsTrue(row >= 0);
            Assert.AreEqual("No parachutes on the capsule", model.Sections.SectionWarning(row));
        }

        // A section the player takes away is not found again, nor is any other of its kind.
        [TestMethod]
        public void ASectionThePlayerTakesAwayDoesNotComeBack()
        {
            PlannerModel model = PlannerModelFixture.Model(Readout("Kerbal X"), PlannerModelFixture.ToKerbinOrbit());
            int row = model.Sections.Live.FindIndex(section => section.Plan.Kind == SectionKind.Capsule);
            int closed = 0;
            model.ListsStale = () => closed++;

            model.Sections.RemoveSection(row);
            model.Evaluate();

            Assert.AreEqual(1, closed, "the lists folded out under the rows are closed");
            Assert.IsTrue(model.State.Dismissed.Contains(SectionKind.Capsule));
            Assert.IsFalse(model.Sections.Live.Any(section => section.Plan.Kind == SectionKind.Capsule));
            Assert.IsNull(model.Sections.RowSection(99), "a row that is not there has no section");
        }

        // A section the player sets the same parts on as an earlier one is invalid, and says whom it shares them with.
        [TestMethod]
        public void ASectionThatSharesPartsWithAnEarlierOneIsInvalid()
        {
            PlannerModel model = PlannerModelFixture.Model(Readout("Kerbal X"), PlannerModelFixture.ToKerbinOrbit());
            string key = model.Sections.Live.Single(section => section.Plan.Kind == SectionKind.Capsule).Group.Key;

            PlannedSection lander = model.State.AddSection(SectionKind.Lander, key);
            PlannedSection capsule = model.State.AddSection(SectionKind.Capsule, key);
            model.Evaluate();

            LiveSection first = model.Sections.Live[0];
            LiveSection second = model.Sections.Live[1];
            Assert.AreSame(capsule, first.Plan, "capsules come first");
            Assert.IsTrue(first.Valid);
            Assert.AreSame(lander, second.Plan);
            Assert.IsFalse(second.Valid);
            Assert.AreEqual("Shares parts with the capsule", second.Problem);
            CollectionAssert.AreEqual(new[] { first }, model.Sections.Valid.ToArray());
            Assert.AreEqual("Shares parts with the capsule", model.Sections.SectionWarning(1));
        }

        // The planner's guess gives way to a section the player has set: it is not found where the player's takes its parts.
        [TestMethod]
        public void TheFoundCapsuleGivesWayToTheirSectionOnTheSameParts()
        {
            PlannerModel model = PlannerModelFixture.Model(Readout("Kerbal X"), PlannerModelFixture.ToKerbinOrbit());
            string key = model.Sections.Live.Single(section => section.Plan.Kind == SectionKind.Capsule).Group.Key;

            PlannedSection lander = model.State.AddSection(SectionKind.Lander, key);
            model.Evaluate();

            Assert.AreEqual(1, model.Sections.Live.Count);
            Assert.AreSame(lander, model.Sections.Live[0].Plan);
            Assert.IsTrue(model.Sections.Live[0].Valid);
        }

        // Picking the parts of a found section through the list puts the section into the plan and moves the version on.
        [TestMethod]
        public void PickingThePartsOfAFoundSectionPutsItIntoThePlan()
        {
            PlannerModel model = PlannerModelFixture.Model(Readout("Kerbal X"), PlannerModelFixture.ToKerbinOrbit());
            LiveSection found = model.Sections.Live.Single(section => section.Plan.Kind == SectionKind.Capsule);
            List<Choice> choices = model.Sections.PartChoices(found.Plan);
            Assert.AreEqual("Automatic", choices[0].Label);
            Assert.IsTrue(choices[0].Current, "the planner's own pick is the current one");
            Assert.IsTrue(choices.Count > 1, "the pod can also be picked by its parts");
            Choice other = choices[1];
            int version = model.Version;

            other.Pick();

            Assert.IsTrue(model.State.Sections.Contains(found.Plan));
            Assert.AreNotEqual(PlannerState.Automatic, found.Plan.Group);
            Assert.IsTrue(model.Version > version);
        }

        // The pointer on a list's entry lights its group, and the view is told; leaving it puts the light out.
        [TestMethod]
        public void ThePointerOnAnEntryLightsItsGroup()
        {
            PlannerModel model = PlannerModelFixture.Model(Readout("Kerbal X"), PlannerModelFixture.ToKerbinOrbit());
            LiveSection capsule = model.Sections.Live.Single(section => section.Plan.Kind == SectionKind.Capsule);
            List<Choice> choices = model.Sections.PartChoices(capsule.Plan);
            int told = 0;
            model.Sections.GroupLit = () => told++;

            choices[0].Hover(true);
            Assert.AreEqual(1, told);
            Assert.AreSame(capsule.Group, model.Sections.LitGroup);
            choices[0].Hover(false);
            Assert.AreEqual(2, told);
            Assert.IsNull(model.Sections.LitGroup);
        }

        // A kind of section is added by the list of kinds, the new section's parts to pick next, and only while the plan has room.
        [TestMethod]
        public void ASectionIsAddedByItsKindWhileThereIsRoom()
        {
            PlannerModel model = PlannerModelFixture.Model(Readout("Kerbal X"), PlannerModelFixture.ToKerbinOrbit());
            PlannedSection added = null;

            List<Choice> kinds = model.Sections.NewSectionChoices(plan => added = plan);
            CollectionAssert.AreEqual(new[] { "Capsule", "Lander", "Satellite" }, kinds.Select(kind => kind.Label).ToArray());
            kinds[2].Pick();

            Assert.IsNotNull(added);
            Assert.AreEqual(SectionKind.Satellite, added.Kind);
            Assert.AreEqual("", added.Group, "no parts yet");
            Assert.IsTrue(model.State.Sections.Contains(added));

            while (model.State.Sections.Count < PlanSections.MaxSections) model.State.AddSection(SectionKind.Satellite, "");
            Assert.AreEqual(0, model.Sections.NewSectionChoices(null).Count, "the list is full");
        }

        // An uncrewed satellite on a two-stage rocket: the probe core as root with an antenna (0, 1), a decoupler (2), the upper stage (3, 4),
        // a decoupler (5) and the booster (6, 7). The part at index i has the id 100 * (i + 1).
        private static EditorReadout SatelliteRocket()
        {
            PartFacts[] parts =
            {
                Part(-1, -1, 0.1, "OKTO2", command: true), Part(0, -1, 0.05, "Communotron 16"),
                Part(0, 1, 0.05, "TD-06", separator: Separation.Decoupler), Part(2, 1, 2.0, "FL-T400"),
                Part(3, 1, 0.5, "Terrier", engine: true), Part(4, 3, 0.05, "TD-12", separator: Separation.Decoupler),
                Part(5, 3, 4.0, "FL-T800"), Part(6, 3, 1.5, "Swivel", engine: true),
            };
            for (int i = 0; i < parts.Length; i++) parts[i].Id = (uint)(100 * (i + 1));
            parts[4].ActivatesAt = 2;
            parts[7].ActivatesAt = 4;
            StageRow booster = PlannerModelFixture.Stage(4, 1500.0, 200.0, 180.0);
            StageRow upper = PlannerModelFixture.Stage(2, 3000.0, 60.0, 50.0);
            return PlannerModelFixture.Readout(new[] { booster, upper }, parts);
        }

        // A part hanging under `parent` in the part tree.
        private static PartFacts Part(int parent, int decoupleStage, double mass, string title, bool command = false, bool engine = false,
                                      Separation separator = Separation.None)
        {
            return new PartFacts
            {
                Parent = parent, DecoupleStage = decoupleStage, Mass = mass, DryMass = mass, Title = title, Command = command,
                Engine = engine, Separator = separator,
            };
        }

        // An uncrewed craft's satellite is found, stays at the first orbit before the route ends, and is let go by the part that separates it.
        [TestMethod]
        public void ASatelliteStaysInTheFirstOrbitAndIsLetGoThere()
        {
            PlannerModel model = PlannerModelFixture.Model(SatelliteRocket(), PlannerModelFixture.ToKerbinOrbit());

            LiveSection satellite = model.Sections.Live.Single(section => section.Plan.Kind == SectionKind.Satellite);
            Assert.AreEqual(1, satellite.DeployAt, "the route's one orbit");
            List<SectionRelease> releases = model.Sections.ReleasesAt(1);
            Assert.AreEqual(1, releases.Count);
            Assert.AreEqual("Satellite", releases[0].Name);
            Assert.AreEqual(300u, releases[0].SeparatorId, "the decoupler under the probe core, the third part");
            Assert.AreEqual(2, releases[0].Index);
            Assert.AreEqual("TD-06", releases[0].Title);
            Assert.AreEqual(0, model.Sections.ReleasesAt(0).Count);
            Assert.AreEqual(1, model.State.Releases.Count, "the plan keeps what is let go, for a flight loaded again");
            Assert.AreEqual(1, model.State.Releases[0].Stop);
        }

        // A satellite the player sends to a later destination is let go there, and the planner's own pick comes back with "Automatic".
        [TestMethod]
        public void ASatelliteIsLetGoWhereThePlayerSendsIt()
        {
            PlannerModel model = PlannerModelFixture.Model(SatelliteRocket(),
                PlannerTests.Surface(Kerbin), PlannerTests.Orbit(Kerbin, 80000), PlannerTests.Orbit(PlannerModelFixture.Mun, 14000));
            LiveSection satellite = model.Sections.Live.Single(section => section.Plan.Kind == SectionKind.Satellite);
            Assert.AreEqual(2, satellite.DeployAt, "the route's last orbit by default");

            List<Choice> places = model.Sections.DeployChoices(0);
            Assert.AreEqual("Automatic", places[0].Label);
            Assert.AreEqual(3, places.Count, "automatic and the two destinations after the start");
            places[1].Pick();
            model.Evaluate();

            Assert.AreEqual(1, model.Sections.Live.Single(section => section.Plan.Kind == SectionKind.Satellite).DeployAt);
            Assert.AreEqual(1, model.Sections.ReleasesAt(1).Count);
            Assert.AreEqual(0, model.Sections.ReleasesAt(2).Count);
            places = model.Sections.DeployChoices(0);
            Assert.IsTrue(places[1].Current);
            Assert.IsFalse(places[0].Current);
            places[0].Pick();
            model.Evaluate();
            Assert.AreEqual(2, model.Sections.Live[0].DeployAt);
        }

        // A change of the vessel under an open list closes it: the groups it listed are other ones now. The first reading does not.
        [TestMethod]
        public void AChangedVesselClosesTheListsOpenUnderIt()
        {
            EditorReadout first = Readout("Kerbal X");
            PlannerModel model = PlannerModelFixture.Model(first, PlannerModelFixture.ToKerbinOrbit());
            int closed = 0;
            model.ListsStale = () => closed++;

            model.Evaluate();
            Assert.AreEqual(0, closed, "the same parts are the same groups");

            ((FakePlannerHost)model.Host).Readout = Readout("ComSat Lx");
            model.Evaluate();
            Assert.AreEqual(1, closed);
        }

        // The choices of who flies a step name the whole vessel, then each valid section alone.
        [TestMethod]
        public void TheChoicesOfWhoFliesNameTheVesselAndEachSection()
        {
            PlannerModel model = PlannerModelFixture.Model(Readout("Kerbal X"), PlannerModelFixture.ToKerbinOrbit());

            List<Choice> choices = model.Sections.FlyerChoices(0, StepRole.Launch);

            Assert.AreEqual("Ship", choices[0].Label);
            Assert.IsTrue(choices[0].Current, "the whole vessel flies a launch");
            CollectionAssert.Contains(choices.Select(choice => choice.Label).ToArray(), "Capsule");
            Assert.AreEqual(0, model.Sections.FlyerChoices(0, StepRole.Land).Count, "a step that is not there has no list");
        }
    }
}
