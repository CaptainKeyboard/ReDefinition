using System;
using System.Collections;
using System.IO;
using ReDefinition.Bridges;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Probe
{
    // -redefinitionProbeLaunch <craft>: the probe's save's flight a new launch of that craft from the VAB's ships
    // (Ships/VAB/<craft>.craft), its first seat crewed, in place of the vessel on the pad.
    // -redefinitionProbeStaging <seconds>: then the launch flown -- full throttle, the first stage, and after that
    // many seconds the next, which drops what it decouples -- with the desktop recorded from just before the
    // drop (ScreenRecordingBridge, frame generation's frames included; with -redefinitionProbeDumps its inputs
    // too), and shots before and after. For what frame generation makes of parts the vessel drops.
    internal static class StagingProbe
    {
        // The switches for the craft to launch and for the staging flight after it.
        internal const string LaunchArgument = "-redefinitionProbeLaunch";
        internal const string StagingArgument = "-redefinitionProbeStaging";

        // The craft's file, or null where none is asked for or found.
        internal static string CraftPath()
        {
            string craft = SceneProbe.ArgumentAfter(LaunchArgument);
            if (craft == null) return null;
            string path = Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "Ships"), "VAB"), craft + ".craft");
            return File.Exists(path) ? path : null;
        }

        // A new launch of the craft from the pad, as the VAB's launch button starts one; false where it cannot.
        internal static bool Launch(Game game)
        {
            string path = CraftPath();
            if (path == null)
            {
                Debug.LogWarning(Log.Tag + " Probe: no craft " + SceneProbe.ArgumentAfter(LaunchArgument) + " in Ships/VAB.");
                return false;
            }
            ConfigNode craft = ConfigNode.Load(path);
            if (craft == null) return false;
            VesselCrewManifest manifest = VesselCrewManifest.FromConfigNode(craft);
            // The first seat crewed: without a pilot the vessel takes neither the throttle nor the staging.
            foreach (PartCrewManifest part in manifest.GetCrewableParts())
            {
                if (part.NoSeats()) continue;
                foreach (ProtoCrewMember kerbal in game.CrewRoster.Crew)
                {
                    if (kerbal.rosterStatus != ProtoCrewMember.RosterStatus.Available) continue;
                    part.AddCrewToSeat(kerbal, 0);
                    break;
                }
                break;
            }
            Debug.Log(Log.Tag + " Probe: a new launch of " + Path.GetFileNameWithoutExtension(path) + ".");
            Launched = true;
            FlightDriver.StartWithNewLaunch(path, "Squad/Flags/default", "LaunchPad", manifest);
            return true;
        }

        // Whether the probe started a new launch this session.
        internal static bool Launched { get; private set; }
        // Whether a new launch waits for the space centre to stand, set by the scene probe on its way there.
        internal static bool Pending;

        // Whether KSP was started to fly the staging.
        internal static bool Requested
        {
            get { return SceneProbe.ArgumentAfter(StagingArgument) != null; }
        }

        // The flight: full throttle, the first stage, and after the delay (10 s where the switch's value is no number)
        // the next, recorded from just before, with shots before and after the drop.
        public static IEnumerator Run(string folder, string shot)
        {
            float delay;
            if (!float.TryParse(SceneProbe.ArgumentAfter(StagingArgument), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out delay))
                delay = 10f;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) yield break;
            Debug.Log(Log.Tag + " Probe: staging: " + vessel.vesselName + ", " + vessel.parts.Count + " parts.");
            FlightInputHandler.state.mainThrottle = 1f;
            yield return new WaitForSeconds(1f);
            KSP.UI.Screens.StageManager.ActivateNextStage();
            Debug.Log(Log.Tag + " Probe: staging: launched.");
            float launched = Time.time;
            while (Time.time - launched < delay - 0.3f)
            {
                FlightInputHandler.state.mainThrottle = 1f;
                yield return null;
            }
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, shot + "-before-drop.png"));
            bool recording = ScreenRecordingBridge.Start(FlipProbe.Frames, FlipProbe.Dumps);
            Debug.Log(Log.Tag + " Probe: staging: " + (recording ? "recording" : "not recorded: " + ScreenRecordingBridge.State())
                      + " from game frame " + (Time.frameCount + 1) + ".");
            for (int i = 0; i < 10; i++) yield return null;
            int before = FlightGlobals.Vessels.Count;
            KSP.UI.Screens.StageManager.ActivateNextStage();
            Debug.Log(Log.Tag + " Probe: staging: dropped at game frame " + Time.frameCount + ", " + before + " vessels before.");
            float until = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < until)
            {
                string state = ScreenRecordingBridge.State();
                if (!state.StartsWith("recording", StringComparison.Ordinal) && !state.StartsWith("saving", StringComparison.Ordinal))
                    break;
                FlightInputHandler.state.mainThrottle = 1f;
                yield return null;
            }
            Debug.Log(Log.Tag + " Probe: staging: " + ScreenRecordingBridge.State() + "; " + FlightGlobals.Vessels.Count + " vessels.");
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, shot + "-after-drop.png"));
            yield return new WaitForSeconds(1f);
        }
    }
}
