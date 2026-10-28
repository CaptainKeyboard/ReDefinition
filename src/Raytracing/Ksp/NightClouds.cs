namespace ReDefinition.Raytracing
{
    // Whether the clouds can take the moon's light at all: Scatterer's cloud light is hooked (EveCloudMoonlight.InstallHook).
    // The Night clouds row asks, and is greyed where it is not. Free of Unity, for the tests.
    internal static class NightClouds
    {
        internal static bool Hooked;
    }
}
