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
        internal const string Argument = "-redefinitionProbeFlip";
        internal const string HalfStepArgument = "-redefinitionProbeHalfStep";
        // Frame generation's input dump beside the recording, for
        // tools/analyze_fg_inputs.py: fewer frames, a dump is large.
        internal const string DumpsArgument = "-redefinitionProbeDumps";

        // Desktop frames, the most the recording takes (ScreenRecorder.cpp): without
        // frame generation one a game frame, so the whole sweep at half step; with
        // it, two a game frame, the first 58 of its 60 steps.
        internal const int RecordedFrames = 120;

        internal static bool Requested
        {
            get { return Has(Argument); }
        }

        internal static bool Dumps
        {
            get { return Has(DumpsArgument); }
        }

        internal static int Frames
        {
            get { return Dumps ? 40 : RecordedFrames; }
        }

        internal static int Subdivide
        {
            get { return Has(HalfStepArgument) ? 2 : 1; }
        }

        private static bool Has(string argument)
        {
            foreach (string arg in Environment.GetCommandLineArgs())
                if (string.Equals(arg, argument, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
