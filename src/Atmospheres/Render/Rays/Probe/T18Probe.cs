using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The probe of package T18 inside the integrated series K-T3 (rows tests/series/rows/T18.json): the ray-side pack of the own layers in the running game.
    // It runs one flight and measures, in this order:
    //   1. the frame time with rtOwnClouds off (300 frames);
    //   2. the switch on: the wall time and the frame time until the first pack stands, the bake's main-thread time and its worst tick, the upload;
    //   3. the frame time with the pack standing and refreshed (300 frames);
    //   4. the pack's facts: the layers in it against the stack's, the texture;
    //   5. the five places round the vessel (RtPackAgreement.Pick, sun 45 degrees, 6 degrees at the terminator): the shadow factor by the pack's rays
    //      against the cloud shadow field at 64 steps a span, and against the field at the shadow pass's 8 steps; and the scan of the whole lattice;
    //   6. the layer cap 2: the pack holds two layers; then the switch off: no pack, no texture.
    // What it cannot measure: the picture of the rays (nothing in the main mod reads the pack yet), the GPU cost, a cloud in a reflection. Frame times are
    // the CPU's (Time.unscaledDeltaTime); the series pins no frame cap.
    [AtmospheresModule(991)]
    internal sealed class T18Probe : SeriesProbe
    {
        internal const string Series = "K-T3";

        protected override string SeriesName
        {
            get { return Series; }
        }

        protected override double MaxSeconds
        {
            get { return 10 * 60; }
        }

        protected override string LostGround()
        {
            return null;
        }

        private static double FrameMs(float fromTime, int fromFrame)
        {
            int frames = Time.frameCount - fromFrame;
            return frames <= 0 ? 0.0 : (Time.realtimeSinceStartup - fromTime) * 1000.0 / frames;
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            OwnCloudModule module = null;
            yield return ProbeWait.Until(() => { module = OwnCloudModule.Current; return InFlightReady() && module != null && module.Stack != null && module.Stack.LayerCount > 0; },
                                         300, "the flight scene and the own cloud stack");
            var stack = (LayerStackSource)module.Stack;
            Vessel vessel = FlightGlobals.ActiveVessel;
            Emit("T18.stack_layers", stack.LayerCount);

            // 1. Off.
            SetSetting(AtmospheresSettings.Keys.RtOwnClouds, 0);
            SetSetting(AtmospheresSettings.Keys.RtOwnCloudsLayerCap, 0);
            yield return ProbeWait.Frames(30);
            Emit("T18.off_no_pack", RtOwnClouds.Pack == null && RtOwnClouds.Texture == null);
            float t0 = Time.realtimeSinceStartup;
            int f0 = Time.frameCount;
            yield return ProbeWait.Frames(300);
            double offMs = FrameMs(t0, f0);
            Emit("T18.frame_ms_off", offMs);

            // 2. On.
            int bakesBefore = RtOwnClouds.Stats.BakesDone;
            float onAt = Time.realtimeSinceStartup;
            int onFrame = Time.frameCount;
            SetSetting(AtmospheresSettings.Keys.RtOwnClouds, 1);
            yield return ProbeWait.Until(() => RtOwnClouds.Pack != null && RtOwnClouds.Texture != null, 240, "the first ray cloud pack");
            double bakeMs = FrameMs(onAt, onFrame);
            Emit("T18.first_pack_wall_s", Time.realtimeSinceStartup - onAt);
            Emit("T18.frame_ms_bake", bakeMs);
            Emit("T18.frame_ms_delta_bake", bakeMs - offMs);
            RtCloudPackDriver.Statistics stats = RtOwnClouds.Stats;
            Emit("T18.bake_total_ms", stats.LastBakeMs);
            Emit("T18.bake_tick_worst_ms", stats.WorstTickMs);
            Emit("T18.bakes_done", stats.BakesDone - bakesBefore);
            Emit("T18.upload_ms", RtOwnClouds.LastUploadMs);
            Emit("T18.upload_worst_ms", RtOwnClouds.WorstUploadMs);

            // 3. Steady.
            t0 = Time.realtimeSinceStartup;
            f0 = Time.frameCount;
            yield return ProbeWait.Frames(300);
            double steadyMs = FrameMs(t0, f0);
            Emit("T18.frame_ms_steady", steadyMs);
            Emit("T18.frame_ms_delta_steady", steadyMs - offMs);

            // 4. Facts.
            RtCloudPack pack = RtOwnClouds.Pack;
            Check("a pack stands", pack != null);
            if (pack == null) yield break;
            int expected = Math.Min(stack.LayerCount, RtCloudPackLayout.MaxLayers);
            Emit("T18.layers_in_pack", pack.LayerCount);
            Emit("T18.layers_in_pack_all", pack.LayerCount == expected);
            Emit("T18.pack_width", pack.Width);
            Emit("T18.pack_mb", RtCloudPackLayout.Bytes(pack.Width, pack.Height, pack.LayerCount) / (1024.0 * 1024.0));
            Texture texture = RtOwnClouds.Texture;
            Emit("T18.texture_ok", texture != null && texture.width == pack.Width && texture.height == pack.Height * pack.LayerCount);
            double covered = 0.0;
            for (int i = 0; i < pack.Width * pack.Height * 2; i += 2)
                if (LayerDensity.Shape(pack.Data[i], pack.Entries[0].Coverage, pack.Entries[0].EdgeSoftness) > 0.0) covered += 1.0;
            Emit("T18.layer0_cloud_share", covered / (pack.Width * pack.Height));

            // 5. The five places and the scan, against the stack as it stands now.
            LayerTypeTable[] tables = stack.Tables();
            string missing;
            List<RtAgreementPlace> places = RtPackAgreement.Pick(stack, tables, pack, vessel.latitude, vessel.longitude, 4.0, 0.2, 45.0, out missing);
            foreach (RtAgreementPlace p in places)
            {
                string key = p.Name.Substring(0, 2);
                Emit("T18.shadow_ref_" + key, p.Reference);
                Emit("T18.shadow_ray_" + key, p.Ray);
                Emit("T18.shadow_diff_" + key, p.Diff);
                Emit("T18.shadow_quick_diff_" + key, p.QuickDiff);
            }
            Emit("T18.places_missing", missing.Length == 0 ? 0 : missing.Split(',').Length);
            if (missing.Length > 0) EmitText("T18.places_missing_kinds", missing);
            Check("all five kinds of place exist in the sky", missing.Length == 0, missing.Length == 0 ? null : "no place for " + missing);
            foreach (double elevation in new[] { 45.0, 6.0 })
            {
                RtAgreementScan scan = RtPackAgreement.Scan(stack, tables, pack, vessel.latitude, vessel.longitude, 4.0, 0.2, elevation);
                string key = "T18.scan" + (int)elevation;
                Emit(key + "_places", scan.Places);
                Emit(key + "_median", scan.Median);
                Emit(key + "_p90", scan.P90);
                Emit(key + "_worst", scan.Worst);
            }

            // 6. The layer cap, then off.
            SetSetting(AtmospheresSettings.Keys.RtOwnCloudsLayerCap, 2);
            int capExpected = Math.Min(2, stack.LayerCount);
            yield return ProbeWait.Until(() => RtOwnClouds.Pack != null && RtOwnClouds.Pack.LayerCount == capExpected && !RtOwnClouds.Baking, 240, "the pack with the layer cap");
            Emit("T18.cap2_layers", RtOwnClouds.Pack != null ? RtOwnClouds.Pack.LayerCount : -1);
            SetSetting(AtmospheresSettings.Keys.RtOwnCloudsLayerCap, 0);
            SetSetting(AtmospheresSettings.Keys.RtOwnClouds, 0);
            yield return ProbeWait.Frames(10);
            Emit("T18.off_releases_pack", RtOwnClouds.Pack == null && RtOwnClouds.Texture == null);
            Check("the switch off releases the pack and the texture", RtOwnClouds.Pack == null && RtOwnClouds.Texture == null);
        }
    }
}
