using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.A02
{
    // The assumption catalogue, the orbit overlay host and the smoke level (assemblies load and every module can be made; no game start).
    [TestClass]
    public class PlatformTests
    {
        private sealed class Layer : IOrbitOverlayLayer
        {
            internal readonly List<string> Calls;
            private readonly bool wants, throws;

            internal Layer(string name, int order, bool wants, bool throws, List<string> calls)
            {
                Name = name;
                Order = order;
                this.wants = wants;
                this.throws = throws;
                Calls = calls;
            }

            public string Name { get; private set; }
            public int Order { get; private set; }
            public bool Wants(CelestialBody body) { return wants; }

            public void Draw(CelestialBody body, bool focused, double ut)
            {
                Calls.Add(Name);
                if (throws) throw new InvalidOperationException("layer broke");
            }
        }

        [TestMethod]
        public void TheCatalogueCollectsTheMarkedClassesById()
        {
            IList<ForeignAssumption> all = AtmospheresAssumptions.All;
            Assert.IsTrue(all.Count >= 1);
            Assert.IsNotNull(AtmospheresAssumptions.Find("atmo-host-api"));
            var ids = all.Select(a => a.Id).ToList();
            CollectionAssert.AreEqual(ids.OrderBy(i => i, StringComparer.Ordinal).ToList(), ids);
            Assert.AreEqual(ids.Count, ids.Distinct().Count());
            Assert.IsFalse(AtmospheresAssumptions.Holds("no-such-assumption"));
        }

        [TestMethod]
        public void TheHostApiAssumptionIsNotBrokenWhereTheMainModIsLoaded()
        {
            ForeignAssumption host = AtmospheresAssumptions.Find("atmo-host-api");
            Assert.AreNotEqual(ForeignState.Broken, host.State, host.Problem);
            Assert.AreNotEqual(ForeignState.Absent, host.State, "the test project loads ReDefinition");
        }

        [TestMethod]
        public void AnAssumptionNamingAMissingMemberIsBroken()
        {
            var broken = new ForeignAssumption("t", "ReDefinition", "x").Type(ApiBridge.HooksTypeName).Calls(ApiBridge.HooksTypeName, "NoSuchHook");
            Assert.AreEqual(ForeignState.Broken, broken.State);
        }

        [TestMethod]
        public void TheOverlayHostDrawsLayersInOrderAndIsolatesAFailure()
        {
            var calls = new List<string>();
            var a = new Layer("a", 10, true, false, calls);
            var b = new Layer("b", 20, true, true, calls);
            var c = new Layer("c", 30, false, false, calls);
            var d = new Layer("d", 40, true, false, calls);
            foreach (Layer l in new[] { d, b, a, c }) OrbitOverlayLayers.Register(l);
            var reported = new List<string>();
            Action<string, Exception> before = OrbitOverlayHost.OnFailure;
            OrbitOverlayHost.OnFailure = (name, e) => reported.Add(name);
            try
            {
                int failuresBefore = OrbitOverlayHost.Failures;
                int drawn = OrbitOverlayHost.Draw(null, true, 0);
                CollectionAssert.AreEqual(new[] { "a", "b", "d" }, calls);
                Assert.AreEqual(2, drawn, "a, d; b threw, c did not want");
                Assert.AreEqual(2, OrbitOverlayHost.LastDrawn);
                Assert.AreEqual(failuresBefore + 1, OrbitOverlayHost.Failures);
                CollectionAssert.AreEqual(new[] { "b" }, reported);
            }
            finally
            {
                OrbitOverlayHost.OnFailure = before;
                foreach (Layer l in new[] { a, b, c, d }) OrbitOverlayLayers.Unregister(l);
            }
        }

        // The smoke level before any game start: the assemblies load with all their types, every module of the atmospheres can be made,
        // and the registry's start line (Init, Tick, Shutdown) runs a module set without an exception.
        [TestMethod]
        [TestCategory("Smoke")]
        public void SmokeTheAssembliesLoadAndTheModulesCanBeMade()
        {
            // The main mod is not walked type by type: it needs Harmony, which the test host does not carry; its Api type is enough.
            Assert.IsNotNull(typeof(ReDefinition.Api.Hooks).GetMethod("RegisterAfterUpscaling"));
            foreach (Type anchor in new[] { typeof(IAtmospheresModule), typeof(WeatherHub) })
            {
                Assembly assembly = anchor.Assembly;
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    Assert.Fail(assembly.GetName().Name + ": " + string.Join(" | ", e.LoaderExceptions.Select(x => x.Message).Distinct().Take(3)));
                    return;
                }
                Assert.IsTrue(types.Length > 0, assembly.GetName().Name);
            }
            List<Type> modules = ModuleRegistry.Discover(typeof(IAtmospheresModule).Assembly);
            Assert.IsTrue(modules.Count >= 1);
            foreach (Type type in modules)
            {
                var module = Activator.CreateInstance(type, true);
                Assert.IsInstanceOfType(module, typeof(IAtmospheresModule), type.Name);
            }
        }

        private sealed class Counter : IAtmospheresModule
        {
            internal int Inits, Ticks, Shutdowns;
            public void Init() { Inits++; }
            public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody) { }
            public void Tick(float dt) { Ticks++; }
            public void Shutdown() { Shutdowns++; }
        }

        [TestMethod]
        [TestCategory("Smoke")]
        public void SmokeTheStartLineRunsAModuleSetWithoutAnException()
        {
            var warnings = new List<string>();
            var registry = new ModuleRegistry(warnings.Add);
            var module = new Counter();
            registry.Add(module, 1);
            registry.Tick(0f, 0.016f);
            registry.Tick(0.016f, 0.016f);
            registry.Shutdown();
            Assert.AreEqual(1, module.Inits);
            Assert.AreEqual(2, module.Ticks);
            Assert.AreEqual(1, module.Shutdowns);
            Assert.AreEqual(0, warnings.Count);
        }
    }
}
