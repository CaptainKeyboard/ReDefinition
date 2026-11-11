using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Live ground refresh: paints the quads that are built again when the season phase has moved, a few at a time, then tells
    // Parallax's terrain and recolours its scatters. One pass takes a snapshot of the quads and works through it over several
    // frames; SnowRefresher calls Step every frame. The decisions (when a pass starts, when it is logged) are static methods.
    internal sealed class LiveRepainter
    {
        // The smallest change of the phase that makes a quad paint again, as a part of the year (about 90 s of game time).
        internal const double PaintTolerance = 1e-5;

        // A change of the phase as large as this starts a pass before the refresh interval has passed, but not twice in MinPassSeconds.
        internal const double PassPhaseTrigger = 0.002;
        internal const float MinPassSeconds = 1f;

        // The time one frame spends on painting quads, in milliseconds.
        private const long FrameBudgetMilliseconds = 2;

        // Parallax scatter quads recoloured in one frame, and the change of the mean snow amount of a quad that makes its scatters
        // be made again.
        private const int ScatterQuadsPerFrame = 2;
        internal const float ScatterRegenerateDelta = 0.1f;

        // A pass is logged when its phase is this far from the last logged one, or this many real seconds after it.
        internal const double LogPhaseDelta = 0.005;
        internal const float LogThrottleSeconds = 60f;

        private readonly List<PQ> queue = new List<PQ>();
        private readonly List<PQ> scatterQueue = new List<PQ>();
        private int cursor;
        private int scatterCursor;
        private bool passRunning;
        private double passPhase;
        private double lastPassPhase = double.NaN;
        private float lastPassStart = -1000f;
        private int repainted, terrainNotified, scatterRecoloured, scatterRegenerated, passes;
        private double lastLoggedPhase = double.NaN;
        private float lastLoggedAt = -1000f;
        private bool broken;

        // Whether a pass starts now: none has run (NaN), or the refresh interval has passed, or the phase moved far enough and the
        // pass before is at least MinPassSeconds old.
        internal static bool Due(double lastPassPhase, double phase, float secondsSinceLast, float refreshSeconds)
        {
            return double.IsNaN(lastPassPhase)
                   || secondsSinceLast >= refreshSeconds
                   || (secondsSinceLast >= MinPassSeconds && SeasonClock.PhaseDistance(phase, lastPassPhase) > PassPhaseTrigger);
        }

        // Whether a finished pass is written to the log: it repainted something, and its phase is new or the last line is old.
        internal static bool ShouldLog(int repainted, double phase, double lastLoggedPhase, float secondsSinceLogged)
        {
            return repainted > 0 && (double.IsNaN(lastLoggedPhase) || SeasonClock.PhaseDistance(phase, lastLoggedPhase) > LogPhaseDelta || secondsSinceLogged > LogThrottleSeconds);
        }

        // The work of one frame at real time now.
        internal void Step(float now)
        {
            if (broken) return;
            if (!passRunning && !StartPass(now)) return;

            var watch = Stopwatch.StartNew();
            try
            {
                while (cursor < queue.Count && watch.ElapsedMilliseconds < FrameBudgetMilliseconds) Repaint(queue[cursor++]);
                int budget = ScatterQuadsPerFrame;
                while (cursor >= queue.Count && scatterCursor < scatterQueue.Count && budget-- > 0) RecolourScatters(scatterQueue[scatterCursor++]);
            }
            catch (Exception e)
            {
                broken = true;
                passRunning = false;
                AtmospheresLog.WarnOnce("the live ground refresh failed and is switched off for this session (" + CompatibilityLog.Reason(e)
                                    + "). Set groundRefreshMode to Rebuild or Off.");
                return;
            }
            if (cursor < queue.Count || scatterCursor < scatterQueue.Count) return;

            passRunning = false;
            lastPassPhase = passPhase;
            passes++;
            if (ShouldLog(repainted, passPhase, lastLoggedPhase, now - lastLoggedAt))
            {
                lastLoggedPhase = passPhase;
                lastLoggedAt = now;
                AtmospheresLog.Say("ground refresh (live) pass " + passes + " for phase " + passPhase.ToString("0.000", CultureInfo.InvariantCulture)
                               + ": " + repainted + " of " + queue.Count + " quads re-tinted, Parallax terrain told for " + terrainNotified
                               + ", scatter quads recoloured " + scatterRecoloured + " (made again " + scatterRegenerated + ")");
            }
        }

        // Takes the snapshot of the quads when a pass is due; false where there is nothing to do now.
        private bool StartPass(float now)
        {
            if (SnowQuads.Count == 0) return false;
            double? clock = SeasonState.Phase();
            if (!clock.HasValue) return false;
            double phase = clock.Value;
            if (!Due(lastPassPhase, phase, now - lastPassStart, AtmospheresSettings.RefreshSeconds)) return false;
            SnowQuads.Snapshot(queue);
            scatterQueue.Clear();
            cursor = 0;
            scatterCursor = 0;
            passPhase = phase;
            passRunning = true;
            lastPassStart = now;
            repainted = terrainNotified = scatterRecoloured = scatterRegenerated = 0;
            return true;
        }

        // Paints one built quad again for the pass's phase.
        private void Repaint(PQ quad)
        {
            if (quad == null) return;
            QuadSnow snow;
            if (!SnowQuads.TryGet(quad, out snow) || !quad.isBuilt || quad.mesh == null) return;
            if (!snow.NeedsPaint(passPhase, PaintTolerance)) return;
            Mesh mesh = quad.mesh;
            if (mesh.vertexCount != snow.Length) return;
            float strength = AtmospheresSettings.SnowStrength;
            double line = AtmospheresSettings.WinterSnowLine;
            if (snow.ScatterBasis < 0f && ParallaxLink.HasScatters(quad)) snow.ScatterBasis = snow.MeanAmount(snow.PaintPhase, strength, line);
            snow.Paint(passPhase, strength, line);
            mesh.colors32 = snow.Painted;
            repainted++;
            if (ParallaxLink.TerrainChanged(quad)) terrainNotified++;
            if (ParallaxLink.HasScatters(quad)) scatterQueue.Add(quad);
        }

        // Gives the scatters of a quad its new colours; they are made again where the mean snow has changed enough.
        private void RecolourScatters(PQ quad)
        {
            if (quad == null) return;
            QuadSnow snow;
            if (!SnowQuads.TryGet(quad, out snow)) return;
            bool regenerate = snow.ScatterBasis < 0f || Math.Abs(snow.LastMean - snow.ScatterBasis) > ScatterRegenerateDelta;
            if (!ParallaxLink.Recolour(quad, snow.Painted, regenerate)) return;
            scatterRecoloured++;
            if (regenerate)
            {
                scatterRegenerated++;
                snow.ScatterBasis = snow.LastMean;
            }
        }
    }
}
