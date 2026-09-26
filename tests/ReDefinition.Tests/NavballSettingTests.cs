using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReDefinition.Tests
{
    // Where the navball stands (OurModules.FlightUi, Flight/NavballDrag): its row
    // and how it names a position.
    [TestClass]
    public class NavballSettingTests
    {
        private static ModuleSetting Row()
        {
            return OurModules.FlightUi.Settings.Single(setting => setting.Key == "navballOffset");
        }

        [TestMethod]
        public void TheCentreIsNamedAndSidesByTheirDistance()
        {
            Assert.AreEqual("Centre", Row().Label("0"));
            Assert.AreEqual("300 left", Row().Label("-300"));
            Assert.AreEqual("120 right", Row().Label("120"));
        }

        [TestMethod]
        public void ItIsSavedAndReadBackAsAWholeNumber()
        {
            OwnSettings settings = new OwnSettings();
            Row().Write(settings, "-250");
            Assert.AreEqual(-250f, settings.NavballOffset);
            Assert.AreEqual("-250", Row().Read(settings));
        }

        [TestMethod]
        public void NoProfileIsNeededToMoveIt()
        {
            Assert.IsFalse(Row().NeedsProfile);
        }
    }
}
