using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Settings.Behaviours;

namespace ReDefinition.Tests
{
    // KSP's UI scale default by the screen's height (KspBehaviour.Default).
    [TestClass]
    public class UiScaleDefaultTests
    {
        [TestMethod]
        public void FullHdAndBelowKeepKspsOwnHundredPercent()
        {
            Assert.AreEqual("1", KspBehaviour.UiScaleFor(720));
            Assert.AreEqual("1", KspBehaviour.UiScaleFor(1080));
            Assert.AreEqual("1", KspBehaviour.UiScaleFor(1200));
        }

        [TestMethod]
        public void From1440LinesItIsOneAndAHalf()
        {
            Assert.AreEqual("1.5", KspBehaviour.UiScaleFor(1440));
            Assert.AreEqual("1.5", KspBehaviour.UiScaleFor(1600));
        }

        [TestMethod]
        public void From4kItIsTwo()
        {
            Assert.AreEqual("2", KspBehaviour.UiScaleFor(2160));
            Assert.AreEqual("2", KspBehaviour.UiScaleFor(2880));
        }
    }
}
