using ReDefinition.Core;
using ReDefinition.Raytracing;

namespace ReDefinition.Upscaler
{
    // The host's answer to WeatherHooks.CloudOpticalDepth: the vertical optical depth of EVE's deck over a place of the main body
    // (CloudDensityEstimate through RtEveClouds). The hook is null where EVE has no cloud deck for the atmospheres to read, which the
    // contract says means "none": it is set while EVE's cloud manager is loaded and cleared otherwise.
    internal static class EveOpticalDepthLink
    {
        // The delegate handed out, made once.
        private static readonly System.Func<double, double, double, double, float> reader = Read;

        // Every frame, from HostLink.FillCaps. EVE's presence is a type lookup the lookup caches.
        internal static void Refresh()
        {
            bool eve = TypeLookup.Find("Atmosphere.CloudsManager") != null;
            if (eve && WeatherHooks.CloudOpticalDepth == null) WeatherHooks.CloudOpticalDepth = reader;
            else if (!eve && ReferenceEquals(WeatherHooks.CloudOpticalDepth, reader)) WeatherHooks.CloudOpticalDepth = null;
        }

        // latitude and longitude in degrees, altitude in metres above the surface, ut unused (the deck's own time fade is current).
        private static float Read(double latitude, double longitude, double altitude, double ut)
        {
            return RtEveClouds.OpticalDepth(FlightGlobals.currentMainBody, latitude, longitude, altitude);
        }
    }
}
