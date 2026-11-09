using ReDefinition.Core;

namespace ReDefinition.Autopilot
{
    // The switches that change how a route is planned and flown, in one place. The game's probes set them to try other values and to compare; a player's
    // game runs on the defaults. Nothing else in the autopilot reads the command line.
    internal static class RouteTuning
    {
        // The height over the measured ground a vessel is set down at, added, in m, and whether the game's own ground positioning is skipped.
        internal static double ExtraClearance;
        internal static bool TrustHeight = true;
        // Whether the ground is measured with rays before a vessel is unpacked.
        internal static bool Measure = true;
        // The touchdown at a given place instead of where the descent meets the ground, with its search radius in m (negative for the usual one), and a scan of
        // the steepest ground near the place.
        internal static bool FixedSite;
        internal static double SiteLatitude, SiteLongitude, SiteRadius = -1.0;
        internal static bool ScanSteepest;
        // Factors on what a pass through the air may load the vessel with, and on how hot its parts read.
        internal static readonly double HeatLimitScale = CommandLine.Float("-redefinitionProbeHeatLimitScale", 1f);
        internal static readonly double PressureLimitScale = CommandLine.Float("-redefinitionProbePressureLimitScale", 1f);
        internal static readonly double HeatShareScale = CommandLine.Float("-redefinitionProbeHeatShareScale", 1f);
        // The flight does not ask for the ground under the vessel with a ray; the route's meeting docks where the target is a vessel; Parsek is not asked
        // to record the flight.
        // The air is used for a stop that asks for it, also where the check of the pass would refuse it: a probe vessel without a heat shield
        // can then fly the aerobrake's wait for the periapsis.
        internal static bool AirAlways
        {
            get { return CommandLine.Has("-redefinitionProbeAirAlways"); }
        }

        internal static bool NoGroundRay
        {
            get { return CommandLine.Has("-redefinitionProbeNoGroundRay"); }
        }

        internal static bool DockWithVessel
        {
            get { return CommandLine.Has("-redefinitionProbeDock"); }
        }

        // MechJeb is taken as not there, to see what the autopilot does without it.
        internal static bool NoMechJeb
        {
            get { return CommandLine.Has("-redefinitionNoMechJeb"); }
        }

        internal static bool NoParsekRecording
        {
            get { return CommandLine.Has("-redefinitionNoParsekRecording"); }
        }
    }
}
