using ReDefinition.Landing;

namespace ReDefinition.Autopilot.Guidance
{
    // Whether the vessel can land on the body it orbits, told before the landing is started. The engines must give more than the gravity. The delta-v on
    // board, as the game works it out stage by stage, must cover the deorbit and the descent as the simulation expends it, with the air's help.
    internal static class LandingCheck
    {
        // False, with the reason, where the landing cannot be done; true with a line of what it needs where it can. Where the simulation gives no
        // landing, or the game no delta-v, there is nothing to say against it.
        internal static bool Possible(Vessel vessel, out string verdict)
        {
            verdict = "";
            if (vessel == null || vessel.mainBody == null) return true;
            string body = vessel.mainBody.bodyName;
            // With no engine lit or made ready the thrust is not known: nothing is said against the landing.
            if (Engines.Acceleration(vessel) <= 0.0)
            {
                verdict = "No engine is lit: the landing on " + body + " cannot be checked.";
                return true;
            }
            double needed = LandingPrediction.DeltaVNeeded(vessel);
            if (double.IsPositiveInfinity(needed) && LandingPrediction.UsesAir(vessel, 0.0))
            {
                // The air and the parachutes brake it; the engines are for the last part.
                verdict = "The engines are weak for " + body + ", the landing relies on the air and the parachutes.";
                return true;
            }
            if (double.IsPositiveInfinity(needed))
            {
                verdict = "The engines are too weak to land on " + body + ": they give " + Engines.Acceleration(vessel).ToString("0.0") + " m/s2 against "
                          + (vessel.mainBody.GeeASL * 9.81).ToString("0.0") + " m/s2 of gravity.";
                return false;
            }
            if (double.IsNaN(needed) || vessel.VesselDeltaV == null) return true;
            double have = vessel.VesselDeltaV.TotalDeltaVVac;
            // The game has no figure for the delta-v yet.
            if (have <= 0.0) return true;
            if (needed > have)
            {
                verdict = "Not enough delta-v to land on " + body + ": " + needed.ToString("0") + " m/s needed, " + have.ToString("0") + " m/s on board.";
                return false;
            }
            verdict = "Landing on " + body + " needs " + needed.ToString("0") + " m/s of the " + have.ToString("0") + " m/s on board.";
            return true;
        }

        // The warning for a descent that is under way: the delta-v the rest of it expends against what is on board; null where it is covered.
        internal static string Shortage(Vessel vessel, ReentrySimulation.Result result)
        {
            if (vessel == null || result == null || result.Outcome != ReentrySimulation.Outcome.LANDED || vessel.VesselDeltaV == null) return null;
            double have = vessel.VesselDeltaV.TotalDeltaVVac;
            return result.DeltaVExpended > have
                ? "NOT ENOUGH DELTA-V: " + result.DeltaVExpended.ToString("0") + " m/s needed, " + have.ToString("0") + " m/s on board."
                : null;
        }
    }
}
