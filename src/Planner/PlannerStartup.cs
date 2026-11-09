using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Planner
{
    // Where no host is present, the planner's own settings file answers the two switches the shared seams ask.
    // The host assigns its seams in its own Awake, also at Startup.Instantly, and wins whichever add-on runs first.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    internal sealed class PlannerStartup : MonoBehaviour
    {
        private void Awake()
        {
            if (!HostSeams.StandsAlone()) return;
            HostSeams.BackgroundRoute = () => { PlannerSettings.Load(); return PlannerSettings.BackgroundRoute; };
            HostSeams.WorldButtons = () => { PlannerSettings.Load(); return PlannerSettings.WorldButtons; };
        }
    }
}
