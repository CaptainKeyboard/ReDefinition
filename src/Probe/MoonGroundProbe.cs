using System;
using System.Collections;
using System.IO;
using ReDefinition.Core;
using ReDefinition.Raytracing;
using UnityEngine;

namespace ReDefinition.Probe
{
    // -redefinitionProbeMoonGround <moon>: the moon in the night sky of the planet the pod stands on, in the phases the sky
    // gives. The game's time is hopped on (TimeWarp.WarpTo) until the moon stands 10 to 45 degrees over the horizon with the
    // sun under it. Each phase range of Ranges is then shot once, as full frames at the game's field of view. They show the moon
    // before the brightening, with it, and with it but without the planet's light.
    // The flight camera is turned to the moon from a frame found by trying it (Calibrate): its pitch and heading follow
    // the surface where the pod stands, the world's axes in orbit.
    internal static class MoonGroundProbe
    {
        internal const string Argument = "-redefinitionProbeMoonGround";

        // The phase angles, in degrees, that each shot's set stands for: from, to. 0 is full, 180 new.
        private static readonly float[][] Ranges =
        {
            new[] { 5f, 35f }, new[] { 35f, 65f }, new[] { 65f, 95f }, new[] { 95f, 125f }, new[] { 125f, 155f },
        };

        // The warp's hop and the scan's step, in seconds, and how far on the time is looked through at most. Then the moon's
        // height over the horizon wanted, in degrees. The sun is under the horizon where NightAdaptation.Night is at least half.
        private const double HopSeconds = 1800.0, ScanStep = 300.0, ScanSeconds = 300000.0;
        private const float LeastElevation = 10f, MostElevation = 45f;
        private const float LeastNight = 0.5f;

        // The camera's axes found by trying it. A is the direction it looks along at heading and pitch 0, B at heading 90 degrees,
        // and Up its up axis: look = cos(pitch) (cos(heading) A + sin(heading) B) - sin(pitch) Up.
        private sealed class Frame
        {
            internal Vector3 A, B, Up;
        }

        // The camera's yaw and pitch error to a world direction, radians: positive where the direction is to the right, above.
        private static void Error(Camera camera, Vector3 direction, out float yaw, out float pitch)
        {
            float forward = Vector3.Dot(direction, camera.transform.forward);
            yaw = Mathf.Atan2(Vector3.Dot(direction, camera.transform.right), forward);
            pitch = Mathf.Atan2(Vector3.Dot(direction, camera.transform.up), forward);
        }

        // Finds the camera's frame: three looks, two frames each.
        private static IEnumerator Calibrate(FlightCamera flight, Frame frame)
        {
            Camera camera = flight.mainCamera;
            flight.camHdg = 0f;
            flight.camPitch = 0f;
            yield return null;
            yield return null;
            frame.A = camera.transform.forward;
            flight.camHdg = Mathf.PI / 2f;
            yield return null;
            yield return null;
            frame.B = camera.transform.forward;
            flight.camHdg = 0f;
            flight.camPitch = 0.5f;
            yield return null;
            yield return null;
            frame.Up = ((Mathf.Cos(0.5f) * frame.A - camera.transform.forward) / Mathf.Sin(0.5f)).normalized;
        }

        // Turns the camera to look at `direction` with it `lift` radians above the middle of the picture; the error left, in degrees.
        private static IEnumerator Aim(FlightCamera flight, Frame frame, Func<Vector3> direction, float lift, float[] left)
        {
            Camera camera = flight.mainCamera;
            Vector3 d = direction();
            Vector3 up = (frame.Up - Vector3.Dot(frame.Up, d) * d).normalized;
            Vector3 look = (d * Mathf.Cos(lift) - up * Mathf.Sin(lift)).normalized;
            flight.camHdg = Mathf.Atan2(Vector3.Dot(look, frame.B), Vector3.Dot(look, frame.A));
            flight.camPitch = -Mathf.Asin(Mathf.Clamp(Vector3.Dot(look, frame.Up), -1f, 1f));
            float yaw = 0f, pitch = 0f;
            for (int i = 0; i < 6; i++)
            {
                yield return null;
                yield return null;
                Error(camera, direction(), out yaw, out pitch);
                pitch -= lift;
                if (Mathf.Abs(yaw) < 0.002f && Mathf.Abs(pitch) < 0.002f) break;
                flight.camHdg += yaw * 0.9f;
                flight.camPitch -= pitch * 0.9f;
            }
            left[0] = yaw * Mathf.Rad2Deg;
            left[1] = pitch * Mathf.Rad2Deg;
        }

        // The moon's phase angle, its height over the pod's horizon and how dark the sky is, now.
        private static void Measure(Vessel vessel, CelestialBody moon, CelestialBody star, out float phase, out float elevation, out float night)
        {
            Vector3d position = vessel.GetWorldPos3D();
            Vector3d up = (position - vessel.mainBody.position).normalized;
            elevation = (float)(Math.Asin(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(up, (moon.position - position).normalized)))) * Mathf.Rad2Deg);
            phase = (float)Vector3d.Angle(star.position - moon.position, position - moon.position);
            night = NightAdaptation.Night(position, vessel.mainBody, star);
        }

        // The game's time hopped on by `seconds`: TimeWarp.WarpTo, and the time set where that does not get there.
        private static IEnumerator Hop(double seconds)
        {
            double target = Planetarium.GetUniversalTime() + seconds;
            if (TimeWarp.fetch != null) TimeWarp.fetch.WarpTo(target);
            float waited = 0f;
            while (Planetarium.GetUniversalTime() < target - 1.0 && waited < 90f)
            {
                yield return new WaitForSecondsRealtime(0.25f);
                waited += 0.25f;
            }
            if (Planetarium.GetUniversalTime() < target - 1.0)
            {
                Debug.Log(Log.Tag + " Probe: moon ground: the warp did not reach " + target.ToString("0") + "; the time is set.");
                if (TimeWarp.fetch != null) TimeWarp.SetRate(0, true);
                Planetarium.SetUniversalTime(target);
            }
            yield return new WaitForSecondsRealtime(3f);
        }

        // The scan and the shots. The game's time is set 300 s on, over and over, and what the sky then shows is read from the
        // game itself; where setting it does not take, the time hops with the warp instead.
        internal static IEnumerator Run(string folder, string shot)
        {
            string name = CommandLine.After(Argument);
            CelestialBody moon = name != null ? FlightGlobals.Bodies.Find(b => b.bodyName == name) : null;
            CelestialBody star = RtKspMods.StarBody();
            if (moon == null || star == null || FlightCamera.fetch == null || FlightGlobals.ActiveVessel == null) yield break;
            FlightCamera flight = FlightCamera.fetch;
            Vessel vessel = FlightGlobals.ActiveVessel;
            GameEvents.onHideUI.Fire();
            flight.SetDistance(12f);
            var done = new bool[Ranges.Length];
            int taken = 0;
            bool direct = true;
            double scanned = 0.0;
            var frame = new Frame();
            var left = new float[2];
            Func<Vector3> toMoon = () => (Vector3)(moon.position - vessel.GetWorldPos3D()).normalized;
            while (taken < Ranges.Length && scanned < ScanSeconds)
            {
                float phase, elevation, night;
                Measure(vessel, moon, star, out phase, out elevation, out night);
                int range = -1;
                for (int i = 0; i < Ranges.Length; i++)
                    if (!done[i] && phase >= Ranges[i][0] && phase < Ranges[i][1]) range = i;
                if (range >= 0 && elevation >= LeastElevation && elevation <= MostElevation && night >= LeastNight)
                {
                    done[range] = true;
                    taken++;
                    yield return new WaitForSecondsRealtime(3f);
                    yield return Calibrate(flight, frame);
                    // The moon at most some 22 degrees over the middle of the picture; the camera as low as the ground lets it.
                    float viewElevation = Mathf.Max(3f, elevation - 22f);
                    flight.SetFoV(flight.fovDefault);
                    yield return Aim(flight, frame, toMoon, (elevation - viewElevation) * Mathf.Deg2Rad, left);
                    yield return new WaitForSecondsRealtime(2f);
                    Debug.Log(Log.Tag + " Probe: moon ground: phase " + phase.ToString("0") + " degrees, " + elevation.ToString("0") + " degrees high, night "
                              + night.ToString("0.00") + ", aim error " + left[0].ToString("0.0") + ", " + left[1].ToString("0.0") + " degrees, the time "
                              + Planetarium.GetUniversalTime().ToString("0") + "; the moons' light: " + RtReflectors.DescribeCandidates());
                    string prefix = shot + "-ground-phase" + ((int)phase).ToString("000");
                    yield return Variant(folder, prefix + "-before", () => MoonGlow.HeldOff = true, () => MoonGlow.HeldOff = false);
                    yield return Variant(folder, prefix + "-now", () => { }, () => { });
                    yield return Variant(folder, prefix + "-noearthshine", () => MoonGlow.NoEarthshine = true, () => MoonGlow.NoEarthshine = false);
                }
                if (direct)
                {
                    double target = Planetarium.GetUniversalTime() + ScanStep;
                    Planetarium.SetUniversalTime(target);
                    for (int f = 0; f < 3; f++) yield return null;
                    if (Math.Abs(Planetarium.GetUniversalTime() - target) > 5.0)
                    {
                        direct = false;
                        Debug.Log(Log.Tag + " Probe: moon ground: the time does not take; the warp hops it on.");
                    }
                    scanned += ScanStep;
                }
                else
                {
                    yield return Hop(HopSeconds);
                    scanned += HopSeconds;
                }
            }
            Debug.Log(Log.Tag + " Probe: moon ground done: " + taken + " of " + Ranges.Length + " phases, " + scanned.ToString("0") + " s scanned.");
        }

        // One shot with a switch set, and the switch put back.
        private static IEnumerator Variant(string folder, string file, Action set, Action reset)
        {
            set();
            yield return new WaitForSecondsRealtime(2f);
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, file + ".png"));
            yield return null;
            reset();
        }
    }
}
