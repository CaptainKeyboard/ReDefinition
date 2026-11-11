using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Puts the snow mod into Kerbin's ground and keeps the ground that is already built in step with the season. The ground refresh
    // mode decides how. Live paints the built quads again (LiveRepainter), Rebuild builds the terrain anew (GroundRebuilder), Off does
    // neither.
    internal sealed class SnowRefresher : MonoBehaviour
    {
        // Seconds of real time between two looks for Kerbin's ground while the mod is not in it.
        private const float InstallPollSeconds = 1f;

        private SnowPqsMod mod;
        private float nextInstallTry;
        private int installs;
        private readonly LiveRepainter repainter = new LiveRepainter();
        private GroundRebuilder rebuilder;

        private void Update()
        {
            if (!AtmospheresSettings.Enabled) return;
            if (mod == null)
            {
                if (Time.realtimeSinceStartup < nextInstallTry) return;
                nextInstallTry = Time.realtimeSinceStartup + InstallPollSeconds;
                if (!TryInstall()) return;
            }
            float now = Time.realtimeSinceStartup;
            if (AtmospheresSettings.GroundRefreshMode == AtmospheresSettings.GroundRefresh.Live) repainter.Step(now);
            if (rebuilder != null) rebuilder.Step(now, AtmospheresSettings.GroundRefreshMode == AtmospheresSettings.GroundRefresh.Rebuild);
        }

        // Adds the snow mod as a child of Kerbin's PQS; the sphere takes the child mods it finds when it sets up.
        private bool TryInstall()
        {
            if (FlightGlobals.fetch == null || FlightGlobals.Bodies == null) return false;
            CelestialBody kerbin = FlightGlobals.GetBodyByName(SeasonClock.BodyName);
            PQS pqs = kerbin != null ? kerbin.pqsController : null;
            if (pqs == null) return false;
            // The records belong to the sphere that was there before.
            SnowQuads.Clear();
            var holder = new GameObject("ReDefinitionAtmospheresSnow");
            holder.transform.SetParent(pqs.transform, false);
            holder.layer = pqs.gameObject.layer;
            mod = holder.AddComponent<SnowPqsMod>();
            mod.order = SnowPqsMod.SortOrder;
            mod.sphere = pqs;
            installs++;
            // A sphere that is already started was built before the mod was there: it gets its snow at the next rebuild.
            rebuilder = new GroundRebuilder(pqs, SeasonState.Phase(), pqs.isStarted);
            if (installs == 1)
            {
                AtmospheresLog.Say("snow PQS mod added to " + SeasonClock.BodyName + ", order " + SnowPqsMod.SortOrder
                               + ", sphere " + (pqs.isStarted ? "already started" : "not started yet") + ", refresh mode "
                               + EnumNames<AtmospheresSettings.GroundRefresh>.Name(AtmospheresSettings.GroundRefreshMode)
                               + ", " + ParallaxLink.Summary);
            }
            else
            {
                AtmospheresLog.Say("snow PQS mod added again (install " + installs + "): the sphere of " + SeasonClock.BodyName + " was made anew, sphere "
                               + (pqs.isStarted ? "already started" : "not started yet"));
            }
            if (pqs.isStarted)
                AtmospheresLog.Say("the ground was built before the snow mod was added; it gets its snow at the next rebuild.");
            return true;
        }
    }
}
