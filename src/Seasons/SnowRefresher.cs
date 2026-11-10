using System;
using System.Collections.Generic;
using System.Diagnostics;
using ReDefinition.Core;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ReDefinition.Seasons
{
    // Puts the snow mod into Kerbin's ground and keeps the ground that is already built in step with the season, by the ground
    // refresh mode: Live paints the built quads again, Rebuild builds the terrain anew, Off does neither.
    internal sealed class SnowRefresher : MonoBehaviour
    {
        // Seconds of real time between two looks for Kerbin's ground while the mod is not in it.
        private const float InstallPollSeconds = 1f;

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

        // Rebuild mode: the change of the phase that triggers a rebuild, and the least real time between two.
        internal const double RebuildPhaseTrigger = 0.02;
        internal const float RebuildMinSeconds = 120f;

        private SnowPqsMod mod;
        private PQS sphere;
        private float nextInstallTry;
        private bool installReported;

        // Live: the pass in progress and the last one.
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
        private bool liveBroken;

        // Rebuild: the phase the ground was built for, when the last rebuild was, and whether one is wanted regardless of the phase.
        private double builtPhase = double.NaN;
        private float lastRebuildAt = -1000f;
        private bool rebuildWanted;
        private bool rebuildWaitLogged;

        private void Update()
        {
            if (!SeasonsSettings.Enabled) return;
            if (mod == null)
            {
                if (Time.realtimeSinceStartup < nextInstallTry) return;
                nextInstallTry = Time.realtimeSinceStartup + InstallPollSeconds;
                if (!TryInstall()) return;
            }
            if (SeasonsSettings.GroundRefreshMode == SeasonsSettings.GroundRefresh.Live) LivePass();
            RebuildCheck();
        }

        // Adds the snow mod as a child of Kerbin's PQS; the sphere takes the child mods it finds when it sets up.
        private bool TryInstall()
        {
            if (FlightGlobals.fetch == null || FlightGlobals.Bodies == null) return false;
            CelestialBody kerbin = FlightGlobals.GetBodyByName(SeasonClock.BodyName);
            PQS pqs = kerbin != null ? kerbin.pqsController : null;
            if (pqs == null) return false;
            var holder = new GameObject("ReDefinitionSeasonsSnow");
            holder.transform.SetParent(pqs.transform, false);
            holder.layer = pqs.gameObject.layer;
            mod = holder.AddComponent<SnowPqsMod>();
            mod.order = SnowPqsMod.SortOrder;
            mod.sphere = pqs;
            sphere = pqs;
            builtPhase = SeasonState.Phase();
            if (!installReported)
            {
                installReported = true;
                Debug.Log(Log.Tag + " Seasons: snow PQS mod added to " + SeasonClock.BodyName + ", order " + SnowPqsMod.SortOrder
                          + ", sphere " + (pqs.isStarted ? "already started" : "not started yet") + ", refresh mode "
                          + EnumNames<SeasonsSettings.GroundRefresh>.Name(SeasonsSettings.GroundRefreshMode)
                          + ", " + ParallaxLink.Summary);
            }
            if (pqs.isStarted)
            {
                rebuildWanted = true;
                Debug.Log(Log.Tag + " Seasons: the ground was built before the snow mod was added; it gets its snow at the next rebuild.");
            }
            return true;
        }

        // Live mode: paints the built quads again when the phase has moved, a few at a time.
        private void LivePass()
        {
            if (liveBroken) return;
            float now = Time.realtimeSinceStartup;
            if (!passRunning)
            {
                if (SnowQuads.Count == 0) return;
                double phase = SeasonState.Phase();
                float since = now - lastPassStart;
                bool due = double.IsNaN(lastPassPhase)
                           || since >= SeasonsSettings.RefreshSeconds
                           || (since >= MinPassSeconds && SeasonClock.PhaseDistance(phase, lastPassPhase) > PassPhaseTrigger);
                if (!due) return;
                SnowQuads.Snapshot(queue);
                scatterQueue.Clear();
                cursor = 0;
                scatterCursor = 0;
                passPhase = phase;
                passRunning = true;
                lastPassStart = now;
                repainted = terrainNotified = scatterRecoloured = scatterRegenerated = 0;
            }

            var watch = Stopwatch.StartNew();
            try
            {
                while (cursor < queue.Count && watch.ElapsedMilliseconds < FrameBudgetMilliseconds) Repaint(queue[cursor++]);
                int budget = ScatterQuadsPerFrame;
                while (cursor >= queue.Count && scatterCursor < scatterQueue.Count && budget-- > 0) RecolourScatters(scatterQueue[scatterCursor++]);
            }
            catch (Exception e)
            {
                liveBroken = true;
                passRunning = false;
                Debug.LogWarning(Log.Tag + " Seasons: the live ground refresh failed and is switched off for this session ("
                                 + CompatibilityLog.Reason(e) + "). Set groundRefreshMode to Rebuild or Off.");
                return;
            }
            if (cursor < queue.Count || scatterCursor < scatterQueue.Count) return;

            passRunning = false;
            lastPassPhase = passPhase;
            passes++;
            if (repainted > 0 && (double.IsNaN(lastLoggedPhase) || SeasonClock.PhaseDistance(passPhase, lastLoggedPhase) > 0.005 || now - lastLoggedAt > 60f))
            {
                lastLoggedPhase = passPhase;
                lastLoggedAt = now;
                Debug.Log(Log.Tag + " Seasons: ground refresh (live) pass " + passes + " for phase "
                          + passPhase.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                          + ": " + repainted + " of " + queue.Count + " quads re-tinted, Parallax terrain told for " + terrainNotified
                          + ", scatter quads recoloured " + scatterRecoloured + " (made again " + scatterRegenerated + ")");
            }
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
            float strength = SeasonsSettings.SnowStrength;
            double line = SeasonsSettings.WinterSnowLine;
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

        // Builds Kerbin's ground anew where the mode or the first install wants it and no vessel needs the ground.
        private void RebuildCheck()
        {
            if (sphere == null) return;
            bool modeWants = SeasonsSettings.GroundRefreshMode == SeasonsSettings.GroundRefresh.Rebuild;
            if (!rebuildWanted && !modeWants) return;
            float now = Time.realtimeSinceStartup;
            double phase = SeasonState.Phase();
            if (!rebuildWanted && SeasonClock.PhaseDistance(phase, builtPhase) <= RebuildPhaseTrigger) return;
            if (!rebuildWanted && now - lastRebuildAt < RebuildMinSeconds) return;
            if (!SafeToRebuild())
            {
                if (!rebuildWaitLogged)
                {
                    rebuildWaitLogged = true;
                    Debug.Log(Log.Tag + " Seasons: a ground rebuild is due and waits for a moment when no vessel is on the ground (scene "
                              + HighLogic.LoadedScene + ", sphere " + (sphere.isActive ? "active" : "inactive") + ").");
                }
                return;
            }
            rebuildWaitLogged = false;
            rebuildWanted = false;
            lastRebuildAt = now;
            builtPhase = phase;
            // A sphere that is not started builds with the snow mod when it starts; resetting it would start it.
            if (!sphere.isStarted) return;
            sphere.RebuildSphere();
            Debug.Log(Log.Tag + " Seasons: ground rebuild for phase " + phase.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                      + " (scene " + HighLogic.LoadedScene + ").");
        }

        // A rebuild destroys every quad, so it runs only where nothing stands on them: Kerbin's sphere not started, or in flight or
        // the tracking station with the sphere inactive (the view is in space). Never in the space centre, whose buildings sit on it.
        private bool SafeToRebuild()
        {
            if (sphere == null) return false;
            if (!sphere.isStarted) return true;
            GameScenes scene = HighLogic.LoadedScene;
            return (scene == GameScenes.FLIGHT || scene == GameScenes.TRACKSTATION) && !sphere.isActive;
        }
    }
}
