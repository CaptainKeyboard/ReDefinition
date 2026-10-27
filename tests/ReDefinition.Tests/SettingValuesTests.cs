using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Settings;

namespace ReDefinition.Tests
{
    // How two setting values are compared (SettingValues.Same): as the values they
    // are where both are numbers, switches or lists of numbers, and exactly otherwise.
    [TestClass]
    public class SettingValuesTests
    {
        [TestMethod]
        public void NumbersAndSwitchesCompareAsValues()
        {
            Assert.IsTrue(SettingValues.Same("0.9", "0.899999976"));
            Assert.IsTrue(SettingValues.Same("true", "True"));
            Assert.IsFalse(SettingValues.Same("0.9", "0.8"));
        }

        [TestMethod]
        public void TextCompareExactlyAndNullOnlyMatchesNull()
        {
            Assert.IsTrue(SettingValues.Same("Low", "Low"));
            Assert.IsFalse(SettingValues.Same("Low", "low"));
            Assert.IsTrue(SettingValues.Same(null, null));
            Assert.IsFalse(SettingValues.Same(null, ""));
        }

        [TestMethod]
        public void ListsOfNumbersCompareItemByItem()
        {
            Assert.IsTrue(SettingValues.Same("1,2,3", "1.0, 2, 3.000001"));
            Assert.IsFalse(SettingValues.Same("1,2,3", "1,2,4"));
            Assert.IsFalse(SettingValues.Same("1,2,3", "1,2"));
        }

        [TestMethod]
        public void ACommaOnOneSideOnlyIsNoList()
        {
            Assert.IsFalse(SettingValues.Same("1,2", "12"));
            Assert.IsFalse(SettingValues.Same("12", "1,2"));
            Assert.IsFalse(SettingValues.Same("Low,High", "Low"));
            Assert.IsFalse(SettingValues.Same("Low", "Low,High"));
        }
    }
}
