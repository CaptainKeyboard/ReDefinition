using System;
using System.Collections.Generic;
using System.Globalization;

namespace ReDefinition.Atmospheres
{
    // Series K-T3 (package T09, the owner rule "test everything you build"): one game start measures many switches. Every variant of the table (T09ProbeK3.Table.cs) is a set of
    // settings and tuning keys that the probe puts on at runtime, so that one start holds up to 26 variants; the start's variants (-atmoVariants) are listed in tests/series/K-T3.json
    // and judged by tools/weather_metrics/kt3.py.
    //
    // Per variant: the settings of the start's values for every key any variant sets (K3RestoreKeys), the plain own pass (UseOwn), the variant's keys; a wait for a steady stretch of
    // drawn frames; the placement of its view when the view changes; the K-T2 timing window (Dwell: "T09 view begin / end", the GPU profiler's reports inside it, the cost read by
    // the busy delta of tools/weather_metrics/kt2.py against the "off" window of the view); then the numeric frame (T09ProbeDiag.DiagTake: rows T09d.<view>.k3.<id>.*, against the
    // frame of the "off" window) and the aid frame (optical depth), both saved as pictures named t09-<start>-<view>_k3-<id>-<WxH>-natural-half / -aid-direct-half. The sky3 view has no
    // horizon in the frame: its variants take the K-T2 pictures (natural and aid) without the numeric frame. Variants with Noise add the K-T2 noise read (flicker, hf).
    // A storm variant forces a cell source 12 km east of the site into the lightning module and counts the flashes of the window (K3.flashes); a rain variant sets the diagnostics'
    // forceLevel and forceType (settings), the wet surfaces and the droplets are settings on top.
    internal sealed partial class T09Probe
    {
        internal const string K3Series = "K-T3";
        // The window of one variant: two profiler reports at the 10 s cadence lie wholly inside it (the first after the begin line is dropped).
        internal const double K3DwellS = 22.0;
        private const double K3SettleS = 3.0;

        private bool? k3Cache;

        private bool IsK3
        {
            get
            {
                if (k3Cache == null) k3Cache = IsActive(Args(), K3Series);
                return k3Cache.Value;
            }
        }

        private static bool IsOffVariant(string variant)
        {
            return variant == "off" || (variant != null && variant.StartsWith("off_", StringComparison.Ordinal));
        }

        private static View K3View(string name)
        {
            switch (name)
            {
                case K3Variants.Above: return Views[2];
                case K3Variants.Horizon: return HorizonView;
                case K3Variants.Sky3: return Views[4];
                default: return Views[0];
            }
        }

        // The start's values of every key a variant sets, read once before the first variant.
        private readonly Dictionary<string, double> k3Start = new Dictionary<string, double>(StringComparer.Ordinal);
        private int k3Windows;

        private void K3Apply(string id)
        {
            ClearForced();
            K3Variant v;
            if (!K3Variants.TryGet(id, out v)) return;
            if (v.Off)
            {
                SetSetting(AtmospheresSettings.Keys.CloudSource, (double)(int)CloudSourceKind.None);
                SetSetting(AtmospheresSettings.Keys.OwnClouds, 0.0);
                foreach (KeyValuePair<string, double> s in k3Start) SetSetting(s.Key, s.Value);
                // The rain block's off window keeps the rain (no clouds, the precipitation as it is).
                foreach (KeyValuePair<string, double> s in v.Sets) SetSetting(s.Key, s.Value);
                K3Storm(false);
                return;
            }
            UseOwn();
            foreach (KeyValuePair<string, double> s in k3Start)
            {
                if (!K3UseOwnKeys.Contains(s.Key)) SetSetting(s.Key, s.Value);
            }
            foreach (KeyValuePair<string, double> s in v.Sets) SetSetting(s.Key, s.Value);
            foreach (KeyValuePair<string, float> t in v.Tunes) ForceTuning(t.Key, t.Value);
            K3Storm(v.Special == K3Special.Storm);
            LogLine("Probe: k3 variant " + id + " keys: " + K3Describe(v));
            EmitText("K3.keys", K3Describe(v));
            EmitText("K3.view", v.View);
            EmitText("K3.group", v.Group);
        }

        // The keys UseOwn sets itself: they are not put back to the start's values after it (the plain pass of every K-T2 variant has them as UseOwn sets them).
        private static readonly HashSet<string> K3UseOwnKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            AtmospheresSettings.Keys.CloudSource, AtmospheresSettings.Keys.OwnClouds, AtmospheresSettings.Keys.CloudReconMode, AtmospheresSettings.Keys.CloudResolution,
            AtmospheresSettings.Keys.CloudEntryRefinement, AtmospheresSettings.Keys.CloudRefineAtContours, AtmospheresSettings.Keys.CloudLightCache, AtmospheresSettings.Keys.LayerCap,
            AtmospheresSettings.Keys.CloudBaseVariation, AtmospheresSettings.Keys.CloudBasePhysics, AtmospheresSettings.Keys.CloudTopFromUpdraft, AtmospheresSettings.Keys.BaseRagged
        };

        private static string K3Describe(K3Variant v)
        {
            var parts = new List<string>();
            foreach (KeyValuePair<string, double> s in v.Sets) parts.Add(s.Key + "=" + s.Value.ToString("0.###", CultureInfo.InvariantCulture));
            foreach (KeyValuePair<string, float> t in v.Tunes) parts.Add(t.Key + "=" + t.Value.ToString("0.#####", CultureInfo.InvariantCulture));
            if (v.Special != K3Special.None) parts.Add("special " + v.Special);
            return parts.Count == 0 ? "(none)" : string.Join(", ", parts.ToArray());
        }

        // The look keys of the sheet layer: the name is read from the pass's stack when the variant is on (the highest map outside a group, as the look probe finds it).
        private void K3ApplySheet(K3Variant v)
        {
            if (v.Special != K3Special.SheetShift && v.Special != K3Special.SheetTypeFair && v.Special != K3Special.SheetTypeOverride) return;
            T09LookInput input = Game.ReadLookInput();
            int sheet = input == null ? -1 : SheetLayer(input);
            if (sheet < 0)
            {
                Warn("k3_no_sheet_layer", v.Id + ": the pass has no layer stack to read; the key of the sheet layer is not set");
                return;
            }
            string name = input.Layers[sheet].Name;
            EmitText("K3.sheet_layer", name);
            switch (v.Special)
            {
                case K3Special.SheetShift: ForceTuning(CloudLookTuning.TuneLayerShift + name, -0.1f); break;
                case K3Special.SheetTypeFair: ForceTuning(CloudLookTuning.TuneTypeFair + name, 0.5f); break;
                default: ForceTuning(CloudLookTuning.TuneTypeOverride + name, 1f); break;
            }
        }

        // --- The storm ---------------------------------------------------------------------------------------------------------------------

        private sealed class K3StormCells : ICellSource
        {
            internal double CentreLatRad, CentreLonRad, BodyRadiusM;

            public bool Sample(double latRad, double lonRad, double ut, out CellSample sample)
            {
                sample = new CellSample { Strength = 0.9f, Level01 = 1f, TopM = 9000f, Coverage = 0.95f };
                double dn = (latRad - CentreLatRad) * BodyRadiusM;
                double de = (lonRad - CentreLonRad) * BodyRadiusM * Math.Cos(latRad);
                return dn * dn + de * de < 12000.0 * 12000.0;
            }
        }

        private bool k3StormWarned;

        // On: a disc of 12 km radius, centred 12 km east of the vessel, is the lightning module's cell source; off: the module's own source again.
        private void K3Storm(bool on)
        {
            LightningModule module = LightningModule.Current;
            if (module == null)
            {
                if (on && !k3StormWarned)
                {
                    k3StormWarned = true;
                    Warn("k3_no_lightning_module", "the lightning module is not running; the storm variants draw no flash");
                }
                return;
            }
            if (!on)
            {
                module.ForcedSource = null;
                return;
            }
            double radius = Game.BodyRadius;
            module.ForcedSource = new K3StormCells
            {
                BodyRadiusM = radius,
                CentreLatRad = Game.VesselLatitude * Math.PI / 180.0,
                CentreLonRad = (Game.VesselLongitude + 12000.0 / radius * 180.0 / Math.PI) * Math.PI / 180.0
            };
        }

        // --- The scenario ------------------------------------------------------------------------------------------------------------------

        private IEnumerator<ProbeWait> K3Scenario()
        {
            foreach (ProbeWait w in Prelude()) yield return w;
            if (!preludeOk) yield break;
            foreach (string id in Variants)
            {
                K3Variant known;
                if (!K3Variants.TryGet(id, out known))
                {
                    Fail("K-T3 has no variant " + id);
                    yield break;
                }
            }
            // The start's values of every key the variants set (the profile of the start is what a variant is measured against).
            k3Start.Clear();
            foreach (string key in K3Variants.SettingKeys()) k3Start[key] = AtmospheresSettings.Number(key);
            double siteLat = Game.VesselLatitude, siteLon = Game.VesselLongitude;
            foreach (ProbeWait w in ChooseUt(Game.BodyIndex, siteLat, siteLon)) yield return w;
            if (!utOk)
            {
                Fail(utFailure);
                yield break;
            }
            Emit("T09.ut", utChosen);
            Emit("T09.cover", coverChosen);
            Emit("T09.sun_elev_deg", sunChosen);
            Emit("T09.sun_band_narrow", utNarrow);
            Check("the sky is covered at the chosen time", coverChosen >= MinCover, coverChosen.ToString("0.00"));
            Check("the sun stands in the band at the chosen time", sunChosen >= SunMinDeg && sunChosen <= SunMaxDeg, sunChosen.ToString("0.0") + " degrees");
            AtmospheresLog.Say("T09 clock: ut " + utChosen.ToString("0") + ", cover " + coverChosen.ToString("0.00") + ", sun " + sunChosen.ToString("0.0") + " degrees; waiting "
                               + MapSettleS.ToString("0") + " s for the maps");
            yield return ProbeWait.Seconds(MapSettleS);
            CheckPicturesFolder();

            string placedView = null;
            T09Readout.Reading baseline = null;
            foreach (string id in Variants)
            {
                K3Variant v;
                K3Variants.TryGet(id, out v);
                View view = K3View(v.View);
                BeginVariant(id);
                CheckVariantGates(id);
                yield return ProbeWait.Seconds(K3SettleS);
                if (v.ExtraSettleS > 0.0) yield return ProbeWait.Seconds(v.ExtraSettleS);
                if (!v.Off)
                {
                    OwnCloudModule m = OwnCloudModule.Current;
                    int drawnFrom = m != null ? m.Stats.FramesDrawn : 0;
                    yield return ProbeWait.Until(() =>
                    {
                        OwnCloudModule o = OwnCloudModule.Current;
                        return o != null && o.Stats.Mode != "off" && o.Stats.FramesDrawn - drawnFrom > SteadyFrames;
                    }, 150, "the own pass to draw a steady stretch (" + id + ")");
                }
                // The sheet layer's name comes from the pass's stack, which is there once the pass draws.
                if (v.Special != K3Special.None) K3ApplySheet(v);
                // The maps and the type values slew to the new numbers (the limit is 0.08 a second).
                if (v.Special == K3Special.SheetShift || v.Special == K3Special.SheetTypeFair || v.Special == K3Special.SheetTypeOverride) yield return ProbeWait.Seconds(LookTypeSettleS);
                if (placedView != v.View)
                {
                    foreach (ProbeWait w in PlaceChecked(siteLat, siteLon, view)) yield return w;
                    Emit("K3." + v.View + ".placed_ok", placeOk);
                    placedView = placeOk ? v.View : null;
                    baseline = null;
                    if (!placeOk)
                    {
                        EndVariant();
                        continue;
                    }
                }
                foreach (ProbeWait w in StableGate(view, id)) yield return w;
                LightningModule storm = LightningModule.Current;
                int spawnedBefore = storm != null ? storm.Spawned : 0;
                foreach (ProbeWait w in Dwell(siteLat, siteLon, view, id, K3DwellS)) yield return w;
                k3Windows++;
                if (v.Special == K3Special.Storm && storm != null) Emit("K3.flashes", storm.Spawned - spawnedBefore);
                if (v.Readout)
                {
                    foreach (ProbeWait w in K3Frames(view, v, baseline)) yield return w;
                    if (v.Off) baseline = diagReading;
                }
                else
                {
                    foreach (ProbeWait w in Pictures(view, id)) yield return w;
                }
                if (v.Noise && NoiseView(view))
                {
                    foreach (ProbeWait w in Noise(view, id)) yield return w;
                }
                if (IsOwn(id)) EmitVariantTotals(id);
                EndVariant();
            }
            K3Storm(false);
            Game.StopHold();
            Emit("K3.windows", k3Windows);
            Emit("T09.pictures.saved", picturesSaved);
            Emit("T09.pictures.bytes", picturesBytes);
            Emit("T09.pictures.warnings", pictureWarnings);
        }

        // The natural frame with its numbers and, for a drawn variant, the aid frame of the optical depth, both against the frame of the view's "off" window.
        private IEnumerable<ProbeWait> K3Frames(View view, K3Variant v, T09Readout.Reading baseline)
        {
            if (!viewPoseOk)
            {
                Warn("pose_mismatch", view.Name + " " + v.Id + ": the pose left the view during the dwell; no frame of a pose that is not the view's");
                yield break;
            }
            if (Elapsed > MaxSeconds - PictureReserveS)
            {
                if (!picturesStopped) Warn("pictures_time", "the scenario is at " + Elapsed.ToString("0") + " s of " + MaxSeconds.ToString("0") + " s; the frames stop so that the measurement ends in time");
                picturesStopped = true;
                yield break;
            }
            Game.KeepRaw = true;
            try
            {
                yield return ProbeWait.Frames(DiagSettleFrames);
                foreach (ProbeWait w in DiagTake(view, "k3", v.Id, false, v.Off ? null : baseline)) yield return w;
                if (v.Off || diagReading == null) yield break;
                foreach (ProbeWait w in LookAidTau(view, "k3", v.Id, baseline)) yield return w;
            }
            finally
            {
                Game.KeepRaw = false;
            }
        }
    }
}
