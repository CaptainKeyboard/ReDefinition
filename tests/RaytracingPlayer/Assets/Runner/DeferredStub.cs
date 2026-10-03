namespace Deferred
{
    // Deferred's main type, as ForeignAssumptions.DeferredReflections looks for it: the
    // player sets Deferred's shaders and globals itself (RtPlayerRunner.LoadDeferred),
    // and this assembly names the globals as Deferred's does. It stands for the mod,
    // which needs KSP to load.
    internal static class Deferred
    {
    }
}
