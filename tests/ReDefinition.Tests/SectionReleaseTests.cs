using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Engineer;

namespace ReDefinition.Tests
{
    // The part a section is let go by, as the planner names it: the separator in the group's key.
    [TestClass]
    public class SectionReleaseTests
    {
        // The satellite of each stock craft that has one is cut off by a part that separates, and its id is in the craft's parts.
        [TestMethod]
        public void TheSeparatorOfASatelliteIsAPartThatSeparates()
        {
            int found = 0;
            foreach (string name in new[] { "Ariane 5", "ComSat Lx", "GDLV3", "Kerbal X", "Dynawing" })
            {
                List<PartFacts> parts = StockCrafts.Named(name).Parts;
                PartGroupChoice satellite = VesselShape.AutoSatellite(parts, VesselShape.Candidates(parts));
                if (satellite == null) continue;
                found++;
                // The fixtures carry no persistent ids, so the key's part is told by the id it holds, which is zero there.
                Assert.IsTrue(satellite.Key.Contains(":"), name + ": the key " + satellite.Key + " has no separator and side.");
                uint id = VesselShape.SeparatorId(satellite);
                Assert.IsTrue(parts.Any(part => part.Id == id && part.Separator != Separation.None), name + ": no separating part has the id " + id + ".");
            }
            Assert.IsTrue(found >= 1, "No stock craft has a satellite to try.");
        }

        // A key with no separator names no part.
        [TestMethod]
        public void AKeyWithNoSeparatorNamesNoPart()
        {
            Assert.AreEqual(0u, VesselShape.SeparatorId(null));
            Assert.AreEqual(0u, VesselShape.SeparatorId(new PartGroupChoice { Key = "nothing" }));
            Assert.AreEqual(77u, VesselShape.SeparatorId(new PartGroupChoice { Key = "77:1" }));
        }
    }
}
