using System;
using System.Collections.Generic;
using System.Globalization;

namespace ReDefinition.Atmospheres
{
    // Series K-T2d, the start look55 (-atmoLook C; E40 start 55, the questions a to f of .local/plans/atmospheres/E40-start55-result.md), built from the code of look A / B (T09ProbeLook.cs)
    // and the grain start (T09ProbeGrain.cs). Everything at the Medium profile, the own pass at its defaults (recon.accumulate 0.2) unless a frame forces a key.
    //   low cover, ground view   off, off2, then
    //     (a) colour    the frames ca02t0 / ca02t5 / ca02t10 (light.ambientOcclusion 0.02 with cloud.sunTint 0 / 0.5 / 1), caDt0 / caDt5 / caDt10 (the default 1.2765), cair (the sun's
    //                   path through Scatterer's air), ce14 (exposure 1.4), csky0 / csky1 (light.skyTint 0 / 1 against 0.7), caDt0b (the repeat of caDt0: the floor)
    //     (d) fog rule  set C5 with layer 0 alone (wC5o0) and all layers (kC5), each with cloud.typeFair.<rain deck> 0.5 forced (...f), the optical depth of the layer-alone pair
    //     (c) grain     the variants gacc (the default) and gleapacc (layers.gridFine 4 + layers.emptyLeap 4), three frames each (T09ProbeGrain.DiagLookG, rows grain_*)
    //     cost windows  35 s each of off / plain / leap / shape0 (shadow.shapeNoise 0) between "T09 view begin / end" lines: the GPU profiler's reports are read by kt2.py
    //   low cover, horizon pose  off, off2, the order pair in the order 1, 0, 0, 1 with the row profile (b)
    //   low cover, above view    off, the order frames ord1 / ord0, hh1 / hh2 (the pass's own aerial perspective at strength 1 and 2) (b), kC5 / kC5f (d), the grain pair (c),
    //                            sh1 / sh0 (shadow.shapeNoise 1 / 0: the cloud shadow on the sea) (e), cost windows off / plain / leap
    //   high cover (only while the scenario is early enough)  the cumulonimbus layer alone at the ground view and the rain deck alone from 2.3 km with cloud.layerNoiseSizeScale.<layer>
    //                    1 / 0.1 / 0.05, natural and optical depth (f)
    // The rows of every frame as in the other look starts: T09d.<view>.<cover>.<frame>.*. The frames' names are the picture names.
    internal sealed partial class T09Probe
    {
        internal const double Look55MaxSeconds = 17 * 60;
        // The window of one cost reading: the profiler reports every 10 s and the first report of a window is dropped (kt2.py), so 35 s hold two or three clean ones.
        private const double Look55CostWindowS = 35.0;
        // Elapsed seconds after which a later section is left out so that the earlier ones end in time.
        private const double Look55AboveGrainUntilS = 11 * 60, Look55AboveCostUntilS = 12 * 60, Look55HighUntilS = 12.5 * 60;
        private static readonly string[] Look55GrainTags = { "gacc", "gleapacc" };
        private static readonly float[] Look55NoiseScales = { 1f, 0.1f, 0.05f };
        // The rain deck of the shipped cfg (its fair-weather type is the fog deck of the owner's complaint).
        private const string Look55RainDeck = "Kerbin-weather-rain";

        private struct Look55Frame
        {
            internal string Tag;
            internal KeyValuePair<string, float>[] Keys;
        }

        private static Look55Frame F55(string tag, params KeyValuePair<string, float>[] keys)
        {
            return new Look55Frame { Tag = tag, Keys = keys };
        }

        // (a) the colour frames. light.ambientOcclusion 0.02 against the default 1.2765; cloud.sunTint 0 (default), 0.5, 1; the sun's path through Scatterer's air; exposure 1.4; the sky tint of the ambient.
        private static readonly Look55Frame[] Look55Colours =
        {
            F55("ca02t0", Key(OwnCloudOptions.TuneAmbientOcclusion, 0.02f)),
            F55("ca02t5", Key(OwnCloudOptions.TuneAmbientOcclusion, 0.02f), Key(CloudLookTuning.TuneSunTint, 0.5f)),
            F55("ca02t10", Key(OwnCloudOptions.TuneAmbientOcclusion, 0.02f), Key(CloudLookTuning.TuneSunTint, 1f)),
            F55("caDt0"),
            F55("caDt5", Key(CloudLookTuning.TuneSunTint, 0.5f)),
            F55("caDt10", Key(CloudLookTuning.TuneSunTint, 1f)),
            F55("cair", Key(OwnCloudModule.TuneSunAirWithScatterer, 1f)),
            F55("ce14", Key(OwnCloudModule.TuneExposure, 1.4f)),
            F55("csky0", Key(OwnCloudOptions.TuneSkyTint, 0f)),
            F55("csky1", Key(OwnCloudOptions.TuneSkyTint, 1f)),
            F55("caDt0b")
        };

        // The first section of a cover: the ground view, the horizon pose, the above view; the high cover has the noise-scale frames.
        private IEnumerable<ProbeWait> Look55Cover(string cover, double lat, double lon)
        {
            if (cover == "low")
            {
                View ground = Views[0];
                foreach (ProbeWait w in PlaceChecked(lat, lon, ground)) yield return w;
                Emit("T09d.ground.low.placed_ok", placeOk);
                foreach (ProbeWait w in Look55Ground(ground, cover)) yield return w;
                foreach (ProbeWait w in Look55Horizon(cover, lat, lon)) yield return w;
                View above = Views[2];
                foreach (ProbeWait w in PlaceChecked(lat, lon, above)) yield return w;
                Emit("T09d.above.low.placed_ok", placeOk);
                foreach (ProbeWait w in Look55Above(above, cover)) yield return w;
                yield break;
            }
            if (Elapsed > Look55HighUntilS)
            {
                Warn("look55_high_skipped", "the scenario is at " + Elapsed.ToString("0") + " s; the noise-scale frames of the high cover are left out (proposed for start 56)");
                yield break;
            }
            foreach (ProbeWait w in Look55NoiseScale(Views[0], cover, lat, lon, "Kerbin-cumulonimbus")) yield return w;
            foreach (ProbeWait w in Look55NoiseScale(DeckMidView, cover, lat, lon, Look55RainDeck)) yield return w;
        }

        // The baseline frames of a view: the pass runs, no layer is drawn (off), and a second one (off2) where the noise floor of the view is wanted. diagReading of the first is returned in look55Off.
        private T09Readout.Reading look55Off;

        private IEnumerable<ProbeWait> Look55Baseline(View view, string cover, bool second)
        {
            ClearForced();
            Game.SetLayerMask(NoLayersMask);
            yield return ProbeWait.Frames(DiagSettleFrames);
            foreach (ProbeWait w in DiagTake(view, cover, "off", false, null)) yield return w;
            look55Off = diagReading;
            if (second)
            {
                yield return ProbeWait.Frames(DiagSettleFrames);
                foreach (ProbeWait w in DiagTake(view, cover, "off2", false, null)) yield return w;
            }
            Game.SetLayerMask(0);
            yield return ProbeWait.Frames(DiagSettleFrames);
        }

        // One frame under the keys forced for it: settle, expectation, read. wait: seconds the maps need after the keys (types: LookTypeSettleS).
        private IEnumerable<ProbeWait> Look55Take(View view, string cover, string head, string frame, T09Readout.Reading off, KeyValuePair<string, float>[] keys, double wait)
        {
            ClearForced();
            if (keys != null)
                foreach (KeyValuePair<string, float> k in keys) ForceTuning(k.Key, k.Value);
            foreach (ProbeWait w in LookFrame(view, cover, head, frame, off, wait)) yield return w;
        }

        // The ground view at the low cover: (a), (d), (c) and the cost windows.
        private IEnumerable<ProbeWait> Look55Ground(View view, string cover)
        {
            string head = "T09d." + view.Name + "." + cover + ".";
            foreach (ProbeWait w in Look55Baseline(view, cover, true)) yield return w;
            T09Readout.Reading off = look55Off;
            T09LookInput input = Game.ReadLookInput();
            if (input == null) Warn("no_look_input", head + "the pass has no layer stack to read: the expectation rows are missing");
            else EmitLookMapProfile(head, view, input);
            // (a) the colour of the deck's underside.
            foreach (Look55Frame f in Look55Colours)
                foreach (ProbeWait w in Look55Take(view, cover, head, f.Tag, off, f.Keys, 0.0)) yield return w;
            ClearForced();
            // (d) the fog rule: layer 0 alone and all layers, set C5, without and with cloud.typeFair.<rain deck> 0.5.
            foreach (ProbeWait w in Look55FogFrames(view, cover, head, off, true)) yield return w;
            // (c) the grain pair and the cost windows of the pair, the plain pass and the shadow's shape noise.
            foreach (ProbeWait w in DiagLookG(view, cover, Look55GrainTags, "goff")) yield return w;
            ClearForced();
            Game.SetLayerMask(0);
            foreach (ProbeWait w in Look55CostWindows(view, cover, true)) yield return w;
        }

        // (d): the fog deck. wC5o0 is the rain deck alone under the set C5 (the frame of start 51); kC5 draws every layer. The fair-weather type of the rain deck forced to 0.5 turns it
        // from its fog type to the stratus that the precipitation level 0 would not give.
        private IEnumerable<ProbeWait> Look55FogFrames(View view, string cover, string head, T09Readout.Reading off, bool layerAlone)
        {
            LookSet c5 = CloudLookModel.SetByTag("C5") ?? CloudLookModel.Sets[0];
            foreach (bool fair in new[] { false, true })
            {
                if (layerAlone)
                {
                    ApplyLookKeys(c5, 0f);
                    if (fair) ForceTuning(CloudLookTuning.TuneTypeFair + Look55RainDeck, 0.5f);
                    Game.SetLayerMask(1 << 0);
                    string alone = "wC5o0" + (fair ? "f" : "");
                    foreach (ProbeWait w in LookFrame(view, cover, head, alone, off, LookTypeSettleS, 0)) yield return w;
                    foreach (ProbeWait w in LookAidTau(view, cover, alone, off)) yield return w;
                    Game.SetLayerMask(0);
                }
                ApplyLookKeys(c5, 0f);
                if (fair) ForceTuning(CloudLookTuning.TuneTypeFair + Look55RainDeck, 0.5f);
                foreach (ProbeWait w in LookFrame(view, cover, head, "kC5" + (fair ? "f" : ""), off, LookTypeSettleS)) yield return w;
            }
            // The types slew back to their own values at 0.08 a second: the frames after this wait for it.
            ClearForced();
            yield return ProbeWait.Frames(DiagSettleFrames);
            yield return ProbeWait.Seconds(LookTypeSettleS);
        }

        // The horizon pose at the low cover: the order pair in the order 1, 0, 0, 1 with the row profile (the clear sea below the row; look52's frames of the rework).
        private IEnumerable<ProbeWait> Look55Horizon(string cover, double lat, double lon)
        {
            View view = HorizonView;
            string head = "T09d." + view.Name + "." + cover + ".";
            foreach (ProbeWait w in PlaceChecked(lat, lon, view)) yield return w;
            Emit(head + "placed_ok", placeOk);
            foreach (ProbeWait w in Look55Baseline(view, cover, true)) yield return w;
            foreach (ProbeWait w in LookOrderPair(view, cover, head, look55Off, new[] { 1, 0, 0, 1 })) yield return w;
        }

        // The view from above at the low cover: (b) the order frames and the aerial perspective, (d), (c), (e) and the cost windows.
        private IEnumerable<ProbeWait> Look55Above(View view, string cover)
        {
            string head = "T09d." + view.Name + "." + cover + ".";
            foreach (ProbeWait w in Look55Baseline(view, cover, false)) yield return w;
            T09Readout.Reading off = look55Off;
            T09LookInput input = Game.ReadLookInput();
            if (input != null) EmitLookMapProfile(head, view, input);
            foreach (ProbeWait w in LookOrderPair(view, cover, head, off, new[] { 1, 0, 0, 1 })) yield return w;
            foreach (ProbeWait w in LookHazeFrames(view, cover, head, off)) yield return w;
            foreach (ProbeWait w in Look55FogFrames(view, cover, head, off, false)) yield return w;
            // (e) the cloud shadow on the sea with and without the shape noise (the default is on).
            foreach (float shape in new[] { 1f, 0f })
                foreach (ProbeWait w in Look55Take(view, cover, head, "sh" + ((int)shape).ToString(CultureInfo.InvariantCulture), off,
                                                   new[] { Key(CloudShadowShapeNoise.TuneKey, shape) }, 0.0)) yield return w;
            ClearForced();
            yield return ProbeWait.Frames(DiagSettleFrames);
            if (Elapsed > Look55AboveGrainUntilS) Warn("look55_above_grain_skipped", "the scenario is at " + Elapsed.ToString("0") + " s; the grain pair of the above view is left out");
            else
                foreach (ProbeWait w in DiagLookG(view, cover, Look55GrainTags, "goff")) yield return w;
            ClearForced();
            Game.SetLayerMask(0);
            if (Elapsed > Look55AboveCostUntilS) Warn("look55_above_cost_skipped", "the scenario is at " + Elapsed.ToString("0") + " s; the cost windows of the above view are left out");
            else
                foreach (ProbeWait w in Look55CostWindows(view, cover, false)) yield return w;
        }

        // The cost windows of a view: off (no layer drawn), plain (the pass at its defaults), leap (layers.gridFine 4 + layers.emptyLeap 4, the history at its default) and, for the ground view,
        // shape0 (shadow.shapeNoise 0). The lines "T09 view begin / end: <view> <variant> <WxH>" are what kt2.py cuts the profiler reports at.
        private IEnumerable<ProbeWait> Look55CostWindows(View view, string cover, bool withShape)
        {
            string head = "T09d." + view.Name + "." + cover + ".";
            foreach (ProbeWait w in Look55CostWindow(view, head, "off", NoLayersMask, null)) yield return w;
            foreach (ProbeWait w in Look55CostWindow(view, head, "plain", 0, null)) yield return w;
            foreach (ProbeWait w in Look55CostWindow(view, head, "leap", 0,
                                                     new[] { Key(OwnCloudOptions.TuneGridFine, 4f), Key(OwnCloudOptions.TuneEmptyLeap, 4f) })) yield return w;
            if (withShape)
                foreach (ProbeWait w in Look55CostWindow(view, head, "shape0", 0, new[] { Key(CloudShadowShapeNoise.TuneKey, 0f) })) yield return w;
            ClearForced();
            Game.SetLayerMask(0);
            yield return ProbeWait.Frames(DiagSettleFrames);
        }

        private IEnumerable<ProbeWait> Look55CostWindow(View view, string head, string variant, int layerMask, KeyValuePair<string, float>[] keys)
        {
            ClearForced();
            if (keys != null)
                foreach (KeyValuePair<string, float> k in keys) ForceTuning(k.Key, k.Value);
            Game.SetLayerMask(layerMask);
            // The history of the half-size march needs frames to settle after a change of the march.
            yield return ProbeWait.Frames(DiagSettleFrames + 60);
            OwnCloudModule own = OwnCloudModule.Current;
            int drawnBefore = own != null ? own.Stats.FramesDrawn : 0;
            int framesBefore = Game.FrameCount;
            string size = Game.ScreenWidth + "x" + Game.ScreenHeight;
            AtmospheresLog.Say("T09 view begin: " + view.Name + " " + variant + " " + size);
            double dwelled = 0.0;
            viewPoseOk = true;
            while (dwelled < Look55CostWindowS - 1e-9)
            {
                double slice = Math.Min(HeldSliceS, Look55CostWindowS - dwelled);
                yield return ProbeWait.Seconds(slice);
                dwelled += slice;
                CheckHeld(view, "cost_" + variant, dwelled);
            }
            AtmospheresLog.Say("T09 view end: " + view.Name + " " + variant + " " + size);
            int frames = Math.Max(1, Game.FrameCount - framesBefore);
            string prefix = head + "cost_" + variant + ".";
            Emit(prefix + "frames", frames);
            Emit(prefix + "fps", frames / Look55CostWindowS);
            if (own != null)
            {
                Emit(prefix + "frames_drawn", own.Stats.FramesDrawn - drawnBefore);
                EmitText(prefix + "mode", own.Stats.Mode ?? "");
            }
        }

        // (f) a layer alone with the noise size scaled: 1 (the cfg's), 0.1 and 0.05; natural and optical-depth frames. The layer's name is read from the stack.
        private IEnumerable<ProbeWait> Look55NoiseScale(View view, string cover, double lat, double lon, string layerName)
        {
            foreach (ProbeWait w in PlaceChecked(lat, lon, view)) yield return w;
            string head = "T09d." + view.Name + "." + cover + ".";
            Emit(head + "placed_ok", placeOk);
            foreach (ProbeWait w in Look55Baseline(view, cover, false)) yield return w;
            T09Readout.Reading off = look55Off;
            T09LookInput input = Game.ReadLookInput();
            int layer = -1;
            if (input != null && input.Layers != null)
                for (int l = 0; l < input.Layers.Length; l++)
                    if (input.Layers[l].Name == layerName) layer = l;
            if (layer < 0)
            {
                Warn("look55_no_layer", head + "the stack has no layer " + layerName);
                yield break;
            }
            string name = layerName;
            Emit(head + "noise_layer_index", layer);
            EmitText(head + "noise_layer", name);
            foreach (float scale in Look55NoiseScales)
            {
                ClearForced();
                if (scale != 1f) ForceTuning(CloudLookTuning.TuneNoiseSize + "." + name, scale);
                Game.SetLayerMask(1 << layer);
                string frame = "f" + layer.ToString(CultureInfo.InvariantCulture) + "s" + Math.Round(scale * 100.0).ToString("0", CultureInfo.InvariantCulture);
                foreach (ProbeWait w in LookFrame(view, cover, head, frame, off, LookMapSettleS, layer)) yield return w;
                foreach (ProbeWait w in LookAidTau(view, cover, frame, off)) yield return w;
            }
            ClearForced();
            Game.SetLayerMask(0);
            yield return ProbeWait.Frames(DiagSettleFrames);
        }
    }
}
