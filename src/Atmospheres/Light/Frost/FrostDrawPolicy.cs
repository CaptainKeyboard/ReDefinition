namespace ReDefinition.Atmospheres
{
    // Which way the frost is drawn. Pure.
    //
    //   host        the composite on the upscaled image, from the main mod's after-upscaling hook: the upscaler never sees the frost.
    //   quad        a scene-side overlay drawn before the upscaler. Without an upscaler it is harmless; with one it is part of the moving
    //               scene, has no motion vectors and no reactive mask, so the upscaler smears it.
    //   suppressed  no frost: a view the host composite cannot serve while an upscaler runs, unless the quad is allowed there.
    internal static class FrostDrawPolicy
    {
        internal const string Host = "host", Quad = "quad", Suppressed = "suppressed";

        // Whether the quad may be drawn: always where no upscaler runs on the picture, otherwise only when frost.quadWithUpscaler says so.
        internal static bool QuadAllowed(bool upscalerRunning, bool quadWithUpscaler)
        {
            return !upscalerRunning || quadWithUpscaler;
        }

        internal static string Mode(bool hostUsable, bool quadAllowed)
        {
            return hostUsable ? Host : quadAllowed ? Quad : Suppressed;
        }
    }
}
