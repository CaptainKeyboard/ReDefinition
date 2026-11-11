using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReDefinition.Tests
{
    // KSP's AssemblyLoader loads assemblies in the order of their KSPAssemblyDependency declarations, not of their folders. Common
    // declares KSPAssembly("ReDefinition.Common", 0, 1); the four mods that reference it declare a dependency on that name.
    [TestClass]
    public class KspAssemblyDeclarationTests
    {
        private static Assembly[] Dependents => new[]
        {
            typeof(HostLink).Assembly,
            typeof(global::ReDefinition.Planner.PlannerStartup).Assembly,
            typeof(global::ReDefinition.Dashboard.DashboardStartup).Assembly,
            typeof(global::ReDefinition.Atmospheres.AtmospheresStartup).Assembly,
        };

        [TestMethod]
        public void CommonDeclaresItsKspAssemblyName()
        {
            Assert.AreEqual("ReDefinition.Common", typeof(Core.CommonVersionCheck).Assembly.GetName().Name);
            Assert.IsTrue(Declarations(typeof(Core.CommonVersionCheck).Assembly, "KSPAssembly").Any(a => Matches(a, "ReDefinition.Common")));
        }

        [TestMethod]
        public void TheFourModsDependOnCommon()
        {
            Assert.AreEqual(4, Dependents.Select(a => a.GetName().Name).Distinct().Count());
            foreach (Assembly assembly in Dependents)
                Assert.IsTrue(Declarations(assembly, "KSPAssemblyDependency").Any(a => Matches(a, "ReDefinition.Common")),
                    assembly.GetName().Name + " declares no KSPAssemblyDependency on ReDefinition.Common");
        }

        private static CustomAttributeData[] Declarations(Assembly assembly, string attribute)
        {
            return assembly.GetCustomAttributesData().Where(a => a.AttributeType.Name == attribute).ToArray();
        }

        // Name, major 0, minor 1: what KSP compares against the version the dependency's KSPAssembly declares.
        private static bool Matches(CustomAttributeData data, string name)
        {
            var args = data.ConstructorArguments;
            return args.Count == 3 && (string)args[0].Value == name && (int)args[1].Value == 0 && (int)args[2].Value == 1;
        }
    }
}
