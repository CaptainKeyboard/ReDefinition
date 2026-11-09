using System.Collections.Generic;
using System;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // How fast what flies a step can turn (docs/player/planner.md) is the time to
    // turn it by 90 degrees about its pitch or yaw axis, whichever is slower. It
    // speeds up over the first half and slows down over the second at the
    // most its torque allows, from rest to rest (2 * sqrt(angle / acceleration)).
    //
    // Its torque is as KSP's own ITorqueProvider modules give theirs (decompiled).
    // Reaction wheels give their PitchTorque and YawTorque at their authority limiter
    // (ModuleReactionWheel). RCS thrusters give each nozzle its thrust times its
    // lever about the centre of mass, those that turn one way against those that
    // turn the other, the weaker of the two counting (ModuleRCS). Gimballing
    // engines, while they burn, give their thrust times the sine of their gimbal range
    // times their distance from the centre of mass (ModuleGimbal). Its moment of
    // inertia is as AtmosphereAutopilot takes a vessel's (RotationModel.get_moments):
    // each part's mass at its distance from the centre of mass, plus the part's
    // own, here a solid box of its colliders' size. Torque in kN m over inertia in
    // t m^2 gives radians per second squared.
    internal static class Turning
    {
        // A quarter turn, in radians: the turn the time is given for.
        internal const double Angle = Math.PI / 2.0;

        // `members`: the parts that fly, by index; their masses as stage `stage`
        // starts (-1: as the vessel starts). `burning`: the stage whose engines
        // burn, their gimbals counting; -1 for none. Seconds, or infinity where it
        // cannot turn; NaN where nothing flies.
        internal static double Seconds(IList<PartFacts> parts, IEnumerable<int> members, int stage, int burning)
        {
            List<int> flying = new List<int>();
            double total = 0.0;
            Vector3d centre = Vector3d.zero;
            foreach (int at in members)
            {
                if (at < 0 || at >= parts.Count || parts[at].Clamp) continue;
                if (!parts[at].AttachedIn(stage)) continue;
                double mass = MassAt(parts[at], stage);
                if (mass <= 0.0) continue;
                flying.Add(at);
                total += mass;
                centre += (Vector3d)parts[at].Position * mass;
            }
            if (total <= 0.0) return double.NaN;
            centre /= total;

            double pitchInertia = 0.0, yawInertia = 0.0;
            double pitchTorque = 0.0, yawTorque = 0.0;
            double pitchUp = 0.0, pitchDown = 0.0, yawUp = 0.0, yawDown = 0.0;
            foreach (int at in flying)
            {
                PartFacts part = parts[at];
                double mass = MassAt(part, stage);
                Vector3d offset = (Vector3d)part.Position - centre;
                Vector3d size = part.Size;
                // Pitch about x, yaw about z, as KSP's torque vectors have them.
                pitchInertia += mass * (offset.y * offset.y + offset.z * offset.z) + mass / 12.0 * (size.y * size.y + size.z * size.z);
                yawInertia += mass * (offset.x * offset.x + offset.y * offset.y) + mass / 12.0 * (size.x * size.x + size.y * size.y);
                pitchTorque += part.WheelTorque.x;
                yawTorque += part.WheelTorque.z;
                foreach (Nozzle nozzle in part.Nozzles)
                {
                    Vector3d torque = Vector3d.Cross((Vector3d)nozzle.At - centre, (Vector3d)nozzle.Force);
                    if (nozzle.Pitch)
                    {
                        if (torque.x > 0.0) pitchUp += torque.x;
                        else pitchDown -= torque.x;
                    }
                    if (nozzle.Yaw)
                    {
                        if (torque.z > 0.0) yawUp += torque.z;
                        else yawDown -= torque.z;
                    }
                }
                if (burning >= 0 && part.GimbalSide > 0.0 && part.ActivatesAt >= burning && part.AttachedIn(burning))
                {
                    double lever = offset.magnitude;
                    pitchTorque += part.GimbalSide * lever;
                    yawTorque += part.GimbalSide * lever;
                }
            }
            pitchTorque += Math.Min(pitchUp, pitchDown);
            yawTorque += Math.Min(yawUp, yawDown);
            return Math.Max(Time(pitchTorque, pitchInertia), Time(yawTorque, yawInertia));
        }

        // From rest to rest through Angle at the torque's acceleration; infinity without torque, 0 without inertia.
        private static double Time(double torque, double inertia)
        {
            if (inertia <= 0.0) return 0.0;
            if (torque <= 0.0) return double.PositiveInfinity;
            return 2.0 * Math.Sqrt(Angle / (torque / inertia));
        }

        // A part's mass as stage `stage` starts. It is its dry mass and what its
        // resources hold then (PartFacts.StageFuel: per stage its resources' mass as
        // the stage starts and ends). Where the stage leaves them alone, it counts what the
        // last stage before it left, else all it holds.
        internal static double MassAt(PartFacts part, int stage)
        {
            if (stage < 0) return part.Mass;
            double[] mass;
            if (part.StageFuel.TryGetValue(stage, out mass) && mass != null && mass.Length >= 2) return part.DryMass + mass[0];
            int before = int.MaxValue;
            double[] left = null;
            foreach (KeyValuePair<int, double[]> entry in part.StageFuel)
                if (entry.Key > stage && entry.Key < before && entry.Value != null && entry.Value.Length >= 2)
                {
                    before = entry.Key;
                    left = entry.Value;
                }
            return left != null ? part.DryMass + left[1] : part.Mass;
        }
    }

    // One RCS nozzle: where it sits and the force it pushes with at full thrust, in
    // kN, in the vessel's frame; the axes it fires for.
    internal struct Nozzle
    {
        // Where it sits and the force at full thrust, in the vessel's frame; whether it fires for pitch, and for yaw.
        public Vector3 At;
        public Vector3 Force;
        public bool Pitch;
        public bool Yaw;
    }
}
