using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The wind at the loaded vessels, one value per vessel per physics tick, shared by the two ways it reaches the aerodynamics (the
    // ModularFlightIntegrator override and the force path): they ask the same service, and the plan decides which of them acts, so a vessel
    // never gets both. Everything that touches the game's objects is here and in WindHook, WindForceModule and WindModule; the arithmetic is
    // in WindMath and WindCompose.
    internal static class WindService
    {
        private static readonly Dictionary<Vessel, VesselWindState> States = new Dictionary<Vessel, VesselWindState>();

        // The plan in force (set by WindModule when the settings or the installed mods change). Never null.
        internal static volatile WindPlan Plan = new WindPlan { Route = WindRoute.Off };

        // A stand-in for the body's groundFactor, set by the series probe for the parked-rover rows (above the friction threshold the wind
        // has to be strong on the ground). Null in the game.
        internal static double? GroundFactorOverride;

        // The last wind computed for any vessel, for the log and the probe.
        internal static Vec3 LastApplied;
        internal static Vessel LastVessel;
        internal static double LastClampShare = 1.0;
        internal static int Computations;
        // The vessels that a hook or a force acted on this session.
        internal static int FailureCount;

        internal static VesselWindState StateOf(Vessel vessel)
        {
            VesselWindState s;
            if (!States.TryGetValue(vessel, out s))
            {
                s = new VesselWindState();
                States[vessel] = s;
            }
            return s;
        }

        internal static void Forget(Vessel vessel)
        {
            States.Remove(vessel);
        }

        internal static void ForgetAll()
        {
            States.Clear();
        }

        internal static void MarkUnpacked(Vessel vessel)
        {
            if (vessel == null) return;
            double ut = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0.0;
            StateOf(vessel).MarkUnpacked(ut);
        }

        // Whether the wind may act on a vessel at all this tick.
        internal static bool Eligible(Vessel v)
        {
            if (v == null || !v.loaded || v.packed) return false;
            if (!AtmospheresSettings.Enabled || !AtmospheresSettings.WeatherEffects || !AtmospheresSettings.WindForces) return false;
            if (!Plan.AppliesAnything) return false;
            CelestialBody body = v.mainBody;
            if (body == null || !body.atmosphere) return false;
            return true;
        }

        // The wind to apply to a vessel this physics tick, in the world frame. Computed once per tick and vessel; zero where it must not act.
        internal static Vec3 WindFor(Vessel v)
        {
            VesselWindState state = StateOf(v);
            long stamp = StampNow();
            if (state.Stamp == stamp) return state.Applied;
            state.Stamp = stamp;
            // The drag area measured over the last tick closes; the next one starts.
            state.CloseTick(v.GetTotalMass());
            Vec3 applied = new Vec3(0.0, 0.0, 0.0);
            try
            {
                if (Eligible(v)) applied = Compute(v, state);
                else state.MarkIdle();
            }
            catch (Exception e)
            {
                FailureCount++;
                AtmospheresLog.Failed("wind-compute", "the wind at a vessel could not be computed and is zero for it", e);
                state.MarkIdle();
            }
            LastApplied = applied;
            LastVessel = v;
            LastClampShare = state.LastClampShare;
            Computations++;
            return applied;
        }

        // One value per physics step: the fixed time in milliseconds.
        internal static long StampNow()
        {
            return (long)Math.Round(Time.fixedTime * 1000.0);
        }

        private static Vec3 Compute(Vessel v, VesselWindState state)
        {
            CelestialBody body = v.mainBody;
            BodyClimate climate = Bodies.Get(body);
            if (climate == null || !climate.Status.WeatherRuns)
            {
                state.MarkIdle();
                return new Vec3(0.0, 0.0, 0.0);
            }
            WindRules rules = climate.Wind;
            WindPlan plan = Plan;
            double ut = Planetarium.GetUniversalTime();
            double lat = v.latitude, lon = v.longitude, alt = v.altitude;

            WeatherSample merged;
            if (!WeatherHub.Sample(body.flightGlobalsIndex, lat, lon, alt, ut, out merged)) merged = WeatherSample.Clear(body.flightGlobalsIndex);
            WeatherSample own = merged;
            // Where a foreign provider owns the merged wind the storm's own wind is the field's.
            if (plan.ForeignOwner != null || WeatherHub.SourceCount > 0)
            {
                if (!WeatherHub.Field.Sample(body.flightGlobalsIndex, lat, lon, alt, ut, out own)) own = WeatherSample.Clear(body.flightGlobalsIndex);
            }

            bool landed = v.LandedOrSplashed;
            double height = landed ? 0.0 : v.heightFromTerrain;
            double heightShare = WindMath.HeightProfile(height, GroundFactorOverride ?? rules.GroundFactor, rules.FullHeightM);
            if (landed && !(AtmospheresSettings.WindOnLanded && rules.WindOnLanded)) heightShare = 0.0;

            var input = new WindInputs
            {
                BaseEast = merged.WindWorld.x, BaseNorth = merged.WindWorld.y, BaseUp = merged.WindWorld.z,
                StormEast = own.WindWorld.x, StormNorth = own.WindWorld.y, StormUp = own.WindWorld.z,
                Level01 = Math.Max(merged.Level01, own.Level01),
                GustFactor = own.GustFactor > 0f ? own.GustFactor : merged.GustFactor,
                Ut = ut,
                BodyFixedPos = BodyFixed(body, lat, lon, alt),
                Seed = climate.Seed,
                Strength = AtmospheresSettings.WindStrength,
                GustStrength = AtmospheresSettings.GustStrength,
                ApplyBase = plan.ApplyBase,
                ApplyStorm = plan.ApplyStorm,
                MaxWindMs = rules.MaxWindMs,
                ViolentWindMs = rules.ViolentWindMs,
                HeightShare = heightShare,
                DensityShare = WindMath.DensityProfile(v.atmDensity)
            };
            Vec3 enu = WindCompose.Target(input);

            // The world's east, north and up at the vessel, from the body's own frame (it rotates; the origin floats).
            Vec3 e, n, up;
            Frame(body, lat, lon, out e, out n, out up);
            Vec3 targetWorld = WindMath.EnuToWorld(enu.X, enu.Y, enu.Z, e, n, up);

            Vector3d sv = v.srf_velocity;
            var vel = new Vec3(sv.x, sv.y, sv.z);
            double spin = v.angularVelocity.magnitude;
            double maxSlew = rules.MaxSlewMsPerS;
            double maxAccel = rules.MaxAccelMs2;
            return state.Step(targetWorld, vel, v.atmDensity, spin, ut, maxSlew, maxAccel);
        }

        // The east, north and up unit vectors in the world at a place, from the body's frame.
        internal static void Frame(CelestialBody body, double latDeg, double lonDeg, out Vec3 east, out Vec3 north, out Vec3 up)
        {
            const double Step = 0.01;
            Vector3d u = body.GetRelSurfaceNVector(latDeg, lonDeg);
            Vector3d ue = body.GetRelSurfaceNVector(latDeg, lonDeg + Step);
            double latUp = latDeg + Step;
            if (latUp > 89.99) latUp = latDeg - Step;
            Vector3d un = body.GetRelSurfaceNVector(latUp, lonDeg);
            Vector3d eV = ue - u, nV = un - u;
            eV -= u * Vector3d.Dot(eV, u);
            nV -= u * Vector3d.Dot(nV, u);
            if (latUp < latDeg) nV = -nV;
            eV = eV.normalized;
            nV = nV.normalized;
            east = new Vec3(eV.x, eV.y, eV.z);
            north = new Vec3(nV.x, nV.y, nV.z);
            up = new Vec3(u.x, u.y, u.z);
        }

        private static Vector3 BodyFixed(CelestialBody body, double latDeg, double lonDeg, double alt)
        {
            double r = body.Radius + alt;
            double la = latDeg * Math.PI / 180.0, lo = lonDeg * Math.PI / 180.0;
            return new Vector3((float)(r * Math.Cos(la) * Math.Cos(lo)), (float)(r * Math.Cos(la) * Math.Sin(lo)), (float)(r * Math.Sin(la)));
        }
    }
}
