using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // H3: the wind as a force on the parts, for the vessels that H1 does not serve (ModularFlightIntegrator absent, or windInjection
    // Force). For every part the extra force is F(v - w) - F(v), with F(u) = -u^ q(u) A(u^): the dynamic pressure of the air-relative
    // velocity u times the drag area that the part's drag cubes give for that direction, scaled as the stock integrator scales it (the drag
    // cube multiplier and the drag multiplier). It cannot match the stock drag exactly (lift, the cache of the occlusion and the ocean are
    // left out) [VERIFY against a glider: row H1 against H3]; the clamp and the ramp keep an error from becoming a launch.
    // Never together with H1 on one vessel: the plan holds one route.
    public sealed class WindForceModule : VesselModule
    {
        public override Activation GetActivation()
        {
            return Activation.FlightScene | Activation.LoadedVessels;
        }

        public override void OnLoadVessel()
        {
            WindService.MarkUnpacked(vessel);
        }

        public override void OnGoOffRails()
        {
            WindService.MarkUnpacked(vessel);
        }

        public override void OnGoOnRails()
        {
            WindService.Forget(vessel);
        }

        private void OnDestroy()
        {
            if (vessel != null) WindService.Forget(vessel);
        }

        // The number of ticks in which a force was added to a vessel (the probe checks that H1 and H3 never act together).
        internal static int Applications;

        private void FixedUpdate()
        {
            try
            {
                if (vessel == null || WindService.Plan.Route != WindRoute.Force || !WindService.Eligible(vessel)) return;
                Vec3 w = WindService.WindFor(vessel);
                if (w.Length < 1e-3) return;
                Applications++;
                Apply(vessel, w);
            }
            catch (Exception e)
            {
                WindService.FailureCount++;
                AtmospheresLog.Failed("wind-force", "the wind force could not be applied to a vessel", e);
            }
        }

        // The drag force in kN of a part in air that moves past it at u (world), and its direction.
        private static Vector3 DragForce(Part p, Vector3 u, float mach, float multiplier)
        {
            float mag = u.magnitude;
            if (mag < 1e-4f) return Vector3.zero;
            Vector3 dir = u / mag;
            Vector3 local = -p.partTransform.InverseTransformDirection(dir);
            p.DragCubes.SetDrag(local, mach);
            double area = p.DragCubes.AreaDrag * (double)multiplier;
            double q = 0.5 * p.atmDensity * (double)mag * mag * 0.001;
            return -dir * (float)(q * area);
        }

        private static void Apply(Vessel v, Vec3 w)
        {
            var wind = new Vector3((float)w.X, (float)w.Y, (float)w.Z);
            float multiplier = (float)(PhysicsGlobals.DragCubeMultiplier * PhysicsGlobals.DragMultiplier);
            float mach = (float)v.mach;
            var parts = v.parts;
            for (int i = 0; i < parts.Count; i++)
            {
                Part p = parts[i];
                Rigidbody rb = p.Rigidbody;
                // A part in the water is left alone, as the H1 hook leaves it: the air-relative velocity would make a current of the same size
                // in the water's drag.
                if (rb == null || p.ShieldedFromAirstream || p.DragCubes == null || p.DragCubes.None || !(p.atmDensity > 0.0) || p.submergedPortion > 0.0) continue;
                // The drag area over the mass for the clamp, from what the integrator computed for this part this tick.
                WindService.StateOf(v).AddPart(p.dragScalar, p.dynamicPressurekPa);
                Vector3 u0 = p.dragVector;
                if (u0.sqrMagnitude < 1e-6f) u0 = (Vector3)v.srf_velocity;
                Vector3 f0 = DragForce(p, u0, mach, multiplier);
                Vector3 f1 = DragForce(p, u0 - wind, mach, multiplier);
                // The cubes go back to the direction the integrator set for this tick.
                if (p.dragVectorDirLocal != Vector3.zero) p.DragCubes.SetDrag(p.dragVectorDirLocal, mach);
                Vector3 delta = f1 - f0;
                if (float.IsNaN(delta.x) || float.IsNaN(delta.y) || float.IsNaN(delta.z)) continue;
                Vector3 at = rb == p.rb ? p.partTransform.TransformPoint(p.CoPOffset) : rb.worldCenterOfMass;
                rb.AddForceAtPosition(delta, at, ForceMode.Force);
            }
        }
    }
}
