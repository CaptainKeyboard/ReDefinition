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
    //                        with and without the aerial perspective (cloud.farHaze); the aid frame of the direct light, the taps and the ambient. Follow-up of start 52: a second baseline frame
    //                        (off2) and the repeats t0b / hz0h0b for the noise floor; the order pair at the horizon pose in the order 1, 0, 0, 1 (frames ord1, ord0, ord0b, ord1b) with the vertical
    //                        row profile of the deck (rows prof_b<k> / prof_a<k>, T09Readout.MeasureRowProfile); at the high cover the rain deck alone from 8.3 km and 2.3 km with the
    //                        cloud shadows off (l0up / l0mid + sh0), every layer drawn (+ all) and the aid frames of the optical depth and the light.
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
        private const double LookTypeSettleS = 6.0, LookMapSettleS = 2.0;
        // The horizon pose of look B: 200 m up, 2 degrees above the horizontal.
        // Look A, start 52: the rain deck alone from 2.3 km above the ground (above the deck's top at the wet site, where the T13 base floor puts it at 0 to about 355 m), looking 8 degrees up like the 4.6 km view.
        internal static readonly View DeckMidView = new View { Name = "deck", AglM = 2300.0, PitchDeg = 8.0, HeadingDeg = 90.0, CameraDistM = 150.0 };
        // The probe's limit in a look start (the ordinary K-T2d start is 8 minutes): about 80 frames, the order pair's waits and the type waits.
        internal const double LookMaxSeconds = 12 * 60;
        // The seconds the order switch (cloud.afterScatterer) needs: the pass re-reads the camera's buffer order once a second in OnPreRender and moves its buffers in the same frame; the
        // window after it counts the moves.
        private const double OrderSettleS = 2.5, OrderCountS = 1.0, OrderGiveUpS = 8.0;
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
                    // "G" is the grain start (T09ProbeGrain.cs), "C" the start look55 (T09ProbeLook55.cs), "H" the start gt57 (T09ProbeGt.cs).
                    lookCache = string.Equals(v, "A", StringComparison.OrdinalIgnoreCase) ? "A" : string.Equals(v, "B", StringComparison.OrdinalIgnoreCase) ? "B"
                        : string.Equals(v, "G", StringComparison.OrdinalIgnoreCase) ? "G" : string.Equals(v, "C", StringComparison.OrdinalIgnoreCase) ? "C"
                        : string.Equals(v, "H", StringComparison.OrdinalIgnoreCase) ? "H" : null;
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
        private bool LookWants(View view, string cover)
        {
            // The grain start reads the low cover only (both views).
            if (LookMode == "G") return cover == "low";
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
        private void EmitLookExpectation(string head, string frame, View view, T09LookInput input, int onlyLayer = -1)
        {
            if (input == null) return;
            LookExpectation e;
            try { e = CloudLookModel.Expect(input, Game.ReadPose().CameraAsl, view.PitchDeg, Game.CameraFovDeg, onlyLayer: onlyLayer); }
            catch (Exception ex)
            {
                Warn("look_expectation_fault", head + frame + ": " + ex.GetType().Name + " " + ex.Message);
                return;
            }
            Emit(head + frame + ".expect_closed_mean", e.Mean);
            Emit(head + frame + ".expect_closed_p10", e.P10);
            Emit(head + frame + ".expect_closed_p90", e.P90);
            Emit(head + frame + ".expect_sites", e.Sites);
            Emit(head + frame + ".expect_tau_zenith_mean", e.ZenithTauMean);
            Emit(head + frame + ".expect_tau_zenith_p10", e.ZenithTauP10);
            Emit(head + frame + ".expect_tau_zenith_p90", e.ZenithTauP90);
            Emit(head + frame + ".expect_zenith_closed", e.ZenithClosed);
        }

        // The layers' coverage maps along the view's heading (the CPU maps; the oracle assumes the one at the vessel for the whole sky).
        private void EmitLookMapProfile(string head, View view, T09LookInput input)
        {
            if (input == null || input.CoverAt == null) return;
            double lat0 = Game.VesselLatitude, lon0 = Game.VesselLongitude;
            for (int l = 0; l < input.Layers.Length; l++)
            {
                // The height maps' offsets at the vessel (the root offset is what lifts or sinks a layer at this place; K-T2d start 52 asked for the rain deck's).
                if (input.RootM != null && input.RootM.Length == input.Layers.Length)
                {
                    Emit(head + "L" + l.ToString(CultureInfo.InvariantCulture) + ".root_m", input.RootM[l]);
                    Emit(head + "L" + l.ToString(CultureInfo.InvariantCulture) + ".top_m", input.TopM[l]);
                    Emit(head + "L" + l.ToString(CultureInfo.InvariantCulture) + ".variation_m", input.VariationM[l]);
                }
                foreach (double km in LookMapKm)
                {
                    double lat, lon;
                    CloudLookModel.Walk(lat0, lon0, view.HeadingDeg, km * 1000.0, input.BodyRadiusM, out lat, out lon);
                    Emit(head + "L" + l.ToString(CultureInfo.InvariantCulture) + ".map_at_" + km.ToString("0", CultureInfo.InvariantCulture) + "_km", input.CoverAt(l, lat, lon));
                }
            }
        }

        // One frame under the forced keys: settle, expectation, read.
        private IEnumerable<ProbeWait> LookFrame(View view, string cover, string head, string frame, T09Readout.Reading off, double extraWaitS, int onlyLayer = -1)
        {
            yield return ProbeWait.Frames(DiagSettleFrames);
            if (extraWaitS > 0.0) yield return ProbeWait.Seconds(extraWaitS);
            EmitLookExpectation(head, frame, view, Game.ReadLookInput(), onlyLayer);
            foreach (ProbeWait w in DiagTake(view, cover, frame, false, off)) yield return w;
        }

        // The aid frame of the optical depth (aid mode 1) of the scene as it stands (the keys and the mask forced now): rows <frame>.aidtau_*, decoded against the baseline's colours.
        // Written after the natural frame of the same set, so that the tau and the colour-change closed share of one pose sit side by side (K-T2d start 51 had no aid frame).
        private IEnumerable<ProbeWait> LookAidTau(View view, string cover, string frame, T09Readout.Reading off)
        {
            bool on = false;
            try { on = Game.AidOn(1); }
            catch (Exception e) { Warn("aid_view_fault", view.Name + " " + cover + " " + frame + ": " + e.GetType().Name + " " + e.Message); }
            if (!on)
            {
                Warn("no_aid_view", view.Name + " " + cover + " " + frame + ": the own pass has no aid view now");
                yield break;
            }
            yield return ProbeWait.Frames(DiagAidFrames);
            foreach (ProbeWait w in DiagTake(view, cover, "aid1" + frame, true, off, 1)) yield return w;
            try { Game.AidOff(); }
            catch (Exception e) { Warn("aid_view_fault", view.Name + " " + cover + " " + frame + ": switching it off threw " + e.GetType().Name + " " + e.Message); }
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
            // The maps and types of the pass move when the forced key set changes what they hold (the module snaps them in the frame after a look-knob change; the wait covers the
            // round and the upload). A frame whose set leaves the types alone still waits where the set before it changed them.
            bool typesChanged = false;
            foreach (LookSet set in LookSelected())
                foreach (bool shifted in new[] { false, true })
                {
                    ApplyLookKeys(set, shifted ? LookShift : 0f);
                    double wait = set.ChangesTypes ? LookTypeSettleS : typesChanged ? LookMapSettleS : 0.0;
                    typesChanged = set.ChangesTypes;
                    foreach (ProbeWait w in LookFrame(view, cover, head, FrameTag(set, shifted), off, wait)) yield return w;
                    // The optical depth of the set's own frame at shift 0, for the current set (C0) and the winning one.
                    if (!shifted && (set == CloudLookModel.Sets[0] || set == LookWin()))
                        foreach (ProbeWait w in LookAidTau(view, cover, FrameTag(set, false), off)) yield return w;
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
                    foreach (ProbeWait w in LookFrame(view, cover, head, "kovr", off, LookMapSettleS)) yield return w;
                }
            }
            ClearForced();
            // Start 52: the order pair, at the low cover (the ground view and the view from above).
            if (cover == "low")
                foreach (ProbeWait w in LookOrderPair(view, cover, head, off, new[] { 1, 0 })) yield return w;
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
                else if (k == 0) yield return ProbeWait.Seconds(LookMapSettleS);
                string alone = "w" + win.Tag + "o" + k.ToString(CultureInfo.InvariantCulture);
                // Start 52: the oracle's expectation for this layer alone (the zenith tau of L0 and of the sheet layer), and the aid frame of the rain deck and the sheet beside the natural one.
                if (k <= 1) EmitLookExpectation(head, alone, view, Game.ReadLookInput(), k);
                foreach (ProbeWait w in DiagTake(view, cover, alone, false, off)) yield return w;
                if (k <= 1)
                    foreach (ProbeWait w in LookAidTau(view, cover, alone, off)) yield return w;
            }
            Game.SetLayerMask(0);
            // Q21: the rain deck's coverage 0.5 (the file) against 0.42 on its own (cloud.coverageShiftRain).
            foreach (float rain in new[] { 0f, -0.08f })
            {
                ClearForced();
                if (rain != 0f) ForceTuning(CloudCoverShift.TuneShiftRain, rain);
                foreach (ProbeWait w in LookFrame(view, cover, head, rain == 0f ? "q50" : "q42", off, LookMapSettleS)) yield return w;
            }
            ClearForced();
            yield return ProbeWait.Frames(DiagSettleFrames);
        }

        // Start 52, the order pair: one pose, the current numbers (set C0), the pass drawn after Scatterer's screen-space scattering (cloud.afterScatterer 1, the default) and before it (0).
        // The pass re-reads the order of the camera's buffers once a second in OnPreRender, so each frame waits OrderSettleS, then counts the frames in which the pass moved its buffers
        // over OrderCountS: with 1 the count is above 0 (Scatterer puts its buffers behind the pass again every frame), with 0 it is 0. The rows: <frame>.order_moves (the count),
        // <frame>.order_flag, <frame>.render_order (the text of the order the pass read), and the horizon-line rows of the frame (EmitHorizonLine) that tell whether the cloud's colour steps
        // at the sea horizon's row. The env lines of the log (T09d env renderOrder) are written after each wait.
        // flags: the order of the switch values, one frame each (start 52: 1, 0; the follow-up of look B: 1, 0, 0, 1, so that a drift of the scene over the batch (the eye adaptation, the sun,
        // the weather writer) falls on both orders alike and the repeat of each is the noise floor of the pair). The first frame of a value is named ord<flag>, the repeat ord<flag>b.
        private IEnumerable<ProbeWait> LookOrderPair(View view, string cover, string head, T09Readout.Reading off, int[] flags)
        {
            string previousOrder = null;
            int previousFlag = -1;
            var seen = new HashSet<int>();
            foreach (int flag in flags)
            {
                string frame = "ord" + flag.ToString(CultureInfo.InvariantCulture) + (seen.Add(flag) ? "" : "b");
                ApplyLookKeys(CloudLookModel.Sets[0], 0f);
                ForceTuning(OwnCloudModule.TuneAfterScatterer, flag);
                // The pair measures Scatterer's air on the cloud against none; the pass's aerial perspective (on by default behind the scattering) is held off so that it does not fill that gap.
                ForceTuning(CloudLookTuning.TuneFarHaze, 0f);
                yield return ProbeWait.Frames(DiagSettleFrames);
                yield return ProbeWait.Seconds(OrderSettleS);
                OwnCloudModule own = OwnCloudModule.Current;
                if (own == null || own.RenderOrder == null || own.RenderOrder.StartsWith("not read yet", StringComparison.Ordinal))
                {
                    Warn("no_order_state", head + frame + ": the own pass is not there or has not read the camera's buffer order; the order is not read");
                }
                else
                {
                    int before = own.Stats.OrderMoves;
                    yield return ProbeWait.Seconds(OrderCountS);
                    int moves = own.Stats.OrderMoves - before;
                    Emit(head + frame + ".order_flag", flag);
                    Emit(head + frame + ".order_moves", moves);
                    Emit(head + frame + ".order_moving", own.MovesBehindScattering);
                    string order = own.RenderOrder ?? "";
                    EmitText(head + frame + ".render_order", order);
                    LogEnvironment(true);
                    Check("T09d.order_" + view.Name + "_" + cover + "_" + frame, flag == 1 ? moves > 0 : moves == 0,
                          "cloud.afterScatterer " + flag + ": " + moves + " moved frames in " + OrderCountS.ToString("0.0") + " s; order " + order);
                    if (previousOrder != null && flag != previousFlag)
                        Check("T09d.order_changed_" + view.Name + "_" + cover + (flags.Length > 2 ? "_" + frame : ""),
                              !string.Equals(previousOrder, order, StringComparison.Ordinal),
                              "the buffer order read with the switch on and off: " + previousOrder + " | " + order);
                    previousOrder = order;
                    previousFlag = flag;
                }
                foreach (ProbeWait w in DiagTake(view, cover, frame, false, off)) yield return w;
            }
            ClearForced();
            yield return ProbeWait.Frames(DiagSettleFrames);
        }

        // Look A, high cover: the rain deck alone from inside (4.6 km), from 2.3 km and from above (P3).
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
            string l0Frame = view.Name == "inside" ? "l0in" : view.Name == "deck" ? "l0mid" : "l0up";
            EmitLookExpectation(head, l0Frame, view, Game.ReadLookInput(), 0);
            foreach (ProbeWait w in DiagTake(view, cover, l0Frame, false, off)) yield return w;
            foreach (ProbeWait w in LookAidTau(view, cover, l0Frame, off)) yield return w;
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
            new LightVariant { Frame = "air1", Tint = 0f, Exposure = 1f, SunAir = 1f },
            // The sun's path through the air with the full tint (the two together, beige against white), and the repeat of the first frame at the end of the batch: what differs between
            // t0 and t0b is the drift of the scene and the noise of the pass, the floor under every difference between the variants.
            new LightVariant { Frame = "air1t10", Tint = 1f, Exposure = 1f, SunAir = 1f },
            new LightVariant { Frame = "t0b", Tint = 0f, Exposure = 1f, SunAir = 0f }
        };

        private IEnumerable<ProbeWait> DiagLookB(View view, string cover)
        {
            string head = "T09d." + view.Name + "." + cover + ".";
            ClearForced();
            Game.SetLayerMask(NoLayersMask);
            yield return ProbeWait.Frames(DiagSettleFrames);
            foreach (ProbeWait w in DiagTake(view, cover, "off", false, null)) yield return w;
            T09Readout.Reading off = diagReading;
            // The noise floor of everything read against the baseline: a second baseline frame of the same pose a few frames later (its rows are read beside the first's).
            yield return ProbeWait.Frames(DiagSettleFrames);
            foreach (ProbeWait w in DiagTake(view, cover, "off2", false, null)) yield return w;
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
            // The view from above at the low cover: the deck lies over the sea there, at 20 to 60 km. The order pair in the order 1, 0, 0, 1 with the row profile, and the pass's own aerial
            // perspective (cloud.farHaze 1) at the strength 1 and 2 with the order at its default (1: the pass behind Scatterer's air).
            if (view.Name == "above" && cover == "low")
            {
                foreach (ProbeWait w in LookOrderPair(view, cover, head, off, new[] { 1, 0, 0, 1 })) yield return w;
                foreach (ProbeWait w in LookHazeFrames(view, cover, head, off)) yield return w;
            }
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

        // The pass's aerial perspective (T06: the cloud's colour fogged towards the horizon sky by the air to its mean distance; cloud.farHaze, strength cloud.hazeStrength) at the strength 1 and 2:
        // frames hh1 and hh2. The strength 2 is the ratio of Scatterer's Kerbin air to the pass's own coefficients (computed, E40-horizon-fade-design.md). Rows prof_* beside the order frames.
        private IEnumerable<ProbeWait> LookHazeFrames(View view, string cover, string head, T09Readout.Reading off)
        {
            foreach (float strength in new[] { 1f, 2f })
            {
                ClearForced();
                ForceTuning(CloudLookTuning.TuneFarHaze, 1f);
                if (strength != 1f) ForceTuning(CloudLookTuning.TuneHazeStrength, strength);
                string frame = "hh" + ((int)strength).ToString(CultureInfo.InvariantCulture);
                yield return ProbeWait.Frames(DiagSettleFrames);
                EmitLookExpectation(head, frame, view, Game.ReadLookInput());
                foreach (ProbeWait w in DiagTake(view, cover, frame, false, off)) yield return w;
            }
            ClearForced();
            yield return ProbeWait.Frames(DiagSettleFrames);
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
            yield return ProbeWait.Frames(DiagSettleFrames);
            foreach (ProbeWait w in DiagTake(view, cover, "off2", false, null)) yield return w;
            Game.SetLayerMask(0);
            yield return ProbeWait.Frames(DiagSettleFrames);
            // The order pair at the horizon pose, where the sea below the row is clear (the sheet is overhead): 1, 0, 0, 1, each repeated, rows prof_* give the colour of the deck row by row.
            foreach (ProbeWait w in LookOrderPair(view, cover, head, off, new[] { 1, 0, 0, 1 })) yield return w;
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
            // The repeat of the first frame (no fade, no haze) at the end of the batch: the floor under the differences between the frames above.
            ClearForced();
            ForceTuning(CloudLookTuning.TuneFarHaze, 0f);
            yield return ProbeWait.Frames(DiagSettleFrames);
            EmitLookExpectation(head, "hz0h0b", view, Game.ReadLookInput());
            foreach (ProbeWait w in DiagTake(view, cover, "hz0h0b", false, off)) yield return w;
            ClearForced();
            yield return ProbeWait.Frames(DiagSettleFrames);
        }

        // Look B at the high cover (the rain deck L0 alone, from 2.3 km and from above): the deck's own light. Frames: off (no layer), the deck (l0mid / l0up), the same with the cloud shadows
        // off (<frame>sh0), the same with every layer drawn (<frame>all, the layers that the layer-alone frames hide and that the deck's light march still sees), and the aid frames: optical
        // depth (aid1) and light (aid4: direct r, light taps g, ambient b; the pass has no separate shadow channel, the cloud's shadow on itself is in the direct term).
        private IEnumerable<ProbeWait> DiagLookDeckLight(View view, string cover)
        {
            string head = "T09d." + view.Name + "." + cover + ".";
            ClearForced();
            Game.SetLayerMask(NoLayersMask);
            yield return ProbeWait.Frames(DiagSettleFrames);
            foreach (ProbeWait w in DiagTake(view, cover, "off", false, null)) yield return w;
            T09Readout.Reading off = diagReading;
            string frame = view.Name == "deck" ? "l0mid" : "l0up";
            Game.SetLayerMask(1 << 0);
            yield return ProbeWait.Frames(DiagSettleFrames);
            EmitLookExpectation(head, frame, view, Game.ReadLookInput(), 0);
            foreach (ProbeWait w in DiagTake(view, cover, frame, false, off)) yield return w;
            T09Readout.Reading natural = diagReading;
            foreach (ProbeWait w in LookAidTau(view, cover, frame, off)) yield return w;
            foreach (ProbeWait w in LookAidLight(view, cover, frame, natural)) yield return w;
            // The cloud shadow off: the pass does not read it (its buffers are the sun's mask and the opaque colour, both before the pass), so the deck does not change; the sea under an open
            // deck would.
            double shadowsBefore = AtmospheresSettings.CloudShadows ? 1.0 : 0.0;
            if (SetSetting(AtmospheresSettings.Keys.CloudShadows, 0.0))
            {
                yield return ProbeWait.Frames(DiagSettleFrames);
                foreach (ProbeWait w in DiagTake(view, cover, frame + "sh0", false, off)) yield return w;
                SetSetting(AtmospheresSettings.Keys.CloudShadows, shadowsBefore);
                yield return ProbeWait.Frames(DiagSettleFrames);
            }
            else Warn("shadow_switch_unreadable", head + "the setting cloudShadows could not be set; the frame " + frame + "sh0 is not taken");
            // Every layer drawn: the layers above the deck are in the picture where they cast the deck's dark patches.
            Game.SetLayerMask(0);
            yield return ProbeWait.Frames(DiagSettleFrames);
            foreach (ProbeWait w in DiagTake(view, cover, frame + "all", false, off)) yield return w;
            Game.SetLayerMask(0);
            yield return ProbeWait.Frames(DiagSettleFrames);
        }

        // The aid frame of the light (aid mode 4) of the scene as it stands, read over the deck of the natural frame next to it: rows <frame>aid4.direct_*, taps_*, ambient_* (EmitAid).
        private IEnumerable<ProbeWait> LookAidLight(View view, string cover, string frame, T09Readout.Reading natural)
        {
            bool on = false;
            try { on = Game.AidOn(4); }
            catch (Exception e) { Warn("aid_view_fault", view.Name + " " + cover + " " + frame + ": " + e.GetType().Name + " " + e.Message); }
            if (!on)
            {
                Warn("no_aid_view", view.Name + " " + cover + " " + frame + ": the own pass has no aid view now");
                yield break;
            }
            yield return ProbeWait.Frames(DiagAidFrames);
            foreach (ProbeWait w in DiagTake(view, cover, "aid4" + frame, true, natural)) yield return w;
            try { Game.AidOff(); }
            catch (Exception e) { Warn("aid_view_fault", view.Name + " " + cover + " " + frame + ": switching it off threw " + e.GetType().Name + " " + e.Message); }
        }
    }
}
