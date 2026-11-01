using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Autopilot.Flight;
using ReDefinition.Autopilot.Guidance;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;
using ReDefinition.Landing;
using ReDefinition.Orbits;
using UnityEngine;

namespace ReDefinition.Autopilot.Background
{
    // A vessel set down is looked at on the ground it will stand on, and then let down on it at the speed of a landing.
    internal sealed class SettleService
    {
        // How long a vessel set down is let down, in seconds, and the vessels in it with the time they began.
        private const float SettleTime = 60f;
        private sealed class Settle
        {
            public float Began;
            // When the vessel came to rest, or -1 while it moves: it is let go three seconds after.
            public float RestSince = -1f;
            // Whether the landing legs have been put out.
            public bool LegsOut;
        }

        private readonly Dictionary<Guid, Settle> settling = new Dictionary<Guid, Settle>();

        // The vessel is loaded after its route, and set down: it is let down slowly from now on.
        internal void Begin(Guid vessel)
        {
            settling[vessel] = new Settle { Began = Time.time };
        }

        // The ground measured under a vessel that is loaded and not yet unpacked: how many physics frames it was looked at, and the last figures.
        private sealed class Survey
        {
            public int Frames, Stable;
            public double Ground, Height;
        }

        private readonly Dictionary<Guid, Survey> surveys = new Dictionary<Guid, Survey>();
        // The frames the vessel is held packed while the ground is built, at most, and the clearance over the measured ground, in m.
        private const int SurveyFrames = 150;
        private const double SurveyClearance = 1.0;
        private static readonly System.Reflection.MethodInfo HoldPacked = typeof(Vessel).GetMethod("SetPhysicsHoldExpiryOverride", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        // The landing legs that are in are put out as the vessel comes down, as a lander has them out when it lands. Symmetry partners go with the leg that is toggled.
        private static void PutLegsOut(Vessel vessel)
        {
            foreach (Part part in vessel.parts)
                foreach (ModuleWheels.ModuleWheelDeployment legs in part.FindModulesImplementing<ModuleWheels.ModuleWheelDeployment>())
                {
                    try
                    {
                        if (legs.fsm == null || !legs.fsm.Started || legs.fsm.CurrentState == null || legs.fsm.CurrentState != legs.st_retracted) continue;
                        if (part.ShieldedFromAirstream && !legs.shieldedCanDeploy) continue;
                        legs.EventToggle();
                        Debug.Log(Log.Tag + " Route: " + vessel.vesselName + " puts the legs of " + part.partInfo.title + " out.");
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning(Log.Tag + " Route: the legs of " + part.partInfo.title + " could not be put out: " + e.Message);
                    }
                }
        }

        // A vessel set down is first looked at on the ground it will stand on, and then let down on it at the speed of a landing.
        internal void Tick(List<RouteEntry> entries)
        {
            if (FlightGlobals.fetch == null) return;
            foreach (RouteEntry entry in entries)
            {
                if (!entry.Down || entry.Surveyed || !RouteTuning.Measure || entry.Wet) continue;
                Vessel packed = FlightGlobals.Vessels.Find(v => v.id == entry.Vessel);
                if (packed != null && packed.loaded && packed.packed && packed == FlightGlobals.ActiveVessel) Look(packed, entry);
            }
            if (settling.Count == 0) return;
            var done = new List<Guid>();
            foreach (KeyValuePair<Guid, Settle> began in settling)
            {
                Vessel vessel = FlightGlobals.Vessels.Find(v => v.id == began.Key);
                if (vessel == null || !vessel.loaded || Time.time - began.Value.Began > SettleTime)
                {
                    done.Add(began.Key);
                    continue;
                }
                if (vessel.packed) continue;
                if (!began.Value.LegsOut)
                {
                    began.Value.LegsOut = true;
                    PutLegsOut(vessel);
                }
                double limit = LandingGuide.TouchdownSpeed, speed = vessel.verticalSpeed;
                if (speed < -limit) vessel.ChangeWorldVelocity(vessel.upAxis * (-limit - speed));
                else if (speed > limit) vessel.ChangeWorldVelocity(-vessel.upAxis * (speed - limit));
                // It does not slide or turn meanwhile: ground a few metres off, or the water, would tip a tall vessel over before it has settled.
                Vector3d sideways = vessel.srf_velocity - vessel.upAxis * Vector3d.Dot(vessel.srf_velocity, vessel.upAxis);
                vessel.ChangeWorldVelocity(-sideways);
                foreach (Part part in vessel.parts)
                    if (part.Rigidbody != null) part.Rigidbody.angularVelocity = Vector3.zero;
                // At rest on the ground for three seconds it is let go.
                if (vessel.Landed && vessel.srfSpeed < 0.05 && Time.time - began.Value.Began > 5f)
                {
                    if (began.Value.RestSince < 0f) began.Value.RestSince = Time.time;
                    else if (Time.time - began.Value.RestSince >= 3f) done.Add(began.Key);
                }
                else began.Value.RestSince = -1f;
            }
            foreach (Guid id in done)
            {
                settling.Remove(id);
                Debug.Log(Log.Tag + " Route: the vessel is let go on the ground.");
            }
        }

        // The ground under a packed vessel is measured as the game builds it, and the vessel is held packed meanwhile.
        // When the figures stand still it is put where the ground is flattest near the place, upright to it and over the highest ground under it.
        private void Look(Vessel vessel, RouteEntry entry)
        {
            Survey look;
            if (!surveys.TryGetValue(vessel.id, out look)) surveys[vessel.id] = look = new Survey();
            if (HoldPacked != null) HoldPacked.Invoke(vessel, new object[] { 5 });
            look.Frames++;
            if (look.Frames < 10) return;
            CelestialBody body = vessel.mainBody;
            TouchdownSite site;
            bool ready = GroundSurvey.Choose(body, entry.DownLatitude, entry.DownLongitude, entry.Lowest, entry.Footprint, SurveyClearance, out site);
            if (ready)
            {
                look.Stable = Math.Abs(site.Ground - look.Ground) < 0.02 && Math.Abs(site.Height - look.Height) < 0.02 ? look.Stable + 1 : 0;
                look.Ground = site.Ground;
                look.Height = site.Height;
            }
            if (ready && look.Stable < 3 && look.Frames < SurveyFrames) return;
            if (!ready)
            {
                if (look.Frames >= SurveyFrames)
                {
                    entry.Surveyed = true;
                    Debug.LogWarning(Log.Tag + " Route: " + entry.Name + ": the ground under it could not be measured; it is let down where it was put.");
                }
                return;
            }
            entry.Surveyed = true;
            surveys.Remove(vessel.id);
            Debug.Log(Log.Tag + " Route: " + entry.Name + ": the ground under it is " + site.Ground.ToString("0.0", CultureInfo.InvariantCulture) + " m, the terrain gave "
                      + body.TerrainAltitude(entry.DownLatitude, entry.DownLongitude, true).ToString("0.0", CultureInfo.InvariantCulture) + " m, " + site.Tilt.ToString("0.0", CultureInfo.InvariantCulture) + " degrees steep, uneven by " + site.Score.ToString("0.0", CultureInfo.InvariantCulture)
                      + " degrees; the vessel is put " + site.Shift.ToString("0") + " m from where it was.");
            Quaternion world = body.bodyTransform.rotation * site.Rotation;
            vessel.SetRotation(world);
            vessel.SetPosition(body.GetWorldSurfacePosition(site.Latitude, site.Longitude, site.Height));
            entry.DownLatitude = site.Latitude;
            entry.DownLongitude = site.Longitude;
            entry.DownHeight = site.Height;
            vessel.latitude = site.Latitude;
            vessel.longitude = site.Longitude;
            vessel.altitude = site.Height;
            vessel.srfRelRotation = site.Rotation;
            ProtoVessel proto = vessel.protoVessel;
            proto.latitude = site.Latitude;
            proto.longitude = site.Longitude;
            proto.altitude = site.Height;
            proto.rotation = site.Rotation;
        }
    }
}
