using System;
using System.Collections.Generic;
using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.D30
{
    // Reports, tracks and fakes for the tests of the forecast interface (package D30).
    internal static class D30Fixtures
    {
        internal const double H = 3600.0;
        internal const double RadiusM = 600000.0;

        internal static ForecastSite Site(string name = "Pad")
        {
            return new ForecastSite { BodyIndex = 1, LatDeg = 28.5, LonDeg = -80.5, Name = name };
        }

        // A report whose steps carry the bands in the order given, one every hour from startUt.
        internal static ForecastReport Report(double startUt, params PrecipBand[] bands)
        {
            var report = new ForecastReport { BodyIndex = 1, IssuedUt = startUt, StepSeconds = H, Available = true };
            report.Steps = new ForecastStep[bands.Length];
            for (int i = 0; i < bands.Length; i++)
            {
                float level = bands[i] == PrecipBand.None ? 0f : PrecipScale.StartOf(bands[i]) + 0.05f;
                report.Steps[i] = new ForecastStep
                {
                    Ut = startUt + i * H, Level01 = level, Band = bands[i], Type = bands[i] == PrecipBand.None ? PrecipType.None : PrecipType.Snow,
                    Intensity01 = level, VisibilityM = bands[i] == PrecipBand.None ? 100000f : 4000f / (1 + (int)bands[i]), WindMs = 6f + i, WindFromDeg = 300f,
                    GustMs = 9f + i, Confidence01 = 1f
                };
            }
            return report;
        }

        internal static ForecastTrack Track(ulong key, double ut0, double lat0, double lon0, double dLatPerHour, double dLonPerHour, int samples, float radiusM,
            float strength)
        {
            var t = new ForecastTrack { CellKey = key };
            t.Ut = new double[samples];
            t.Lat = new double[samples];
            t.Lon = new double[samples];
            t.RadiusM = new float[samples];
            t.Strength01 = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                double hours = i * 0.5;
                t.Ut[i] = ut0 + hours * H;
                t.Lat[i] = lat0 + dLatPerHour * hours;
                t.Lon[i] = lon0 + dLonPerHour * hours;
                t.RadiusM[i] = radiusM;
                t.Strength01[i] = strength;
            }
            return t;
        }
    }

    // A projector that maps latitude and longitude to a plane (an equirectangular view of the body): enough to test the overlay's maths
    // without a camera. Places beyond the hidden longitude band are "on the far side".
    internal sealed class PlaneProjector : IMapProjector
    {
        internal float PixelsPerDegree = 8f;
        internal float OriginX = 400f, OriginY = 300f;
        internal double HiddenFromLon = double.NaN, HiddenToLon = double.NaN;
        internal int Calls;

        public bool Project(int bodyIndex, double latDeg, double lonDeg, out float x, out float y)
        {
            Calls++;
            x = OriginX + (float)lonDeg * PixelsPerDegree;
            y = OriginY + (float)latDeg * PixelsPerDegree;
            if (!double.IsNaN(HiddenFromLon) && lonDeg >= HiddenFromLon && lonDeg <= HiddenToLon) return false;
            return true;
        }
    }

    internal sealed class FakeSink : IAlertSink
    {
        internal readonly List<string> Screen = new List<string>();
        internal readonly List<string> App = new List<string>();
        internal int WarpStops;
        internal bool Running;
        internal Func<ForecastSite, bool> ActiveSite = s => false;

        public void ScreenMessage(string text, float seconds)
        {
            Screen.Add(text);
        }

        public void MessageApp(string title, string text, AlertKind kind)
        {
            App.Add(kind + ":" + text);
        }

        public void StopWarp()
        {
            WarpStops++;
            Running = false;
        }

        public bool IsActiveVesselSite(ForecastSite site)
        {
            return ActiveSite(site);
        }

        public bool WarpRunning
        {
            get { return Running; }
        }

        internal double Rate = 1.0;

        public double WarpRate
        {
            get { return Rate; }
        }
    }

    internal sealed class FakeKac : IKacApi
    {
        internal bool IsReady = true;
        internal int NextId = 1;
        internal readonly Dictionary<string, KeyValuePair<string, double>> Alarms = new Dictionary<string, KeyValuePair<string, double>>();
        internal readonly Dictionary<string, KacAction> Actions = new Dictionary<string, KacAction>();
        internal readonly List<string> Deleted = new List<string>();

        public bool Ready
        {
            get { return IsReady; }
        }

        public string CreateAlarm(string name, double ut, KacAction action)
        {
            string id = "A" + NextId++;
            Alarms[id] = new KeyValuePair<string, double>(name, ut);
            Actions[id] = action;
            return id;
        }

        public bool DeleteAlarm(string id)
        {
            Deleted.Add(id);
            return Alarms.Remove(id);
        }
    }
}
