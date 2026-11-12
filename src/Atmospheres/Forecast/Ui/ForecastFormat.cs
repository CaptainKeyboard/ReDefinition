using System;
using System.Globalization;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // The text of the forecast interface: levels, directions, distances, the temperature of the field, the wind label. Pure, no Unity
    // and no game types, so that a test reads every string. Numbers are written in the invariant culture; temperatures in degrees
    // Celsius; latitudes as "deg N/S latitude".
    internal static class ForecastFormat
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly string[] Compass = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        // The letter of a band, so that the level does not rest on a colour.
        internal static string Letter(PrecipBand band)
        {
            switch (band)
            {
                case PrecipBand.Flurries: return "F";
                case PrecipBand.Light: return "L";
                case PrecipBand.Moderate: return "M";
                case PrecipBand.Heavy: return "H";
                case PrecipBand.Blizzard: return "B";
                default: return "-";
            }
        }

        // The band's name with its number beside it: "Heavy (4)".
        internal static string LevelName(PrecipBand band)
        {
            return PrecipScale.DefaultName(band) + " (" + ((int)band).ToString(Inv) + ")";
        }

        // The colour of a band as an RGB hex string. The hues are those of the Okabe-Ito palette (sky blue, bluish green, yellow, orange,
        // vermillion), which stay apart for the common kinds of colour blindness; the letter and the number carry the level anyway.
        internal static string Hex(PrecipBand band)
        {
            switch (band)
            {
                case PrecipBand.Flurries: return "56B4E9";
                case PrecipBand.Light: return "009E73";
                case PrecipBand.Moderate: return "F0E442";
                case PrecipBand.Heavy: return "E69F00";
                case PrecipBand.Blizzard: return "D55E00";
                default: return "4A4A4A";
            }
        }

        // The colour of the letter on a band's cell: dark on the light cells, white on the dark ones.
        internal static string TextHex(PrecipBand band)
        {
            switch (band)
            {
                case PrecipBand.Moderate:
                case PrecipBand.Flurries:
                case PrecipBand.Heavy:
                    return "101010";
                default: return "FFFFFF";
            }
        }

        // The compass point a bearing falls in, clockwise from north.
        internal static string Direction(double bearingDeg)
        {
            double b = bearingDeg % 360.0;
            if (b < 0.0) b += 360.0;
            return Compass[(int)Math.Floor((b + 22.5) / 45.0) % 8];
        }

        // The bearing from one place to another in degrees clockwise from north.
        internal static double Bearing(double lat1Deg, double lon1Deg, double lat2Deg, double lon2Deg)
        {
            double p1 = lat1Deg * ForecastMath.DegToRad, p2 = lat2Deg * ForecastMath.DegToRad;
            double dl = ForecastMath.WrapLon(lon2Deg - lon1Deg) * ForecastMath.DegToRad;
            double y = Math.Sin(dl) * Math.Cos(p2);
            double x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
            double b = Math.Atan2(y, x) * ForecastMath.RadToDeg;
            return b < 0.0 ? b + 360.0 : b;
        }

        // "850 m", "4.2 km", "12 km".
        internal static string Distance(double metres)
        {
            if (double.IsNaN(metres) || double.IsInfinity(metres)) return "-";
            if (metres < 1000.0) return Math.Round(metres).ToString("0", Inv) + " m";
            double km = metres / 1000.0;
            return (km < 10.0 ? km.ToString("0.0", Inv) : Math.Round(km).ToString("0", Inv)) + " km";
        }

        // The visibility as the table shows it: "850 m", "2.4 km", "> 10 km".
        internal static string Visibility(float metres)
        {
            if (float.IsNaN(metres)) return "-";
            if (metres >= 10000f) return "> 10 km";
            return Distance(metres);
        }

        // The air temperature of the field in degrees Celsius; "-" where the field does not know it.
        internal static string Temperature(float kelvin)
        {
            if (float.IsNaN(kelvin) || float.IsInfinity(kelvin)) return "-";
            return Math.Round(kelvin - 273.15f).ToString("0", Inv) + " °C";
        }

        // "9 m/s NW": the direction the wind blows from (the column is headed "Wind From").
        internal static string Wind(float windMs, float fromDeg)
        {
            if (float.IsNaN(windMs)) return "-";
            string speed = Math.Round(windMs).ToString("0", Inv) + " m/s";
            return windMs < 0.5f ? speed : speed + " " + Direction(fromDeg);
        }

        internal static string Gust(float gustMs)
        {
            return float.IsNaN(gustMs) ? "-" : Math.Round(gustMs).ToString("0", Inv) + " m/s";
        }

        // "28.6 deg N latitude".
        internal static string Latitude(double latDeg)
        {
            return Math.Abs(latDeg).ToString("0.0", Inv) + " deg " + (latDeg >= 0.0 ? "N" : "S") + " latitude";
        }

        // "80.0 deg W longitude".
        internal static string Longitude(double lonDeg)
        {
            double l = ForecastMath.WrapLon(lonDeg);
            return Math.Abs(l).ToString("0.0", Inv) + " deg " + (l >= 0.0 ? "E" : "W") + " longitude";
        }

        // The lead time of a table row: "now", "+1 h", "+2 h 30 min".
        internal static string Lead(double seconds)
        {
            if (seconds < 60.0) return "now";
            int minutes = (int)Math.Round(seconds / 60.0);
            int h = minutes / 60, m = minutes % 60;
            if (h == 0) return "+" + m.ToString(Inv) + " min";
            return m == 0 ? "+" + h.ToString(Inv) + " h" : "+" + h.ToString(Inv) + " h " + m.ToString(Inv) + " min";
        }

        // "Heavy snow", "Light rain": the band's name and the type's.
        internal static string Precip(PrecipBand band, PrecipType type)
        {
            if (band == PrecipBand.None) return "Clear";
            return PrecipScale.DefaultName(band) + " " + LaunchConditions.TypeName(type);
        }

        // The wind's source for the window: "Wind: ours", "Wind: KWP", "Wind: KWP, now only" (another mod's current wind carried forward).
        internal static string WindSourceLabel(WeatherSource source)
        {
            switch (source.Kind)
            {
                case WeatherSourceKind.Foreign: return "Wind: " + source.Name;
                case WeatherSourceKind.ExternalNowcast: return "Wind: " + source.Name + ForecastStrings.WindNowOnlySuffix;
                default: return ForecastStrings.WindOurs;
            }
        }

        // The opacity of a row's confidence bar: full for a step the source forecasts, faded for persistence (another mod's wind carried
        // forward), by the step's confidence.
        internal static float ConfidenceAlpha(WeatherSource source, float confidence01)
        {
            float c = float.IsNaN(confidence01) ? 0f : Math.Max(0f, Math.Min(1f, confidence01));
            if (source.Kind != WeatherSourceKind.ExternalNowcast) return 1f;
            return 0.25f + 0.75f * c;
        }

        // The state of the interface for a report and a field.
        internal static string StateText(bool enabled, bool hasBody, FieldReadiness readiness, bool reportAvailable)
        {
            if (!enabled) return ForecastStrings.StateOff;
            if (!hasBody) return ForecastStrings.StateNoBody;
            if (readiness == FieldReadiness.CatchingUp || readiness == FieldReadiness.Seeding) return ForecastStrings.StateApproximate;
            if (readiness == FieldReadiness.Broken) return ForecastStrings.StateBroken;
            if (!reportAvailable) return ForecastStrings.StateComputing;
            return "";
        }
    }
}
