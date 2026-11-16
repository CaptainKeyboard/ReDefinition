using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ReDefinition.Atmospheres
{
    internal enum RebuildPhase
    {
        Idle = 0,
        // A request stands: the parameter sets are made on the main thread, the states on a worker.
        Building = 1,
        // Every state is swapped in; the maps of the new states are made on the maps worker and the cloud pass fetches them.
        Fetching = 2,
        Done = 3
    }

    // The bookkeeping of one rebuild of the generator's states (the cloud viewer's Rebuild Weather button, a changed seed): who asked, which bodies are due, how long it took and what it
    // cost the main thread. A state is made on a worker thread (GeneratorState.Create) and swapped in at once, so the weather the game shows never has a gap and the main thread does
    // only the parameter set and the swap; the worst single frame of that work is compared with FreezeBudgetMs and the viewer shows both.
    internal static class WeatherRebuild
    {
        // The most main-thread time one frame of the rebuild may take; a frame above it is counted and shown.
        internal const double FreezeBudgetMs = 20.0;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly HashSet<int> Due = new HashSet<int>();
        private static double beganMs, doneMs;

        internal static RebuildPhase Phase { get; private set; }
        internal static string Reason { get; private set; } = "";
        // The sum of the main-thread time of the rebuild's frames, and its worst frame, in milliseconds.
        internal static double MainMs { get; private set; }
        internal static double WorstFrameMs { get; private set; }
        // The sum of the workers' time making the states.
        internal static double WorkerMs { get; private set; }
        internal static int FramesOverBudget { get; private set; }
        internal static int StatesSwapped { get; private set; }
        internal static int Requests { get; private set; }

        internal static bool Active
        {
            get { return Phase == RebuildPhase.Building; }
        }

        // The milliseconds from the request to now, or to the end where it is done.
        internal static double ElapsedMs
        {
            get
            {
                if (Phase == RebuildPhase.Idle) return 0.0;
                double end = Phase == RebuildPhase.Done ? doneMs : Clock.Elapsed.TotalMilliseconds;
                return Math.Max(0.0, end - beganMs);
            }
        }

        internal static bool OverBudget
        {
            get { return WorstFrameMs > FreezeBudgetMs; }
        }

        // A rebuild for these bodies (the ones that have a state); with none due the phase goes straight to fetching.
        internal static void Begin(string reason, IEnumerable<int> bodies)
        {
            Due.Clear();
            if (bodies != null) foreach (int b in bodies) Due.Add(b);
            Requests++;
            Reason = reason ?? "";
            MainMs = WorstFrameMs = WorkerMs = 0.0;
            FramesOverBudget = StatesSwapped = 0;
            beganMs = Clock.Elapsed.TotalMilliseconds;
            Phase = Due.Count > 0 ? RebuildPhase.Building : RebuildPhase.Fetching;
        }

        internal static bool IsDue(int body)
        {
            return Phase == RebuildPhase.Building && Due.Contains(body);
        }

        // One frame of the rebuild's main-thread work.
        internal static void AddFrame(double ms)
        {
            if (Phase != RebuildPhase.Building) return;
            MainMs += ms;
            if (ms > WorstFrameMs) WorstFrameMs = ms;
            if (ms > FreezeBudgetMs) FramesOverBudget++;
        }

        // The state of a body is swapped in (workerMs: the time its worker took), or the body drops out (no state, broken).
        internal static void Finished(int body, double workerMs, bool swapped)
        {
            if (!Due.Remove(body)) return;
            WorkerMs += workerMs;
            if (swapped) StatesSwapped++;
            if (Due.Count == 0 && Phase == RebuildPhase.Building) Phase = RebuildPhase.Fetching;
        }

        // The cloud pass fetched the new maps.
        internal static void MapsFetched()
        {
            if (Phase != RebuildPhase.Fetching) return;
            Phase = RebuildPhase.Done;
            doneMs = Clock.Elapsed.TotalMilliseconds;
        }

        internal static void Reset()
        {
            Due.Clear();
            Phase = RebuildPhase.Idle;
            Reason = "";
            MainMs = WorstFrameMs = WorkerMs = 0.0;
            FramesOverBudget = StatesSwapped = 0;
        }

        // One line for the log and the window.
        internal static string Describe()
        {
            switch (Phase)
            {
                case RebuildPhase.Idle: return "no rebuild yet";
                case RebuildPhase.Building: return "building (" + Due.Count + " body to swap), " + ElapsedMs.ToString("0") + " ms so far";
                case RebuildPhase.Fetching: return "states swapped, waiting for the maps, " + ElapsedMs.ToString("0") + " ms so far";
                default:
                    return Reason + ": " + ElapsedMs.ToString("0") + " ms to new maps, main thread " + MainMs.ToString("0.0") + " ms (worst frame " + WorstFrameMs.ToString("0.0")
                           + " ms, budget " + FreezeBudgetMs.ToString("0") + (OverBudget ? ", OVER" : ", within") + "), worker " + WorkerMs.ToString("0") + " ms";
            }
        }
    }
}
