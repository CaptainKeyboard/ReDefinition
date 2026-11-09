using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Core;

namespace ReDefinition.Tests
{
    // What the host does where the planner is not installed: the hooks are null and the helpers that read through them stay quiet.
    [TestClass]
    public class PlannerHooksTests
    {
        [TestMethod]
        public void WithoutThePlannerTheHooksAreEmpty()
        {
            Assert.IsNull(PlannerHooks.Guidance);
            Assert.IsNull(PlannerHooks.Routes);
        }

        [TestMethod]
        public void WithoutAnythingToSkipTheFirstItemCounts()
        {
            Assert.AreEqual("a", StockNodes.First(new[] { "a", "b" }, null));
        }

        [TestMethod]
        public void TheGuideIsSkippedWhereTheTestKnowsIt()
        {
            Assert.AreEqual("b", StockNodes.First(new[] { "guide", "b" }, n => n == "guide"));
        }

        [TestMethod]
        public void NoItemsOrOnlyTheGuideGiveNull()
        {
            Assert.IsNull(StockNodes.First<string>(null, null));
            Assert.IsNull(StockNodes.First(new string[0], null));
            Assert.IsNull(StockNodes.First(new[] { "guide" }, n => n == "guide"));
        }

        [TestMethod]
        public void TheContractsSayNothingByDefault()
        {
            GuidanceHook guidance = new Empty();
            Assert.IsFalse(guidance.Current(null));
            Assert.IsFalse(guidance.HasBar);
            Assert.IsFalse(guidance.HoldsNode(null));
            Assert.IsFalse(guidance.IsGuideNode(null));
            RouteHook routes = new EmptyRoutes();
            Assert.AreEqual("", routes.Status);
            Assert.IsFalse(routes.NavigationOn(null));
            Assert.IsFalse(routes.AutopilotOn(null));
        }

        private sealed class Empty : GuidanceHook { }
        private sealed class EmptyRoutes : RouteHook { }
    }
}
