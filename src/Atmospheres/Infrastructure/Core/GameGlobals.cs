using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // The statics of FlightGlobals that need the FlightGlobals object (ActiveVessel, Bodies, VesselsLoaded, GetHomeBody) throw a
    // NullReferenceException while it does not exist: in the main menu, while a scene loads and for a frame after a scene is left. A
    // module tick reads them through this class and gets null instead. Present is the seam: the unit tests replace it to play the
    // absent game.
    internal static class GameGlobals
    {
        private static readonly Func<bool> GameDefault = () => FlightGlobals.fetch != null;
        private static Func<bool> present = GameDefault;

        // Whether the FlightGlobals object exists.
        internal static bool Present
        {
            get { return present(); }
        }

        // Replaces the presence test (tests only); null restores the game's.
        internal static void SetPresence(Func<bool> test)
        {
            present = test ?? GameDefault;
        }

        // The active vessel, or null when there is no FlightGlobals or no active vessel.
        internal static Vessel ActiveVessel
        {
            get { return Present ? FlightGlobals.ActiveVessel : null; }
        }

        // The bodies, or null when there is no FlightGlobals.
        internal static List<CelestialBody> Bodies
        {
            get { return Present ? FlightGlobals.Bodies : null; }
        }

        // The loaded vessels, or null when there is no FlightGlobals.
        internal static List<Vessel> VesselsLoaded
        {
            get { return Present ? FlightGlobals.VesselsLoaded : null; }
        }

        // The home body, or null when there is no FlightGlobals.
        internal static CelestialBody HomeBody
        {
            get { return Present && FlightGlobals.Bodies != null ? FlightGlobals.GetHomeBody() : null; }
        }

        // The sun (body 0), or null.
        internal static CelestialBody Sun
        {
            get
            {
                List<CelestialBody> bodies = Bodies;
                return bodies != null && bodies.Count > 0 ? bodies[0] : null;
            }
        }
    }
}
