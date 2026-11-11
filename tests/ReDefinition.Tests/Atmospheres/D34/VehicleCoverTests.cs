using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.D34
{
    // Package D34, vehicle cover: the amount's state machine, its side file, and the shell mesh of a part.
    [TestClass]
    public class VehicleCoverTests
    {
        private static readonly VehicleCoverModel.Rates R = new VehicleCoverModel.Rates();

        private static VehicleCoverInputs Resting(double ground, double fall = 0, double tempK = 255, bool sunlit = true)
        {
            return new VehicleCoverInputs { Ground01 = ground, Fall01 = fall, TemperatureK = tempK, Sunlit = sunlit, SpeedMs = 0, AltitudeM = 2, Heating01 = 0, Landed = true };
        }

        private static double Run(double amount, VehicleCoverInputs i, double seconds, double dt = 1.0)
        {
            float t, f;
            for (double s = 0; s < seconds; s += dt) amount = VehicleCoverModel.Step(amount, i, dt, R, out t, out f);
            return amount;
        }

        [TestMethod]
        public void ARestingVesselOnCoverGathersItInProportionAndNeverMoreThanTheGround()
        {
            double a = Run(0, Resting(0.8), 600);
            Assert.IsTrue(a > 0.1 && a < 0.8, "rises: " + a);
            double full = Run(0, Resting(0.8), 6 * 3600);
            Assert.AreEqual(0.8, full, 1e-9, "stops at the ground cover");
            Assert.AreEqual(0.0, Run(0, Resting(0.0), 3600), 1e-12, "no ground cover, no vehicle cover");
        }

        [TestMethod]
        public void SnowfallMakesItRiseFasterThanTheGroundAlone()
        {
            double quiet = Run(0, Resting(1.0), 300);
            double snowing = Run(0, Resting(1.0, fall: 1.0), 300);
            Assert.IsTrue(snowing > quiet * 3, "quiet " + quiet + ", snowing " + snowing);
        }

        [TestMethod]
        public void AWarmSunnySurfaceMeltsItAndAColdOneKeepsIt()
        {
            double held = Run(0.7, Resting(0.7, tempK: 260), 3600);
            Assert.AreEqual(0.7, held, 1e-9, "frozen: nothing changes");
            double melted = Run(0.7, Resting(0.7, tempK: 285, sunlit: true), 2 * 3600);
            Assert.AreEqual(0.0, melted, 1e-9, "10 K above freezing in the sun takes it in under an hour");
            double slower = Run(0.7, Resting(0.7, tempK: 285, sunlit: false), 1800);
            double sunny = Run(0.7, Resting(0.7, tempK: 285, sunlit: true), 1800);
            Assert.IsTrue(slower > sunny, "in the shade it lasts longer: " + slower + " against " + sunny);
            Assert.AreEqual(0.7, Run(0.7, Resting(0.7, tempK: 273.15), 3600), 1e-9, "at freezing exactly it does not melt");
        }

        [TestMethod]
        public void SpeedHeightAndHeatingShedItAndHeatingClearsAtOnce()
        {
            VehicleCoverInputs fast = Resting(0.9);
            fast.SpeedMs = 40;
            fast.Landed = false;
            Assert.AreEqual(0.0, Run(0.6, fast, 120), 1e-9, "above 20 m/s it sheds in about a minute");
            VehicleCoverInputs high = Resting(0.9);
            high.AltitudeM = 3000;
            high.Landed = false;
            Assert.IsTrue(Run(0.6, high, 30) < 0.6 && Run(0.6, high, 30) > 0.0, "above 1.5 km it falls slowly");
            Assert.AreEqual(0.0, Run(0.6, high, 200), 1e-9);
            VehicleCoverInputs hot = Resting(0.9);
            hot.Heating01 = 0.5;
            float t, f;
            Assert.AreEqual(0.0, VehicleCoverModel.Step(0.9, hot, 1.0, R, out t, out f), "a reentry glow clears it in one step");
        }

        [TestMethod]
        public void AFastVesselGathersNothingEvenOverCover()
        {
            VehicleCoverInputs fast = Resting(1.0);
            fast.SpeedMs = 25;
            float t, f;
            Assert.AreEqual(0.0, VehicleCoverModel.Step(0.0, fast, 10.0, R, out t, out f), 1e-12);
        }

        [TestMethod]
        public void TheAmountFollowsTheGroundDownSlowly()
        {
            double a = Run(0.9, Resting(0.3), 600);
            Assert.IsTrue(a < 0.9 && a > 0.3, "follows the ground down: " + a);
            Assert.AreEqual(0.3, Run(0.9, Resting(0.3), 6 * 3600), 1e-9);
        }

        [TestMethod]
        public void TheStepIgnoresBadTimesAndKeepsTheAmountInRange()
        {
            float t, f;
            Assert.AreEqual(0.5, VehicleCoverModel.Step(0.5, Resting(1), 0.0, R, out t, out f), 1e-12);
            Assert.AreEqual(0.5, VehicleCoverModel.Step(0.5, Resting(1), double.NaN, R, out t, out f), 1e-12);
            Assert.AreEqual(1.0, VehicleCoverModel.Step(7.0, Resting(1), 1.0, R, out t, out f), 1e-12, "above 1 is clamped");
            Assert.AreEqual(0.0, VehicleCoverModel.Step(-3.0, Resting(0), 1.0, R, out t, out f), 1e-12);
        }

        [TestMethod]
        public void OnLoadTheAmountIsWhatTheSituationLeftItAfterTheTimeAway()
        {
            // Rising toward 0.8 when saved at 0.2.
            var rising = new VehicleCoverStored { Amount = 0.2f, StampUt = 1000, Target = 0.8f, FallPerSecond = 0f };
            Assert.AreEqual(0.2, VehicleCoverModel.Restore(rising, 1000, R), 1e-6, "no time passed");
            double later = VehicleCoverModel.Restore(rising, 1000 + 900, R);
            Assert.IsTrue(later > 0.2 && later < 0.8, "rose: " + later);
            Assert.AreEqual(0.8, VehicleCoverModel.Restore(rising, 1000 + 1e6, R), 1e-6, "capped by the target");
            // Melting when saved.
            var melting = new VehicleCoverStored { Amount = 0.5f, StampUt = 2000, Target = 0f, FallPerSecond = 1f / 7200f };
            Assert.AreEqual(0.25, VehicleCoverModel.Restore(melting, 2000 + 1800, R), 1e-6);
            Assert.AreEqual(0.0, VehicleCoverModel.Restore(melting, 2000 + 86400, R), 1e-9);
            // Steady.
            var steady = new VehicleCoverStored { Amount = 0.4f, StampUt = 3000, Target = 0f, FallPerSecond = 0f };
            Assert.AreEqual(0.4, VehicleCoverModel.Restore(steady, 3000 + 1e5, R), 1e-6);
        }

        [TestMethod]
        public void ARevertByTwoGameDaysClearsWhatWasStampedInTheFuture()
        {
            var s = new VehicleCoverStored { Amount = 0.9f, StampUt = 1000000, Target = 0.9f, FallPerSecond = 0f };
            Assert.AreEqual(0.0, VehicleCoverModel.Restore(s, 1000000 - 2 * 6 * 3600.0, R), "the stamp lies after now");
            Assert.AreEqual(0.0, VehicleCoverModel.Restore(new VehicleCoverStored { Amount = 0.5f, StampUt = double.NaN }, 10, R));
            double a = VehicleCoverModel.Restore(new VehicleCoverStored { Amount = 7f, StampUt = 0, Target = 0f }, 10, R);
            Assert.IsTrue(a >= 0.0 && a <= 1.0, "values stay within 0..1");
        }

        [TestMethod]
        public void TheShellsAreKeptByTheSettingAndTheSituation()
        {
            Assert.IsFalse(VehicleCoverModel.Wanted(VehicleCoverModeLite.Off, true, 0));
            Assert.IsTrue(VehicleCoverModel.Wanted(VehicleCoverModeLite.Landed, true, 0));
            Assert.IsTrue(VehicleCoverModel.Wanted(VehicleCoverModeLite.Landed, false, 20), "just above the ground");
            Assert.IsFalse(VehicleCoverModel.Wanted(VehicleCoverModeLite.Landed, false, 2000));
            Assert.IsTrue(VehicleCoverModel.Wanted(VehicleCoverModeLite.Always, false, 20000));
            Assert.IsTrue(VehicleCoverModel.Visible(0.5, R));
            Assert.IsFalse(VehicleCoverModel.Visible(0.001, R));
            Assert.AreEqual((int)VehicleCoverMode.Off, (int)VehicleCoverModeLite.Off);
            Assert.AreEqual((int)VehicleCoverMode.Landed, (int)VehicleCoverModeLite.Landed);
            Assert.AreEqual((int)VehicleCoverMode.Always, (int)VehicleCoverModeLite.Always);
        }

        [TestMethod]
        public void TheSideFileRoundTripsAndSkipsADamagedLine()
        {
            var entries = new Dictionary<uint, VehicleCoverStored>
            {
                { 12345u, new VehicleCoverStored { Amount = 0.42f, StampUt = 8765432.123, Target = 0.9f, FallPerSecond = 0f } },
                { 4000000000u, new VehicleCoverStored { Amount = 1f, StampUt = 0, Target = 0f, FallPerSecond = 0.001f } }
            };
            string text = VehicleCoverFile.Serialize(entries);
            int skipped;
            Dictionary<uint, VehicleCoverStored> back = VehicleCoverFile.Parse(text, out skipped);
            Assert.AreEqual(0, skipped);
            Assert.AreEqual(2, back.Count);
            Assert.AreEqual(0.42f, back[12345u].Amount, 0f);
            Assert.AreEqual(8765432.123, back[12345u].StampUt, 0.0);
            Assert.AreEqual(0.001f, back[4000000000u].FallPerSecond, 0f);
            string damaged = text + "oops not a line\n77 0.5 notanumber 0 0\n88 0.5 100 0 0\n";
            Dictionary<uint, VehicleCoverStored> partly = VehicleCoverFile.Parse(damaged, out skipped);
            Assert.AreEqual(2, skipped);
            Assert.AreEqual(3, partly.Count, "a damaged line costs one vessel, never the file");
            Assert.AreEqual(0, VehicleCoverFile.Parse("garbage", out skipped).Count, "no header, no entries");
            Assert.AreEqual(0, VehicleCoverFile.Parse(null, out skipped).Count);
        }

        // ---- the shell mesh -----------------------------------------------------------------------------------------------------

        // A cube of side 2 with four vertices per face, normals outward, triangles clockwise seen from outside (Unity's winding).
        private static void Cube(out float[] positions, out float[] normals, out int[] indices)
        {
            var p = new List<float>();
            var n = new List<float>();
            var idx = new List<int>();
            float[][] faces =
            {
                new float[] { 0, 1, 0 }, new float[] { 0, -1, 0 }, new float[] { 1, 0, 0 }, new float[] { -1, 0, 0 }, new float[] { 0, 0, 1 }, new float[] { 0, 0, -1 }
            };
            foreach (float[] f in faces)
            {
                // Two tangents.
                float[] t1 = f[1] != 0 ? new float[] { 1, 0, 0 } : new float[] { 0, 1, 0 };
                float[] t2 = { f[1] * t1[2] - f[2] * t1[1], f[2] * t1[0] - f[0] * t1[2], f[0] * t1[1] - f[1] * t1[0] };
                int b = p.Count / 3;
                float[][] corners = { new float[] { -1, -1 }, new float[] { -1, 1 }, new float[] { 1, 1 }, new float[] { 1, -1 } };
                foreach (float[] c in corners)
                {
                    for (int k = 0; k < 3; k++) p.Add(f[k] + t1[k] * c[0] + t2[k] * c[1]);
                    n.AddRange(f);
                }
                // Choose the order so that the face normal (b - a) x (c - a) points along f.
                float[] a = { p[b * 3], p[b * 3 + 1], p[b * 3 + 2] };
                float[] bb = { p[(b + 1) * 3], p[(b + 1) * 3 + 1], p[(b + 1) * 3 + 2] };
                float[] cc = { p[(b + 2) * 3], p[(b + 2) * 3 + 1], p[(b + 2) * 3 + 2] };
                float[] u = { bb[0] - a[0], bb[1] - a[1], bb[2] - a[2] }, v = { cc[0] - a[0], cc[1] - a[1], cc[2] - a[2] };
                float[] cross = { u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0] };
                bool along = cross[0] * f[0] + cross[1] * f[1] + cross[2] * f[2] > 0;
                if (along) idx.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                else idx.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
            }
            positions = p.ToArray();
            normals = n.ToArray();
            indices = idx.ToArray();
        }

        [TestMethod]
        public void TheShellKeepsTheFacesThatCanLookUpAndNotTheOnesThatLookDown()
        {
            float[] p, n;
            int[] i;
            Cube(out p, out n, out i);
            ShellMesh.Result roof = ShellMesh.Select(p, n, i, 0, 1, 0, 0.5f, 0.02f);
            Assert.AreEqual(2, roof.TriangleCount, "only the top");
            Assert.AreEqual(4, roof.VertexCount);
            for (int v = 0; v < roof.VertexCount; v++) Assert.AreEqual(1.02f, roof.Positions[v * 3 + 1], 1e-5f, "inflated along the normal");
            ShellMesh.Result withSides = ShellMesh.Select(p, n, i, 0, 1, 0, -0.25f, 0.02f);
            Assert.AreEqual(10, withSides.TriangleCount, "top and the four sides, not the bottom");
            ShellMesh.Result tilted = ShellMesh.Select(p, n, i, 0.7071f, 0.7071f, 0, 0.5f, 0.0f);
            Assert.AreEqual(4, tilted.TriangleCount, "a cube tilted by 45 degrees shows its top and one side to the sky");
            Assert.IsNull(ShellMesh.Select(p, n, i, 0, 0, 0, 0.99f, 0.02f), "an up of zero length keeps nothing");
        }

        [TestMethod]
        public void TheShellOfBadInputIsNullAndDegenerateTrianglesAreSkipped()
        {
            Assert.IsNull(ShellMesh.Select(null, null, null, 0, 1, 0, 0, 0));
            Assert.IsNull(ShellMesh.Select(new float[9], new float[6], new[] { 0, 1, 2 }, 0, 1, 0, 0, 0), "normals do not match");
            Assert.IsNull(ShellMesh.Select(new float[9], new float[9], new[] { 0, 1, 2 }, 0, 1, 0, -1, 0), "a triangle of no area");
            Assert.IsNull(ShellMesh.Select(new float[9], new float[9], new[] { 0, 1, 7 }, 0, 1, 0, -1, 0), "an index out of range");
        }

        [TestMethod]
        public void AShellIsBuiltAnewOnlyWhenTheVesselTurnedFarAndSharedByOrientation()
        {
            Assert.IsFalse(ShellMesh.Turned(0, 1, 0, 0, 1, 0));
            Assert.IsFalse(ShellMesh.Turned(0, 1, 0, 0.5f, 0.866f, 0));
            Assert.IsTrue(ShellMesh.Turned(0, 1, 0, 0.9f, 0.436f, 0), "64 degrees");
            long a = ShellMesh.CacheKey(42, 0, 1, 0);
            Assert.AreEqual(a, ShellMesh.CacheKey(42, 0.05f, 0.99f, 0.0f), "nearly the same up shares the shell");
            Assert.AreNotEqual(a, ShellMesh.CacheKey(42, 1, 0, 0));
            Assert.AreNotEqual(a, ShellMesh.CacheKey(43, 0, 1, 0));
        }
    }
}
