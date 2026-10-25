using System;

namespace ReDefinition.Probe
{
    // A recording of the scene probe's camera sweep for FLIP (tools/flip_probe.ps1):
    // -redefinitionProbeFlip records what the monitor shows while the camera sweeps,
    // and quits. With -redefinitionProbeHalfStep the sweep takes twice the frames at
    // half the angle each: the same path, so that without frame generation its odd
    // frames show the camera where a generated frame of the full-step sweep stands
    // between two of the game's frames -- the reference to measure that one against.
    internal static class FlipProbe
    {
        // The switches for the recording and for the sweep at half step.
        internal const string Argument = "-redefinitionProbeFlip";
        internal const string HalfStepArgument = "-redefinitionProbeHalfStep";
        // Frame generation's input dump beside the recording, for
        // tools/analyze_fg_inputs.py: fewer frames, a dump is large.
        internal const string DumpsArgument = "-redefinitionProbeDumps";

        // Desktop frames, the most the recording takes (ScreenRecorder.cpp): without
        // frame generation one a game frame, so the whole sweep at half step; with
        // it, two a game frame, the first 58 of its 60 steps.
        internal const int RecordedFrames = 120;

        // Whether KSP was started to record the sweep.
        internal static bool Requested
        {
            get { return Has(Argument); }
        }

        // Whether frame generation's input dumps are taken beside the recording.
        internal static bool Dumps
        {
            get { return Has(DumpsArgument); }
        }

        // The desktop frames to record: 40 with the dumps, else the most the recording takes.
        internal static int Frames
        {
            get { return Dumps ? 40 : RecordedFrames; }
        }

        // How many sweep steps each step is cut into: 2 at half step, else 1.
        internal static int Subdivide
        {
            get { return Has(HalfStepArgument) ? 2 : 1; }
        }

        // Whether KSP's command line holds a switch, in any case.
        private static bool Has(string argument)
        {
            foreach (string arg in Environment.GetCommandLineArgs())
                if (string.Equals(arg, argument, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
