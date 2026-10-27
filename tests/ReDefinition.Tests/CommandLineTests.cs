using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Core;

namespace ReDefinition.Tests
{
    // The command line reader (src/Core/CommandLine.cs): a switch the test host was not started with is not found,
    // and the readers over a given argument list read what the probes need of it.
    [TestClass]
    public class CommandLineTests
    {
        private static readonly string[] Args = { "KSP_x64.exe", "-redefinitionProbe", "-Redefinitionprobescenes", "3",
                                                  "-redefinitionProbeCaptureStep", "x", "-redefinitionProbeScale", "0.5",
                                                  "-redefinitionProbeLast" };

        // A switch that is not given is reported absent, and nothing follows it; the reader reads the line and
        // returns, which a reader that called itself would not.
        [TestMethod]
        public void ASwitchNotGivenIsAbsent()
        {
            Assert.IsFalse(CommandLine.Has("-redefinitionNoSuchSwitch"));
            Assert.IsNull(CommandLine.After("-redefinitionNoSuchSwitch"));
            Assert.IsFalse(CommandLine.Has("-redefinitionNoSuchSwitch", true));
            Assert.AreEqual(-1, CommandLine.Int("-redefinitionNoSuchSwitch", -1));
            Assert.AreEqual(2.5f, CommandLine.Float("-redefinitionNoSuchSwitch", 2.5f));
        }

        // The spelling counts unless the reader is asked to ignore the case.
        [TestMethod]
        public void TheCaseCountsOnlyWhereAsked()
        {
            Assert.IsTrue(CommandLine.Has(Args, "-redefinitionProbe", false));
            Assert.IsFalse(CommandLine.Has(Args, "-redefinitionprobe", false));
            Assert.IsTrue(CommandLine.Has(Args, "-REDEFINITIONPROBE", true));
            Assert.IsNull(CommandLine.After(Args, "-redefinitionProbeScenes", false));
            Assert.AreEqual("3", CommandLine.After(Args, "-redefinitionProbeScenes", true));
        }

        // The word after the switch is null where the switch ends the line.
        [TestMethod]
        public void ASwitchAtTheEndHasNoWordAfterIt()
        {
            Assert.IsTrue(CommandLine.Has(Args, "-redefinitionProbeLast", false));
            Assert.IsNull(CommandLine.After(Args, "-redefinitionProbeLast", false));
            Assert.AreEqual(7, CommandLine.Int(Args, "-redefinitionProbeLast", 7));
        }

        // A whole number is read as given, the fallback stands where the switch is absent, and a word that is
        // no number stops the run, as the probes' int.Parse always did.
        [TestMethod]
        public void AWholeNumberIsReadAndAMistypedOneThrows()
        {
            Assert.AreEqual(3, CommandLine.Int(Args, "-redefinitionProbeScenes", -1, true));
            Assert.AreEqual(-1, CommandLine.Int(Args, "-redefinitionProbeScenes", -1));
            Assert.ThrowsException<FormatException>(() => CommandLine.Int(Args, "-redefinitionProbeCaptureStep", 21));
        }

        // A number is read with the invariant culture; a word that is no number gives the fallback.
        [TestMethod]
        public void ANumberIsReadAndAMistypedOneFallsBack()
        {
            Assert.AreEqual(0.5f, CommandLine.Float(Args, "-redefinitionProbeScale", 1f));
            Assert.AreEqual(1f, CommandLine.Float(Args, "-redefinitionProbeCaptureStep", 1f));
            Assert.AreEqual(1f, CommandLine.Float(Args, "-redefinitionProbeLast", 1f));
        }
    }
}
