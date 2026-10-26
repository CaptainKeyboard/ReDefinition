using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Core;

namespace ReDefinition.Tests
{
    // The command line reader (src/Core/CommandLine.cs): a switch the test host was not started with is not found.
    [TestClass]
    public class CommandLineTests
    {
        // A switch that is not given is reported absent, and nothing follows it; the reader reads the line and
        // returns, which a reader that called itself would not.
        [TestMethod]
        public void ASwitchNotGivenIsAbsent()
        {
            Assert.IsFalse(CommandLine.Has("-redefinitionNoSuchSwitch"));
            Assert.IsNull(CommandLine.After("-redefinitionNoSuchSwitch"));
        }
    }
}
