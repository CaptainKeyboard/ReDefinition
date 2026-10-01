namespace ReDefinition.Upscaler
{
    // The one value of the game's rig that the FSR 3 port reads (Fsr3UpscalerContext's
    // check of the sharpness), as src/Upscaler/UpscalerRig.cs has it.
    internal static class UpscalerRig
    {
        internal const float MaximumSharpness = 1.2f;
    }
}
