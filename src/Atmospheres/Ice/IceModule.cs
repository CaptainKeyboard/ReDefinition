using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Runs the sea ice (package D35): the bodies someone asked for lately (the orbit overlay host, the ground overlay, the active body)
    // get their heights read and their coast map made once, the field's ice maps read every few seconds, and the drift stepped; the
    // orbit layer is registered with the overlay host (order 20), and the near-ground overlay is ticked. Does nothing while the master
    // switch, weather effects or seaIce is off, so the game is vanilla. The field is the only source of cold: this module holds no clock
    // and no freeze line.
    [AtmospheresModule(69)]
    internal sealed class IceModule : IAtmospheresModule
    {
        // A body counts as wanted this many real seconds after the last request.
        internal const float WantedSeconds = 10f;

        private static readonly Dictionary<int, float> Requests = new Dictionary<int, float>();
        private static readonly List<int> Scratch = new List<int>();

        internal static IceModule Current { get; private set; }

        internal IceOrbitLayer Orbit { get; private set; }
        internal IceGroundOverlay Ground { get; private set; }

        // Asks for the body's ice to be kept up to date (cheap; call it every frame it is needed).
        internal static void Request(CelestialBody body)
        {
            if (body == null) return;
            Requests[body.flightGlobalsIndex] = Time.realtimeSinceStartup;
        }

        public void Init()
        {
            Requests.Clear();
            IceBodies.Clear();
            Current = this;
            if (Orbit == null) Orbit = new IceOrbitLayer();
            if (Ground == null) Ground = new IceGroundOverlay();
            Ground.ResetFailure();
            Orbit.ReleaseAll();
            OrbitOverlayLayers.Register(Orbit);
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
            Request(newBody);
        }

        public void Tick(float dt)
        {
            bool any = IceTuning.OrbitWanted || IceTuning.GroundOverlayWanted;
            if (!any)
            {
                Orbit.Housekeeping();
                Ground.Tick();
                return;
            }
            double ut;
            if (!CoverClock.TryUt(out ut))
            {
                Ground.Tick();
                return;
            }
            Request(BodyWatcher.ActiveBody);
            List<CelestialBody> bodies = GameGlobals.Bodies;
            if (bodies != null)
            {
                float now = Time.realtimeSinceStartup;
                Scratch.Clear();
                foreach (KeyValuePair<int, float> pair in Requests)
                    if (now - pair.Value > WantedSeconds) Scratch.Add(pair.Key);
                for (int i = 0; i < Scratch.Count; i++) Requests.Remove(Scratch[i]);
                foreach (KeyValuePair<int, float> pair in Requests)
                {
                    if (pair.Key < 0 || pair.Key >= bodies.Count) continue;
                    Step(bodies[pair.Key], now, ut);
                }
            }
            Orbit.Housekeeping();
            Ground.Tick();
        }

        private static void Step(CelestialBody body, float now, double ut)
        {
            IceBodyState state = IceBodies.Of(body);
            if (state == null) return;
            try
            {
                if (state.ShoreError != null)
                {
                    AtmospheresLog.WarnOnce("sea ice of " + body.name + ": " + state.ShoreError + "; no coast structure for this session.");
                    return;
                }
                state.StepShore();
                state.RefreshMaps(now, ut);
                double lat = 60.0, lon = 0.0;
                Vessel vessel = GameGlobals.ActiveVessel;
                if (vessel != null && ReferenceEquals(vessel.mainBody, body))
                {
                    lat = vessel.latitude;
                    lon = vessel.longitude;
                }
                state.StepDrift(now, ut, lat, lon);
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("atmospheres-ice-step", "sea ice: the state of " + body.name + " could not be advanced", e);
            }
        }

        public void Shutdown()
        {
            if (Orbit != null)
            {
                OrbitOverlayLayers.Unregister(Orbit);
                Orbit.ReleaseAll();
            }
            if (Ground != null) Ground.Release();
            Requests.Clear();
            IceBodies.Clear();
            if (ReferenceEquals(Current, this)) Current = null;
        }
    }
}
