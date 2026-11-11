using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // The numbers of the engine's last run for a site that the report itself does not carry: the first time the user's alert level is
    // reached, and the time it is left again (ForecastResult).
    internal struct ForecastDetail
    {
        internal bool Known;
        internal double ThresholdEtaUt;
        internal double ClearUt;
        internal bool ActiveAtStart;

        internal static ForecastDetail None
        {
            get { return new ForecastDetail { Known = false, ThresholdEtaUt = double.NaN, ClearUt = double.NaN }; }
        }
    }

    // What the window shows depends on these; the game fills them, a test fills them by hand.
    internal sealed class ForecastViewInput
    {
        internal bool Enabled = true;
        internal bool HasBody = true;
        internal bool Vacuum;
        internal FieldReadiness Readiness = FieldReadiness.Ready;
        internal ForecastReport Report;
        internal ForecastDetail Detail = ForecastDetail.None;
        internal LaunchAssessment Launch;
        internal double NowUt;
        internal ForecastSite Site;
        internal string BodyName = "";
        internal double BodyRadiusM;
        // Whether the launch conditions apply to the site (the pad, the runway, the Space Centre).
        internal bool LaunchSite;
        // The first step of the strip's page, and how many cells a page holds.
        internal int StripFirst;
        internal int StripCells = 24;
        // The air temperature of the weather field at the site at a time, in kelvin; NaN where it is not known. The step of a report does
        // not carry it, so the window asks the field.
        internal Func<double, float> FieldTemperatureK;
        // A game time as a date for the player ("Y1, D12, 04:00"); null writes the seconds.
        internal Func<double, string> DateText;
        // The horizon the player set, in hours (for "No storm within 24 h").
        internal double HorizonHours = 24.0;
    }

    // One cell of the hourly strip.
    internal struct StripCell
    {
        internal string Text;
        internal string Lead;
        internal string BackHex;
        internal string TextHex;
        internal PrecipBand Band;
        internal float Alpha;
    }

    internal enum ForecastState
    {
        Off = 0,
        NoBody = 1,
        Vacuum = 2,
        Computing = 3,
        Approximate = 4,
        Broken = 5,
        Ready = 6
    }

    // The window's content as plain strings and cells: state, site line, the next storm with its ETA, the launch conditions, the strip and
    // the table. Built from a ForecastViewInput by ForecastView.Build; the window draws it and decides nothing. Pure.
    internal sealed class ForecastView
    {
        internal const int LaunchReasonLines = 3;
        internal static readonly string[] Columns =
        {
            ForecastStrings.ColWhen, ForecastStrings.ColLevel, ForecastStrings.ColType, ForecastStrings.ColVisibility, ForecastStrings.ColWind,
            ForecastStrings.ColGust, ForecastStrings.ColTemperature
        };

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal ForecastState State;
        internal string StateText = "";
        internal string SiteLine = "";
        internal string NextStormLine = "";
        internal string ArrivalLine = "";
        internal string ClearLine = "";
        internal string LaunchHeadline = "";
        internal string[] LaunchReasons = new string[LaunchReasonLines];
        internal string WindowLine = "";
        internal string WindLine = "";
        internal string IssuedLine = "";
        internal StripCell[] Strip = new StripCell[0];
        internal int StripPages = 1;
        internal int StripPage;
        // One array of Columns.Length strings per step of the report.
        internal string[][] Rows = new string[0][];

        // Whether there is anything to show beyond the state text.
        internal bool HasData
        {
            get { return State == ForecastState.Ready; }
        }

        internal static ForecastView Build(ForecastViewInput input)
        {
            var view = new ForecastView();
            for (int i = 0; i < LaunchReasonLines; i++) view.LaunchReasons[i] = " ";
            ForecastReport report = input.Report;
            bool available = report != null && report.Available && report.Steps.Length > 0;
            view.StateText = ForecastFormat.StateText(input.Enabled, input.HasBody, input.Readiness, available);
            if (!input.Enabled) view.State = ForecastState.Off;
            else if (!input.HasBody) view.State = ForecastState.NoBody;
            else if (input.Vacuum) view.State = ForecastState.Vacuum;
            else if (input.Readiness == FieldReadiness.Broken) view.State = ForecastState.Broken;
            else if (input.Readiness != FieldReadiness.Ready) view.State = ForecastState.Approximate;
            else if (!available) view.State = ForecastState.Computing;
            else view.State = ForecastState.Ready;
            if (view.State == ForecastState.Vacuum) view.StateText = ForecastStrings.StateVacuum;
            view.SiteLine = SiteLineOf(input);
            if (view.State != ForecastState.Ready) return view;

            view.IssuedLine = "Issued " + DateOf(input, report.IssuedUt);
            view.WindLine = ForecastFormat.WindSourceLabel(report.Wind) + ConfidenceText(report);
            StormLines(view, input, report);
            LaunchLines(view, input);
            StripOf(view, input, report);
            view.Rows = RowsOf(input, report);
            return view;
        }

        private static string SiteLineOf(ForecastViewInput input)
        {
            string name = string.IsNullOrEmpty(input.Site.Name) ? "Site" : input.Site.Name;
            return input.BodyName + ", " + name + ": " + ForecastFormat.Latitude(input.Site.LatDeg) + ", " + ForecastFormat.Longitude(input.Site.LonDeg);
        }

        private static string DateOf(ForecastViewInput input, double ut)
        {
            return input.DateText != null ? input.DateText(ut) : ut.ToString("0", Inv) + " s";
        }

        private static string ConfidenceText(ForecastReport report)
        {
            if (report.Wind.Kind != WeatherSourceKind.ExternalNowcast || report.Steps.Length == 0) return "";
            float last = report.Steps[report.Steps.Length - 1].Confidence01;
            return ", confidence " + Math.Round(last * 100f).ToString("0", Inv) + " % at the end";
        }

        // The nearest system: its distance, and its direction from the site where the tracks give it.
        private static string NearestText(ForecastViewInput input, ForecastReport report)
        {
            ForecastTrack[] tracks = report.Tracks;
            if (tracks == null || tracks.Length == 0 || input.BodyRadiusM <= 0.0)
                return double.IsNaN(report.NearestSystemDistanceM) ? "" : ForecastFormat.Distance(report.NearestSystemDistanceM);
            double best = double.MaxValue, bearing = 0.0;
            for (int t = 0; t < tracks.Length; t++)
            {
                double lat, lon;
                float radius, strength;
                double at = Math.Max(input.NowUt, tracks[t].Ut.Length > 0 ? tracks[t].Ut[0] : input.NowUt);
                if (!SystemTracker.PositionAt(tracks[t], at, out lat, out lon, out radius, out strength)) continue;
                double d = ForecastMath.DistanceM(input.Site.LatDeg, input.Site.LonDeg, lat, lon, input.BodyRadiusM);
                if (d >= best) continue;
                best = d;
                bearing = ForecastFormat.Bearing(input.Site.LatDeg, input.Site.LonDeg, lat, lon);
            }
            if (best == double.MaxValue) return double.IsNaN(report.NearestSystemDistanceM) ? "" : ForecastFormat.Distance(report.NearestSystemDistanceM);
            return ForecastFormat.Distance(best) + " " + ForecastFormat.Direction(bearing);
        }

        // The strongest band of the stay that begins at the threshold ETA, with the type of that step.
        private static void Peak(ForecastReport report, double fromUt, double toUt, out PrecipBand band, out PrecipType type)
        {
            band = PrecipBand.None;
            type = PrecipType.None;
            double step = report.StepSeconds > 0.0 ? report.StepSeconds : 3600.0;
            for (int i = 0; i < report.Steps.Length; i++)
            {
                ForecastStep s = report.Steps[i];
                if (s.Ut + step <= fromUt || s.Ut > toUt) continue;
                if ((int)s.Band > (int)band)
                {
                    band = s.Band;
                    type = s.Type;
                }
            }
        }

        private static void StormLines(ForecastView view, ForecastViewInput input, ForecastReport report)
        {
            ForecastDetail d = input.Detail;
            double now = input.NowUt;
            if (!d.Known || double.IsNaN(d.ThresholdEtaUt))
            {
                view.NextStormLine = "No storm within " + Math.Round(input.HorizonHours).ToString("0", Inv) + " h";
                view.ArrivalLine = " ";
                view.ClearLine = " ";
                return;
            }
            double end = double.IsNaN(d.ClearUt) ? double.MaxValue : d.ClearUt;
            PrecipBand band;
            PrecipType type;
            Peak(report, d.ThresholdEtaUt, end, out band, out type);
            string nearest = NearestText(input, report);
            string detail = ForecastFormat.LevelName(band) + (nearest.Length > 0 ? ", " + nearest : "");
            if (d.ActiveAtStart || d.ThresholdEtaUt <= now)
            {
                view.NextStormLine = "Storm now: " + ForecastFormat.Precip(band, type) + " (" + detail + ")";
                view.ArrivalLine = "Arrived";
            }
            else
            {
                view.NextStormLine = "Next storm " + LaunchConditions.InText(d.ThresholdEtaUt - now) + " (" + detail + ")";
                view.ArrivalLine = "Arrives " + DateOf(input, d.ThresholdEtaUt);
            }
            view.ClearLine = double.IsNaN(d.ClearUt)
                ? "Clears after the forecast horizon"
                : "Clears " + DateOf(input, d.ClearUt) + " (" + LaunchConditions.InText(d.ClearUt - now) + ")";
        }

        private static void LaunchLines(ForecastView view, ForecastViewInput input)
        {
            LaunchAssessment a = input.Launch;
            if (!input.LaunchSite || a == null) return;
            switch (a.Status)
            {
                case LaunchStatus.Go: view.LaunchHeadline = ForecastStrings.LaunchGo; break;
                case LaunchStatus.Marginal: view.LaunchHeadline = ForecastStrings.LaunchMarginal; break;
                case LaunchStatus.NoGo: view.LaunchHeadline = ForecastStrings.LaunchNoGo; break;
                default: view.LaunchHeadline = ForecastStrings.LaunchUnavailable; break;
            }
            for (int i = 0; i < LaunchReasonLines && i < a.Reasons.Length; i++) view.LaunchReasons[i] = a.Reasons[i];
            if (!double.IsNaN(a.WindowStartUt))
            {
                view.WindowLine = "Best window in the next 24 h: " + DateOf(input, a.WindowStartUt) + " to " + DateOf(input, a.WindowEndUt) + " ("
                                  + Math.Round((a.WindowEndUt - a.WindowStartUt) / 3600.0, 1).ToString("0.#", Inv) + " h)";
            }
            else
            {
                view.WindowLine = a.Status == LaunchStatus.Unavailable ? " " : "No launch window in the next 24 h";
            }
        }

        private static void StripOf(ForecastView view, ForecastViewInput input, ForecastReport report)
        {
            ForecastStep[] steps = report.Steps;
            int per = Math.Max(1, input.StripCells);
            view.StripPages = Math.Max(1, (steps.Length + per - 1) / per);
            view.StripPage = Math.Max(0, Math.Min(view.StripPages - 1, input.StripFirst / per));
            int first = view.StripPage * per;
            int count = Math.Max(0, Math.Min(per, steps.Length - first));
            view.Strip = new StripCell[count];
            double step = report.StepSeconds > 0.0 ? report.StepSeconds : 3600.0;
            for (int i = 0; i < count; i++)
            {
                ForecastStep s = steps[first + i];
                var cell = new StripCell();
                cell.Band = s.Band;
                cell.Text = ForecastStrings.Cell(s.Band);
                cell.BackHex = ForecastFormat.Hex(s.Band);
                cell.TextHex = ForecastFormat.TextHex(s.Band);
                cell.Alpha = ForecastFormat.ConfidenceAlpha(report.Wind, s.Confidence01);
                cell.Lead = Math.Max(0, (int)Math.Round((s.Ut - input.NowUt) / 3600.0)).ToString("0", Inv);
                view.Strip[i] = cell;
            }
            if (step <= 0.0) view.Strip = new StripCell[0];
        }

        private static string[][] RowsOf(ForecastViewInput input, ForecastReport report)
        {
            ForecastStep[] steps = report.Steps;
            var rows = new string[steps.Length][];
            for (int i = 0; i < steps.Length; i++)
            {
                ForecastStep s = steps[i];
                float k = input.FieldTemperatureK != null ? input.FieldTemperatureK(s.Ut) : float.NaN;
                rows[i] = new[]
                {
                    ForecastFormat.Lead(s.Ut - input.NowUt),
                    ForecastFormat.LevelName(s.Band),
                    s.Band == PrecipBand.None ? "-" : LaunchConditions.TypeName(s.Type),
                    ForecastFormat.Visibility(s.VisibilityM),
                    ForecastFormat.Wind(s.WindMs, s.WindFromDeg),
                    ForecastFormat.Gust(s.GustMs),
                    ForecastFormat.Temperature(k)
                };
            }
            return rows;
        }

        // The cells of the strip as one line of text, for the log and the probe: "H4 M3 -0".
        internal string StripText()
        {
            var parts = new List<string>();
            for (int i = 0; i < Strip.Length; i++) parts.Add(Strip[i].Text);
            return string.Join(" ", parts.ToArray());
        }
    }
}
