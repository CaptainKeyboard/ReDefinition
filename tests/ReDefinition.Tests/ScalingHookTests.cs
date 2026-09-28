using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
    }
}
