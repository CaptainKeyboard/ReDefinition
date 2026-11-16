using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace ProbeLite
{
    // The poses of the pictures: K-S2's (tests/series/K-S2.json, SurfaceGroundProbe): the far pose 300 m over the pad's vessel with the camera 100 m
    // behind it looking 5 degrees up (the camera at 300 - 100 sin 5 degrees), and the ground pose 5.2 m over the PQS terrain, 40 m behind the vessel,
    // looking 1.5 degrees up. The 2 m poses put the camera 2 m over the ground collider the ray found under the 5.2 m pose.
    internal static class Poses
    {
        internal static PoseSpec Far(string name, double heading)
        {
            return new PoseSpec { Name = name, HeadingDeg = heading, ElevationDeg = 5.0, DistM = 100.0, CamAglM = 300.0 - 100.0 * Math.Sin(5.0 * Math.PI / 180.0) };
        }

        internal static PoseSpec Ground(string name, double heading, bool spaceCentre)
        {
            return new PoseSpec { Name = name, HeadingDeg = heading, ElevationDeg = 1.5, DistM = spaceCentre ? 60.0 : 40.0, CamAglM = 5.2, SpaceCentre = spaceCentre };
        }
    }

    // Main menu: the probe's save is loaded and the game goes to the space centre (or straight to the flight).
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    internal sealed class LiteMenu : MonoBehaviour
    {
        private void Start()
        {
            if (Lite.MenuDone || Lite.Finished) return;
            Lite.MenuDone = true;
            StartCoroutine(Lite.Safe(Run(), "menu"));
        }

        private IEnumerator Run()
        {
            Lite.Say("main menu reached at " + Lite.F(Time.realtimeSinceStartup) + " s");
            yield return new WaitForSecondsRealtime(5f);
            string saves = Path.Combine(KSPUtil.ApplicationRootPath, "saves");
            bool template = File.Exists(Path.Combine(Path.Combine(saves, Lite.SaveFolder), "probe.sfs"));
            Game game = GamePersistence.LoadGame(template ? "probe" : "persistent", Lite.SaveFolder, true, false);
            if (game == null)
            {
                Lite.Finish(false, "no save " + Lite.SaveFolder);
                yield break;
            }
            int index = game.flightState.protoVessels.FindIndex(v => v.situation == Vessel.Situations.PRELAUNCH);
            if (index < 0)
            {
                Lite.Finish(false, "no vessel on the launch pad in the save " + Lite.SaveFolder);
                yield break;
            }
            Lite.PadVesselIndex = index;
            game.flightState.universalTime = Lite.Ut;
            HighLogic.SaveFolder = Lite.SaveFolder;
            HighLogic.CurrentGame = game;
            Lite.Say("save " + Lite.SaveFolder + " (" + (template ? "probe.sfs" : "persistent") + ") loaded; pad vessel " + game.flightState.protoVessels[index].vesselName + "; UT " + Lite.F(Lite.Ut));
            if (Lite.SpaceCentre)
            {
                HighLogic.LoadScene(GameScenes.SPACECENTER);
            }
            else
            {
                FlightDriver.StartAndFocusVessel(game, index);
            }
        }
    }

    // Space centre: the picture at the game's own camera, then two low views over the lawn, then the flight.
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    internal sealed class LiteSpaceCentre : MonoBehaviour
    {
        private void Start()
        {
            if (Lite.SpaceCentreDone || Lite.Finished || !Lite.SpaceCentre) return;
            Lite.SpaceCentreDone = true;
            StartCoroutine(Lite.Safe(Run(), "space centre"));
        }

        private void LateUpdate()
        {
            if (Rig.Current != null && Rig.Current.SpaceCentre) Rig.Apply();
        }

        private IEnumerator Run()
        {
            Lite.Say("space centre reached at " + Lite.F(Time.realtimeSinceStartup) + " s");
            SpaceCenterCamera2 control = null;
            for (int i = 0; i < 600 && control == null; i++)
            {
                control = FindObjectOfType<SpaceCenterCamera2>();
                if (control == null) yield return null;
            }
            yield return new WaitForSecondsRealtime(2f);
            GameEvents.onHideUI.Fire();
            Rig.SiteLat = Lite.PadLat;
            Rig.SiteLon = Lite.PadLon;
            Rig.Current = null;
            foreach (object step in Shots.Take("sc_default", null, "spacecentre", Lite.SceneSettleMinS, Lite.SceneSettleMaxS)) yield return step;
            if (control != null && FlightCamera.fetch != null)
            {
                control.enabled = false;
                foreach (PoseSpec pose in new[] { Poses.Ground("sc_low_p1", 90.0, true), Poses.Ground("sc_low_p2", 180.0, true) })
                {
                    Rig.Current = pose;
                    foreach (object step in Shots.Take(pose.Name, pose, "spacecentre", Lite.MinSettleS, Lite.MaxSettleS)) yield return step;
                }
                Rig.Current = null;
                control.enabled = true;
            }
            else Lite.Warn("no space centre camera: only the default view was taken");
            GameEvents.onShowUI.Fire();
            if (!Lite.Flight)
            {
                Lite.Finish(true, "space centre pictures taken, warnings " + Lite.Warnings);
                yield break;
            }
            Lite.Say("to the flight of the pad vessel");
            FlightDriver.StartAndFocusVessel(HighLogic.CurrentGame, Lite.PadVesselIndex);
        }
    }

    // Flight: the vessel stays on the pad; the camera is put at the poses (the game's own camera update is off) with the clock held.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal sealed class LiteFlight : MonoBehaviour
    {
        private bool started;

        private void Start()
        {
            if (started || Lite.Finished || !Lite.Flight) return;
            started = true;
            StartCoroutine(Lite.Safe(Run(), "flight"));
        }

        private void LateUpdate()
        {
            if (Rig.Current != null && !Rig.Current.SpaceCentre) Rig.Apply();
        }

        private static bool Ready()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            return FlightGlobals.ready && v != null && v.loaded && !v.packed && FlightCamera.fetch != null && FlightCamera.fetch.mainCamera != null;
        }

        private IEnumerator Run()
        {
            Lite.Say("flight scene reached at " + Lite.F(Time.realtimeSinceStartup) + " s");
            float begun = Time.realtimeSinceStartup;
            while (!Ready())
            {
                if (Time.realtimeSinceStartup - begun > 300f)
                {
                    Lite.Finish(false, "the flight was not ready within 300 s");
                    yield break;
                }
                yield return null;
            }
            yield return new WaitForSecondsRealtime(3f);
            Vessel v = FlightGlobals.ActiveVessel;
            Rig.SiteLat = v.latitude;
            Rig.SiteLon = v.longitude;
            Lite.Say("flight ready: " + v.vesselName + " at " + Lite.F(v.latitude) + ", " + Lite.F(v.longitude) + ", " + Lite.F(v.altitude) + " m, situation " + v.situation + ", UT " + Lite.F(Planetarium.GetUniversalTime()));
            Lite.FrozenUt = Lite.Ut;
            GameEvents.onHideUI.Fire();
            FlightCamera.fetch.DeactivateUpdate();

            var frames = new System.Collections.Generic.List<PoseSpec>
            {
                Poses.Far("far_p1", 90.0), Poses.Far("far_p2", 270.0),
                Poses.Ground("g5_p1", 90.0, false), null, Poses.Ground("g5_p2", 180.0, false), null
            };
            // The two 2 m poses follow the 5.2 m pose of the same heading: the same spot, put over the collider the ray found.
            int taken = 0;
            bool first = true;
            PoseSpec lastGround = null;
            for (int i = 0; i < frames.Count; i++)
            {
                PoseSpec pose = frames[i];
                if (pose == null)
                {
                    if (lastGround == null || !Shots.LastUnder.Hit)
                    {
                        Lite.Warn("no ground collider under " + (lastGround != null ? lastGround.Name : "the 5.2 m pose") + ": the 2 m pose is skipped");
                        continue;
                    }
                    pose = Poses.Ground(lastGround.Name.Replace("g5", "g2"), lastGround.HeadingDeg, false);
                    pose.AbsAltM = Shots.LastUnder.AltAsl + 2.0;
                }
                Rig.Current = pose;
                foreach (object step in Shots.Take(pose.Name, pose, "flight", first ? Lite.SceneSettleMinS : Lite.MinSettleS, first ? Lite.SceneSettleMaxS : Lite.MaxSettleS)) yield return step;
                first = false;
                taken++;
                if (pose.Name.StartsWith("g5", StringComparison.Ordinal)) lastGround = pose;
            }
            Rig.Current = null;
            Lite.Finish(true, taken + " flight pictures, warnings " + Lite.Warnings);
        }
    }
}
