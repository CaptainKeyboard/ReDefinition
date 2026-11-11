using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The shell in the real sky (start "aurora" of K-W3b, full mod set). Everything is read from the flight camera's own frame as the
    // positive part of (shell on minus shell off) of one luminance image per pose, taken one after the other with the same camera,
    // the same forced aurora state and the same clock; the shell is "off" by a gain of zero, so that both frames go through the same
    // draw. A third frame (off again) gives the noise floor of the pair: whatever moves in the real sky without the shell (water,
    // clouds, Scatterer's animation).
    //
    // Expectations, written before the first run:
    //   * Draw order. The aurora lies above the atmosphere; Scatterer's sky sphere (queue 2998) and EVE's cloud layers (2999) draw
    //     after BeforeForwardAlpha, so at that event the sky should extinguish part of the shell (none_before dimmer than none_after)
    //     and the shell must still be seen (zbuffer_before keeps at least a quarter of zbuffer_after). If the sky removes more than
    //     that, the default event must move to AfterForwardAlpha (aurora.event 1), the row D38.order.survives_sky fails and says so.
    //   * Sky test. The hardware depth buffer (zbuffer) paints the same pixels as the unrestricted dome (none) in an open sky, and
    //     hides behind an opaque sphere. A copy mode that reads a depth the stack does not fill paints nothing (peak below the
    //     level) or paints over the sphere; the rows name which.
    //   * Artefacts. The three metrics of the player (lid, drip, band) at lat 60 (with the strong event, the oval overhead),
    //     70 and 78, activity 0.6 and 1, elevation 25, and 10 and 40 at lat 70; a pose whose shell peak is under AuroraMetrics.PeakMin
    //     is not judged and is counted in D38.sky.poses_dim.
    internal sealed partial class D38Probe
    {
        // Hover altitude above the sea (m), and the clearance over the terrain below it.
        private const double HoverAltitude = 3000.0, TerrainClearance = 1500.0;
        private const float CameraDistance = 150f;
        private const double AimToleranceDeg = 1.5;

        private ReconHost host;
        private float[] lastLuma;
        private Color[] lastPixels;
        private int lastWidth, lastHeight;
        // The largest distance of the sun's elevation from the one a pose asked for, over every pose placed.
        private double worstSunError;
        private const double PoseSunToleranceDeg = 2.0;
        private bool waitedOk;
        private float shellGain = 0.9f;

        private struct SkyPose
        {
            internal string Name;
            internal float Lat, Activity, Event, Elevation;
        }

        private struct VariantDef
        {
            internal string Name;
            internal AuroraSkyTest Test;
            internal AuroraEvent Event;
            // Copy modes only: the sky value of the copy and its tolerance (0 keeps the far-value test).
            internal float SkyRef, SkyTol;
        }

        // RECON P6 (run4, full set, live frame): the global depth texture at BeforeForwardAlpha reads 0.314 in an empty view and 0.34 to
        // 0.89 on geometry from 2.5 m on, so a test against that sky value tells sky from geometry where the far-value test finds none.
        private const float ReconSkyDepth = 0.314f, ReconSkyTolerance = 0.03f;
        private float variantSkyRef, variantSkyTol;

        private static readonly VariantDef[] OrderVariants =
        {
            new VariantDef { Name = "zbuffer_before", Test = AuroraSkyTest.ZBuffer, Event = AuroraEvent.BeforeForwardAlpha },
            new VariantDef { Name = "copy_depth_before", Test = AuroraSkyTest.CopyDepth, Event = AuroraEvent.BeforeForwardAlpha },
            new VariantDef { Name = "copy_resolved_before", Test = AuroraSkyTest.CopyResolved, Event = AuroraEvent.BeforeForwardAlpha },
            new VariantDef { Name = "copy_global_before", Test = AuroraSkyTest.CopyGlobal, Event = AuroraEvent.BeforeForwardAlpha },
            new VariantDef { Name = "copy_global_ref_before", Test = AuroraSkyTest.CopyGlobal, Event = AuroraEvent.BeforeForwardAlpha, SkyRef = ReconSkyDepth, SkyTol = ReconSkyTolerance },
            new VariantDef { Name = "none_before", Test = AuroraSkyTest.None, Event = AuroraEvent.BeforeForwardAlpha },
            new VariantDef { Name = "zbuffer_after", Test = AuroraSkyTest.ZBuffer, Event = AuroraEvent.AfterForwardAlpha },
            new VariantDef { Name = "none_after", Test = AuroraSkyTest.None, Event = AuroraEvent.AfterForwardAlpha }
        };

        private static string Num(double v)
        {
            return v.ToString("0.#####", CultureInfo.InvariantCulture);
        }

        // The sun elevation the pose asks for at a latitude: -12 degrees, or 1 degree above the lowest the latitude reaches on a body
        // with no axial tilt (at 78 degrees latitude the midnight sun stands at -12).
        internal static float WantedSunDeg(float latDeg)
        {
            return Math.Max(-12f, -(90f - Math.Abs(latDeg)) + 1f);
        }

        // The direction from the body's centre to the place at that latitude where the sun stands at that elevation, on the night side
        // (the longitude from the sub-solar point is the one that gives the elevation; a value out of reach is clamped to the nearest).
        private static Vector3d PlaceDirection(CelestialBody body, double latDeg, double sunDeg)
        {
            Vector3d axis = body.RotationAxis.normalized;
            Vector3d toSun = (FlightGlobals.Bodies[0].position - body.position).normalized;
            double sunAxis = Vector3d.Dot(toSun, axis);
            Vector3d e1 = (toSun - axis * sunAxis).normalized;
            Vector3d e2 = Vector3d.Cross(axis, e1);
            double lat = latDeg * Math.PI / 180.0;
            double cosLat = Math.Cos(lat), sinLat = Math.Sin(lat);
            double cosSun = Math.Sqrt(Math.Max(0.0, 1.0 - sunAxis * sunAxis));
            double c = cosLat * cosSun > 1e-9 ? (Math.Sin(sunDeg * Math.PI / 180.0) - sinLat * sunAxis) / (cosLat * cosSun) : 1.0;
            double longitude = Math.Acos(Math.Max(-1.0, Math.Min(1.0, c)));
            return (e1 * (cosLat * Math.Cos(longitude)) + e2 * (cosLat * Math.Sin(longitude)) + axis * sinLat).normalized;
        }

        // Waits for a condition for up to that many real seconds; waitedOk says whether it came true. A step that times out goes on with
        // that knowledge instead of ending the whole scenario.
        private IEnumerable<ProbeWait> WaitFor(Func<bool> condition, double seconds)
        {
            double end = Time.realtimeSinceStartup + seconds;
            waitedOk = false;
            while (Time.realtimeSinceStartup < end)
            {
                if (condition())
                {
                    waitedOk = true;
                    yield break;
                }
                yield return ProbeWait.Frames(1);
            }
            waitedOk = condition();
        }

        // Reads the flight camera's frame at the end of this frame as a luminance image (row 0 at the bottom); lastLuma is null when the
        // read failed.
        private IEnumerable<ProbeWait> Shot()
        {
            host.Read();
            foreach (ProbeWait w in WaitFor(() => !host.Reading, 15)) yield return w;
            ReconFrame f = waitedOk ? host.Frame : null;
            if (f == null)
            {
                lastLuma = null;
                lastPixels = null;
                yield break;
            }
            var luma = new float[f.Pixels.Length];
            for (int i = 0; i < luma.Length; i++)
            {
                Color c = f.Pixels[i];
                luma[i] = ReconMath.Luma(c.r, c.g, c.b);
            }
            lastLuma = luma;
            lastPixels = f.Pixels;
            lastWidth = f.Width;
            lastHeight = f.Height;
        }

        // Forces the aurora state and the way the shell is drawn, and waits for a draw with it. gain 0 is "shell off".
        private IEnumerable<ProbeWait> Show(bool on, float activity, float evt, AuroraSkyTest test, AuroraEvent camEvent)
        {
            ForceTuning("aurora.forceActivity", activity);
            ForceTuning("aurora.forceEvent", evt);
            ForceTuning("aurora.skyTest", (float)test);
            ForceTuning("aurora.event", (float)camEvent);
            ForceTuning("aurora.skyDepthRef", variantSkyRef);
            ForceTuning("aurora.skyDepthTol", variantSkyTol);
            ForceTuning("aurora.gain", on ? shellGain : 0f);
            int from = Time.frameCount;
            foreach (ProbeWait w in WaitFor(() => AuroraRenderer.LastDrawFrame > from && AuroraRenderer.LastSkyTest == test
                                                  && AuroraRenderer.LastEvent == camEvent, 12)) yield return w;
            if (!waitedOk) Say("the shell did not draw in " + AuroraSkyTests.Name(test) + "/" + camEvent + " (sun " + Num(PoseSunDeg()) + " degrees)");
            yield return ProbeWait.Frames(2);
        }

        private static void Say(string message)
        {
            AtmospheresLog.Say("D38: " + message);
        }

        // The sun elevation at the flight camera as the renderer takes it.
        private static float PoseSunDeg()
        {
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            CelestialBody body = FlightGlobals.currentMainBody;
            if (cam == null || body == null) return 0f;
            Vector3d camWorld = cam.transform.position;
            Vector3d up = (camWorld - body.position).normalized;
            Vector3d toSun = (FlightGlobals.Bodies[0].position - camWorld).normalized;
            return AuroraModel.SunElevationDeg(Vector3d.Dot(up, toSun));
        }

        // The camera's compass heading and elevation in degrees (0 north, 90 east).
        private static void CameraAim(out double azimuthDeg, out double elevationDeg)
        {
            Camera cam = FlightCamera.fetch.mainCamera;
            CelestialBody body = FlightGlobals.currentMainBody;
            Vector3d forward = cam.transform.forward;
            Vector3d up = ((Vector3d)cam.transform.position - body.position).normalized;
            Vector3d axis = body.RotationAxis.normalized;
            Vector3d north = (axis - up * Vector3d.Dot(axis, up)).normalized;
            Vector3d east = Vector3d.Cross(up, north);
            azimuthDeg = Math.Atan2(Vector3d.Dot(forward, east), Vector3d.Dot(forward, north)) * 180.0 / Math.PI;
            elevationDeg = Math.Asin(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(forward, up)))) * 180.0 / Math.PI;
        }

        private static double WrapDeg(double d)
        {
            while (d > 180.0) d -= 360.0;
            while (d < -180.0) d += 360.0;
            return d;
        }

        // Puts the vessel at rest in the air over the latitude where the sun stands at the wanted elevation, upright, and holds it.
        private IEnumerable<ProbeWait> Place(double latDeg, double sunDeg, out double altitude)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            CelestialBody body = vessel.mainBody;
            Vector3d dir = PlaceDirection(body, latDeg, sunDeg);
            double lat, lon;
            body.GetLatLonAlt(body.position + dir * body.Radius, out lat, out lon, out altitude);
            double terrain = 0.0;
            try
            {
                terrain = body.TerrainAltitude(lat, lon, true);
            }
            catch (Exception)
            {
                terrain = 0.0;
            }
            altitude = Math.Max(HoverAltitude, terrain + TerrainClearance);
            return PlaceSteps(vessel, body, dir, altitude);
        }

        private IEnumerable<ProbeWait> PlaceSteps(Vessel vessel, CelestialBody body, Vector3d dir, double altitude)
        {
            Vector3d place = body.position + dir * (body.Radius + altitude);
            vessel.SetPosition(place);
            vessel.SetRotation(Quaternion.FromToRotation(Vector3.up, (Vector3)dir));
            FloatingOrigin.SetOffset(place);
            Krakensbane.ResetVelocityFrame(true);
            vessel.SetWorldVelocity(body.getRFrmVel(vessel.GetWorldPos3D()));
            CheatOptions.IgnoreMaxTemperature = true;
            CheatOptions.NoCrashDamage = true;
            // The vessel is held at rest in the air by a hook that sets its velocity to the ground's every frame (the rigid bodies of its
            // parts are not reachable from this assembly).
            if (!holding)
            {
                holding = true;
                host.PerFrame.Add(HoldVessel);
            }
            yield return ProbeWait.Frames(6);
        }

        private bool holding;

        // The altitude of the last placement.
        private double placedAltitude;

        // Waits until the vessel is in the game for real: loaded, unpacked and out of the launch pad's hold. A vessel that is still
        // packed takes SetPosition but is put back on the pad when it unpacks (K-W3b: the first pose stayed at the pad in daylight,
        // sun +20 degrees against -12 asked for, while the later poses placed well).
        private IEnumerable<ProbeWait> VesselReady()
        {
            yield return ProbeWait.Until(() =>
            {
                Vessel v = FlightGlobals.ActiveVessel;
                return v != null && v.loaded && !v.packed && v.mainBody != null && FlightGlobals.ready;
            }, 120, "the vessel to unpack");
            yield return ProbeWait.Frames(120);
        }

        // Place, then check the sun's elevation at the camera against the wanted one and place again (up to three tries) when it is
        // off by more than the tolerance. The worst error of all poses is the row D38.sky.pose_sun_error_deg.
        private IEnumerable<ProbeWait> PlaceChecked(double latDeg, double sunDeg)
        {
            double error = double.MaxValue;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                double altitude;
                IEnumerable<ProbeWait> steps = Place(latDeg, sunDeg, out altitude);
                placedAltitude = altitude;
                foreach (ProbeWait w in steps) yield return w;
                yield return ProbeWait.Frames(12);
                error = Math.Abs(PoseSunDeg() - sunDeg);
                if (error <= PoseSunToleranceDeg) break;
                Say("placement at " + Num(latDeg) + " degrees latitude: the sun stands " + Num(PoseSunDeg()) + " degrees, " + Num(sunDeg) + " asked for; placing again");
            }
            worstSunError = Math.Max(worstSunError, error);
            if (error > PoseSunToleranceDeg) Fail("D38: the pose at " + Num(latDeg) + " degrees latitude could not be placed with the sun at " + Num(sunDeg) + " degrees (error " + Num(error) + ")");
        }

        private static void HoldVessel()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody == null) return;
            v.SetWorldVelocity(v.mainBody.getRFrmVel(v.GetWorldPos3D()));
        }

        // Turns the flight camera to the compass heading and elevation (it orbits the vessel and looks at it, so the vessel sits at the
        // centre of the picture; the distance keeps it to a speck). The camera's real aim is read back and corrected; the sign of each
        // correction is learnt from whether the error shrank.
        private IEnumerable<ProbeWait> Aim(double azimuthDeg, double elevationDeg)
        {
            FlightCamera fc = FlightCamera.fetch;
            fc.maxDistance = Mathf.Max(fc.maxDistance, CameraDistance + 100f);
            fc.minDistance = Mathf.Min(fc.minDistance, CameraDistance);
            fc.SetDistanceImmediate(CameraDistance);
            fc.camPitch = -(float)(elevationDeg * Math.PI / 180.0);
            fc.camHdg = (float)(WrapDeg(azimuthDeg + 180.0) * Math.PI / 180.0);
            float signH = 1f, signP = 1f;
            double lastEz = double.NaN, lastEe = double.NaN;
            for (int i = 0; i < 12; i++)
            {
                yield return ProbeWait.Frames(3);
                double az, el;
                CameraAim(out az, out el);
                double ez = WrapDeg(azimuthDeg - az), ee = elevationDeg - el;
                if (Math.Abs(ez) < AimToleranceDeg && Math.Abs(ee) < AimToleranceDeg) break;
                if (!double.IsNaN(lastEz))
                {
                    if (Math.Abs(ez) > Math.Abs(lastEz) + 0.5) signH = -signH;
                    if (Math.Abs(ee) > Math.Abs(lastEe) + 0.5) signP = -signP;
                }
                lastEz = ez;
                lastEe = ee;
                fc.camHdg += signH * (float)(ez * Math.PI / 180.0);
                fc.camPitch -= signP * (float)(ee * Math.PI / 180.0);
            }
            yield return ProbeWait.Frames(3);
        }

        // Frames of one pose: off, off again (the noise floor), then the caller's shots. Returns through the fields below.
        // poseEnv is the pixel-wise larger of the two: a brightening only counts as the shell where it exceeds what the sky does by itself
        // between two shell-off frames (clouds, water, exposure, the upscaler's jitter). The noise floor row keeps the raw pair.
        private float[] poseOff, poseNoise, poseEnv;

        private IEnumerable<ProbeWait> OffFrames(float activity, float evt, AuroraSkyTest test, AuroraEvent camEvent)
        {
            poseOff = null;
            poseNoise = null;
            poseEnv = null;
            foreach (ProbeWait w in Show(false, activity, evt, test, camEvent)) yield return w;
            foreach (ProbeWait w in Shot()) yield return w;
            poseOff = lastLuma;
            yield return ProbeWait.Frames(2);
            foreach (ProbeWait w in Shot()) yield return w;
            poseNoise = lastLuma;
            if (poseOff != null && poseNoise != null && poseOff.Length == poseNoise.Length) poseEnv = AuroraMetrics.Envelope(poseOff, poseNoise);
        }

        // Saves the natural view and its difference heat map against the off envelope of the same state as one sheet.
        private void SaveSheet(string name, float[] envelope)
        {
            if (lastPixels != null && envelope != null) SeriesPictures.SaveSheet("D38-" + name, lastPixels, envelope, lastWidth, lastHeight);
        }

        // A ball of flat black, opaque, in the world at a place (the floating origin does not move while the vessel is held).
        private GameObject MakeOccluder(Vector3 centre, float radius, out string shaderName)
        {
            Shader shader = Shader.Find("Legacy Shaders/Diffuse");
            shaderName = "Legacy Shaders/Diffuse";
            Material material;
            if (shader != null)
            {
                material = new Material(shader);
                material.color = Color.black;
            }
            else
            {
                material = ReconMesh.Flat(Color.black, 2000);
                shaderName = "Hidden/Internal-Colored";
            }
            var go = new GameObject("ReDefinition aurora occluder");
            go.AddComponent<MeshFilter>().sharedMesh = ReconMesh.Sphere(radius, 48, 24);
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            go.transform.position = centre;
            return ReconMesh.Track(go);
        }

        private IEnumerable<ProbeWait> SkyScenario()
        {
            host = ReconHost.Get();
            shellGain = AuroraRenderer.ReadLook().Gain;
            SetSetting(AtmospheresSettings.Keys.Aurora, (double)AuroraMode.Shell2D);
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            ClearForced();
            yield return ProbeWait.Until(() => AuroraRenderer.Ready, 60, "the aurora renderer");
            foreach (ProbeWait w in VesselReady()) yield return w;
            worstSunError = 0;
            // The body rule is the premise of every row below: with the rule off the shell is never drawn and the frames only differ by
            // the sky's own movement. The series stops here rather than measure noise.
            {
                CelestialBody ruleBody = FlightGlobals.currentMainBody;
                BodyClimate ruleClimate = ruleBody != null ? Bodies.Get(ruleBody) : null;
                bool ruleOn = ruleClimate != null && ruleClimate.Aurora.Enabled;
                Emit("D38.sky.body_rule_enabled", ruleOn);
                if (!ruleOn)
                {
                    Fail("D38: the AURORA rule of " + (ruleBody != null ? ruleBody.bodyName : "(none)") + " is off, so the shell never draws; the real-sky rows are left out");
                    Cleanup();
                    yield break;
                }
            }

            CelestialBody body = FlightGlobals.currentMainBody;
            EmitText("D38.sky.body", body != null ? body.bodyName : "(none)");
            // The stack this start measured (the rows below are only read as "with Scatterer" when it is there), and the anti-aliasing of
            // the quality level (RECON measured no MSAA case for the depth copy, D38-11).
            var present = new List<string>();
            foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = a.GetName().Name;
                if (n.IndexOf("Scatterer", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("EVEManager", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("TUFX", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Parallax", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Deferred", StringComparison.OrdinalIgnoreCase) >= 0)
                    present.Add(n);
            }
            EmitText("D38.sky.stack", string.Join(",", present.ToArray()));
            Emit("D38.sky.msaa_samples", QualitySettings.antiAliasing);
            Emit("D38.sky.scatterer_present", present.Exists(p => p.IndexOf("Scatterer", StringComparison.OrdinalIgnoreCase) >= 0));

            // ---- The reference pose: lat 70, activity 1, elevation 25, sun -12.
            var reference = new SkyPose { Name = "ref", Lat = 70f, Activity = 1f, Event = 0f, Elevation = 25f };
            foreach (ProbeWait w in PlaceChecked(reference.Lat, WantedSunDeg(reference.Lat))) yield return w;
            double altitude = placedAltitude;
            AuroraFrame refFrame = AuroraModel.FromActivity(reference.Activity, reference.Event, WantedSunDeg(reference.Lat), 60f, 1f);
            foreach (ProbeWait w in Aim(AuroraModel.PoseYawDeg(refFrame.RingLatDeg, reference.Lat), reference.Elevation)) yield return w;
            double aimAz, aimEl;
            CameraAim(out aimAz, out aimEl);
            Emit("D38.sky.aim_error_deg", Math.Max(Math.Abs(WrapDeg(aimAz - AuroraModel.PoseYawDeg(refFrame.RingLatDeg, reference.Lat))), Math.Abs(aimEl - reference.Elevation)));
            Emit("D38.sky.altitude_m", altitude);
            float fov = FlightCamera.fetch.mainCamera.fieldOfView;

            // ---- Which frame source follows the scene: the camera's target, else the screen.
            foreach (ProbeWait w in OffFrames(reference.Activity, 0f, AuroraSkyTest.None, AuroraEvent.AfterForwardAlpha)) yield return w;
            float[] off = poseOff, noise = poseNoise, env = poseEnv;
            foreach (ProbeWait w in Show(true, reference.Activity, 0f, AuroraSkyTest.None, AuroraEvent.AfterForwardAlpha)) yield return w;
            foreach (ProbeWait w in Shot()) yield return w;
            bool live = env != null && lastLuma != null && AuroraMetrics.PeakRowMean(AuroraMetrics.Mask(lastLuma, env), lastWidth, lastHeight) >= AuroraMetrics.PeakMin;
            bool screen = false;
            if (!live)
            {
                host.UseScreen = true;
                screen = true;
                foreach (ProbeWait w in OffFrames(reference.Activity, 0f, AuroraSkyTest.None, AuroraEvent.AfterForwardAlpha)) yield return w;
                off = poseOff;
                noise = poseNoise;
                env = poseEnv;
                foreach (ProbeWait w in Show(true, reference.Activity, 0f, AuroraSkyTest.None, AuroraEvent.AfterForwardAlpha)) yield return w;
                foreach (ProbeWait w in Shot()) yield return w;
                live = env != null && lastLuma != null && AuroraMetrics.PeakRowMean(AuroraMetrics.Mask(lastLuma, env), lastWidth, lastHeight) >= AuroraMetrics.PeakMin;
            }
            SaveSheet("ref-none_after-liveness", env);
            Emit("D38.sky.frame_from_screen", screen);
            Emit("D38.order.frame_live", live);
            if (!live)
            {
                // Nothing else can be read from a frame that does not move with the shell; the rows say so by being missing or false.
                Say("the frame did not change with the shell (target and screen); the real-sky rows are left out");
                Cleanup();
                yield break;
            }

            // ---- Draw order and sky test at the reference pose (D38-01).
            var peaks = new Dictionary<string, double>();
            var lit = new Dictionary<string, double>();
            var refMasks = new Dictionary<string, float[]>();
            double noiseFloor = AuroraMetrics.PeakRowMean(AuroraMetrics.Mask(noise, off), lastWidth, lastHeight);
            foreach (VariantDef v in OrderVariants)
            {
                BeginVariant(v.Name);
                variantSkyRef = v.SkyRef;
                variantSkyTol = v.SkyTol;
                foreach (ProbeWait w in OffFrames(reference.Activity, 0f, v.Test, v.Event)) yield return w;
                float[] vOff = poseOff, vEnv = poseEnv;
                foreach (ProbeWait w in Show(true, reference.Activity, 0f, v.Test, v.Event)) yield return w;
                foreach (ProbeWait w in Shot()) yield return w;
                if (lastLuma != null && vOff != null && vEnv != null)
                {
                    float[] mask = AuroraMetrics.Mask(lastLuma, vEnv);
                    double peak = AuroraMetrics.PeakRowMean(mask, lastWidth, lastHeight);
                    double share = AuroraMetrics.LitShare(mask, 1e-4f);
                    peaks[v.Name] = peak;
                    lit[v.Name] = share;
                    refMasks[v.Name] = mask;
                    noiseFloor = Math.Max(noiseFloor, AuroraMetrics.PeakRowMean(AuroraMetrics.Mask(poseNoise, vOff), lastWidth, lastHeight));
                    Emit("D38.order.peak", peak);
                    Emit("D38.order.lit_share", share);
                    SaveSheet("ref-" + v.Name, vEnv);
                }
                EndVariant();
            }
            variantSkyRef = 0f;
            variantSkyTol = 0f;
            Func<string, double> peakOf = n => { double p; return peaks.TryGetValue(n, out p) ? p : 0.0; };
            Func<string, double> litOf = n => { double p; return lit.TryGetValue(n, out p) ? p : 0.0; };
            Emit("D38.order.default_visible", peakOf("zbuffer_before") >= AuroraMetrics.PeakMin);
            Emit("D38.order.survives_sky", peakOf("zbuffer_after") > 0 ? peakOf("zbuffer_before") / peakOf("zbuffer_after") : 0.0);
            Emit("D38.order.sky_extinction", peakOf("none_after") > 0 ? peakOf("none_before") / peakOf("none_after") : 0.0);
            Emit("D38.order.hardware_matches_none", litOf("none_before") > 0 ? litOf("zbuffer_before") / litOf("none_before") : 0.0);
            Emit("D38.order.copy_depth_visible", peakOf("copy_depth_before") >= AuroraMetrics.PeakMin);
            Emit("D38.order.copy_resolved_visible", peakOf("copy_resolved_before") >= AuroraMetrics.PeakMin);
            Emit("D38.order.copy_global_visible", peakOf("copy_global_before") >= AuroraMetrics.PeakMin);
            Emit("D38.order.copy_global_ref_visible", peakOf("copy_global_ref_before") >= AuroraMetrics.PeakMin);

            // ---- An opaque ball in front of the brightest part of the shell: the hardware test hides the shell behind it, the
            // unrestricted dome (the control) paints over it.
            float[] brightMask;
            if (!refMasks.TryGetValue("none_before", out brightMask)) brightMask = refMasks.ContainsKey("zbuffer_before") ? refMasks["zbuffer_before"] : null;
            if (brightMask != null)
            {
                int cx, cy;
                AuroraMetrics.BrightestBlock(brightMask, lastWidth, lastHeight, Math.Max(16, lastHeight / 12), out cx, out cy);
                Camera cam = FlightCamera.fetch.mainCamera;
                // The target is lastWidth x lastHeight; the camera's pixel size is the same when the frame is read from its target.
                Ray ray = cam.ScreenPointToRay(new Vector3(cx * cam.pixelWidth / (float)lastWidth, cy * cam.pixelHeight / (float)lastHeight, 0f));
                const float distance = 4000f, radius = 400f;
                string shaderName;
                GameObject ball = MakeOccluder(ray.origin + ray.direction * distance, radius, out shaderName);
                EmitText("D38.terrain.shader", shaderName);
                double angular = Math.Asin(radius / distance);
                double radiusPx = AuroraMetrics.SphereRadiusPx(angular, fov, lastHeight);
                int half = (int)(radiusPx * 0.6);
                int x0 = Math.Max(0, cx - half), x1 = Math.Min(lastWidth, cx + half), y0 = Math.Max(0, cy - half), y1 = Math.Min(lastHeight, cy + half);
                var inside = new Dictionary<string, double>();
                foreach (VariantDef v in OrderVariants)
                {
                    if (v.Event != AuroraEvent.BeforeForwardAlpha) continue;
                    BeginVariant(v.Name);
                    variantSkyRef = v.SkyRef;
                    variantSkyTol = v.SkyTol;
                    foreach (ProbeWait w in OffFrames(reference.Activity, 0f, v.Test, v.Event)) yield return w;
                    float[] vEnv = poseEnv;
                    foreach (ProbeWait w in Show(true, reference.Activity, 0f, v.Test, v.Event)) yield return w;
                    foreach (ProbeWait w in Shot()) yield return w;
                    if (lastLuma != null && vEnv != null)
                    {
                        float[] mask = AuroraMetrics.Mask(lastLuma, vEnv);
                        double mean = AuroraMetrics.MeanMask(mask, lastWidth, lastHeight, x0, y0, x1, y1, true);
                        inside[v.Name] = mean;
                        Emit("D38.terrain.inside", mean);
                        SaveSheet("ball-" + v.Name, vEnv);
                    }
                    EndVariant();
                }
                UnityEngine.Object.Destroy(ball);
                variantSkyRef = 0f;
                variantSkyTol = 0f;
                // The control without the ball: the same square is lit by the shell (so that a dark square under the ball is the ball).
                foreach (ProbeWait w in OffFrames(reference.Activity, 0f, AuroraSkyTest.ZBuffer, AuroraEvent.BeforeForwardAlpha)) yield return w;
                float[] cEnv = poseEnv;
                foreach (ProbeWait w in Show(true, reference.Activity, 0f, AuroraSkyTest.ZBuffer, AuroraEvent.BeforeForwardAlpha)) yield return w;
                foreach (ProbeWait w in Shot()) yield return w;
                double control = lastLuma != null && cEnv != null ? AuroraMetrics.MeanMask(AuroraMetrics.Mask(lastLuma, cEnv), lastWidth, lastHeight, x0, y0, x1, y1, true) : 0.0;
                SaveSheet("ball-control-no-ball", cEnv);
                Emit("D38.terrain.control_inside", control);
                Func<string, double> inOf = n => { double p; return inside.TryGetValue(n, out p) ? p : double.NaN; };
                Emit("D38.terrain.control_lit", control >= 1e-3);
                Emit("D38.terrain.default_hides", inOf("zbuffer_before") <= 1e-4 && control >= 1e-3);
                Emit("D38.terrain.none_covers", inOf("none_before") >= 1e-3);
                Emit("D38.terrain.copy_depth_hides", inOf("copy_depth_before") <= 1e-4 && control >= 1e-3);
                Emit("D38.terrain.copy_resolved_hides", inOf("copy_resolved_before") <= 1e-4 && control >= 1e-3);
                Emit("D38.terrain.copy_global_hides", inOf("copy_global_before") <= 1e-4 && control >= 1e-3);
                Emit("D38.terrain.copy_global_ref_hides", inOf("copy_global_ref_before") <= 1e-4 && control >= 1e-3);
            }

            // ---- Lid, drip and band at a night pose per latitude, the default draw (D38-02).
            var poses = new List<SkyPose>();
            foreach (float lat in new[] { 60f, 70f, 78f })
            {
                foreach (float activity in new[] { 1f, 0.6f })
                    poses.Add(new SkyPose { Name = "l" + (int)lat + "_a" + (int)(activity * 10f + 0.5f) + "_e25", Lat = lat, Activity = activity, Event = lat == 60f ? 1f : 0f, Elevation = 25f });
            }
            poses.Add(new SkyPose { Name = "l70_a10_e10", Lat = 70f, Activity = 1f, Event = 0f, Elevation = 10f });
            poses.Add(new SkyPose { Name = "l70_a10_e40", Lat = 70f, Activity = 1f, Event = 0f, Elevation = 40f });
            double worstLid = 0, worstDrip = 0, worstBand = 0;
            int judged = 0, dim = 0;
            foreach (SkyPose pose in poses)
            {
                foreach (ProbeWait w in PlaceChecked(pose.Lat, WantedSunDeg(pose.Lat))) yield return w;
                AuroraFrame frame = AuroraModel.FromActivity(pose.Activity, pose.Event, WantedSunDeg(pose.Lat), 60f, 1f);
                double yaw = AuroraModel.PoseYawDeg(frame.RingLatDeg, pose.Lat);
                foreach (ProbeWait w in Aim(yaw, pose.Elevation)) yield return w;
                double az, el;
                CameraAim(out az, out el);
                foreach (ProbeWait w in OffFrames(pose.Activity, pose.Event, AuroraSkyTest.ZBuffer, AuroraEvent.BeforeForwardAlpha)) yield return w;
                float[] pOff = poseOff, pNoise = poseNoise, pEnv = poseEnv;
                foreach (ProbeWait w in Show(true, pose.Activity, pose.Event, AuroraSkyTest.ZBuffer, AuroraEvent.BeforeForwardAlpha)) yield return w;
                foreach (ProbeWait w in Shot()) yield return w;
                if (lastLuma == null || pOff == null || pNoise == null || pEnv == null)
                {
                    Say("pose " + pose.Name + ": a frame could not be read");
                    dim++;
                    continue;
                }
                double fovNow = FlightCamera.fetch.mainCamera.fieldOfView;
                noiseFloor = Math.Max(noiseFloor, AuroraMetrics.PeakRowMean(AuroraMetrics.Mask(pNoise, pOff), lastWidth, lastHeight));
                AuroraMetrics.Result r = AuroraMetrics.Measure(lastLuma, pEnv, lastWidth, lastHeight, el, fovNow);
                Emit("D38.sky.peak." + pose.Name, r.Peak);
                Emit("D38.sky.sun_deg." + pose.Name, PoseSunDeg());
                Emit("D38.sky.aim_el_deg." + pose.Name, el);
                SaveSheet("pose-" + pose.Name, pEnv);
                if (!r.Judged)
                {
                    dim++;
                    continue;
                }
                judged++;
                worstLid = Math.Max(worstLid, r.Lid);
                worstDrip = Math.Max(worstDrip, r.Drip);
                worstBand = Math.Max(worstBand, r.Band);
                Emit("D38.sky.lid." + pose.Name, r.Lid);
                Emit("D38.sky.drip." + pose.Name, r.Drip);
                Emit("D38.sky.band." + pose.Name, r.Band);
            }
            Emit("D38.lid_worst", worstLid);
            Emit("D38.drip_worst", worstDrip);
            Emit("D38.band_worst", worstBand);
            Emit("D38.sky.poses_judged", judged);
            Emit("D38.sky.poses_dim", dim);
            Emit("D38.sky.noise_floor", noiseFloor);
            Emit("D38.sky.pose_sun_error_deg", worstSunError);

            // ---- Frame time at the dearest pose (lat 60 with the strong event, elevation 10: the longest paths): the mean time of a frame
            // with the shell against without, alternating. The whole frame (CPU and GPU of the game), so only informative; the exclusive
            // GPU figure is the weather player's (shell_gpu_ms_1440p_equivalent).
            {
                double alt;
                foreach (ProbeWait w in Place(60.0, WantedSunDeg(60f), out alt)) yield return w;
                AuroraFrame frame = AuroraModel.FromActivity(1f, 1f, WantedSunDeg(60f), 60f, 1f);
                foreach (ProbeWait w in Aim(AuroraModel.PoseYawDeg(frame.RingLatDeg, 60f), 10.0)) yield return w;
                double onSum = 0, offSum = 0;
                for (int round = 0; round < 3; round++)
                {
                    foreach (ProbeWait w in Show(false, 1f, 1f, AuroraSkyTest.ZBuffer, AuroraEvent.BeforeForwardAlpha)) yield return w;
                    foreach (ProbeWait w in MeanFrameMs(90)) yield return w;
                    offSum += meanFrameMs;
                    foreach (ProbeWait w in Show(true, 1f, 1f, AuroraSkyTest.ZBuffer, AuroraEvent.BeforeForwardAlpha)) yield return w;
                    foreach (ProbeWait w in MeanFrameMs(90)) yield return w;
                    onSum += meanFrameMs;
                }
                Emit("D38.sky.frame_ms_off", offSum / 3.0);
                Emit("D38.sky.frame_ms_on", onSum / 3.0);
                Emit("D38.sky.frame_ms_delta", (onSum - offSum) / 3.0);
            }

            Emit("D38.sky.done", true);
            Cleanup();
        }

        private double meanFrameMs;

        private IEnumerable<ProbeWait> MeanFrameMs(int frames)
        {
            yield return ProbeWait.Frames(10);
            double start = Time.realtimeSinceStartup;
            yield return ProbeWait.Frames(frames);
            meanFrameMs = (Time.realtimeSinceStartup - start) * 1000.0 / frames;
        }

        private void Cleanup()
        {
            ClearForced();
            ReconMesh.DestroyTracked();
            if (host != null) host.PerFrame.Remove(HoldVessel);
            holding = false;
            ReconHost.Release();
            host = null;
        }
    }
}
