using System.Reflection;
using System.Reflection.Emit;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Core;

namespace ReDefinition.Tests.ForeignMod
{
    // Stands for another mod's code: what an assumption names and reads.
    public class Renderer
    {
        // A literal set in the static initializer, and fields an assumption names with their types.
        private static readonly string FadeName = "cloudFade";
        private int renderWidth = 640;
        private float scale = 1f;

        // A method an assumption names, whose body is fingerprinted.
        private void OnPreCull()
        {
            renderWidth = renderWidth / 4;
        }

        // A second method of another body, to tell fingerprints apart.
        private void OnPostRender()
        {
            renderWidth = renderWidth * 2 + 1;
        }

        // A call whose signature an assumption names.
        public void Resize(bool screenshot)
        {
            scale = screenshot ? 2f : 1f;
        }

        // A method body that uses the second literal, which the assembly holds but the static initializer does not.
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
        // The stand-in mod's type, and the members of every visibility its methods and fields are looked up with.
        private const string Type = "ReDefinition.Tests.ForeignMod.Renderer";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        // The fingerprint of Renderer's named methods, together in their order.
        private static string Fingerprint(params string[] methods)
        {
            var found = new System.Collections.Generic.List<MethodBase>();
            foreach (string method in methods) found.Add(typeof(ForeignMod.Renderer).GetMethod(method, Any));
            return ForeignCode.Fingerprint(found);
        }

        // An assumption about a type that is not loaded is absent and does not hold.
        [TestMethod]
        public void AModThatIsNotLoadedIsAbsentAndHoldsNothing()
        {
            ForeignAssumption assumption = new ForeignAssumption("t", "None", "test").Type("No.Such.Mod.Type");
            Assert.AreEqual(ForeignState.Absent, assumption.State);
            Assert.IsFalse(assumption.Holds);
        }

        // Everything an assumption names present, and the methods' fingerprint the one tested: verified, holding,
        // with no problem.
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

        // Code whose fingerprint differs from the tested one is changed: under Keep the assumption still holds,
        // the new fingerprint named in the problem; under Fallback it does not.
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

        // A mod of a version other than those it was tested with counts as changed, the version named.
        [TestMethod]
        public void AnotherVersionIsChanged()
        {
            ForeignAssumption assumption = new ForeignAssumption("t", "Mod", "test").Type(Type).TestedVersions("9.9.9.9");
            Assert.AreEqual(ForeignState.Changed, assumption.State);
            StringAssert.Contains(assumption.Problem, "9.9.9.9");
        }

        // A missing method, a call with another signature, a missing field, a field of another type, a missing
        // literal or type each break the assumption, which then holds under no policy, Keep included.
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

        // A string literal counts as loaded by the type where it stands in the static initializer, and by the
        // assembly where a method body holds it; text put together at run time is not found.
        [TestMethod]
        public void ALiteralInTheStaticInitializerCounts()
        {
            Assert.IsTrue(ForeignCode.TypeLoads(typeof(ForeignMod.Renderer), "cloudFade"));
            Assert.IsTrue(ForeignCode.AssemblyLoads(typeof(ForeignMod.Renderer).Assembly, "godrayRenderingResolution"));
            // Put together at run time: as a literal it would be in this assembly.
            string absent = string.Join("-", new[] { "no", "such", "literal" });
            Assert.IsFalse(ForeignCode.AssemblyLoads(typeof(ForeignMod.Renderer).Assembly, absent));
        }

        // A fingerprint is 12 characters, the same for the same method each time, different for another method,
        // and for the same methods in another order.
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

        // A body changed under the same type, name and signature: the case a new
        // build of a mod brings. Two methods apart, as above, differ by name alone;
        // here only the instructions do -- built as two builds of one mod would be.
        [TestMethod]
        public void AFingerprintFollowsTheBody()
        {
            string two = Fingerprint(Build(2, OpCodes.Add));
            Assert.IsNotNull(two);
            Assert.AreEqual(two, Fingerprint(Build(2, OpCodes.Add)), "the same body in another build");
            Assert.AreNotEqual(two, Fingerprint(Build(3, OpCodes.Add)), "another operand");
            Assert.AreNotEqual(two, Fingerprint(Build(2, OpCodes.Sub)), "another instruction");
        }

        // The fingerprint of a single method.
        private static string Fingerprint(MethodBase method)
        {
            return ForeignCode.Fingerprint(new System.Collections.Generic.List<MethodBase> { method });
        }

        // ForeignMod.Renderer.Scale(int width) { return width <op> factor; }, in an
        // assembly of its own.
        private static MethodBase Build(int factor, OpCode op)
        {
            AssemblyBuilder assembly = System.AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("ForeignBuild"),
                AssemblyBuilderAccess.Run);
            ModuleBuilder module = assembly.DefineDynamicModule("ForeignBuild");
            TypeBuilder type = module.DefineType("ForeignMod.Renderer", TypeAttributes.Public);
            MethodBuilder scale = type.DefineMethod("Scale", MethodAttributes.Public | MethodAttributes.Static, typeof(int),
                new[] { typeof(int) });
            ILGenerator il = scale.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4, factor);
            il.Emit(op);
            il.Emit(OpCodes.Ret);
            return type.CreateType().GetMethod("Scale");
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
