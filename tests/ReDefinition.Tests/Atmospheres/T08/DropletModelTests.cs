using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.T08
{
    // The droplets' state machine against references that do not come from the code under test (package T08).
    //
    // Reference expectations (written before the model was built):
    //   * a dry pane in steady level-1 rain on still air is fully wet after 1 / (1 / FillS - 1 / DryStillS) seconds (+-10 %);
    //   * a wet pane with no rain is dry after DryStillS seconds in still air and after 1 / (1 / DryStillS + (v / BlowRef)^2 / BlowS)
    //     seconds under an airflow v (+-10 %); the dryingSpeed of EVE, about 12 s, is the blow-off at the reference airflow;
    //   * a sheltered pane gets no water; snow wets a warm pane by its melt share and a frozen pane not at all;
    //   * drops slide along the picture's gravity direction and are pushed along the airflow, with a lag.
    [TestClass]
    public class DropletModelTests
    {
        private static DropletInput Rain(float level, float airflow = 0f)
        {
            return new DropletInput { Dt = 0.05f, Level = level, Type = PrecipType.Rain, WarmGlass = 1f, AirflowMs = airflow, GravityX = 0f, GravityY = -1f, GravityInPlane = 1f, AirX = 1f, AirY = 0f, AirInPlane = 1f };
        }

        private static float TimeToWet(DropletInput input, ref DropletTuning t, float target)
        {
            var m = new DropletModel();
            float elapsed = 0f;
            while (m.Wet01 < target && elapsed < 7200f)
            {
                m.Step(ref input, ref t);
                elapsed += input.Dt;
            }
            return elapsed;
        }

        [TestMethod]
        public void APaneFillsInTheConfiguredTime()
        {
            DropletTuning t = DropletTuning.Defaults();
            DropletInput rain = Rain(1f);
            float expected = DropletModel.FillTimeS(1f, PrecipType.Rain, 1f, 0f, ref t);
            Assert.AreEqual(t.FillS * t.DryStillS / (t.DryStillS - t.FillS), expected, 0.01f * expected);
            float measured = TimeToWet(rain, ref t, 0.9999f);
            Assert.AreEqual(expected, measured, 0.10f * expected);
            // Half the level takes about twice as long.
            float half = TimeToWet(Rain(0.5f), ref t, 0.9999f);
            Assert.AreEqual(DropletModel.FillTimeS(0.5f, PrecipType.Rain, 1f, 0f, ref t), half, 0.10f * half);
            Assert.IsTrue(half > 1.8f * measured);
        }

        [TestMethod]
        public void AWetPaneDriesByTheDryingSpeed()
        {
            DropletTuning t = DropletTuning.Defaults();
            foreach (float air in new[] { 0f, 25f, 50f, 100f })
            {
                // Wet it fully with rain, then stop the rain and time the dry-out.
                var m = new DropletModel();
                DropletInput wetting = Rain(1f);
                for (int i = 0; i < 2000 && m.Wet01 < 1f; i++) m.Step(ref wetting, ref t);
                Assert.AreEqual(1f, m.Wet01, 1e-3f);
                DropletInput dry = Rain(0f, air);
                float elapsed = 0f;
                dry.Dt = air > 0f ? 0.02f : 0.5f;
                while (m.Wet01 > 0f && elapsed < 20000f)
                {
                    m.Step(ref dry, ref t);
                    elapsed += dry.Dt;
                }
                float expected = 1f / DropletModel.DryRate(air, ref t);
                Assert.AreEqual(expected, elapsed, 0.10f * expected + dry.Dt, "airflow " + air);
            }
            // The blow-off at the reference airflow is the order of the dryingSpeed of EVE (12 s).
            Assert.AreEqual(12f, 1f / DropletModel.DryRate(t.BlowRefMs, ref t), 1.5f);
        }

        [TestMethod]
        public void ShelteredAndFrozenGlassGetsNoWaterAndSnowWetsAWarmPane()
        {
            DropletTuning t = DropletTuning.Defaults();
            var sheltered = new DropletModel();
            DropletInput rain = Rain(1f);
            rain.Sheltered = true;
            for (int i = 0; i < 400; i++) sheltered.Step(ref rain, ref t);
            Assert.AreEqual(0f, sheltered.Wet01, 1e-6f);

            DropletInput snowFrozen = Rain(1f);
            snowFrozen.Type = PrecipType.Snow;
            snowFrozen.WarmGlass = 0f;
            var a = new DropletModel();
            for (int i = 0; i < 400; i++) a.Step(ref snowFrozen, ref t);
            Assert.AreEqual(0f, a.Wet01, 1e-6f);

            DropletInput snowWarm = snowFrozen;
            snowWarm.WarmGlass = 1f;
            var b = new DropletModel();
            for (int i = 0; i < 400; i++) b.Step(ref snowWarm, ref t);
            Assert.IsTrue(b.Wet01 > 0.05f, "snow melting on warm glass wets it: " + b.Wet01);
            Assert.AreEqual(0f, DropletModel.WaterWeight(PrecipType.Dust, 1f, ref t), 1e-6f);
        }

        [TestMethod]
        public void DropsSlideDownAndAreBlownAlongTheAirflow()
        {
            DropletTuning t = DropletTuning.Defaults();
            var still = new DropletModel();
            DropletInput rain = Rain(1f);
            for (int i = 0; i < 200; i++) still.Step(ref rain, ref t);
            Assert.IsTrue(still.SlideY < -0.01f && Math.Abs(still.SlideX) < 1e-4f, "still air: down " + still.SlideY);

            var fast = new DropletModel();
            DropletInput flying = Rain(1f, 80f);
            for (int i = 0; i < 400; i++) fast.Step(ref flying, ref t);
            Assert.IsTrue(fast.SlideX > 0.2f, "pushed along the airflow: " + fast.SlideX);

            // A level camera (gravity out of the picture plane) leaves only the airflow.
            var level = new DropletModel();
            DropletInput looking = Rain(1f, 0f);
            looking.GravityInPlane = 0f;
            for (int i = 0; i < 200; i++) level.Step(ref looking, ref t);
            Assert.AreEqual(0f, level.SlideY, 1e-4f);
        }

        [TestMethod]
        public void TheSlideFollowsTheAirflowWithALag()
        {
            DropletTuning t = DropletTuning.Defaults();
            var m = new DropletModel();
            DropletInput calm = Rain(1f);
            for (int i = 0; i < 100; i++) m.Step(ref calm, ref t);
            DropletInput gust = Rain(1f, 80f);
            gust.Dt = 0.05f;
            m.Step(ref gust, ref t);
            float after1 = m.SlideX;
            for (int i = 0; i < 200; i++) m.Step(ref gust, ref t);
            Assert.IsTrue(after1 < 0.3f * m.SlideX, "one step moves it a little, the lag settles it: " + after1 + " -> " + m.SlideX);
        }

        [TestMethod]
        public void CoverageIsMonotoneAndReachesTheConfiguredShare()
        {
            DropletTuning t = DropletTuning.Defaults();
            float last = -1f;
            for (float w = 0f; w <= 1.0001f; w += 0.1f)
            {
                float c = DropletModel.Coverage(w, ref t);
                Assert.IsTrue(c >= last);
                last = c;
            }
            Assert.AreEqual(t.CoverMax, DropletModel.Coverage(1f, ref t), 1e-6f);
            Assert.AreEqual(0f, DropletModel.Coverage(0f, ref t), 1e-6f);
        }

        [TestMethod]
        public void TheStateIsDeterministicAndFinite()
        {
            DropletTuning t = DropletTuning.Defaults();
            var a = new DropletModel(); var b = new DropletModel();
            DropletInput rain = Rain(0.7f, 30f);
            for (int i = 0; i < 500; i++) { a.Step(ref rain, ref t); b.Step(ref rain, ref t); }
            Assert.AreEqual(a.Wet01, b.Wet01);
            Assert.AreEqual(a.SlideX, b.SlideX);
            rain.Level = float.NaN;
            rain.GravityX = float.NaN;
            a.Step(ref rain, ref t);
            Assert.IsFalse(float.IsNaN(a.Wet01));
        }
    }
}
