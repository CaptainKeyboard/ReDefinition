using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.B13
{
    // The year, the tilt, the envelope, the body checklist and the no-body-name rule.
    [TestClass]
    public class BodyClimateModelTests
    {
        [TestMethod]
        public void AFarPlanetsYearIsItsOrbitPeriodInUnitsOfTheHomeYear()
        {
            BodyClimate c = BodyTestSupport.Run(BodyTestSupport.Planet("Far", BodyTestSupport.DunaYear)).Climate;
            Assert.AreEqual(1.88, c.YearSeconds / BodyTestSupport.HomeYear, 0.005);
            Assert.AreEqual(YearSourceKind.OrbitPeriod, c.Seasons.YearSource);
        }

        [TestMethod]
        public void AMoonFollowsItsPlanetsYear()
        {
            BodyFacts planet = BodyTestSupport.Planet("Giant", BodyTestSupport.OuterYear, atmosphere: true, surface: false);
            BodyFacts moon = BodyTestSupport.Moon("Wet", BodyTestSupport.OuterYear, true, true, true);
            BodyClimate m = BodyTestSupport.Run(moon).Climate;
            BodyClimate p = BodyTestSupport.Run(planet).Climate;
            Assert.AreEqual(YearSourceKind.ParentOrbitPeriod, m.Seasons.YearSource);
            Assert.AreEqual(p.YearSeconds, m.YearSeconds, 1e-6, "the moon has the planet's season year");
            // Without the cap the moon's year is the planet's orbit period.
            BodyClimate uncapped = BodyTestSupport.Run(moon, "REDEFINITION_BODY { name = Wet ; SEASONS { yearCap = 0 } }").Climate;
            Assert.AreEqual(BodyTestSupport.OuterYear, uncapped.YearSeconds, 1e-3);
            // An explicit source overrides the default.
            BodyClimate own = BodyTestSupport.Run(moon, "REDEFINITION_BODY { name = Wet ; SEASONS { yearSource = OrbitPeriod ; yearCap = 0 } }").Climate;
            Assert.AreEqual(500000.0, own.YearSeconds, 1e-6);
        }

        [TestMethod]
        public void AVeryLongYearIsCutToThreeHomeYearsUnlessTheCapIsChanged()
        {
            BodyFacts far = BodyTestSupport.Planet("Far", BodyTestSupport.HomeYear * 10);
            Assert.AreEqual(BodyTestSupport.HomeYear * 3, BodyTestSupport.Run(far).Climate.YearSeconds, 1e-3);
            Assert.AreEqual(BodyTestSupport.HomeYear * 10, BodyTestSupport.Run(far, "REDEFINITION_BODY { name = Far ; SEASONS { yearCap = 0 } }").Climate.YearSeconds, 1e-3);
            Assert.AreEqual(BodyTestSupport.HomeYear * 5, BodyTestSupport.Run(far, "REDEFINITION_BODY { name = Far ; SEASONS { yearCap = 5 } }").Climate.YearSeconds, 1e-3);
            // The factor multiplies the capped year.
            Assert.AreEqual(BodyTestSupport.HomeYear * 6, BodyTestSupport.Run(far, "REDEFINITION_BODY { name = Far ; SEASONS { yearFactor = 2 } }").Climate.YearSeconds, 1e-3);
            // An explicit number of seconds is not capped.
            Assert.AreEqual(5.0e8, BodyTestSupport.Run(far, "REDEFINITION_BODY { name = Far ; SEASONS { yearSource = Seconds ; yearSeconds = 5e8 } }").Climate.YearSeconds, 1e-3);
            // A game without a home planet cannot cap.
            far.HomeOrbitPeriod = 0;
            Assert.AreEqual(BodyTestSupport.HomeYear * 10, BodyTestSupport.Run(far).Climate.YearSeconds, 1e-3);
        }

        [TestMethod]
        public void ABodyWithoutAnOrbitHasNoClock()
        {
            BodyFacts f = BodyTestSupport.Planet("Lone", 0.0);
            BodyParseResult r = BodyTestSupport.Run(f);
            Assert.AreEqual(SeasonClockKind.Constant, r.Climate.Seasons.Clock);
            Assert.AreEqual(0.0, r.Climate.YearSeconds);
            Assert.IsTrue(r.Warnings.Count >= 1);
            BodyFacts locked = BodyTestSupport.Planet("Locked", 1e7);
            locked.TidallyLocked = true;
            Assert.AreEqual(SeasonClockKind.Constant, BodyTestSupport.Run(locked).Climate.Seasons.Clock);
        }

        [TestMethod]
        public void TiltZeroAndTiltTwentyFiveGiveDifferentHemisphereOffsetsAndSunLatitudes()
        {
            // Property over a sweep of phases and tilts: an untilted eccentric body has no offset between the hemispheres, a tilted one
            // always half a year; the overhead sun stays on the equator for the first and reaches the tilt for the second.
            for (double phase = 0.0; phase < 1.0; phase += 0.05)
            {
                var untilted = new SeasonModel(SeasonClockKind.Orbit, 0, 1e7, 0.0, 0.5, true);
                var tilted = new SeasonModel(SeasonClockKind.Orbit, 0, 1e7, 25.0, 0.5, true);
                double northU = SeasonClock.HemispherePhase(phase, true, untilted);
                double southU = SeasonClock.HemispherePhase(phase, false, untilted);
                double northT = SeasonClock.HemispherePhase(phase, true, tilted);
                double southT = SeasonClock.HemispherePhase(phase, false, tilted);
                Assert.AreEqual(0.0, SeasonClock.PhaseDistance(northU, southU), 1e-12);
                Assert.AreEqual(0.5, SeasonClock.PhaseDistance(northT, southT), 1e-12);
                Assert.AreEqual(0.0, untilted.SubsolarLatitudeDegrees(phase), 1e-12);
                Assert.IsTrue(Math.Abs(tilted.SubsolarLatitudeDegrees(phase)) <= 25.0 + 1e-9);
            }
            for (double tilt = 1; tilt < 90; tilt += 4)
            {
                var m = new SeasonModel(SeasonClockKind.Orbit, 0, 1e7, tilt, 0.1, false);
                Assert.AreEqual(0.5, m.EffectiveHemisphereOffset, 1e-12, "tilt " + tilt);
                Assert.AreEqual(Math.Sin(tilt * Math.PI / 180.0), m.Amplitude, 1e-12);
            }
            Assert.AreEqual(1.0, new SeasonModel(SeasonClockKind.Orbit, 0, 1e7, 0, 0.5, false).Amplitude, "the artificial season is full strength");
            Assert.AreEqual(0.0, new SeasonModel(SeasonClockKind.None, 0, 1e7, 40, 0.5, false).Amplitude);
        }

        [TestMethod]
        public void AutoTiltReadsTheAxisAndTheConfiguredTiltWins()
        {
            BodyFacts untilted = BodyTestSupport.Planet("A", 1e7);
            untilted.AxisFromReferenceDeg = 0.0;
            untilted.ObliquityDeg = 2.1;
            Assert.AreEqual(0.0, BodyTestSupport.Run(untilted).Climate.Seasons.TiltDegrees, "the orbit's inclination is not a tilt");
            BodyFacts tilted = BodyTestSupport.Planet("B", 1e7);
            tilted.AxisFromReferenceDeg = 23.0;
            tilted.ObliquityDeg = 25.0;
            BodyParseResult r = BodyTestSupport.Run(tilted);
            Assert.AreEqual(25.0, r.Climate.Seasons.TiltDegrees);
            StringAssert.Contains(r.Summary, "25 auto");
            Assert.AreEqual(10.0, BodyTestSupport.Run(tilted, "REDEFINITION_BODY { name = B ; SEASONS { tilt = 10 } }").Climate.Seasons.TiltDegrees);
            Assert.AreEqual(0.0, BodyTestSupport.Run(tilted, "REDEFINITION_BODY { name = B ; SEASONS { tilt = None } }").Climate.Seasons.TiltDegrees);
            Assert.AreEqual(0.0, BodyTestSupport.Run(BodyTestSupport.Planet("C", 1e7)).Climate.Seasons.TiltDegrees, "an axis that could not be read is no tilt");
        }

        [TestMethod]
        public void FiveExtremeParameterSetsAreBrokenByTheEnvelope()
        {
            var cases = new List<Action<BodyFacts>>
            {
                f => f.RadiusM = 6.4e6,
                f => f.RadiusM = 1e4,
                f => f.AtmosphereDepthM = 2000.0,
                f => f.SeaLevelPressureKPa = 1e6,
                f => f.DaySeconds = 2.0
            };
            for (int i = 0; i < cases.Count; i++)
            {
                BodyFacts f = BodyTestSupport.Planet("Extreme", 1e7);
                cases[i](f);
                BodyParseResult r = BodyTestSupport.Run(f);
                Assert.AreEqual(BodyStatusKind.Broken, r.Climate.Status.Kind, "case " + i);
                Assert.AreEqual("envelope", r.Climate.Status.Reason);
                Assert.IsTrue(r.Warnings.Any(w => w.Contains("envelope")), "case " + i);
                BodyParseResult accepted = BodyTestSupport.Run(f, "REDEFINITION_BODY { name = Extreme ; acceptOutsideEnvelope = true }");
                Assert.AreEqual(BodyStatusKind.Full, accepted.Climate.Status.Kind, "case " + i + " accepted");
            }
            Assert.IsNull(BodyEnvelope.Violation(BodyTestSupport.Planet("Fine", 1e7)));
            Assert.IsNull(BodyEnvelope.Violation(BodyTestSupport.Planet("Airless", 1e7, atmosphere: false)), "a body without an atmosphere is not checked");
        }

        // The body checklist of the brief on synthetic descriptions: name, facts, expected status, expected precipitation kinds, expected covers.
        [TestMethod]
        public void TheBodyChecklistTable()
        {
            var rows = new List<string>();
            var failures = new List<string>();

            foreach (string name in new[] { "Moho", "Gilly", "Mun", "Minmus", "Ike", "Dres", "Bop", "Pol", "Vall", "Tylo", "Eeloo" })
            {
                BodyFacts f = BodyTestSupport.Moon(name, BodyTestSupport.OuterYear, atmosphere: false);
                Check(rows, failures, name, BodyTestSupport.Run(f).Climate, "Off(NoAtmosphere)", "", "");
            }

            BodyFacts hot = BodyTestSupport.Planet("Hothouse", 5.6e6);
            hot.SeaLevelTemperatureK = 750.0;
            Check(rows, failures, "Hothouse (clouds only)", BodyTestSupport.Run(hot).Climate, "Full", "none", "");

            BodyFacts home = BodyTestSupport.Planet("Home", BodyTestSupport.HomeYear);
            home.EveFields = new List<string> { "rain-Home", "snow-Home" };
            Check(rows, failures, "Home", BodyTestSupport.Run(home).Climate, "Full", "snow,rain", "snow");

            BodyFacts dry = BodyTestSupport.Planet("Dry", BodyTestSupport.DunaYear);
            dry.SeaLevelTemperatureK = 200.0;
            dry.EveFields = new List<string> { "dust-Dry" };
            BodyClimate dryClimate = BodyTestSupport.Run(dry).Climate;
            Check(rows, failures, "Dry (dust)", dryClimate, "Full", "dust", "dust");
            Assert.AreEqual("dust-Dry", dryClimate.Precip.Types[0].Field);

            BodyFacts wet = BodyTestSupport.Moon("Wet", BodyTestSupport.OuterYear, true, true, true);
            wet.EveFields = new List<string> { "snow-Wet" };
            BodyClimate wetClimate = BodyTestSupport.Run(wet).Climate;
            Check(rows, failures, "Wet (rain/snow by temperature)", wetClimate, "Full", "snow,rain", "snow");
            Assert.IsTrue(wetClimate.Covers[0].NoWater, "the ocean takes no cover");
            Assert.AreEqual(BodyTestSupport.Run(BodyTestSupport.Planet("Giant", BodyTestSupport.OuterYear, true, false)).Climate.YearSeconds, wetClimate.YearSeconds, 1e-6, "the moon's year is the planet's");

            BodyFacts giant = BodyTestSupport.Planet("Giant", BodyTestSupport.OuterYear, atmosphere: true, surface: false);
            Check(rows, failures, "Giant", BodyTestSupport.Run(giant).Climate, "Off(NoSurface)", "", "");

            BodyFacts pack = BodyTestSupport.Planet("PackWorld", 3.1e7);
            Check(rows, failures, "PackWorld (atmosphere and surface, no node)", BodyTestSupport.Run(pack).Climate, "Full", "", "");

            Console.WriteLine("body                                         expected             got");
            foreach (string row in rows) Console.WriteLine(row);
            Assert.AreEqual(0, failures.Count, string.Join("\n", failures));
        }

        private static void Check(List<string> rows, List<string> failures, string label, BodyClimate c, string status, string precip, string covers)
        {
            string gotPrecip = string.Join(",", c.Precip.Types.Select(t => t.Kind == PrecipType.None ? "none" : t.Id));
            string gotCovers = string.Join(",", c.Covers.Select(x => x.Id));
            rows.Add(label.PadRight(44) + " " + status.PadRight(20) + " " + c.Status + " / " + gotPrecip + " / " + gotCovers);
            if (c.Status.ToString() != status) failures.Add(label + ": status " + c.Status + ", expected " + status);
            if (gotPrecip != precip) failures.Add(label + ": precipitation '" + gotPrecip + "', expected '" + precip + "'");
            if (gotCovers != covers) failures.Add(label + ": covers '" + gotCovers + "', expected '" + covers + "'");
        }

        private static readonly Regex BodyNames = new Regex("Kerbin|600000");

        // Files of the whole src/Atmospheres folder that still name a body or a Kerbin number today. The gate of package E40 (the same
        // test over every file) shrinks this list to empty after W3: each package that removes its use removes its entry, and an entry
        // whose file is clean fails the test so that the list cannot go stale.
        private static readonly string[] AllowList =
        {
            "Bodies/Schema/BodyClimate.cs", "Bodies/Schema/ClimateRules.cs", "Bodies/SeasonClockObsolete.cs", "EveCellLayer.cs", "EveCoverageMath.cs",
            "EveLayerStorm.cs", "EveRainLink.cs", "EveSnowLink.cs", "EveSwirlMath.cs", "GroundRebuilder.cs", "Infrastructure/Bridge/Legacy/LegacyBridge.cs",
            "OrbitSnow.cs", "SnowNoise.cs", "SnowPqsMod.cs", "SnowQuads.cs", "SnowRefresher.cs", "StormCells.cs", "StormEffect.cs", "StormFlakes.cs", "StormModel.cs"
        };

        private static string AtmospheresFolder()
        {
            return Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src", "Atmospheres");
        }

        private static IEnumerable<string> SourceFiles(string folder)
        {
            return Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
        }

        private static string Relative(string root, string file)
        {
            return file.Substring(root.Length + 1).Replace('\\', '/');
        }

        [TestMethod]
        public void NoSourceFileOfTheBodyPackageNamesABodyOrAHomeNumber()
        {
            string root = AtmospheresFolder();
            var offenders = new List<string>();
            foreach (string file in SourceFiles(Path.Combine(root, "Bodies")))
            {
                string rel = Relative(root, file);
                if (rel.StartsWith("Bodies/Schema/") || rel == "Bodies/SeasonClockObsolete.cs") continue;
                if (BodyNames.IsMatch(File.ReadAllText(file))) offenders.Add(rel);
            }
            Assert.AreEqual(0, offenders.Count, "names a body or a body's number: " + string.Join(", ", offenders));
            Assert.IsTrue(SourceFiles(Path.Combine(root, "Bodies", "Terrain")).Count() >= 3, "the terrain helpers are in Bodies/Terrain");
        }

        [TestMethod]
        public void TheWholeAtmospheresFolderNamesNoBodyBeyondTheAllowList()
        {
            string root = AtmospheresFolder();
            var offenders = new List<string>();
            var clean = new List<string>(AllowList);
            foreach (string file in SourceFiles(root))
            {
                string rel = Relative(root, file);
                bool names = BodyNames.IsMatch(File.ReadAllText(file));
                if (names && !AllowList.Contains(rel)) offenders.Add(rel);
                if (names) clean.Remove(rel);
            }
            Assert.AreEqual(0, offenders.Count, "new body names outside the allow-list: " + string.Join(", ", offenders));
            Assert.AreEqual(0, clean.Count, "allow-list entries that are clean now (remove them): " + string.Join(", ", clean));
        }
    }
}
