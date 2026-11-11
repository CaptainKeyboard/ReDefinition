using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Finds the lamps: the spot and point lights on the loaded vessels and on the kerbals on foot. The sun and the system lights are not
    // under a part and so never found. The lights are listed once a second (a vessel within reach of the camera), scored at the
    // rescoring rate from the cached list. Nothing here allocates per call once the lists have grown.
    internal sealed class LampLights
    {
        private struct Known
        {
            internal Light Light;
            internal Vessel Vessel;
        }

        private readonly List<Known> known = new List<Known>(32);
        private readonly List<Light> buffer = new List<Light>(8);
        private readonly Dictionary<int, Light> byId = new Dictionary<int, Light>(32);
        private double lastRefresh = double.NegativeInfinity;

        internal const double RefreshSeconds = 1.0;

        // The number of lights in the cache (for the probe).
        internal int KnownCount
        {
            get { return known.Count; }
        }

        // The light of an id the selector holds, or null when it is gone.
        internal Light Find(int id)
        {
            Light l;
            if (!byId.TryGetValue(id, out l)) return null;
            return l;
        }

        // The vessel a lamp belongs to, or null when the lamp is not in the list.
        internal Vessel VesselOf(int id)
        {
            for (int i = 0; i < known.Count; i++)
            {
                if (known[i].Light != null && known[i].Light.GetInstanceID() == id) return known[i].Vessel;
            }
            return null;
        }

        // Lists the lights of the vessels near the camera. A vessel counts when its centre is within the active range plus its own size
        // of the camera; the active vessel always.
        internal void Refresh(Vector3 cameraPosition, float activeRangeM, double now)
        {
            if (now - lastRefresh < RefreshSeconds) return;
            lastRefresh = now;
            known.Clear();
            byId.Clear();
            List<Vessel> vessels = FlightGlobals.VesselsLoaded;
            if (vessels == null) return;
            Vessel active = FlightGlobals.ActiveVessel;
            for (int v = 0; v < vessels.Count; v++)
            {
                Vessel vessel = vessels[v];
                if (vessel == null || vessel.parts == null) continue;
                if (vessel != active)
                {
                    float reach = activeRangeM + vessel.vesselSize.magnitude;
                    if (((Vector3)vessel.transform.position - cameraPosition).sqrMagnitude > reach * reach) continue;
                }
                List<Part> parts = vessel.parts;
                for (int p = 0; p < parts.Count; p++)
                {
                    Part part = parts[p];
                    if (part == null) continue;
                    buffer.Clear();
                    part.GetComponentsInChildren<Light>(true, buffer);
                    for (int i = 0; i < buffer.Count; i++)
                    {
                        Light l = buffer[i];
                        if (l == null || (l.type != LightType.Spot && l.type != LightType.Point)) continue;
                        known.Add(new Known { Light = l, Vessel = vessel });
                        byId[l.GetInstanceID()] = l;
                    }
                }
            }
        }

        // Scores the lights that are on now and within the active range of the camera into `into` (cleared first). The lights of
        // hideVessel are left out (the camera is inside it).
        internal void Candidates(Vector3 cameraPosition, Vessel hideVessel, ref LampTuning t, List<LampCandidate> into)
        {
            into.Clear();
            float range2 = t.ActiveRangeM * t.ActiveRangeM;
            for (int i = 0; i < known.Count; i++)
            {
                Light l = known[i].Light;
                if (l == null || !l.enabled || !l.gameObject.activeInHierarchy) continue;
                if (hideVessel != null && known[i].Vessel == hideVessel) continue;
                if (!LampMath.IsLamp(l.intensity, l.range, ref t)) continue;
                float d2 = (l.transform.position - cameraPosition).sqrMagnitude;
                if (d2 > range2) continue;
                into.Add(new LampCandidate(l.GetInstanceID(), LampMath.Score(l.intensity, l.range, d2)));
            }
        }

        internal void Clear()
        {
            known.Clear();
            byId.Clear();
            lastRefresh = double.NegativeInfinity;
        }
    }
}
