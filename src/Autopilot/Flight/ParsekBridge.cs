using System;
using System.Reflection;
using UnityEngine;
using ReDefinition.Core;

namespace ReDefinition.Autopilot.Flight
{
    // What the autopilot needs to know of Parsek, read through reflection behind the parsek-ghost assumption, and the one thing it asks of it: to record the flight (parsek-start).
    // A vessel Parsek replays from a recording, a ghost, takes no nodes and no orders: the plan leaves it alone.
    internal static class ParsekBridge
    {
        private static MethodInfo isGhost;
        private static bool looked;
        private static bool warned;

        // Why Parsek's ghosts cannot be told, for the window and the log; null where Parsek is not installed or is a build the code was checked
        // against.
        internal static string Unavailable
        {
            get
            {
                // The first of the things taken from Parsek that does not hold names the problem; the others fail alone, each to its own fallback.
                foreach (ForeignAssumption assumption in new[] { ForeignAssumptions.ParsekGhost, ForeignAssumptions.ParsekRecorder, ForeignAssumptions.ParsekStart, ForeignAssumptions.ParsekRecording })
                {
                    if (assumption.State == ForeignState.Absent || assumption.Holds) continue;
                    return "Parsek " + assumption.Version + " is not a build the autopilot was checked against (" + assumption.Problem + "): " + assumption.Fallback + ".";
                }
                return null;
            }
        }

        // Tells the player once, on the screen and in the log, that Parsek is installed in a build the autopilot does not know.
        internal static void WarnOnce()
        {
            string why = Unavailable;
            if (why == null || warned) return;
            warned = true;
            Debug.LogWarning(Log.Tag + " " + why);
            ScreenMessages.PostScreenMessage("ReDefinition: " + why, 12f, ScreenMessageStyle.UPPER_CENTER);
        }

        private static PropertyInfo instance;
        private static FieldInfo recorderField;
        private static PropertyInfo isRecording;
        private static PropertyInfo committed;
        private static bool lookedAtRecorder;

        // What Parsek is doing with this flight, for the window: "Parsek is recording this flight" or "Parsek is not recording", with the
        // number of recordings kept. Null where Parsek is not installed, or is a build the bridge was not checked against. Nothing is written.
        internal static string RecordingState
        {
            get
            {
                if (!ForeignAssumptions.ParsekRecorder.Holds) return null;
                if (!lookedAtRecorder)
                {
                    lookedAtRecorder = true;
                    Type flight = TypeLookup.Find("Parsek.ParsekFlight");
                    Type recorder = TypeLookup.Find("Parsek.FlightRecorder");
                    Type store = TypeLookup.Find("Parsek.RecordingStore");
                    instance = flight != null ? flight.GetProperty("Instance", TypeLookup.Any) : null;
                    recorderField = flight != null ? flight.GetField("recorder", TypeLookup.Any) : null;
                    isRecording = recorder != null ? recorder.GetProperty("IsRecording", TypeLookup.Any) : null;
                    committed = store != null ? store.GetProperty("CommittedRecordings", TypeLookup.Any) : null;
                }
                if (instance == null || recorderField == null || isRecording == null) return null;
                try
                {
                    object flightNow = instance.GetValue(null, null);
                    if (flightNow == null) return null;
                    object recorderNow = recorderField.GetValue(flightNow);
                    bool recording = recorderNow != null && (bool)isRecording.GetValue(recorderNow, null);
                    var list = committed != null ? committed.GetValue(null, null) as System.Collections.ICollection : null;
                    string kept = list != null ? ", " + list.Count + " kept" : "";
                    return (recording ? "Parsek is recording this flight" : "Parsek is not recording") + kept + ".";
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        private static bool started;

        // Has Parsek record the flight, where it does not already: the only thing of Parsek's the autopilot starts. Parsek ends the recording and
        // asks to commit it when the flight is left. False where Parsek is not there, is a build the code was not checked against, or already records.
        internal static bool StartRecording()
        {
            started = false;
            if (!ForeignAssumptions.ParsekStart.Holds || CommandLine.Has("-redefinitionNoParsekRecording")) return false;
            try
            {
                Type flight = TypeLookup.Find("Parsek.ParsekFlight");
                Type recorder = TypeLookup.Find("Parsek.FlightRecorder");
                PropertyInfo current = flight != null ? flight.GetProperty("Instance", TypeLookup.Any) : null;
                FieldInfo held = flight != null ? flight.GetField("recorder", TypeLookup.Any) : null;
                PropertyInfo running = recorder != null ? recorder.GetProperty("IsRecording", TypeLookup.Any) : null;
                MethodInfo start = flight != null ? flight.GetMethod("StartRecording", TypeLookup.Any, null, new[] { typeof(bool), typeof(bool) }, null) : null;
                if (current == null || held == null || running == null || start == null) return false;
                object parsek = current.GetValue(null, null);
                if (parsek == null) return false;
                object now = held.GetValue(parsek);
                if (now != null && (bool)running.GetValue(now, null)) return false;
                start.Invoke(parsek, new object[] { false, false });
                started = true;
                Debug.Log(Log.Tag + " Parsek is asked to record the flight.");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Parsek could not be asked to record: " + e.Message);
                return false;
            }
        }

        // Ends the recording the autopilot started, at the end of its route; a recording that was running before is left to Parsek.
        internal static void StopRecording()
        {
            if (!started) return;
            started = false;
            try
            {
                Type flight = TypeLookup.Find("Parsek.ParsekFlight");
                PropertyInfo current = flight != null ? flight.GetProperty("Instance", TypeLookup.Any) : null;
                MethodInfo stop = flight != null ? flight.GetMethod("StopRecording", TypeLookup.Any, null, Type.EmptyTypes, null) : null;
                object parsek = current != null ? current.GetValue(null, null) : null;
                if (parsek == null || stop == null) return;
                stop.Invoke(parsek, null);
                Debug.Log(Log.Tag + " Parsek is asked to end the recording of the flight.");
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Parsek could not be asked to end the recording: " + e.Message);
            }
        }

        // Whether the vessel is one of Parsek's ghosts. False without Parsek, or with a build the bridge was not checked against.
        internal static bool IsGhost(Vessel vessel)
        {
            if (vessel == null || !ForeignAssumptions.ParsekGhost.Holds) return false;
            if (!looked)
            {
                looked = true;
                Type type = TypeLookup.Find("Parsek.GhostMapPresence");
                isGhost = type != null ? type.GetMethod("IsGhostMapVessel", TypeLookup.Any, null, new[] { typeof(uint) }, null) : null;
            }
            if (isGhost == null) return false;
            try
            {
                return (bool)isGhost.Invoke(null, new object[] { vessel.persistentId });
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
