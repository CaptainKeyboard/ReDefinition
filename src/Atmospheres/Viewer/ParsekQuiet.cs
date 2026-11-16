using System;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres.Viewer
{
    // Keeps Parsek from taking the viewer's placement for a launch. A placement moves the vessel off the ground, so the vessel's situation changes PRELAUNCH or LANDED to FLYING; Parsek
    // answers that (ParsekFlight.EvaluateAutoRecordLaunchDecision, setting autoRecordOnLaunch) by starting a recording: it writes a rewind save of the player's game
    // (saves/<save>/Parsek/Saves/parsek_rw_<id>.sfs and its .loadmeta), builds a recording tree for the vessel with every debris vessel that breaks off, and leaves it pending in the
    // save for the commit or discard dialog when the flight is left (K-T2 log of 2026-11-15, 16:03:32). Parsek has one suppression of that decision that it arms itself for time jumps:
    // the frame up to which an auto-record launch is skipped (TimeJumpManager.timeJumpLaunchAutoRecordSuppressUntilFrame, a private static int). The viewer sets it a few
    // hundred frames ahead on every frame it holds a vessel and for a while after it lets go; it runs out by itself, so nothing is left behind in the player's settings or save.
    // Where Parsek is not installed, or the field is not there (another release), the viewer says so once and places as before.
    internal static class ParsekQuiet
    {
        // How far ahead of the current frame the suppression is set each time (the decision looks at the current frame against it).
        internal const int FramesAhead = 300;

        private static FieldInfo field;
        private static bool looked, warned;

        // Whether Parsek's suppression can be set.
        internal static bool Available
        {
            get
            {
                Look();
                return field != null;
            }
        }

        private static void Look()
        {
            if (looked) return;
            looked = true;
            try
            {
                Type manager = TypeLookup.Find("Parsek.TimeJumpManager");
                if (manager == null) return;
                field = manager.GetField("timeJumpLaunchAutoRecordSuppressUntilFrame", BindingFlags.NonPublic | BindingFlags.Static);
                if (field != null && field.FieldType != typeof(int)) field = null;
                if (field == null && !warned)
                {
                    warned = true;
                    AtmospheresLog.Warn("atmospheres-parsek-quiet", "Parsek is installed but its launch auto-record suppression (TimeJumpManager.timeJumpLaunchAutoRecordSuppressUntilFrame) is not there: "
                                        + "a viewer placement can start a Parsek recording and write a rewind save into the game's save.");
                }
            }
            catch (Exception e)
            {
                field = null;
                AtmospheresLog.Failed("atmospheres-parsek-quiet", "Parsek's suppression could not be looked up", e);
            }
        }

        // Asks Parsek to skip an auto-record launch for the next FramesAhead frames. Cheap; call it every frame a vessel is held.
        internal static void Hold()
        {
            Look();
            if (field == null) return;
            try
            {
                field.SetValue(null, Time.frameCount + FramesAhead);
            }
            catch (Exception e)
            {
                field = null;
                AtmospheresLog.Failed("atmospheres-parsek-quiet", "Parsek's suppression could not be set", e);
            }
        }
    }
}
