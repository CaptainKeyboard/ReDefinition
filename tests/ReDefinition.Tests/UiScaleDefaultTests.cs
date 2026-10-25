using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Settings.Behaviours;

namespace ReDefinition.Tests
{
    // KSP's UI scale default by the screen's height (KspBehaviour.Default).
    [TestClass]
    public class UiScaleDefaultTests
    {
        // Below 1440 lines the default stays KSP's own 100 %.
        [TestMethod]
        public void FullHdAndBelowKeepKspsOwnHundredPercent()
        {
            Assert.AreEqual("1", KspBehaviour.UiScaleFor(720));
            Assert.AreEqual("1", KspBehaviour.UiScaleFor(1080));
            Assert.AreEqual("1", KspBehaviour.UiScaleFor(1200));
        }

        // From 1440 lines up to below 2160 the default is 150 %.
        [TestMethod]
        public void From1440LinesItIsOneAndAHalf()
        {
            Assert.AreEqual("1.5", KspBehaviour.UiScaleFor(1440));
            Assert.AreEqual("1.5", KspBehaviour.UiScaleFor(1600));
        }

        // From 2160 lines (4K) the default is 200 %.
        [TestMethod]
        public void From4kItIsTwo()
        {
            Assert.AreEqual("2", KspBehaviour.UiScaleFor(2160));
            Assert.AreEqual("2", KspBehaviour.UiScaleFor(2880));
        }
    }
}
