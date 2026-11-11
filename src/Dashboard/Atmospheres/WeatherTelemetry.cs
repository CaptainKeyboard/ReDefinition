using System;
using System.Collections.Generic;
using ReDefinition.Core;

namespace ReDefinition.Dashboard
{
    // The forecast of one site as the dashboard sends it: scalars and parallel arrays, because the dashboard's JSON reader takes flat
    // objects (strings, numbers and arrays of those). Built from the report the atmospheres publish in WeatherHooks.Forecast; nothing here
    // names them. Round trip: Encode writes it, Decode reads it back.
    internal sealed class WeatherDashboardModel
    {
        internal bool Available;
        internal string Body = "";
        internal string Site = "";
        internal double LatDeg, LonDeg;
        internal double IssuedUt, StepSeconds;
        internal string WindSource = "Ours";
        internal long Generation;
        internal double NearestEtaUt = double.NaN, NearestDistanceM = double.NaN;
        internal double[] Ut = new double[0];
        internal string[] Band = new string[0];
        internal string[] Type = new string[0];
        internal double[] Level = new double[0];
        internal double[] VisibilityM = new double[0];
        internal double[] WindMs = new double[0];
        internal double[] GustMs = new double[0];
        internal double[] WindFromDeg = new double[0];
        internal double[] Confidence = new double[0];

        internal static WeatherDashboardModel From(ForecastReport report, ForecastSite site, string bodyName)
        {
            var m = new WeatherDashboardModel();
            m.Body = bodyName ?? "";
            m.Site = site.Name ?? "";
            m.LatDeg = site.LatDeg;
            m.LonDeg = site.LonDeg;
            if (report == null) return m;
            m.Available = report.Available;
            m.IssuedUt = report.IssuedUt;
            m.StepSeconds = report.StepSeconds;
            m.WindSource = report.Wind.ToString();
            m.Generation = report.Generation;
            m.NearestEtaUt = report.NearestSystemEtaUt;
            m.NearestDistanceM = report.NearestSystemDistanceM;
            int n = report.Steps.Length;
            m.Ut = new double[n];
            m.Band = new string[n];
            m.Type = new string[n];
            m.Level = new double[n];
            m.VisibilityM = new double[n];
            m.WindMs = new double[n];
            m.GustMs = new double[n];
            m.WindFromDeg = new double[n];
            m.Confidence = new double[n];
            for (int i = 0; i < n; i++)
            {
                ForecastStep s = report.Steps[i];
                m.Ut[i] = s.Ut;
                m.Band[i] = PrecipScale.DefaultName(s.Band);
                m.Type[i] = s.Type.ToString();
                m.Level[i] = s.Level01;
                m.VisibilityM[i] = s.VisibilityM;
                m.WindMs[i] = s.WindMs;
                m.GustMs[i] = s.GustMs;
                m.WindFromDeg[i] = s.WindFromDeg;
                m.Confidence[i] = s.Confidence01;
            }
            return m;
        }

        internal Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "available", Available ? "true" : "false" },
                { "body", Body },
                { "site", Site },
                { "lat", LatDeg },
                { "lon", LonDeg },
                { "issuedUt", IssuedUt },
                { "stepSeconds", StepSeconds },
                { "windSource", WindSource },
                { "generation", Generation },
                { "nearestEtaUt", NearestEtaUt },
                { "nearestDistanceM", NearestDistanceM },
                { "ut", Ut },
                { "band", Band },
                { "type", Type },
                { "level", Level },
                { "visibilityM", VisibilityM },
                { "windMs", WindMs },
                { "gustMs", GustMs },
                { "windFromDeg", WindFromDeg },
                { "confidence", Confidence }
            };
        }

        internal string Encode()
        {
            return Json.Encode(ToDictionary());
        }

        // Reads what Encode wrote. A number that was NaN comes back as NaN (it is written as null).
        internal static WeatherDashboardModel Decode(string text)
        {
            Dictionary<string, object> d = Json.DecodeObject(text);
            var m = new WeatherDashboardModel();
            m.Available = Str(d, "available") == "true";
            m.Body = Str(d, "body");
            m.Site = Str(d, "site");
            m.LatDeg = Num(d, "lat");
            m.LonDeg = Num(d, "lon");
            m.IssuedUt = Num(d, "issuedUt");
            m.StepSeconds = Num(d, "stepSeconds");
            m.WindSource = Str(d, "windSource");
            m.Generation = (long)Num(d, "generation");
            m.NearestEtaUt = Num(d, "nearestEtaUt");
            m.NearestDistanceM = Num(d, "nearestDistanceM");
            m.Ut = Numbers(d, "ut");
            m.Band = Strings(d, "band");
            m.Type = Strings(d, "type");
            m.Level = Numbers(d, "level");
            m.VisibilityM = Numbers(d, "visibilityM");
            m.WindMs = Numbers(d, "windMs");
            m.GustMs = Numbers(d, "gustMs");
            m.WindFromDeg = Numbers(d, "windFromDeg");
            m.Confidence = Numbers(d, "confidence");
            return m;
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v as string ?? "" : "";
        }

        private static double Num(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v)) return double.NaN;
            if (v is double) return (double)v;
            return double.NaN;
        }

        private static double[] Numbers(Dictionary<string, object> d, string key)
        {
            object v;
            var list = d.TryGetValue(key, out v) ? v as List<object> : null;
            if (list == null) return new double[0];
            var result = new double[list.Count];
            for (int i = 0; i < result.Length; i++) result[i] = list[i] is double ? (double)list[i] : double.NaN;
            return result;
        }

        private static string[] Strings(Dictionary<string, object> d, string key)
        {
            object v;
            var list = d.TryGetValue(key, out v) ? v as List<object> : null;
            if (list == null) return new string[0];
            var result = new string[list.Count];
            for (int i = 0; i < result.Length; i++) result[i] = list[i] as string ?? "";
            return result;
        }
    }

    // The alerts the forecast raised, newest last, as parallel arrays for the same reason. A bounded list in memory: a dashboard that
    // connects late reads what the last ones were.
    internal sealed class WeatherAlertFeed
    {
        internal const int Capacity = 20;
        private readonly List<ForecastAlert> alerts = new List<ForecastAlert>();

        // Whether the feed takes alerts (the player's switch for the dashboard's push).
        internal bool Enabled = true;
        internal long Count { get; private set; }

        internal int Length
        {
            get { return alerts.Count; }
        }

        internal void Add(ForecastAlert alert)
        {
            if (!Enabled) return;
            alerts.Add(alert);
            Count++;
            if (alerts.Count > Capacity) alerts.RemoveAt(0);
        }

        internal Dictionary<string, object> ToDictionary()
        {
            int n = alerts.Count;
            var kind = new string[n];
            var ut = new double[n];
            var eta = new double[n];
            var band = new string[n];
            var text = new string[n];
            var site = new string[n];
            for (int i = 0; i < n; i++)
            {
                ForecastAlert a = alerts[i];
                kind[i] = a.Kind.ToString();
                ut[i] = a.Ut;
                eta[i] = a.EtaSeconds;
                band[i] = PrecipScale.DefaultName(a.Band);
                text[i] = a.Text ?? "";
                site[i] = a.Site.Name ?? "";
            }
            return new Dictionary<string, object>
            {
                { "count", Count }, { "kind", kind }, { "ut", ut }, { "etaSeconds", eta }, { "band", band }, { "text", text }, { "site", site }
            };
        }

        internal string Encode()
        {
            return Json.Encode(ToDictionary());
        }
    }

    // The dashboard's side of the weather: the model of a site and the alert feed, behind the keys w.forecast and w.alerts (the telemetry
    // table registers them with the readers below). Reads WeatherHooks only, so without the atmospheres a read answers null and nothing is
    // listened to. The widget on the web page needs the owner's plan-first approval and is not built.
    internal sealed class WeatherTelemetry
    {
        // The one the telemetry table reads (w.forecast and w.alerts are registered there with `WeatherTelemetry.Shared.Forecast` and
        // `.Alerts`); it listens from the start of the game, so that a dashboard that connects late reads the last alerts.
        internal static readonly WeatherTelemetry Shared = new WeatherTelemetry();

        internal readonly WeatherAlertFeed Feed = new WeatherAlertFeed();
        private readonly Action<ForecastAlert> listener;
        private bool attached;

        internal WeatherTelemetry()
        {
            listener = Feed.Add;
        }

        // Listens for alerts; asked once, repeated asks do nothing.
        internal void Attach()
        {
            if (attached) return;
            WeatherHooks.AlertRaised += listener;
            attached = true;
        }

        internal void Detach()
        {
            if (!attached) return;
            WeatherHooks.AlertRaised -= listener;
            attached = false;
        }

        // w.forecast: the forecast of the active vessel's place in flight, else of the Space Centre; null where no forecast is published.
        internal object Forecast(string[] args)
        {
            Func<ForecastSite, ForecastReport> source = WeatherHooks.Forecast;
            if (source == null) return null;
            ForecastSite site;
            string body;
            if (!SiteNow(out site, out body)) return null;
            ForecastReport report = source(site);
            if (report == null) return null;
            return new Json.Raw(WeatherDashboardModel.From(report, site, body).Encode());
        }

        // w.alerts: the last alerts; null where the atmospheres are not there (nothing was ever raised and no forecast is published).
        internal object Alerts(string[] args)
        {
            if (WeatherHooks.Forecast == null && Feed.Length == 0) return null;
            Attach();
            return new Json.Raw(Feed.Encode());
        }

        // The forecast of the active vessel's place in flight, else of the Space Centre; false where the game has neither.
        private static bool SiteNow(out ForecastSite site, out string body)
        {
            site = new ForecastSite();
            body = "";
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.fetch != null && FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.mainBody != null)
            {
                Vessel v = FlightGlobals.ActiveVessel;
                double height = v.heightFromTerrain >= 0.0 ? v.heightFromTerrain : v.altitude;
                site = new ForecastSite { BodyIndex = v.mainBody.flightGlobalsIndex, LatDeg = v.latitude, LonDeg = v.longitude, AltAgl = height, Name = v.vesselName };
                body = v.mainBody.bodyName;
                return true;
            }
            SpaceCenter centre = SpaceCenter.Instance;
            if (centre == null || centre.cb == null) return false;
            site = new ForecastSite { BodyIndex = centre.cb.flightGlobalsIndex, LatDeg = centre.Latitude, LonDeg = centre.Longitude, Name = "Space Centre" };
            body = centre.cb.bodyName;
            return true;
        }
    }
}
