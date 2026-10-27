using System;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Core;

namespace ReDefinition.Tests
{
    // TypeLookup remembers the names no assembly has, and an assembly that arrives later
    // makes it look again.
    [TestClass]
    public class TypeLookupTests
    {
        [TestMethod]
        public void AnAssemblyLoadedLaterIsStillFound()
        {
            string name = "ReDefinition.Tests.LateMod.Marker" + Guid.NewGuid().ToString("N");

            Assert.IsNull(TypeLookup.Find(name));
            // The miss is remembered, so a second ask does not scan again.
            Assert.IsNull(TypeLookup.Find(name));

            AssemblyBuilder assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
                new AssemblyName("LateMod" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
            ModuleBuilder module = assembly.DefineDynamicModule("LateMod");
            Type made = module.DefineType(name, TypeAttributes.Public).CreateType();

            Assert.AreSame(made, TypeLookup.Find(name));
        }

        [TestMethod]
        public void AHitStaysAfterReload()
        {
            Type found = TypeLookup.Find(typeof(TypeLookupTests).FullName);
            Assert.AreSame(typeof(TypeLookupTests), found);

            TypeLookup.Reload();

            Assert.AreSame(typeof(TypeLookupTests), TypeLookup.Find(typeof(TypeLookupTests).FullName));
        }
    }
}
