using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.D32
{
    // L0 of the lamp scatter (D32): the density functions are monotone, zero in clear air, bounded and deterministic; the phase
    // function integrates to one; the slews and lags are bounded; the choice of lamps and its fades; the geometry of the beam against a
    // brute-force march; the beam's luminance against sigma.
    [TestClass]
    public class LampMathTests
    {
        private static LampTuning T()
        {
            return LampTuning.Defaults();
        }

        // --- The sources ---

        [TestMethod]
        public void FogSigmaIsTheEqualVisibilityExtinction()
        {
            Assert.AreEqual(0f, LampMath.FogSigma(0f));
            Assert.AreEqual(0f, LampMath.FogSigma(-1f));
            foreach (float d in new[] { 0.0002f, 0.001f, 0.004f, 0.02f })
            {
                // sigma x visibility = 3.912: the medium has the visibility the fog has.
                Assert.AreEqual(LampMath.KoschmiederNumerator, LampMath.FogSigma(d) * LampMath.FogVisibility(d), 1e-3f);
                // The fog's own transmission at that visibility is the 2 % contrast.
                double tau = d * LampMath.FogVisibility(d);
                Assert.AreEqual(0.02, Math.Exp(-tau * tau), 0.0015);
            }
            Assert.IsTrue(float.IsPositiveInfinity(LampMath.FogVisibility(0f)));
        }

        [TestMethod]
        public void FogSigmaIsMonotone()
        {
            float last = -1f;
            for (float d = 0f; d < 0.05f; d += 0.0005f)
            {
                float s = LampMath.FogSigma(d);
                Assert.IsTrue(s >= last, "monotone at " + d);
                last = s;
            }
        }

        [TestMethod]
        public void PrecipSigmaIsZeroWithoutPrecipitationAndMonotoneInLevel()
        {
            foreach (PrecipType type in (PrecipType[])Enum.GetValues(typeof(PrecipType)))
            {
                Assert.AreEqual(0f, LampMath.PrecipSigma(type, 0f, 1.3f), "level 0 gives nothing for " + type);
                Assert.AreEqual(0f, LampMath.PrecipSigma(type, -0.5f, 1.3f));
                float last = 0f;
                for (float level = 0f; level <= 1.0001f; level += 0.05f)
                {
                    float s = LampMath.PrecipSigma(type, level, 1.3f);
                    Assert.IsTrue(s >= last, type + " monotone at " + level);
                    last = s;
                }
                if (type == PrecipType.None) Assert.AreEqual(0f, LampMath.PrecipSigma(type, 1f, 1.3f));
                else Assert.IsTrue(LampMath.PrecipSigma(type, 1f, 1.3f) > 0.005f, type + " has a table value");
            }
            // A level above 1 does not raise it further.
            Assert.AreEqual(LampMath.PrecipSigma(PrecipType.Snow, 1f, 1.3f), LampMath.PrecipSigma(PrecipType.Snow, 3f, 1.3f));
        }

        [TestMethod]
        public void SnowAtBlizzardHasTheVisibilityOfABlizzard()
        {
            float visibility = LampMath.KoschmiederNumerator / LampMath.PrecipSigma(PrecipType.Snow, 1f, 1.3f);
            Assert.IsTrue(visibility > 30f && visibility < 80f, "snow at level 1 sees " + visibility + " m");
            float light = LampMath.KoschmiederNumerator / LampMath.PrecipSigma(PrecipType.Snow, 0.2f, 1.3f);
            Assert.IsTrue(light > 300f && light < 1500f, "snow at level 0.2 sees " + light + " m");
        }

        [TestMethod]
        public void CloudSigmaIsMonotoneAndBounded()
        {
            Assert.AreEqual(0f, LampMath.CloudSigma(0f, 0.05f));
            Assert.AreEqual(0f, LampMath.CloudSigma(-1f, 0.05f));
            float last = 0f;
            for (float d = 0f; d <= 1.5f; d += 0.05f)
            {
                float s = LampMath.CloudSigma(d, 0.05f);
                Assert.IsTrue(s >= last);
                Assert.IsTrue(s <= 0.05f + 1e-6f);
                last = s;
            }
        }

        [TestMethod]
        public void TheMediumIsZeroInClearAir()
        {
            LampTuning t = T();
            LampSigma s = LampMath.Sigma(ref t, LampSources.All, 0f, PrecipType.None, 0f, 0f, 0f, 1f);
            Assert.AreEqual(0f, s.Total);
            Assert.AreEqual(0f, s.Fog);
            Assert.AreEqual(0f, s.Precip);
            Assert.AreEqual(0f, s.Cloud);
            Assert.AreEqual(0f, s.Dust);
            Assert.IsFalse(LampMath.Worth(s.Total, 5f, ref t), "nothing is drawn in clear air");
        }

        [TestMethod]
        public void EachSourceCountsOnItsOwnAndTheTotalIsTheirSum()
        {
            LampTuning t = T();
            LampSources all = LampSources.All;
            LampSigma full = LampMath.Sigma(ref t, all, 0.002f, PrecipType.Snow, 0.6f, 0.4f, 0.003f, 1f);
            Assert.IsTrue(full.Fog > 0f && full.Precip > 0f && full.Cloud > 0f && full.Dust > 0f);
            Assert.AreEqual(full.Fog + full.Precip + full.Cloud + full.Dust, full.Total, 1e-7f);

            var fogOnly = new LampSources { Fog = true };
            LampSigma f = LampMath.Sigma(ref t, fogOnly, 0.002f, PrecipType.Snow, 0.6f, 0.4f, 0.003f, 1f);
            Assert.AreEqual(full.Fog, f.Fog);
            Assert.AreEqual(0f, f.Precip + f.Cloud + f.Dust);

            var precipOnly = new LampSources { Precip = true };
            LampSigma p = LampMath.Sigma(ref t, precipOnly, 0.002f, PrecipType.Snow, 0.6f, 0.4f, 0.003f, 1f);
            Assert.AreEqual(full.Precip, p.Precip);
            Assert.AreEqual(0f, p.Fog + p.Cloud + p.Dust);

            var cloudOnly = new LampSources { Cloud = true };
            LampSigma c = LampMath.Sigma(ref t, cloudOnly, 0.002f, PrecipType.Snow, 0.6f, 0.4f, 0.003f, 1f);
            Assert.AreEqual(full.Cloud, c.Cloud);
            Assert.AreEqual(0f, c.Fog + c.Precip + c.Dust);

            var dustOnly = new LampSources { Dust = true };
            LampSigma d = LampMath.Sigma(ref t, dustOnly, 0.002f, PrecipType.Snow, 0.6f, 0.4f, 0.003f, 1f);
            Assert.AreEqual(full.Dust, d.Dust);
            Assert.AreEqual(0f, d.Fog + d.Precip + d.Cloud);

            Assert.AreEqual(0f, LampMath.Sigma(ref t, default(LampSources), 0.002f, PrecipType.Snow, 0.6f, 0.4f, 0.003f, 1f).Total, "all sources off");
        }

        [TestMethod]
        public void TheMediumIsBoundedDeterministicAndSafeAgainstBadNumbers()
        {
            LampTuning t = T();
            LampSigma huge = LampMath.Sigma(ref t, LampSources.All, 100f, PrecipType.Snow, 5f, 5f, 100f, 100f);
            Assert.IsTrue(huge.Fog <= t.MaxSigma && huge.Precip <= t.MaxSigma && huge.Cloud <= t.MaxSigma && huge.Dust <= t.MaxSigma);
            LampSigma a = LampMath.Sigma(ref t, LampSources.All, 0.001f, PrecipType.Sleet, 0.33f, 0.2f, 0.001f, 1.5f);
            LampSigma b = LampMath.Sigma(ref t, LampSources.All, 0.001f, PrecipType.Sleet, 0.33f, 0.2f, 0.001f, 1.5f);
            Assert.AreEqual(a.Total, b.Total);
            LampSigma nan = LampMath.Sigma(ref t, LampSources.All, float.NaN, PrecipType.Snow, float.NaN, float.NaN, float.NaN, 1f);
            Assert.IsFalse(float.IsNaN(nan.Total), "no NaN gets through");
            Assert.AreEqual(0f, LampMath.Sigma(ref t, LampSources.All, 0.01f, PrecipType.Snow, 1f, 1f, 0.1f, 0f).Total, "a body scale of 0 switches the scatter off");
        }

        // --- The phase function ---

        [TestMethod]
        public void TheHenyeyGreensteinPhaseIntegratesToOne()
        {
            foreach (float g in new[] { 0f, 0.3f, 0.6f, 0.85f })
            {
                // The integral over the sphere: 2 pi x the integral over cos from -1 to 1.
                double sum = 0;
                const int N = 20000;
                for (int i = 0; i < N; i++)
                {
                    float c = -1f + 2f * (i + 0.5f) / N;
                    sum += LampMath.HenyeyGreenstein(c, g) * (2.0 / N);
                }
                Assert.AreEqual(1.0, 2.0 * Math.PI * sum, 0.01, "g = " + g);
            }
            Assert.AreEqual(1f / (4f * (float)Math.PI), LampMath.HenyeyGreenstein(0.3f, 0f), 1e-6f, "g 0 is isotropic");
            Assert.IsTrue(LampMath.HenyeyGreenstein(1f, 0.6f) > 20f * LampMath.HenyeyGreenstein(-1f, 0.6f), "forward scattering dominates at g 0.6");
        }

        // --- The slews ---

        [TestMethod]
        public void SigmaSlewMovesBoundedAndNeverOvershoots()
        {
            float current = 0f;
            const float dt = 1f / 60f;
            float last = 0f;
            for (int i = 0; i < 60 * 30; i++)
            {
                current = LampMath.SlewSigma(current, 0.08f, 1f, 0.01f, dt);
                Assert.IsTrue(current >= last && current <= 0.08f + 1e-7f, "monotone rise without overshoot");
                Assert.IsTrue(current - last <= 1f * Math.Max(last, 0.01f) * dt + 1e-7f, "at most the relative rate per second");
                last = current;
            }
            Assert.AreEqual(0.08f, current, 1e-6f, "reaches the target");
            for (int i = 0; i < 60 * 30; i++)
            {
                float next = LampMath.SlewSigma(current, 0f, 1f, 0.01f, dt);
                Assert.IsTrue(next <= current && next >= 0f);
                current = next;
            }
            Assert.AreEqual(0f, current, 1e-6f, "falls to zero");
            Assert.AreEqual(0.02f, LampMath.SlewSigma(0.02f, 0.5f, 1f, 0.01f, 0f), "no time, no change");
        }

        [TestMethod]
        public void BrightnessLagFollowsWithTheTimeConstant()
        {
            float v = 0f;
            for (int i = 0; i < 6; i++) v = LampMath.Lag(v, 1f, 0.1f, 1f / 60f);
            // After 0.1 s (6 frames of 1/60 s) the lag has covered 1 - 1/e of the step.
            Assert.AreEqual(1.0 - Math.Exp(-1.0), v, 1e-4);
            Assert.AreEqual(1f, LampMath.Lag(0f, 1f, 0f, 0.01f), "a zero time constant follows at once");
            Assert.AreEqual(0.3f, LampMath.Lag(0.3f, 1f, 0.1f, 0f));
        }

        [TestMethod]
        public void NothingIsWorthDrawingBelowTheSigmaOrBeyondTheRange()
        {
            LampTuning t = T();
            Assert.IsFalse(LampMath.Worth(5e-5f, 10f, ref t), "sigma below 1e-4");
            Assert.IsTrue(LampMath.Worth(2e-4f, 10f, ref t));
            Assert.IsFalse(LampMath.Worth(0.05f, 151f, ref t), "no lamp within 150 m");
            Assert.IsTrue(LampMath.Worth(0.05f, 150f, ref t));
        }

        // --- The choice of lamps ---

        [TestMethod]
        public void TheDrawingHoldsBetweenTheEnterAndTheLeaveThresholds()
        {
            LampTuning t = T();
            // Not drawing: the plain threshold enters.
            Assert.IsFalse(LampMath.WorthHolding(false, 0.9e-4f, 10f, ref t));
            Assert.IsTrue(LampMath.WorthHolding(false, 1.1e-4f, 10f, ref t));
            // Drawing: stays until 0.8 of the least sigma, and until 1.1 times the range.
            Assert.IsTrue(LampMath.WorthHolding(true, 0.9e-4f, 10f, ref t), "patchy snow near the threshold does not switch it off");
            Assert.IsFalse(LampMath.WorthHolding(true, 0.7e-4f, 10f, ref t));
            Assert.IsTrue(LampMath.WorthHolding(true, 0.05f, 160f, ref t), "a lamp just beyond 150 m stays");
            Assert.IsFalse(LampMath.WorthHolding(true, 0.05f, 166f, ref t));
        }

        [TestMethod]
        public void TheDustHazeNeedsAir()
        {
            Assert.AreEqual(1f, LampMath.AirShare(101.3, 101.3));
            Assert.AreEqual(0.5f, LampMath.AirShare(50.65, 101.3), 1e-6f);
            Assert.AreEqual(0f, LampMath.AirShare(0.0, 101.3), "vacuum");
            Assert.AreEqual(0f, LampMath.AirShare(10.0, 0.0), "a body without air");
            Assert.AreEqual(1f, LampMath.AirShare(120.0, 101.3), "below the surface pressure's reference it is held to 1");
        }

        [TestMethod]
        public void TheTuningIsHeldToValuesTheMathsCanTake()
        {
            LampTuning t = T();
            t.PhaseG = 1.5f;
            t.Albedo = 7f;
            t.Steps = 1000f;
            t.FadeS = 0f;
            t.GlintSizeM = float.NaN;
            t.Gain = -3f;
            LampTuning c = t.Clamped();
            Assert.IsTrue(c.PhaseG <= 0.95f);
            Assert.IsFalse(float.IsInfinity(LampMath.HenyeyGreenstein(1f, c.PhaseG)), "no division by zero at g = 1");
            Assert.AreEqual(1f, c.Albedo);
            Assert.AreEqual(16f, c.Steps);
            Assert.IsTrue(c.FadeS > 0f);
            Assert.IsFalse(float.IsNaN(c.GlintSizeM));
            Assert.AreEqual(0f, c.Gain);
            // The defaults sit inside their own ranges.
            LampTuning d = T();
            LampTuning dc = d.Clamped();
            Assert.AreEqual(d.PhaseG, dc.PhaseG);
            Assert.AreEqual(d.Steps, dc.Steps);
            Assert.AreEqual(d.Gain, dc.Gain);
            Assert.AreEqual(d.MinSigma, dc.MinSigma);
            Assert.AreEqual(d.ActiveRangeM, dc.ActiveRangeM);
            Assert.AreEqual(d.GlintSizeM, dc.GlintSizeM);
        }

        [TestMethod]
        public void TheNoiseTableIsDeterministicAndSpreadsOverZeroToOne()
        {
            Assert.AreEqual(LampMath.NoiseValue(5, 9), LampMath.NoiseValue(5, 9));
            Assert.AreEqual(LampMath.NoiseValue(5, 9), LampMath.NoiseValue(5 + 256, 9 + 512), "the table wraps at 256");
            double sum = 0;
            int below = 0;
            const int N = 65536;
            for (int i = 0; i < N; i++)
            {
                float v = LampMath.NoiseValue(i & 255, i >> 8);
                Assert.IsTrue(v >= 0f && v < 1f);
                sum += v;
                if (v < 0.5f) below++;
            }
            Assert.AreEqual(0.5, sum / N, 0.01);
            Assert.AreEqual(0.5, below / (double)N, 0.01);
        }

        [TestMethod]
        public void TheGlintFactorHasAMeanOfOne()
        {
            // The shader's noise: trilinear (smoothstep) between lattice values NoiseValue(x + 37 z, y + 17 z); its cube times 5.675
            // must average 1 so that the glints leave the beam's mean as it is.
            var rng = new Random(77);
            double sum = 0;
            const int N = 100000;
            for (int k = 0; k < N; k++)
            {
                double px = rng.NextDouble() * 200.0, py = rng.NextDouble() * 200.0, pz = rng.NextDouble() * 200.0;
                int ix = (int)Math.Floor(px), iy = (int)Math.Floor(py), iz = (int)Math.Floor(pz);
                double fx = Smooth(px - ix), fy = Smooth(py - iy), fz = Smooth(pz - iz);
                double v0 = Plane(ix, iy, iz, fx, fy), v1 = Plane(ix, iy, iz + 1, fx, fy);
                double n = v0 + (v1 - v0) * fz;
                sum += 5.675 * n * n * n;
            }
            Assert.AreEqual(1.0, sum / N, 0.02);
        }

        private static double Smooth(double f)
        {
            return f * f * (3 - 2 * f);
        }

        private static double Plane(int ix, int iy, int iz, double fx, double fy)
        {
            double a = LampMath.NoiseValue(ix + 37 * iz, iy + 17 * iz), b = LampMath.NoiseValue(ix + 1 + 37 * iz, iy + 17 * iz);
            double c = LampMath.NoiseValue(ix + 37 * iz, iy + 1 + 17 * iz), d = LampMath.NoiseValue(ix + 1 + 37 * iz, iy + 1 + 17 * iz);
            return (a + (b - a) * fx) + ((c + (d - c) * fx) - (a + (b - a) * fx)) * fy;
        }

        [TestMethod]
        public void TheScoreIsIntensityTimesRangeSquaredOverDistanceSquaredPlusOne()
        {
            Assert.AreEqual(2f * 100f * 100f / (9f + 1f), LampMath.Score(2f, 100f, 9f), 1e-3f);
            Assert.IsTrue(LampMath.Score(2f, 100f, 25f) > LampMath.Score(2f, 100f, 400f), "nearer scores higher");
            Assert.IsTrue(LampMath.Score(4f, 50f, 25f) > LampMath.Score(1f, 50f, 25f), "brighter scores higher");
        }

        [TestMethod]
        public void OnlyRealLampsCount()
        {
            LampTuning t = T();
            Assert.IsFalse(LampMath.IsLamp(0.1f, 10f, ref t), "intensity must be above 0.1");
            Assert.IsFalse(LampMath.IsLamp(1f, 2f, ref t), "range must be above 2 m");
            Assert.IsTrue(LampMath.IsLamp(0.11f, 2.1f, ref t));
        }

        [TestMethod]
        public void TheCapFollowsTheTierAndTheSetting()
        {
            Assert.AreEqual(2, LampMath.TierCap(QualityTier.Low));
            Assert.AreEqual(4, LampMath.TierCap(QualityTier.Medium));
            Assert.AreEqual(8, LampMath.TierCap(QualityTier.High));
            Assert.AreEqual(4, LampMath.TierCap(QualityTier.Auto));
            Assert.AreEqual(2, LampMath.Cap(8, QualityTier.Low));
            Assert.AreEqual(3, LampMath.Cap(3, QualityTier.High));
            Assert.AreEqual(0, LampMath.Cap(0, QualityTier.High));
        }

        private static List<LampCandidate> Candidates(params float[] scores)
        {
            var list = new List<LampCandidate>();
            for (int i = 0; i < scores.Length; i++) list.Add(new LampCandidate(100 + i, scores[i]));
            return list;
        }

        [TestMethod]
        public void TheSelectorKeepsTheBestAndFadesThemInOverTheFadeTime()
        {
            var sel = new LampSelector();
            sel.Rescore(Candidates(5f, 9f, 1f, 7f, 3f), 2);
            Assert.AreEqual(2, sel.Count);
            // Ids 101 (score 9) and 103 (score 7) are in; they begin at fade 0.
            float f;
            Assert.IsTrue(sel.TryGetFade(101, out f) && f == 0f);
            Assert.IsTrue(sel.TryGetFade(103, out f));
            Assert.IsFalse(sel.TryGetFade(100, out f));
            for (int i = 0; i < 9; i++) sel.Step(0.03f, 0.3f);
            Assert.IsTrue(sel.TryGetFade(101, out f));
            Assert.AreEqual(0.9f, f, 1e-4f, "0.27 s of 0.3 s");
            sel.Step(0.03f, 0.3f);
            sel.Step(0.03f, 0.3f);
            Assert.IsTrue(sel.TryGetFade(101, out f) && f == 1f, "fully in after the fade time");
        }

        [TestMethod]
        public void ALampThatLeavesFadesOutAndIsDropped()
        {
            var sel = new LampSelector();
            sel.Rescore(Candidates(9f, 7f), 2);
            for (int i = 0; i < 20; i++) sel.Step(0.03f, 0.3f);
            // 101 now scores lower than a new lamp 102 and drops out of a cap of 2 ... the new one needs a better score than 100/101.
            var next = Candidates(9f, 1f, 12f);
            sel.Rescore(next, 2);
            Assert.AreEqual(3, sel.Count, "the leaving lamp is still in the set while it fades");
            float f;
            Assert.IsTrue(sel.TryGetFade(101, out f) && f == 1f);
            sel.Step(0.15f, 0.3f);
            Assert.IsTrue(sel.TryGetFade(101, out f));
            Assert.AreEqual(0.5f, f, 1e-4f);
            sel.Step(0.2f, 0.3f);
            Assert.IsFalse(sel.TryGetFade(101, out f), "dropped once faded out");
            Assert.AreEqual(2, sel.Count);
        }

        [TestMethod]
        public void TheSelectorIsDeterministicOnTies()
        {
            var a = new LampSelector();
            var b = new LampSelector();
            a.Rescore(new List<LampCandidate> { new LampCandidate(5, 1f), new LampCandidate(3, 1f), new LampCandidate(9, 1f) }, 2);
            b.Rescore(new List<LampCandidate> { new LampCandidate(9, 1f), new LampCandidate(5, 1f), new LampCandidate(3, 1f) }, 2);
            Assert.AreEqual(a.Count, b.Count);
            float f;
            Assert.IsTrue(a.TryGetFade(3, out f) && a.TryGetFade(5, out f) && !a.TryGetFade(9, out f), "the lower ids win a tie");
            Assert.IsTrue(b.TryGetFade(3, out f) && b.TryGetFade(5, out f) && !b.TryGetFade(9, out f));
        }

        [TestMethod]
        public void ACapOfZeroSelectsNothing()
        {
            var sel = new LampSelector();
            sel.Rescore(Candidates(5f, 9f), 0);
            Assert.AreEqual(0, sel.Count);
        }

        // --- The geometry of the beam ---

        private static readonly LampVec Zero = new LampVec(0f, 0f, 0f);

        // The march over the whole ray with no interval at all: the reference the interval must agree with.
        private static float Brute(LampVec origin, LampVec dir, float tMax, LampVec pos, LampVec axis, float range, float cosOuter,
            float sigma, float sigmaCam, ref LampTuning t)
        {
            const int N = 40000;
            float cosInner = cosOuter > 0f ? (float)Math.Cos(Math.Acos(cosOuter) * t.SpotInner) : 1f;
            double h = tMax / N, sum = 0;
            for (int i = 0; i < N; i++)
            {
                float tt = (float)(h * (i + 0.5));
                LampVec x = origin + dir * tt;
                LampVec l = x - pos;
                float d = l.Length;
                if (d < 1e-4f) continue;
                LampVec travel = l * (1f / d);
                float e = LampMath.Emission(d, LampVec.Dot(travel, axis), range, cosOuter, cosInner, t.NearFadeM);
                if (e <= 0f) continue;
                float phase = LampMath.HenyeyGreenstein(LampVec.Dot(travel, dir * -1f), t.PhaseG);
                sum += e * phase * Math.Exp(-t.AttenScale * (sigmaCam * tt + sigma * d));
            }
            return (float)(t.Albedo * sigma * sum * h);
        }

        [TestMethod]
        public void TheIntervalHoldsExactlyWhereTheLampShines()
        {
            // Random rays, lamps and cones: every sample outside the interval has zero emission, and the interval-based march agrees with the
            // march over the whole ray.
            var rng = new Random(20261111);
            LampTuning t = T();
            int checkedRays = 0, hits = 0;
            for (int n = 0; n < 160; n++)
            {
                LampVec pos = new LampVec(Rnd(rng, -3, 3), Rnd(rng, -3, 3), Rnd(rng, -3, 3));
                LampVec axis = new LampVec(Rnd(rng, -1, 1), Rnd(rng, -1, 1), Rnd(rng, -1, 1)).Normalized;
                float range = Rnd(rng, 20, 90);
                float cosOuter = n % 4 == 0 ? 0f : (float)Math.Cos(Rnd(rng, 8, 70) * Math.PI / 180.0);
                LampVec origin = new LampVec(Rnd(rng, -40, 40), Rnd(rng, -40, 40), Rnd(rng, -40, 40));
                // Aim near the lamp so that many rays cross the cone.
                LampVec aim = pos + axis * Rnd(rng, 5, 40) + new LampVec(Rnd(rng, -6, 6), Rnd(rng, -6, 6), Rnd(rng, -6, 6));
                LampVec dir = (aim - origin).Normalized;
                float tMax = Rnd(rng, 30, 160);
                float t0, t1;
                bool any = LampMath.BeamInterval(origin, dir, tMax, pos, axis, range, cosOuter, out t0, out t1);
                float cosInner = cosOuter > 0f ? (float)Math.Cos(Math.Acos(cosOuter) * t.SpotInner) : 1f;
                // Outside the interval nothing shines.
                const int S = 3000;
                for (int i = 0; i < S; i++)
                {
                    float tt = tMax * (i + 0.5f) / S;
                    if (any && tt >= t0 && tt <= t1) continue;
                    LampVec l = origin + dir * tt - pos;
                    float d = l.Length;
                    if (d < 1e-4f) continue;
                    float e = LampMath.Emission(d, LampVec.Dot(l * (1f / d), axis), range, cosOuter, cosInner, t.NearFadeM);
                    Assert.IsTrue(e <= 1e-5f, "emission " + e + " at t " + tt + " outside the interval [" + t0 + ", " + t1 + "] (case " + n + ")");
                }
                checkedRays++;
                if (!any) continue;
                hits++;
                Assert.IsTrue(t0 >= 0f && t1 <= tMax + 1e-3f && t1 > t0);
                float a = LampMath.BeamRadiance(origin, dir, tMax, pos, axis, range, cosOuter, 0.03f, 0.03f, ref t, 4000, 0.5f);
                float b = Brute(origin, dir, tMax, pos, axis, range, cosOuter, 0.03f, 0.03f, ref t);
                Assert.AreEqual(b, a, Math.Max(2e-3f * b, 1e-7f), "case " + n);
            }
            Assert.AreEqual(160, checkedRays);
            Assert.IsTrue(hits > 40, "enough rays cross a lamp's reach (" + hits + ")");
        }

        private static float Rnd(Random r, float lo, float hi)
        {
            return lo + (float)r.NextDouble() * (hi - lo);
        }

        [TestMethod]
        public void ARayThatMissesTheConeGetsNothing()
        {
            LampTuning t = T();
            LampVec axis = new LampVec(0f, 0f, 1f);
            float cosOuter = (float)Math.Cos(20.0 * Math.PI / 180.0);
            // Behind the lamp, looking away.
            float t0, t1;
            Assert.IsFalse(LampMath.BeamInterval(new LampVec(0f, 0f, -10f), new LampVec(0f, 0f, -1f), 100f, Zero, axis, 80f, cosOuter, out t0, out t1));
            // Beside the cone, parallel to its axis, 60 m off (outside the range as well).
            Assert.IsFalse(LampMath.BeamInterval(new LampVec(60f, 0f, -10f), new LampVec(0f, 0f, 1f), 100f, Zero, axis, 80f, cosOuter, out t0, out t1));
            Assert.AreEqual(0f, LampMath.BeamRadiance(new LampVec(60f, 0f, -10f), new LampVec(0f, 0f, 1f), 100f, Zero, axis, 80f, cosOuter, 0.05f, 0.05f, ref t, 10, 0.5f));
            // Along the axis from behind: the interval starts at the lamp.
            Assert.IsTrue(LampMath.BeamInterval(new LampVec(0f, 0f, -10f), new LampVec(0f, 0f, 1f), 200f, Zero, axis, 80f, cosOuter, out t0, out t1));
            Assert.AreEqual(10f, t0, 1e-3f);
            Assert.AreEqual(90f, t1, 1e-3f);
            // Inside the cone: from the camera on.
            Assert.IsTrue(LampMath.BeamInterval(new LampVec(0f, 0f, 20f), new LampVec(0f, 0f, 1f), 200f, Zero, axis, 80f, cosOuter, out t0, out t1));
            Assert.AreEqual(0f, t0, 1e-3f);
            Assert.AreEqual(60f, t1, 1e-3f);
        }

        [TestMethod]
        public void ADepthHitCutsTheBeamOff()
        {
            // A wall at 25 m along a ray through the beam: the radiance with the wall is the part up to it, and less than without it.
            LampTuning t = T();
            LampVec axis = new LampVec(0f, 0f, 1f);
            float cosOuter = (float)Math.Cos(25.0 * Math.PI / 180.0);
            LampVec origin = new LampVec(-12f, 0f, 20f);
            LampVec dir = new LampVec(1f, 0f, 0f);
            float open = LampMath.BeamRadiance(origin, dir, 1e6f, Zero, axis, 100f, cosOuter, 0.03f, 0.03f, ref t, 1000, 0.5f);
            float walled = LampMath.BeamRadiance(origin, dir, 12f, Zero, axis, 100f, cosOuter, 0.03f, 0.03f, ref t, 1000, 0.5f);
            float nothing = LampMath.BeamRadiance(origin, dir, 0.001f, Zero, axis, 100f, cosOuter, 0.03f, 0.03f, ref t, 1000, 0.5f);
            Assert.IsTrue(open > 0f);
            // The wall at the axis keeps the camera's half of the ray, which scatters forward towards the camera and so carries more
            // than half of the total.
            Assert.IsTrue(walled < open && walled > open * 0.5f, "walled " + walled + " of " + open);
            Assert.IsTrue(nothing < 1e-6f, "a wall at the camera hides all of it");
        }

        [TestMethod]
        public void TheBeamIsNothingInClearAirAndRisesWithSigma()
        {
            LampTuning t = T();
            LampVec axis = new LampVec(0f, 0f, 1f);
            float cosOuter = (float)Math.Cos(20.0 * Math.PI / 180.0);
            // A camera 12 m to the side of the axis, 15 m in front of the lamp, looking across the beam; and one behind the lamp looking
            // along it.
            LampVec across = new LampVec(12f, 0f, 15f);
            LampVec acrossDir = new LampVec(-1f, 0f, 0f);
            LampVec along = new LampVec(0.5f, 0.3f, -6f);
            LampVec alongDir = new LampVec(0f, 0f, 1f);
            foreach (bool side in new[] { true, false })
            {
                LampVec o = side ? across : along, d = side ? acrossDir : alongDir;
                Assert.AreEqual(0f, LampMath.BeamRadiance(o, d, 100f, Zero, axis, 120f, cosOuter, 0f, 0f, ref t, 10, 0.5f), "clear air (side " + side + ")");
                float last = 0f;
                // Over the sigmas of snow and fog up to the blizzard, each step brightens the beam.
                foreach (float sigma in new[] { 0.0005f, 0.001f, 0.002f, 0.005f, 0.01f, 0.02f, 0.03f, 0.05f })
                {
                    float r = LampMath.BeamRadiance(o, d, 100f, Zero, axis, 120f, cosOuter, sigma, sigma, ref t, 400, 0.5f);
                    Assert.IsTrue(r > last, "the beam rises with sigma at " + sigma + " (side " + side + "): " + r + " after " + last);
                    last = r;
                }
            }
        }

        [TestMethod]
        public void ThickAirShortensTheBeamAndTheCameraSideAttenuates()
        {
            LampTuning t = T();
            LampVec axis = new LampVec(0f, 0f, 1f);
            float cosOuter = (float)Math.Cos(20.0 * Math.PI / 180.0);
            LampVec across = new LampVec(12f, 0f, 15f);
            LampVec acrossDir = new LampVec(-1f, 0f, 0f);
            float peak = 0f, atPeak = 0f;
            foreach (float sigma in new[] { 0.01f, 0.03f, 0.06f, 0.1f, 0.2f, 0.4f })
            {
                float r = LampMath.BeamRadiance(across, acrossDir, 100f, Zero, axis, 120f, cosOuter, sigma, sigma, ref t, 400, 0.5f);
                if (r > peak)
                {
                    peak = r;
                    atPeak = sigma;
                }
            }
            Assert.IsTrue(atPeak < 0.4f, "the beam stops rising and falls in a whiteout");
            float thin = LampMath.BeamRadiance(across, acrossDir, 100f, Zero, axis, 120f, cosOuter, 0.03f, 0f, ref t, 400, 0.5f);
            float thick = LampMath.BeamRadiance(across, acrossDir, 100f, Zero, axis, 120f, cosOuter, 0.03f, 0.1f, ref t, 400, 0.5f);
            Assert.IsTrue(thick < thin, "a cloud between the camera and the beam dims it");
            // Linear in the weight a caller multiplies on: twice the radiance for a ray with twice the integral is the caller's.
            float again = LampMath.BeamRadiance(across, acrossDir, 100f, Zero, axis, 120f, cosOuter, 0.03f, 0f, ref t, 400, 0.5f);
            Assert.AreEqual(thin, again, "deterministic");
        }

        [TestMethod]
        public void APointLightShinesAllAround()
        {
            LampTuning t = T();
            LampVec axis = new LampVec(0f, 0f, 1f);
            float a = LampMath.BeamRadiance(new LampVec(10f, 0f, 0f), new LampVec(-1f, 0f, 0f), 60f, Zero, axis, 50f, 0f, 0.03f, 0.03f, ref t, 400, 0.5f);
            float b = LampMath.BeamRadiance(new LampVec(0f, 0f, -10f), new LampVec(0f, 0f, 1f), 60f, Zero, axis, 50f, 0f, 0.03f, 0.03f, ref t, 400, 0.5f);
            float c = LampMath.BeamRadiance(new LampVec(0f, -10f, 0f), new LampVec(0f, 1f, 0f), 60f, Zero, axis, 50f, 0f, 0.03f, 0.03f, ref t, 400, 0.5f);
            Assert.IsTrue(a > 0f && b > 0f && c > 0f);
            Assert.AreEqual(a, c, a * 0.02f, "a point light is the same from the side and from below");
        }

        // --- The depth set-up and the glints ---

        [TestMethod]
        public void TheDepthParametersGiveTheClipPlanesBack()
        {
            float x, y, z, w;
            const float near = 0.21f, far = 750000f;
            LampDepth.ZParams(near, far, true, out x, out y, out z, out w);
            Assert.AreEqual(near, 1f / (z * 1f + w), near * 1e-3f, "reversed: raw 1 is the near plane");
            Assert.AreEqual(far, 1f / (z * 0f + w), far * 1e-3f, "reversed: raw 0 is the far plane");
            LampDepth.ZParams(near, far, false, out x, out y, out z, out w);
            Assert.AreEqual(near, 1f / (z * 0f + w), near * 1e-3f, "forward: raw 0 is the near plane");
            // (In single precision the far end of the forward encoding cancels; only the order is asserted there.)
            Assert.IsTrue(1f / (z * 0.5f + w) > 1f / (z * 0.2f + w), "forward: the eye depth rises with the raw depth");
        }

        [TestMethod]
        public void TheGlintsScaleWithTheLevelAndThePrecipitationShare()
        {
            Assert.AreEqual(0f, LampMath.GlintDepth(0.35f, 0.8f, 0f, 0.05f), "fog and cloud alone keep a smooth beam");
            Assert.AreEqual(0f, LampMath.GlintDepth(0.35f, 0f, 0.04f, 0.05f), "no level, no glints");
            Assert.AreEqual(0f, LampMath.GlintDepth(0.35f, 0.8f, 0.04f, 0f));
            Assert.AreEqual(0.35f, LampMath.GlintDepth(0.35f, 1f, 0.05f, 0.05f), 1e-6f);
            Assert.IsTrue(LampMath.GlintDepth(0.35f, 0.5f, 0.03f, 0.05f) < LampMath.GlintDepth(0.35f, 1f, 0.03f, 0.05f));
            Assert.IsTrue(LampMath.GlintDepth(0.35f, 1f, 0.01f, 0.05f) < LampMath.GlintDepth(0.35f, 1f, 0.04f, 0.05f));
        }

        [TestMethod]
        public void TheCloudShellFadesAtItsEdges()
        {
            Assert.AreEqual(0f, LampMedium.ShellFactor(900f, 1000f, 2000f));
            Assert.AreEqual(0f, LampMedium.ShellFactor(2100f, 1000f, 2000f));
            Assert.AreEqual(1f, LampMedium.ShellFactor(1500f, 1000f, 2000f));
            float inner = LampMedium.ShellFactor(1050f, 1000f, 2000f);
            Assert.IsTrue(inner > 0f && inner < 1f);
            Assert.IsTrue(LampMedium.ShellFactor(1090f, 1000f, 2000f) > inner);
            Assert.AreEqual(0f, LampMedium.ShellFactor(1500f, 2000f, 1000f), "a shell with no depth gives nothing");
        }
    }
}
