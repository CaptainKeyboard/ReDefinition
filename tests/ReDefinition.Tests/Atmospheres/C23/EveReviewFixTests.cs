using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.C23
{
    // The review fixes of package C23: which parts run per cloudSource, the mirror's channels, the first paint of the type target,
    // the thunder kind of EVE's own weather and the second assumption entry.
    [TestClass]
    public class EveReviewFixTests
    {
        [TestMethod]
        public void TheModeFollowsCloudSourceAndEveBeingThere()
        {
            bool eve, own;
            // cloudSource x EVE available -> eveMode, ownMode
            CloudModeModel.Decide(CloudSourceKind.Ours, true, out eve, out own);
            Assert.IsFalse(eve);
            Assert.IsTrue(own);
            CloudModeModel.Decide(CloudSourceKind.Ours, false, out eve, out own);
            Assert.IsFalse(eve);
            Assert.IsTrue(own);
            CloudModeModel.Decide(CloudSourceKind.Eve, true, out eve, out own);
            Assert.IsTrue(eve);
            Assert.IsFalse(own);
            CloudModeModel.Decide(CloudSourceKind.Eve, false, out eve, out own);
            Assert.IsFalse(eve);
            Assert.IsTrue(own, "Eve without EVE installed still shows the orbit and map picture");
            CloudModeModel.Decide(CloudSourceKind.Both, true, out eve, out own);
            Assert.IsTrue(eve);
            Assert.IsFalse(own);
            CloudModeModel.Decide(CloudSourceKind.Both, false, out eve, out own);
            Assert.IsFalse(eve);
            Assert.IsTrue(own);
            CloudModeModel.Decide(CloudSourceKind.None, true, out eve, out own);
            Assert.IsFalse(eve);
            Assert.IsFalse(own);
            CloudModeModel.Decide(CloudSourceKind.None, false, out eve, out own);
            Assert.IsFalse(eve);
            Assert.IsFalse(own);
        }

        [TestMethod]
        public void TheShellStandsInWhereEveHasNoPicture()
        {
            Assert.IsTrue(CloudModeModel.ShellWanted(CloudSourceKind.Ours, true));
            Assert.IsTrue(CloudModeModel.ShellWanted(CloudSourceKind.Ours, false));
            Assert.IsFalse(CloudModeModel.ShellWanted(CloudSourceKind.None, false));
            Assert.IsTrue(CloudModeModel.ShellWanted(CloudSourceKind.Eve, false), "a body with no EVE 2D layer, or no EVE layer at all");
            Assert.IsFalse(CloudModeModel.ShellWanted(CloudSourceKind.Eve, true));
            Assert.IsTrue(CloudModeModel.ShellWanted(CloudSourceKind.Both, false));
            Assert.IsFalse(CloudModeModel.ShellWanted(CloudSourceKind.Both, true));
        }

        [TestMethod]
        public void TheMirrorCarriesTheValueInEveryChannel()
        {
            var value = new byte[] { 0, 1, 127, 255 };
            var rgba = new byte[16];
            CpuMirror.ExpandToRgba(value, rgba);
            for (int i = 0; i < value.Length; i++)
            {
                for (int c = 0; c < 4; c++) Assert.AreEqual(value[i], rgba[i * 4 + c], "texel " + i + " channel " + c);
            }
        }

        [TestMethod]
        public void EachTargetJumpsOnItsOwnFirstRun()
        {
            // First paint: the coverage pass runs, the type pass is skipped (no table yet): only the coverage is primed.
            bool coveragePrimed = false, typePrimed = false;
            Assert.IsTrue(MapSlew.Jumps(false, coveragePrimed));
            coveragePrimed = true;
            // Second paint, the table exists: the coverage slews, the type target jumps to the field's map.
            Assert.IsFalse(MapSlew.Jumps(false, coveragePrimed));
            Assert.IsTrue(MapSlew.Jumps(false, typePrimed), "the type target must not slew up from 0 after the bind");
            typePrimed = true;
            Assert.IsFalse(MapSlew.Jumps(false, typePrimed));
            Assert.IsTrue(MapSlew.Jumps(true, typePrimed), "a cut snaps both");
        }

        [TestMethod]
        public void TheThunderKindComesFromTheTypeName()
        {
            Assert.IsTrue(TypeRoles.IsStormName("Thunder"));
            Assert.IsTrue(TypeRoles.IsStormName("StormCumulonimbus"));
            Assert.IsFalse(TypeRoles.IsStormName("Rain"));
            Assert.IsFalse(TypeRoles.IsStormName(null));
            WeatherSample clear = WeatherSample.Clear(1);
            clear.TemperatureK = 285f;
            GroundEffectMath.Factors plain = EveGroundEffects.FactorsFor(clear, 0.8f, false, true, 1f, 0.9f);
            GroundEffectMath.Factors thunder = EveGroundEffects.FactorsFor(clear, 0.8f, true, true, 1f, 0.9f);
            Assert.AreEqual(0f, plain.Lightning, "level 0.46 is below the lightning threshold");
            Assert.IsTrue(thunder.Lightning > 0f, "a thunder-kind layer reaches 0.67 and may strike");
        }

        [TestMethod]
        public void TheGroundEntryIsInTheCatalogueOnceWithAFingerprint()
        {
            int count = 0;
            foreach (ForeignAssumption a in AtmospheresAssumptions.All)
            {
                if (a.Id == AssumptionsC23.GroundId) count++;
            }
            Assert.AreEqual(1, count);
            Assert.AreEqual(WhenChanged.Fallback, AssumptionsC23.EveGroundEffects.Policy);
        }
    }
}
