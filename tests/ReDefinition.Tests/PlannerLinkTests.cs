using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Planner;

namespace ReDefinition.Tests
{
    // ReDefinition and the planner are released apart; ReDefinition fills the planner's seams only when both name the same level.
    [TestClass]
    public class PlannerLinkTests
    {
        [TestMethod]
        public void ReDefinitionAndThePlannerNameTheSameSeamLevel()
        {
            Assert.AreEqual(PlannerLink.Expected, PlannerSeams.Level, "Raise both together when a seam is added, removed or changes its meaning.");
        }

        [TestMethod]
        public void ThePlannerStandsAloneUntilReDefinitionFillsTheSeams()
        {
            Assert.IsTrue(PlannerSeams.StandsAlone());
            Assert.AreEqual(1f, PlannerSeams.WorldScale());
            Assert.IsNull(PlannerSeams.SpaceCentreButton("Buttons_VAB"));
        }
    }
}
