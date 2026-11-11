using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.C22
{
    // The frame the renderer computes from a view: what draws, how many, which way things fall and drift, the veil and the type table.
    [TestClass]
    public class PrecipFrameTests
    {
        private static PrecipInput Input(PrecipType type, float level, float windE = 0f, float windN = 0f)
        {
            return new PrecipInput
            {
                Active = true,
                Type = type,
                Level = level,
                VisibilityM = 400f,
                WindE = windE,
                WindN = windN,
                GustNoise = 0.5f,
                GustDirNoise = 0.5f,
                FlakeFallMs = 1.1f,
                FlakeBrightness = 1f,
                Seed = 5,
                Near = new FlakeLayer { Width = 7f, Share = 0.06f, SizeMin = 0.10f, SizeMax = 0.26f, Opacity = 0.5f },
                Mid = new FlakeLayer { Width = 18f, Share = 0.34f, SizeMin = 0.03f, SizeMax = 0.08f, Opacity = 0.95f },
                Far = new FlakeLayer { Width = 36f, Share = 0.60f, SizeMin = 0.02f, SizeMax = 0.05f, Opacity = 1f },
                VeilGroundAlpha = level > 0.4f ? (level - 0.4f) : 0f,
                VeilSkyAlpha = level > 0.4f ? (level - 0.4f) * 1.1f : 0f,
                VeilGroundColour = new Color(0.8f, 0.8f, 0.85f, 1f),
                VeilSkyColour = new Color(0.6f, 0.62f, 0.66f, 1f),
                SkyGreyStart = 0f,
                SkyGreyFull = 30f,
                Density = 1f,
                TypeEnabled = true,
                FovYRad = 60f * Mathf.Deg2Rad,
                HeightPx = 1080f,
                Dt = 1f / 60f,
                GroundDistM = 1e6f
            };
        }

        private static PrecipFrame Settle(PrecipInput input, PrecipState state, int frames = 120)
        {
            PrecipTuning tuning = PrecipTuning.Defaults();
            PrecipFrame frame = default(PrecipFrame);
            for (int i = 0; i < frames; i++) PrecipLookParams.Compute(ref input, ref tuning, state, out frame);
            return frame;
        }

        [TestMethod]
        public void NothingDrawsBelowPointZeroFourOrForASwitchedOffType()
        {
            Assert.IsFalse(Settle(Input(PrecipType.Snow, 0.039f), new PrecipState()).Draw);
            Assert.IsTrue(Settle(Input(PrecipType.Snow, 0.05f), new PrecipState()).Draw);
            PrecipInput off = Input(PrecipType.Snow, 0.7f);
            off.TypeEnabled = false;
            PrecipFrame frame = Settle(off, new PrecipState());
            Assert.IsFalse(frame.Draw);
            Assert.AreEqual(0, frame.TotalCount);
            PrecipInput none = Input(PrecipType.None, 0.7f);
            Assert.IsFalse(Settle(none, new PrecipState()).Draw);
            PrecipInput dead = Input(PrecipType.Snow, 0.7f);
            dead.Density = 0f;
            Assert.IsFalse(Settle(dead, new PrecipState()).Draw, "precipDensity 0 draws nothing");
            PrecipInput inactive = Input(PrecipType.Snow, 0.7f);
            inactive.Active = false;
            Assert.IsFalse(Settle(inactive, new PrecipState()).Draw);
        }

        [TestMethod]
        public void TheNearLayerComesInAtPointTwoAndTheTotalStaysWithinTheBudget()
        {
            PrecipFrame low = Settle(Input(PrecipType.Snow, 0.15f), new PrecipState());
            Assert.IsFalse(low.Near.Active);
            Assert.IsTrue(low.Mid.Active && low.Far.Active);
            PrecipFrame mid = Settle(Input(PrecipType.Snow, 0.25f), new PrecipState());
            Assert.IsTrue(mid.Near.Active);
            PrecipInput dense = Input(PrecipType.Snow, 1f);
            dense.VisibilityM = 30f;
            PrecipFrame full = Settle(dense, new PrecipState());
            Assert.IsTrue(full.TotalCount > 0 && full.TotalCount <= PrecipTuning.Defaults().MaxTotal, "total " + full.TotalCount);
            Assert.AreEqual(full.Near.Count + full.Mid.Count + full.Far.Count, full.TotalCount);
        }

        [TestMethod]
        public void EveryTypeHasARowAndPassesItsSanityMetric()
        {
            PrecipType[] types = PrecipTypeTable.Types;
            Assert.AreEqual(Enum.GetValues(typeof(PrecipType)).Length - 1, types.Length, "every type but None has a row");
            foreach (PrecipType type in types)
            {
                PrecipTypeLook look = PrecipTypeTable.Get(type);
                Assert.IsTrue(look.SizeScale > 0f && look.Opacity > 0f && look.Opacity <= 1f, type + " size and opacity");
                Assert.IsTrue(look.R >= 0f && look.R <= 1f && look.G >= 0f && look.G <= 1f && look.B >= 0f && look.B <= 1f, type + " colour");
                Assert.IsTrue(look.WindCoupling > 0f && look.WindCoupling <= 1f, type + " coupling");

                // No wind: the fall is straight down (gravity), nothing sideways.
                PrecipFrame still = Settle(Input(type, 0.6f), new PrecipState());
                Assert.IsTrue(still.Draw, type + " draws");
                Assert.IsTrue(still.TotalCount > 100, type + " count " + still.TotalCount);
                Assert.IsTrue(still.VelU < 0f, type + " falls");
                Assert.AreEqual(0f, still.VelE, 1e-6f, type + " has no sideways motion without wind");
                Assert.AreEqual(0f, still.VelN, 1e-6f);

                // Wind from the west: the drift follows it, in proportion to the coupling.
                PrecipFrame blown = Settle(Input(type, 0.6f, 10f), new PrecipState());
                Assert.AreEqual(10f * look.WindCoupling, blown.VelE, 1.2f * look.WindCoupling * 2f, type + " drifts with the wind");
                Assert.IsTrue(blown.VelE > 0f);

                // The luma sign is the sign of the colour against a mid grey.
                float luma = 0.2126f * look.R + 0.7152f * look.G + 0.0722f * look.B;
                Assert.AreEqual(luma >= PrecipTypeTable.MidGrey ? 1 : -1, PrecipTypeTable.LumaSign(type));
            }
            Assert.AreEqual(-1, PrecipTypeTable.LumaSign(PrecipType.Ash), "ash is dark");
            Assert.AreEqual(1, PrecipTypeTable.LumaSign(PrecipType.Snow), "snow is bright");
            // Dust and sand are carried by the wind (coupling 1) and fall slowly; rain falls fast and splashes.
            Assert.AreEqual(1f, PrecipTypeTable.Get(PrecipType.Dust).WindCoupling);
            Assert.AreEqual(1f, PrecipTypeTable.Get(PrecipType.Sand).WindCoupling);
            Assert.IsTrue(PrecipTypeTable.Get(PrecipType.Dust).FallMs < 1f);
            Assert.IsTrue(PrecipTypeTable.Get(PrecipType.Rain).FallMs > 5f);
            Assert.IsTrue(PrecipTypeTable.Get(PrecipType.Rain).Splash > 0f && PrecipTypeTable.Get(PrecipType.Snow).Splash == 0f);
            Assert.IsTrue(PrecipTypeTable.Get(PrecipType.Rain).Stretch > PrecipTypeTable.Get(PrecipType.Snow).Stretch * 10f, "rain is streaks, snow is not");
        }

        [TestMethod]
        public void SnowFallsAtTheViewsSpeedAndOtherTypesAtTheirOwn()
        {
            PrecipFrame snow = Settle(Input(PrecipType.Snow, 0.5f), new PrecipState());
            Assert.AreEqual(-1.1f, snow.VelU, 1e-5f);
            PrecipFrame rain = Settle(Input(PrecipType.Rain, 0.5f), new PrecipState());
            Assert.AreEqual(-PrecipTypeTable.Get(PrecipType.Rain).FallMs, rain.VelU, 1e-5f);
        }

        [TestMethod]
        public void SplashesAppearOnlyForDropsNearTheGround()
        {
            PrecipInput rain = Input(PrecipType.Rain, 0.6f);
            rain.GroundDistM = 30f;
            Assert.IsTrue(Settle(rain, new PrecipState()).SplashCount >= 1f);
            rain.GroundDistM = 5000f;
            Assert.AreEqual(0f, Settle(rain, new PrecipState()).SplashCount);
            PrecipInput snow = Input(PrecipType.Snow, 0.6f);
            snow.GroundDistM = 30f;
            Assert.AreEqual(0f, Settle(snow, new PrecipState()).SplashCount);
        }

        [TestMethod]
        public void TheVeilFollowsTheViewAndAGustAddsToIt()
        {
            PrecipInput clear = Input(PrecipType.Snow, 0.3f);
            Assert.AreEqual(0f, Settle(clear, new PrecipState()).VeilGround, "no veil where the table gives none");
            PrecipInput veil = Input(PrecipType.Snow, 0.8f);
            PrecipFrame calm = Settle(veil, new PrecipState());
            Assert.AreEqual(0.4f, calm.VeilGround, 1e-4f, "a calm gust adds nothing");
            veil.GustNoise = 1f;
            PrecipFrame gust = Settle(veil, new PrecipState(), 300);
            Assert.IsTrue(gust.VeilGround > calm.VeilGround, "a gust raises the veil");
            Assert.IsTrue(gust.VeilGround <= 0.97f);
            Assert.IsTrue(gust.VeilSky >= gust.VeilGround);
        }

        [TestMethod]
        public void TheGustMovesTheStateInSlewLimitedSteps()
        {
            var state = new PrecipState();
            PrecipInput input = Input(PrecipType.Snow, 0.9f);
            PrecipTuning tuning = PrecipTuning.Defaults();
            PrecipFrame frame;
            PrecipLookParams.Compute(ref input, ref tuning, state, out frame);
            float factor = frame.GustFactor, dir = frame.GustDirDeg;
            input.GustNoise = 1f;
            input.GustDirNoise = 1f;
            for (int i = 0; i < 240; i++)
            {
                PrecipLookParams.Compute(ref input, ref tuning, state, out frame);
                Assert.IsTrue(Math.Abs(frame.GustFactor - factor) <= PrecipMath.GustSlewPerS / 60f + 1e-5f, "factor jumped at frame " + i);
                Assert.IsTrue(Math.Abs(frame.GustDirDeg - dir) <= PrecipMath.GustDirectionRateDegPerS / 60f + 1e-4f, "direction jumped at frame " + i);
                Assert.IsTrue(Math.Abs(frame.GustDirDeg) <= PrecipMath.GustDirectionMaxDeg + 1e-4f);
                factor = frame.GustFactor;
                dir = frame.GustDirDeg;
            }
            Assert.AreEqual(12f, dir, 1e-3f);
            Assert.AreEqual(1f + PrecipMath.GustAmplitude(0.9f), factor, 1e-3f);
        }

        [TestMethod]
        public void TheDriftIsContinuousWhenTheWindChanges()
        {
            var state = new PrecipState();
            PrecipTuning tuning = PrecipTuning.Defaults();
            PrecipInput input = Input(PrecipType.Snow, 0.6f, 2f, 0f);
            PrecipFrame frame;
            PrecipLookParams.Compute(ref input, ref tuning, state, out frame);
            float x = frame.Mid.DriftX;
            input.WindE = 14f;
            for (int i = 0; i < 100; i++)
            {
                PrecipLookParams.Compute(ref input, ref tuning, state, out frame);
                float step = frame.Mid.DriftX - x;
                if (step < -0.5f) step += 1f;
                Assert.IsTrue(step >= 0f && step <= 14f * (1f / 60f) / 18f * 1.3f + 1e-4f, "the drift jumped by " + step + " at frame " + i);
                x = frame.Mid.DriftX;
            }
        }

        [TestMethod]
        public void ASizeFloorFewerFlakesCarryTheSameExtinction()
        {
            // A small flake far away is raised to the floor, so fewer of them carry the same share of the extinction.
            PrecipInput far = Input(PrecipType.Snow, 0.5f);
            far.Far.SizeMin = 0.005f;
            far.Far.SizeMax = 0.01f;
            far.VisibilityM = 30000f;
            PrecipTuning withFloor = PrecipTuning.Defaults();
            PrecipTuning noFloor = PrecipTuning.Defaults();
            noFloor.FloorPx = 0f;
            PrecipFrame a, b;
            PrecipLookParams.Compute(ref far, ref withFloor, new PrecipState(), out a);
            PrecipLookParams.Compute(ref far, ref noFloor, new PrecipState(), out b);
            Assert.IsTrue(a.Far.Count > 0, "some flakes remain");
            Assert.IsTrue(a.Far.Count < b.Far.Count, "the floor makes the flakes count for more: " + a.Far.Count + " vs " + b.Far.Count);
        }

        [TestMethod]
        public void TheCameraTrackIgnoresJumpsOfTheFloatingOrigin()
        {
            var track = new PrecipCameraTrack();
            // East = x, up = y, north = z in the body's own coordinates; the camera flies 100 m east in a second.
            track.Update(0, 600000, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1);
            track.Update(100, 600000, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1);
            Assert.AreEqual(100.0, track.E, 1e-9);
            Assert.AreEqual(0.0, track.U, 1e-9);
            // The frame turns with the position (the camera is now 10 degrees on): the movement is projected on the NEW axes.
            double a = 10.0 * Math.PI / 180.0;
            track.Update(100 + 50, 600000, 0, Math.Cos(a), 0, Math.Sin(a), 0, 1, 0, -Math.Sin(a), 0, Math.Cos(a));
            Assert.AreEqual(100.0 + 50.0 * Math.Cos(a), track.E, 1e-9);
            Assert.AreEqual(-50.0 * Math.Sin(a), track.N, 1e-9);
            track.Reset();
            Assert.AreEqual(0.0, track.E);
        }

        [TestMethod]
        public void TheComposeStateRunsThroughAutoSceneHostAndALostHook()
        {
            Assert.AreEqual(WeatherCompose.Scene, PrecipCompose.Resolve(WeatherCompose.Auto, true, false), "Auto needs recorded metrics for the pair");
            Assert.AreEqual(WeatherCompose.Host, PrecipCompose.Resolve(WeatherCompose.Auto, true, true));
            Assert.AreEqual(WeatherCompose.Scene, PrecipCompose.Resolve(WeatherCompose.Auto, false, true), "no hook, no Host");
            Assert.AreEqual(WeatherCompose.Scene, PrecipCompose.Resolve(WeatherCompose.Host, false, true));
            Assert.AreEqual(WeatherCompose.Host, PrecipCompose.Resolve(WeatherCompose.Host, true, false));
            Assert.AreEqual(WeatherCompose.Scene, PrecipCompose.Resolve(WeatherCompose.Scene, true, true));

            var c = new PrecipCompose();
            c.Step(WeatherCompose.Host, true, true, 1f / 60f);
            Assert.AreEqual(1f, c.HostWeight);
            Assert.AreEqual(0f, c.SceneWeight);
            // The hook disappears: Scene is whole within 0.25 s.
            float t = 0f;
            while (c.SceneWeight < 1f && t < 1f)
            {
                c.Step(WeatherCompose.Host, false, true, 1f / 60f);
                t += 1f / 60f;
            }
            Assert.IsTrue(t <= 0.25f + 1f / 60f + 1e-4f, "took " + t);
            Assert.AreEqual(0f, c.HostWeight);
            Assert.IsTrue(c.TakeLostNotice());
            Assert.IsFalse(c.TakeLostNotice(), "told once");
        }

        [TestMethod]
        public void ALiveSwitchOfTheModeNeverDoublesTheVeil()
        {
            var c = new PrecipCompose();
            c.Step(WeatherCompose.Scene, true, true, 1f / 60f);
            for (int i = 0; i < 40; i++)
            {
                c.Step(WeatherCompose.Host, true, true, 1f / 60f);
                const float alpha = 0.7f;
                float a = PrecipCompose.VeilShare(alpha, c.HostWeight), b = PrecipCompose.VeilShare(alpha, c.SceneWeight);
                float coverage = 1f - (1f - a) * (1f - b);
                Assert.AreEqual(alpha, coverage, 1e-4f, "frame " + i + " covers " + coverage);
            }
            Assert.AreEqual(1f, c.HostWeight);
            Assert.AreEqual(0f, c.SceneWeight);
            Assert.AreEqual(0.7f, PrecipCompose.VeilShare(0.7f, 1f));
            Assert.AreEqual(0f, PrecipCompose.VeilShare(0.7f, 0f));
        }

        [TestMethod]
        public void TheSheetHasAShapeForEveryCell()
        {
            byte[] pixels = PrecipSheets.Pixels();
            int size = PrecipSheets.CellSize * 2;
            Assert.AreEqual(size * size * 4, pixels.Length);
            for (int cell = 0; cell < 4; cell++)
            {
                int sum = 0, centre = 0;
                int cx = (cell % 2) * PrecipSheets.CellSize, cy = (cell / 2) * PrecipSheets.CellSize;
                for (int y = 0; y < PrecipSheets.CellSize; y++)
                    for (int x = 0; x < PrecipSheets.CellSize; x++)
                        sum += pixels[((cy + y) * size + cx + x) * 4 + 3];
                centre = pixels[((cy + PrecipSheets.CellSize / 2) * size + cx + PrecipSheets.CellSize / 2) * 4 + 3];
                Assert.IsTrue(sum > 1000, "cell " + cell + " is empty");
                Assert.IsTrue(sum < 255 * PrecipSheets.CellSize * PrecipSheets.CellSize / 2, "cell " + cell + " is a blot");
                Assert.IsTrue(centre > 0 || cell == 3 || cell == 1, "cell " + cell + " has nothing in its middle");
                // The corners are empty so that a quad has no hard edge.
                Assert.AreEqual(0, pixels[(cy * size + cx) * 4 + 3], "cell " + cell + " corner");
            }
            // The streak is brighter at its head (v > 0) than at its tail.
            Assert.IsTrue(PrecipSheets.Alpha(PrecipSheetKind.Streak, 0f, 0.6f) > PrecipSheets.Alpha(PrecipSheetKind.Streak, 0f, -0.6f));
        }

        [TestMethod]
        public void TheLegacyHandshakeFlagsFollowTheLegacyPiecesAndTheFogTracker()
        {
            Assert.IsFalse(StormVeil.LegacyDrawing);
            Assert.IsFalse(StormFlakes.LegacyDrawing);
            var legacyFog = new FogController();
            var ours = new FogController(false);
            ours.Save(false, FogMode.Linear, 0.001f, Color.gray);
            Assert.IsFalse(FogController.LegacyHolds, "the fog writer's own tracker does not count as the legacy one");
            legacyFog.Save(false, FogMode.Linear, 0.001f, Color.gray);
            Assert.IsTrue(FogController.LegacyHolds);
            legacyFog.Restore(true, FogMode.ExponentialSquared, 0.001f, Color.gray);
            Assert.IsFalse(FogController.LegacyHolds);
            ours.CountExternal();
            Assert.AreEqual(1, ours.ExternalWrites);
        }
    }
}
