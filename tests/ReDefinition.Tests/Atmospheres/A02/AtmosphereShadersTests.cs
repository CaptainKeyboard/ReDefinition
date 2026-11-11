using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.A02
{
    // The shader loader answers with an error string and no exception where the bundle or the shader is missing. (A bundle that exists
    // needs the game's engine; the missing-file paths are the ones that run without it.)
    [TestClass]
    public class AtmosphereShadersTests
    {
        [TestCleanup]
        public void Clean()
        {
            AtmosphereShaders.Reset();
        }

        [TestMethod]
        public void AMissingBundleGivesAnErrorString()
        {
            string path = Path.Combine(Path.GetTempPath(), "no-such-atmo-bundle-" + System.Guid.NewGuid().ToString("N") + ".shaders");
            AtmosphereShaders.Reset(path);
            string error;
            Assert.IsNull(AtmosphereShaders.Get("ReDefinition/AtmospheresNothing", out error));
            Assert.IsNotNull(error);
            StringAssert.Contains(error, "missing");
            StringAssert.Contains(error, path);
        }

        [TestMethod]
        public void TheFailureIsCachedPerName()
        {
            AtmosphereShaders.Reset(Path.Combine(Path.GetTempPath(), "no-such-atmo-bundle.shaders"));
            string first, again, other;
            Assert.IsNull(AtmosphereShaders.Get("A", out first));
            Assert.IsNull(AtmosphereShaders.Get("A", out again));
            Assert.IsNull(AtmosphereShaders.Get("B", out other));
            Assert.AreEqual(first, again);
            Assert.IsNotNull(other);
        }

        [TestMethod]
        public void AnEmptyNameGivesAnErrorString()
        {
            string error;
            Assert.IsNull(AtmosphereShaders.Get("", out error));
            Assert.IsNotNull(error);
            Assert.IsNull(AtmosphereShaders.Get(null, out error));
            Assert.IsNotNull(error);
        }
    }
}
