using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.D37
{
    // The state machine of the camera frost: what builds it, what clears it, flakes on gust peaks, drops down the gravity direction.
    [TestClass]
    public class CameraFrostTests
    {
        private const float Dt = 1f / 30f;
        private static FrostTuning tuning = FrostTuning.Defaults();

        private static FrostInput Weather(FrostView view = FrostView.Eva, float level = 0.9f, PrecipType type = PrecipType.Snow, float airK = 250f,
            float wind = 20f, float gust = 0.2f)
        {
            return new FrostInput
            {
                Dt = Dt, View = view, Level = level, Type = type, TemperatureK = airK, FrostLimitK = 268f, Strength = 1f, AirflowMs = wind,
                GustPhase = gust, Sheltered = false, GravityX = 0f, GravityY = -1f, GravityInPlane = 1f, Seed = 5
            };
        }

        private static void Run(CameraFrost frost, FrostInput input, float seconds)
        {
            int steps = (int)Math.Round(seconds / Dt);
            for (int i = 0; i < steps; i++) frost.Step(ref input, ref tuning);
        }

        [TestMethod]
        public void BuildsInHeavyColdWindySnow()
        {
            var frost = new CameraFrost();
            Run(frost, Weather(), 90f);
            Assert.IsTrue(frost.Exposure > 0.85f, "exposure " + frost.Exposure);
            Assert.AreEqual(FrostShape.MaxGrowth, frost.Growth, 0.02f);
        }

        [TestMethod]
        public void BuildsWithLevelColdWindAndTime()
        {
            float Exposure(float level, float airK, float wind, float seconds, PrecipType type = PrecipType.Snow)
            {
                var frost = new CameraFrost();
                Run(frost, Weather(level: level, airK: airK, wind: wind, type: type), seconds);
                return frost.Exposure;
            }
            Assert.IsTrue(Exposure(0.9f, 250f, 10f, 60f) > Exposure(0.3f, 250f, 10f, 60f), "level");
            Assert.IsTrue(Exposure(0.6f, 240f, 10f, 60f) > Exposure(0.6f, 266f, 10f, 60f), "cold");
            Assert.IsTrue(Exposure(0.6f, 250f, 20f, 60f) > Exposure(0.6f, 250f, 0f, 60f), "wind");
            Assert.IsTrue(Exposure(0.6f, 250f, 10f, 120f) > Exposure(0.6f, 250f, 10f, 20f), "time");
            Assert.AreEqual(0f, Exposure(0.9f, 285f, 20f, 120f), 1e-4f, "no frost above the limit");
            Assert.AreEqual(0f, Exposure(0.9f, 250f, 20f, 120f, PrecipType.Dust), 1e-4f, "dust does not freeze onto the glass");
            Assert.AreEqual(0f, Exposure(0.02f, 250f, 20f, 120f), 1e-4f, "no weather");
            var unknown = new CameraFrost();
            Run(unknown, Weather(airK: float.NaN), 120f);
            Assert.AreEqual(0f, unknown.Exposure, 1e-4f, "an unknown temperature freezes nothing");
        }

        [TestMethod]
        public void ClearsInThirtySecondsWhenSheltered()
        {
            var frost = new CameraFrost();
            Run(frost, Weather(), 120f);
            Assert.IsTrue(frost.Exposure > 0.9f);
            FrostInput shelter = Weather();
            shelter.Sheltered = true;
            Run(frost, shelter, 20f);
            Assert.IsTrue(frost.Exposure > 0.2f && frost.Exposure < 0.5f, "after 20 s " + frost.Exposure);
            Run(frost, shelter, 10.5f);
            Assert.IsTrue(frost.Exposure < 0.02f, "after 30 s " + frost.Exposure);
        }

        [TestMethod]
        public void ClearsWhenTheWeatherStops()
        {
            var frost = new CameraFrost();
            Run(frost, Weather(), 120f);
            FrostInput calm = Weather(level: 0f);
            Run(frost, calm, 31f);
            Assert.IsTrue(frost.Exposure < 0.02f, "exposure " + frost.Exposure);
        }

        [TestMethod]
        public void WarmGlassMeltsFasterThanColdGlassClears()
        {
            var cold = new CameraFrost();
            var warm = new CameraFrost();
            Run(cold, Weather(), 120f);
            Run(warm, Weather(), 120f);
            FrostInput dryCold = Weather(level: 0f, airK: 250f);
            FrostInput dryWarm = Weather(level: 0f, airK: 290f);
            // The warm glass has to heat up first (a visor follows the air in 10 s).
            Run(cold, dryCold, 15f);
            Run(warm, dryWarm, 15f);
            Assert.IsTrue(warm.Exposure < cold.Exposure - 0.1f, "warm " + warm.Exposure + " cold " + cold.Exposure);
        }

        [TestMethod]
        public void AWarmCabinKeepsTheWindowsClear()
        {
            var frost = new CameraFrost();
            Run(frost, Weather(FrostView.Iva, airK: 250f), 600f);
            Assert.IsTrue(frost.Exposure < 0.02f, "exposure " + frost.Exposure + " glass " + frost.GlassK);
            Assert.IsTrue(frost.GlassK > 268f, "the pane stays above the limit: " + frost.GlassK);
            // The same weather on a visor freezes it.
            var visor = new CameraFrost();
            Run(visor, Weather(FrostView.Eva, airK: 250f), 120f);
            Assert.IsTrue(visor.Exposure > 0.5f);
            // An outside cold beyond what the cabin can offset frosts the pane.
            var extreme = new CameraFrost();
            Run(extreme, Weather(FrostView.Iva, airK: 170f), 900f);
            Assert.IsTrue(extreme.Exposure > 0.2f, "extreme cold " + extreme.Exposure);
            Assert.IsTrue(extreme.Exposure <= 1f);
        }

        [TestMethod]
        public void ThePaneFollowsTheOutsideWithALag()
        {
            var frost = new CameraFrost();
            Run(frost, Weather(FrostView.Iva, level: 0f, airK: 293f), 5f);
            Assert.AreEqual(293f + 0f, frost.GlassK, 0.5f);
            // The air falls to 200 K at once; the pane is at 200 + 0.7 * 93 = 265 K in the end.
            Run(frost, Weather(FrostView.Iva, level: 0f, airK: 200f), 30f);
            Assert.IsTrue(frost.GlassK > 280f, "after 30 s " + frost.GlassK);
            Run(frost, Weather(FrostView.Iva, level: 0f, airK: 200f), 600f);
            Assert.AreEqual(265.1f, frost.GlassK, 0.5f);
        }

        [TestMethod]
        public void NothingToDrawWhenTheGlassIsClear()
        {
            var frost = new CameraFrost();
            Assert.IsFalse(frost.NeedsDraw(ref tuning));
            Run(frost, Weather(level: 0f), 20f);
            Assert.IsFalse(frost.NeedsDraw(ref tuning));
            Run(frost, Weather(), 60f);
            Assert.IsTrue(frost.NeedsDraw(ref tuning));
        }

        [TestMethod]
        public void FlakesStickOnGustPeaksAndMelt()
        {
            var frost = new CameraFrost();
            FrostInput input = Weather(level: 0.8f, gust: 0.2f);
            int newFlakes = 0, before = 0;
            // A gust wave with the period of 9 s of WindNoise.
            for (int i = 0; i < (int)(40f / Dt); i++)
            {
                input.GustPhase = 0.5f + 0.5f * (float)Math.Sin(2 * Math.PI * i * Dt / 9.0);
                float previous = i == 0 ? 0f : 0.5f + 0.5f * (float)Math.Sin(2 * Math.PI * (i - 1) * Dt / 9.0);
                before = frost.FlakeCount;
                frost.Step(ref input, ref tuning);
                if (frost.FlakeCount > before)
                {
                    newFlakes++;
                    Assert.IsTrue(previous < 0.8f && input.GustPhase >= 0.8f, "a flake appeared outside a rising gust peak at " + i * Dt);
                }
                Assert.IsTrue(frost.FlakeCount <= CameraFrost.MaxFlakes);
            }
            Assert.IsTrue(newFlakes >= 3, "peaks that stuck flakes " + newFlakes);
            foreach (FrostFlake f in frost.Flakes)
            {
                if (!f.Alive) continue;
                float diameterPx = f.Size * 2f * 1080f;
                Assert.IsTrue(diameterPx >= 2.99f && diameterPx <= 15.01f, "diameter " + diameterPx + " px");
                Assert.IsTrue(f.Life >= 8f && f.Life <= 20f, "life " + f.Life);
                Assert.IsTrue(f.X >= 0f && f.X <= 1f && f.Y >= 0f && f.Y <= 1f);
            }
            // With the gust gone and the weather over, every flake melts within 20 s.
            input.GustPhase = 0.1f;
            input.Level = 0f;
            Run(frost, input, 21f);
            Assert.AreEqual(0, frost.FlakeCount);
        }

        [TestMethod]
        public void NoFlakesWithoutSnowyWeather()
        {
            foreach (PrecipType type in new[] { PrecipType.Rain, PrecipType.Dust, PrecipType.Sand })
            {
                var frost = new CameraFrost();
                FrostInput input = Weather(level: 0.9f, type: type, airK: 285f);
                for (int i = 0; i < (int)(60f / Dt); i++)
                {
                    input.GustPhase = 0.5f + 0.5f * (float)Math.Sin(2 * Math.PI * i * Dt / 9.0);
                    frost.Step(ref input, ref tuning);
                    Assert.AreEqual(0, frost.FlakeCount, type + " at " + i * Dt);
                }
            }
        }

        [TestMethod]
        public void FlakesStayAwayFromTheMiddleMoreThanTheEdges()
        {
            double centre = 0, outer = 0;
            for (int seed = 1; seed < 40; seed++)
            {
                var frost = new CameraFrost();
                FrostInput input = Weather(level: 1f);
                input.Seed = seed;
                for (int i = 0; i < (int)(30f / Dt); i++)
                {
                    input.GustPhase = 0.5f + 0.5f * (float)Math.Sin(2 * Math.PI * i * Dt / 5.3);
                    frost.Step(ref input, ref tuning);
                    foreach (FrostFlake f in frost.Flakes)
                    {
                        if (!f.Alive || f.Age > Dt * 1.5f) continue;
                        if (FrostShape.Radius(f.X, f.Y, 16f / 9f) < 0.4f) centre++; else outer++;
                    }
                }
            }
            // The middle (r < 0.4) is 30 % of the picture's area at 16:9; with the edge bias it holds clearly fewer than half of that.
            Assert.IsTrue(centre / (centre + outer) < 0.15, "centre share " + centre / (centre + outer));
        }

        [TestMethod]
        public void DropsSlideDownTheGravityDirection()
        {
            // Rain on warm glass: drops spawn; they slide along gravity as it lies in the picture.
            Func<float, float, float, float[]> slide = (gx, gy, inPlane) =>
            {
                var frost = new CameraFrost();
                FrostInput input = Weather(level: 0.9f, type: PrecipType.Rain, airK: 288f, gust: 0.2f);
                input.GravityX = gx;
                input.GravityY = gy;
                input.GravityInPlane = inPlane;
                Run(frost, input, 12f);
                var xs = new float[CameraFrost.MaxDrops];
                var ys = new float[CameraFrost.MaxDrops];
                var sizes = new float[CameraFrost.MaxDrops];
                for (int i = 0; i < xs.Length; i++) { xs[i] = frost.Drops[i].X; ys[i] = frost.Drops[i].Y; sizes[i] = frost.Drops[i].Size; }
                int moved = 0;
                float dx = 0, dy = 0;
                Run(frost, input, 6f);
                for (int i = 0; i < xs.Length; i++)
                {
                    // The same drop: alive before and after, with the size it had (a slot taken by a new drop is not compared).
                    if (!frost.Drops[i].Alive || sizes[i] == 0f || sizes[i] != frost.Drops[i].Size) continue;
                    if (Math.Abs(frost.Drops[i].X - xs[i]) + Math.Abs(frost.Drops[i].Y - ys[i]) > 1e-5f) moved++;
                    dx += frost.Drops[i].X - xs[i];
                    dy += frost.Drops[i].Y - ys[i];
                }
                return new[] { moved, dx, dy, frost.DropCount };
            };
            float[] down = slide(0f, -1f, 1f);
            Assert.IsTrue(down[3] >= 3, "drops " + down[3]);
            Assert.IsTrue(down[0] >= 1 && down[2] < -0.01f, "moved " + down[0] + " dy " + down[2]);
            Assert.IsTrue(Math.Abs(down[1]) < Math.Abs(down[2]) * 0.1f, "no sideways drift when gravity is straight down: dx " + down[1]);
            // The camera rolled a quarter turn: down is to the right of the picture.
            float[] right = slide(1f, 0f, 1f);
            Assert.IsTrue(right[1] > 0.005f && Math.Abs(right[2]) < Math.Abs(right[1]) * 0.1f, "rolled: dx " + right[1] + " dy " + right[2]);
            // Looking straight down at the ground, gravity has no component in the picture: the drops hold.
            float[] flat = slide(0f, -1f, 0f);
            Assert.IsTrue(Math.Abs(flat[1]) + Math.Abs(flat[2]) < 0.01f, "looking down: dx " + flat[1] + " dy " + flat[2]);
        }

        [TestMethod]
        public void DropsLeaveThePictureAndDie()
        {
            var frost = new CameraFrost();
            FrostInput input = Weather(level: 0.9f, type: PrecipType.Rain, airK: 288f);
            Run(frost, input, 20f);
            Assert.IsTrue(frost.DropCount > 0 && frost.DropCount <= CameraFrost.MaxDrops);
            input.Level = 0f;
            Run(frost, input, 120f);
            Assert.AreEqual(0, frost.DropCount, "the drops dry up");
        }

        [TestMethod]
        public void TheSameSeedGivesTheSameGlass()
        {
            var a = new CameraFrost();
            var b = new CameraFrost();
            Run(a, Weather(level: 0.8f, gust: 0.95f), 30f);
            Run(b, Weather(level: 0.8f, gust: 0.95f), 30f);
            Assert.AreEqual(a.Exposure, b.Exposure);
            Assert.AreEqual(a.FlakeCount, b.FlakeCount);
            a.Reset();
            Assert.AreEqual(0f, a.Exposure);
            Assert.AreEqual(0, a.FlakeCount);
            Assert.IsFalse(a.NeedsDraw(ref tuning));
        }

        [TestMethod]
        public void ColdAndTheTypeTable()
        {
            Assert.AreEqual(0f, CameraFrost.Cold(float.NaN, 268f, 10f));
            Assert.AreEqual(0f, CameraFrost.Cold(280f, 268f, 10f));
            Assert.AreEqual(1f, CameraFrost.Cold(250f, 268f, 10f), 1e-6f);
            Assert.IsTrue(CameraFrost.Cold(263f, 268f, 10f) > 0f && CameraFrost.Cold(263f, 268f, 10f) < 1f);
            Assert.IsTrue(CameraFrost.TypeWeight(PrecipType.Snow) > CameraFrost.TypeWeight(PrecipType.Rain));
            Assert.AreEqual(0f, CameraFrost.TypeWeight(PrecipType.Methane));
            Assert.AreEqual(1f, CameraFrost.WetWeight(PrecipType.Rain));
            Assert.AreEqual(0f, CameraFrost.WetWeight(PrecipType.Snow));
        }

        [TestMethod]
        public void ViewClassification()
        {
            const float Near = 0.4f, Far = 6f;
            Assert.AreEqual(FrostView.Off, FrostCameraClass.Classify(FrostCameraMode.Flight, false, false, Far, FrostCameraClass.FirstPersonM));
            Assert.AreEqual(FrostView.Off, FrostCameraClass.Classify(FrostCameraMode.Flight, false, true, Far, FrostCameraClass.FirstPersonM));
            Assert.AreEqual(FrostView.Eva, FrostCameraClass.Classify(FrostCameraMode.Flight, false, true, Near, FrostCameraClass.FirstPersonM));
            Assert.AreEqual(FrostView.Off, FrostCameraClass.Classify(FrostCameraMode.Flight, false, false, Near, FrostCameraClass.FirstPersonM));
            Assert.AreEqual(FrostView.Iva, FrostCameraClass.Classify(FrostCameraMode.Iva, false, false, Far, FrostCameraClass.FirstPersonM));
            Assert.AreEqual(FrostView.Iva, FrostCameraClass.Classify(FrostCameraMode.Internal, false, false, Far, FrostCameraClass.FirstPersonM));
            Assert.AreEqual(FrostView.Eva, FrostCameraClass.Classify(FrostCameraMode.Iva, false, true, Far, FrostCameraClass.FirstPersonM));
            Assert.AreEqual(FrostView.Off, FrostCameraClass.Classify(FrostCameraMode.Map, false, true, Near, FrostCameraClass.FirstPersonM));
            Assert.AreEqual(FrostView.Off, FrostCameraClass.Classify(FrostCameraMode.Iva, true, false, Far, FrostCameraClass.FirstPersonM), "map view is off");
            Assert.AreEqual(FrostView.Off, FrostCameraClass.Classify(FrostCameraMode.Other, false, true, Near, FrostCameraClass.FirstPersonM));
        }

        [TestMethod]
        public void LightIsLowAtNightAndHighInSun()
        {
            float night = FrostLight.Factor(-0.5f, 1f, ref tuning);
            float day = FrostLight.Factor(0.8f, 1f, ref tuning);
            float overcast = FrostLight.Factor(0.8f, 0.2f, ref tuning);
            Assert.AreEqual(tuning.Ambient, night, 1e-4f);
            Assert.IsTrue(day > overcast && overcast > night);
            Assert.IsTrue(day <= 1f);
        }
    }
}
