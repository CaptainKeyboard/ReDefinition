using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T16
{
    // E40 C2: an extra light (a moon) that is too faint beside the sun to be marched to (light.moonMinShare). The weather player measures the cost of a moon and the picture
    // with and without it (tools/weather_metrics/e40c1.py, cases mn<N>, mc<N>).
    [TestClass]
    public class CloudMoonShareTests
    {
        [TestMethod]
        public void AMoonOfAFewHundredthsOfTheSunIsTooFaintAndANightsMoonIsNot()
        {
            const float share = 0.03f;
            Assert.IsTrue(OwnCloudOptions.MoonTooFaint(0.027f * 2.7f, 2.7f, share), "a full Mun at 2.7 percent of a day's sun");
            Assert.IsFalse(OwnCloudOptions.MoonTooFaint(0.05f * 2.7f, 2.7f, share), "a moon at 5 percent of the sun is part of the picture");
            Assert.IsFalse(OwnCloudOptions.MoonTooFaint(0.05f, 0.01f, share), "at night the sun is the floor value and the moon is the light");
            Assert.IsFalse(OwnCloudOptions.MoonTooFaint(0.001f, 2.7f, 0f), "a share of zero is the switch off: every light above zero is marched to");
        }

        [TestMethod]
        public void TheMoonShareIsOnByDefault()
        {
            Assert.IsTrue(OwnCloudOptions.MoonMinShareDefault > 0f && OwnCloudOptions.MoonMinShareDefault < 0.1f, "a moon below a few hundredths of the sun is left out in the product");
            Assert.AreEqual("light.moonMinShare", OwnCloudOptions.TuneMoonMinShare);
        }
    }
}
