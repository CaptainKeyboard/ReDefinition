using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres.Audio;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.D33
{
    // The loader fallbacks, the distant storm's geometry, the probe meter and the files the package ships or reads.
    [TestClass]
    public class LibraryCellMeterTests
    {
        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "ReDefinition.sln"))) dir = Path.GetDirectoryName(dir);
            Assert.IsNotNull(dir, "repository root");
            return dir;
        }

        // --- Loader ---

        private static Func<string, bool> Exists(HashSet<string> present)
        {
            return p => present.Contains(p);
        }

        private static ClipLibrary<string> Load(HashSet<string> present, List<string> log, Func<string, string> get = null)
        {
            var lib = new ClipLibrary<string>(log.Add);
            lib.Load(Exists(present), get ?? (p => p));
            return lib;
        }

        private static HashSet<string> All()
        {
            var set = new HashSet<string>();
            foreach (string n in ClipLibrary<string>.SnowSoft) set.Add(ClipLibrary<string>.Folder + n);
            foreach (string n in ClipLibrary<string>.SnowMedium) set.Add(ClipLibrary<string>.Folder + n);
            foreach (string n in ClipLibrary<string>.SnowHard) set.Add(ClipLibrary<string>.Folder + n);
            return set;
        }

        [TestMethod]
        public void AllTwelveClipsLoadAndEachClassPicksItsOwn()
        {
            var log = new List<string>();
            ClipLibrary<string> lib = Load(All(), log);
            Assert.AreEqual(12, lib.Count);
            Assert.IsTrue(lib.Any);
            Assert.AreEqual(0, log.Count);
            Assert.IsTrue(lib.Pick(0, 0).EndsWith("steps_snow_01"));
            Assert.IsTrue(lib.Pick(1, 5).EndsWith("steps_snow_06"));
            Assert.IsTrue(lib.Pick(2, 3).EndsWith("steps_snow_12"));
            Assert.IsTrue(lib.Pick(2, 4).EndsWith("steps_snow_09"), "the pick index wraps");
        }

        [TestMethod]
        public void AMissingFileIsSkippedAndLoggedOnce()
        {
            HashSet<string> present = All();
            present.Remove(ClipLibrary<string>.Folder + "steps_snow_03");
            var log = new List<string>();
            var lib = new ClipLibrary<string>(log.Add);
            lib.Load(Exists(present), p => p);
            lib.Load(Exists(present), p => p);
            Assert.AreEqual(11, lib.Count);
            Assert.AreEqual(1, log.Count, "logged once across two loads");
            StringAssert.Contains(log[0], "steps_snow_03");
        }

        [TestMethod]
        public void AClassWithoutClipsBorrowsTheNearestAndNoClipAtAllDisablesTheLayer()
        {
            HashSet<string> present = All();
            foreach (string n in ClipLibrary<string>.SnowMedium) present.Remove(ClipLibrary<string>.Folder + n);
            var log = new List<string>();
            ClipLibrary<string> lib = Load(present, log);
            Assert.IsNotNull(lib.Pick(1, 0));
            Assert.AreEqual(4, log.Count);
            ClipLibrary<string> none = Load(new HashSet<string>(), new List<string>());
            Assert.IsFalse(none.Any);
            Assert.IsNull(none.Pick(0, 0));
        }

        [TestMethod]
        public void AThrowingLookupDisablesThatClipOnly()
        {
            var log = new List<string>();
            var lib = new ClipLibrary<string>(log.Add);
            lib.Load(Exists(All()), p => { if (p.EndsWith("_07")) throw new InvalidOperationException("bad"); return p; });
            Assert.AreEqual(11, lib.Count);
            Assert.AreEqual(1, log.Count);
            var lib2 = new ClipLibrary<string>(log.Add);
            lib2.Load(p => { throw new InvalidOperationException(); }, p => p);
            Assert.IsFalse(lib2.Any);
        }

        [TestMethod]
        public void ABodysOwnFootstepClipJoinsThroughAddSingle()
        {
            var lib = new ClipLibrary<string>(s => { });
            lib.Load(p => false, p => p);
            Assert.IsFalse(lib.Any);
            Assert.IsTrue(lib.AddSingle(1, "Mod/Sounds/sand_step", p => true, p => p));
            Assert.AreEqual("Mod/Sounds/sand_step", lib.Pick(2, 9));
            Assert.IsFalse(lib.AddSingle(1, "", p => true, p => p));
            Assert.IsFalse(lib.AddSingle(7, "x", p => true, p => p));
        }

        [TestMethod]
        public void TheShippedFilesTheManifestAndThePatchAgree()
        {
            string root = RepoRoot();
            string sounds = Path.Combine(root, "GameData", "ReDefinition", "Atmospheres", "Sounds");
            string manifest = File.ReadAllText(Path.Combine(sounds, "SOUNDS.json"));
            var names = new List<string>();
            names.AddRange(ClipLibrary<string>.SnowSoft);
            names.AddRange(ClipLibrary<string>.SnowMedium);
            names.AddRange(ClipLibrary<string>.SnowHard);
            string patch = File.ReadAllText(Path.Combine(root, "GameData", "ReDefinition", "Atmospheres", "Audio", "RocketSoundEnhancementPatch.cfg"));
            foreach (string name in names)
            {
                Assert.IsTrue(File.Exists(Path.Combine(sounds, name + ".ogg")), name + ".ogg");
                StringAssert.Contains(manifest, "\"" + name + ".ogg\"");
                StringAssert.Contains(patch, ClipLibrary<string>.Folder + name + " = Exterior");
            }
            Assert.AreEqual(12, Regex.Matches(patch, @"= Exterior").Count);
            StringAssert.Contains(patch, ":NEEDS[RocketSoundEnhancement]");
        }

        [TestMethod]
        public void TheDecodedFootstepsFitTheMemoryBudget()
        {
            string manifest = File.ReadAllText(Path.Combine(RepoRoot(), "GameData", "ReDefinition", "Atmospheres", "Sounds", "SOUNDS.json"));
            long bytes = 0;
            foreach (Match m in Regex.Matches(manifest, "\"decodedBytes\": (\\d+)")) bytes += long.Parse(m.Groups[1].Value);
            Assert.IsTrue(bytes > 0 && bytes <= ClipLibrary<string>.BudgetBytes / 4, "decoded " + bytes);
        }

        [TestMethod]
        public void TheSeriesRowsAreWellFormedAndOwnedByThePackage()
        {
            string json = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "series", "rows", "D33.json"));
            StringAssert.Contains(json, "\"package\": \"D33\"");
            var metrics = new HashSet<string>();
            foreach (Match m in Regex.Matches(json, "\"metric\": \"(D33\\.[A-Za-z0-9_.]+)\""))
            {
                Assert.IsTrue(metrics.Add(m.Groups[1].Value), "duplicate " + m.Groups[1].Value);
            }
            Assert.IsTrue(metrics.Count >= 45);
            Assert.AreEqual(Regex.Matches(json, "\"series\": \"K-W3a\"").Count, Regex.Matches(json, "\"metric\":").Count);
            // Every metric the probe emits has a row.
            string probe = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Atmospheres", "Audio", "Runtime", "Probe", "D33Probe.cs"));
            foreach (Match m in Regex.Matches(probe, "Emit\\(\"(D33\\.[A-Za-z0-9_.]+)\","))
            {
                Assert.IsTrue(metrics.Contains(m.Groups[1].Value), "no row for " + m.Groups[1].Value);
            }
        }

        // --- The distant storm ---

        [TestMethod]
        public void TheDistanceCurveIsSilentFarFullNearAndStrictlyRisingBetween()
        {
            Assert.AreEqual(0f, DistantCellModel.Closeness(40000));
            Assert.AreEqual(0f, DistantCellModel.Closeness(30000));
            Assert.AreEqual(1f, DistantCellModel.Closeness(3000));
            Assert.AreEqual(1f, DistantCellModel.Closeness(0));
            float previous = 0f;
            for (double d = 29000; d >= 3200; d -= 200)
            {
                float c = DistantCellModel.Closeness(d);
                Assert.IsTrue(c > previous, d + " m");
                previous = c;
            }
            Assert.AreEqual(0.5f, DistantCellModel.Closeness(Math.Sqrt(30000.0 * 3000.0)), 1e-3f, "the geometric middle");
        }

        [TestMethod]
        public void TheGreatCircleDistanceAndTheTrackInterpolation()
        {
            Assert.AreEqual(Math.PI * 600000.0 / 2, DistantCellModel.DistanceM(0, 0, 0, 90, 600000), 1.0);
            Assert.AreEqual(0.0, DistantCellModel.DistanceM(10, 20, 10, 20, 600000), 1e-6);
            var track = new ForecastTrack
            {
                Ut = new[] { 0.0, 100.0 },
                Lat = new[] { 0.0, 10.0 },
                Lon = new[] { 170.0, -170.0 },
                RadiusM = new[] { 1000f, 3000f },
                Strength01 = new[] { 0.4f, 0.8f }
            };
            double lat, lon, radius;
            float strength;
            Assert.IsTrue(DistantCellModel.TrackAt(track, 50, out lat, out lon, out radius, out strength));
            Assert.AreEqual(5.0, lat, 1e-9);
            Assert.AreEqual(180.0, Math.Abs(lon), 1e-9, "across the date line");
            Assert.AreEqual(2000.0, radius, 1e-6);
            Assert.AreEqual(0.6f, strength, 1e-6f);
            Assert.IsTrue(DistantCellModel.TrackAt(track, -5, out lat, out lon, out radius, out strength));
            Assert.AreEqual(0.0, lat, 1e-9);
            Assert.IsFalse(DistantCellModel.TrackAt(new ForecastTrack(), 0, out lat, out lon, out radius, out strength));
            Assert.IsFalse(DistantCellModel.TrackAt(null, 0, out lat, out lon, out radius, out strength));
        }

        [TestMethod]
        public void TheNearestSystemIsChosenByTheDistanceToItsEdge()
        {
            const double radius = 600000.0;
            ForecastTrack Make(double lat, double lon, float r, float strength)
            {
                return new ForecastTrack { Ut = new[] { 0.0 }, Lat = new[] { lat }, Lon = new[] { lon }, RadiusM = new[] { r }, Strength01 = new[] { strength } };
            }
            // 0.5 deg is 5.2 km on this body; a big system 1 deg away has its edge nearer than a small one 0.6 deg away.
            ForecastTrack[] tracks = { Make(0, 0.6, 500f, 1f), Make(0, 1.0, 9000f, 1f), Make(0, 0.2, 100f, 0.05f) };
            CellPlace place = DistantCellModel.Nearest(tracks, 0, 0, 0, radius);
            Assert.IsTrue(place.Found);
            Assert.AreEqual(1.0, place.LonDeg, 1e-9);
            Assert.AreEqual(Math.PI * radius / 180.0 - 9000.0, place.EdgeDistanceM, 1.0);
            Assert.IsFalse(DistantCellModel.Nearest(new[] { Make(0, 0.2, 100f, 0.05f) }, 0, 0, 0, radius).Found, "too weak to be heard");
            Assert.IsFalse(DistantCellModel.Nearest(null, 0, 0, 0, radius).Found);
        }

        [TestMethod]
        public void ThePanFollowsTheDirectionOnTheGround()
        {
            Vector3 up = new Vector3(0, 1, 0), right = new Vector3(1, 0, 0);
            Assert.AreEqual(1f, DistantCellModel.Pan(new Vector3(10, 0, 0), right, up), 1e-5f);
            Assert.AreEqual(-1f, DistantCellModel.Pan(new Vector3(-10, 0, 0), right, up), 1e-5f);
            Assert.AreEqual(0f, DistantCellModel.Pan(new Vector3(0, 0, 10), right, up), 1e-5f);
            Assert.AreEqual(0f, DistantCellModel.Pan(new Vector3(0, 500, 0), right, up), 1e-5f, "straight up has no side");
            Assert.AreEqual(0.7071f, DistantCellModel.Pan(new Vector3(10, 300, 10), right, up), 1e-3f, "the height is dropped");
        }

        // --- The probe meter ---

        private static MeterSummary Feed(ProbeMeter meter, Func<int, float> left, Func<int, float> right, int frames)
        {
            var buf = new float[2048];
            for (int done = 0; done < frames; done += 1024)
            {
                for (int i = 0; i < 1024; i++)
                {
                    buf[i * 2] = left(done + i);
                    buf[i * 2 + 1] = right(done + i);
                }
                meter.Feed(buf, 2);
            }
            return meter.Summarise(0, meter.WindowCount);
        }

        [TestMethod]
        public void TheMeterReadsLevelPeakShareAndBands()
        {
            var meter = new ProbeMeter(48000, 100);
            MeterSummary s = Feed(meter, n => (float)(0.5 * Math.Sin(2 * Math.PI * 100 * n / 48000.0)), n => 0f, 48000);
            Assert.IsTrue(meter.WindowCount >= 9);
            Assert.AreEqual(10 * Math.Log10(0.5 * 0.125), s.RmsDb, 0.5, "mean of the channels' energy: (0.125 + 0) / 2");
            Assert.AreEqual(0.5, s.PeakAbs, 0.01);
            Assert.IsTrue(s.LeftShare > 0.99);
            Assert.IsTrue(s.Lo > s.Mid * 20 && s.Lo > s.Hi * 1000, "a 100 Hz tone is in the low band");
            Assert.IsTrue(s.CentroidHz < 200);

            var high = new ProbeMeter(48000, 100);
            MeterSummary h = Feed(high, n => 0f, n => (float)(0.3 * Math.Sin(2 * Math.PI * 3500 * n / 48000.0)), 48000);
            Assert.IsTrue(h.Hi > h.Lo * 100 && h.Hi > h.Mid * 5, "hi " + h.Hi + " mid " + h.Mid);
            Assert.IsTrue(h.CentroidHz > 2500, "centroid " + h.CentroidHz);
            var above = new ProbeMeter(48000, 100);
            MeterSummary a = Feed(above, n => 0f, n => (float)(0.3 * Math.Sin(2 * Math.PI * 12000 * n / 48000.0)), 48000);
            Assert.IsTrue(a.Hi < h.Hi * 0.1, "the high band ends at 5 kHz");
            Assert.IsTrue(h.LeftShare < 0.01);

            var mid = new ProbeMeter(48000, 100);
            MeterSummary m = Feed(mid, n => (float)(0.3 * Math.Sin(2 * Math.PI * 800 * n / 48000.0)), n => (float)(0.3 * Math.Sin(2 * Math.PI * 800 * n / 48000.0)), 48000);
            Assert.IsTrue(m.Mid > m.Lo * 5 && m.Mid > m.Hi * 20);
            Assert.AreEqual(0.5, m.LeftShare, 0.01);
        }

        [TestMethod]
        public void TheMeterCountsNonFiniteAndClippedSamplesAndReportsFailures()
        {
            var meter = new ProbeMeter(48000, 100);
            Feed(meter, n => n == 100 ? float.NaN : 0.1f, n => 0.1f, 8192);
            Assert.AreEqual(1, meter.NonFiniteSamples);
            StringAssert.Contains(meter.FailureReason(false, 0), "non-finite");
            var clip = new ProbeMeter(48000, 100);
            Feed(clip, n => n == 5 ? 1.0f : 0.1f, n => 0.1f, 4096);
            Assert.AreEqual(1, clip.ClippedSamples);
            StringAssert.Contains(clip.FailureReason(false, 0), "clipped");
            var clean = new ProbeMeter(48000, 100);
            Feed(clean, n => 0.2f, n => 0.2f, 8192);
            Assert.IsNull(clean.FailureReason(false, 0));
            Assert.IsNotNull(clean.FailureReason(true, -120), "silence although the level is above 0.3");
            Assert.IsNull(clean.FailureReason(true, -30));
        }

        [TestMethod]
        public void TheMeterKeepsItsCapacity()
        {
            var meter = new ProbeMeter(48000, 3);
            Feed(meter, n => 0.1f, n => 0.1f, 48000);
            Assert.AreEqual(3, meter.WindowCount);
            Assert.AreEqual(0.0, meter.WindowEnergy(9));
            Assert.AreEqual(-120.0, ProbeMeter.Db(0), 0);
        }
    }
}
