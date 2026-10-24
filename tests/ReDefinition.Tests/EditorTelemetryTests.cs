using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Dashboard;

namespace ReDefinition.Tests
{
    // The dashboard's VAB and SPH page as the mod gives it: its keys and commands known in every
    // scene. What they read needs the game's editor, and is seen in it.
    [TestClass]
    public class EditorTelemetryTests
    {
        [TestMethod]
        public void TheEditorsKeysAndCommandsAreKnownInEveryScene()
        {
            Telemetry telemetry = new Telemetry();
            foreach (string key in new[]
                     {
                         "e.ship", "e.plan", "e.choiceList", "e.change[margin,10]", "e.choices[body,1,0]", "e.pick[body,1,0,4,Kerbin]",
                     })
                Assert.IsTrue(telemetry.IsKnown(key), key);
        }
    }
}
