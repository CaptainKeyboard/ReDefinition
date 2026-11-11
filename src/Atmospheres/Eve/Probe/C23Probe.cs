using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The probe of package C23 inside the integrated series K-W1 (one start, run1): the cloud-map painter against the running game.
    // It measures
    //   - EVE's renderer status (Ready in the full set; NoScatterer with EVE and without Scatterer, where nothing is bound and the
    //     frame is not read);
    //   - the binding after 20 forced Applies of the first bound layer: the number of losses and rebinds, the longest time from the
    //     loss to the new binding, and whether the layer is bound at the end (rows C23.apply_*); the binding is judged by identity
    //     (the material EVE made, the paint set) and by the render proof, never by reading a texture back (EVE's shader declares no
    //     properties, RECON P9);
    //   - the render proof before and after the Applies: the painter paints coverage 0 and then 1 everywhere and the sky of the flight
    //     camera must change, by the same amount before and after (rows C23.render_*);
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

            // EVE's renderer: the module looks once a second; the status is read after a few looks. In the full mod set it is Ready;
            // with EVE and without Scatterer it is NoScatterer, Eve and Both are then treated as Ours and nothing is bound.
            yield return ProbeWait.Seconds(4);
            Emit("C23.scatterer_loaded", EveRenderCompat.ScattererLoaded);
            Emit("C23.render_status_code", (int)EveRenderCompat.Status);
            EmitText("C23.render_status", EveRenderCompat.Status + ": " + EveRenderCompat.Reason);
            Emit("C23.render_blocked", EveRenderCompat.Blocked);
            Emit("C23.effective_source_is_ours", EveRenderCompat.EffectiveSource(CloudSourceKind.Eve) == CloudSourceKind.Ours
                && EveRenderCompat.EffectiveSource(CloudSourceKind.Both) == CloudSourceKind.Ours);
            if (EveRenderCompat.Blocked)
            {
                // Nothing is bound to EVE while it cannot render; the frame is frozen by EVE itself in that set, so no frame is read.
                yield return ProbeWait.Seconds(15);
                Emit("C23.blocked_layers_bound", EveCloudStats.LayersBound);
                Emit("C23.blocked_failure_lines", EveRenderCompat.FailureLines);
                yield break;
            }

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
            // The render proof before the Applies (the control): with the painter told to paint coverage 0 and then 1 everywhere, the frame
            // of the flight camera must differ. EVE's shader declares no properties, so the binding cannot be read back from the material;
            // the frame is what shows that EVE draws our target.
            foreach (ProbeWait w in RenderProof(module, "before")) yield return w;

            // Twenty forced Applies. A loss is counted when the layer's material is another object than the one bound; the binding is
            // closed when the layer is Bound on the material it holds now and on the paint set it should carry (identity and the
            // keyword sentinel; no texture is read back).
            int lossesBefore = EveCloudStats.Losses, rebindsBefore = EveCloudStats.Rebinds, reassertsBefore = EveCloudStats.Reasserts;
            int forced = 0, replaced = 0;
            double worstLatency = 0;
            int worstFrames = 0;
            bool allBound = true;
            for (int k = 0; k < 20; k++)
            {
                EveLayer current = FindByName(module, layer.Name) ?? layer;
                Material mainBefore = current.Main;
                double t0 = Time.realtimeSinceStartup;
                if (!EveCloudModule.ForceApply(current))
                {
                    Fail("C23: the layer could not be applied again");
                    break;
                }
                forced++;
                int frames = 0;
                bool closed = false;
                double deadline = t0 + 5.0;
                while (Time.realtimeSinceStartup < deadline)
                {
                    yield return ProbeWait.Frames(1);
                    frames++;
                    EveLayer now = FindByName(module, layer.Name);
                    if (now == null || now.Tracker.State != BindState.Bound) continue;
                    EveLayerAccess.ReadMaterials(now);
                    if (ReferenceEquals(now.Main, mainBefore)) continue;
                    if (EveBinding.Holds(now, SetOf(module, now)))
                    {
                        closed = true;
                        break;
                    }
                }
                EveLayer after = FindByName(module, layer.Name);
                if (after != null && !ReferenceEquals(after.Main, mainBefore)) replaced++;
                allBound &= closed;
                if (closed)
                {
                    worstLatency = Math.Max(worstLatency, Time.realtimeSinceStartup - t0);
                    worstFrames = Math.Max(worstFrames, frames);
                }
                yield return ProbeWait.Seconds(0.25);
            }
            Emit("C23.apply_forced", forced);
            Emit("C23.apply_material_replaced", replaced);
            Emit("C23.apply_losses", EveCloudStats.Losses - lossesBefore);
            Emit("C23.apply_rebinds", EveCloudStats.Rebinds - rebindsBefore);
            Emit("C23.apply_latency_s_max", worstLatency);
            Emit("C23.apply_latency_frames_max", worstFrames);
            Emit("C23.apply_bound_after_each", allBound);
            Emit("C23.reasserts_during_applies", EveCloudStats.Reasserts - reassertsBefore);
            Emit("C23.loss_counter_total", EveCloudStats.Losses);
            Emit("C23.watchdog_finds_total", EveCloudStats.WatchdogFinds);
            Emit("C23.rebind_latency_s_max", EveCloudStats.MaxRebindLatencyS);

            // The same render proof after the Applies: the effect must be what it was before.
            foreach (ProbeWait w in RenderProof(module, "after")) yield return w;
            if (renderBefore > 0.0) Emit("C23.render_effect_retained", renderAfter / renderBefore);
            else Emit("C23.render_effect_retained", double.NaN);

            // The cost.
            yield return ProbeWait.Seconds(5);
            Emit("C23.paint_main_thread_ms_mean", EveCloudStats.MeanPaintMs);
            Emit("C23.paint_main_thread_ms_worst", EveCloudStats.WorstPaintMs);
            Emit("C23.mirror_frame_ms_worst", EveCloudStats.MirrorWorstFrameMs);
            Emit("C23.layers_bound_at_end", EveCloudStats.LayersBound);
        }

        // The relative change of the sky's luminance between the painter's coverage 0 and coverage 1, before and after the Applies.
        private double renderBefore, renderAfter;

        // Tells the painter to paint coverage 0 everywhere, waits for EVE to draw it, reads the sky of the flight camera, does the same
        // with coverage 1, and gives the field its own map back. The effect is |L1 - L0| / max(L0, L1) of the sky region's mean
        // luminance (linear). A frame that does not follow the scene (the EVE-without-Scatterer state, RECON run3) shows an effect of 0.
        // The camera stays where the flight put it: the region is the upper part of the frame, where a ground view sees the sky.
        private IEnumerable<ProbeWait> RenderProof(EveCloudModule module, string label)
        {
            const double SettleS = 8.0;
            double luma0 = double.NaN, luma1 = double.NaN;
            ReconHost host = ReconHost.Get();
            module.ForceCoverage(0f);
            yield return ProbeWait.Seconds(SettleS);
            foreach (ProbeWait w in ReadSky(host, v => luma0 = v)) yield return w;
            module.ForceCoverage(1f);
            yield return ProbeWait.Seconds(SettleS);
            foreach (ProbeWait w in ReadSky(host, v => luma1 = v)) yield return w;
            module.ForceCoverage(null);
            double effect = double.IsNaN(luma0) || double.IsNaN(luma1) ? double.NaN : Math.Abs(luma1 - luma0) / Math.Max(1e-4, Math.Max(luma0, luma1));
            Emit("C23.render_luma_cov0_" + label, luma0);
            Emit("C23.render_luma_cov1_" + label, luma1);
            Emit("C23.render_effect_" + label, effect);
            if (label == "before") renderBefore = double.IsNaN(effect) ? 0.0 : effect;
            else renderAfter = double.IsNaN(effect) ? 0.0 : effect;
            ReconHost.Release();
        }

        private static IEnumerable<ProbeWait> ReadSky(ReconHost host, Action<double> result)
        {
            host.Read();
            yield return ProbeWait.Until(() => !host.Reading, 10, "a frame read");
            ReconFrame frame = host.Frame;
            if (frame == null || frame.Pixels == null || frame.Pixels.Length == 0)
            {
                result(double.NaN);
                yield break;
            }
            System.Collections.Generic.List<float> lumas = frame.Lumas(0.5, 0.75, 0.6, 0.4);
            double sum = 0;
            for (int i = 0; i < lumas.Count; i++) sum += lumas[i];
            result(lumas.Count > 0 ? sum / lumas.Count : double.NaN);
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
