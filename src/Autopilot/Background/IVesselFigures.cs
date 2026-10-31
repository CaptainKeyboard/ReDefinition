using System.Collections.Generic;

namespace ReDefinition.Autopilot.Background
{
    // The burns of a plan shared over the vessel's stages: what each part keeps of its propellant, and which parts are let go when a stage is spent.
    internal interface IBurnShare
    {
        bool Ready { get; }
        // The delta-v asked for that no stage had left, in m/s, and the time the engines have burned so far, in s.
        double Short { get; }
        double Elapsed { get; }
        // A burn of `deltaV` m/s: the share each part keeps (multiplied into `keeps`, by persistent id) and the parts let go (added to `gone`).
        void Burn(double deltaV, Dictionary<uint, double> keeps, List<uint> gone);
    }

    // What the engineer knows of a vessel that the background route needs: its stages, and what a launch from a body costs.
    internal interface IVesselFigures
    {
        IBurnShare StagesOf(Vessel vessel);
        // The delta-v of a launch from the body's surface into a circular orbit at `altitude`, in m/s.
        double LaunchDeltaV(CelestialBody body, double altitude);
    }
}
