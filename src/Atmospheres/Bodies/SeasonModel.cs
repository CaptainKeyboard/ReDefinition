using System;

namespace ReDefinition.Atmospheres
{
    // The season phase of one body as data: what runs the clock, how long the year is, the axial tilt and the offset between the
    // hemispheres. Built by the body provider from the body's SEASONS node and the probes; immutable. The phase it gives is an input of
    // the insolation of the weather field (SubsolarLatitudeDegrees, Amplitude), not a climate of its own.
    internal sealed class SeasonModel
    {
        // The tilt (degrees) below which a body counts as untilted.
        internal const double UntiltedDegrees = 0.05;

        internal static readonly SeasonModel None = new SeasonModel(SeasonClockKind.None, 0.0, 0.0, 0.0, 0.0, false);

        internal SeasonModel(SeasonClockKind clock, double fixedPhase, double yearSeconds, double tiltDegrees, double hemisphereOffset, bool eccentric)
        {
            Clock = clock;
            FixedPhase = SeasonClock.Wrap(fixedPhase);
            YearSeconds = yearSeconds > 0 && !double.IsInfinity(yearSeconds) ? yearSeconds : 0.0;
            TiltDegrees = double.IsNaN(tiltDegrees) || double.IsInfinity(tiltDegrees) ? 0.0 : tiltDegrees;
            HemisphereOffset = SeasonClock.Wrap(hemisphereOffset);
            Eccentric = eccentric;
        }

        internal SeasonClockKind Clock { get; }
        internal double FixedPhase { get; }
        // The length of the season year in seconds of game time; 0 where there is none.
        internal double YearSeconds { get; }
        // The axial tilt in degrees (obliquity), 0 where the body is untilted or the tilt is unknown.
        internal double TiltDegrees { get; }
        // The configured offset of the southern year as a share of the year.
        internal double HemisphereOffset { get; }
        // Whether the season follows the distance to the star (an eccentric orbit) and not the tilt.
        internal bool Eccentric { get; }

        // Whether the axis is tilted against the orbit.
        internal bool Tilted
        {
            get { return Math.Abs(TiltDegrees) > UntiltedDegrees; }
        }

        // The offset the southern year really has. A tilted body is half a year apart by geometry whatever is configured; an untilted
        // body whose season is the distance to the star has one global year (no offset); any other untilted body has the artificial
        // season of its configuration.
        internal double EffectiveHemisphereOffset
        {
            get
            {
                if (Tilted) return SeasonClock.DefaultHemisphereOffset;
                return Eccentric ? 0.0 : HemisphereOffset;
            }
        }

        // The strength of the seasons from 0 (none) to 1: the sine of the tilt, so that the poles of a body tilted by 90 degrees have
        // the full swing of day and night. An untilted body has the artificial season at full strength where its clock runs.
        internal double Amplitude
        {
            get
            {
                if (Clock == SeasonClockKind.None || Clock == SeasonClockKind.Constant) return 0.0;
                if (!Tilted) return 1.0;
                return Math.Abs(Math.Sin(TiltDegrees * Math.PI / 180.0));
            }
        }

        // The latitude of the overhead sun at a phase, in degrees: the tilt times the cosine of the year, with the northern midwinter at
        // phase 0. An untilted body has the sun over the equator all year.
        internal double SubsolarLatitudeDegrees(double phase)
        {
            if (!Tilted) return 0.0;
            return -TiltDegrees * Math.Cos(2.0 * Math.PI * SeasonClock.Wrap(phase));
        }

        // The same model with the year multiplied by a factor (the player's global year length). A factor that is not a positive finite number counts as 1.
        internal SeasonModel WithYearFactor(double factor)
        {
            return new SeasonModel(Clock, FixedPhase, SeasonClock.YearSeconds(YearSeconds, factor), TiltDegrees, HemisphereOffset, Eccentric);
        }

        // The model of a SEASONS rule with the year of the body's climate (BodyClimate.YearSeconds: source, factors and cap already
        // applied). A rule whose tilt is still Auto (NaN) takes the tilt given here.
        internal static SeasonModel From(SeasonRules rules, double yearSeconds, double tiltDegrees)
        {
            SeasonRules r = rules ?? SeasonRules.Default;
            double tilt = double.IsNaN(r.TiltDegrees) ? tiltDegrees : r.TiltDegrees;
            return new SeasonModel(r.Clock, r.FixedPhase, yearSeconds, tilt, r.HemisphereOffset, r.EccentricSeason);
        }
    }
}
