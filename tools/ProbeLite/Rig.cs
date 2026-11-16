using System;
using UnityEngine;

namespace ProbeLite
{
    // One camera pose: the camera looks along (heading, elevation) from DistM behind the site point; its height is CamAglM over the PQS terrain
    // at its own spot, or AbsAltM above sea level where that is set (the lawn poses put it 2 m over the ground collider the ray found).
    internal sealed class PoseSpec
    {
        internal string Name;
        internal double HeadingDeg, ElevationDeg, DistM, CamAglM;
        internal double AbsAltM = double.NaN;
        // A pose of the space centre scene (the site is the pad), else the flight scene (the site is the vessel's spot).
        internal bool SpaceCentre;
    }

    internal struct GroundReading
    {
        internal bool Hit;
        internal string Object, Material, Shader;
        internal double AltAsl;
    }

    // Places the game's camera rig where a pose says, every frame. The rig is FlightCamera.fetch.transform in the flight and in the space centre
    // (the game's own update of it is off while a pose is held). Nothing of the vessel is moved.
    internal static class Rig
    {
        internal static PoseSpec Current;
        internal static double SiteLat = double.NaN, SiteLon = double.NaN;

        internal const double RayUpM = 20.0, RayLengthM = 450.0, RayAheadM = 30.0, LawnWindowM = 6.0;

        internal static CelestialBody Home()
        {
            return FlightGlobals.GetHomeBody();
        }

        // The local frame at a place: up from the body's centre, north along the meridian, east.
        internal static void Frame(CelestialBody body, double lat, double lon, out Vector3d up, out Vector3d north, out Vector3d east)
        {
            Vector3d here = body.GetWorldSurfacePosition(lat, lon, 0.0);
            Vector3d further = body.GetWorldSurfacePosition(lat + 0.5, lon, 0.0);
            up = (here - body.position).normalized;
            Vector3d along = further - here;
            north = (along - up * Vector3d.Dot(along, up)).normalized;
            east = Vector3d.Cross(up, north);
        }

        internal static bool Compute(PoseSpec p, out Vector3d position, out Quaternion rotation, out double camLat, out double camLon)
        {
            position = Vector3d.zero;
            rotation = Quaternion.identity;
            camLat = camLon = 0.0;
            CelestialBody body = Home();
            if (body == null || double.IsNaN(SiteLat) || double.IsNaN(SiteLon)) return false;
            double h = p.HeadingDeg * Math.PI / 180.0, el = p.ElevationDeg * Math.PI / 180.0;
            Vector3d up0, north0, east0;
            Frame(body, SiteLat, SiteLon, out up0, out north0, out east0);
            Vector3d site = body.GetWorldSurfacePosition(SiteLat, SiteLon, body.TerrainAltitude(SiteLat, SiteLon, true));
            Vector3d horizontal = north0 * Math.Cos(h) + east0 * Math.Sin(h);
            Vector3d approx = site - horizontal * (p.DistM * Math.Cos(el));
            double alt;
            body.GetLatLonAlt(approx, out camLat, out camLon, out alt);
            double terrain = body.TerrainAltitude(camLat, camLon, true);
            double wanted = double.IsNaN(p.AbsAltM) ? terrain + p.CamAglM : p.AbsAltM;
            position = body.GetWorldSurfacePosition(camLat, camLon, wanted);
            Vector3d up, north, east;
            Frame(body, camLat, camLon, out up, out north, out east);
            Vector3d forward = (north * Math.Cos(h) + east * Math.Sin(h)) * Math.Cos(el) + up * Math.Sin(el);
            rotation = Quaternion.LookRotation((Vector3)forward, (Vector3)up);
            return true;
        }

        // Every frame while a pose is held (called from the scene's LateUpdate).
        internal static void Apply()
        {
            if (Current == null || FlightCamera.fetch == null) return;
            Vector3d position;
            Quaternion rotation;
            double lat, lon;
            if (!Compute(Current, out position, out rotation, out lat, out lon)) return;
            Transform view = FlightCamera.fetch.transform;
            view.position = (Vector3)position;
            view.rotation = rotation;
        }

        // What the camera really shows, read from the camera object: the compass azimuth and elevation of its view (the game's own axis,
        // independent of the pose maths) and its height over the PQS terrain.
        internal static bool ReadBack(out double azimuthDeg, out double elevationDeg, out double aglM, out double altAsl, out Vector3d cameraPosition)
        {
            azimuthDeg = elevationDeg = aglM = altAsl = double.NaN;
            cameraPosition = Vector3d.zero;
            CelestialBody body = Home();
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam == null || body == null) return false;
            cameraPosition = cam.transform.position;
            Vector3d forward = cam.transform.forward;
            Vector3d up = (cameraPosition - body.position).normalized;
            Vector3d axis = body.RotationAxis.normalized;
            Vector3d north = (axis - up * Vector3d.Dot(axis, up)).normalized;
            Vector3d east = Vector3d.Cross(up, north);
            azimuthDeg = Math.Atan2(Vector3d.Dot(forward, east), Vector3d.Dot(forward, north)) * 180.0 / Math.PI;
            elevationDeg = Math.Asin(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(forward, up)))) * 180.0 / Math.PI;
            double lat, lon;
            body.GetLatLonAlt(cameraPosition, out lat, out lon, out altAsl);
            aglM = altAsl - body.TerrainAltitude(lat, lon, true);
            return true;
        }

        // Where the horizon stands in the picture, as a fraction of the height from the top: the sea-level horizon's dip below the camera's level (acos(R / (R + altitude))) and
        // the camera's own elevation against its vertical field of view. NaN where the camera cannot be read or the horizon is outside the picture.
        internal static double HorizonFromTop()
        {
            double az, el, agl, alt;
            Vector3d pos;
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            CelestialBody body = Home();
            if (cam == null || body == null || !ReadBack(out az, out el, out agl, out alt, out pos)) return double.NaN;
            double dip = Math.Acos(body.Radius / (body.Radius + Math.Max(0.0, alt)));
            double angle = dip + el * Math.PI / 180.0;
            double y = 0.5 + 0.5 * Math.Tan(angle) / Math.Tan(cam.fieldOfView * 0.5 * Math.PI / 180.0);
            return y > 0.05 && y < 0.95 ? y : double.NaN;
        }

        // The first collider (not a vessel part) under a point: a ray straight down from RayUpM above it.
        internal static GroundReading RayDown(Vector3d from)
        {
            var none = new GroundReading { Object = "", Material = "", Shader = "", AltAsl = double.NaN };
            try
            {
                CelestialBody body = Home();
                if (body == null) return none;
                Vector3 up = (Vector3)((from - body.position).normalized);
                RaycastHit[] hits = Physics.RaycastAll((Vector3)from + up * (float)RayUpM, -up, (float)RayLengthM);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                // The ground is the highest collider within LawnWindowM of the PQS terrain there (the lawn lies 2.7 to 3.2 m over it, the pad's apron at it); the first
                // collider from above can be the pad's tower or a building (the vanilla start read the launch pad structure, 17 m up, as the ground). Where none is in the
                // window the first collider is the answer.
                double fromLat, fromLon, fromAlt;
                body.GetLatLonAlt(from, out fromLat, out fromLon, out fromAlt);
                double terrain = body.TerrainAltitude(fromLat, fromLon, true);
                GroundReading first = none, lawn = none;
                foreach (RaycastHit hit in hits)
                {
                    if (hit.collider == null || hit.collider.GetComponentInParent<Part>() != null) continue;
                    Renderer renderer = hit.collider.GetComponent<Renderer>() ?? hit.collider.GetComponentInParent<Renderer>();
                    Material material = renderer != null ? renderer.sharedMaterial : null;
                    double lat, lon, alt;
                    body.GetLatLonAlt(hit.point, out lat, out lon, out alt);
                    var reading = new GroundReading
                    {
                        Hit = true, Object = hit.collider.gameObject.name, Material = material != null ? material.name : "none",
                        Shader = material != null && material.shader != null ? material.shader.name : "none", AltAsl = alt
                    };
                    if (!first.Hit) first = reading;
                    if (alt >= terrain - 1.0 && alt <= terrain + LawnWindowM && (!lawn.Hit || alt > lawn.AltAsl)) lawn = reading;
                }
                if (lawn.Hit) return lawn;
                if (first.Hit) return first;
            }
            catch (Exception e)
            {
                Lite.Say("ray: " + e.GetType().Name + " " + e.Message);
            }
            return none;
        }

        // The point RayAheadM in front of the camera (horizontally): what the camera looks at.
        internal static Vector3d Ahead(Vector3d cameraPosition)
        {
            CelestialBody body = Home();
            Camera cam = FlightCamera.fetch.mainCamera;
            Vector3d up = (cameraPosition - body.position).normalized;
            Vector3d forward = (Vector3d)cam.transform.forward;
            Vector3d flat = (forward - up * Vector3d.Dot(forward, up)).normalized;
            return cameraPosition + flat * RayAheadM;
        }
    }
}
