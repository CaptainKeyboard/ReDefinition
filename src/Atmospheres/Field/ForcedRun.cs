namespace ReDefinition.Atmospheres
{
    // Whether a forced run is going: the test decorator ForcedField (ReDefinition.Atmospheres.Probes.dll, cleanup 2 C) counts itself here while it is attached, and the
    // writers of side files (the tracks, the vehicle cover) ask AllowSideFileWrite before they write, so that a forced run never writes side files.
    internal static class ForcedRun
    {
        internal static int Attached;

        internal static bool AllowSideFileWrite()
        {
            return Attached == 0;
        }
    }
}
