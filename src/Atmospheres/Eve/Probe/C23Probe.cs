using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The probe of package C23 inside the integrated series K-W1 (one start, run1): the cloud-map painter against the running game.
    // It measures
    //   - the binding after 20 forced Applies of the first bound layer: the number of losses and rebinds, the longest time from the
    //     loss to the new binding, and whether the layer is bound at the end (rows C23.apply_*);
    //   - the painted map read back from the GPU against the CPU mirror of the same layout, correlation (C23.gpu_mirror_correlation);
    //   - the CPU readers' view: EVE's SampleCoverage at 12 sites of the body against the field's coverage at them, correlation
    //     (C23.sites_correlation; SampleCoverage multiplies the map by the type's height curve, so the number is a correlation and not
    //     an equality) -- the layout check of the CPU path;
    //   - the cost of the painter and of the mirror's frame work.
    // It needs a flight on the body: the flight itself is started by the series' other probe or by hand (-atmoKeep), the probe waits
    // for it. What the probe cannot measure from inside the game -- the render of EVE against the painted target and the sky
    // fraction from the ground, which need the frame -- is in the rows as not required, with the way to read it from the screenshots of
    // the series [VERIFY: not measured yet].
    [AtmospheresModule(985)]
    internal sealed class C23Probe : SeriesProbe
    {
        internal const string Series = "K-W1";

        protected override string SeriesName
        {
            get { return Series; }
        }

        protected override double MaxSeconds
        {
            get { return 14 * 60; }
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            yield return ProbeWait.Until(() => HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready && FlightGlobals.ActiveVessel != null,
                330, "the flight");
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                Fail("C23: no flight to measure in");
                yield break;
            }
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            SetSetting(AtmospheresSettings.Keys.Enabled, 1.0);
            Emit("C23.eve_available", EveLayerAccess.Available);
            EmitText("C23.eve_link", EveLayerAccess.Summary);
            if (!EveLayerAccess.Available) yield break;

            // The module binds when a layer is live, calibrated and painted into.
            yield return ProbeWait.Until(() => EveCloudModule.Current != null && EveCloudModule.Current.FirstBoundLayer() != null, 120, "a bound layer");
            EveCloudModule module = EveCloudModule.Current;
            EveLayer layer = module != null ? module.FirstBoundLayer() : null;
            Emit("C23.layers_bound", EveCloudStats.LayersBound);
            if (layer == null)
            {
                Fail("C23: no EVE layer was bound (layers found " + (module != null ? module.CurrentLayers.Count : 0) + ")");
                yield break;
            }
            EmitText("C23.layer", layer.Name + " shift " + layer.ShiftDeg.ToString("0.00", CultureInfo.InvariantCulture) + (layer.Mirrored ? " mirrored" : ""));

            // The painted map against its mirror, once the mirror has had a cycle.
            yield return ProbeWait.Until(() => EveCloudStats.MirrorVersion > 0, 20, "a mirror cycle");
            yield return ProbeWait.Seconds(3);
            byte[] painted, mirrored;
            if (module.ReadPaintedAndMirror(layer, out painted, out mirrored) && mirrored != null)
            {
                double c = EveMapLayout.CorrelationAcrossSizes(mirrored, CpuMirror.Width, CpuMirror.Height, painted, module.PaintWidth, module.PaintHeight);
                Emit("C23.gpu_mirror_correlation", c);
            }
            else
            {
                Emit("C23.gpu_mirror_correlation", double.NaN);
            }

            // EVE's CPU path at 12 sites against the field's coverage there.
            foreach (ProbeWait w in Sites(module, layer)) yield return w;

            // Twenty forced Applies.
            int lossesBefore = EveCloudStats.Losses, rebindsBefore = EveCloudStats.Rebinds;
            int forced = 0;
            double worstLatency = 0;
            bool allBound = true;
            for (int k = 0; k < 20; k++)
            {
                EveLayer current = module.FirstBoundLayer() ?? layer;
                double t0 = Time.realtimeSinceStartup;
                if (!EveCloudModule.ForceApply(current))
                {
                    Fail("C23: the layer could not be applied again");
                    break;
                }
                forced++;
                yield return ProbeWait.Frames(1);
                double deadline = t0 + 5.0;
                while (Time.realtimeSinceStartup < deadline)
                {
                    EveLayer now = FindByName(module, layer.Name);
                    if (now != null && now.Tracker.State == BindState.Bound && EveBinding.ReadBack(now, SetOf(module, now))) break;
                    yield return ProbeWait.Frames(1);
                }
                EveLayer after = FindByName(module, layer.Name);
                bool bound = after != null && after.Tracker.State == BindState.Bound;
                allBound &= bound;
                worstLatency = Math.Max(worstLatency, Time.realtimeSinceStartup - t0);
                yield return ProbeWait.Seconds(0.25);
            }
            Emit("C23.apply_forced", forced);
            Emit("C23.apply_losses", EveCloudStats.Losses - lossesBefore);
            Emit("C23.apply_rebinds", EveCloudStats.Rebinds - rebindsBefore);
            Emit("C23.apply_latency_s_max", worstLatency);
            Emit("C23.apply_bound_after_each", allBound);
            Emit("C23.loss_counter_total", EveCloudStats.Losses);
            Emit("C23.watchdog_finds_total", EveCloudStats.WatchdogFinds);
            Emit("C23.rebind_latency_s_max", EveCloudStats.MaxRebindLatencyS);

            // The cost.
            yield return ProbeWait.Seconds(5);
            Emit("C23.paint_main_thread_ms_mean", EveCloudStats.MeanPaintMs);
            Emit("C23.paint_main_thread_ms_worst", EveCloudStats.WorstPaintMs);
            Emit("C23.mirror_frame_ms_worst", EveCloudStats.MirrorWorstFrameMs);
            Emit("C23.layers_bound_at_end", EveCloudStats.LayersBound);
        }

        private static EveLayer FindByName(EveCloudModule module, string name)
        {
            foreach (EveLayer h in module.CurrentLayers)
            {
                if (string.Equals(h.Name, name, StringComparison.Ordinal)) return h;
            }
            return null;
        }

        private static PaintSet SetOf(EveCloudModule module, EveLayer layer)
        {
            return module.SetOfLayer(layer);
        }

        // EVE's SampleCoverage at twelve sites, in the middle of the deck, against the field's coverage at the same sites.
        private IEnumerable<ProbeWait> Sites(EveCloudModule module, EveLayer layer)
        {
            CelestialBody body = module.CurrentBody;
            IWeatherField field = WeatherHub.Field;
            var map = new WeatherMap();
            double ut = Planetarium.GetUniversalTime();
            if (body == null || !field.GetMap(body.flightGlobalsIndex, WeatherMapKind.Coverage, ut, map))
            {
                Emit("C23.sites_correlation", double.NaN);
                yield break;
            }
            var eve = new List<double>();
            var wanted = new List<double>();
            var rng = new System.Random(5);
            for (int i = 0; i < 12; i++)
            {
                double lat = -60 + 10.0 * i + rng.NextDouble() * 5.0;
                double lon = -180 + 30.0 * i + rng.NextDouble() * 10.0;
                double altitude = 0.5 * (layer.InnerRadiusM + layer.OuterRadiusM) - body.Radius;
                Vector3d p = body.GetWorldSurfacePosition(lat, lon, altitude);
                float type;
                float coverage = EveLayerAccess.SampleCoverage(layer, new Vector3((float)p.x, (float)p.y, (float)p.z), out type);
                eve.Add(coverage);
                wanted.Add(map.Sample(lat, lon));
                EmitText("C23.site" + i, lat.ToString("0.0", CultureInfo.InvariantCulture) + "," + lon.ToString("0.0", CultureInfo.InvariantCulture)
                    + " eve " + coverage.ToString("0.00", CultureInfo.InvariantCulture) + " map " + wanted[i].ToString("0.00", CultureInfo.InvariantCulture));
            }
            Emit("C23.sites_correlation", Pearson(eve, wanted));
            yield break;
        }

        private static double Pearson(IList<double> a, IList<double> b)
        {
            int n = Math.Min(a.Count, b.Count);
            if (n < 2) return double.NaN;
            double ma = 0, mb = 0;
            for (int i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
            ma /= n; mb /= n;
            double saa = 0, sbb = 0, sab = 0;
            for (int i = 0; i < n; i++)
            {
                double da = a[i] - ma, db = b[i] - mb;
                saa += da * da; sbb += db * db; sab += da * db;
            }
            return saa <= 0 || sbb <= 0 ? 0.0 : sab / Math.Sqrt(saa * sbb);
        }
    }
}
