namespace ReDefinition.Atmospheres.Audio
{
    // The CPU budget of the audio thread, defined by the build that is measured. The plan's budget (07-audio, section 5) is 1 % of one core
    // for all layers; its fail line is a mean block time of more than 5 % of the block duration. A Debug build of the plugin costs several
    // times a Release build, and the series builds Debug unless it is told otherwise, so the 1 % budget applies to a Release build only and
    // a Debug build is held to the fail line. The row reads the used share of the limit of its own build, so it can fail in both.
    internal static class CpuGate
    {
        internal const double ReleaseLimitPct = 1.0;
        internal const double DebugLimitPct = 5.0;

        // Whether this assembly is a Release build.
        internal static bool IsReleaseBuild
        {
            get
            {
#if DEBUG
                return false;
#else
                return true;
#endif
            }
        }

        internal static double LimitPct(bool release)
        {
            return release ? ReleaseLimitPct : DebugLimitPct;
        }

        // The mean block cost in percent of one core over the limit of the build: 1 or below is within the budget.
        internal static double BudgetUsed(double pctOfCore, bool release)
        {
            return pctOfCore / LimitPct(release);
        }
    }
}
