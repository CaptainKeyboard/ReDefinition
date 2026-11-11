using System;
using System.Globalization;
using ReDefinition.Core;
using ReDefinition.Hud.Readouts;
using UnityEngine;

namespace ReDefinition.Hud
{
    // The weather line of the flight panel: "Weather: Heavy snow in 2 h 10 min | wind 9 m/s". It reads the forecast the atmospheres publish
    // in WeatherHooks.Forecast and nothing else, so it needs no reference to them: where the atmospheres are not installed, the field is
    // null and the line is absent. It is a readout of the panel (key "weather", group Air) that a profile can name; it applies only in the
    // air of a body, with a forecast that is available. The text is made by Compose, which is free of Unity and the game.
    internal static class WeatherLine
    {
        internal const string Key = "weather";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // The line's two parts for a report at a game time: the precipitation now or ahead, and the wind now. Null for no line (no report,
        // not available, no steps).
        internal static bool Compose(ForecastReport report, double nowUt, out string precipitation, out string wind)
        {
            precipitation = wind = null;
            if (report == null || !report.Available || report.Steps == null || report.Steps.Length == 0) return false;
            ForecastStep[] steps = report.Steps;
            int now = 0;
            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i].Ut <= nowUt) now = i;
                else break;
            }
            ForecastStep current = steps[now];
            wind = "wind " + Math.Round(current.WindMs).ToString("0", Inv) + " m/s";
            if (current.Band != PrecipBand.None)
            {
                precipitation = Label(Peak(steps, now)) + " now";
                return true;
            }
            for (int i = now + 1; i < steps.Length; i++)
            {
                if (steps[i].Band == PrecipBand.None) continue;
                precipitation = Label(Peak(steps, i)) + " " + In(steps[i].Ut - nowUt);
                return true;
            }
            precipitation = "clear";
            return true;
        }

        // The whole line as text.
        internal static string Text(ForecastReport report, double nowUt)
        {
            string precipitation, wind;
            return Compose(report, nowUt, out precipitation, out wind) ? "Weather: " + precipitation + " | " + wind : null;
        }

        // The strongest step of the run of precipitation that begins at the index.
        private static ForecastStep Peak(ForecastStep[] steps, int from)
        {
            ForecastStep best = steps[from];
            for (int i = from; i < steps.Length && steps[i].Band != PrecipBand.None; i++)
            {
                if ((int)steps[i].Band > (int)best.Band) best = steps[i];
            }
            return best;
        }

        private static string Label(ForecastStep step)
        {
            return PrecipScale.DefaultName(step.Band) + " " + TypeName(step.Type);
        }

        internal static string TypeName(PrecipType type)
        {
            switch (type)
            {
                case PrecipType.Snow: return "snow";
                case PrecipType.Rain: return "rain";
                case PrecipType.Sleet: return "sleet";
                case PrecipType.FreezingRain: return "freezing rain";
                case PrecipType.Dust: return "dust";
                case PrecipType.Sand: return "sand";
                case PrecipType.Ash: return "ash";
                case PrecipType.Methane: return "methane rain";
                default: return "precipitation";
            }
        }

        // "in 2 h 10 min".
        internal static string In(double seconds)
        {
            if (seconds < 60.0) return "now";
            int minutes = (int)Math.Round(seconds / 60.0);
            int h = minutes / 60, m = minutes % 60;
            if (h == 0) return "in " + m.ToString(Inv) + " min";
            return m == 0 ? "in " + h.ToString(Inv) + " h" : "in " + h.ToString(Inv) + " h " + m.ToString(Inv) + " min";
        }

        // The forecast of the vessel's place, or null: not in the air of a body, or no forecast published.
        private static ForecastReport ReportOf(ReadoutFrame f)
        {
            Func<ForecastSite, ForecastReport> source = WeatherHooks.Forecast;
            if (source == null || f == null || f.Vessel == null || f.Body == null) return null;
            Vessel vessel = f.Vessel;
            if (!f.Body.atmosphere || vessel.altitude > f.Body.atmosphereDepth) return null;
            // AltAgl carries metres above sea level (CONTRACT.md, "Altitude argument").
            var site = new ForecastSite
            {
                BodyIndex = f.Body.flightGlobalsIndex, LatDeg = vessel.latitude, LonDeg = vessel.longitude, AltAgl = vessel.altitude, Name = vessel.vesselName
            };
            return source(site);
        }

        private static double Now()
        {
            return Planetarium.fetch != null ? Planetarium.GetUniversalTime() : double.NaN;
        }

        internal static bool Applies(ReadoutFrame f)
        {
            double now = Now();
            if (double.IsNaN(now)) return false;
            string precipitation, wind;
            return Compose(ReportOf(f), now, out precipitation, out wind);
        }

        private static string Part(ReadoutFrame f, bool second)
        {
            string precipitation, wind;
            if (!Compose(ReportOf(f), Now(), out precipitation, out wind)) return "-";
            return second ? wind : precipitation;
        }

        // The readout the panel lists. Registering it twice adds it once.
        internal static Readout MakeReadout()
        {
            return new Readout
            {
                Key = Key, Title = "WEATHER", Group = ReadoutCatalog.Air, Value = f => Part(f, false), Second = f => Part(f, true), Applies = Applies
            };
        }

        internal static void Register()
        {
            if (ReadoutCatalog.Find(Key) == null) ReadoutCatalog.All.Add(MakeReadout());
        }
    }

    // Registers the weather line with the panel's catalogue at the start of the game, before any profile is read.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    internal sealed class WeatherLineStartup : MonoBehaviour
    {
        private void Awake()
        {
            try
            {
                WeatherLine.Register();
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("weather-line", "the weather line could not be registered (" + CompatibilityLog.Reason(e) + ")");
            }
        }
    }
}
