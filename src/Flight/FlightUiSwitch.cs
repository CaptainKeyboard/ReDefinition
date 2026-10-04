using System;

namespace ReDefinition.Flight
{
    // Whether a feature of the flight UI is on (OwnSettings, OurModules.FlightUi):
    // as the settings have it, and on where the add-on is not there to say.
    internal static class FlightUiSwitch
    {
        internal static bool On(Func<OwnSettings, bool> feature)
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            return addon == null || feature(addon.Live);
        }
    }
}
