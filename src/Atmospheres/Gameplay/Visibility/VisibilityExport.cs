using System;
using System.Globalization;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The visibility at the active vessel as one number in metres, and the line the flight readout and the HUD show (package D31, setting
    // visibilityExport). The number is the frame driver's (WeatherHooks.View.VisibilityM, which already holds the rate limit) while the view
    // is fresh, and the merged sample's otherwise. Hooks into other mods' sensors or cameras are not made: no consumer mod is installed
    // [trigger: a concrete consumer mod is installed].
    internal static class VisibilityExport
    {
        // The visibility in metres at the active vessel, or NaN where the export is off or there is no weather there.
        internal static volatile float VisibilityM = float.NaN;

        // "visibility 400 m", "visibility 1.2 km", or empty where there is none.
        internal static string Line
        {
            get { return Format(VisibilityM); }
        }

        // The line for a visibility in metres: tens of metres below a kilometre, a tenth of a kilometre below ten, whole kilometres above.
        internal static string Format(float metres)
        {
            if (float.IsNaN(metres) || metres < 0f) return "";
            var c = CultureInfo.InvariantCulture;
            if (metres < 1000f) return "visibility " + (Math.Round(metres / 10.0) * 10.0).ToString("0", c) + " m";
            if (metres < 10000f) return "visibility " + (metres / 1000.0).ToString("0.0", c) + " km";
            return "visibility " + Math.Round(metres / 1000.0).ToString("0", c) + " km";
        }

        // Works the number out for a frame.
        internal static float Read(WeatherView view, int frameCount, bool haveSample, float sampleVisibilityM)
        {
            if (view != null && !view.IsStale(frameCount)) return view.VisibilityM;
            return haveSample ? sampleVisibilityM : float.NaN;
        }
    }

    // Publishes the number every frame while the export is on.
    [AtmospheresModule(64)]
    internal sealed class VisibilityExportModule : IAtmospheresModule
    {
        public void Init()
        {
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
        }

        public void Tick(float dt)
        {
            if (!AtmospheresSettings.Enabled || !AtmospheresSettings.WeatherEffects || !AtmospheresSettings.VisibilityExport)
            {
                VisibilityExport.VisibilityM = float.NaN;
                return;
            }
            Vessel v = GameGlobals.ActiveVessel;
            if (v == null || v.mainBody == null)
            {
                VisibilityExport.VisibilityM = float.NaN;
                return;
            }
            WeatherSample s;
            bool have = false;
            if (WeatherHooks.View.IsStale(UnityEngine.Time.frameCount))
            {
                have = WeatherHub.Sample(v.mainBody.flightGlobalsIndex, v.latitude, v.longitude, v.altitude, Planetarium.GetUniversalTime(), out s);
            }
            else
            {
                s = default(WeatherSample);
            }
            VisibilityExport.VisibilityM = VisibilityExport.Read(WeatherHooks.View, UnityEngine.Time.frameCount, have, s.VisibilityM);
        }

        public void Shutdown()
        {
            VisibilityExport.VisibilityM = float.NaN;
        }
    }
}
