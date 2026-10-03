using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Core;

namespace ReDefinition.Tests.ForeignMod
{
    // Stands for another mod's code: what an assumption names and reads.
    public class Renderer
    {
        private static readonly string FadeName = "cloudFade";
        private int renderWidth = 640;
        private float scale = 1f;

        private void OnPreCull()
        {
            renderWidth = renderWidth / 4;
        }

        private void OnPostRender()
        {
            renderWidth = renderWidth * 2 + 1;
        }

        public void Resize(bool screenshot)
        {
            scale = screenshot ? 2f : 1f;
        }

        public string Name()
        {
            return FadeName + "godrayRenderingResolution";
        }
    }
}

namespace ReDefinition.Tests
{
    // What ForeignAssumption makes of another mod's code (ForeignAssumptions'
    // catalogue): the states, and what holds under each policy.
    [TestClass]
    public class ForeignAssumptionTests
    {
        private const string Type = "ReDefinition.Tests.ForeignMod.Renderer";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static string Fingerprint(params string[] methods)
        {
            var found = new System.Collections.Generic.List<MethodBase>();
            foreach (string method in methods) found.Add(typeof(ForeignMod.Renderer).GetMethod(method, Any));
            return ForeignCode.Fingerprint(found);
        }

        [TestMethod]
        public void AModThatIsNotLoadedIsAbsentAndHoldsNothing()
        {
            ForeignAssumption assumption = new ForeignAssumption("t", "None", "test").Type("No.Such.Mod.Type");
            Assert.AreEqual(ForeignState.Absent, assumption.State);
            Assert.IsFalse(assumption.Holds);
        }

        [TestMethod]
        public void TheCodeItWasCheckedAgainstIsVerified()
        {
            ForeignAssumption assumption = new ForeignAssumption("t", "Mod", "test").Type(Type)
                .Method(Type, "OnPreCull").Field(Type, "renderWidth", "System.Int32")
                .Calls(Type, "Resize", "System.Boolean").Literal(Type, "cloudFade", "godrayRenderingResolution")
                .Tested(Fingerprint("OnPreCull"));
            Assert.AreEqual(ForeignState.Verified, assumption.State, assumption.Problem);
            Assert.IsTrue(assumption.Holds);
            Assert.IsNull(assumption.Problem);
        }

        [TestMethod]
        public void OtherCodeIsChangedAndThePolicyDecides()
        {
            ForeignAssumption kept = new ForeignAssumption("t", "Mod", "test").Type(Type)
                .Method(Type, "OnPreCull").Tested("000000000000");
            Assert.AreEqual(ForeignState.Changed, kept.State);
            Assert.IsTrue(kept.Holds);
            StringAssert.Contains(kept.Problem, kept.Fingerprint);

            ForeignAssumption fallback = new ForeignAssumption("t", "Mod", "test").Type(Type)
                .Method(Type, "OnPreCull").Tested("000000000000").OnChange(WhenChanged.Fallback, "the other way");
            Assert.AreEqual(ForeignState.Changed, fallback.State);
            Assert.IsFalse(fallback.Holds);
        }

        [TestMethod]
        public void AnotherVersionIsChanged()
        {
            ForeignAssumption assumption = new ForeignAssumption("t", "Mod", "test").Type(Type).TestedVersions("9.9.9.9");
            Assert.AreEqual(ForeignState.Changed, assumption.State);
            StringAssert.Contains(assumption.Problem, "9.9.9.9");
        }

        [TestMethod]
        public void SomethingNamedThatIsMissingIsBroken()
        {
            string[][] cases =
            {
                new[] { "method" }, new[] { "signature" }, new[] { "field" }, new[] { "field type" }, new[] { "literal" },
                new[] { "type" },
            };
            foreach (string[] which in cases)
            {
                ForeignAssumption assumption = new ForeignAssumption("t", "Mod", "test").Type(Type)
                    .OnChange(WhenChanged.Keep, "kept");
                switch (which[0])
                {
                    case "method": assumption.Method(Type, "OnUpdate"); break;
                    case "signature": assumption.Calls(Type, "Resize", "System.Int32"); break;
                    case "field": assumption.Field(Type, "renderHeight"); break;
                    case "field type": assumption.Field(Type, "renderWidth", "System.Single"); break;
                    case "literal": assumption.Literal(Type, "cloudFadeRenamed"); break;
                    case "type": assumption.Type("ReDefinition.Tests.ForeignMod.Missing"); break;
                }
                Assert.AreEqual(ForeignState.Broken, assumption.State, which[0]);
                // Broken holds under no policy.
                Assert.IsFalse(assumption.Holds, which[0]);
            }
        }

        [TestMethod]
        public void ALiteralInTheStaticInitializerCounts()
        {
            Assert.IsTrue(ForeignCode.TypeLoads(typeof(ForeignMod.Renderer), "cloudFade"));
            Assert.IsTrue(ForeignCode.AssemblyLoads(typeof(ForeignMod.Renderer).Assembly, "godrayRenderingResolution"));
            // Put together at run time: as a literal it would be in this assembly.
            string absent = string.Join("-", new[] { "no", "such", "literal" });
            Assert.IsFalse(ForeignCode.AssemblyLoads(typeof(ForeignMod.Renderer).Assembly, absent));
        }

        [TestMethod]
        public void AFingerprintIsStableAndTellsMethodsApart()
        {
            string preCull = Fingerprint("OnPreCull");
            Assert.AreEqual(12, preCull.Length);
            Assert.AreEqual(preCull, Fingerprint("OnPreCull"));
            Assert.AreNotEqual(preCull, Fingerprint("OnPostRender"));
            // Together, in their order.
            Assert.AreNotEqual(Fingerprint("OnPreCull", "OnPostRender"), Fingerprint("OnPostRender", "OnPreCull"));
        }

        // Every assumption in the catalogue names a mod and says what happens where it
        // does not hold, and no two share an id.
        [TestMethod]
        public void TheCatalogueIsComplete()
        {
            var ids = new System.Collections.Generic.HashSet<string>();
            foreach (ForeignAssumption assumption in ForeignAssumptions.All)
            {
                Assert.IsNotNull(assumption, "a null entry in ForeignAssumptions.All");
                Assert.IsTrue(ids.Add(assumption.Id), "id twice: " + assumption.Id);
                Assert.IsFalse(string.IsNullOrEmpty(assumption.Mod), assumption.Id);
                Assert.IsFalse(string.IsNullOrEmpty(assumption.Fallback), assumption.Id + " has no fallback");
            }
        }
    }
}
