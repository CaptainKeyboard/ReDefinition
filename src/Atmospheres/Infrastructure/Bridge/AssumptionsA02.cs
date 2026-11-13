using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // What the bridge to the main mod relies on: ReDefinition.Api as the game has it. The entry is Verified where the members the
    // atmospheres call are there; where the main mod is absent it is Absent, and the atmospheres do not start (HostGate).
    [AtmospheresAssumptions]
    internal static class AssumptionsA02
    {
        public static readonly ForeignAssumption HostApi =
            new ForeignAssumption("atmo-host-api", "ReDefinition", "the main mod's after-upscaling, motion-vector and overlay hooks")
                .Type(ApiBridge.HooksTypeName)
                .Calls(ApiBridge.HooksTypeName, "RegisterAfterUpscaling")
                .Calls(ApiBridge.HooksTypeName, "UnregisterAfterUpscaling")
                .Calls(ApiBridge.HooksTypeName, "RegisterAfterUpscalingWithDepth")
                .Calls(ApiBridge.HooksTypeName, "UnregisterAfterUpscalingWithDepth")
                .Calls(ApiBridge.HooksTypeName, "RegisterMotionVectors")
                .Calls(ApiBridge.HooksTypeName, "UnregisterMotionVectors")
                .Calls(ApiBridge.HooksTypeName, "RegisterOverlay")
                .Calls(ApiBridge.HooksTypeName, "UnregisterOverlay")
                .Calls(ApiBridge.FrameTypeName, "RequestHistoryReset")
                .Calls(ForecastKey.KeysTypeName, "Pressed")
                .Field(HostGate.ApiInfoTypeName, "Version", "System.Int32")
                .OnChange(WhenChanged.Keep, "the atmospheres do not start (HostGate); the same members are what the gate checks");
    }
}
