using System.Collections.Generic;
using ReDefinition.Core;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // -t09TapReadout (the light-volume fps block, 2026-11-21): after a perf view's timed dwell, the march's light taps of that view and variant. The layer shader's aid 10
    // (ReDefinitionAtmospheresLayers.shader) writes per march pixel the sun's taps of the lit samples that marched their light anew (r; CloudLight.cginc gSunTapsAid: the full
    // march's and the far path's near taps, NOT the moons' and NOT the ambient's up march), those samples (g; the others reuse the last optical depth) and the samples (b);
    // the march target (LayerReconstructor.March) is read back and summed. Rows T09.view.<view>.sun_taps_per_fresh_sample, fresh_lit_per_pixel, march_samples_per_pixel
    // and march_pixels. The aid changes the picture for a few frames after
    // the dwell; the timed frames and the pictures are not affected.
    internal sealed partial class T09Probe
    {
        private const int TapAidMode = 10;

        private IEnumerable<ProbeWait> TapReadout(OwnCloudModule own, View view, string variant)
        {
            if (!CommandLine.Has(Args(), "-t09TapReadout", true)) yield break;
            string prefix = "T09.view." + view.Name + ".";
            LayerReconstructor player = own != null && own.Driver != null ? own.Driver.Player : null;
            if (player == null || !Game.AidOn(TapAidMode))
            {
                EmitText(prefix + "sun_taps_per_fresh_sample", "none");
                yield break;
            }
            // Three frames for the aid to reach the march target.
            yield return ProbeWait.Frames(3);
            RenderTexture march = player.March;
            AsyncGPUReadbackRequest request = default(AsyncGPUReadbackRequest);
            bool asked = false;
            if (march != null && march.IsCreated() && SystemInfo.supportsAsyncGPUReadback)
            {
                request = AsyncGPUReadback.Request(march, 0);
                asked = true;
            }
            int waited = 0;
            while (asked && !request.done && waited++ < 30) yield return ProbeWait.Frames(1);
            Game.AidOff();
            if (!asked || !request.done || request.hasError)
            {
                EmitText(prefix + "sun_taps_per_fresh_sample", "readback failed");
                yield return ProbeWait.Frames(3);
                yield break;
            }
            // The march target is ARGBHalf: four halves a pixel, in the order r g b a.
            NativeArray<ushort> halves = request.GetData<ushort>();
            int pixels = march.format == RenderTextureFormat.ARGBHalf ? halves.Length / 4 : 0;
            var px = new int[pixels];
            double taps = 0, fresh = 0, samples = 0;
            for (int i = 0; i < pixels; i++)
            {
                taps += Mathf.HalfToFloat(halves[4 * i]);
                fresh += Mathf.HalfToFloat(halves[4 * i + 1]);
                samples += Mathf.HalfToFloat(halves[4 * i + 2]);
            }
            Emit(prefix + "sun_taps_per_fresh_sample", fresh > 0 ? taps / fresh : 0.0);
            Emit(prefix + "fresh_lit_per_pixel", px.Length > 0 ? fresh / px.Length : 0.0);
            Emit(prefix + "march_samples_per_pixel", px.Length > 0 ? samples / px.Length : 0.0);
            Emit(prefix + "march_pixels", px.Length);
            AtmospheresLog.Say("T09 taps: " + view.Name + " " + variant + ": " + (fresh > 0 ? taps / fresh : 0.0).ToString("0.00") + " sun taps per fresh lit sample, "
                               + (px.Length > 0 ? fresh / px.Length : 0.0).ToString("0.00") + " fresh lit samples per pixel over " + px.Length + " march pixels ("
                               + march.width + "x" + march.height + ")");
            // The aid off reaches the frames after.
            yield return ProbeWait.Frames(3);
            foreach (ProbeWait w in CapReadout(player, view, variant)) yield return w;
        }

        // Aid 11 (the step budget block, 2026-11-21): per march pixel the spans whose step count their length over the step set (r), the budget maxSteps x share (g), the
        // noise-scale count featCount (b). Rows T09.view.<view>.span_len_share, span_budget_share, span_feat_share (shares of all spans) and spans_per_pixel.
        private const int CapAidMode = 11;

        private IEnumerable<ProbeWait> CapReadout(LayerReconstructor player, View view, string variant)
        {
            string prefix = "T09.view." + view.Name + ".";
            if (player == null || !Game.AidOn(CapAidMode))
            {
                EmitText(prefix + "span_budget_share", "none");
                yield break;
            }
            yield return ProbeWait.Frames(3);
            RenderTexture march = player.March;
            AsyncGPUReadbackRequest request = default(AsyncGPUReadbackRequest);
            bool asked = false;
            if (march != null && march.IsCreated() && SystemInfo.supportsAsyncGPUReadback)
            {
                request = AsyncGPUReadback.Request(march, 0);
                asked = true;
            }
            int waited = 0;
            while (asked && !request.done && waited++ < 30) yield return ProbeWait.Frames(1);
            Game.AidOff();
            if (!asked || !request.done || request.hasError || march.format != RenderTextureFormat.ARGBHalf)
            {
                EmitText(prefix + "span_budget_share", "readback failed");
                yield return ProbeWait.Frames(3);
                yield break;
            }
            NativeArray<ushort> halves = request.GetData<ushort>();
            int pixels = halves.Length / 4;
            double len = 0, budget = 0, feat = 0;
            for (int i = 0; i < pixels; i++)
            {
                len += Mathf.HalfToFloat(halves[4 * i]);
                budget += Mathf.HalfToFloat(halves[4 * i + 1]);
                feat += Mathf.HalfToFloat(halves[4 * i + 2]);
            }
            double spans = len + budget + feat;
            Emit(prefix + "span_len_share", spans > 0 ? len / spans : 0.0);
            Emit(prefix + "span_budget_share", spans > 0 ? budget / spans : 0.0);
            Emit(prefix + "span_feat_share", spans > 0 ? feat / spans : 0.0);
            Emit(prefix + "spans_per_pixel", pixels > 0 ? spans / pixels : 0.0);
            AtmospheresLog.Say("T09 caps: " + view.Name + " " + variant + ": of " + spans.ToString("0") + " spans the length set " + (spans > 0 ? len / spans : 0.0).ToString("0.000")
                               + ", the budget " + (spans > 0 ? budget / spans : 0.0).ToString("0.000") + ", the noise-scale count " + (spans > 0 ? feat / spans : 0.0).ToString("0.000"));
            yield return ProbeWait.Frames(3);
        }
    }
}
