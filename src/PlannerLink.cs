using ReDefinition.Hud;
using ReDefinition.Planner;
using ReDefinition.Scaling;
using ReDefinition.Ui;

namespace ReDefinition
{
    // What ReDefinition gives the planner (ReDefinition.Planner) when both are installed: its settings, its scale, the space centre's buttons and the
    // click shields. The planner works without any of it.
    internal static class PlannerLink
    {
        // From the addon's Awake.
        internal static void Connect()
        {
            PlannerSeams.StandsAlone = () => false;
            PlannerSeams.WorldScale = () => UiScaling.World;
            PlannerSeams.BackgroundRoute = () => ReDefinitionAddon.Instance == null || ReDefinitionAddon.Instance.Live.BackgroundRoute;
            PlannerSeams.WorldButtons = () => ReDefinitionAddon.Instance == null || ReDefinitionAddon.Instance.Live.WorldButtons;
            PlannerSeams.SpaceCentreButton = name =>
            {
                KscButtons.Look held = KscButtons.Get(name);
                return held == null ? null : new SpaceCentreLook
                {
                    Normal = held.Normal, Highlighted = held.Highlighted, Pressed = held.Pressed, Disabled = held.Disabled,
                };
            };
            PlannerSeams.ShieldDialog = UnityMouseEvents.Shield;
            PlannerSeams.ShieldFrame = UnityMouseEvents.ShieldFrame;
        }
    }
}
