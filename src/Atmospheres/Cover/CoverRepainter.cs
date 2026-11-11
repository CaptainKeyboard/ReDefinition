using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Live ground repaint: paints the quads that are already built again when the map under them changed (the per-tile version of
    // CoverMap), a few at a time inside a budget of 2 ms a frame, then tells Parallax's terrain and recolours its scatters (made anew
    // only when a quad's mean cover crosses 0.4 or 0.8). One pass takes a snapshot of the quads and works through it over several
    // frames; SnowRefresher calls Step every frame. The decisions (when a pass starts, when it is logged) are static methods.
    internal sealed class CoverRepainter
    {
        // The least real time between two passes, in seconds.
        internal const float MinPassSeconds = 1f;

        // The game time after which the wind pattern of a quad is drawn anew, in seconds.
        internal const double DriftRepaintSeconds = 900.0;

        // The time one frame spends on painting quads, in milliseconds (tuning key cover.repaintBudgetMs).
        internal const string BudgetKey = "cover.repaintBudgetMs";
        internal const float BudgetDefault = 2f;

        // Parallax scatter quads recoloured in one frame.
        private const int ScatterQuadsPerFrame = 2;

        // A pass is logged when it has repainted something and the last line is this old.
        internal const float LogThrottleSeconds = 60f;

        private readonly List<PQ> queue = new List<PQ>();
        private readonly List<PQ> scatterQueue = new List<PQ>();
        private readonly Dictionary<CoverBodyState, CoverLook> looks = new Dictionary<CoverBodyState, CoverLook>();
        private readonly Dictionary<CoverBodyState, int> stamps = new Dictionary<CoverBodyState, int>();
        private readonly Dictionary<CoverBodyState, int> lastFrames = new Dictionary<CoverBodyState, int>();
        private int cursor;
        private int scatterCursor;
        private bool passRunning;
        private float lastPassStart = -1000f;
        private double passUt;
        private int repainted, terrainNotified, scatterRecoloured, scatterRegenerated, passes;
        private float lastLoggedAt = -1000f;
        private bool broken;
        private readonly Stopwatch passWatch = new Stopwatch();

        // The milliseconds the last finished pass took in all (the sum of the frames' work), for the probe and the log.
        internal double LastPassMilliseconds { get; private set; }
        internal int LastRepainted { get; private set; }

        // Whether a pass starts now: the last one is old enough and a body's map, settings or wind pattern asks for it.
        internal static bool Due(bool anythingChanged, float secondsSinceLast)
        {
            return anythingChanged && secondsSinceLast >= MinPassSeconds;
        }

        // Whether a finished pass is written to the log.
        internal static bool ShouldLog(int repainted, float secondsSinceLogged)
        {
            return repainted > 0 && secondsSinceLogged > LogThrottleSeconds;
        }

        // The work of one frame at real time now.
        internal void Step(float now)
        {
            if (broken) return;
            if (!passRunning && !StartPass(now)) return;

            long budget = (long)Math.Max(1f, Tuning.Get(BudgetKey, BudgetDefault));
            var watch = Stopwatch.StartNew();
            passWatch.Start();
            try
            {
                while (cursor < queue.Count && watch.ElapsedMilliseconds < budget) Repaint(queue[cursor++]);
                int scatters = ScatterQuadsPerFrame;
                while (cursor >= queue.Count && scatterCursor < scatterQueue.Count && scatters-- > 0) RecolourScatters(scatterQueue[scatterCursor++]);
            }
            catch (Exception e)
            {
                broken = true;
                passRunning = false;
                AtmospheresLog.WarnOnce("the live ground cover failed and is switched off for this session (" + CompatibilityLog.Reason(e) + ").");
                return;
            }
            finally
            {
                passWatch.Stop();
            }
            if (cursor < queue.Count || scatterCursor < scatterQueue.Count) return;

            passRunning = false;
            passes++;
            LastRepainted = repainted;
            LastPassMilliseconds = passWatch.Elapsed.TotalMilliseconds;
            if (ShouldLog(repainted, now - lastLoggedAt))
            {
                lastLoggedAt = now;
                AtmospheresLog.Say("ground cover pass " + passes + ": " + repainted + " of " + queue.Count + " quads repainted, Parallax terrain told for "
                               + terrainNotified + ", scatter quads recoloured " + scatterRecoloured + " (made again " + scatterRegenerated + ")");
            }
        }

        // Puts the colours the quads had before the cover back (the cover was switched off), a budgeted part per call; true when all
        // quads are done. A quad restored is due for a paint when the cover comes back.
        internal bool RestoreStep()
        {
            if (restoreQueue.Count == 0 && restoreCursor == 0) CoverQuads.Snapshot(restoreQueue);
            var watch = Stopwatch.StartNew();
            while (restoreCursor < restoreQueue.Count && watch.ElapsedMilliseconds < 2)
            {
                PQ quad = restoreQueue[restoreCursor++];
                QuadCover cover;
                if (quad == null || !CoverQuads.TryGet(quad, out cover) || !quad.isBuilt || quad.mesh == null) continue;
                if (cover.PaintedVersion < 0 || quad.mesh.vertexCount != cover.Length) continue;
                quad.mesh.colors32 = cover.Original;
                cover.PaintedVersion = -1;
                cover.ScatterBasis = -1f;
                ParallaxLink.TerrainChanged(quad);
            }
            if (restoreCursor < restoreQueue.Count) return false;
            restoreQueue.Clear();
            restoreCursor = 0;
            passRunning = false;
            // The next pass paints everything that was restored.
            lastFrames.Clear();
            stamps.Clear();
            return true;
        }

        private readonly List<PQ> restoreQueue = new List<PQ>();
        private int restoreCursor;

        // Takes the snapshot of the quads when a pass is due; false where there is nothing to do now.
        private bool StartPass(float now)
        {
            if (CoverQuads.Count == 0 || !CoverModule.Enabled) return false;
            double ut;
            if (!CoverClock.TryUt(out ut)) ut = 0;
            looks.Clear();
            bool changed = false;
            foreach (CoverBodyState state in CoverBodies.All)
            {
                CoverMap.Frame frame = state.Map.Current;
                int stamp = CoverLooks.SettingsStamp(state);
                int lastFrame, lastStamp;
                if (!lastFrames.TryGetValue(state, out lastFrame) || lastFrame != frame.Number) changed = true;
                if (!stamps.TryGetValue(state, out lastStamp) || lastStamp != stamp) changed = true;
                if (CoverLooks.Drift(state) > 0) changed = true;
            }
            if (!Due(changed, now - lastPassStart)) return false;
            foreach (CoverBodyState state in CoverBodies.All)
            {
                lastFrames[state] = state.Map.Current.Number;
                stamps[state] = CoverLooks.SettingsStamp(state);
            }
            CoverQuads.Snapshot(queue);
            scatterQueue.Clear();
            cursor = 0;
            scatterCursor = 0;
            passUt = ut;
            passRunning = true;
            lastPassStart = now;
            repainted = terrainNotified = scatterRecoloured = scatterRegenerated = 0;
            passWatch.Reset();
            return true;
        }

        private CoverLook LookOf(CoverBodyState state)
        {
            CoverLook look;
            if (!looks.TryGetValue(state, out look)) looks[state] = look = CoverLooks.Make(state, passUt);
            return look;
        }

        // Paints one built quad again where it is due.
        private void Repaint(PQ quad)
        {
            if (quad == null) return;
            QuadCover cover;
            if (!CoverQuads.TryGet(quad, out cover) || !quad.isBuilt || quad.mesh == null) return;
            CoverBodyState state = cover.Body;
            CoverMap.Frame frame = state.Map.Current;
            if (frame.Number == 0) return;
            int stamp = CoverLooks.SettingsStamp(state);
            if (!cover.NeedsPaint(frame, stamp, passUt, CoverLooks.Drift(state) > 0 ? DriftRepaintSeconds : 0.0)) return;
            Mesh mesh = quad.mesh;
            if (mesh.vertexCount != cover.Length) return;
            CoverLook shared = LookOf(state);
            // The wind is the quad's: a copy of the shared look with the wind at the quad's middle.
            CoverLook look = shared;
            if (shared.Drift > 0)
            {
                look = new CoverLook
                {
                    Def = shared.Def, Strength = shared.Strength, EdgeNoise = shared.EdgeNoise, Drift = shared.Drift, Ut = shared.Ut, RadiusM = shared.RadiusM
                };
                CoverLooks.WindAt(state, (cover.LatMin + cover.LatMax) * 0.5, (cover.LonMin + cover.LonMax) * 0.5, 0.0, passUt, out look.WindEast, out look.WindNorth);
            }
            bool ocean = state.Body.ocean;
            if (cover.ScatterBasis < 0f && ParallaxLink.HasScatters(quad)) cover.ScatterBasis = cover.MeanAmount(frame, look, ocean);
            cover.Paint(frame, look, ocean);
            cover.MarkPainted(frame, stamp, passUt);
            mesh.colors32 = cover.Painted;
            repainted++;
            if (ParallaxLink.TerrainChanged(quad)) terrainNotified++;
            if (ParallaxLink.HasScatters(quad)) scatterQueue.Add(quad);
        }

        // Gives the scatters of a quad its new colours; they are made again where the mean cover has crossed 0.4 or 0.8.
        private void RecolourScatters(PQ quad)
        {
            if (quad == null) return;
            QuadCover cover;
            if (!CoverQuads.TryGet(quad, out cover)) return;
            bool regenerate = QuadCover.RegenerateScatters(cover.Body.Def.Kind, cover.ScatterBasis, cover.LastMean);
            if (!ParallaxLink.Recolour(quad, cover.Painted, regenerate)) return;
            scatterRecoloured++;
            if (regenerate)
            {
                scatterRegenerated++;
                cover.ScatterBasis = cover.LastMean;
            }
        }
    }
}
