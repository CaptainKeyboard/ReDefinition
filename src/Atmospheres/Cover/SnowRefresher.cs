using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Puts the cover mod into the ground of every body that carries cover and keeps the ground that is already built in step with the
    // field (CoverRepainter). The component keeps the name of the single-body snow refresher it replaces, because the start-up file
    // adds it by type (a rename is a request to the owner of that file). Does nothing while ground cover is off; switching it off puts
    // the colours back that the quads had before.
    internal sealed class SnowRefresher : MonoBehaviour
    {
        // Seconds of real time between two looks for the bodies' ground while the mod is not in it.
        private const float InstallPollSeconds = 1f;

        // The least real time between two rebuilds of a sphere that was built before the mod was added, in seconds.
        internal const float RebuildMinSeconds = 120f;

        private sealed class Installed
        {
            internal CelestialBody Body;
            internal PQS Sphere;
            internal CoverPqsMod Mod;
            internal bool RebuildWanted;
            internal float LastRebuild = -1000f;
            internal bool WaitLogged;
        }

        private readonly Dictionary<int, Installed> installed = new Dictionary<int, Installed>();
        private readonly CoverRepainter repainter = new CoverRepainter();
        private float nextInstallTry;
        private bool restored = true;
        private int installs;

        // The number of bodies the mod is in, for the probe.
        internal int InstalledCount { get { return installed.Count; } }

        // A rebuild destroys every quad, so it runs only where nothing stands on them: the sphere not started, or in flight or the
        // tracking station with the sphere inactive (the view is in space). Never in the space centre, whose buildings sit on it.
        internal static bool SafeToRebuild(bool sphereExists, bool started, GameScenes scene, bool active)
        {
            if (!sphereExists) return false;
            if (!started) return true;
            return (scene == GameScenes.FLIGHT || scene == GameScenes.TRACKSTATION) && !active;
        }

        private void Update()
        {
            if (!AtmospheresSettings.Enabled || !AtmospheresSettings.GroundCover)
            {
                if (!restored && CoverQuads.Count > 0) restored = repainter.RestoreStep();
                else if (CoverQuads.Count == 0) restored = true;
                return;
            }
            restored = false;
            float now = Time.realtimeSinceStartup;
            if (now >= nextInstallTry)
            {
                nextInstallTry = now + InstallPollSeconds;
                TryInstall();
            }
            if (installed.Count == 0) return;
            foreach (Installed i in installed.Values)
            {
                if (i.Sphere != null && i.Sphere.isStarted && i.Sphere.isActive) CoverModule.Request(i.Body);
                Rebuild(i, now);
            }
            repainter.Step(now);
        }

        // Adds the cover mod as a child of the PQS of every body that has cover and a sphere; the sphere takes the child mods it finds
        // when it sets up. A body whose sphere was made anew (the mod is gone) gets it again.
        private void TryInstall()
        {
            List<CelestialBody> bodies = GameGlobals.Bodies;
            if (bodies == null) return;
            for (int b = 0; b < bodies.Count; b++)
            {
                CelestialBody body = bodies[b];
                if (body == null || body.pqsController == null) continue;
                Installed existing;
                if (installed.TryGetValue(body.flightGlobalsIndex, out existing) && existing.Mod != null && existing.Sphere == body.pqsController) continue;
                CoverBodyState state = CoverBodies.Of(body);
                if (state == null) continue;
                Install(body, state, existing != null);
            }
        }

        private void Install(CelestialBody body, CoverBodyState state, bool again)
        {
            PQS pqs = body.pqsController;
            // The records belong to the sphere that was there before.
            CoverQuads.RemoveBody(state);
            var holder = new GameObject("ReDefinitionAtmospheresCover");
            holder.transform.SetParent(pqs.transform, false);
            holder.layer = pqs.gameObject.layer;
            var mod = holder.AddComponent<CoverPqsMod>();
            mod.order = CoverPqsMod.SortOrder;
            mod.sphere = pqs;
            mod.State = state;
            installs++;
            installed[body.flightGlobalsIndex] = new Installed { Body = body, Sphere = pqs, Mod = mod, RebuildWanted = pqs.isStarted };
            AtmospheresLog.Say("cover PQS mod " + (again ? "added again to " : "added to ") + body.name + ", order " + CoverPqsMod.SortOrder + ", layer "
                           + state.Def.Id + " (" + state.Def.Kind + "), sphere " + (pqs.isStarted ? "already started" : "not started yet") + ", " + ParallaxLink.Summary);
            if (pqs.isStarted)
                AtmospheresLog.Say("the ground of " + body.name + " was built before the cover mod was added; it is rebuilt when no vessel stands on it.");
        }

        // A sphere that was already started when the mod was added is built once more, where that is safe.
        private void Rebuild(Installed i, float now)
        {
            if (!i.RebuildWanted || i.Sphere == null) return;
            if (now - i.LastRebuild < RebuildMinSeconds) return;
            if (!SafeToRebuild(true, i.Sphere.isStarted, HighLogic.LoadedScene, i.Sphere.isActive))
            {
                if (!i.WaitLogged)
                {
                    i.WaitLogged = true;
                    AtmospheresLog.Say("a ground rebuild of " + i.Body.name + " is due and waits for a moment when no vessel is on the ground (scene "
                                   + HighLogic.LoadedScene + ", sphere " + (i.Sphere.isActive ? "active" : "inactive") + ").");
                }
                return;
            }
            i.WaitLogged = false;
            i.RebuildWanted = false;
            i.LastRebuild = now;
            // A sphere that is not started builds with the mod when it starts; resetting it would start it.
            if (!i.Sphere.isStarted) return;
            i.Sphere.RebuildSphere();
            AtmospheresLog.Say("ground rebuild of " + i.Body.name + " for the cover (scene " + HighLogic.LoadedScene + ").");
        }
    }
}
