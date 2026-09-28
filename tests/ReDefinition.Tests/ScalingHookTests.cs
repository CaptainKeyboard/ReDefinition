using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KSP.UI.Screens;
using ReDefinition.Scaling;

namespace ReDefinition.Tests
{
    // The UI scaling's Harmony targets against KSP's own assembly: every method
    // found, and every postfix parameter Harmony binds by name present on it.
    [TestClass]
    public class ScalingHookTests
    {
        [TestMethod]
        public void EveryScalingHookFindsItsMethod()
        {
            foreach (KeyValuePair<MethodBase, string> target in ScalingHooks.Targets())
            {
                Assert.IsNotNull(target.Key, "a method to hook for " + target.Value);
                MethodInfo postfix = typeof(ScalingHooks).GetMethod(target.Value, BindingFlags.NonPublic | BindingFlags.Static);
                Assert.IsNotNull(postfix, target.Value);
                HashSet<string> names = new HashSet<string>(target.Key.GetParameters().Select(parameter => parameter.Name));
                foreach (ParameterInfo parameter in postfix.GetParameters())
                {
                    string name = parameter.Name;
                    if (name.StartsWith("___"))
                    {
                        FieldInfo field = target.Key.DeclaringType.GetField(name.Substring(3),
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        Assert.IsNotNull(field, target.Value + ": field " + name.Substring(3));
                        continue;
                    }
                    if (name.StartsWith("__")) continue;
                    Assert.IsTrue(names.Contains(name), target.Value + ": parameter " + name + " of " + target.Key.DeclaringType.Name
                                                        + "." + target.Key.Name);
                }
            }
        }

        // The R&D nodes' and the orbit lines' hooks: the method found, and every
        // field their prefixes and postfixes bind by name.
        [TestMethod]
        public void TheRnDAndOrbitLineHooksFindTheirMembers()
        {
            AssertFields(RnDNodeHooks.Target(), typeof(RnDNodeHooks), "SetupPrefix", "SetupPostfix");
            AssertFields(ReDefinition.Flight.OrbitLineFade.Target(), typeof(ReDefinition.Flight.OrbitLineFade), "OpacityPrefix",
                "OpacityPostfix");
        }

        private static void AssertFields(MethodBase target, System.Type owner, params string[] hooks)
        {
            Assert.IsNotNull(target, owner.Name);
            foreach (string hook in hooks)
            {
                MethodInfo method = owner.GetMethod(hook, BindingFlags.NonPublic | BindingFlags.Static);
                Assert.IsNotNull(method, hook);
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    if (!parameter.Name.StartsWith("___")) continue;
                    Assert.IsNotNull(target.DeclaringType.GetField(parameter.Name.Substring(3),
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public), hook + ": " + parameter.Name);
                }
            }
        }

        // What SpaceCentreScaling and OtherScalers reach by name in KSP.
        [TestMethod]
        public void KspMembersReachedByNameExist()
        {
            MethodInfo zoomTo = typeof(UIGridArea).GetMethod("ZoomTo", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(zoomTo, "UIGridArea.ZoomTo");
            CollectionAssert.AreEqual(new[] { typeof(float), typeof(bool) },
                zoomTo.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
            Assert.IsNotNull(typeof(ManeuverGizmoBase).GetField("screenSize",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public), "ManeuverGizmoBase.screenSize");
        }
    }
}
