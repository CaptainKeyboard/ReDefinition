namespace ReDefinition.Atmospheres
{
    // The status row of the wind in the settings window: who owns the base wind and what this mod adds, one sentence for each cell of the
    // mode table (windSource Auto, Ours, Foreign, Off, each with a wind mod that applies a base wind present or absent). The wind package
    // (D31) decides the single applier; the sentences here say what its table does and are checked against the same cells.
    internal static class WindStatus
    {
        // The sentence for a mode. foreignOwner is the name of the wind mod that applies a base wind, or empty for none.
        internal static string Text(WindSourceMode mode, string foreignOwner)
        {
            bool foreign = !string.IsNullOrEmpty(foreignOwner);
            switch (mode)
            {
                case WindSourceMode.Auto:
                    return foreign
                        ? "Wind: " + foreignOwner + " owns the base wind, ours adds the storm delta."
                        : "Wind: ours, base wind and storms.";
                case WindSourceMode.Ours:
                    return foreign
                        ? "Wind: ours replaces " + foreignOwner + "'s where it can; see the log."
                        : "Wind: ours, base wind and storms.";
                case WindSourceMode.Foreign:
                    return foreign
                        ? "Wind: " + foreignOwner + "'s only, ours draws the visuals."
                        : "Wind: no other wind mod found, so no wind forces; visuals only.";
                default:
                    return "Wind: off, ours draws the visuals.";
            }
        }

        // How the wind reaches a vehicle, as it will run: Auto takes Aero where ModularFlightIntegrator is there, else Force; an Aero that
        // cannot run falls back to Force. Empty where the wind is off or does not push vehicles.
        internal static string InjectionText(WindInjection chosen, bool modularFlightIntegrator, bool windForces)
        {
            if (!windForces) return "";
            bool aero = chosen == WindInjection.Aero || (chosen == WindInjection.Auto && modularFlightIntegrator);
            if (aero && !modularFlightIntegrator) return "Pushing: as a force, as ModularFlightIntegrator is not installed.";
            return aero ? "Pushing: through the air speed the drag sees." : "Pushing: as a force on the parts.";
        }
    }
}
