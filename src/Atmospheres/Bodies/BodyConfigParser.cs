using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReDefinition.Atmospheres
{
    // What the parser made of one body: the climate, what it could not read (warnings, never exceptions), and the one-line summary.
    internal sealed class BodyParseResult
    {
        internal BodyParseResult(BodyClimate climate, IReadOnlyList<string> warnings, string summary)
        {
            Climate = climate;
            Warnings = warnings;
            Summary = summary;
        }

        internal BodyClimate Climate { get; }
        internal IReadOnlyList<string> Warnings { get; }
        internal string Summary { get; }
    }

    // Turns a REDEFINITION_BODY node (or none) and the facts about the body into its BodyClimate. Pure: it reads no game object, so it
    // runs in tests with ConfigNode stubs. A key or sub-node that cannot be read is a warning and keeps the generic default (not the
    // numbers of any body); the whole parse never throws: a failure inside gives Broken(reason). The node arrives as ModuleManager
    // left it, so a patch such as @REDEFINITION_BODY[Name] { @TEMPERATURE { @waterCelsius = -5 } } has changed it before this runs;
    // the parser has no merge logic of its own.
    internal static class BodyConfigParser
    {
        internal const string NodeName = "REDEFINITION_BODY";

        private static readonly HashSet<string> KnownNodes = new HashSet<string>
        {
            "SEASONS", "TEMPERATURE", "PRECIP", "COVER", "ICE", "CELLS", "SIM", "CLOUDS", "LOOK", "SOUND", "WIND", "GAMEPLAY", "AURORA",
            "LIGHT_MEDIUM", "FROST", "FORECAST"
        };

        // The tilt (degrees) between the axis and the reference plane's normal above which an add-on counts as having tilted the body.
        internal const double AxisTiltDetectDegrees = 0.5;

        internal static BodyParseResult Parse(BodyFacts facts, ConfigNode node, BodyPlayerOptions player, int globalSeed)
        {
            var warnings = new List<string>();
            BodyFacts f = facts ?? new BodyFacts();
            int seed = BodyClimate.SeedOf(f.Name, globalSeed);
            try
            {
                return ParseInner(f, node, player ?? BodyPlayerOptions.Default, seed, warnings);
            }
            catch (Exception e)
            {
                warnings.Add("the configuration of " + f.Name + " could not be read: " + e.GetType().Name + ": " + e.Message);
                BodyClimate broken = BodyClimate.Generic(f.Name, f.Index, f.RadiusM, 0.0, f.DaySeconds, BodyStatus.Broken("exception"), seed);
                return new BodyParseResult(broken, warnings, Summarise(broken, "none", "exception", warnings.Count));
            }
        }

        private static BodyParseResult ParseInner(BodyFacts f, ConfigNode node, BodyPlayerOptions player, int seed, List<string> warnings)
        {
            var root = new BodyNodeReader(node, NodeName + "[" + f.Name + "]", warnings);
            foreach (string name in root.NodeNames())
            {
                if (!KnownNodes.Contains(name)) root.Warn("", "the sub-node " + name + " is not known and is ignored");
            }

            // Seasons: the year, the tilt, the clock.
            BodyNodeReader s = root.Child("SEASONS");
            YearSourceKind source = s.Has("yearSource") ? s.Enum("yearSource", BodyYear.DefaultSource(f)) : BodyYear.DefaultSource(f);
            double yearSecondsKey = s.Double("yearSeconds", 0.0, 0.0, 1.0e12);
            double nodeFactor = s.Double("yearFactor", 1.0, BodyPlayerOptions.MinYearFactor, BodyPlayerOptions.MaxYearFactor);
            double cap = s.Double("yearCap", BodyYear.DefaultCapHomeYears, 0.0, 1000.0);
            bool capped;
            double year = BodyYear.Compute(f, source, yearSecondsKey, cap, nodeFactor * player.YearFactor, out capped);
            SeasonClockKind clock = s.Enum("clock", f.TidallyLocked ? SeasonClockKind.Constant : SeasonClockKind.Orbit);
            if (clock == SeasonClockKind.Orbit && !(year > 0))
            {
                s.Warn("clock", "the body has no year (no orbit period); the clock is Constant");
                clock = SeasonClockKind.Constant;
            }
            string tiltNote;
            double tilt = ResolveTilt(s, f, out tiltNote);
            bool eccentric = s.Bool("eccentricitySeason", false);
            var seasons = new SeasonRules(clock, s.Double("phase", 0.0, 0.0, 1.0), source, yearSecondsKey, nodeFactor * player.YearFactor, tilt,
                s.Double("hemispherePhaseOffset", SeasonClock.DefaultHemisphereOffset, 0.0, 1.0), eccentric);

            // Temperature, precipitation and cover, with the guess for what the node leaves out.
            TemperatureRules temperature = BodyRuleReaders.Temperature(root.Child("TEMPERATURE"), f.TemperatureTarget);
            List<PrecipTypeRule> guessedTypes = BodyGuess.Precip(f, temperature.WaterCelsius);
            PrecipRules precip = BodyRuleReaders.Precip(root.Child("PRECIP"), guessedTypes);
            List<CoverRules> covers = BodyRuleReaders.Covers(root, BodyGuess.Covers(f, precip.Types));

            // The status: the node's choice or the probes'.
            bool accept = root.Bool("acceptOutsideEnvelope", false);
            BodyStatus status = ResolveStatus(root, f, accept, warnings);

            var climate = new BodyClimate(f.Name, f.Index, f.RadiusM, year, f.DaySeconds, status, seed, seasons, temperature, precip, covers,
                BodyRuleReaders.Ice(root.Child("ICE")), BodyRuleReaders.Cells(root.Child("CELLS")), BodyRuleReaders.Sim(root.Child("SIM")),
                BodyRuleReaders.Clouds(root.Child("CLOUDS")), BodyRuleReaders.Look(root.Child("LOOK")), BodyRuleReaders.Sound(root.Child("SOUND")),
                BodyRuleReaders.Wind(root.Child("WIND")), BodyRuleReaders.Gameplay(root.Child("GAMEPLAY")), BodyRuleReaders.Aurora(root.Child("AURORA")),
                BodyRuleReaders.LightMedium(root.Child("LIGHT_MEDIUM")), BodyRuleReaders.Frost(root.Child("FROST")), BodyRuleReaders.Forecast(root.Child("FORECAST")));
            string yearNote = source + (capped ? ", capped" : "") + ", factor " + (nodeFactor * player.YearFactor).ToString("0.##", CultureInfo.InvariantCulture);
            return new BodyParseResult(climate, warnings, Summarise(climate, tiltNote, yearNote, warnings.Count));
        }

        // The status the node asks for (key status: Auto, Off, CoverOnly, Full; key enabled) against what the probes found.
        private static BodyStatus ResolveStatus(BodyNodeReader root, BodyFacts f, bool acceptOutside, List<string> warnings)
        {
            if (!root.Bool("enabled", true)) return BodyStatus.Off("Disabled");
            string asked = root.String("status", "Auto");
            BodyStatusKind kind;
            bool auto = asked.Equals("Auto", StringComparison.OrdinalIgnoreCase);
            if (auto) kind = BodyStatusKind.Full;
            else if (!Enum.TryParse(asked, true, out kind) || kind == BodyStatusKind.Broken)
            {
                root.Warn("status", "'" + asked + "' is not Auto, Off, CoverOnly or Full; Auto is used");
                auto = true;
                kind = BodyStatusKind.Full;
            }
            if (kind == BodyStatusKind.Off) return BodyStatus.Off("Disabled");
            if (f.IsStar) return BodyStatus.Off("Star");
            if (!f.HasAtmosphere)
            {
                if (!auto) root.Warn("status", "the body has no atmosphere; it stays Off");
                return BodyStatus.Off("NoAtmosphere");
            }
            if (!f.HasSurface && !f.HasOcean) return BodyStatus.Off("NoSurface");
            if (!acceptOutside)
            {
                string violation = BodyEnvelope.Violation(f);
                if (violation != null)
                {
                    warnings.Add("the body is outside the tested envelope (" + violation + "); set acceptOutsideEnvelope = true to run it anyway");
                    return BodyStatus.Broken("envelope");
                }
            }
            return kind == BodyStatusKind.CoverOnly ? BodyStatus.CoverOnly : BodyStatus.Full;
        }

        // The tilt of the body: None is 0, a number is that number, Auto (also a missing key or a bad one) reads the axis the game reports.
        // The note says which, for the summary line.
        internal static double ResolveTilt(BodyNodeReader s, BodyFacts f, out string note)
        {
            string text = s.Raw("tilt");
            if (text != null && text.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                note = "none";
                return 0.0;
            }
            if (text != null && !text.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                double given = s.Double("tilt", double.NaN, -180.0, 180.0);
                if (!double.IsNaN(given))
                {
                    note = given.ToString("0.##", CultureInfo.InvariantCulture) + " configured";
                    return given;
                }
            }
            return AutoTilt(f, out note);
        }

        // Auto: an add-on that tilts a body moves its rotation axis away from the normal of the reference plane; the tilt is then the
        // angle between the axis and the normal of the planet's orbit. Without such an axis the body is untilted.
        internal static double AutoTilt(BodyFacts f, out string note)
        {
            if (double.IsNaN(f.AxisFromReferenceDeg) || double.IsNaN(f.ObliquityDeg))
            {
                note = "0 auto (axis not readable)";
                return 0.0;
            }
            if (f.AxisFromReferenceDeg > AxisTiltDetectDegrees)
            {
                note = f.ObliquityDeg.ToString("0.##", CultureInfo.InvariantCulture) + " auto (axis " + f.AxisFromReferenceDeg.ToString("0.##", CultureInfo.InvariantCulture)
                    + " deg off the reference plane's normal)";
                return f.ObliquityDeg;
            }
            note = "0 auto (axis on the reference plane's normal)";
            return 0.0;
        }

        private static string Summarise(BodyClimate c, string tiltNote, string yearNote, int warningCount)
        {
            string types = c.Precip.Types.Count == 0 ? "by temperature" : string.Join("/", c.Precip.Types.Select(t => t.Id));
            string covers = c.Covers.Count == 0 ? "none" : string.Join("/", c.Covers.Select(x => x.Id));
            return "body " + c.Name + ": " + c.Status + ", year " + c.YearSeconds.ToString("0", CultureInfo.InvariantCulture) + " s (" + yearNote + "), clock " + c.Seasons.Clock
                + ", tilt " + tiltNote + ", precipitation " + types + ", cover " + covers + ", seed " + c.Seed.ToString(CultureInfo.InvariantCulture)
                + ", " + warningCount + " warning" + (warningCount == 1 ? "" : "s");
        }
    }
}
