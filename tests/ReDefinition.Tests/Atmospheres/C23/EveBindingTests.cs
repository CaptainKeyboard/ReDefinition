using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.C23
{
    // The binding state machine, the cloud type encoding, the ground effect factors and the orbit picture of package C23.
    [TestClass]
    public class EveBindingTests
    {
        // A material of EVE's as far as the binding knows it: an identity (the object EVE made), the paint set we put on it, and the
        // keywords the sentinel looks at. EVE's shader declares no properties (RECON P9), so no texture is ever read back.
        private sealed class FakeMaterial
        {
            public object Set;
            public bool AlphaMaskKeywords;

            public void Bind(object ours)
            {
                Set = ours;
                AlphaMaskKeywords = true;
            }

            // What EVE's SetShaderParams does to a material that carries a plain wrapper: the keywords go.
            public void EveRewritesKeywords()
            {
                AlphaMaskKeywords = false;
            }

            public bool KeywordsAsLeft()
            {
                return AlphaMaskKeywords;
            }
        }

        [TestMethod]
        public void AKeywordLossOnTheSameMaterialIsFoundByTheSentinelAndBoundAgain()
        {
            var tracker = new BindingTracker(1.0);
            var material = new FakeMaterial();
            object ours = new object();
            Assert.AreEqual(BindAction.Bind, tracker.Observe(0.0, false, false, material.KeywordsAsLeft), "unbound asks for a binding");
            material.Bind(ours);
            tracker.Bound(0.0);
            Assert.AreEqual(BindState.Bound, tracker.State);
            Assert.AreEqual(BindAction.None, tracker.Observe(0.5, false, false, material.KeywordsAsLeft));
            material.EveRewritesKeywords();
            // The look is every second: at 0.9 nothing is seen, at 1.0 the loss is.
            Assert.AreEqual(BindAction.None, tracker.Observe(0.9, false, false, material.KeywordsAsLeft));
            Assert.AreEqual(BindAction.Bind, tracker.Observe(1.0, false, false, material.KeywordsAsLeft));
            Assert.AreEqual(BindState.Lost, tracker.State);
            Assert.AreEqual(1, tracker.Losses);
            Assert.AreEqual(1, tracker.WatchdogFinds);
            material.Bind(ours);
            tracker.Bound(1.25);
            Assert.AreEqual(BindState.Bound, tracker.State);
            Assert.AreEqual(1, tracker.Rebinds);
            Assert.AreEqual(0.25, tracker.LastLatencyS, 1e-9);
        }

        [TestMethod]
        public void WithNothingSeenTheWatchdogSetsTheTexturesAgainEveryInterval()
        {
            // A lost binding on the same material with the same keywords leaves no trace (EVE's wrapper of an alpha map writes the same
            // keywords): setting the same textures again every interval is what restores it. It is not a loss.
            var tracker = new BindingTracker(1.0);
            var material = new FakeMaterial();
            object ours = new object();
            material.Bind(ours);
            tracker.Bound(0.0);
            Assert.AreEqual(BindAction.None, tracker.Observe(0.9, false, false, material.KeywordsAsLeft));
            Assert.AreEqual(BindAction.Reassert, tracker.Observe(1.0, false, false, material.KeywordsAsLeft));
            tracker.Reasserted(1.0);
            Assert.AreEqual(BindAction.None, tracker.Observe(1.5, false, false, material.KeywordsAsLeft));
            Assert.AreEqual(BindAction.Reassert, tracker.Observe(2.0, false, false, material.KeywordsAsLeft));
            tracker.Reasserted(2.0);
            Assert.AreEqual(2, tracker.Reasserts);
            Assert.AreEqual(0, tracker.Losses);
            Assert.AreEqual(BindState.Bound, tracker.State);
        }

        [TestMethod]
        public void AnApplyEventOrANewMaterialIsSeenAtOnce()
        {
            var tracker = new BindingTracker(1.0);
            var material = new FakeMaterial();
            object ours = new object();
            material.Bind(ours);
            tracker.Bound(0.0);
            material.EveRewritesKeywords();
            Assert.AreEqual(BindAction.Bind, tracker.Observe(0.016, true, false, material.KeywordsAsLeft), "the event does not wait for the watchdog");
            Assert.AreEqual(1, tracker.Losses);
            Assert.AreEqual(0, tracker.WatchdogFinds);
            material.Bind(ours);
            tracker.Bound(0.016);
            Assert.AreEqual(0.0, tracker.LastLatencyS, 1e-9);
            // A new material object (EVE's Apply makes them in the same frame, RECON P9) counts like an event.
            Assert.AreEqual(BindAction.Bind, tracker.Observe(0.1, false, true, null));
            Assert.AreEqual(2, tracker.Losses);
        }

        [TestMethod]
        public void TwentyForcedAppliesAreAllSurvivedAndCounted()
        {
            var tracker = new BindingTracker(1.0);
            var material = new FakeMaterial();
            object ours = new object();
            material.Bind(ours);
            tracker.Bound(0.0);
            double now = 0;
            for (int k = 0; k < 20; k++)
            {
                now += 0.5;
                // The forced Apply fires no onApply (RECON P9): the new material object is the only sign.
                material.EveRewritesKeywords();
                BindAction action = tracker.Observe(now, false, true, material.KeywordsAsLeft);
                Assert.AreEqual(BindAction.Bind, action, "apply " + k);
                material.Bind(ours);
                tracker.Bound(now + 0.02);
            }
            Assert.AreEqual(20, tracker.Losses);
            Assert.AreEqual(20, tracker.Rebinds);
            Assert.AreEqual(0, tracker.WatchdogFinds, "found by identity, not by the sentinel");
            Assert.IsTrue(tracker.MaxLatencyS <= 0.02 + 1e-9);
            Assert.AreEqual(20, tracker.Latencies.Count);
            Assert.AreEqual(BindState.Bound, tracker.State);
        }

        [TestMethod]
        public void ALayerWithTimeSettingsIsNeverFought()
        {
            var tracker = new BindingTracker(1.0);
            tracker.Leave();
            Assert.AreEqual(BindAction.None, tracker.Observe(0.0, true, true, () => false));
            Assert.AreEqual(BindAction.None, tracker.Observe(5.0, false, false, () => false));
            tracker.Bound(6.0);
            Assert.AreEqual(BindState.Left, tracker.State);
            Assert.AreEqual(0, tracker.Losses);
        }

        [TestMethod]
        public void ResetKeepsTheCountersAndAsksForABinding()
        {
            var tracker = new BindingTracker(1.0);
            tracker.Bound(0.0);
            tracker.Observe(0.1, true, false, null);
            tracker.Reset();
            Assert.AreEqual(BindState.Unbound, tracker.State);
            Assert.AreEqual(1, tracker.Losses);
            Assert.AreEqual(BindAction.Bind, tracker.Observe(1.0, false, false, null));
        }

        [TestMethod]
        public void TheIntervalCanBeChangedAtRunTime()
        {
            var tracker = new BindingTracker(1.0);
            tracker.Bound(0.0);
            tracker.IntervalS = 0.25;
            Assert.AreEqual(BindAction.None, tracker.Observe(0.2, false, false, () => true));
            Assert.AreEqual(BindAction.Reassert, tracker.Observe(0.25, false, false, () => true));
        }

        // --- Cloud type encoding ------------------------------------------------------------------------------------

        private static TypeSlots KerbinTemplate()
        {
            var slots = new TypeSlots(5);
            slots.Assign(CloudRole.SnowLight, 1);
            slots.Assign(CloudRole.SnowStratus, 2);
            slots.Assign(CloudRole.Storm, 3);
            return slots;
        }

        [TestMethod]
        public void EveryBandMapsToADefinedType()
        {
            TypeSlots slots = KerbinTemplate();
            var expected = new Dictionary<PrecipBand, CloudRole>
            {
                { PrecipBand.Flurries, CloudRole.SnowLight },
                { PrecipBand.Light, CloudRole.SnowLight },
                { PrecipBand.Moderate, CloudRole.SnowStratus },
                { PrecipBand.Heavy, CloudRole.Storm },
                { PrecipBand.Blizzard, CloudRole.Storm }
            };
            foreach (PrecipBand band in new[] { PrecipBand.Flurries, PrecipBand.Light, PrecipBand.Moderate, PrecipBand.Heavy, PrecipBand.Blizzard })
            {
                Assert.AreEqual(expected[band], CloudTypeEncoding.RoleOfBand(band, false), band.ToString());
                float level = CloudTypeEncoding.RepresentativeLevel(band);
                Assert.AreEqual(band, PrecipScale.BandOf(level, 20f), "the representative level lies in its band");
                float value = CloudTypeEncoding.Value(slots, level, false);
                Assert.IsTrue(value >= 0f && value <= 1f);
                int index = (int)Math.Round(value * (slots.Count - 1));
                Assert.AreEqual(slots.IndexOf(expected[band]), index, band + " value " + value);
            }
        }

        [TestMethod]
        public void TheTypeValueRisesContinuouslyWithTheLevel()
        {
            TypeSlots slots = KerbinTemplate();
            float previous = CloudTypeEncoding.Value(slots, 0f, false);
            float biggestStep = 0f;
            for (int i = 1; i <= 1000; i++)
            {
                float v = CloudTypeEncoding.Value(slots, i / 1000f, false);
                Assert.IsTrue(v >= previous - 1e-6f, "falls at " + i / 1000f);
                biggestStep = Math.Max(biggestStep, v - previous);
                previous = v;
            }
            Assert.IsTrue(biggestStep < 0.02f, "no step: " + biggestStep);
            Assert.AreEqual(slots.ValueOfIndex(1), CloudTypeEncoding.Value(slots, 0f, false), 1e-6);
            Assert.AreEqual(slots.ValueOfIndex(3), CloudTypeEncoding.Value(slots, 1f, false), 1e-6);
        }

        [TestMethod]
        public void AMissingRoleIsPassedOverAndALayerWithNoneGivesZero()
        {
            var noStratus = new TypeSlots(3);
            noStratus.Assign(CloudRole.SnowLight, 0);
            noStratus.Assign(CloudRole.Storm, 2);
            float mid = CloudTypeEncoding.Value(noStratus, 0.5f, false);
            Assert.IsTrue(mid > 0f && mid < 1f);
            Assert.AreEqual(0f, CloudTypeEncoding.Value(new TypeSlots(4), 0.7f, false));
            Assert.AreEqual(0f, CloudTypeEncoding.Value(null, 0.7f, false));
            var dust = new TypeSlots(3);
            dust.Assign(CloudRole.Dust, 2);
            Assert.AreEqual(1f, CloudTypeEncoding.Value(dust, 0.1f, true), 1e-6);
        }

        [TestMethod]
        public void TheLutHas256EntriesAndFollowsTheValue()
        {
            TypeSlots slots = KerbinTemplate();
            byte[] lut = CloudTypeEncoding.BuildLut(slots, false);
            Assert.AreEqual(CloudTypeEncoding.LutSize, lut.Length);
            for (int i = 1; i < lut.Length; i++) Assert.IsTrue(lut[i] >= lut[i - 1]);
            Assert.AreEqual((int)Math.Round(slots.ValueOfIndex(3) * 255f), lut[255]);
        }

        [TestMethod]
        public void SlotsComeFromTheTypeNamesAndTheRoleLists()
        {
            var rules = new CloudRules(snowTypeNames: new[] { "SnowLight" }, stratusTypeNames: new[] { "SnowStratus" });
            TypeSlots slots = TypeRoles.FromNames(new[] { "Rain", "SnowLight", "SnowStratus", "Storm-core" }, rules);
            Assert.AreEqual(4, slots.Count);
            Assert.AreEqual(1, slots.IndexOf(CloudRole.SnowLight));
            Assert.AreEqual(2, slots.IndexOf(CloudRole.SnowStratus));
            Assert.AreEqual(3, slots.IndexOf(CloudRole.Storm));
            Assert.IsFalse(slots.Has(CloudRole.Dust));
        }

        [TestMethod]
        public void TheDeckThickensWithTheLevelThroughTheTypeValue()
        {
            // CONTRACT.md: SnowLight 600-1500 (here 300 thick), SnowStratus 400-1800, storm 300-2300.
            float[] min = { 600f, 400f, 300f };
            float[] max = { 900f, 1800f, 2300f };
            TypeSlots slots = new TypeSlots(3);
            slots.Assign(CloudRole.SnowLight, 0);
            slots.Assign(CloudRole.SnowStratus, 1);
            slots.Assign(CloudRole.Storm, 2);
            float previous = 0f;
            for (int i = 0; i <= 100; i++)
            {
                float thickness = CloudTypeEncoding.DeckThicknessM(min, max, CloudTypeEncoding.Value(slots, i / 100f, false));
                Assert.IsTrue(thickness >= previous - 1e-3f);
                previous = thickness;
            }
            Assert.AreEqual(300f, CloudTypeEncoding.DeckThicknessM(min, max, 0f), 1e-3);
            Assert.AreEqual(2000f, CloudTypeEncoding.DeckThicknessM(min, max, 1f), 1e-3);
        }

        // --- Ground effects -----------------------------------------------------------------------------------------

        [TestMethod]
        public void RainMeansRainEffectsAndSnowMeansSnowEffects()
        {
            GroundEffectMath.Factors rain = GroundEffectMath.From(0f, 0f, 1f, 0.7f);
            Assert.AreEqual(1f, rain.RainField, 1e-6);
            Assert.AreEqual(0f, rain.SnowField, 1e-6);
            Assert.AreEqual(1f, rain.Droplets, 1e-6);
            Assert.AreEqual(1f, rain.Wet, 1e-6);
            Assert.AreEqual(1f, rain.Lightning, 1e-6);
            GroundEffectMath.Factors snow = GroundEffectMath.From(1f, 0f, 0f, 0.9f);
            Assert.AreEqual(0f, snow.RainField, 1e-6);
            Assert.AreEqual(1f, snow.SnowField, 1e-6);
            Assert.AreEqual(0f, snow.Droplets, 1e-6);
            Assert.AreEqual(0f, snow.Lightning, 1e-6, "snow thunder never");
            Assert.AreEqual(1f, snow.SnowSound, 1e-6);
            Assert.AreEqual(0f, snow.RainSound, 1e-6);
        }

        [TestMethod]
        public void SleetCountsHalfEachAndLightningNeedsHalfScale()
        {
            GroundEffectMath.Factors sleet = GroundEffectMath.From(0f, 1f, 0f, 0.6f);
            Assert.AreEqual(0.5f, sleet.RainField, 1e-6);
            Assert.AreEqual(0.5f, sleet.SnowField, 1e-6);
            Assert.AreEqual(0.5f, sleet.Droplets, 1e-6);
            Assert.AreEqual(0.3f, sleet.Wet, 1e-6);
            Assert.AreEqual(0f, sleet.Lightning, 1e-6, "lightning only by the rain share");
            GroundEffectMath.Factors weak = GroundEffectMath.From(0f, 0f, 1f, 0.3f);
            Assert.AreEqual(0f, weak.Lightning, 1e-6, "no lightning below level 0.5");
            Assert.AreEqual(1f, GroundEffectMath.From(0f, 0f, 1f, 0.5f).Lightning, 1e-6);
            GroundEffectMath.Factors none = GroundEffectMath.From(0.3f, 0.3f, 0.4f, 0f);
            Assert.AreEqual(0f, none.RainField);
            Assert.AreEqual(0f, none.SnowField);
        }

        [TestMethod]
        public void AMixIsNormalisedAndTheFactorsStayInRange()
        {
            var rng = new Random(11);
            for (int k = 0; k < 500; k++)
            {
                GroundEffectMath.Factors f = GroundEffectMath.From((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());
                foreach (float v in new[] { f.RainField, f.SnowField, f.Droplets, f.Wet, f.Lightning, f.RainSound, f.SnowSound })
                    Assert.IsTrue(v >= 0f && v <= 1f);
                Assert.IsTrue(f.RainField + f.SnowField <= 1.0001f);
            }
        }

        [TestMethod]
        public void EvesOwnWeatherMapsToALevelAndJoinsTheField()
        {
            Assert.AreEqual(0f, GroundEffectMath.EveLevel(0f, false, true), 1e-6);
            Assert.AreEqual(0.55f, GroundEffectMath.EveLevel(1f, false, true), 1e-6);
            Assert.AreEqual(0.55f * (float)Math.Pow(0.5, 0.8), GroundEffectMath.EveLevel(0.5f, false, true), 1e-6);
            Assert.AreEqual(0.8f, GroundEffectMath.EveLevel(1f, true, true), 1e-6);
            Assert.IsTrue(GroundEffectMath.EveLevel(1f, false, false) <= 0.6f);
            Assert.AreEqual(1f - 0.5f * 0.7f, GroundEffectMath.Union(0.5f, 0.3f), 1e-6);
            Assert.AreEqual(0.4f, GroundEffectMath.Union(0.4f, 0f), 1e-6);
        }

        [TestMethod]
        public void ATypeValueIsWrittenAsOriginalTimesFactor()
        {
            Assert.AreEqual(0.6f, GroundEffectMath.Written(1.2f, 0.5f), 1e-6);
            Assert.AreEqual(1.2f, GroundEffectMath.Written(1.2f, 7f), 1e-6, "never above the original");
            Assert.AreEqual(0f, GroundEffectMath.Written(1.2f, -1f), 1e-6);
        }

        [TestMethod]
        public void FactorsFromASampleUseEvesWeatherOnlyWhereTheFieldHasNone()
        {
            WeatherSample clear = WeatherSample.Clear(1);
            clear.TemperatureK = 285f;
            GroundEffectMath.Factors off = EveGroundEffects.FactorsFor(clear, 0.9f, false, false, 1f, 0.9f);
            Assert.AreEqual(0f, off.RainField);
            GroundEffectMath.Factors both = EveGroundEffects.FactorsFor(clear, 0.9f, false, true, 1f, 0.9f);
            Assert.IsTrue(both.RainField > 0.5f, "warm air: EVE's weather is rain");
            WeatherSample cold = WeatherSample.Clear(1);
            cold.TemperatureK = 262f;
            GroundEffectMath.Factors snow = EveGroundEffects.FactorsFor(cold, 0.9f, false, true, 1f, 0.9f);
            Assert.IsTrue(snow.SnowField > 0.5f && snow.RainField < 0.2f, "cold air: EVE's weather is snow");
            WeatherSample field = WeatherSample.Clear(1);
            field.Level01 = 0.7f;
            field.SnowFraction = 1f;
            GroundEffectMath.Factors own = EveGroundEffects.FactorsFor(field, 0.1f, false, true, 1f, 0.9f);
            Assert.AreEqual(1f, own.SnowField, 1e-6);
        }

        // --- Orbit picture ------------------------------------------------------------------------------------------

        [TestMethod]
        public void FlurriesAreFaintAndTheSwirlStartsAtModerate()
        {
            Assert.IsTrue(OrbitPicture.OpacityAt(0.05f) < 0.5f);
            Assert.AreEqual(1f, OrbitPicture.OpacityAt(PrecipScale.ModerateFrom), 1e-6);
            Assert.AreEqual(1f, OrbitPicture.OpacityAt(0.9f), 1e-6);
            float previous = 0f;
            for (int i = 0; i <= 100; i++)
            {
                float o = OrbitPicture.OpacityAt(i / 100f);
                Assert.IsTrue(o >= previous - 1e-6f);
                previous = o;
            }
            Assert.AreEqual(PrecipScale.ModerateFrom, OrbitPicture.SwirlFromLevel);
        }

        [TestMethod]
        public void SwirlCellsAreTheMaximaFromModerateUp()
        {
            const int W = 256, H = 128;
            var levels = new float[W * H];
            var coverage = new float[W * H];
            // A strong cell at lat 30 / lon 60, a weak one (flurries) elsewhere, a second strong one across the date line.
            PutBump(levels, W, H, 30, 60, 8, 0.8f);
            PutBump(levels, W, H, -20, -178, 8, 0.6f);
            PutBump(levels, W, H, 0, -100, 8, 0.15f);
            for (int i = 0; i < coverage.Length; i++) coverage[i] = levels[i] > 0 ? 0.9f : 0f;
            var cells = new List<EveCoverageMath.Blob>();
            int n = OrbitPicture.FindCells(levels, coverage, W, H, 600000.0, 0.0, false, cells);
            Assert.AreEqual(2, n, "the flurry has no swirl");
            // The strongest first, with its centre where the cell is: layer u for lon 60 is 0.5 + 60/360.
            double u, v;
            EveCoverageMath.Uv(cells[0].X, cells[0].Y, cells[0].Z, out u, out v);
            Assert.AreEqual(0.5 + 60.0 / 360.0, u, 0.02);
            Assert.AreEqual((90.0 - 30.0) / 180.0, v, 0.02);
            Assert.IsTrue(cells[0].RadiusM >= OrbitPicture.MinRadiusM);
            Assert.IsTrue(cells[0].Peak >= 0.4);
            // With a shift the centre moves along u.
            var shifted = new List<EveCoverageMath.Blob>();
            OrbitPicture.FindCells(levels, coverage, W, H, 600000.0, 90.0, false, shifted);
            double u2, v2;
            EveCoverageMath.Uv(shifted[0].X, shifted[0].Y, shifted[0].Z, out u2, out v2);
            double du = (u - u2) * 360.0;
            Assert.AreEqual(90.0, ((du % 360.0) + 360.0) % 360.0, 3.0);
        }

        private static void PutBump(float[] values, int w, int h, double latDeg, double lonDeg, double radiusTexels, float peak)
        {
            int cj = (int)((90.0 - latDeg) / 180.0 * h);
            int ci = (int)((lonDeg + 180.0) / 360.0 * w);
            for (int j = -12; j <= 12; j++)
            {
                for (int i = -12; i <= 12; i++)
                {
                    double d = Math.Sqrt(i * i + j * j);
                    if (d >= radiusTexels) continue;
                    int jj = cj + j;
                    if (jj < 0 || jj >= h) continue;
                    int ii = (ci + i + w) % w;
                    values[jj * w + ii] = Math.Max(values[jj * w + ii], (float)(peak * (0.5 + 0.5 * Math.Cos(Math.PI * d / radiusTexels))));
                }
            }
        }

        // --- Catalogue -----------------------------------------------------------------------------------------------

        [TestMethod]
        public void TheAssumptionIsInTheCatalogueOnce()
        {
            int count = 0;
            foreach (ForeignAssumption a in AtmospheresAssumptions.All)
            {
                if (a.Id == AssumptionsC23.Id) count++;
            }
            Assert.AreEqual(1, count);
            Assert.AreEqual(ForeignState.Absent, AtmospheresAssumptions.Find(AssumptionsC23.Id).State, "EVE is not loaded in the tests");
            Assert.AreEqual(WhenChanged.Fallback, AssumptionsC23.EvePainter.Policy);
        }
    }
}
