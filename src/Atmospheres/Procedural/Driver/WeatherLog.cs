using System.Globalization;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The log lines of the weather frame driver, built from plain values: the driver decides when, this class says what. The numbers are written with the invariant culture.
    internal static class WeatherLog
    {
        private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

        // The line at the start of the flight scene: the frame driver that writes the view.
        internal static string DriverReady()
        {
            return "frame driver ready in flight: the single writer of the weather view (LateUpdate), light written in Camera.onPreCull; "
                   + "shower pulse " + (Tuning.Flag("driver.showerPulse", true) ? "on" : "off")
                   + ", freezing rain " + (Tuning.Flag("driver.freezingRain", false) ? "on" : "off");
        }

        // The band or the type changed: the numbers that follow from the level.
        internal static string DriverBand(WeatherView view, float level, TypeMix mix, LightWriter light)
        {
            return "weather " + PrecipScale.DefaultName(view.Band) + " " + view.Type + ": level " + view.Level.ToString("0.00", Culture)
                   + " (field " + level.ToString("0.00", Culture) + "), snow/sleet/rain " + mix.Snow.ToString("0.00", Culture) + "/"
                   + mix.Sleet.ToString("0.00", Culture) + "/" + mix.Rain.ToString("0.00", Culture)
                   + ", visibility " + view.VisibilityM.ToString("0", Culture) + " m, precipitation extinction " + view.PrecipExtinctionGroundAlpha.ToString("0.00", Culture)
                   + ", flakes " + view.FlakeRatePerS.ToString("0", Culture) + "/s, sun transmission " + view.SunTransmission.ToString("0.00", Culture)
                   + ", sun factor " + light.SunFactor.ToString("0.00", Culture) + ", ambient factor " + view.AmbientFactor.ToString("0.00", Culture);
        }
    }
}
