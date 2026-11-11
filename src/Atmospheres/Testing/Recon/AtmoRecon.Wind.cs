using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The hook of the wind experiment (premise P4, the plan's E1): a ModularFlightIntegrator override of UpdateAerodynamics(Part) that
    // lowers the part's rigidbody velocity by a constant wind for the duration of the base call, so that the stock aerodynamics (or the
    // replacement KSPCommunityFixes installs) compute the drag against the moving air, and puts the velocity back. The body shifted is the
    // part's own rigidbody or, for a part without one, the first rigidbody up the parent chain (the body the aerodynamics read).
    // ModularFlightIntegrator is reached by reflection; MIT (sarbian, GameData/ModularFlightIntegrator/LICENSE.txt).
    // [VERIFY] The Register* methods keep one function per slot; the call returns false when another mod holds the slot.
    internal static class ReconWindHook
    {
        // The wind in world axes, in m/s; zero switches the hook's effect off while the override stays registered.
        internal static Vector3 Wind;

        internal static bool Registered;
        internal static string Status = "not tried";
        internal static int Calls, Shifted;

        // The last values of the aerodynamics of the part the sampler looks at (the pod's root part).
        internal static Part Watched;
        internal static float WatchedDragMagnitude, WatchedDragScalar;
        internal static double WatchedDynamicPressure;
        internal static int Samples;
        internal static double SumDragMagnitude, SumDragScalar;

        // The peak, since the last reset, of the horizontal part of the watched part's dragVector, taken in the hook once per physics tick.
        internal static double PeakHorizontal;

        private static PropertyInfo velocityProperty;
        private static MethodInfo baseUpdate;

        internal static void Register(Action<string> say)
        {
            try
            {
                Type integrator = ReconDyn.Type("ModularFI.ModularFlightIntegrator");
                if (integrator == null)
                {
                    Status = "ModularFlightIntegrator is not loaded";
                    say("P4: " + Status);
                    return;
                }
                Type delegateType = integrator.GetNestedType("voidPartDelegate", BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo register = integrator.GetMethod("RegisterUpdateAerodynamicsOverride", BindingFlags.Public | BindingFlags.Static);
                baseUpdate = integrator.GetMethod("BaseFIUpdateAerodynamics", BindingFlags.Public | BindingFlags.Instance);
                if (delegateType == null || register == null || baseUpdate == null)
                {
                    Status = "the integrator's API differs: delegate " + (delegateType != null) + ", register " + (register != null) + ", base " + (baseUpdate != null);
                    say("P4: " + Status);
                    return;
                }
                MethodInfo hook = typeof(ReconWindHook).GetMethod("UpdateAerodynamics", BindingFlags.Static | BindingFlags.NonPublic);
                Delegate function = Delegate.CreateDelegate(delegateType, hook);
                object result = register.Invoke(null, new object[] { function });
                Registered = result is bool && (bool)result;
                Status = Registered ? "registered" : "the slot is taken by another mod";
                say("P4: UpdateAerodynamics override " + Status);
            }
            catch (Exception e)
            {
                Status = "registration failed: " + e.GetType().Name + " " + e.Message;
                say("P4: " + Status);
            }
        }

        // The rigidbody of a part, or of the nearest ancestor that has one (a physicsless part moves with its parent's body); null if none.
        internal static object BodyOf(Part part)
        {
            for (Part p = part; p != null; p = p.parent)
            {
                object body = ReconDyn.Get(p, "Rigidbody");
                var unityObject = body as UnityEngine.Object;
                if (body != null && unityObject != null) return body;
            }
            return null;
        }

        // Whether the part has a rigidbody of its own (false: it is physicsless and rides on its parent's body).
        internal static bool HasOwnBody(Part part)
        {
            var unityObject = ReconDyn.Get(part, "Rigidbody") as UnityEngine.Object;
            return unityObject != null;
        }

        // The override: shift the velocity, run the base, put the velocity back.
        private static void UpdateAerodynamics(object integrator, Part part)
        {
            Calls++;
            object body = null;
            Vector3 velocity = Vector3.zero;
            bool shifted = false;
            try
            {
                if (Wind != Vector3.zero && part != null)
                {
                    body = BodyOf(part);
                    if (body != null)
                    {
                        if (velocityProperty == null) velocityProperty = body.GetType().GetProperty("velocity");
                        velocity = (Vector3)velocityProperty.GetValue(body, null);
                        velocityProperty.SetValue(body, velocity - Wind, null);
                        shifted = true;
                        Shifted++;
                    }
                }
            }
            catch (Exception)
            {
                shifted = false;
            }
            try
            {
                baseUpdate.Invoke(integrator, new object[] { part });
            }
            finally
            {
                if (shifted) velocityProperty.SetValue(body, velocity, null);
            }
            if (part != null && part == Watched)
            {
                WatchedDragMagnitude = part.dragVectorMag;
                WatchedDragScalar = part.dragScalar;
                WatchedDynamicPressure = part.dynamicPressurekPa;
                Samples++;
                // Once per physics tick: the horizontal part of the dragVector the aerodynamics just computed.
                if (part.vessel != null && part.vessel.mainBody != null)
                {
                    Vector3 up = ((Vector3)(part.vessel.CoMD - part.vessel.mainBody.position)).normalized;
                    PeakHorizontal = Math.Max(PeakHorizontal, Vector3.ProjectOnPlane(part.dragVector, up).magnitude);
                }
                SumDragMagnitude += part.dragVectorMag;
                SumDragScalar += part.dragScalar;
            }
        }
    }

    // P4: a constant wind on a parachute drop. The pod hangs under its canopy over the sea; for 15 s there is no wind (the control), then a
    // wind of 20 m/s blows for 30 s along the bearing from the pad to the pod. The drift along it in each period is compared with a drag model whose only input is the
    // measured descent speed (ReconMath.PredictChuteDrift).
    internal sealed partial class AtmoRecon
    {
        private const double WindSpeed = 20.0, ControlSeconds = 15.0, WindSeconds = 30.0, SettleSeconds = 6.0;

        private IEnumerable<ProbeWait> WindDrop()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            Emit("P4.mfi_present", ReconDyn.Type("ModularFI.ModularFlightIntegrator") != null);
            Emit("P4.override_registered", ReconWindHook.Registered);
            EmitText("P4.override_status", ReconWindHook.Status);
            Emit("P4.field_dragVector", typeof(Part).GetField("dragVector") != null);
            Emit("P4.field_dragVelocity", typeof(Part).GetField("dragVelocity") != null);
            Emit("P4.kspcf_present", AssemblyLoaded("KSPCommunityFixes"));

            ModuleParachute chute = vessel.FindPartModuleImplementing<ModuleParachute>();
            if (chute == null)
            {
                EmitText("P4.error", "the pod has no ModuleParachute");
                foreach (ProbeWait w in RescuePod(vessel)) yield return w;
                yield break;
            }
            ReconWindHook.Watched = chute.part;
            ReconWindHook.Wind = Vector3.zero;
            // Which body the hook shifts: the canopy's own (false means the canopy is physicsless and rides on its parent's body).
            Emit("P4.chute_rb_own", ReconWindHook.HasOwnBody(chute.part));
            Emit("P4.chute_rb_found", ReconWindHook.BodyOf(chute.part) != null);

            // A deployment altitude above the drop: the canopy opens fully at once, so that the measurement does not wait for the
            // altitude the stock value asks for.
            chute.deployAltitude = 20000f;
            chute.Deploy();
            foreach (ProbeWait w in WaitFor(() => chute.deploymentState == ModuleParachute.deploymentStates.DEPLOYED, 60)) yield return w;
            Emit("P4.chute_deployed", waited);
            if (!waited)
            {
                EmitText("P4.error", "the canopy did not open fully, state " + chute.deploymentState + ", altitude " + Num(vessel.altitude));
                ReconWindHook.Watched = null;
                foreach (ProbeWait w in RescuePod(vessel)) yield return w;
                yield break;
            }
            yield return ProbeWait.Seconds(SettleSeconds);

            CelestialBody body = vessel.mainBody;

            // The pod should hang at rest with the ground below: a horizontal speed here would be a residue of the start orbit and would
            // spoil the control.
            Emit("P4.horizontal_speed_before", vessel.horizontalSrfSpeed);

            // Control: no wind.
            double lat0 = vessel.latitude, lon0 = vessel.longitude;
            var descent = new List<double>();
            ReconWindHook.Samples = 0;
            ReconWindHook.SumDragMagnitude = ReconWindHook.SumDragScalar = 0;
            double peakDragControl = 0;
            ReconWindHook.PeakHorizontal = 0;
            foreach (ProbeWait w in SampleFor(ControlSeconds, () =>
            {
                descent.Add(-vessel.verticalSpeed);
                peakDragControl = Math.Max(peakDragControl, HorizontalDrag(vessel, chute.part));
            })) yield return w;
            Emit("P4.control_dragvector_horizontal_ms", ReconWindHook.PeakHorizontal);
            Emit("P4.control_dragvector_horizontal_frame_ms", peakDragControl);
            double eastA, northA;
            Displacement(body, lat0, lon0, vessel.latitude, vessel.longitude, vessel.altitude, out eastA, out northA);
            double controlDrag = ReconWindHook.Samples > 0 ? ReconWindHook.SumDragMagnitude / ReconWindHook.Samples : double.NaN;
            double vt = 0;
            int tail = Math.Max(1, descent.Count / 3);
            for (int i = descent.Count - tail; i < descent.Count; i++) vt += descent[i] / tail;
            double g = body.gravParameter / ((body.Radius + vessel.altitude) * (body.Radius + vessel.altitude));
            Emit("P4.chute_vt_ms", vt);
            double alongA = AlongBearing(eastA, northA);
            Emit("P4.control_east_m", eastA);
            Emit("P4.control_north_m", northA);
            Emit("P4.control_along_m", alongA);
            Emit("P4.control_drag_mag", controlDrag);
            double predictedA = ReconMath.PredictChuteDrift(WindSpeed, vt, g, ControlSeconds);
            Emit("P4.control_ratio", predictedA > 0 ? Math.Abs(alongA) / predictedA : double.NaN);

            // The wind blows along the bearing from the pad to the pod (away from the coast, so that the canopy comes down at sea). The
            // north and east at the pod come from the world positions of two nearby places, which carry the body's rotation and need no
            // assumption about the handedness of the axes; the wind is fixed in world axes for the period.
            Vector3d here = body.GetWorldSurfacePosition(vessel.latitude, vessel.longitude, vessel.altitude);
            Vector3d northDirection = (body.GetWorldSurfacePosition(vessel.latitude + 0.001, vessel.longitude, vessel.altitude) - here).normalized;
            Vector3d eastDirection = (body.GetWorldSurfacePosition(vessel.latitude, vessel.longitude + 0.001, vessel.altitude) - here).normalized;
            double bearing = coastBearing * Math.PI / 180.0;
            Vector3d windDirection = (northDirection * Math.Cos(bearing) + eastDirection * Math.Sin(bearing)).normalized;
            ReconWindHook.Wind = (Vector3)windDirection * (float)WindSpeed;
            Emit("P4.wind_bearing_deg", coastBearing);
            ReconWindHook.Shifted = 0;
            ReconWindHook.Samples = 0;
            ReconWindHook.SumDragMagnitude = ReconWindHook.SumDragScalar = 0;
            lat0 = vessel.latitude;
            lon0 = vessel.longitude;
            double altitudeB = vessel.altitude;
            // The mechanism: the horizontal part of the canopy's own dragVector (the velocity relative to the air the aerodynamics used)
            // at its peak during the wind period. The drift is measured over the last half of the period, after the pod has swung into
            // the relative wind; the model assumes the drag is already aligned.
            double peakDragWind = 0, startUt = Planetarium.GetUniversalTime(), midLat = double.NaN, midLon = double.NaN, midAlt = 0;
            ReconWindHook.PeakHorizontal = 0;
            foreach (ProbeWait w in SampleFor(WindSeconds, () =>
            {
                peakDragWind = Math.Max(peakDragWind, HorizontalDrag(vessel, chute.part));
                if (double.IsNaN(midLat) && Planetarium.GetUniversalTime() - startUt >= 0.5 * WindSeconds)
                {
                    midLat = vessel.latitude;
                    midLon = vessel.longitude;
                    midAlt = vessel.altitude;
                }
            })) yield return w;
            ReconWindHook.Wind = Vector3.zero;
            double eastB, northB;
            Displacement(body, lat0, lon0, vessel.latitude, vessel.longitude, vessel.altitude, out eastB, out northB);
            double windDrag = ReconWindHook.Samples > 0 ? ReconWindHook.SumDragMagnitude / ReconWindHook.Samples : double.NaN;
            double alongB = AlongBearing(eastB, northB);
            double eastLate = double.NaN, northLate = double.NaN, alongLate = double.NaN;
            if (!double.IsNaN(midLat))
            {
                Displacement(body, midLat, midLon, vessel.latitude, vessel.longitude, vessel.altitude, out eastLate, out northLate);
                alongLate = AlongBearing(eastLate, northLate);
            }
            double predicted = ReconMath.PredictChuteDrift(WindSpeed, vt, g, WindSeconds);
            double predictedLate = ReconMath.PredictChuteDriftBetween(WindSpeed, vt, g, 0.5 * WindSeconds, WindSeconds);
            Emit("P4.dragvector_horizontal_ms", ReconWindHook.PeakHorizontal);
            Emit("P4.dragvector_horizontal_frame_ms", peakDragWind);
            Emit("P4.wind_east_m", eastB);
            Emit("P4.wind_north_m", northB);
            Emit("P4.wind_along_m", alongB);
            Emit("P4.wind_predicted_m", predicted);
            Emit("P4.wind_along_late_m", alongLate);
            Emit("P4.wind_predicted_late_m", predictedLate);
            Emit("P4.wind_error_rel", predictedLate > 0 && !double.IsNaN(alongLate) ? Math.Abs(alongLate - predictedLate) / predictedLate : double.NaN);
            Emit("P4.wind_error_full_rel", predicted > 0 ? Math.Abs(alongB - predicted) / predicted : double.NaN);
            Emit("P4.wind_drag_mag", windDrag);
            Emit("P4.hook_calls", ReconWindHook.Calls);
            Emit("P4.hook_shifted", ReconWindHook.Shifted);
            Emit("P4.srf_speed_after", vessel.horizontalSrfSpeed);
            Say("P4: vt " + Num(vt) + " m/s, control drift " + Num(alongA) + " m (model " + Num(predictedA) + "), wind drift " + Num(alongB)
                + " m (model " + Num(predicted) + "), drop " + Num(altitudeB - vessel.altitude) + " m");

            // On down to the sea, for the flight that follows.
            foreach (ProbeWait w in WaitFor(() => vessel.Splashed || vessel.altitude < 3.0, 240)) yield return w;
            Say("P4: the pod is " + vessel.situation + " at " + Num(vessel.altitude) + " m");
            ReconWindHook.Watched = null;
            yield return ProbeWait.Seconds(8);
        }

        // A pod that is still in the air after a step that could not use it goes down to the sea at rest, so that the steps that follow
        // (P11, P8, P6) are not lost to a crash at terminal speed.
        private IEnumerable<ProbeWait> RescuePod(Vessel vessel)
        {
            CelestialBody body = vessel.mainBody;
            if (vessel.Splashed || vessel.altitude < 3.0) yield break;
            Say("P4: the pod is put on the sea at rest (altitude " + Num(vessel.altitude) + " m)");
            vessel.SetPosition(body.GetWorldSurfacePosition(vessel.latitude, vessel.longitude, 3.0));
            vessel.ChangeWorldVelocity(-vessel.srf_velocity); // [VERIFY] srf_velocity is the world-frame velocity against the ground
            foreach (ProbeWait w in WaitFor(() => vessel.Splashed, 60)) yield return w;
            Emit("P4.rescued_to_sea", waited);
            yield return ProbeWait.Seconds(3);
        }

        // The length of the horizontal part of a part's dragVector (the velocity against the air as the aerodynamics last computed it).
        private static double HorizontalDrag(Vessel vessel, Part part)
        {
            if (vessel == null || part == null) return 0;
            Vector3 up = ((Vector3)(vessel.CoMD - vessel.mainBody.position)).normalized;
            return Vector3.ProjectOnPlane(part.dragVector, up).magnitude;
        }

        // Calls an action every frame for the game-time seconds given; real time caps it, so that a clock that stops cannot hold the probe.
        private IEnumerable<ProbeWait> SampleFor(double seconds, Action each)
        {
            double end = Planetarium.GetUniversalTime() + seconds;
            double realEnd = Time.realtimeSinceStartup + 3.0 * seconds + 20.0;
            while (Planetarium.GetUniversalTime() < end && Time.realtimeSinceStartup < realEnd)
            {
                if (each != null) each();
                yield return ProbeWait.Frames(1);
            }
        }

        // The part of a displacement (east, north in metres) that lies along the compass bearing of the wind.
        private double AlongBearing(double east, double north)
        {
            double b = coastBearing * Math.PI / 180.0;
            return east * Math.Sin(b) + north * Math.Cos(b);
        }

        // The ground distance moved east and north between two places, in metres, on a body at an altitude.
        private static void Displacement(CelestialBody body, double lat0, double lon0, double lat1, double lon1, double altitude, out double east, out double north)
        {
            double radius = body.Radius + altitude;
            double dLon = lon1 - lon0;
            while (dLon > 180.0) dLon -= 360.0;
            while (dLon < -180.0) dLon += 360.0;
            north = (lat1 - lat0) * Math.PI / 180.0 * radius;
            east = dLon * Math.PI / 180.0 * radius * Math.Cos(0.5 * (lat0 + lat1) * Math.PI / 180.0);
        }
    }
}
