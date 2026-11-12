using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres.Audio;

namespace ReDefinition.Tests.Atmospheres.D33
{
    // The routing of a footstep source through Rocket Sound Enhancement's table (series row D33.rse_state). The fake types have the shape of
    // RSE 0.9.13: Settings.CustomAudioClips is a public static Dictionary<string, MixerGroup>, AudioUtility.GetMixerGroup(MixerGroup) is public static.
    [TestClass]
    public class RseClipRouterTests
    {
        public enum FakeGroup { Ignore, Exterior, Interior, Focus }

        public static class FakeSettings
        {
            public static Dictionary<string, FakeGroup> CustomAudioClips = new Dictionary<string, FakeGroup>();
        }

        public static class FakeAudioUtility
        {
            public static bool EffectsOn = true;

            public static object GetMixerGroup(FakeGroup group)
            {
                return EffectsOn ? "group:" + group : null;
            }
        }

        [TestInitialize]
        public void Reset()
        {
            FakeSettings.CustomAudioClips.Clear();
            FakeAudioUtility.EffectsOn = true;
        }

        [TestMethod]
        public void AListedClipGetsTheGroupOfTheTable()
        {
            FakeSettings.CustomAudioClips["ReDefinition/Atmospheres/Sounds/steps_snow_01"] = FakeGroup.Exterior;
            RseClipRouter router = RseClipRouter.FromTypes(typeof(FakeSettings), typeof(FakeAudioUtility));
            Assert.IsNotNull(router);
            Assert.AreEqual("group:Exterior", router.GroupFor("ReDefinition/Atmospheres/Sounds/steps_snow_01"));
        }

        [TestMethod]
        public void AClipThatIsNotListedOrIsIgnoredGetsNoGroup()
        {
            FakeSettings.CustomAudioClips["listed_but_ignored"] = FakeGroup.Ignore;
            RseClipRouter router = RseClipRouter.FromTypes(typeof(FakeSettings), typeof(FakeAudioUtility));
            Assert.IsNull(router.GroupFor("not_listed"));
            Assert.IsNull(router.GroupFor("listed_but_ignored"));
            Assert.IsNull(router.GroupFor(null));
            Assert.IsNull(router.GroupFor(""));
        }

        [TestMethod]
        public void TheTableIsReadAtTheTimeOfTheStepNotWhenTheRouterIsMade()
        {
            RseClipRouter router = RseClipRouter.FromTypes(typeof(FakeSettings), typeof(FakeAudioUtility));
            Assert.IsNull(router.GroupFor("late"));
            FakeSettings.CustomAudioClips["late"] = FakeGroup.Focus;
            Assert.AreEqual("group:Focus", router.GroupFor("late"));
        }

        [TestMethod]
        public void AClipOnlyOurPatchDeclaresGetsItsGroupWhileRsesTableDoesNotListIt()
        {
            RseClipRouter router = RseClipRouter.FromTypes(typeof(FakeSettings), typeof(FakeAudioUtility));
            Assert.IsNull(router.GroupFor("steps_01"));
            router.Declare("steps_01", "Exterior");
            router.Declare("steps_bad", "NoSuchGroup");
            router.Declare("steps_ignored", "Ignore");
            Assert.AreEqual("group:Exterior", router.GroupFor("steps_01"));
            Assert.IsNull(router.GroupFor("steps_bad"));
            Assert.IsNull(router.GroupFor("steps_ignored"));
            Assert.AreEqual(3, router.DeclaredCount);
            Assert.AreEqual(0, router.TableCount);
        }

        [TestMethod]
        public void ATableEntryWinsOverTheDeclaration()
        {
            FakeSettings.CustomAudioClips["steps_01"] = FakeGroup.Interior;
            RseClipRouter router = RseClipRouter.FromTypes(typeof(FakeSettings), typeof(FakeAudioUtility));
            router.Declare("steps_01", "Exterior");
            Assert.AreEqual("group:Interior", router.GroupFor("steps_01"));
        }

        [TestMethod]
        public void WithRseEffectsOffNoGroupIsGiven()
        {
            FakeSettings.CustomAudioClips["x"] = FakeGroup.Exterior;
            FakeAudioUtility.EffectsOn = false;
            RseClipRouter router = RseClipRouter.FromTypes(typeof(FakeSettings), typeof(FakeAudioUtility));
            Assert.IsNull(router.GroupFor("x"));
        }

        [TestMethod]
        public void AFailingLookupGivesNoGroupAndDoesNotThrow()
        {
            FakeSettings.CustomAudioClips["x"] = FakeGroup.Exterior;
            var router = new RseClipRouter(FakeSettings.CustomAudioClips, v => { throw new InvalidOperationException("mixer bundle missing"); });
            Assert.IsNull(router.GroupFor("x"));
        }

        [TestMethod]
        public void AnotherShapeOfRseGivesNoRouter()
        {
            Assert.IsNull(RseClipRouter.FromTypes(typeof(string), typeof(FakeAudioUtility)));
            Assert.IsNull(RseClipRouter.FromTypes(typeof(FakeSettings), typeof(string)));
            Assert.IsNull(RseClipRouter.FromTypes(null, typeof(FakeAudioUtility)));
            Assert.IsNull(RseClipRouter.FromAssembly(null));
        }
    }
}
