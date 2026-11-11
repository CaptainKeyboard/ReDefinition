using System;

namespace ReDefinition.Core
{
    // A place a forecast is made for. A vessel's site follows the vessel: the consumer asks again with its current position.
    public struct ForecastSite
    {
        public int BodyIndex;
        public double LatDeg;
        public double LonDeg;
        // The height above the ground in metres.
        public double AltAgl;
        // A name for the interface ("Launch site", a vessel's name).
        public string Name;
    }

    // One step of a site's forecast, hourly by default. The values are statistical: the forecast and the live weather agree on the
    // smooth level, gust and shower pulses are display-only multipliers.
    public struct ForecastStep
    {
        public double Ut;
        public float Level01;
        public PrecipBand Band;
        public PrecipType Type;
        public float Intensity01;
        public float VisibilityM;
        public float WindMs;
        // The direction the wind comes from, degrees clockwise from north.
        public float WindFromDeg;
        public float GustMs;
        // 1 by default; an error model or an external provider gives less.
        public float Confidence01;
        // The number of weather systems that touch the site in this step.
        public int CellCount;
    }

    // The path of one weather system (a cell or a front), sampled over the horizon, for a map overlay.
    public sealed class ForecastTrack
    {
        // A key that stays the same for one system across reports.
        public ulong CellKey;
        public double[] Ut = new double[0];
        public double[] Lat = new double[0];
        public double[] Lon = new double[0];
        public float[] RadiusM = new float[0];
        public float[] Strength01 = new float[0];
    }

    // What an alert says happened or will happen at a site.
    public enum AlertKind
    {
        // A system reaches the user's level within the lead time.
        Approaching = 0,
        // The level has been reached at the site.
        Arrived = 1,
        // The level rises by a band while it is there.
        Worsening = 2,
        // The level falls below the user's threshold.
        Clearing = 3,
        // A window in which the launch limits hold (launchMaxLevel, launchMaxWindMs, launchMinVisibilityM) begins.
        LaunchWindow = 4
    }

    // A warning about a site: what, when, how far away.
    public struct ForecastAlert
    {
        public AlertKind Kind;
        public double Ut;
        public double EtaSeconds;
        public PrecipBand Band;
        public ulong CellKey;
        public ForecastSite Site;
        public string Text;
    }

    // Where a wind value came from.
    public enum WeatherSourceKind
    {
        // The field of this mod.
        Ours = 0,
        // Another mod's provider; the name says which.
        Foreign = 1,
        // Another mod's current wind carried forward with falling confidence.
        ExternalNowcast = 2
    }

    // The label of the wind in a forecast: Ours, Foreign(name) or ExternalNowcast(name).
    public struct WeatherSource
    {
        public WeatherSourceKind Kind;
        public string Name;

        public static WeatherSource Ours()
        {
            return new WeatherSource { Kind = WeatherSourceKind.Ours, Name = "Ours" };
        }

        public static WeatherSource Foreign(string name)
        {
            return new WeatherSource { Kind = WeatherSourceKind.Foreign, Name = name };
        }

        public static WeatherSource ExternalNowcast(string name)
        {
            return new WeatherSource { Kind = WeatherSourceKind.ExternalNowcast, Name = name };
        }

        // "Ours", "Foreign(name)" or "ExternalNowcast(name)".
        public override string ToString()
        {
            switch (Kind)
            {
                case WeatherSourceKind.Foreign: return "Foreign(" + Name + ")";
                case WeatherSourceKind.ExternalNowcast: return "ExternalNowcast(" + Name + ")";
                default: return "Ours";
            }
        }
    }

    // The forecast of one site: steps, the systems around it, alerts, and the nearest system's arrival.
    public sealed class ForecastReport
    {
        public int BodyIndex;
        public double IssuedUt;
        public double StepSeconds;
        // False while the field is not Ready: forecast and alerts then report "unavailable".
        public bool Available;
        public ForecastStep[] Steps = new ForecastStep[0];
        public ForecastTrack[] Tracks = new ForecastTrack[0];
        public ForecastAlert[] Alerts = new ForecastAlert[0];
        // The time and the distance in metres of the nearest system; NaN where there is none.
        public double NearestSystemEtaUt = double.NaN;
        public double NearestSystemDistanceM = double.NaN;
        // The source of the wind in the steps.
        public WeatherSource Wind = WeatherSource.Ours();
        // WeatherHooks.ForecastGeneration when the report was made; a changed number means the inputs changed.
        public long Generation;
    }
}
