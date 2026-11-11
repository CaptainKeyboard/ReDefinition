using System;
using System.Threading;

namespace ReDefinition.Core
{
    // The upscaler the host runs, as the atmospheres need to know it.
    public enum UpscalerKind
    {
        None = 0,
        Dlss = 1,
        Fsr = 2,
        Xess = 3
    }

    // The quality tier the budgets are chosen by. The setting qualityTier may say Auto; the host resolves it and writes a tier from
    // Low to Max into WeatherRenderCaps.
    public enum QualityTier
    {
        Auto = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Ultra = 4,
        Max = 5
    }

    // What the host (the main mod) tells the atmospheres. Only the host writes it; the atmospheres read it. Defaults describe a
    // game without the main mod: no upscaler, no host API, tier Auto.
    public sealed class WeatherRenderCaps
    {
        public UpscalerKind Upscaler;
        public bool FrameGenerationOn;
        // Whether ReDefinition.Api answers, so that the after-upscaling hooks can be used.
        public bool HostApiAvailable;
        public QualityTier Tier = QualityTier.Auto;
    }

    // The shared state of the weather seam: the view of the frame, the host's caps, the host's cloud optical depth, and the forecast
    // that the atmospheres publish. Static fields hold stable references; "whoever assigns last wins", as in the other hooks.
    public static class WeatherHooks
    {
        // The weather at the camera. One writer: the frame driver of the atmospheres.
        public static readonly WeatherView View = new WeatherView();

        // The host's caps. One writer: the host.
        public static readonly WeatherRenderCaps Caps = new WeatherRenderCaps();

        // The vertical optical depth of the clouds at a place (latitude, longitude, altitude, UT), filled by the host when EVE's decks are
        // present. Null means there are none.
        public static Func<double, double, double, double, float> CloudOpticalDepth;

        // The forecast of a site; assigned by the atmospheres, null until the field is Ready.
        public static Func<ForecastSite, ForecastReport> Forecast;

        // The tracks of the weather systems of a body (by body index), for a map overlay; null where there is no forecast.
        public static Func<int, ForecastTrack[]> BodyTracks;

        // Raised when an alert fires, for consumers that want the push (the dashboard, the HUD).
        public static event Action<ForecastAlert> AlertRaised;

        private static long forecastGeneration;

        // Rises whenever the inputs of the forecast change (seed, settings, the field's DefinitionId); a report older than this is stale.
        // It is not the simulation's epoch boundary.
        public static long ForecastGeneration
        {
            get { return Interlocked.Read(ref forecastGeneration); }
        }

        // Raises ForecastGeneration and returns the new value.
        public static long BumpForecastGeneration()
        {
            return Interlocked.Increment(ref forecastGeneration);
        }

        // Raises AlertRaised; a handler that throws does not keep the others from the alert.
        public static void RaiseAlert(ForecastAlert alert)
        {
            Action<ForecastAlert> handlers = AlertRaised;
            if (handlers == null) return;
            foreach (Delegate d in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<ForecastAlert>)d)(alert);
                }
                catch (Exception e)
                {
                    LogHandlerFailure(e);
                }
            }
        }

        private static void LogHandlerFailure(Exception e)
        {
            try
            {
                CompatibilityLog.Warn("weather-alert-handler", "an alert handler failed (" + CompatibilityLog.Reason(e) + ")");
            }
            catch (Exception)
            {
                // Outside the game the log is not there; the failure of a handler is not worth another one.
            }
        }
    }
}
