using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // What the wind and solar packages rely on in other code: the ModularFlightIntegrator override slot, the stock aerodynamics' fields
    // and the stock solar panel's efficiency curve. Each is Absent where the mod is, Verified where the names are there.
    [AtmospheresAssumptions]
    internal static class AssumptionsD31
    {
        public static readonly ForeignAssumption ModularFlightIntegrator =
            new ForeignAssumption("atmo-wind-mfi", "ModularFlightIntegrator", "the UpdateAerodynamics override slot the wind goes in through")
                .Type(WindHook.IntegratorType)
                .Calls(WindHook.IntegratorType, "RegisterUpdateAerodynamicsOverride")
                .Calls(WindHook.IntegratorType, "BaseFIUpdateAerodynamics")
                .OnChange(WhenChanged.Fallback, "the wind goes in as a force on the parts");

        public static readonly ForeignAssumption StockAerodynamics =
            new ForeignAssumption("atmo-wind-aero", "Kerbal Space Program", "Part.dragVector as the stock aerodynamics set it from the rigidbody velocity")
                .Type("Part")
                .Field("Part", "dragVector", "UnityEngine.Vector3")
                .Field("Part", "dragScalar", "System.Single")
                .Field("Part", "dynamicPressurekPa", "System.Double")
                .OnChange(WhenChanged.Fallback, "the wind forces are off");

        public static readonly ForeignAssumption StockSolarPanel =
            new ForeignAssumption("atmo-solar-panel", "Kerbal Space Program", "the stock solar panel's timeEfficCurve, the factor of the dimming")
                .Type("ModuleDeployableSolarPanel")
                .Field("ModuleDeployableSolarPanel", "timeEfficCurve", "FloatCurve")
                .Field("ModuleDeployableSolarPanel", "efficiencyMult", "System.Single")
                .OnChange(WhenChanged.Fallback, "the solar dimming is off");
    }
}
