using UnityEngine;

namespace ReDefinition.Raytracing
{
    // The sea's frame for the waves (RtCamera.Sea; RtCommon.hlsli, MakeSeaFrame): a point on
    // the main body's sea level, by latitude and longitude, so that it turns with the body,
    // and its east and north, world space. The waves are laid out in metres from it, so
    // they stay on the water as the camera moves. A float keeps those metres to millimetres
    // only near the point: it is set anew under the camera once the camera is RebaseMetres
    // away, or on another body, and the waves' pattern starts afresh there.
    internal sealed class SeaFrameAnchor
    {
        // How far the camera may go from the point before it is set anew; the body and the place of the point.
        private const double RebaseMetres = 50000.0;
        private CelestialBody body;
        private double latitude, longitude;

        // The frame for a camera at `eye` over `main`, the point relative to the eye; false where the body has no sea.
        // The point is set anew where it lies farther from the eye than RebaseMetres plus the eye's altitude.
        internal bool Frame(CelestialBody main, Vector3d eye, out Vector3 origin, out Vector3 east, out Vector3 north)
        {
            origin = east = north = Vector3.zero;
            if (main == null || !main.ocean) return false;
            if (main != body || (main.GetWorldSurfacePosition(latitude, longitude, 0.0) - eye).magnitude
                                > RebaseMetres + main.GetAltitude(eye))
            {
                body = main;
                latitude = main.GetLatitude(eye);
                longitude = main.GetLongitude(eye);
            }
            Vector3d point = main.GetWorldSurfacePosition(latitude, longitude, 0.0);
            Vector3d up = (point - main.position).normalized;
            // East as the longitude grows, in the point's plane; north across it as the
            // shader makes it from up and east.
            Vector3d ahead = main.GetWorldSurfacePosition(latitude, longitude + 0.001, 0.0)
                             - main.GetWorldSurfacePosition(latitude, longitude - 0.001, 0.0);
            Vector3d flat = ahead - Vector3d.Dot(ahead, up) * up;
            if (flat.sqrMagnitude <= 0.0) return false;
            Vector3d eastward = flat.normalized;
            origin = (Vector3)(point - eye);
            east = (Vector3)eastward;
            north = (Vector3)Vector3d.Cross(up, eastward);
            return true;
        }
    }
}
