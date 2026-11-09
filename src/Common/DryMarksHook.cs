using System.Collections.Generic;

namespace ReDefinition.Core
{
    // What the host asks of the planner's simulation for the dashboard's resource tapes: where engines run dry.
    // An abstract class with a default, for the same reason as GuidanceHook. The default marks nothing.
    public abstract class DryMarksHook
    {
        // Per propellant, the totals of it at which engines burning it run dry, in order, for the vessel as it flies.
        // Null where the simulation cannot tell, such as without KSP's delta-v stages.
        public virtual Dictionary<string, List<object>> Marks(Vessel vessel) { return null; }
    }
}
