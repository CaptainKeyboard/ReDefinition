using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The probe of series K-T2 (package T09, the owner's order of 2026-11-12: the performance of TVC against the own cloud pass). It places the
    // vessel at rest in the air over the launch site at the height of each view, aims the flight camera, and dwells long enough for the GPU profiler
    // (-redefinitionGpuProfile, one report every 10 s) to log at least one clean report inside the view. It writes a begin and an end line per view
    // (T09 view begin / end) and the module's counters; tools/weather_metrics/kt2.py reads the log: the milliseconds of the cloud pass per view are
    // the GPU profiler's segment of the cloud command buffers ("ReDefinition layer pass" and "ReDefinition layer depth" for the own pass, the
    // segments of EVE's cloud buffers for TVC), never a timer around the probe.
    //
    // Variants (one start switches them one after another, -atmoVariants): tvc (cloudSource Eve, TVC draws our maps, the own pass off), plain
    // (cloudSource Ours, reconstruction off), recon4 (reconstruction in 4 x 4 blocks), entry (plain plus the entry refinement). The views:
    // ground (pad, looking up), inside (inside the lowest cumulus layer), above (between the cumulus tops and the cirrus, looking down), orbit_edge
    // (300 km, looking at the limb), sky3 (ground, higher pitch under all layers). The weather is the field's at one universal time chosen by
    // the cover it gives over the site (the first time of the candidates whose layers cover at least half of the sky), the same for every variant.
    // Not run in the game yet [VERIFY]: placement of the vessel at 300 km (the hold works at the heights of K-W3b).
    [AtmospheresModule(985)]
    internal sealed class T09Probe : SeriesProbe
    {
        internal const string Series = "K-T2";
        private const double DwellS = 35.0, CandidateStepS = 6.0 * 3600.0;
        private const int Candidates = 40;
        private const double MinCover = 0.5;

        internal struct View
        {
            internal string Name;
            // Metres above the terrain at the site (the pad view stays on the ground), pitch in degrees above the horizon, heading in degrees.
            internal double AglM, PitchDeg, HeadingDeg;
        }

        // The views of the series. The orbit view's pitch is the horizon's dip at that height, computed when it is placed.
        internal static readonly View[] Views =
        {
            new View { Name = "ground", AglM = 30.0, PitchDeg = 18.0, HeadingDeg = 90.0 },
            new View { Name = "inside", AglM = 4550.0, PitchDeg = 8.0, HeadingDeg = 90.0 },
            new View { Name = "above", AglM = 8300.0, PitchDeg = -25.0, HeadingDeg = 90.0 },
            new View { Name = "orbit_edge", AglM = 300000.0, PitchDeg = double.NaN, HeadingDeg = 90.0 },
            new View { Name = "sky3", AglM = 30.0, PitchDeg = 38.0, HeadingDeg = 270.0 }
        };

        private bool holding;
        private int fewestFrames = int.MaxValue;

        protected override string SeriesName
        {
            get { return Series; }
        }

        protected override double MaxSeconds
        {
            get { return 18 * 60; }
        }

        protected override IList<string> DefaultVariants
        {
            get { return new[] { "plain", "recon4", "entry" }; }
        }

        protected override void ApplyVariant(string name)
        {
            switch (name)
            {
                case "tvc":
                    SetSetting(AtmospheresSettings.Keys.CloudSource, (double)(int)CloudSourceKind.Eve);
                    SetSetting(AtmospheresSettings.Keys.OwnClouds, 0.0);
                    break;
                case "recon4":
                    UseOwn();
                    SetSetting(AtmospheresSettings.Keys.CloudResolution, 4.0);
                    break;
                case "entry":
                    UseOwn();
                    SetSetting(AtmospheresSettings.Keys.CloudEntryRefinement, 1.0);
                    break;
                default:
                    UseOwn();
                    break;
            }
        }

        private void UseOwn()
        {
            SetSetting(AtmospheresSettings.Keys.CloudSource, (double)(int)CloudSourceKind.Ours);
            SetSetting(AtmospheresSettings.Keys.OwnClouds, 1.0);
            SetSetting(AtmospheresSettings.Keys.CloudReconMode, (double)(int)ReconMode.Full);
            SetSetting(AtmospheresSettings.Keys.CloudEntryRefinement, 0.0);
        }

        protected override void RevertVariant(string name)
        {
            ClearForced();
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            yield return ProbeWait.Until(() =>
            {
                Vessel v = FlightGlobals.ActiveVessel;
                return v != null && v.loaded && !v.packed && v.mainBody != null && FlightGlobals.ready && FlightCamera.fetch != null;
            }, 150, "the flight scene with an unpacked vessel");
            yield return ProbeWait.Frames(120);
            Vessel vessel = FlightGlobals.ActiveVessel;
            CelestialBody body = vessel.mainBody;
            if (!body.atmosphere)
            {
                Emit("T09.skipped_no_atmosphere", true);
                yield break;
            }
            double siteLat = vessel.latitude, siteLon = vessel.longitude;
            double ut = ChooseUt(body, siteLat, siteLon);
            ForceUniversalTime(ut);
            EmitText("T09.ut", ut.ToString("0"));
            yield return ProbeWait.Seconds(3);

            foreach (string variant in Variants)
            {
                BeginVariant(variant);
                fewestFrames = int.MaxValue;
                yield return ProbeWait.Seconds(4);
                foreach (View view in Views)
                {
                    foreach (ProbeWait w in Place(body, siteLat, siteLon, view)) yield return w;
                    OwnCloudModule own = OwnCloudModule.Current;
                    int framesBefore = own != null ? own.Stats.FramesDrawn : 0;
                    AtmospheresLog.Say("T09 view begin: " + view.Name + " " + variant + " " + Screen.width + "x" + Screen.height);
                    yield return ProbeWait.Seconds(DwellS);
                    AtmospheresLog.Say("T09 view end: " + view.Name + " " + variant + " " + Screen.width + "x" + Screen.height);
                    if (variant != "tvc")
                    {
                        int drawn = own != null ? own.Stats.FramesDrawn - framesBefore : 0;
                        Emit("T09.view." + view.Name + ".frames_drawn", drawn);
                        EmitText("T09.view." + view.Name + ".mode", own != null ? own.Stats.Mode : "none");
                        fewestFrames = Math.Min(fewestFrames, drawn);
                        Check("the own pass drew in the " + view.Name + " view (" + variant + ")", drawn >= 60, drawn + " frames, why: " + (own != null ? own.Stats.Why : "no module"));
                        if (variant == "recon4") Check("the reconstruction ran in the " + view.Name + " view", own != null && own.Stats.Mode == "reconstruct", own != null ? own.Stats.Mode : "no module");
                    }
                }
                if (variant != "tvc")
                {
                    Emit("T09.view.frames_drawn", fewestFrames);
                    Emit("T09.height_build_worst_ms", OwnCloudModule.Current != null ? OwnCloudModule.Current.Stats.WorstHeightMs : 0.0);
                    Emit("T09.map_fetch_worst_ms", OwnCloudModule.Current != null ? OwnCloudModule.Current.Stats.WorstMapMs : 0.0);
                }
                // (the worst times above are cumulative over the start)
                EndVariant();
            }
            if (host != null) host.PerFrame.Remove(HoldVessel);
            holding = false;
        }

        private ReconHost host;

        // The first candidate time whose sky over the site is covered by at least half of the layers' cover (the field's, the same for TVC and ours);
        // the most covered one where none is.
        private static double ChooseUt(CelestialBody body, double lat, double lon)
        {
            double start = Planetarium.GetUniversalTime(), best = start, bestCover = -1.0;
            for (int k = 0; k < Candidates; k++)
            {
                double t = start + k * CandidateStepS;
                WeatherSample s;
                if (!WeatherHub.Sample(body.flightGlobalsIndex, lat, lon, 3000.0, t, out s)) continue;
                if (s.CloudCover01 > bestCover) { bestCover = s.CloudCover01; best = t; }
                if (s.CloudCover01 >= MinCover) return t;
            }
            return best;
        }

        // The vessel at rest in the air over the site at the view's height, upright, held; the camera aimed. The ground views are 30 m above the terrain.
        private IEnumerable<ProbeWait> Place(CelestialBody body, double lat, double lon, View view)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            double terrain = 0.0;
            try { terrain = body.TerrainAltitude(lat, lon, true); } catch (Exception) { terrain = 0.0; }
            double altitude = Math.Max(0.0, terrain) + view.AglM;
            Vector3d up = body.GetSurfaceNVector(lat, lon);
            Vector3d place = body.GetWorldSurfacePosition(lat, lon, altitude);
            vessel.SetPosition(place);
            vessel.SetRotation(Quaternion.FromToRotation(Vector3.up, (Vector3)up));
            FloatingOrigin.SetOffset(place);
            Krakensbane.ResetVelocityFrame(true);
            vessel.SetWorldVelocity(body.getRFrmVel(vessel.GetWorldPos3D()));
            CheatOptions.IgnoreMaxTemperature = true;
            CheatOptions.NoCrashDamage = true;
            if (!holding)
            {
                holding = true;
                host = ReconHost.Get();
                host.PerFrame.Add(HoldVessel);
            }
            yield return ProbeWait.Frames(30);
            double pitch = view.PitchDeg;
            if (double.IsNaN(pitch))
            {
                // The limb: the horizon lies below the horizontal by acos(R / (R + h)); the camera looks at it.
                double h = vessel.altitude;
                pitch = -Math.Acos(body.Radius / (body.Radius + Math.Max(0.0, h))) * 180.0 / Math.PI;
            }
            FlightCamera fc = FlightCamera.fetch;
            fc.maxDistance = Mathf.Max(fc.maxDistance, 300f);
            fc.minDistance = Mathf.Min(fc.minDistance, 150f);
            fc.SetDistanceImmediate(150f);
            // The camera orbits the vessel and looks at it: a camera below the vessel (a negative camPitch) looks up.
            fc.camPitch = -(float)(pitch * Math.PI / 180.0);
            fc.camHdg = (float)(((view.HeadingDeg + 180.0) % 360.0) * Math.PI / 180.0);
            yield return ProbeWait.Frames(30);
            AtmospheresLog.Say("T09 view placed: " + view.Name + " altitude " + vessel.altitude.ToString("0") + " m, pitch " + pitch.ToString("0.0") + ", situation " + vessel.situation);
        }

        private static void HoldVessel()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody == null) return;
            v.SetWorldVelocity(v.mainBody.getRFrmVel(v.GetWorldPos3D()));
        }
    }
}
