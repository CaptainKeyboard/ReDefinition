using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // What the composition of the wind at a vessel reads: the merged sample's wind and level, the place and time for the gust, the
    // settings and the node values of the body, and the profile shares.
    internal struct WindInputs
    {
        // The base wind (the merged sample's: the best provider's, else the field's) and the storm's wind (the field's own), in the
        // east-north-up frame of the place, in m/s. Without a foreign provider the two are the same.
        internal double BaseEast, BaseNorth, BaseUp;
        internal double StormEast, StormNorth, StormUp;
        internal double Level01;
        internal double GustFactor;
        internal double Ut;
        // The place in the body-fixed frame, for the travelling gust pattern.
        internal Vector3 BodyFixedPos;
        internal int Seed;
        // The settings.
        internal double Strength, GustStrength;
        // The parts of the wind this mod applies (the single applier's decision).
        internal bool ApplyBase, ApplyStorm;
        // The node values of the body.
        internal double MaxWindMs, ViolentWindMs;
        // The shares from the height above the ground and from the density.
        internal double HeightShare, DensityShare;
    }

    // The composition of the wind that acts on a vessel, as a function (package D31): the base and the storm part of the field's wind, the
    // part this mod applies, the speed cap, the gust, the strength and the profile. One definition, tested on its own.
    internal static class WindCompose
    {
        // The share of the field's wind that is counted as the storm's: 0 in fair weather, 1 from a heavy fall. The field's sample carries
        // one wind and no split, so the split follows the level. What Auto adds beside a foreign base wind is therefore the field's whole
        // wind times this share, not the field's wind minus a base: where the foreign mod pushes 10 m/s and the field says 10 m/s at level
        // 0.8, the vehicle feels about 20 m/s. (WindStatus calls this the "storm delta"; its wording belongs to the settings window.) [VERIFY with the generator's author: a sample with a storm wind of its own
        // would replace this.]
        internal static double StormShare(double level01)
        {
            return WindMath.SmoothStep(PrecipScale.FlurriesFrom, PrecipScale.HeavyFrom, level01);
        }

        // The wind to apply in the east-north-up frame, in m/s. Zero for a calm sky, a wind that is none of this mod's to apply, or no air.
        internal static Vec3 Target(WindInputs i)
        {
            double share = StormShare(i.Level01);
            double b = i.ApplyBase ? 1.0 - share : 0.0, s = i.ApplyStorm ? share : 0.0;
            double e = i.BaseEast * b + i.StormEast * s, n = i.BaseNorth * b + i.StormNorth * s, u = i.BaseUp * b + i.StormUp * s;
            double h = Math.Sqrt(e * e + n * n);
            if (h < 1e-9) return new Vec3(0.0, 0.0, u * i.Strength * i.HeightShare * i.DensityShare);

            // The speed cap on the mean.
            double cap = WindMath.SpeedCap(i.MaxWindMs, i.ViolentWindMs, i.Level01);
            double mean = h > cap ? cap : h;

            // The gust: the travelling wave of the one gust function, scaled as the picture's is (the field's gust factor, and the setting).
            var dir = new Vector3((float)(e / h), (float)(n / h), 0f);
            float gust = WindNoise.Gust(i.Ut, i.BodyFixedPos, dir, (float)i.Level01, i.Seed);
            double gustScale = i.GustFactor > 0.0 ? i.GustFactor : 1.0;
            double factor = 1.0 + (gust - 1.0) * gustScale * i.GustStrength;
            if (factor < 0.0) factor = 0.0;

            double speed = mean * factor * i.Strength * i.HeightShare * i.DensityShare;
            return new Vec3(e / h * speed, n / h * speed, u * i.Strength * i.HeightShare * i.DensityShare);
        }
    }

    // The state of one vessel's wind between physics ticks, and the one step that turns the wind in the world into the wind applied.
    internal sealed class VesselWindState
    {
        // The wind applied last tick, in the world frame, and the UT of that tick.
        internal Vec3 Applied;
        internal double LastUt = double.NaN;
        // The UT at which the vessel last unpacked or loaded; NaN until known.
        internal double UnpackedAtUt = double.NaN;
        // The vessel's drag area over its mass as the last tick measured it, m2 per tonne.
        internal double DragPerMass;
        // The tick the wind was last computed for (so that the parts of one vessel share one value).
        internal long Stamp = -1;
        // The last clamp share, for the log and the probe.
        internal double LastClampShare = 1.0;
        internal bool LastSpinCut;

        // One step. targetWorld is the wind in the world; v the vessel's velocity against the ground in the world; the settings and the
        // body's node values limit it. Returns the wind to apply this tick, in the world.
        internal Vec3 Step(Vec3 targetWorld, Vec3 v, double density, double angularSpeed, double ut, double maxSlew, double maxAccel)
        {
            double dt = double.IsNaN(LastUt) ? 0.0 : ut - LastUt;
            LastUt = ut;
            if (dt < 0.0 || dt > 1.0)
            {
                // A jump of the clock (a rewind, a long warp, a load): start over from the ramp.
                Applied = new Vec3(0.0, 0.0, 0.0);
                UnpackedAtUt = ut;
                dt = 0.0;
            }
            if (double.IsNaN(UnpackedAtUt)) UnpackedAtUt = ut;

            Vec3 target = targetWorld * WindMath.Ramp(ut - UnpackedAtUt);
            LastSpinCut = WindMath.SpinTooFast(angularSpeed);
            if (LastSpinCut || !target.IsFinite) target = new Vec3(0.0, 0.0, 0.0);

            // The step towards the target, limited by the slew rate; then the share of that step that keeps the acceleration it adds under
            // the clamp. The clamp judges the step, not the wind: the extra drag acceleration of the air-relative velocity of the last tick
            // (the ground velocity less the wind applied then) against the same velocity changed by the step. A vessel that drifts with the
            // wind (a canopy at the wind's speed) therefore feels the whole wind; only a step that would push it harder than maxWindAccel is
            // cut, so a jump of the input cannot launch a craft. maxWindAccel (1.5 m/s2) is therefore the limit of the acceleration ADDED BY ONE
            // STEP, not of the total wind-induced acceleration: judged on the total (drag at v - w against drag at v) a canopy at a standing
            // 15 m/s wind (650 m2 per tonne, 1.2 kg/m3) would be held to a wind of a fraction of a metre per second, which is the opposite of
            // what the canopy row asks for (see TheTotalAccelerationOfAStandingWindOnACanopyIsFarOverTheLimit). The consequence: a vessel
            // already in a strong, slowly built wind is limited by the speed cap and the slew, not by this clamp.
            Vec3 step = (dt > 0.0 ? WindMath.Slew(Applied, target, maxSlew, dt) : target) - Applied;
            LastClampShare = 1.0;
            if (step.Length > 1e-9)
            {
                Vec3 relative = v - Applied;
                LastClampShare = WindMath.ClampShare(density, DragPerMass, relative, step, maxAccel);
            }
            Applied = Applied + step * LastClampShare;
            return Applied;
        }

        // The sum of the drag areas (dragScalar over dynamic pressure, m2) of the parts measured during the tick that is running.
        internal double AccDragArea;

        // Closes the measuring tick: the drag area over the mass of the vessel in tonnes becomes DragPerMass.
        internal void CloseTick(double massTonnes)
        {
            if (AccDragArea > 0.0 && massTonnes > 1e-6) DragPerMass = AccDragArea / massTonnes;
            AccDragArea = 0.0;
        }

        // One part's contribution to the drag area, from what the aerodynamics just computed for it.
        internal void AddPart(double dragScalar, double dynamicPressureKpa)
        {
            if (dynamicPressureKpa > 1e-6 && dragScalar > 0.0) AccDragArea += dragScalar / dynamicPressureKpa;
        }

        // The wind does not act on this vessel now: it starts again from the ramp when it does.
        internal void MarkIdle()
        {
            Applied = new Vec3(0.0, 0.0, 0.0);
            LastUt = double.NaN;
            UnpackedAtUt = double.NaN;
            LastClampShare = 1.0;
        }

        // The vessel unpacked or loaded at that UT: the wind ramps from zero again.
        internal void MarkUnpacked(double ut)
        {
            UnpackedAtUt = ut;
            Applied = new Vec3(0.0, 0.0, 0.0);
            LastUt = double.NaN;
        }
    }
}
