using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KSP.UI.Screens;
using ReDefinition.Scaling;

namespace ReDefinition.Tests
{
    // The UI scaling's Harmony targets against KSP's own assembly: every method
    // found, and every postfix parameter Harmony binds by name present on it; plus the window scaling's
    // rules (ModWindowScale).
    [TestClass]
    public class ScalingHookTests
    {
        // Each of ScalingHooks.Targets() is found, its postfix exists, and every postfix parameter binds: a
        // `___` name to a field of the target's type, any other (but `__` specials) to a target parameter.
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

        // The R&D nodes' hooks: the method found, and every
        // field their prefixes and postfixes bind by name.
        [TestMethod]
        public void TheRnDNodeHooksFindTheirMembers()
        {
            AssertFields(RnDNodeHooks.Target(), typeof(RnDNodeHooks), "SetupPrefix", "SetupPostfix");
        }

        // Asserts the target was found and that each named hook of `owner` exists with every `___` parameter
        // naming a field of the target's declaring type.
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

        // Every window method ModWindowScale hooks takes a style fifth or none at
        // all: the prefix without one would leave a see-through window shielded.
        [TestMethod]
        public void EveryHookedWindowMethodTakesItsStyleFifthOrNone()
        {
            int hooked = 0;
            foreach (System.Type type in new[] { typeof(UnityEngine.GUI), typeof(UnityEngine.GUILayout) })
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if ((method.Name != "Window" && method.Name != "ModalWindow") || !ModWindowScale.Hookable(method)) continue;
                    hooked++;
                    ParameterInfo[] parameters = method.GetParameters();
                    bool styled = false;
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        if (parameters[i].ParameterType != typeof(UnityEngine.GUIStyle)) continue;
                        styled = true;
                        Assert.AreEqual(4, i, type.Name + "." + method.Name + ": the style at " + i);
                    }
                    Assert.AreEqual(styled, ModWindowScale.StyleFifth(method), type.Name + "." + method.Name);
                }
            Assert.IsTrue(hooked > 0, "no window method found");
        }

        // A rect passed in where the last call returned it was dragged, if at all;
        // one passed in elsewhere was placed by the mod's code.
        [TestMethod]
        public void AWindowPutElsewhereThanReturnedWasPlacedByCode()
        {
            UnityEngine.Vector2 returned = new UnityEngine.Vector2(100f, 50f);
            Assert.IsFalse(ModWindowScale.PlacedByCode(returned, returned));
            Assert.IsFalse(ModWindowScale.PlacedByCode(returned, new UnityEngine.Vector2(100.4f, 49.7f)), "rounded");
            Assert.IsTrue(ModWindowScale.PlacedByCode(returned, new UnityEngine.Vector2(300f, 50f)), "moved across");
            Assert.IsTrue(ModWindowScale.PlacedByCode(returned, new UnityEngine.Vector2(100f, 20f)), "moved up");
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
