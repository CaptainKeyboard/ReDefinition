using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Core;
using ReDefinition.Dashboard;

namespace ReDefinition.Tests
{
    // The dashboard's VAB and SPH page as the mod gives it: its keys and commands known in every
    // scene. What they read needs the game's editor, and is seen in it.
    [TestClass]
    public class EditorTelemetryTests
    {
        // The editor page's readings and commands are known outside the editor too: an unknown key is
        // answered to the page as unknown, so a page opened in another scene would stop asking for it.
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

        // Without the planner the editor page has no plan and no list, and its commands do nothing.
        [TestMethod]
        public void WithoutThePlannerThePlanKeysAreEmptyAndTheCommandsQuiet()
        {
            EditorPlanHook saved = PlannerHooks.EditorPlan;
            PlannerHooks.EditorPlan = null;
            try
            {
                EditorTelemetry editor = new EditorTelemetry();
                Assert.IsNull(editor.Plan());
                Assert.IsNull(editor.Choices());
                EditorTelemetry.Change(new[] { "margin", "10" });
                editor.AskChoices(new[] { "body", "1", "0" });
                EditorTelemetry.Pick(new[] { "body", "1", "0", "4", "Kerbin" });
                Assert.IsNull(editor.Choices());
            }
            finally
            {
                PlannerHooks.EditorPlan = saved;
            }
        }

        // The defaults of the hook say that there is no planner page: nothing to show, nothing to change.
        [TestMethod]
        public void TheEditorPlanHooksDefaultsOfferNothing()
        {
            EditorPlanHook hook = new EmptyPlan();
            Assert.IsNull(hook.Snapshot());
            Assert.IsFalse(hook.Change("margin", new string[0]));
            Assert.AreEqual(0, hook.ChoicesFor("body", 0, 0).Count);
            Assert.IsFalse(hook.Pick("body", 0, 0, 0, "Kerbin"));
            Assert.IsNull(new EmptyMarks().Marks(null));
        }

        private sealed class EmptyPlan : EditorPlanHook { }

        private sealed class EmptyMarks : DryMarksHook { }
    }
}
