using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Proxy;

namespace ReDefinition.Tests
{
    // The text the proxy answers into a builder (NativeText) is kept as one string until the answer changes.
    [TestClass]
    public class NativeTextTests
    {
        [TestMethod]
        public void TheSameTextKeepsTheSameString()
        {
            StringBuilder buffer = new StringBuilder("FSR 4.1.1");
            string source = null;

            Assert.IsTrue(NativeText.Refresh(buffer, ref source));
            string first = source;
            Assert.AreEqual("FSR 4.1.1", first);

            buffer.Length = 0;
            buffer.Append("FSR 4.1.1");
            Assert.IsFalse(NativeText.Refresh(buffer, ref source));
            Assert.AreSame(first, source);
        }

        [TestMethod]
        public void ADifferentTextReplacesIt()
        {
            StringBuilder buffer = new StringBuilder("Reflex");
            string source = "Anti-Lag 2";

            Assert.IsTrue(NativeText.Refresh(buffer, ref source));
            Assert.AreEqual("Reflex", source);

            buffer.Append("!");
            Assert.IsTrue(NativeText.Refresh(buffer, ref source));
            Assert.AreEqual("Reflex!", source);
        }

        [TestMethod]
        public void SameComparesLengthAndEveryChar()
        {
            Assert.IsTrue(NativeText.Same(new StringBuilder(), ""));
            Assert.IsFalse(NativeText.Same(new StringBuilder("ab"), "abc"));
            Assert.IsFalse(NativeText.Same(new StringBuilder("abd"), "abc"));
        }
    }
}
