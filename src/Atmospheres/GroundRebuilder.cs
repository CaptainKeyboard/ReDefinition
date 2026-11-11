using System.Globalization;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Rebuild ground refresh: builds Kerbin's terrain anew when the season has moved a lot, or once after the snow mod was added to
    // ground that was already built, and only where no vessel needs the ground. SnowRefresher calls Step every frame. The decisions
    // (when a rebuild is due, where it is safe) are static methods.
    internal sealed class GroundRebuilder
    {
        // The change of the phase that triggers a rebuild, and the least real time between two.
        internal const double RebuildPhaseTrigger = 0.02;
        internal const float RebuildMinSeconds = 120f;

        private readonly PQS sphere;
        private double builtPhase;
        private float lastRebuildAt = -1000f;
        private bool wanted;
        private bool waitLogged;

        // sphere is Kerbin's PQS; phase the phase the ground was built for (NaN where there was no clock); wantedNow asks for a rebuild
        // whatever the phase.
        internal GroundRebuilder(PQS sphere, double? phase, bool wantedNow)
        {
            this.sphere = sphere;
            builtPhase = phase.HasValue ? phase.Value : double.NaN;
            wanted = wantedNow;
        }

        // Whether a rebuild is due: asked for, or the mode is Rebuild and the phase has moved past the trigger and the last rebuild is
        // old enough. A ground built without a phase (NaN) is due as soon as there is one.
        internal static bool Due(bool wantedNow, bool modeWants, double phase, double builtPhase, float secondsSinceLast)
        {
            if (wantedNow) return true;
            if (!modeWants) return false;
            if (!double.IsNaN(builtPhase) && SeasonClock.PhaseDistance(phase, builtPhase) <= RebuildPhaseTrigger) return false;
            return secondsSinceLast >= RebuildMinSeconds;
        }

        // A rebuild destroys every quad, so it runs only where nothing stands on them: the sphere not started, or in flight or the
        // tracking station with the sphere inactive (the view is in space). Never in the space centre, whose buildings sit on it.
        internal static bool SafeToRebuild(bool sphereExists, bool started, GameScenes scene, bool active)
        {
            if (!sphereExists) return false;
            if (!started) return true;
            return (scene == GameScenes.FLIGHT || scene == GameScenes.TRACKSTATION) && !active;
        }

        internal void Step(float now, bool modeWants)
        {
            if (sphere == null) return;
            if (!wanted && !modeWants) return;
            double? clock = SeasonState.Phase();
            if (!clock.HasValue) return;
            double phase = clock.Value;
            if (!Due(wanted, modeWants, phase, builtPhase, now - lastRebuildAt)) return;
            if (!SafeToRebuild(true, sphere.isStarted, HighLogic.LoadedScene, sphere.isActive))
            {
                if (!waitLogged)
                {
                    waitLogged = true;
                    AtmospheresLog.Say("a ground rebuild is due and waits for a moment when no vessel is on the ground (scene "
                                   + HighLogic.LoadedScene + ", sphere " + (sphere.isActive ? "active" : "inactive") + ").");
                }
                return;
            }
            waitLogged = false;
            wanted = false;
            lastRebuildAt = now;
            builtPhase = phase;
            // A sphere that is not started builds with the snow mod when it starts; resetting it would start it.
            if (!sphere.isStarted) return;
            sphere.RebuildSphere();
            AtmospheresLog.Say("ground rebuild for phase " + phase.ToString("0.000", CultureInfo.InvariantCulture) + " (scene " + HighLogic.LoadedScene + ").");
        }
    }
}
