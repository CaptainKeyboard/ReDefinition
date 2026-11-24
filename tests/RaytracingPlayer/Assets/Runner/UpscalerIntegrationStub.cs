namespace ReDefinition.Upscaler
{
    // The one value of the game's integration that the FSR 3 port reads (Fsr3UpscalerContext's
    // check of the sharpness), as src/Upscaler/Integration/UpscalerIntegration.cs has it.
    internal static class UpscalerIntegration
    {
        // The integration's ceiling for the sharpness, which RaytracingPlayerStubTests checks against the integration's own.
        internal const float MaximumSharpness = 1.2f;
    }
}
