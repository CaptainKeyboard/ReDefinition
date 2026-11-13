using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Series K-T2d, the look starts of the E40 plan 2026-11 (.local/plans/atmospheres/E40-plan-2026-11.md, sections 2 and 3; E40-phaseO-result.md has the numbers that choose the sets).
    // Selected by the command line of the start (the series runner takes them from the start's probeArgs):
    //   -atmoLook A          start 51, "look A": the shape and the cover of the clouds. Per view and cover (ground low, ground high, above low): the baseline frame, then every look set
    //                        (CloudLookModel.Sets, the ones named by -atmoLookSets, default all) at shift 0 and -0.08; the same frame with the sheet layer's map forced to its value at the
    //                        vessel (cloud.mapOverride, the frame that tells a map that varies over the view from the pass); at the ground view the layers one at a time for the set named
    //                        by -atmoLookWin; the rain deck's coverage 0.5 against 0.42 (cloud.coverageShiftRain, Q21) at the ground view; at the high cover the rain deck alone from the
    //                        inside (4.6 km) and from above (P3: it drew nothing at map 0.86).
    //   -atmoLook B          start 52, "look B": the light and the surface. The same three view/cover pairs with the sun tint 0 / 0.5 / 1 (cloud.sunTint), the exposure 1.4 and the sun's
    //                        path through the air with Scatterer loaded; the horizon pose (pitch +2 degrees, 200 m) at the low cover with the distance fade 0 / 20 / 40 / 80 km (cloud.fadeFarM)
    //                        with and without the aerial perspective (cloud.farHaze); the aid frame of the direct light, the taps and the ambient.
    // Rows, all T09d.<view>.<cover>.<frame>.*: the readout of the frame (EmitReading), written after the expectation rows (<frame>.expect_closed_mean / _p10 / _p90 / _sites: the CPU
    // oracle's closed share for the camera's geometry and the stack's CPU maps at the vessel with the knobs as they are, CloudLookModel.Expect, written BEFORE the frame is read) and
    // T09d.<view>.<cover>.L<k>.map_at_<d>_km, the layer's coverage map along the view's heading. Frames are named k<set>[m8] (shift 0 / -0.08), kovr (map forced), w<set>o<k> (layer k alone),
    // q50 / q42, l0in / l0up, t0 / t5 / t10 (tint), e14, air1, hz<fade in km>h<0|1>, aid4.
    internal sealed partial class T09Probe
    {
        private const string LookSwitch = "-atmoLook", LookSetsSwitch = "-atmoLookSets", LookWinSwitch = "-atmoLookWin";
        // The shift of the second frame of a set (the plan: each set at shift 0 and -0.08).
        private const float LookShift = -0.08f;
        // The seconds the maps need to slew to a new type value (the slew limit is 0.08 a second, the type values move by at most 0.25).
        private const double LookTypeSettleS = 6.0;
        // The horizon pose of look B: 200 m up, 2 degrees above the horizontal.
        internal static readonly View HorizonView = new View { Name = "horizon", AglM = 200.0, PitchDeg = 2.0, HeadingDeg = 90.0, CameraDistM = 100.0 };
        // The distances (km) along the view at which the layer maps are read.
        internal static readonly double[] LookMapKm = { 0.0, 5.0, 10.0, 20.0, 40.0, 80.0 };
        internal static readonly float[] HorizonFadesM = { 0f, 20000f, 40000f, 80000f };

        private string lookCache;
        private bool lookRead;

        // "A", "B" or null (the ordinary K-T2d scenario).
        internal string LookMode
        {
            get
            {
                if (!lookRead)
                {
                    lookRead = true;
                    string v = CommandLine.After(Args(), LookSwitch, true);
                    lookCache = string.Equals(v, "A", StringComparison.OrdinalIgnoreCase) ? "A" : string.Equals(v, "B", StringComparison.OrdinalIgnoreCase) ? "B" : null;
                }
                return lookCache;
            }
        }

        private List<LookSet> LookSelected()
        {
            return CloudLookModel.Selected(CommandLine.After(Args(), LookSetsSwitch, true));
        }

        // The set the layer-alone frames use: -atmoLookWin, else the last of the selected ones.
        private LookSet LookWin()
        {
            LookSet named = CloudLookModel.SetByTag(CommandLine.After(Args(), LookWinSwitch, true) ?? "");
            if (named != null) return named;
            List<LookSet> all = LookSelected();
            return all.Count == 0 ? CloudLookModel.Sets[0] : all[all.Count - 1];
        }

        // Which view/cover pairs a look start reads: the ground view at both covers and the above view at the low cover.
        private static bool LookWants(View view, string cover)
        {
            return view.Name == "ground" || cover == "low";
        }

        // The tag of the frame of a set at a shift.
        internal static string FrameTag(LookSet set, bool shifted)
        {
            return "k" + set.Tag + (shifted ? "m8" : "");
        }

        private void ApplyLookKeys(LookSet set, float shift)
        {
            ClearForced();
            foreach (KeyValuePair<string, float> k in set.Keys) ForceTuning(k.Key, k.Value);
            if (shift != 0f) ForceTuning(CloudCoverShift.TuneShift, shift);
        }

        // The sheet layer of the stack: the one with the highest map at the vessel that is not in a group (the rain deck is the group "weather"); the highest overall where all are.
        internal static int SheetLayer(T09LookInput input)
        {
            int best = -1;
            for (int l = 0; l < input.Layers.Length; l++)
            {
                if (input.Layers[l].Group.Length > 0) continue;
                if (best < 0 || input.Cover[l] > input.Cover[best]) best = l;
            }
            if (best >= 0) return best;
            for (int l = 0; l < input.Layers.Length; l++)
                if (best < 0 || input.Cover[l] > input.Cover[best]) best = l;
            return best;
        }

        // The expectation rows of the frame about to be read (CloudLookModel.Expect with the knobs as they are now); written before DiagTake.
        private void EmitLookExpectation(string head, string frame, View view, T09LookInput input)
        {
            if (input == null) return;
            LookExpectation e;
            try { e = CloudLookModel.Expect(input, Game.ReadPose().CameraAsl, view.PitchDeg, Game.CameraFovDeg); }
            catch (Exception ex)
            {
                Warn("look_expectation_fault", head + frame + ": " + ex.GetType().Name + " " + ex.Message);
                return;
            }
            Emit(head + frame + ".expect_closed_mean", e.Mean);
            Emit(head + frame + ".expect_closed_p10", e.P10);
            Emit(head + frame + ".expect_closed_p90", e.P90);
            Emit(head + frame + ".expect_sites", e.Sites);
        }

        // The layers' coverage maps along the view's heading (the CPU maps; the oracle assumes the one at the vessel for the whole sky).
        private void EmitLookMapProfile(string head, View view, T09LookInput input)
        {
            if (input == null || input.CoverAt == null) return;
            double lat0 = Game.VesselLatitude, lon0 = Game.VesselLongitude;
            for (int l = 0; l < input.Layers.Length; l++)
                foreach (double km in LookMapKm)
                {
                    double lat, lon;
                    CloudLookModel.Walk(lat0, lon0, view.HeadingDeg, km * 1000.0, input.BodyRadiusM, out lat, out lon);
                    Emit(head + "L" + l.ToString(CultureInfo.InvariantCulture) + ".map_at_" + km.ToString("0", CultureInfo.InvariantCulture) + "_km", input.CoverAt(l, lat, lon));
                }
        }

        // One frame under the forced keys: settle, expectation, read.
        private IEnumerable<ProbeWait> LookFrame(View view, string cover, string head, string frame, T09Readout.Reading off, bool typeWait)
        {
            yield return ProbeWait.Frames(DiagSettleFrames);
            if (typeWait) yield return ProbeWait.Seconds(LookTypeSettleS);
            EmitLookExpectation(head, frame, view, Game.ReadLookInput());
            foreach (ProbeWait w in DiagTake(view, cover, frame, false, off)) yield return w;
        }

        // The look A view: the baseline, the sets at two shifts, the forced map, then the ground-only frames.
        private IEnumerable<ProbeWait> DiagLookA(View view, string cover)
        {
            string head = "T09d." + view.Name + "." + cover + ".";
            ClearForced();
            Game.SetLayerMask(NoLayersMask);
            yield return ProbeWait.Frames(DiagSettleFrames);
            foreach (ProbeWait w in DiagTake(view, cover, "off", false, null)) yield return w;
            T09Readout.Reading off = diagReading;
            Game.SetLayerMask(0);
            yield return ProbeWait.Frames(DiagSettleFrames);
            T09LookInput input = Game.ReadLookInput();
            if (input == null) Warn("no_look_input", head + "the pass has no layer stack to read: the expectation rows are missing");
            else
            {
                EmitLookMapProfile(head, view, input);
                int sheet = SheetLayer(input);
                if (sheet >= 0) EmitText(head + "sheet_layer", input.Layers[sheet].Name);
            }
            foreach (LookSet set in LookSelected())
                foreach (bool shifted in new[] { false, true })
                {
                    ApplyLookKeys(set, shifted ? LookShift : 0f);
                    foreach (ProbeWait w in LookFrame(view, cover, head, FrameTag(set, shifted), off, set.ChangesTypes)) yield return w;
                }
            // The map of the sheet layer forced to its value at the vessel, everywhere: if the frame then shows the closed share the oracle expects, the map varied over the view; if
            // it still shows a sheet, the pass differs from the CPU.
            if (input != null)
            {
                int sheet = SheetLayer(input);
                if (sheet >= 0)
                {
                    ClearForced();
                    ForceTuning(CloudLookTuning.TuneMapOverride + input.Layers[sheet].Name, (float)input.Cover[sheet]);
                    Emit(head + "kovr.forced_map", input.Cover[sheet]);
                    foreach (ProbeWait w in LookFrame(view, cover, head, "kovr", off, false)) yield return w;
                }
            }
            ClearForced();
            if (view.Name != "ground") yield break;
            // The layers one at a time under the winning set (P3 and the shape of each layer).
            LookSet win = LookWin();
            int count = input == null ? CloudLayerDef.MaxLayers : input.Layers.Length;
            for (int k = 0; k < count; k++)
            {
                ApplyLookKeys(win, 0f);
                Game.SetLayerMask(1 << k);
                yield return ProbeWait.Frames(DiagSettleFrames);
                if (win.ChangesTypes) yield return ProbeWait.Seconds(LookTypeSettleS);
                foreach (ProbeWait w in DiagTake(view, cover, "w" + win.Tag + "o" + k.ToString(CultureInfo.InvariantCulture), false, off)) yield return w;
            }
            Game.SetLayerMask(0);
            // Q21: the rain deck's coverage 0.5 (the file) against 0.42 on its own (cloud.coverageShiftRain).
            foreach (float rain in new[] { 0f, -0.08f })
            {
                ClearForced();
                if (rain != 0f) ForceTuning(CloudCoverShift.TuneShiftRain, rain);
                foreach (ProbeWait w in LookFrame(view, cover, head, rain == 0f ? "q50" : "q42", off, false)) yield return w;
            }
            ClearForced();
            yield return ProbeWait.Frames(DiagSettleFrames);
        }

        // Look A, high cover: the rain deck alone from inside (4.6 km) and from above (P3).
        private IEnumerable<ProbeWait> DiagLookL0(View view, string cover)
        {
            string head = "T09d." + view.Name + "." + cover + ".";
            ClearForced();
            Game.SetLayerMask(NoLayersMask);
            yield return ProbeWait.Frames(DiagSettleFrames);
            foreach (ProbeWait w in DiagTake(view, cover, "off", false, null)) yield return w;
            T09Readout.Reading off = diagReading;
            Game.SetLayerMask(1 << 0);
            yield return ProbeWait.Frames(DiagSettleFrames);
            foreach (ProbeWait w in DiagTake(view, cover, view.Name == "inside" ? "l0in" : "l0up", false, off)) yield return w;
            Game.SetLayerMask(0);
            yield return ProbeWait.Frames(DiagSettleFrames);
        }

        // The tint and exposure variants of look B (the frames the deck's colour is read from: deck r, g, b are rows of every frame).
        internal struct LightVariant
        {
            internal string Frame;
            internal float Tint, Exposure, SunAir;
        }

        internal static readonly LightVariant[] LightVariants =
        {
            new LightVariant { Frame = "t0", Tint = 0f, Exposure = 1f, SunAir = 0f },
            new LightVariant { Frame = "t5", Tint = 0.5f, Exposure = 1f, SunAir = 0f },
            new LightVariant { Frame = "t10", Tint = 1f, Exposure = 1f, SunAir = 0f },
            new LightVariant { Frame = "e14", Tint = 0f, Exposure = 1.4f, SunAir = 0f },
            new LightVariant { Frame = "air1", Tint = 0f, Exposure = 1f, SunAir = 1f }
        };

        private IEnumerable<ProbeWait> DiagLookB(View view, string cover)
        {
            string head = "T09d." + view.Name + "." + cover + ".";
            ClearForced();
            Game.SetLayerMask(NoLayersMask);
            yield return ProbeWait.Frames(DiagSettleFrames);
            foreach (ProbeWait w in DiagTake(view, cover, "off", false, null)) yield return w;
            T09Readout.Reading off = diagReading;
            Game.SetLayerMask(0);
            yield return ProbeWait.Frames(DiagSettleFrames);
            T09Readout.Reading first = null;
            foreach (LightVariant v in LightVariants)
            {
                ClearForced();
                if (v.Tint != 0f) ForceTuning(CloudLookTuning.TuneSunTint, v.Tint);
                if (v.Exposure != 1f) ForceTuning(OwnCloudModule.TuneExposure, v.Exposure);
                if (v.SunAir != 0f) ForceTuning(OwnCloudModule.TuneSunAirWithScatterer, v.SunAir);
                yield return ProbeWait.Frames(DiagSettleFrames);
                OwnCloudModule own = OwnCloudModule.Current;
                if (own != null)
                {
                    Emit(head + v.Frame + ".sun_r", own.Frame.SunColour.r);
                    Emit(head + v.Frame + ".sun_g", own.Frame.SunColour.g);
                    Emit(head + v.Frame + ".sun_b", own.Frame.SunColour.b);
                }
                foreach (ProbeWait w in DiagTake(view, cover, v.Frame, false, off)) yield return w;
                if (first == null) first = diagReading;
            }
            ClearForced();
            yield return ProbeWait.Frames(DiagSettleFrames);
            bool on = false;
            try { on = Game.AidOn(4); }
            catch (Exception e) { Warn("aid_view_fault", view.Name + " " + cover + ": " + e.GetType().Name + " " + e.Message); }
            if (!on)
            {
                Warn("no_aid_view", view.Name + " " + cover + ": the own pass has no aid view now");
                yield break;
            }
            yield return ProbeWait.Frames(DiagAidFrames);
            foreach (ProbeWait w in DiagTake(view, cover, "aid4", true, first)) yield return w;
            try { Game.AidOff(); }
            catch (Exception e) { Warn("aid_view_fault", view.Name + " " + cover + ": switching it off threw " + e.GetType().Name + " " + e.Message); }
        }

        // The horizon pose at the low cover: the distance fade and the aerial perspective.
        private IEnumerable<ProbeWait> DiagLookHorizon(string cover)
        {
            View view = HorizonView;
            string head = "T09d." + view.Name + "." + cover + ".";
            double lat = Game.VesselLatitude, lon = Game.VesselLongitude;
            foreach (ProbeWait w in PlaceChecked(lat, lon, view)) yield return w;
            Emit(head + "placed_ok", placeOk);
            ClearForced();
            Game.SetLayerMask(NoLayersMask);
            yield return ProbeWait.Frames(DiagSettleFrames);
            foreach (ProbeWait w in DiagTake(view, cover, "off", false, null)) yield return w;
            T09Readout.Reading off = diagReading;
            Game.SetLayerMask(0);
            yield return ProbeWait.Frames(DiagSettleFrames);
            foreach (float fade in HorizonFadesM)
                foreach (int haze in new[] { 0, 1 })
                {
                    ClearForced();
                    if (fade > 0f) ForceTuning(CloudLookTuning.TuneFadeFar, fade);
                    ForceTuning(CloudLookTuning.TuneFarHaze, haze);
                    string frame = "hz" + ((int)Math.Round(fade / 1000f)).ToString(CultureInfo.InvariantCulture) + "h" + haze.ToString(CultureInfo.InvariantCulture);
                    yield return ProbeWait.Frames(DiagSettleFrames);
                    EmitLookExpectation(head, frame, view, Game.ReadLookInput());
                    foreach (ProbeWait w in DiagTake(view, cover, frame, false, off)) yield return w;
                }
            ClearForced();
            yield return ProbeWait.Frames(DiagSettleFrames);
        }
    }
}
