using UnityEngine;

namespace ReDefinition.Flight
{
    // Where a button over one of the space centre's buildings stands in flight
    // (AssemblyReturn, SpaceCentreButtons): the middle of the building's hall,
    // found once it is there -- over the middle of the largest drawn part that is
    // also solid (a collider on it: the SPH's model_sph_exterior_main, not the
    // light flare 90 m up its tower, sharedassets9) and not the ground (Ground), half
    // as high as its roof stands there, a ray down onto the building's colliders;
    // the tower beside the SPH's hall would make the hall's box twice as high --
    // and the building's colliders, which the button stands before
    // (WorldButton.Place).
    internal sealed class FacilitySpot
    {
        // The facility's name; its transform in the space centre; the colliders of the building, once its hall is found;
        // the button's place, in the building's own space, and whether it is measured.
        internal readonly string Name;
        internal readonly Transform Place;
        internal Collider[] Solid;
        private Vector3 middle;
        private bool measured;
        // Where no hall is drawn yet, looked for again only after this time: the
        // search goes through every renderer of the building.
        private float retryAt;
        private const float RetryEvery = 1f;

        // `name`: the facility's, for the log; `place`: its transform.
        internal FacilitySpot(string name, Transform place)
        {
            Name = name;
            Place = place;
        }

        // The middle in the world, `home` the body the space centre stands on.
        internal Vector3 Middle(CelestialBody home)
        {
            if (!measured)
            {
                Vector3 ground = Place.position;
                if (Time.unscaledTime < retryAt) return ground;
                Vector3 up = (ground - home.position).normalized;
                Renderer hall = null;
                float most = -1f;
                foreach (Renderer part in Place.GetComponentsInChildren<Renderer>(false))
                {
                    if (part.GetComponent<Collider>() == null || Ground(part.bounds, ground, up)) continue;
                    Vector3 size = part.bounds.size;
                    float volume = size.x * size.y * size.z;
                    if (volume <= most) continue;
                    most = volume;
                    hall = part;
                }
                if (hall == null)
                {
                    retryAt = Time.unscaledTime + RetryEvery;
                    return ground;
                }
                Solid = Place.GetComponentsInChildren<Collider>(false);
                Bounds bounds = hall.bounds;
                Vector3 extent = bounds.extents;
                float top = Vector3.Dot(bounds.center - ground, up)
                            + Mathf.Abs(extent.x * up.x) + Mathf.Abs(extent.y * up.y) + Mathf.Abs(extent.z * up.z);
                Vector3 across = bounds.center - up * Vector3.Dot(bounds.center - ground, up);
                // Onto the hall's own colliders: where its middle is a courtyard (the
                // R&D's main building), a ray onto all of them met the ground there;
                // where it meets nothing of the hall, its box's top.
                float roof = 0f;
                Ray down = new Ray(across + up * (top + 10f), -up);
                foreach (Collider solid in hall.GetComponents<Collider>())
                {
                    RaycastHit hit;
                    if (solid != null && !solid.isTrigger && solid.Raycast(down, out hit, top + 20f))
                        roof = Mathf.Max(roof, Vector3.Dot(hit.point - ground, up));
                }
                if (roof <= 0f) roof = top;
                middle = Place.InverseTransformPoint(across + up * (roof / 2f));
                measured = true;
                Debug.Log(Core.Log.Tag + " The " + Name + "'s roof stands " + roof.ToString("0") + " m high over "
                          + hall.name + "; its button half as high.");
            }
            return Place.TransformPoint(middle);
        }

        // Whether a part is the ground the building stands on rather than the
        // building: the space centre's ground pieces follow the terrain and reach
        // far under the building's origin -- of each facility's
        // model_*_exterior_ground, 38 to 45 % of the height stands above it, of the
        // halls 79 to 96 % (measured for the VAB, tracking station, mission control,
        // astronaut complex, R&D and administration). Without this the R&D's and the
        // administration's ground, larger than any hall of theirs, was taken.
        private static bool Ground(Bounds bounds, Vector3 ground, Vector3 up)
        {
            Vector3 extent = bounds.extents;
            float height = 2f * (Mathf.Abs(extent.x * up.x) + Mathf.Abs(extent.y * up.y) + Mathf.Abs(extent.z * up.z));
            if (height <= 0f) return false;
            float top = Vector3.Dot(bounds.center - ground, up) + height * 0.5f;
            return top < 0.6f * height;
        }

        // The space centre's facility of that name (PSystemSetup's: VAB, SPH,
        // TrackingStation, AstronautComplex, MissionControl, RnD, Administration, ...),
        // with the body it stands on; null where it is not there. `label` names it
        // in the log; the facility's name where null.
        internal static FacilitySpot Find(string name, out CelestialBody body, string label = null)
        {
            body = null;
            if (PSystemSetup.Instance == null) return null;
            foreach (PSystemSetup.SpaceCenterFacility facility in PSystemSetup.Instance.SpaceCenterFacilities)
            {
                if (facility == null || facility.facilityTransform == null) continue;
                if (facility.name != name && facility.facilityName != name
                    && (facility.facilityTransformName == null || !facility.facilityTransformName.Contains(name)))
                    continue;
                body = facility.hostBody;
                return new FacilitySpot(label ?? name, facility.facilityTransform);
            }
            return null;
        }
    }
}
