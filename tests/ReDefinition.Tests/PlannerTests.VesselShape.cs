using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Engineer;

namespace ReDefinition.Tests
{
    // The planner tests on a vessel's shape (VesselShape): the capsule, the lander and the satellites found in the parts.
    public partial class PlannerTests
    {
        // A part hanging under `parent` in the part tree.
        private static PartFacts Part(int parent, int decoupleStage, double mass, string title = "part", bool command = false,
                                      bool engine = false, bool legs = false, bool parachute = false,
                                      Separation separator = Separation.None, int crew = 0)
        {
            return new PartFacts
            {
                Parent = parent, DecoupleStage = decoupleStage, Mass = mass, DryMass = mass, Title = title, Command = command,
                Engine = engine, Legs = legs, Parachute = parachute, Separator = separator, Crew = crew,
                Id = (uint)(title.GetHashCode() & 0xffff),
            };
        }

        // The Kerbal X's shape: a pod with a parachute on top, a decoupler under it,
        // the upper stage, another decoupler, the booster.
        private static List<PartFacts> KerbalX()
        {
            return new List<PartFacts>
            {
                Part(-1, -1, 0.8, "Mk1 Command Pod", command: true, crew: 1), Part(0, -1, 0.1, "Mk16 Parachute", parachute: true),
                Part(0, 1, 0.05, "TD-12", separator: Separation.Decoupler), Part(2, 1, 4.5, "FL-T800"),
                Part(3, 1, 1.5, "LV-909", engine: true), Part(4, 3, 0.05, "TD-25", separator: Separation.Decoupler),
                Part(5, 3, 18.0, "Rockomax X200-32"), Part(6, 3, 3.0, "Skipper", engine: true),
            };
        }

        // Apollo's shape. From the top come the command module with its parachute (0, 1), a decoupler,
        // and the service module with its engine and docking port (2-5). Under it is the lander
        // by its own docking port (6), its ascent stage (7, 8), a decoupler, and its
        // descent stage with legs (9-12). Then come an adapter and the transfer stage (13-15).
        private static List<PartFacts> Apollo()
        {
            return new List<PartFacts>
            {
                Part(-1, -1, 2.0, "Mk1-3 Command Pod", command: true, crew: 3), Part(0, -1, 0.1, "Mk16 Parachute", parachute: true),
                Part(0, 0, 0.05, "TD-12", separator: Separation.Decoupler), Part(2, 0, 4.0, "FL-T400"),
                Part(3, 0, 1.0, "LV-909", engine: true), Part(4, 0, 0.1, "Clamp-O-Tron SM", separator: Separation.DockingPort),
                Part(5, 0, 0.1, "Clamp-O-Tron LM", separator: Separation.DockingPort),
                Part(6, 0, 1.0, "Mk2 Lander Can", command: true, crew: 2), Part(7, 0, 0.5, "Spark", engine: true),
                Part(8, 2, 0.05, "TD-06", separator: Separation.Decoupler), Part(9, 2, 2.0, "FL-T200"),
                Part(10, 2, 0.2, "LT-2 Landing Strut", legs: true), Part(10, 2, 0.5, "Terrier", engine: true),
                Part(12, 4, 0.05, "TD-25", separator: Separation.Decoupler), Part(13, 4, 6.0, "Rockomax X200-16"),
                Part(14, 4, 1.5, "Poodle", engine: true),
            };
        }

        // An uncrewed satellite on a two-stage rocket: the probe core as root with an
        // antenna (0, 1), a decoupler, the upper stage (3, 4), a decoupler, the booster
        // (6, 7), which fires first.
        private static List<PartFacts> SatelliteRocket()
        {
            List<PartFacts> parts = new List<PartFacts>
            {
                Part(-1, -1, 0.1, "OKTO2", command: true), Part(0, -1, 0.05, "Communotron 16"),
                Part(0, 1, 0.05, "TD-06", separator: Separation.Decoupler), Part(2, 1, 2.0, "FL-T400"),
                Part(3, 1, 0.5, "Terrier", engine: true), Part(4, 3, 0.05, "TD-12", separator: Separation.Decoupler),
                Part(5, 3, 4.0, "FL-T800"), Part(6, 3, 1.5, "Swivel", engine: true),
            };
            parts[4].ActivatesAt = 2;
            parts[7].ActivatesAt = 4;
            return parts;
        }

        // A crewed vessel's capsule is its pod with the parachute, the decoupler under it going with the stage below.
        [TestMethod]
        public void ACrewedVesselsCapsuleIsItsPod()
        {
            List<PartFacts> parts = KerbalX();
            List<PartGroupChoice> groups = VesselShape.Candidates(parts);
            PartGroupChoice capsule = VesselShape.AutoCapsule(groups);

            Assert.IsTrue(VesselShape.Crewed(parts));
            Assert.IsNotNull(capsule);
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, capsule.Members.ToArray(), "the decoupler goes with the stage under it");
            StringAssert.StartsWith(capsule.Name, "Mk1 Command Pod, 2 parts");
        }

        // A pod without parachutes is still the capsule, marked as lacking them.
        [TestMethod]
        public void APodWithoutParachutesIsStillTheCapsule()
        {
            List<PartFacts> parts = KerbalX();
            parts[1].Parachute = false;
            PartGroupChoice capsule = VesselShape.AutoCapsule(VesselShape.Candidates(parts));

            Assert.IsNotNull(capsule);
            Assert.IsFalse(capsule.Parachute, "the planner says it lacks parachutes");
        }

        // The lander is what its docking port separates, without the transfer stage dropped before it; the capsule
        // is the lightest crewed part and belongs to the rest.
        [TestMethod]
        public void TheLanderIsWhatItsDockingPortSeparatesWithoutTheStagesDroppedBeforeIt()
        {
            List<PartGroupChoice> groups = VesselShape.Candidates(Apollo());
            PartGroupChoice capsule = VesselShape.AutoCapsule(groups);
            PartGroupChoice lander = groups.Single(group => group.DockingPort && group.Legs);

            CollectionAssert.AreEquivalent(new[] { 6, 7, 8, 9, 10, 11, 12 }, lander.Members.ToArray(),
                "the lander under its own port, without the transfer stage");
            StringAssert.StartsWith(lander.Name, "Mk2 Lander Can");
            Assert.IsTrue(lander.Command && lander.Engine);
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, capsule.Members.ToArray(), "the command module, the lightest crewed part");
            Assert.IsTrue(VesselShape.Fits(lander, capsule));
            Assert.AreEqual(-1, VesselShape.Parent(new List<PartGroupChoice> { lander, capsule }, 1), "the capsule belongs to the rest");
        }

        // With legs on the Kerbal X's upper stage, the pod and the upper stage together can be the lander, the
        // capsule part of it.
        [TestMethod]
        public void TheKerbalXsUpperStageWithItsLegsCanBeTheLander()
        {
            // The legs on the upper stage: the pod and the upper stage land together.
            List<PartFacts> parts = KerbalX();
            parts.Add(Part(3, 1, 0.2, "LT-1 Landing Struts", legs: true));
            List<PartGroupChoice> groups = VesselShape.Candidates(parts);
            PartGroupChoice capsule = VesselShape.AutoCapsule(groups);
            PartGroupChoice lander = groups.Single(group => group.Legs && group.Command);

            CollectionAssert.AreEquivalent(new[] { 0, 1, 2, 3, 4, 8 }, lander.Members.ToArray(), "the pod, the upper stage, its legs");
            Assert.IsFalse(lander.DockingPort);
            Assert.AreEqual(0, VesselShape.Parent(new List<PartGroupChoice> { lander, capsule }, 1), "the capsule is part of the lander");
        }

        // An uncrewed vessel's one satellite, the probe core and its antenna, is found by itself.
        [TestMethod]
        public void AnUncrewedVesselsOneSatelliteIsFoundByItself()
        {
            List<PartFacts> parts = SatelliteRocket();
            List<PartGroupChoice> groups = VesselShape.Candidates(parts);
            PartGroupChoice satellite = VesselShape.AutoSatellite(parts, groups);

            Assert.IsFalse(VesselShape.Crewed(parts));
            Assert.IsNotNull(satellite);
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, satellite.Members.ToArray(), "the probe core and its antenna");
        }

        // Two probe cores, two satellites: the planner leaves the choice to the player.
        [TestMethod]
        public void TwoSatellitesAreLeftToThePlayer()
        {
            // A second probe core under a decoupler on the upper stage.
            List<PartFacts> parts = SatelliteRocket();
            parts.Add(Part(3, 2, 0.05, "TT-38K", separator: Separation.Decoupler));
            parts.Add(Part(8, 2, 0.1, "OKTO", command: true));

            Assert.IsNull(VesselShape.AutoSatellite(parts, VesselShape.Candidates(parts)));
        }

        // A lander that flies on alone climbs to the next destination's orbit, not to the orbit the rest waits in.
        [TestMethod]
        public void ALanderThatFliesOnAloneClimbsToTheDestinationNotToTheRest()
        {
            RouteStop high = Orbit(Mun, 500000);
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Surface(Mun), high };
            List<RouteStep> steps = Route(Stock(), goals, true, false, false, true, false);

            Assert.AreEqual(500000.0, steps[3].To.Altitude, "up to the 500 km orbit");
        }

        // A Kerbal X lander that flies on alone lands, climbs and goes home without docking, the capsule landing
        // on its parachutes; one that docks comes back to the rest.
        [TestMethod]
        public void ALanderThatFliesOnAloneGoesHomeByItself()
        {
            // The Kerbal X to the Mun and home: the lander lands, climbs and flies home
            // alone, and the capsule lands on its parachutes; no docking.
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Surface(Mun), Surface(Kerbin, true) };
            List<RouteStep> alone = Route(Stock(), goals, true, false, true, true, false);
            List<RouteStep> docks = Route(Stock(), goals, true, false, true, true, true);

            Assert.AreEqual("1L:S 1T:S 1L:L< 2L:L 2T:L 2L:C<", Shape(alone));
            Assert.IsFalse(alone.Any(step => step.Docks || step.Leg.Lines.Any(line => line.Name == "Rendezvous and docking")));
            Assert.IsTrue(alone.All(step => step.Problem == null));
            Assert.AreEqual("1L:S 1T:S 1L:L< 2L:L> 2T:S 2L:C<", Shape(docks), "one that docks comes back to the rest");
        }

        // The pod on top with its parachute (0), a decoupler (1), the lander with a
        // probe core, an engine and legs (2-4), a decoupler (5), the transfer stage (6),
        // and a launch clamp on it (7).
        private static List<PartFacts> LanderOnTop(bool clampKnown)
        {
            List<PartFacts> parts = new List<PartFacts>
            {
                Part(-1, -1, 1.0, "Mk1 Command Pod", command: true, crew: 1), Part(0, 1, 0.05, "TD-06", separator: Separation.Decoupler),
                Part(1, 1, 0.2, "OKTO", command: true), Part(2, 1, 0.5, "Terrier", engine: true),
                Part(2, 1, 0.2, "LT-2 Landing Strut", legs: true), Part(3, 3, 0.05, "TD-12", separator: Separation.Decoupler),
                Part(5, 3, 6.0, "FL-T800"), Part(6, -1, 1.0, "TT18-A Launch Stability Enhancer"),
            };
            parts[7].Clamp = clampKnown;
            return parts;
        }

        // The lander leaves out the launch clamps, known as such or not, and what is no longer joined to it.
        [TestMethod]
        public void ALanderLeavesOutTheLaunchClampsAndWhatIsNoLongerJoinedToIt()
        {
            foreach (bool known in new[] { true, false })
            {
                List<PartGroupChoice> groups = VesselShape.Candidates(LanderOnTop(known));
                PartGroupChoice lander = groups.Single(group => group.Members.Contains(3) && group.Members.Contains(2)
                                                                && !group.Members.Contains(0));
                CollectionAssert.AreEquivalent(new[] { 2, 3, 4 }, lander.Members.ToArray(), "clamp known: " + known);
                Assert.IsFalse(groups.Any(group => group.Members.Contains(7) && group.Command), "no clamp with a command part");
            }
        }

        // A docking port on the nose with nothing beyond it cuts nothing off; the decoupler separates the lander.
        [TestMethod]
        public void ADockingPortWithNothingBeyondItCutsNothingOff()
        {
            // A port on the pod's nose, and legs on the lander stage under the pod: the
            // lander is separated by the decoupler, not by the port.
            List<PartFacts> parts = LanderOnTop(true);
            parts.Add(Part(0, -1, 0.05, "Clamp-O-Tron Jr", separator: Separation.DockingPort));
            List<PartGroupChoice> groups = VesselShape.Candidates(parts);

            Assert.IsFalse(groups.Any(group => group.DockingPort), "no cut at the nose port");
            Assert.IsTrue(groups.Any(group => group.Legs && group.Command && !group.DockingPort), "the decoupler separates the lander");
        }

        // Two docking ports joined in the editor give the lander once, cut at the lower port.
        [TestMethod]
        public void TwoDockingPortsJoinedInTheEditorGiveTheLanderOnce()
        {
            List<PartGroupChoice> groups = VesselShape.Candidates(Apollo());

            Assert.IsTrue(groups.Any(group => group.Members.SetEquals(new[] { 6, 7, 8, 9, 10, 11, 12 })), "the lower port's cut");
            Assert.IsFalse(groups.Any(group => group.Members.SetEquals(new[] { 5, 6, 7, 8, 9, 10, 11, 12 })), "not the upper port's");
        }

        // A lander's radial drop tanks, decoupled a stage before its legs, stay with it; the transfer stage does not.
        [TestMethod]
        public void ALandersDropTanksStayWithIt()
        {
            // Radial drop tanks on the descent stage (16), decoupled a stage before its
            // legs, while its engine fires from that stage on.
            List<PartFacts> parts = Apollo();
            parts[12].ActivatesAt = 3;
            parts.Add(Part(10, 3, 1.0, "FL-T100 drop tank"));
            PartGroupChoice lander = VesselShape.Candidates(parts).Single(group => group.DockingPort && group.Legs);

            Assert.IsTrue(lander.Members.Contains(16), "the drop tank flies with the lander");
            Assert.IsFalse(lander.Members.Contains(14), "the transfer stage does not");
        }
    }
}
