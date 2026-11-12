using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Puts the own cloud system's optical depth into WeatherHooks.CloudOpticalDepth (package T04). The contract lets the host write the
    // hook when EVE's decks are present; under cloudSource = Ours the atmospheres answer it themselves from the own layers, the one
    // definition of the vertical optical depth (the uncalibrated CloudDensityEstimate stays the answer for EVE's decks only). T09 calls
    // Attach when the own system turns on for a body and Detach when it turns off. The hook is "whoever assigns last wins": Attach does
    // not take it from another writer, so under cloudSource = Both the host's reader (EVE's decks) keeps it until T09 decides the sum.
    internal static class OwnCloudHook
    {
        // True when the hook is ours after the call.
        internal static bool Attach()
        {
            if (WeatherHooks.CloudOpticalDepth == null || ReferenceEquals(WeatherHooks.CloudOpticalDepth, OwnCloudOpticalDepth.Reader))
            {
                WeatherHooks.CloudOpticalDepth = OwnCloudOpticalDepth.Reader;
                return true;
            }
            return false;
        }

        internal static void Detach()
        {
            if (ReferenceEquals(WeatherHooks.CloudOpticalDepth, OwnCloudOpticalDepth.Reader)) WeatherHooks.CloudOpticalDepth = null;
            OwnCloudOpticalDepth.Bind(null);
        }
    }
}
