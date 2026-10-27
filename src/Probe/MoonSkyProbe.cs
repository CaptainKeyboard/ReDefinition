using System;
using System.Collections;
using System.IO;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Probe
{
    // -redefinitionProbeMoonSky <moon>: the flight camera turned to that moon and shot with the moons' brightening off and on
    // (MoonGlow.HeldOff), as <shot>-moon-off.png and <shot>-moon-on.png, a wide one and a zoomed one of each. The pod is to
    // stand over the planet's side of that moon (-redefinitionProbeOrbit <planet> -redefinitionProbeOrbitToward <moon>),
    // so that the moon is in the sky and nothing is before it; -redefinitionProbeUniversalTime sets the moon's phase.
    internal static class MoonSkyProbe
    {
        internal const string Argument = "-redefinitionProbeMoonSky";

        // The camera's yaw and pitch error to a world direction, radians: positive where the direction is to the right, above.
        private static void Error(Camera camera, Vector3 direction, out float yaw, out float pitch)
        {
            float forward = Vector3.Dot(direction, camera.transform.forward);
            yaw = Mathf.Atan2(Vector3.Dot(direction, camera.transform.right), forward);
            pitch = Mathf.Atan2(Vector3.Dot(direction, camera.transform.up), forward);
        }

        // Turns the flight camera until the direction is in the middle of the picture, shifted up by `lift` radians. The
        // camera looks along (sin heading * cos pitch, -sin pitch, cos heading * cos pitch) in the world's axes, a positive
        // pitch down, as KSP logged it (2026-10-27). The first heading and pitch follow from that, the rest from the error that remains.
        private static IEnumerator Aim(Func<Vector3> direction, float lift)
        {
            FlightCamera flight = FlightCamera.fetch;
            Camera camera = flight.mainCamera;
            Vector3 d = direction();
            Vector3 up = (Vector3.up - Vector3.Dot(Vector3.up, d) * d).normalized;
            Vector3 look = (d * Mathf.Cos(lift) - up * Mathf.Sin(lift)).normalized;
            flight.camHdg = Mathf.Atan2(look.x, look.z);
            flight.camPitch = -Mathf.Asin(Mathf.Clamp(look.y, -1f, 1f));
            for (int i = 0; i < 12; i++)
            {
                yield return null;
                yield return null;
                float yaw, pitch;
                Error(camera, direction(), out yaw, out pitch);
                pitch -= lift;
                if (Mathf.Abs(yaw) < 0.002f && Mathf.Abs(pitch) < 0.002f) break;
                flight.camHdg += yaw * 0.9f;
                flight.camPitch -= pitch * 0.9f;
            }
        }

        // The shots: wide, then zoomed, with the brightening off and on. The camera sits 200 m from the pod, the moon lifted
        // above it: a wide view with the moon 11 degrees over the pod, a close one with it 2 degrees over.
        internal static IEnumerator Run(string folder, string shot)
        {
            string name = CommandLine.After(Argument);
            CelestialBody moon = name != null ? FlightGlobals.Bodies.Find(b => b.bodyName == name) : null;
            if (moon == null || FlightCamera.fetch == null || FlightGlobals.ActiveVessel == null) yield break;
            FlightCamera flight = FlightCamera.fetch;
            GameEvents.onHideUI.Fire();
            flight.SetDistance(200f);
            flight.fovMin = 1f;
            Func<Vector3> toMoon = () => (Vector3)(moon.position - FlightGlobals.ActiveVessel.GetWorldPos3D()).normalized;
            foreach (float fov in new[] { 40f, 8f })
            {
                flight.SetFoV(fov);
                yield return Aim(toMoon, fov < 10f ? 0.035f : 0.2f);
                yield return new WaitForSecondsRealtime(3f);
                Camera camera = flight.mainCamera;
                float yaw, pitch;
                Error(camera, toMoon(), out yaw, out pitch);
                Vector3 view = camera.WorldToViewportPoint(camera.transform.position + toMoon() * 1000f);
                float radius = Mathf.Asin((float)(moon.Radius / (moon.position - FlightGlobals.ActiveVessel.GetWorldPos3D()).magnitude)) * Mathf.Rad2Deg;
                Debug.Log(Log.Tag + " Probe: moon sky aim: field of view " + camera.fieldOfView.ToString("0.0") + " (asked " + fov + "), the moon at "
                          + view.x.ToString("0.00") + "," + view.y.ToString("0.00") + " of the picture, " + (yaw * Mathf.Rad2Deg).ToString("0.00") + " degrees right and "
                          + (pitch * Mathf.Rad2Deg).ToString("0.00") + " above the middle, its radius " + radius.ToString("0.00") + " degrees.");
                foreach (bool off in new[] { true, false })
                {
                    Raytracing.MoonGlow.HeldOff = off;
                    yield return new WaitForSecondsRealtime(2f);
                    yield return new WaitForEndOfFrame();
                    string file = shot + "-moon-" + (off ? "off" : "on") + "-fov" + (int)fov + ".png";
                    ScreenCapture.CaptureScreenshot(Path.Combine(folder, file));
                    Debug.Log(Log.Tag + " Probe: moon sky: " + file + " (" + name + ", phase "
                              + Vector3d.Angle(FlightGlobals.Bodies[0].position - moon.position, FlightGlobals.ActiveVessel.GetWorldPos3D() - moon.position).ToString("0")
                              + " degrees); the moons' light: " + Raytracing.RtReflectors.DescribeCandidates());
                    yield return null;
                }
            }
            Raytracing.MoonGlow.HeldOff = false;
        }
    }
}
