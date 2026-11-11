using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The orbit painter's tick: once a frame it picks the bodies the orbit and map view show (orbitBodies Focused, Visible or All: the
    // focused body first), calls the overlay host for them (OrbitOverlayHost.DrawRound, which draws every registered layer: this cover,
    // the cloud shell, the ice) and gives the textures of the bodies back that were not picked for a while. The component keeps the
    // name of the single-body orbit snow it replaces, because the start-up file adds it by type. It registers the cover layer
    // (OrbitCover) with the host on its first tick.
    internal sealed class OrbitSnow : MonoBehaviour
    {
        // Seconds of real time between two looks at the list of bodies.
        private const float ListSeconds = 2f;

        // A body that was not picked for this long gives its textures back, in seconds of real time.
        internal const float ReleaseAfterSeconds = 10f;

        private readonly OrbitCover cover = new OrbitCover();
        private readonly List<CelestialBody> candidates = new List<CelestialBody>();
        private readonly Dictionary<int, float> pickedAt = new Dictionary<int, float>();
        private readonly List<int> scratch = new List<int>();
        private float nextList;
        private bool registered;

        // The cover layer, for the probe.
        internal OrbitCover Cover { get { return cover; } }

        private void Update()
        {
            if (!AtmospheresSettings.Enabled || !AtmospheresSettings.OrbitOverlay || !AtmospheresSettings.GroundCover)
            {
                if (cover.ActiveBodies > 0) cover.ReleaseAll();
                return;
            }
            try
            {
                if (!registered)
                {
                    OrbitOverlayLayers.Register(cover);
                    registered = true;
                }
                float now = Time.realtimeSinceStartup;
                if (now >= nextList)
                {
                    nextList = now + ListSeconds;
                    List<CelestialBody> bodies = GameGlobals.Bodies;
                    candidates.Clear();
                    if (bodies != null)
                        for (int i = 0; i < bodies.Count; i++)
                            if (bodies[i] != null && bodies[i].scaledBody != null) candidates.Add(bodies[i]);
                }
                if (candidates.Count == 0) return;
                double ut;
                if (!CoverClock.TryUt(out ut)) ut = 0;
                List<int> picked = OrbitOverlayHost.DrawRound(candidates, FocusBody(), AtmospheresSettings.OrbitBodies, Visible, ut);
                foreach (int i in picked) pickedAt[candidates[i].flightGlobalsIndex] = now;
                scratch.Clear();
                foreach (KeyValuePair<int, float> pair in pickedAt)
                    if (now - pair.Value > ReleaseAfterSeconds) scratch.Add(pair.Key);
                foreach (int key in scratch)
                {
                    pickedAt.Remove(key);
                    cover.Release(key);
                }
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("atmospheres-orbit-tick", "the orbit overlay tick failed", e);
            }
        }

        private void OnDestroy()
        {
            cover.ReleaseAll();
            OrbitOverlayLayers.Unregister(cover);
        }

        private void OnApplicationQuit()
        {
            cover.ReleaseAll();
        }

        // The body in focus: the map view's or the tracking station's target (a vessel's main body), else the active body of the flight.
        private static CelestialBody FocusBody()
        {
            try
            {
                if ((MapView.MapIsEnabled || HighLogic.LoadedScene == GameScenes.TRACKSTATION) && PlanetariumCamera.fetch != null && PlanetariumCamera.fetch.target != null)
                {
                    MapObject target = PlanetariumCamera.fetch.target;
                    if (target.celestialBody != null) return target.celestialBody;
                    if (target.vessel != null) return target.vessel.mainBody;
                }
            }
            catch (Exception)
            {
                // No planetarium camera in this scene.
            }
            return BodyWatcher.ActiveBody;
        }

        // Whether a body's scaled planet is seen by a camera now.
        private static bool Visible(CelestialBody body)
        {
            if (body == null || body.scaledBody == null || !body.scaledBody.activeInHierarchy) return false;
            MeshRenderer r = body.scaledBody.GetComponent<MeshRenderer>();
            return r != null && r.isVisible;
        }
    }
}
