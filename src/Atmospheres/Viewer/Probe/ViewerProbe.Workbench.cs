using System;
using System.Collections.Generic;
using System.Globalization;

namespace ReDefinition.Atmospheres.Viewer
{
    // The workbench section of K-V1 (VIEW3, rows VIEWER.wb_*): the owner tests all options and sliders in the viewer himself, so the start proves in the running game what makes that
    // possible: a key of each group is read by the pass; a mode switch acts live; Rebuild Weather finishes inside its budget and the same seed gives the same weather after it, another
    // seed another; the readout counts cells; a saved preset, Today and the A/B compare put values back exactly; the cost readout has a frame time; and no save or settings file was
    // written. All through the calls the window's controls make (a mouse click is not simulated); the look of the window is judged from the window screenshot of the start.
    internal sealed partial class ViewerProbe
    {
        internal const string WorkbenchPrefix = "wb_";
        internal const double CoverEqualEps = 1e-4, RebuildBudgetStartMs = 20.0;
        internal const int SeedPlaces = 24, SeedDifferMin = 3, ReadFrames = 30;
        internal const double RebuildWaitS = 60.0;
        internal const string PresetProbeName = "VIEW3probe";

        private IWorkbenchRig workbench;

        internal IWorkbenchRig Workbench
        {
            get { return workbench ?? (workbench = new GameWorkbenchRig()); }
            set { workbench = value; }
        }

        // One verdict of the section: row VIEWER.wb_<name>_ok.
        private bool Wb(string name, string what, bool ok, string expected, string read)
        {
            return Verdict(WorkbenchPrefix + name, what, ok, expected, read);
        }

        private IEnumerable<ProbeWait> WorkbenchSection()
        {
            IWorkbenchRig wb = Workbench;
            long stamp0 = wb.WriteStamp();

            // 1. Each group's sample key is read by the pass (a key read only when the tuning table changes is not required to be read in every frame).
            var unread = new List<string>();
            var notEvery = new List<string>();
            IList<CloudKey> samples = wb.SampleKeys;
            foreach (CloudKey key in samples)
            {
                wb.Watch(key.Key);
                yield return ProbeWait.Frames(ReadFrames);
                if (ReadThisFrame()) continue;
                if (key.Liveness == KeyLiveness.Change) notEvery.Add(key.Key);
                else unread.Add(key.Key);
            }
            Info(WorkbenchPrefix + "groups_sampled", samples.Count);
            Wb("keys_read", "a key of each group is read by the pass", unread.Count == 0 && samples.Count >= 4, "every sampled key read in the frame just run (" + samples.Count + " groups)",
               unread.Count == 0 ? "all read" + (notEvery.Count > 0 ? "; read only on a change of the table: " + string.Join(", ", notEvery.ToArray()) : "") : "not read: " + string.Join(", ", unread.ToArray()));

            // 2. A mode switch acts live: the own clouds off stop the pass, on start it again.
            wb.SetOwnClouds(false);
            foreach (ProbeWait w in Poll(() => !Rig.PassDrawing, PollShortS)) yield return w;
            bool off = polled;
            wb.SetOwnClouds(true);
            foreach (ProbeWait w in Poll(() => Rig.PassDrawing, PollMapS)) yield return w;
            Wb("mode_live", "the ownClouds switch acts live (off stops the pass, on starts it)", off && polled, "not drawing after off, drawing after on", "off " + off + ", on " + polled);

            // 3. Rebuild Weather and the seed, at a held time so that the weather is a function of the seed alone.
            Rig.FreezeTime(true);
            yield return ProbeWait.Seconds(1);
            double ut = Rig.GameUt;
            int seed0 = wb.Seed;
            var baseCover = SampleCover(wb, ut);
            wb.StageSeed(seed0 + 1);
            foreach (ProbeWait w in RebuildAndWait(wb)) yield return w;
            bool done = polled;
            Info(WorkbenchPrefix + "rebuild_elapsed_ms", wb.RebuildElapsedMs);
            Info(WorkbenchPrefix + "rebuild_main_ms", wb.RebuildMainMs);
            Info(WorkbenchPrefix + "rebuild_worst_frame_ms", wb.RebuildWorstFrameMs);
            Wb("rebuild_done", "Rebuild Weather ends with the new states swapped in and the cloud maps fetched", done && wb.Seed == seed0 + 1, "phase Done within " + F(RebuildWaitS) + " s, seed " + (seed0 + 1),
               "phase " + wb.Phase + ", seed " + wb.Seed + ", " + F(wb.RebuildElapsedMs) + " ms");
            Wb("rebuild_budget", "no frame of the rebuild took the main thread longer than the stated budget", done && wb.RebuildWorstFrameMs <= WeatherRebuild.FreezeBudgetMs,
               "worst frame at most " + F(WeatherRebuild.FreezeBudgetMs) + " ms", F(wb.RebuildWorstFrameMs) + " ms (sum " + F(wb.RebuildMainMs) + " ms)");
            var otherCover = SampleCover(wb, ut);
            int differ = Differing(baseCover, otherCover);
            Wb("seed_changes_weather", "another seed gives another weather", differ >= SeedDifferMin, "at least " + SeedDifferMin + " of " + wb.Places + " places differ", differ + " differ");
            wb.StageSeed(seed0);
            foreach (ProbeWait w in RebuildAndWait(wb)) yield return w;
            var againCover = SampleCover(wb, ut);
            int drift = Differing(baseCover, againCover);
            Wb("seed_same_weather", "the same seed gives the same weather after a rebuild", polled && wb.Seed == seed0 && drift == 0, "no place differs from before the seed was changed",
               drift + " differ, seed " + wb.Seed + ", phase " + wb.Phase);
            Rig.FreezeTime(false);

            // 4. The readout counts the cells of the field.
            IList<string> lines = wb.RefreshReadout();
            Info(WorkbenchPrefix + "readout_lines", lines.Count);
            Wb("readout", "the readout counts the cells and gives their density per million square kilometres", lines.Count > 0 && lines[0].StartsWith("Cells ", StringComparison.Ordinal)
               && lines[0].Contains("per million km2"), "a first line 'Cells <n>, <d> per million km2 ...'", lines.Count == 0 ? "no line" : lines[0]);

            // 5. A saved preset, Today and the A/B compare put the values back exactly.
            CloudKey probeKey = CloudKeyRegistry.Find(OwnCloudModule.TuneSunGain);
            float original = wb.KeyValue(probeKey.Key), tuned = original + 0.5f;
            wb.SetKey(probeKey.Key, tuned);
            bool saved = wb.SavePreset(PresetProbeName);
            wb.ResetKey(probeKey.Key);
            bool cleared = !wb.HasOverride(probeKey.Key);
            bool loaded = wb.LoadPreset(PresetProbeName);
            yield return ProbeWait.Frames(2);
            Wb("preset_round_trip", "a saved preset puts a key back exactly", saved && cleared && loaded && Math.Abs(wb.KeyValue(probeKey.Key) - tuned) < 1e-6,
               "saved, key cleared, loaded, " + F(tuned), "saved " + saved + ", cleared " + cleared + ", loaded " + loaded + ", value " + F(wb.KeyValue(probeKey.Key)));
            bool today = wb.LoadToday();
            Wb("today_clears", "Today puts every override back to the shipped defaults", today && !wb.HasOverride(probeKey.Key) && Math.Abs(wb.KeyValue(probeKey.Key) - probeKey.Default) < 1e-6,
               "no override, " + F(probeKey.Default), "loaded " + today + ", override " + wb.HasOverride(probeKey.Key) + ", value " + F(wb.KeyValue(probeKey.Key)));
            bool started = wb.StartCompare(PresetProbeName, ViewerPresetBook.TodayName);
            bool showsB = wb.Showing == 'B' && !wb.HasOverride(probeKey.Key);
            wb.FlipAB();
            bool showsA = wb.Showing == 'A' && Math.Abs(wb.KeyValue(probeKey.Key) - tuned) < 1e-6;
            wb.FlipAB();
            bool backToB = wb.Showing == 'B' && !wb.HasOverride(probeKey.Key);
            Wb("ab_compare", "the A/B compare of two presets shows B first, flips to A and back", started && showsB && showsA && backToB, "B (Today), A (" + PresetProbeName + "), B",
               "started " + started + ", B " + showsB + ", A " + showsA + ", B again " + backToB);
            wb.DeletePreset(PresetProbeName);
            wb.LoadToday();

            // 6. The cost readout has a frame time (the GPU time only with -redefinitionGpuProfile, shown as information).
            yield return ProbeWait.Frames(60);
            Info(WorkbenchPrefix + "cost_frame_ms", double.IsNaN(wb.FrameMs) ? -1.0 : wb.FrameMs);
            Info(WorkbenchPrefix + "cost_gpu_ms", double.IsNaN(wb.GpuMs) ? -1.0 : wb.GpuMs);
            Wb("cost_frame", "the cost readout has a frame time", !double.IsNaN(wb.FrameMs) && wb.FrameMs > 0.0 && wb.FrameMs < 2000.0, "a frame time between 0 and 2000 ms", F(wb.FrameMs) + " ms");

            // 7. Nothing wrote the save or the settings file.
            long stamp1 = wb.WriteStamp();
            Wb("no_save_written", "no save and no settings file was written by the section", stamp0 == stamp1, "the same last-write times as before", stamp0 + " then " + stamp1);
        }

        // Rebuild Weather and the wait for it: first for the previous rebuild's Done to be left (the module takes the request on its next tick), then for the new Done; polled says whether it came.
        private IEnumerable<ProbeWait> RebuildAndWait(IWorkbenchRig wb)
        {
            wb.RebuildWeather();
            foreach (ProbeWait w in Poll(() => wb.Phase != RebuildPhase.Done, PollShortS)) yield return w;
            foreach (ProbeWait w in Poll(() => wb.Phase == RebuildPhase.Done, RebuildWaitS)) yield return w;
        }

        private static double[] SampleCover(IWorkbenchRig wb, double ut)
        {
            var values = new double[wb.Places];
            for (int i = 0; i < values.Length; i++) values[i] = wb.CoverAt(i, ut);
            return values;
        }

        // The places where two samples differ by more than the tolerance (a place that has no value in either counts as differing).
        internal static int Differing(double[] a, double[] b)
        {
            int n = 0;
            for (int i = 0; i < a.Length && i < b.Length; i++)
            {
                if (double.IsNaN(a[i]) || double.IsNaN(b[i]) || Math.Abs(a[i] - b[i]) > CoverEqualEps) n++;
            }
            return n;
        }
    }
}
