using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Core;

namespace ReDefinition.Tests
{
    // The shared seams work alone; ReDefinition and the planner fill them only when installed.
    [TestClass]
    public class PlannerLinkTests
    {
        [TestMethod]
        public void TheSeamsStandAloneUntilAHostFillsThem()
        {
            Assert.IsTrue(HostSeams.StandsAlone());
            Assert.AreEqual(1f, HostSeams.WorldScale());
            Assert.IsTrue(HostSeams.BackgroundRoute());
            Assert.IsTrue(HostSeams.WorldButtons());
            Assert.IsNull(HostSeams.SpaceCentreButton("Buttons_VAB"));
        }
    }
}
