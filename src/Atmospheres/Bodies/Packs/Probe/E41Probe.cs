using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The probe of package E41 inside the integrated series K-W4a (its rows come first in the table). It measures what the game can
    // answer from the body the flight is on, without forcing weather:
    //   - the status of every body of the game against the checklist of the stock bodies (E41.status_code.<Body>, one row per body, the expected code written in the rows file);
    //   - the EVE layers of the active body: whether the layer of ours is among them, which layers the role discovery takes with
    //     the body's own role lists, and the number of volumetric layers EVE holds;
    //   - the binding in cloudSource Both: a layer of the active body carries the painted maps;
    //   - the cost of the layers we drive: the map memory of the layers taken and the paint time of the painter.
    // What it cannot measure is in the rows as not required: the layer lists, the orientation of the shader and the dust figures on
    // Duna and Laythe need a flight on those bodies, and a forced dust system needs a forceable field in the game (the field of the
    // game is not an ISurfaceForcing, K-W3b-result.md) [VERIFY: not measured yet].
    [AtmospheresModule(983)]
    internal sealed class E41Probe : SeriesProbe
    {
        internal const string Series = "K-W4a";

        protected override string SeriesName
        {
            get { return Series; }
        }

        protected override double MaxSeconds
        {
            get { return 8 * 60; }
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            yield return ProbeWait.Until(() => HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready && FlightGlobals.ActiveVessel != null,
                330, "the flight");
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                Fail("E41: no flight to measure in");
                yield break;
            }
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            SetSetting(AtmospheresSettings.Keys.Enabled, 1.0);

            // The status of every body.
            // The probe emits the status of each body as a code (E41.status_code.<Body>: Full 1, CoverOnly 2, Off(NoAtmosphere) 10,
            // Off(NoSurface) 11, Off(Star) 12, anything else -1) and the rows of tests/series/rows/E41.json hold the checklist by body
            // name, so the expectation is written apart from the code that makes the status.
            int rows = 0, full = 0;
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                BodyClimate c = Bodies.Get(body);
                if (c == null) continue;
                string actual = c.Status.ToString();
                rows++;
                if (actual == "Full") full++;
                Emit("E41.status_code." + body.name, StatusCode(actual));
                EmitText("E41.status_text." + body.name, actual);
            }
            Emit("E41.status_rows", rows);
            Emit("E41.status_full", full);

            // The layers of the active body.
            CelestialBody active = FlightGlobals.currentMainBody;
            BodyClimate climate = active != null ? Bodies.Get(active) : null;
            Emit("E41.eve_available", EveLayerAccess.Available);
            if (active == null || climate == null || !EveLayerAccess.Available) yield break;
            yield return ProbeWait.Seconds(5);
            // Both: EVE's native weather plus the painted layers. The module scans the layers with the role discovery; the probe reads
            // the discovery it ran (the same one the game uses) and the layers it bound.
            SetSetting(AtmospheresSettings.Keys.CloudSource, CloudSourceKind.Both.ToString());
            yield return ProbeWait.Until(() => EveCloudModule.Current != null && EveCloudModule.Current.Discovery != null, 60, "the discovery of the module");
            EveCloudModule module = EveCloudModule.Current;
            LayerDiscovery found = module != null ? module.Discovery : null;
            if (found == null)
            {
                Fail("E41: the module ran no discovery on " + active.name);
                yield break;
            }
            int cap = climate.Clouds.EveLayerCap > 0 ? climate.Clouds.EveLayerCap : AtmospheresSettings.EveLayerCap;
            EmitText("E41.layers." + active.name, string.Join(", ", found.Decisions.Select(d => d.Layer.Name).ToArray()));
            EmitText("E41.discovery." + active.name, found.Summary());
            Emit("E41.layers_found." + active.name, found.Decisions.Count);
            Emit("E41.layers_taken." + active.name, found.Taken.Count);
            Emit("E41.tvc_layers_total", EveLayerAccess.VolumetricLayerCount());
            // The layer Clouds-<Body>.cfg puts on a body is named after the body.
            string ownLayer = "ReDefinition-Atmospheres-" + active.name;
            Emit("E41.own_layer." + active.name, found.Decisions.Any(d => d.Layer.Name == ownLayer));
            yield return ProbeWait.Until(() => EveCloudModule.Current != null && EveCloudModule.Current.FirstBoundLayer() != null, 120, "a bound layer in Both");
            module = EveCloudModule.Current;
            EveLayer bound = module != null ? module.FirstBoundLayer() : null;
            Emit("E41.both_bound." + active.name, bound != null);
            Emit("E41.layers_bound_both", EveCloudStats.LayersBound);
            // Ours among the layers that carry the painted maps (the layer list of the module, bound state).
            bool ownBound = false;
            if (module != null)
            {
                foreach (EveLayer h in module.CurrentLayers)
                {
                    if (h.Name == ownLayer && h.Tracker.State == BindState.Bound) ownBound = true;
                }
            }
            Emit("E41.own_layer_bound." + active.name, ownBound);
            yield return ProbeWait.Seconds(10);

            // The cost of what we drive: the map memory by the layout of the painter (a figure of the formula, printed for the log) and
            // the paint time as the painter measured it, in total and per bound layer.
            Emit("E41.map_mb_driven", found.MapMegabytes(MapPainter.DefaultWidth, MapPainter.DefaultHeight));
            Emit("E41.paint_ms_mean", EveCloudStats.MeanPaintMs);
            Emit("E41.paint_ms_worst", EveCloudStats.WorstPaintMs);
            Emit("E41.paint_ms_per_layer", EveCloudStats.LayersBound > 0 ? EveCloudStats.MeanPaintMs / EveCloudStats.LayersBound : 0.0);
            EmitText("E41.layer_cap", cap.ToString(CultureInfo.InvariantCulture));
        }

        // The status text of a body as the number the rows compare (see the comment at the call).
        private static int StatusCode(string status)
        {
            switch (status)
            {
                case "Full": return 1;
                case "CoverOnly": return 2;
                case "Off(NoAtmosphere)": return 10;
                case "Off(NoSurface)": return 11;
                case "Off(Star)": return 12;
                default: return -1;
            }
        }
    }
}
