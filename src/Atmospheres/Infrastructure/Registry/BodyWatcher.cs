using System;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The rule that decides when the active body changes, without the game: a new body must be seen for HoldSeconds before it counts, a
    // scene load counts at once, and the weather of the old body ramps to nothing over RampSeconds after the change. Bodies are indexes
    // (FlightGlobals index), -1 is none.
    internal sealed class BodyHysteresis
    {
        internal const float HoldSeconds = 5f;
        internal const float RampSeconds = 1f;

        private int active = -1;
        private int candidate = -1;
        private float candidateSince;
        private int previous = -1;
        private float switchedAt = float.NegativeInfinity;

        // The active body.
        internal int Active
        {
            get { return active; }
        }

        // The body that was active before the last change, which the ramp still fades.
        internal int Previous
        {
            get { return previous; }
        }

        // Looks at the body seen at time now. immediate is true on a scene load. True when the active body changed; the old one is then
        // in Previous.
        internal bool Observe(float now, int seen, bool immediate)
        {
            if (seen == active)
            {
                candidate = -1;
                return false;
            }
            if (active < 0 || immediate)
            {
                Switch(now, seen, immediate);
                return true;
            }
            if (seen != candidate)
            {
                candidate = seen;
                candidateSince = now;
                return false;
            }
            if (now - candidateSince < HoldSeconds) return false;
            Switch(now, seen, false);
            return true;
        }

        // The intensity factor from 0 to 1 that the old body's weather is multiplied by: 1 before a change, then falling to 0 over
        // RampSeconds. After a scene load it is 0 at once (the old body's objects are gone).
        internal float OldBodyIntensity(float now)
        {
            float t = (now - switchedAt) / RampSeconds;
            return t <= 0f ? (previous < 0 ? 0f : 1f) : t >= 1f ? 0f : 1f - t;
        }

        private void Switch(float now, int seen, bool immediate)
        {
            previous = active;
            active = seen;
            candidate = -1;
            // A scene load leaves no old body to fade.
            switchedAt = immediate ? float.NegativeInfinity : now;
        }
    }

    // Watches which body the weather is for (the flying vessel's main body, else the body of the camera) twice a second and tells the
    // modules when it changes. A vessel hopping the edge of a sphere of influence does not flap: the change counts after HoldSeconds,
    // except on a scene load. The simulation host persists across scenes; only the modules' attachments follow the body. AtmospheresStartup
    // adds the component once and it lives across scenes.
    internal sealed class BodyWatcher : MonoBehaviour
    {
        private const float LookEverySeconds = 0.5f;

        private static readonly BodyHysteresis Rule = new BodyHysteresis();
        private static CelestialBody activeBody;
        private float nextLook;
        private bool sceneLoaded = true;

        // The active body, or null.
        internal static CelestialBody ActiveBody
        {
            get { return activeBody; }
        }

        // The factor the old body's weather runs at while it fades (1 to 0 over a second); 0 when there is no fading body.
        internal static float OldBodyIntensity
        {
            get { return Rule.OldBodyIntensity(Time.unscaledTime); }
        }

        // Raised once when the active body changed (old, new), on the main thread.
        internal static event Action<CelestialBody, CelestialBody> Changed;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
        }

        private void OnDestroy()
        {
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
        }

        private void OnSceneLoadRequested(GameScenes scene)
        {
            sceneLoaded = true;
            nextLook = 0f;
        }

        private void Update()
        {
            float now = Time.unscaledTime;
            if (now < nextLook) return;
            nextLook = now + LookEverySeconds;
            CelestialBody seen = SeenBody();
            int index = seen == null ? -1 : seen.flightGlobalsIndex;
            bool immediate = sceneLoaded;
            sceneLoaded = false;
            if (!Rule.Observe(now, index, immediate)) return;
            CelestialBody old = activeBody;
            activeBody = seen;
            Action<CelestialBody, CelestialBody> handlers = Changed;
            if (handlers != null) handlers(old, seen);
        }

        // The body the weather is for: in flight the main body of the vessel, elsewhere the body the camera looks at.
        private static CelestialBody SeenBody()
        {
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.currentMainBody != null) return FlightGlobals.currentMainBody;
            if (HighLogic.LoadedScene == GameScenes.SPACECENTER || HighLogic.LoadedSceneIsEditor)
            {
                return FlightGlobals.GetHomeBody();
            }
            return FlightGlobals.currentMainBody ?? FlightGlobals.GetHomeBody();
        }
    }
}
