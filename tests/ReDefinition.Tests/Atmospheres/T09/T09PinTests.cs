using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.B09;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The pinned capture (master plan phase 0 item 3; T09ProbePin.cs, OwnCloudModule.Pin.cs, LayerPassSettings.cs): the frame origin, the settings the game hands the weather
    // player (every field, set back without loss), the keys pass.json gains, the hash and the .npy files.
    [TestClass]
    public class T09PinTests
    {
        [TestMethod]
        public void TheFrameNumberIsTheFrameCountWithTheDefaultOriginAndCountsFromAnOriginOtherwise()
        {
            foreach (int count in new[] { 0, 1, 7819, int.MaxValue }) Assert.AreEqual(count, OwnCloudPlan.FrameNumber(count, 0), "origin 0 leaves the frame count as it was");
            Assert.AreEqual(0, OwnCloudPlan.FrameNumber(5000, 5000), "the origin's frame is frame 0");
            Assert.AreEqual(63, OwnCloudPlan.FrameNumber(5063, 5000), "the 64th frame from the origin");
            Assert.AreEqual(0, OwnCloudPlan.FrameNumber(4990, 5000), "a count before the origin is 0, never negative");
        }

        [TestMethod]
        public void TheModulesFrameOriginIsZeroByDefault()
        {
            Assert.AreEqual(0, new OwnCloudModule().FrameOrigin);
        }

        private static void Change(LayerFrame f, ReconSettings r, NoiseSwitches n)
        {
            f.MinStepM = 6f;
            f.MaxSteps = 256;
            f.SunMarchM = 12000f;
            f.StepGrowth = 0f;
            f.EarlyOut = 0.004f;
            f.LightStride = 1;
            f.FrameNumber = 63;
            f.PlanetCenter = new Vector3(-139644.22f, 1018.3f, 583762.8f);
            // A rotation about z by 30 degrees written out (Unity's Quaternion.Euler is an engine call the tests cannot make).
            var m = Matrix4x4.identity;
            m.m00 = 0.8660254f; m.m01 = -0.5f; m.m10 = 0.5f; m.m11 = 0.8660254f;
            f.WorldToMap = m;
            f.Light.SunStepsOverride = 8;
            f.Light.Lights[0] = new CloudLight { Towards = new Vector3(0.1f, 0.2f, 0.97f), Colour = new Color(0.01f, 0.02f, 0.03f, 1f) };
            f.Light.LayerM[2] = new Vector4(1f, 2f, 3f, 4f);
            f.Far.InscatterNear = new Color(0.4f, 0.5f, 0.6f);
            f.Far.MinSamples = 3;
            f.Cull.StepGrowth = true;
            f.Cull.StepGrowthPerM = 2e-4f;
            f.Cull.CapDistanceM = float.PositiveInfinity;
            r.Mode = ReconMode.Block;
            r.Block = 2;
            r.Rotation = true;
            r.FreshWeight = 0.2f;
            r.StaleWeight = 0f;
            r.Accumulate = 0.2f;
            r.Resolution = 1f;
            r.CatmullHistory = true;
            r.Edge = null;
            n.Seed = 7u;
            n.SizeLevel = 2;
        }

        private static List<LayerPassSetting> FromJson(string json)
        {
            var list = new List<LayerPassSetting>();
            foreach (object o in (List<object>)MiniJson.Parse(json))
            {
                var d = (Dictionary<string, object>)o;
                list.Add(new LayerPassSetting { path = (string)d["path"], values = ((List<object>)d["values"]).Cast<string>().ToArray() });
            }
            return list;
        }

        private static string Flat(IEnumerable<LayerPassSetting> list)
        {
            return string.Join("\n", list.Select(s => s.path + "=" + string.Join(",", s.values)).ToArray());
        }

        [TestMethod]
        public void TheSettingsGoThroughJsonAndBackOntoFreshObjectsWithoutLoss()
        {
            double softness = CloudSoftDensity.Softness, mean = CloudSoftDensity.DetailMean;
            try
            {
                var f = new LayerFrame();
                var r = new ReconSettings();
                var n = new NoiseSwitches();
                Change(f, r, n);
                CloudSoftDensity.Softness = 0.7;
                LayerPassSettings.Result game = LayerPassSettings.Collect(f, r, n);
                string json = LayerPassSettings.Json(game.Settings);
                List<LayerPassSetting> read = FromJson(json);
                Assert.AreEqual(Flat(game.Settings), Flat(read), "the JSON holds every setting as it was");

                CloudSoftDensity.Softness = 0.5;
                var f2 = new LayerFrame();
                var r2 = new ReconSettings { Edge = new EdgeSettings() };
                var n2 = new NoiseSwitches();
                List<string> problems = LayerPassSettings.Apply(read, f2, r2, n2, null);
                Assert.AreEqual(0, problems.Count, string.Join("\n", problems.ToArray()));
                Assert.AreEqual(Flat(game.Settings), Flat(LayerPassSettings.Collect(f2, r2, n2).Settings), "the replay's objects give the same settings back");
                Assert.AreEqual(6f, f2.MinStepM);
                Assert.AreEqual(8, f2.Light.SunStepsOverride);
                Assert.AreEqual(0.02f, f2.Light.Lights[0].Colour.g);
                Assert.AreEqual(new Color(0.4f, 0.5f, 0.6f), f2.Far.InscatterNear.Value);
                Assert.IsTrue(float.IsPositiveInfinity(f2.Cull.CapDistanceM), "an infinity passes through the text");
                Assert.IsNull(r2.Edge, "a reference the game held as null is null in the replay");
                Assert.AreEqual(ReconMode.Block, r2.Mode);
                Assert.AreEqual(7u, n2.Seed);
                Assert.AreEqual(0.7, CloudSoftDensity.Softness, 1e-12);
            }
            finally
            {
                CloudSoftDensity.Softness = softness;
                CloudSoftDensity.DetailMean = mean;
            }
        }

        [TestMethod]
        public void TheSettingsNameEveryMarchAndTemporalNumberTheTaskAsksFor()
        {
            var paths = new HashSet<string>(LayerPassSettings.Collect(new LayerFrame(), new ReconSettings(), new NoiseSwitches()).Settings.Select(s => s.path));
            foreach (string p in new[]
                     {
                         "Frame.MinStepM", "Frame.MaxSteps", "Frame.SunSteps", "Frame.Light.SunStepsOverride", "Frame.SunMarchM", "Frame.LightStride", "Frame.Resolution", "Frame.EarlyOut",
                         "Frame.StepGrowth", "Frame.TransmittanceGrowth", "Frame.Cull.StepGrowth", "Frame.Cull.StepGrowthPerM", "Frame.FrameNumber", "Frame.PlanetCenter.x",
                         "Frame.WorldToMap.m01", "Frame.Light.Lights[1].Colour.b", "Recon.Mode", "Recon.Resolution", "Recon.Accumulate", "Recon.Block", "Recon.FreshWeight",
                         "Recon.StaleWeight", "Recon.ClipSigma", "Recon.Rotation", "Recon.DriftSeconds", "Noise.Seed", "Soft.Softness", "Soft.DetailMean"
                     })
                Assert.IsTrue(paths.Contains(p), p + " is carried");
            Assert.IsFalse(paths.Any(p => p == "Frame.Light.Cache" || p.StartsWith("Frame.Light.Cache.", StringComparison.Ordinal)), "the light cache is a GPU resource, not a setting");
            LayerPassSettings.Result r = LayerPassSettings.Collect(new LayerFrame(), new ReconSettings(), new NoiseSwitches());
            CollectionAssert.Contains(r.Skipped, "Frame.Light.Cache (LightCache, null)", "the skipped reference is named with its state");
            Assert.AreEqual(0, r.NotDefault.Count, "a cache that is off (null) is at its default: " + string.Join(", ", r.NotDefault.ToArray()));
        }

        [TestMethod]
        public void ApplyNamesWhatItCannotSetAndLeavesTheSkippedPaths()
        {
            var f = new LayerFrame();
            var list = new List<LayerPassSetting>
            {
                new LayerPassSetting { path = "Frame.NoSuchField", values = new[] { "1" } },
                new LayerPassSetting { path = "Elsewhere.MaxSteps", values = new[] { "1" } },
                new LayerPassSetting { path = "Frame.MaxSteps", values = new[] { "many" } },
                new LayerPassSetting { path = "Frame.FrameNumber", values = new[] { "77" } },
                new LayerPassSetting { path = "Frame.PlanetCenter.y", values = new[] { "5" } }
            };
            List<string> problems = LayerPassSettings.Apply(list, f, new ReconSettings(), new NoiseSwitches(), new[] { "Frame.FrameNumber", "Frame.PlanetCenter." });
            Assert.AreEqual(3, problems.Count, string.Join("\n", problems.ToArray()));
            Assert.AreEqual(0, f.FrameNumber, "a skipped path is not set");
            Assert.AreEqual(0f, f.PlanetCenter.y, "a skipped prefix is not set");
        }

        [TestMethod]
        public void PassJsonGainsThePinTheTuningTheMarchTheSettingsAndTheHash()
        {
            var f = new LayerFrame();
            var r = new ReconSettings();
            Change(f, r, new NoiseSwitches());
            var p = new OwnCloudModule.PinState
            {
                FrameCount = 5063, FrameOrigin = 5000, FrameNumber = 63, FramesSinceCut = 64, BlockIndex = 64, CutCount = 11,
                CameraWorld = new Vector3(1.5f, -2f, 3f), CameraRotation = new Quaternion(0f, 0.5f, 0f, 0.8660254f), PlanetCenterWorld = new Vector3(-139644.22f, 1018.3f, 583762.8f),
                CameraFov = 60f, CameraWidth = 1707, CameraHeight = 960
            };
            var tuning = new List<KeyValuePair<string, float>> { new KeyValuePair<string, float>("cloud.motionScale", 0f), new KeyValuePair<string, float>("layers.minStepM", 6f) };
            string block = OwnCloudModule.PinJson(p, tuning, new[] { "cloud.motionScale" }, LayerPassSettings.Collect(f, r, new NoiseSwitches()),
                                                  LayerPassSettings.MarchJson(f, r), "0123456789abcdef");
            Assert.IsTrue(block.StartsWith(",\n", StringComparison.Ordinal), "the block continues pass.json's object");
            var d = (Dictionary<string, object>)MiniJson.Parse("{" + block.Substring(1) + "}");
            var pin = (Dictionary<string, object>)d["pin"];
            Assert.AreEqual(5000.0, pin["frameOrigin"]);
            Assert.AreEqual(63.0, pin["frameNumber"]);
            Assert.AreEqual(64.0, pin["framesSinceCut"]);
            Assert.AreEqual(0.0, pin["firstFrameNumber"], "the replay starts at the cut's frame number");
            Assert.AreEqual(64.0, pin["blockIndex"]);
            var world = (Dictionary<string, object>)pin["world"];
            Assert.AreEqual(-139644.22f, (float)(double)((List<object>)world["planetCenter"])[0]);
            Assert.AreEqual(1707.0, world["width"]);
            var keys = (Dictionary<string, object>)d["tuning"];
            Assert.AreEqual(0.0, keys["cloud.motionScale"]);
            Assert.AreEqual(6.0, keys["layers.minStepM"]);
            Assert.AreEqual("cloud.motionScale", ((List<object>)d["tuningOverrides"]).Single());
            var march = (Dictionary<string, object>)d["march"];
            foreach (string k in new[] { "minStepM", "gridPoints", "sunSteps", "sunMarchM", "lightStride", "resolution", "accumulate", "blockSide", "freshWeight", "staleWeight", "earlyOut", "stepGrowth",
                                         "transmittanceGrowth", "cullStepGrowthPerM", "rotation" })
                Assert.IsTrue(march.ContainsKey(k), "march names " + k);
            Assert.AreEqual(6.0, march["minStepM"]);
            Assert.AreEqual(8.0, march["sunSteps"], "the light's override is the taps the march takes");
            Assert.AreEqual(1.0, march["rotation"]);
            Assert.IsTrue(((List<object>)d["settings"]).Count > 100, "every field is in the settings");
            Assert.AreEqual("0123456789abcdef", d["typeTableHash"]);
        }

        [TestMethod]
        public void TheHashIsFnv1a64()
        {
            // The published test vectors of FNV-1a 64: the empty input is the offset basis, "a" is af63dc4c8601ec8c.
            Assert.AreEqual("cbf29ce484222325", LayerPassSettings.Hex(LayerPassSettings.Fnv1a64(new byte[0])));
            Assert.AreEqual("af63dc4c8601ec8c", LayerPassSettings.Hex(LayerPassSettings.Fnv1a64(Encoding.ASCII.GetBytes("a"))));
            ulong whole = LayerPassSettings.Fnv1a64(Encoding.ASCII.GetBytes("ab"));
            Assert.AreEqual(whole, LayerPassSettings.Fnv1a64(Encoding.ASCII.GetBytes("b"), LayerPassSettings.Fnv1a64(Encoding.ASCII.GetBytes("a"))), "chained parts hash as the whole");
        }

        [TestMethod]
        public void TheNpyFileHasTheFormatsHeaderAndTheValues()
        {
            string path = Path.Combine(Path.GetTempPath(), "redefinition-pin-" + Guid.NewGuid().ToString("N") + ".npy");
            try
            {
                float[] data = { 0f, 1f, -2.5f, float.MaxValue, 0.25f, 3f };
                PinFiles.WriteFloat32(path, 1, 3, 2, data);
                byte[] bytes = File.ReadAllBytes(path);
                Assert.AreEqual(0x93, bytes[0]);
                Assert.AreEqual("NUMPY", Encoding.ASCII.GetString(bytes, 1, 5));
                int headerLength = bytes[8] | (bytes[9] << 8);
                Assert.AreEqual(0, (10 + headerLength) % 64, "the data starts at a multiple of 64");
                string header = Encoding.ASCII.GetString(bytes, 10, headerLength);
                StringAssert.Contains(header, "'descr': '<f4'");
                StringAssert.Contains(header, "'shape': (1, 3, 2)");
                Assert.AreEqual(10 + headerLength + data.Length * 4, bytes.Length);
                Assert.AreEqual(-2.5f, BitConverter.ToSingle(bytes, 10 + headerLength + 8));
                PinFiles.WriteFloat32(path, 2, 3, 1, data);
                StringAssert.Contains(Encoding.ASCII.GetString(File.ReadAllBytes(path), 10, headerLength), "'shape': (2, 3)");
                Assert.ThrowsException<ArgumentException>(() => PinFiles.WriteFloat32(path, 2, 2, 2, data));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [TestMethod]
        public void TheTuningSnapshotNamesEveryKeyInForceAndTheOverrides()
        {
            var table = new TuningTable();
            table.SetOverride("layers.minStepM", 6f);
            table.SetOverride("cloud.motionScale", 0f);
            table.BeginFrame(1);
            HashSet<string> overridden;
            List<KeyValuePair<string, float>> keys = table.Snapshot(out overridden);
            CollectionAssert.AreEqual(new[] { "cloud.motionScale", "layers.minStepM" }, keys.Select(k => k.Key).ToArray(), "sorted by key");
            Assert.AreEqual(6f, keys[1].Value);
            Assert.IsTrue(overridden.SetEquals(new[] { "cloud.motionScale", "layers.minStepM" }));
        }
    }

    public partial class T09WalkThroughTests
    {
        private Run StartPin(string ut)
        {
            return Start(new[] { "plain" }, x =>
            {
                var args = x.Probe.CommandLineOverride.ToList();
                args.Add("-t09Pin");
                if (ut != null)
                {
                    args.Add("-t09Ut");
                    args.Add(ut);
                }
                x.Probe.CommandLineOverride = args.ToArray();
            }, "1707", "perfNpic2560dlssqpin");
        }

        [TestMethod]
        public void PinWithoutAUniversalTimeFailsNamed()
        {
            Run r = StartPin(null);
            Drive(r);
            AssertNamedFailure(r, "T09.pin.clock: -t09Pin needs -t09Ut <seconds>");
            Assert.AreEqual(0, r.Log.Count(l => l.StartsWith("T09 view begin")), "no timing window");
        }

        [TestMethod]
        public void PinPinsTheClockAndFailsNamedWhereThePassCannotBeCaptured()
        {
            // The fake world's module has no renderer: every step up to it is done and said, the module's step fails the start by name, and no timing window runs.
            Run r = StartPin("1018800");
            Drive(r);
            AssertNamedFailure(r, "T09.pin.module");
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("Probe: check T09.pin.placed: ok")), Dump(r));
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("Probe: check T09.pin.clock: ok (ut 1018800.000 against the pinned 1018800.000)")), "the clock was pinned at the asked time\n" + Dump(r));
            Assert.IsTrue(double.IsNaN(r.World.PinnedUt), "the clock runs again after the capture");
            Assert.AreEqual(0, r.World.Module.FrameOrigin, "the frame origin is 0 again after a failed capture");
            Assert.AreEqual(0, r.Log.Count(l => l.StartsWith("T09 view begin")), "no timing window");
        }
    }
}
