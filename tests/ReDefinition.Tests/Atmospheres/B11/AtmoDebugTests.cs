using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.B11
{
    // The shader global of the debug overlay: the contribution mask is 1 (what AtmoContribution.cginc compares), and the global is
    // written when the mode changes, not every frame.
    [TestClass]
    public class AtmoDebugTests
    {
        [TestMethod]
        public void The_modes_map_to_the_numbers_the_include_expects()
        {
            Assert.AreEqual(0f, AtmoDebug.ValueOf(AtmoDebugMode.Off));
            Assert.AreEqual(1f, AtmoDebug.ValueOf(AtmoDebugMode.Contrib));
            Assert.AreEqual(2f, AtmoDebug.ValueOf(AtmoDebugMode.Level));
            Assert.AreEqual(3f, AtmoDebug.ValueOf(AtmoDebugMode.Maps));
        }

        [TestMethod]
        public void Apply_writes_the_global_on_the_first_call_and_on_a_change_only()
        {
            var written = new List<string>();
            var previous = AtmoDebug.SetGlobal;
            try
            {
                AtmoDebug.SetGlobal = (name, value) => written.Add(name + "=" + value);
                AtmoDebug.Reset();
                AtmoDebug.Apply(AtmoDebugMode.Off);
                AtmoDebug.Apply(AtmoDebugMode.Off);
                AtmoDebug.Apply(AtmoDebugMode.Contrib);
                AtmoDebug.Apply(AtmoDebugMode.Contrib);
                AtmoDebug.Apply(AtmoDebugMode.Off);
                CollectionAssert.AreEqual(new[] { "_AtmoDebug=0", "_AtmoDebug=1", "_AtmoDebug=0" }, written);
            }
            finally
            {
                AtmoDebug.SetGlobal = previous;
                AtmoDebug.Reset();
            }
        }
    }
}
