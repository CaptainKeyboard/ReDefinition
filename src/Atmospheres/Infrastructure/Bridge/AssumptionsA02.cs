using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // What the bridge to the main mod relies on: ReDefinition.Api as the game has it. The entry is Verified where the hooks the
    // atmospheres register are there; where the main mod is absent it is Absent, and the Scene-mode fallback draws.
    [AtmospheresAssumptions]
    internal static class AssumptionsA02
    {
        public static readonly ForeignAssumption HostApi =
            new ForeignAssumption("atmo-host-api", "ReDefinition", "the main mod's after-upscaling, motion-vector and overlay hooks")
                .Type(ApiBridge.HooksTypeName)
                .Calls(ApiBridge.HooksTypeName, "RegisterAfterUpscaling")
                .Calls(ApiBridge.HooksTypeName, "UnregisterAfterUpscaling")
                .Calls(ApiBridge.HooksTypeName, "RegisterMotionVectors")
                .Calls(ApiBridge.HooksTypeName, "RegisterOverlay")
                .OnChange(WhenChanged.Keep, "effects that must be drawn after the upscaler draw in Scene mode");
    }
}
