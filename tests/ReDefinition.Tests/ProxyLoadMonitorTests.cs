using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Proxy;

namespace ReDefinition.Tests
{
    // How the proxy's load answer (KspPerfLoad) becomes a state, and what is read without a proxy.
    [TestClass]
    public class ProxyLoadMonitorTests
    {
        // The proxy's three answers: above 0 figures, 0 none yet, below 0 switched off in the ini.
        [TestMethod]
        public void TheAnswerIsReadByItsSign()
        {
            Assert.AreEqual(FrameGenerationBridge.LoadState.Measured, ProxyLoadMonitor.StateOf(1));
            Assert.AreEqual(FrameGenerationBridge.LoadState.NotYet, ProxyLoadMonitor.StateOf(0));
            Assert.AreEqual(FrameGenerationBridge.LoadState.SwitchedOff, ProxyLoadMonitor.StateOf(-1));
        }

        // Without the proxy no figure is known: each stays at -1 and the state is Missing.
        [TestMethod]
        public void WithoutTheProxyNoFigureIsKnown()
        {
            float main, render, gpu, process;
            FrameGenerationBridge.LoadState state = ProxyLoadMonitor.Read(out main, out render, out gpu, out process);

            Assert.AreEqual(FrameGenerationBridge.LoadState.Missing, state);
            Assert.AreEqual(-1f, main);
            Assert.AreEqual(-1f, render);
            Assert.AreEqual(-1f, gpu);
            Assert.AreEqual(-1f, process);
        }
    }
}
