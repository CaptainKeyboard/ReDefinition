using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.A01
{
    // The module registry: discovery by attribute, order, isolation of a throwing module, retries of Init, and the pieces next to it
    // (the tuning table, the body hysteresis).
    [TestClass]
    public class RegistryTests
    {
        private static readonly List<string> Calls = new List<string>();

        private abstract class Recorder : IAtmospheresModule
        {
            private readonly string name;

            protected Recorder(string name)
            {
                this.name = name;
            }

            public virtual void Init() { Calls.Add(name + ".Init"); }
            public virtual void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody) { Calls.Add(name + ".Body"); }
            public virtual void Tick(float dt) { Calls.Add(name + ".Tick"); }
            public virtual void Shutdown() { Calls.Add(name + ".Shutdown"); }
        }

        [AtmospheresModule(20)]
        private sealed class SecondModule : Recorder
        {
            public SecondModule() : base("second") { }
        }

        [AtmospheresModule(10)]
        private sealed class FirstModule : Recorder
        {
            public FirstModule() : base("first") { }
        }

        // Has the interface but no attribute: not a module of the registry.
        private sealed class UnmarkedModule : Recorder
        {
            public UnmarkedModule() : base("unmarked") { }
        }

        private sealed class Throwing : Recorder
        {
            internal int InitFailures;
            internal bool ThrowInTick;
            internal bool ThrowInBody;
            private int inits;

            public Throwing() : base("throwing") { }

            public override void Init()
            {
                inits++;
                if (inits <= InitFailures) throw new InvalidOperationException("init " + inits);
                base.Init();
            }

            public override void Tick(float dt)
            {
                if (ThrowInTick) throw new InvalidOperationException("tick");
                base.Tick(dt);
            }

            public override void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
            {
                if (ThrowInBody) throw new InvalidOperationException("body");
                base.OnBodyChanged(oldBody, newBody);
            }
        }

        private sealed class Counting : Recorder
        {
            internal int Ticks;
            public Counting() : base("counting") { }
            public override void Tick(float dt) { Ticks++; }
        }

        [TestInitialize]
        public void Clear()
        {
            Calls.Clear();
        }

        [TestMethod]
        public void TheRegistryFindsOnlyMarkedModulesAndOrdersThemByTheirNumber()
        {
            List<Type> found = ModuleRegistry.Discover(typeof(RegistryTests).Assembly);
            CollectionAssert.AreEqual(new[] { typeof(FirstModule), typeof(SecondModule) }, found);
            var warnings = new List<string>();
            ModuleRegistry registry = ModuleRegistry.FromAssembly(typeof(RegistryTests).Assembly, warnings.Add);
            Assert.AreEqual(2, registry.Count);
            registry.Tick(0f, 0.016f);
            CollectionAssert.AreEqual(new[] { "first.Init", "first.Tick", "second.Init", "second.Tick" }, Calls);
            Assert.AreEqual(0, warnings.Count);
        }

        [TestMethod]
        public void TwoModulesAreStartedInOrderAndShutDownInReverse()
        {
            var registry = new ModuleRegistry(s => { });
            registry.Add(new SecondModule(), 20);
            registry.Add(new FirstModule(), 10);
            registry.Tick(0f, 0.1f);
            registry.Tick(0.1f, 0.1f);
            registry.BodyChanged(null, null);
            registry.Shutdown();
            registry.Shutdown();
            CollectionAssert.AreEqual(new[]
            {
                "first.Init", "first.Tick", "second.Init", "second.Tick",
                "first.Tick", "second.Tick",
                "first.Body", "second.Body",
                "second.Shutdown", "first.Shutdown"
            }, Calls);
        }

        [TestMethod]
        public void AModuleThatThrowsInTickIsSwitchedOffAfterFiveAndTheOthersKeepRunning()
        {
            var warnings = new List<string>();
            var registry = new ModuleRegistry(warnings.Add);
            var bad = new Throwing { ThrowInTick = true };
            var good = new Counting();
            registry.Add(bad, 1);
            registry.Add(good, 2);
            for (int i = 0; i < 4; i++) registry.Tick(i, 0.1f);
            Assert.AreEqual(ModuleState.Running, registry.StateOf(bad), "four failures are not enough");
            registry.Tick(4f, 0.1f);
            Assert.AreEqual(ModuleState.Failed, registry.StateOf(bad));
            Assert.AreEqual(ModuleState.Running, registry.StateOf(good));
            Assert.IsTrue(Calls.Contains("throwing.Shutdown"), "it is told to shut down once");
            registry.Tick(5f, 0.1f);
            Assert.AreEqual(6, good.Ticks, "the good module ticked every frame");
            Assert.AreEqual(5, warnings.Count);
            StringAssert.Contains(warnings[4], "switched off");
            Assert.AreEqual(1, Calls.Count(c => c == "throwing.Shutdown"));
        }

        [TestMethod]
        public void AFailureStreakIsBrokenByASuccess()
        {
            var registry = new ModuleRegistry(s => { });
            var flaky = new Throwing { ThrowInTick = true };
            registry.Add(flaky, 1);
            for (int i = 0; i < 4; i++) registry.Tick(i, 0.1f);
            flaky.ThrowInTick = false;
            registry.Tick(4f, 0.1f);
            flaky.ThrowInTick = true;
            for (int i = 5; i < 9; i++) registry.Tick(i, 0.1f);
            Assert.AreEqual(ModuleState.Running, registry.StateOf(flaky));
        }

        [TestMethod]
        public void AnInitThatFailsIsTriedAgainAndGivesUpAfterThree()
        {
            var warnings = new List<string>();
            var registry = new ModuleRegistry(warnings.Add);
            var recovers = new Throwing { InitFailures = 1 };
            var hopeless = new Throwing { InitFailures = 99 };
            var other = new Counting();
            registry.Add(recovers, 1);
            registry.Add(hopeless, 2);
            registry.Add(other, 3);
            registry.Tick(0f, 0.1f);
            Assert.AreEqual(ModuleState.Waiting, registry.StateOf(recovers));
            Assert.AreEqual(ModuleState.Running, registry.StateOf(other), "a module that starts is not held back by one that does not");
            registry.Tick(1f, 0.1f);
            Assert.AreEqual(ModuleState.Waiting, registry.StateOf(recovers), "the retry waits for its interval");
            registry.Tick(2.5f, 0.1f);
            Assert.AreEqual(ModuleState.Running, registry.StateOf(recovers));
            registry.Tick(5f, 0.1f);
            Assert.AreEqual(ModuleState.Failed, registry.StateOf(hopeless), "three tries used");
            Assert.AreEqual(ModuleState.Running, registry.StateOf(other));
            Assert.AreEqual(4, warnings.Count, "one failed try of the first, three of the second");
        }

        [TestMethod]
        public void AModuleThatThrowsOnABodyChangeDoesNotKeepTheOthersFromIt()
        {
            var registry = new ModuleRegistry(s => { });
            var bad = new Throwing { ThrowInBody = true };
            registry.Add(bad, 1);
            registry.Add(new SecondModule(), 2);
            registry.Tick(0f, 0.1f);
            registry.BodyChanged(null, null);
            Assert.IsTrue(Calls.Contains("second.Body"));
            Assert.AreEqual(ModuleState.Running, registry.StateOf(bad));
        }

        [TestMethod]
        public void AModuleThatIsNotInTheRegistryHasNoState()
        {
            var registry = new ModuleRegistry(s => { });
            Assert.ThrowsException<ArgumentException>(() => registry.StateOf(new FirstModule()));
            Assert.ThrowsException<ArgumentNullException>(() => registry.Add(null, 1));
        }

        [TestMethod]
        public void TheTuningTableTakesNodesOverridesAndTheCompiledDefaultInThatOrder()
        {
            var table = new TuningTable();
            Assert.AreEqual(3f, table.Get("a", 3f), "nothing loaded: the compiled value");
            var node = new ConfigNode();
            node.AddValue("a", "1.5");
            node.AddValue("b", "oops");
            node.AddValue("c", "NaN");
            var later = new ConfigNode();
            later.AddValue("a", "2.5");
            table.Load(new[] { node, later });
            Assert.AreEqual(3f, table.Get("a", 3f), "a loaded value is seen from the next frame");
            table.BeginFrame(1);
            Assert.AreEqual(2.5f, table.Get("a", 3f), "later nodes win");
            Assert.AreEqual(7f, table.Get("b", 7f), "not a number is skipped");
            Assert.AreEqual(8f, table.Get("c", 8f), "NaN is skipped");
            long version = table.Version;
            table.SetOverride("a", 9f);
            Assert.AreEqual(2.5f, table.Get("a", 3f), "an override waits for the frame boundary");
            table.BeginFrame(1);
            Assert.AreEqual(2.5f, table.Get("a", 3f), "the same frame does not swap");
            table.BeginFrame(2);
            Assert.AreEqual(9f, table.Get("a", 3f));
            Assert.IsTrue(table.Version > version);
            table.ClearOverrides();
            table.BeginFrame(3);
            Assert.AreEqual(2.5f, table.Get("a", 3f));
            long settled = table.Version;
            table.BeginFrame(4);
            Assert.AreEqual(settled, table.Version, "no change, no new table");
        }

        [TestMethod]
        public void TheTuningTableAcceptsEmptyAndNullNodes()
        {
            var table = new TuningTable();
            table.Load(null);
            table.Load(new ConfigNode[] { null, new ConfigNode() });
            table.BeginFrame(1);
            Assert.AreEqual(4f, table.Get("x", 4f));
        }

        [TestMethod]
        public void TheStaticTuningFallsBackToTheCompiledValueAndTakesOverrides()
        {
            Tuning.ClearOverrides();
            Assert.AreEqual(0.42f, Tuning.Get("test.unknownKey", 0.42f));
            Assert.IsTrue(Tuning.Flag("test.unknownFlag", true));
            Tuning.SetOverride("test.unknownKey", 1.25f);
            Tuning.Instance.BeginFrame(int.MaxValue - 1);
            try
            {
                Assert.AreEqual(1.25f, Tuning.Get("test.unknownKey", 0.42f));
            }
            finally
            {
                Tuning.ClearOverrides();
                Tuning.Instance.BeginFrame(int.MaxValue);
            }
            Assert.AreEqual(0.42f, Tuning.Get("test.unknownKey", 0.42f));
        }

        [TestMethod]
        public void TheBodyChangesOnlyAfterFiveSecondsExceptOnTheFirstSightAndOnASceneLoad()
        {
            var rule = new BodyHysteresis();
            Assert.IsTrue(rule.Observe(0f, 1, false), "the first sight counts at once");
            Assert.AreEqual(1, rule.Active);
            Assert.IsFalse(rule.Observe(1f, 2, false));
            Assert.IsFalse(rule.Observe(5.9f, 2, false), "5 s have not passed since it was first seen at 1 s");
            Assert.IsTrue(rule.Observe(6.1f, 2, false));
            Assert.AreEqual(2, rule.Active);
            Assert.AreEqual(1, rule.Previous);
            Assert.IsFalse(rule.Observe(7f, 3, false));
            Assert.IsFalse(rule.Observe(8f, 2, false), "back on the old body: the candidate is dropped");
            Assert.IsFalse(rule.Observe(9f, 3, false));
            Assert.IsFalse(rule.Observe(13.9f, 3, false), "the timer started again at 9 s");
            Assert.IsTrue(rule.Observe(14.1f, 3, false));
            Assert.IsTrue(rule.Observe(15f, 4, true), "a scene load counts at once");
            Assert.AreEqual(4, rule.Active);
        }

        [TestMethod]
        public void TheOldBodyFadesOverASecondAfterAChangeAndNotAfterASceneLoad()
        {
            var rule = new BodyHysteresis();
            Assert.AreEqual(0f, rule.OldBodyIntensity(0f), "no old body yet");
            rule.Observe(0f, 1, false);
            Assert.AreEqual(0f, rule.OldBodyIntensity(0f));
            Assert.IsFalse(rule.Observe(10f, 2, false));
            Assert.IsTrue(rule.Observe(15.5f, 2, false));
            Assert.IsFalse(rule.Observe(20f, 3, false));
            Assert.IsTrue(rule.Observe(25.1f, 3, false));
            Assert.AreEqual(1f, rule.OldBodyIntensity(25.1f), 1e-5f);
            Assert.AreEqual(0.5f, rule.OldBodyIntensity(25.6f), 1e-4f);
            Assert.AreEqual(0f, rule.OldBodyIntensity(26.2f));
            rule.Observe(30f, 9, true);
            Assert.AreEqual(0f, rule.OldBodyIntensity(30f), "a scene load leaves nothing to fade");
        }

        [TestMethod]
        public void TheAttributeKeepsItsOrder()
        {
            var attribute = (AtmospheresModuleAttribute)typeof(FirstModule).GetCustomAttribute(typeof(AtmospheresModuleAttribute));
            Assert.AreEqual(10, attribute.Order);
            Assert.IsNull(typeof(UnmarkedModule).GetCustomAttribute(typeof(AtmospheresModuleAttribute)));
        }
    }
}
