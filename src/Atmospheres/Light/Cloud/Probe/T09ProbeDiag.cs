using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Series K-T2d (package T09): why the own clouds are drawn black in the game (K-T2 start 42; .local/plans/atmospheres/K-T2-black-clouds-diagnosis.md, "One game start
    // would settle the rest"). One start, the variant plain only, no timing. Two universal times chosen by the clock step, one with a cover of about 0.3 and one with about 0.7
    // ("low" and "high"; every candidate hour's cover is emitted as T09.cover.<ut>). At each, in the views ground and above (the poses held, verified at every capture):
    //  - off: the baseline frame of the same pose, the pass running with no layer drawn (Frame.LayerMask names no layer, so the stack and the maps stay as they are);
    //  - gains: cloud.sunGain 1, 4, 16 and 64, one frame each;
    //  - aid 4: the pass's direct light, light taps and ambient as colours (gain 1).
    // Each frame is read as raw values (ReconFrame.Raw: what the target held, no gamma step) and measured in three regions (T09Readout): the sky above the horizon, the sea or
    // ground below it, and the deck (the pixels the pass changed against the off frame). Rows T09d.<view>.<cover>.<gain>.<stat>; the ratios deck_over_off_sky and
    // deck_over_off_ground (the sunlit sea or ground of the off frame, the white reference) need no constant fit. The light and the target are logged at the first drawn
    // frame and every 30 s (T09Environment). Pictures: half size, name t09-<start>-<view>_<cover>-<gain>-WxH-natural.
    internal sealed partial class T09Probe
    {
        internal const string DiagSeries = "K-T2d";
        internal const double DiagMaxSeconds = 8 * 60;
        // The covers asked for (the cover of the field at 3 km): the target and the band that counts as hitting it.
        private const double LowMin = 0.2, LowMax = 0.4, HighTarget = 0.7, HighMin = 0.6, HighMax = 0.8;
        // The two covers must differ by at least this to be an experiment; the picks' sun elevation band (the wide one is SunMinDeg to SunMaxDeg of K-T2).
        private const double MinCoverGap = 0.15, NarrowSunMinDeg = 30.0, NarrowSunMaxDeg = 55.0;
        // The far window of the cover scan: from 48 hours on, a candidate every 7800 s (not a divisor of the 6 h day, so the sun's phase differs from candidate to candidate)
        // up to 10 days of 21600 s.
        private const double FarStepS = 7800.0;
        private const int FarCandidates = 110;
        private const int MaxClockTries = 24, DiagSettleFrames = 30, DiagAidFrames = 8;
        // Real seconds the maps need after a clock jump (a map round every second, the slew limit 0.08 a second).
        private const double MapSettleS = 20.0, EnvEveryS = 30.0;
        // The mask that draws no layer (the shader skips layer i when bit i is clear; the stack has at most 8).
        internal const int NoLayersMask = 1 << 20;
        // The ladder stops at 16 (the deck clips from there on; g64 of start 44 added nothing): the frames freed pay for the coverage variants.
        internal static readonly float[] DiagGains = { 1f, 4f, 16f };
        private const string AidDirect = "aid-direct";

        // The variants of the output stage measured at gain 1 after the ladder: tag, sun unit, gamma, exposure. u1: the units alone; p1: the product's defaults (pi, 2.2, 1);
        // pe2: the product's stage at exposure 2 (the deck must rise by 2 ^ (1 / 2.2) = 1.37 where it is below the white).
        internal struct StageVariant
        {
            internal string Tag;
            internal float SunUnit, Gamma, Exposure;
        }

        internal static readonly StageVariant[] StageVariants =
        {
            new StageVariant { Tag = "u1", SunUnit = CloudOutputStage.SunUnitDefault, Gamma = 1f, Exposure = 1f },
            new StageVariant { Tag = "p1", SunUnit = CloudOutputStage.SunUnitDefault, Gamma = CloudOutputStage.GammaDefault, Exposure = CloudOutputStage.ExposureDefault },
            new StageVariant { Tag = "pe2", SunUnit = CloudOutputStage.SunUnitDefault, Gamma = CloudOutputStage.GammaDefault, Exposure = 2f }
        };

        // The coverage variants after p1 (p1 is the shift 0 frame): tag, cloud.coverageShift, the rain deck's resulting coverage (the file's 0.5 plus the shift).
        internal struct ShiftVariant
        {
            internal string Tag;
            internal float Shift;
        }

        internal static readonly ShiftVariant[] ShiftVariants =
        {
            new ShiftVariant { Tag = "c42", Shift = -0.08f },
            new ShiftVariant { Tag = "c35", Shift = -0.15f }
        };

        // The shifts the model rows cover: p1 is the unshifted frame.
        private static readonly KeyValuePair<string, float>[] ModelShifts =
        {
            new KeyValuePair<string, float>("p1", 0f),
            new KeyValuePair<string, float>("c42", -0.08f),
            new KeyValuePair<string, float>("c35", -0.15f)
        };

        // The rows T09d.<view>.<cover>.<tag>.model_closed: CloudCoverModel's closed share of the rain deck's Stratus at the pass's map value for each shift, the slant of the view's
        // ray (18 degrees up: 3.24; 25 degrees down: 2.37). Written before the frames are read. NaN map (no rain layer found): no rows.
        private void EmitCoverModel(string head, View view, float mapCover)
        {
            if (float.IsNaN(mapCover)) return;
            double slant = 1.0 / Math.Sin(Math.Abs(view.PitchDeg) * Math.PI / 180.0);
            foreach (KeyValuePair<string, float> s in ModelShifts)
                Emit(head + s.Key + ".model_closed", CloudCoverModel.RainStratusClosed(mapCover, s.Value, slant));
        }

        // The rain deck's coverage as the layer reports it after the shift was forced: it must be the file's plus the shift (the shift reached the layer the readers use).
        private void EmitShiftReached(string head, ShiftVariant variant, int rainLayer, OwnCloudModule own)
        {
            Emit(head + variant.Tag + ".coverage_shift", variant.Shift);
            if (own == null || own.Stack == null || rainLayer < 0 || rainLayer >= own.Stack.LayerCount) return;
            CloudLayerDef layer = own.Stack.LayerAt(rainLayer);
            float effective = layer.EffectiveCoverage;
            Emit(head + variant.Tag + ".rain_coverage", effective);
            Emit(head + variant.Tag + ".shift_reached", Math.Abs(effective - (layer.Coverage + variant.Shift)) < 1e-4f);
            if (Math.Abs(effective - (layer.Coverage + variant.Shift)) >= 1e-4f)
                Warn("coverage_shift_not_reached", head + variant.Tag + ": coverage " + layer.Coverage.ToString("0.000") + " -> " + effective.ToString("0.000") + " with shift " + variant.Shift.ToString("0.00"));
        }

        // The pass as it was before the output stage: radiometric sun colour = the scene's, no encoding.
        private void ForceRawStage()
        {
            ForceTuning(OwnCloudModule.TuneSunUnit, 1f);
            ForceTuning(OwnCloudModule.TuneOutputGamma, 1f);
        }

        private bool? diagCache;

        private bool IsDiag
        {
            get
            {
                if (diagCache == null) diagCache = IsActive(Args(), DiagSeries);
                return diagCache.Value;
            }
        }

        private struct CoverCandidate
        {
            internal double Ut, Cover;
        }

        private readonly List<CoverCandidate> coverCandidates = new List<CoverCandidate>();
        private bool coverOk, picked, pickedOffBand;
        private string coverFailure;
        private double lowUt, lowCover, lowSun, highUt, highCover, highSun, pickUt, pickCover, pickSun;
        private double nextEnvAt;
        private int diagFrames, diagFramesPoseOk;
        private bool envEmitted;

        // --- The clock: two times with different covers -----------------------------------------------------------------------------------

        // Samples the field at the site and 3 km for every candidate hour from the base (the field answers false until it has built the body's state: the pass repeats every
        // second up to FieldWaitS), emits every answer, and picks the hour whose cover is nearest to 0.3 and the one nearest to 0.7 whose sun stands in the band. A cover outside
        // the band is taken only when nothing in it fits, and is named in T09d.cover_off_band.
        // The candidates: every hour for Candidates hours from the base (repeated every second up to FieldWaitS until all of them answer: the field answers false until it has built
        // the body's state), then one candidate every FarStepS up to 10 days, taken as they answer. 20 samples a frame, so that the main thread is not held for the whole scan.
        // Fills coverCandidates and the counts candUnsampled (near candidates the field did not answer) and candFarAnswered. K-T2 and K-T2d share it.
        private int candUnsampled, candFarAnswered, candPasses;
        private double candWaitedS;

        private IEnumerable<ProbeWait> SampleCoverCandidates(int bodyIndex, double lat, double lon)
        {
            double start = Game.UniversalTime;
            double baseUt = Math.Ceiling(start / BaseStepS) * BaseStepS;
            double giveUpAt = Elapsed + FieldWaitS;
            candPasses = 0;
            while (true)
            {
                candPasses++;
                candWaitedS = Elapsed - (giveUpAt - FieldWaitS);
                candUnsampled = 0;
                candFarAnswered = 0;
                coverCandidates.Clear();
                int sampled = 0;
                for (int k = 0; k < Candidates + FarCandidates; k++)
                {
                    bool far = k >= Candidates;
                    double t = far ? baseUt + Candidates * CandidateStepS + (k - Candidates + 1) * FarStepS : baseUt + k * CandidateStepS;
                    WeatherSample s;
                    if (WeatherHub.Sample(bodyIndex, lat, lon, 3000.0, t, out s))
                    {
                        coverCandidates.Add(new CoverCandidate { Ut = t, Cover = s.CloudCover01 });
                        if (far) candFarAnswered++;
                    }
                    else if (!far) candUnsampled++;
                    if (++sampled % 20 == 0) yield return ProbeWait.Frames(1);
                }
                if (candUnsampled == 0 || Elapsed >= giveUpAt) break;
                yield return ProbeWait.Seconds(1);
            }
        }

        private IEnumerable<ProbeWait> ChooseCoverTimes(int bodyIndex, double lat, double lon)
        {
            coverOk = false;
            coverFailure = null;
            foreach (ProbeWait w in SampleCoverCandidates(bodyIndex, lat, lon)) yield return w;
            int unsampled = candUnsampled, farAnswered = candFarAnswered;
            foreach (CoverCandidate c in coverCandidates) Emit("T09.cover." + ((long)c.Ut).ToString(CultureInfo.InvariantCulture), c.Cover);
            Emit("T09d.candidates", coverCandidates.Count);
            Emit("T09d.candidates_far", farAnswered);
            Emit("T09d.candidates_unanswered", unsampled);
            if (coverCandidates.Count == 0)
            {
                IWeatherField field = WeatherHub.Field;
                coverFailure = "the weather field did not answer at the site for any of " + Candidates + " candidate times within " + FieldWaitS.ToString("0") + " s (field "
                               + (field != null ? field.GetType().Name + ", readiness " + field.Readiness : "none") + "; gates: " + CloudGates.Describe(AtmospheresSettings.Number) + ")";
                yield break;
            }
            // The two picks share one sun band, so that low against high is a comparison of cover and not of sun elevation (start 44: 45.5 against 15.6 degrees). The narrow band
            // 30 to 55 degrees first; where it holds no pair (a body or a day length that never puts the sun there at a candidate hour) the wide band 15 to 60.
            double coverRange = 0.0, minSeen = double.MaxValue, maxSeen = double.MinValue;
            foreach (CoverCandidate c in coverCandidates)
            {
                minSeen = Math.Min(minSeen, c.Cover);
                maxSeen = Math.Max(maxSeen, c.Cover);
            }
            coverRange = maxSeen - minSeen;
            bool lowOff = false, narrow = false;
            string failure = null;
            for (int band = 0; band < 2 && !coverOk; band++)
            {
                double sunMin = band == 0 ? NarrowSunMinDeg : SunMinDeg, sunMax = band == 0 ? NarrowSunMaxDeg : SunMaxDeg;
                // Low: the lowest cover available whose sun is in the band (even above 0.4: the band 0.2 to 0.4 only decides the cover_off_band flag).
                foreach (ProbeWait w in PickCover(true, double.NaN, double.NaN, double.NaN, double.NaN, sunMin, sunMax, double.NaN)) yield return w;
                if (!picked)
                {
                    failure = "no candidate hour has the sun " + sunMin.ToString("0") + " to " + sunMax.ToString("0") + " degrees up (" + coverCandidates.Count + " answered)";
                    continue;
                }
                lowUt = pickUt; lowCover = pickCover; lowSun = pickSun;
                lowOff = lowCover < LowMin || lowCover > LowMax;
                // High: the cover nearest to 0.7 (first inside 0.6 to 0.8) that is at least MinCoverGap above the low one, with the sun in the same band.
                foreach (ProbeWait w in PickCover(false, HighTarget, HighMin, HighMax, lowUt, sunMin, sunMax, lowCover + MinCoverGap)) yield return w;
                if (!picked)
                {
                    failure = coverRange < MinCoverGap
                        ? "the two covers are the same: the field's covers at the site span " + minSeen.ToString("0.00") + " to " + maxSeen.ToString("0.00") + " over " + coverCandidates.Count + " candidates (less than " + MinCoverGap.ToString("0.00") + " apart)"
                        : "no second candidate hour (the first is " + lowUt.ToString("0") + ", cover " + lowCover.ToString("0.00") + ") has a cover at least " + MinCoverGap.ToString("0.00") + " higher with the sun " + sunMin.ToString("0") + " to " + sunMax.ToString("0") + " degrees up";
                    continue;
                }
                highUt = pickUt; highCover = pickCover; highSun = pickSun;
                narrow = band == 0;
                coverOk = true;
            }
            if (!coverOk)
            {
                coverFailure = failure;
                yield break;
            }
            bool highOff = highCover < HighMin || highCover > HighMax;
            Emit("T09d.cover_off_band", lowOff || highOff);
            Emit("T09d.sun_band_narrow", narrow);
            Emit("T09d.sun_band_min_deg", narrow ? NarrowSunMinDeg : SunMinDeg);
            Emit("T09d.sun_band_max_deg", narrow ? NarrowSunMaxDeg : SunMaxDeg);
            Emit("T09d.sun_elev_diff_deg", Math.Abs(lowSun - highSun));
            // The product has no override of the cloud cover (coverStrength is the surface cover's; ISurfaceForcing forces precipitation), so a field that never drops under
            // 0.4 gives a low pick above the band: the start goes on and the rows name the covers. Only a pair under MinCoverGap apart is no experiment (failed above).
            Emit("T09d.covers_distinct", highCover - lowCover >= MinCoverGap);
        }

        // The candidate whose sun is in [sunMin, sunMax] and which is lowest in cover (lowest) or nearest to the target cover (first those inside [min, max], then the others),
        // at least minCover where that is given; the clock is set to try it.
        private IEnumerable<ProbeWait> PickCover(bool lowest, double target, double min, double max, double avoidUt, double sunMin, double sunMax, double minCover)
        {
            picked = false;
            pickedOffBand = false;
            var order = new List<CoverCandidate>(coverCandidates);
            if (lowest) order.Sort((a, b) => a.Cover.CompareTo(b.Cover));
            else order.Sort((a, b) => Math.Abs(a.Cover - target).CompareTo(Math.Abs(b.Cover - target)));
            for (int pass = 0; pass < (lowest ? 1 : 2) && !picked; pass++)
            {
                int tried = 0;
                foreach (CoverCandidate c in order)
                {
                    if (c.Ut == avoidUt) continue;
                    if (!double.IsNaN(minCover) && c.Cover < minCover) continue;
                    bool inBand = lowest || (c.Cover >= min && c.Cover <= max);
                    if (!lowest && inBand != (pass == 0)) continue;
                    if (tried++ >= MaxClockTries) break;
                    ForceUniversalTime(c.Ut);
                    yield return ProbeWait.Frames(6);
                    double elevation = Game.SunElevationDeg();
                    if (elevation < sunMin || elevation > sunMax) continue;
                    pickUt = c.Ut;
                    pickCover = c.Cover;
                    pickSun = elevation;
                    pickedOffBand = !inBand;
                    picked = true;
                    yield break;
                }
            }
        }

        // --- The scenario ------------------------------------------------------------------------------------------------------------------

        private IEnumerator<ProbeWait> DiagScenario()
        {
            foreach (ProbeWait w in Prelude()) yield return w;
            if (!preludeOk) yield break;
            foreach (string variant in Variants)
            {
                if (variant != "plain")
                {
                    Fail("K-T2d runs the variant plain only (the start asked for " + variant + ")");
                    yield break;
                }
            }
            Game.KeepRaw = true;
            double siteLat = Game.VesselLatitude, siteLon = Game.VesselLongitude;
            foreach (ProbeWait w in ChooseCoverTimes(Game.BodyIndex, siteLat, siteLon)) yield return w;
            if (!coverOk)
            {
                Fail(coverFailure);
                yield break;
            }
            Emit("T09d.low.ut", lowUt);
            Emit("T09d.low.cover", lowCover);
            Emit("T09d.low.sun_elev_deg", lowSun);
            Emit("T09d.high.ut", highUt);
            Emit("T09d.high.cover", highCover);
            Emit("T09d.high.sun_elev_deg", highSun);
            yield return ProbeWait.Seconds(3);
            CheckPicturesFolder();

            BeginVariant("plain");
            CheckVariantGates("plain");
            yield return ProbeWait.Seconds(4);
            OwnCloudModule m = OwnCloudModule.Current;
            int drawnFrom = m != null ? m.Stats.FramesDrawn : 0;
            yield return ProbeWait.Until(() =>
            {
                OwnCloudModule o = OwnCloudModule.Current;
                return o != null && o.Stats.Mode != "off" && o.Stats.FramesDrawn > drawnFrom;
            }, 150, "the own pass to draw its first frame");
            LogEnvironment(true);
            yield return ProbeWait.Until(() =>
            {
                OwnCloudModule o = OwnCloudModule.Current;
                return o != null && o.Stats.FramesDrawn - drawnFrom > SteadyFrames;
            }, 150, "the own pass to draw a steady stretch");

            foreach (ProbeWait w in DiagCover("low", lowUt, lowCover)) yield return w;
            foreach (ProbeWait w in DiagCover("high", highUt, highCover)) yield return w;
            Game.StopHold();
            Game.KeepRaw = false;
            Emit("T09d.frames", diagFrames);
            Emit("T09d.frames_pose_ok", diagFramesPoseOk);
            Emit("T09d.frames_pose_bad", diagFrames - diagFramesPoseOk);
            Emit("T09.pictures.saved", picturesSaved);
            Emit("T09.pictures.bytes", picturesBytes);
            Emit("T09.pictures.warnings", pictureWarnings);
            EndVariant();
        }

        // One cover: the clock, the maps, then the two views.
        private IEnumerable<ProbeWait> DiagCover(string tag, double ut, double cover)
        {
            ForceUniversalTime(ut);
            yield return ProbeWait.Frames(6);
            Emit("T09d." + tag + ".sun_elev_at_run_deg", Game.SunElevationDeg());
            AtmospheresLog.Say("T09d cover " + tag + ": ut " + ut.ToString("0") + ", cover " + cover.ToString("0.00") + "; waiting " + MapSettleS.ToString("0") + " s for the maps");
            yield return ProbeWait.Seconds(MapSettleS);
            LogEnvironment(false);
            double siteLat = Game.VesselLatitude, siteLon = Game.VesselLongitude;
            string look = LookMode;
            foreach (View view in new[] { Views[0], Views[2] })
            {
                // The look starts (T09ProbeLook) read the ground view at both covers and the above view at the low cover.
                if (look != null && !LookWants(view, tag)) continue;
                foreach (ProbeWait w in PlaceChecked(siteLat, siteLon, view)) yield return w;
                // A view that was not reached is read where the vessel stands, with the pose flagged in every row (PlaceChecked named it in a warning).
                Emit("T09d." + view.Name + "." + tag + ".placed_ok", placeOk);
                WeatherSample s;
                if (WeatherHub.Sample(Game.BodyIndex, siteLat, siteLon, Math.Min(20000.0, view.AglM), Game.UniversalTime, out s)) Emit("T09d." + view.Name + "." + tag + ".cover_at_view_height", s.CloudCover01);
                IEnumerable<ProbeWait> body = look == "A" ? DiagLookA(view, tag) : look == "B" ? DiagLookB(view, tag) : look == "G" ? DiagLookG(view, tag) : DiagView(view, tag);
                foreach (ProbeWait w in body) yield return w;
            }
            if (look == "A" && tag == "high")
            {
                // P3: the rain deck alone from inside (4.6 km) and from above, where K-T2d start 50 saw it draw nothing at map 0.86; start 52 adds 2.3 km, just above the deck's top.
                foreach (View view in new[] { Views[1], Views[2], DeckMidView })
                {
                    foreach (ProbeWait w in PlaceChecked(siteLat, siteLon, view)) yield return w;
                    Emit("T09d." + view.Name + "." + tag + ".placed_ok", placeOk);
                    foreach (ProbeWait w in DiagLookL0(view, tag)) yield return w;
                }
            }
            if (look == "B" && tag == "low")
                foreach (ProbeWait w in DiagLookHorizon(tag)) yield return w;
            if (look == "B" && tag == "high")
            {
                // The rain deck's own light from 8.3 km (above) and from 2.3 km: the dark patches of start 52's layer-alone frames, with the cloud shadow off and with every layer drawn.
                foreach (View view in new[] { Views[2], DeckMidView })
                {
                    foreach (ProbeWait w in PlaceChecked(siteLat, siteLon, view)) yield return w;
                    Emit("T09d." + view.Name + "." + tag + ".placed_ok", placeOk);
                    foreach (ProbeWait w in DiagLookDeckLight(view, tag)) yield return w;
                }
            }
        }

        // One view: the baseline, the gains, the aid.
        private IEnumerable<ProbeWait> DiagView(View view, string tag)
        {
            string head = "T09d." + view.Name + "." + tag + ".";
            OwnCloudModule own = OwnCloudModule.Current;
            // The baseline frame: the pass runs, no layer is drawn.
            ClearForced();
            Game.SetLayerMask(NoLayersMask);
            yield return ProbeWait.Frames(DiagSettleFrames);
            foreach (ProbeWait w in DiagTake(view, tag, "off", false, null)) yield return w;
            T09Readout.Reading off = diagReading;
            Game.SetLayerMask(0);
            yield return ProbeWait.Frames(DiagSettleFrames);

            float sunBase = 0f;
            T09Readout.Reading one = null;
            foreach (float gain in DiagGains)
            {
                string gainTag = "g" + gain.ToString("0", CultureInfo.InvariantCulture);
                // The ladder runs with the output stage off (sun unit 1, no gamma): the pass as K-T2d start 44 measured it, so the rows stay comparable.
                ClearForced();
                ForceRawStage();
                if (gain != 1f) ForceTuning(OwnCloudModule.TuneSunGain, gain);
                yield return ProbeWait.Frames(DiagSettleFrames);
                LogEnvironment(false);
                if (own != null)
                {
                    float sun = own.Frame.SunColour.grayscale;
                    // The check reads the gain off the sun colour over the scene's sun (the frame's OutputWhite), since the weather writer scales the scene's sun between two reads.
                    float gained = PassSunPerSceneSun(own.Frame);
                    if (gain == 1f) sunBase = gained;
                    Emit(head + gainTag + ".sun_luma", sun);
                    if (gain != 1f) Check("cloud.sunGain " + gain.ToString("0") + " reaches the pass in the " + view.Name + " view (" + tag + ")", sunBase > 1e-6f && Math.Abs(gained / sunBase - gain) < 0.05f * gain,
                                          sunBase.ToString("0.000") + " -> " + gained.ToString("0.000"));
                }
                foreach (ProbeWait w in DiagTake(view, tag, gainTag, false, off)) yield return w;
                if (gain == 1f) one = diagReading;
                else if (diagReading != null && one != null && one.Deck.Count > 0 && diagReading.Deck.Count > 0 && one.Deck.Median > 1e-6f)
                    Emit(head + gainTag + ".deck_vs_g1", diagReading.Deck.Median / one.Deck.Median);
            }
            ClearForced();
            yield return ProbeWait.Frames(DiagSettleFrames);

            // What the pass's maps hold at the site, per layer (the layer's own coverage map and the type value): the cover the series picked is the field's at 3 km, the
            // rain deck reads its map at its own mid height, so the number the shape sees is this one. The model (CloudCoverModel) turns it into a closed share.
            int rainLayer = -1;
            float rainMapCover = float.NaN;
            if (own != null && own.Stack != null)
            {
                int fullPairs = 0;
                for (int l = 0; l < own.Stack.LayerCount; l++)
                {
                    float layerCover, layerType;
                    try { own.Stack.SampleMaps(l, Game.VesselLatitude, Game.VesselLongitude, out layerCover, out layerType); }
                    catch (Exception e) { Warn("layer_maps_fault", e.GetType().Name + " " + e.Message); break; }
                    Emit(head + "layer" + l + "_map_cover", layerCover);
                    Emit(head + "layer" + l + "_map_type", layerType);
                    // K-T2d start 48 read 1.0 and 0.0 in every layer because the stack held no maps: a layer at exactly that pair is a stack without maps, not a full sky.
                    if (layerCover >= 0.99999f && layerType == 0f) fullPairs++;
                    if (rainLayer < 0 && string.Equals(own.Stack.LayerAt(l).Group, CloudCoverShift.RainGroup, StringComparison.Ordinal))
                    {
                        rainLayer = l;
                        rainMapCover = layerCover;
                    }
                }
                if (fullPairs > 0 && fullPairs == own.Stack.LayerCount) Warn("layer_maps_constant", head + "every layer reads cover 1.0 and type 0.0: the stack holds no maps (StackMapMirror)");
            }

            // The output stage (CloudOutputStage), gain 1, in the same start: the units alone (sun unit pi, no gamma), the product's stage, and the product's stage at exposure 2.
            // Rows T09d.<view>.<cover>.<tag>.* like the ladder's; the expected numbers are in tests/series/K-T2d.json and kt2d.py.
            foreach (StageVariant variant in StageVariants)
            {
                ClearForced();
                ForceTuning(OwnCloudModule.TuneSunUnit, variant.SunUnit);
                ForceTuning(OwnCloudModule.TuneOutputGamma, variant.Gamma);
                ForceTuning(OwnCloudModule.TuneExposure, variant.Exposure);
                yield return ProbeWait.Frames(DiagSettleFrames);
                if (own != null)
                {
                    Emit(head + variant.Tag + ".sun_luma", own.Frame.SunColour.grayscale);
                    Emit(head + variant.Tag + ".stage_gamma", own.Frame.OutputGamma);
                    Emit(head + variant.Tag + ".stage_white", own.Frame.OutputWhite);
                }
                foreach (ProbeWait w in DiagTake(view, tag, variant.Tag, false, off)) yield return w;
                if (diagReading != null && one != null && one.Deck.Count > 0 && diagReading.Deck.Count > 0 && one.Deck.Median > 1e-6f)
                    Emit(head + variant.Tag + ".deck_vs_g1", diagReading.Deck.Median / one.Deck.Median);
            }
            ClearForced();
            yield return ProbeWait.Frames(DiagSettleFrames);

            // The coverage threshold of the rain deck, measured as a switchable variant (the owner has not decided between the file's 0.5 and about 0.42): the product's stage at gain 1
            // with cloud.coverageShift 0 (p1, above), -0.08 (coverage 0.42) and -0.15 (0.35). Readouts per frame: sky_closed_share / ground_closed_share (the proxy of
            // the share of samples changed by T09Readout.ClosedChange), sky_deck_share / ground_deck_share, deck_fraction, the deck median and the picture; beside them the model's closed share at the pass's own map
            // value (model_closed, CloudCoverModel.RainStratusClosed), written before the frame is read.
            EmitCoverModel(head, view, rainMapCover);
            foreach (ShiftVariant variant in ShiftVariants)
            {
                ClearForced();
                if (variant.Shift != 0f) ForceTuning(CloudCoverShift.TuneShift, variant.Shift);
                yield return ProbeWait.Frames(DiagSettleFrames);
                EmitShiftReached(head, variant, rainLayer, own);
                foreach (ProbeWait w in DiagTake(view, tag, variant.Tag, false, off)) yield return w;
            }
            ClearForced();
            yield return ProbeWait.Frames(DiagSettleFrames);

            // K-T2d start 49: the ground view's closed sheet at the low cover did not follow the shift. One layer at a time (Frame.LayerMask names only layer k), at shift 0 and -0.15, beside
            // the per-layer numbers (CPU map, GPU map, the layer's group, type, threshold and softness, the model's closed share).
            if (view.Name == "ground")
                foreach (ProbeWait w in DiagLayers(view, tag, head, off)) yield return w;

            // The aid picture of the direct light (r), the light taps (g) and the ambient (b) over the deck the natural frame showed.
            bool on = false;
            try { on = Game.AidOn(4); }
            catch (Exception e) { Warn("aid_view_fault", view.Name + " " + tag + ": " + e.GetType().Name + " " + e.Message); }
            if (!on)
            {
                Warn("no_aid_view", view.Name + " " + tag + ": the own pass has no aid view now");
                yield break;
            }
            yield return ProbeWait.Frames(DiagAidFrames);
            foreach (ProbeWait w in DiagTake(view, tag, "aid4", true, one)) yield return w;
            try { Game.AidOff(); }
            catch (Exception e) { Warn("aid_view_fault", view.Name + " " + tag + ": switching it off threw " + e.GetType().Name + " " + e.Message); }
        }

        // The shift of the c35 frames of the layer-only series (the same as the ShiftVariants' c35).
        private const float LayerShift = -0.15f;
        // The frame tag of layer k alone: only<k> at shift 0, only<k>c35 at the shift -0.15.
        internal static string OnlyTag(int layer, bool shifted)
        {
            return "only" + layer.ToString(CultureInfo.InvariantCulture) + (shifted ? "c35" : "");
        }

        private IEnumerable<ProbeWait> DiagLayers(View view, string tag, string head, T09Readout.Reading off)
        {
            double slant = 1.0 / Math.Sin(Math.Abs(view.PitchDeg) * Math.PI / 180.0);
            var shifts = new float[ModelShifts.Length];
            for (int i = 0; i < shifts.Length; i++) shifts[i] = ModelShifts[i].Value;
            T09LayerReport report = null;
            try { report = Game.ReadLayers(Game.VesselLatitude, Game.VesselLongitude, slant, shifts); }
            catch (Exception e) { Warn("layer_report_fault", head + e.GetType().Name + " " + e.Message); }
            int count = CloudLayerDef.MaxLayers;
            WeatherSample surface;
            if (WeatherHub.Sample(Game.BodyIndex, Game.VesselLatitude, Game.VesselLongitude, 0.0, Game.UniversalTime, out surface)) Emit(head + "layers.surface_temp_k",surface.TemperatureK);
            if (report == null) Warn("no_layer_report", head + "the pass has no layer stack to read");
            else
            {
                count = report.Layers.Count;
                EmitLayerReport(head, report);
            }
            for (int pass = 0; pass < 2; pass++)
            {
                for (int k = 0; k < count; k++)
                {
                    ClearForced();
                    if (pass == 1) ForceTuning(CloudCoverShift.TuneShift, LayerShift);
                    Game.SetLayerMask(1 << k);
                    yield return ProbeWait.Frames(DiagSettleFrames);
                    foreach (ProbeWait w in DiagTake(view, tag, OnlyTag(k, pass == 1), false, off)) yield return w;
                }
            }
            Game.SetLayerMask(0);
            ClearForced();
            yield return ProbeWait.Frames(DiagSettleFrames);
        }

        // Rows T09d.<view>.<cover>.L<k>.* and the text rows stack_key / L<k>.info.
        private void EmitLayerReport(string head, T09LayerReport report)
        {
            EmitText(head + "layers.stack_key", report.StackKey);
            Emit(head + "layers.stack_cold", report.Cold);
            EmitFinite(head + "layers.cpu_lat_deg", report.CpuLatDeg);
            EmitFinite(head + "layers.cpu_lon_deg", report.CpuLonDeg);
            EmitFinite(head + "layers.shader_lat_deg", report.ShaderLatDeg);
            EmitFinite(head + "layers.shader_lon_deg", report.ShaderLonDeg);
            if (!double.IsNaN(report.ShaderLonDeg) && !double.IsNaN(report.CpuLonDeg))
            {
                double dLon = ((report.ShaderLonDeg - report.CpuLonDeg + 540.0) % 360.0) - 180.0;
                Emit(head + "layers.shader_lon_minus_cpu_deg", dLon);
                Emit(head + "layers.shader_lat_minus_cpu_deg",report.ShaderLatDeg - report.CpuLatDeg);
                if (Math.Abs(dLon) > 0.5 || Math.Abs(report.ShaderLatDeg - report.CpuLatDeg) > 0.5)
                    Warn("shader_place_differs", head + "the pass samples the maps at " + report.ShaderLatDeg.ToString("0.00", CultureInfo.InvariantCulture) + "/" + report.ShaderLonDeg.ToString("0.00", CultureInfo.InvariantCulture)
                                                 + " where the CPU readers look (" + report.CpuLatDeg.ToString("0.00", CultureInfo.InvariantCulture) + "/" + report.CpuLonDeg.ToString("0.00", CultureInfo.InvariantCulture) + ")");
            }
            foreach (T09LayerInfo l in report.Layers)
            {
                string p = head + "L" + l.Index.ToString(CultureInfo.InvariantCulture) + ".";
                EmitText(p + "info", "name " + l.Name + ", group " + (l.Group.Length == 0 ? "none" : l.Group) + ", type " + l.TypeName + ", coverage " + l.Coverage.ToString("0.###", CultureInfo.InvariantCulture)
                                     + ", softness " + l.Softness.ToString("0.###", CultureInfo.InvariantCulture) + ", noise " + l.NoiseAmount.ToString("0.###", CultureInfo.InvariantCulture)
                                     + ", sigma " + l.SigmaPerM.ToString("0.####", CultureInfo.InvariantCulture) + ", thickness " + l.ThicknessM.ToString("0", CultureInfo.InvariantCulture) + " m");
                Emit(p + "coverage", l.Coverage);
                Emit(p + "softness", l.Softness);
                Emit(p + "eq_thickness_m", l.EquivalentThicknessM);
                EmitFinite(p + "cpu_cover", l.CpuCover);
                EmitFinite(p + "cpu_type", l.CpuType);
                EmitFinite(p + "gpu_cover", l.GpuCover);
                EmitFinite(p + "gpu_type", l.GpuType);
                EmitFinite(p + "gpu_goal_cover", l.GpuGoalCover);
                EmitFinite(p + "gpu_goal_type", l.GpuGoalType);
                EmitFinite(p + "shader_cover", l.ShaderCover);
                EmitFinite(p + "shader_type", l.ShaderType);
                if (!float.IsNaN(l.GpuCover) && !float.IsNaN(l.CpuCover))
                {
                    Emit(p + "gpu_minus_cpu_cover", l.GpuCover - l.CpuCover);
                    if (Math.Abs(l.GpuCover - l.CpuCover) > 0.05f)
                        Warn("gpu_map_differs", p + "the pass's map reads " + l.GpuCover.ToString("0.000", CultureInfo.InvariantCulture) + " where the CPU readers see " + l.CpuCover.ToString("0.000", CultureInfo.InvariantCulture));
                }
                if (!float.IsNaN(l.ShaderCover) && !float.IsNaN(l.CpuCover))
                {
                    Emit(p + "shader_minus_cpu_cover", l.ShaderCover - l.CpuCover);
                    if (Math.Abs(l.ShaderCover - l.CpuCover) > 0.05f)
                        Warn("shader_map_differs", p + "the pass's sample reads " + l.ShaderCover.ToString("0.000", CultureInfo.InvariantCulture) + " where the CPU readers see " + l.CpuCover.ToString("0.000", CultureInfo.InvariantCulture));
                }
                for (int s = 0; s < l.ModelClosed.Length && s < ModelShifts.Length; s++) Emit(p + "model_closed_" + ModelShifts[s].Key, l.ModelClosed[s]);
            }
        }

        private void EmitFinite(string metric, double value)
        {
            if (!double.IsNaN(value) && !double.IsInfinity(value)) Emit(metric, value);
        }

        private T09Readout.Reading diagReading;
        // The picture of the last DiagTake (null where it failed) and the whole factor its file is scaled down by (2: half size; the grain start writes 1).
        private T09Image diagImage;
        private int diagPictureFactor = 2;

        // Reads the flight camera's frame, checks the pose at both ends, saves the picture (half size) and measures it. diagReading is null where any of it failed. baseline: the
        // off reading (null for the off frame itself); for the aid frame it is the gain-1 reading whose deck mask the aid frame is measured over.
        private IEnumerable<ProbeWait> DiagTake(View view, string tag, string gainTag, bool aid, T09Readout.Reading baseline, int aidMode = 4)
        {
            diagReading = null;
            diagImage = null;
            string what = view.Name + " " + tag + " " + gainTag;
            string name = T09Pictures.Name(StartId, view.Name + "_" + tag, gainTag, Game.ScreenWidth, Game.ScreenHeight, aid ? AidDirect : T09Pictures.Natural, diagPictureFactor > 1);
            T09Pose before = Game.ReadPose();
            T09Image image = null;
            string fault = null;
            bool started = false;
            try { started = Game.CaptureBegin(); }
            catch (Exception e) { fault = "the read threw " + e.GetType().Name + " " + e.Message; }
            if (started)
            {
                double giveUp = Elapsed + T09Pictures.ReadWaitS;
                while (CaptureBusy(ref fault) && Elapsed < giveUp) yield return ProbeWait.Frames(1);
                if (fault == null && CaptureBusy(ref fault)) fault = "the read did not finish in " + T09Pictures.ReadWaitS.ToString("0") + " s";
                if (fault == null)
                {
                    try { image = Game.CaptureTake(); }
                    catch (Exception e) { fault = "taking the read threw " + e.GetType().Name + " " + e.Message; }
                    if (fault == null && (image == null || image.Pixels == null || image.Pixels.Length != image.Width * image.Height || image.Pixels.Length == 0)) fault = "the read gave no picture";
                    else if (fault == null && (image.Raw == null || image.Raw.Length != image.Pixels.Length)) fault = "the read kept no raw values";
                }
            }
            else if (fault == null) fault = "no read could start (one is still under way)";
            if (fault != null)
            {
                Check("T09d.capture_" + name, false, fault);
                yield break;
            }
            T09Pose after = Game.ReadPose();
            EmitPose("T09.pic." + name + ".", before);
            Emit("T09.pic." + name + ".drift_after_m", after.DriftM);
            // A pose that is not the view's does not stop the start: the frame is read and measured, flagged pose_ok 0 with the pose it really had (the rows T09.pic.<name>.*).
            string poseProblem = PoseProblem(view, before) ?? PoseProblem(view, after);
            bool poseOk = poseProblem == null;
            diagFrames++;
            if (poseOk) diagFramesPoseOk++;
            else Warn("pose_mismatch", name + ": " + poseProblem + " (read starting " + before.VesselAsl.ToString("0") + " m, ending " + after.VesselAsl.ToString("0") + " m); read anyway, pose_ok 0");
            if (!picturesStopped)
            {
                try
                {
                    long saved = Game.SavePicture(name, image, diagPictureFactor);
                    picturesBytes += saved;
                    picturesSaved++;
                    pictureFaults = 0;
                    LogLine("Probe: picture " + name + ".png (" + image.Width + "x" + image.Height + " read, " + (diagPictureFactor > 1 ? "half" : "full") + " size written, " + saved + " bytes)");
                }
                catch (Exception e)
                {
                    Warn("picture_not_saved", name + ".png: " + e.GetType().Name + " " + e.Message);
                    NoteFault();
                }
            }
            double horizon = T09Readout.HorizonV(after.ElevationDeg, Game.CameraFovDeg, after.CameraAsl, Game.BodyRadius);
            T09Readout.Reading reading;
            try { reading = T09Readout.Measure(image.Raw, image.Width, image.Height, horizon, aid ? null : baseline, aid && baseline != null ? baseline.DeckMask : null); }
            catch (Exception e)
            {
                Check("T09d.readout_" + name, false, "the readout threw " + e.GetType().Name + " " + e.Message);
                yield break;
            }
            if (!aid)
            {
                try { reading.Line = T09Readout.MeasureHorizonLine(image.Raw, baseline != null ? baseline.Raw : null, image.Width, image.Height, horizon); }
                catch (Exception e) { Warn("horizon_line_fault", name + ": " + e.GetType().Name + " " + e.Message); }
                if (ProfileWanted(view, gainTag))
                {
                    try { reading.Profile = T09Readout.MeasureRowProfile(image.Raw, baseline != null ? baseline.Raw : null, image.Width, image.Height, horizon); }
                    catch (Exception e) { Warn("row_profile_fault", name + ": " + e.GetType().Name + " " + e.Message); }
                }
            }
            string p = "T09d." + view.Name + "." + tag + "." + gainTag + ".";
            Emit(p + "pose_ok", poseOk);
            Emit(p + "pose_alt_m", after.VesselAsl);
            // The ground view looks up at a sky layer; the other views (inside, deck, above, sky3... ) look down on the deck, so their tau is read over the sea or ground region.
            if (aid && aidMode == 1) EmitAidTau(p, reading, image.Raw, baseline, view.Name != "ground" && view.Name != "sky3");
            else if (aid) EmitAid(p, reading);
            else EmitReading(p, what, reading, baseline, IsOffVariant(gainTag), poseOk);
            diagReading = reading;
            diagImage = image;
        }

        private void EmitStat(string prefix, T09Readout.Stat s)
        {
            Emit(prefix + "n", s.Count);
            if (s.Count == 0) return;
            Emit(prefix + "p10", s.P10);
            Emit(prefix + "median", s.Median);
            Emit(prefix + "p90", s.P90);
            Emit(prefix + "r", s.R);
            Emit(prefix + "g", s.G);
            Emit(prefix + "b", s.B);
            Emit(prefix + "clipped", s.Clipped);
        }

        private void EmitRatio(string metric, double value)
        {
            if (!double.IsNaN(value) && !double.IsInfinity(value)) Emit(metric, value);
        }

        // The rows of one natural frame. The sky and the ground region must hold samples (an empty one means the horizon is off the picture or the pose is lost: a failed check).
        private void EmitReading(string p, string what, T09Readout.Reading r, T09Readout.Reading off, bool isOff, bool poseOk)
        {
            // An empty region of a frame whose pose is not the view's is no fault of the readout (the horizon may really be out of the picture): the check holds for good poses only.
            Check("T09d.region_sky_" + p.TrimEnd('.'), r.Sky.Count > 0 || !poseOk, what + ": " + r.Sky.Count + " sky samples (horizon at " + r.HorizonV.ToString("0.000") + " of the picture's height)");
            Check("T09d.region_ground_" + p.TrimEnd('.'), r.Ground.Count > 0 || !poseOk, what + ": " + r.Ground.Count + " sea or ground samples (horizon at " + r.HorizonV.ToString("0.000") + ")");
            Emit(p + "horizon_v", r.HorizonV);
            Emit(p + "peak", r.Peak);
            EmitStat(p + "sky_", r.Sky);
            EmitStat(p + "ground_", r.Ground);
            EmitHorizonLine(p, r.Line);
            EmitRowProfile(p, r.Profile);
            if (isOff)
            {
                EmitRatio(p + "ground_over_sky", T09Readout.Ratio(r.Ground.Median, r.Ground.Count, r.Sky.Median, r.Sky.Count));
                return;
            }
            Emit(p + "deck_fraction", r.DeckFraction);
            EmitStat(p + "deck_", r.Deck);
            if (off == null) return;
            // The share of each region that the deck changed, and the share that counts as closed (a proxy from the picture: the share of the region's samples whose colour changed by T09Readout.ClosedChange or more; no alpha is read).
            Emit(p + "sky_deck_share", r.SkyDeckShare);
            Emit(p + "ground_deck_share", r.GroundDeckShare);
            Emit(p + "sky_closed_share", r.SkyClosedShare);
            Emit(p + "ground_closed_share", r.GroundClosedShare);
            if (r.HorizonBandSamples > 0)
            {
                // Look B (E40): the band just above the horizon the sky region leaves out.
                Emit(p + "horizon_band_n", r.HorizonBandSamples);
                Emit(p + "horizon_band_closed", r.HorizonBandClosed);
                Emit(p + "horizon_band_change", r.HorizonBandChange);
            }
            EmitRatio(p + "sky_over_off_sky", T09Readout.Ratio(r.Sky.Median, r.Sky.Count, off.Sky.Median, off.Sky.Count));
            EmitRatio(p + "ground_over_off_ground", T09Readout.Ratio(r.Ground.Median, r.Ground.Count, off.Ground.Median, off.Ground.Count));
            EmitRatio(p + "deck_over_off_sky", T09Readout.Ratio(r.Deck.Median, r.Deck.Count, off.Sky.Median, off.Sky.Count));
            EmitRatio(p + "deck_over_off_ground", T09Readout.Ratio(r.Deck.Median, r.Deck.Count, off.Ground.Median, off.Ground.Count));
        }

        // The colour in the bands just above and just below the sea horizon's row (a few columns; T09Readout.MeasureHorizonLine): line_above_r/g/b and line_below_r/g/b are the frame's colours,
        // line_dabove_* and line_dbelow_* the change against the baseline frame (the cloud's own contribution), line_step the largest channel of their difference.
        private void EmitHorizonLine(string p, T09Readout.HorizonLine l)
        {
            if (l.Count == 0) return;
            Emit(p + "line_n", l.Count);
            Emit(p + "line_above_r", l.Above.r); Emit(p + "line_above_g", l.Above.g); Emit(p + "line_above_b", l.Above.b);
            Emit(p + "line_below_r", l.Below.r); Emit(p + "line_below_g", l.Below.g); Emit(p + "line_below_b", l.Below.b);
            Emit(p + "line_step_colour", l.StepColour);
            if (!l.HasDelta) return;
            Emit(p + "line_dabove_r", l.DeltaAbove.r); Emit(p + "line_dabove_g", l.DeltaAbove.g); Emit(p + "line_dabove_b", l.DeltaAbove.b);
            Emit(p + "line_dbelow_r", l.DeltaBelow.r); Emit(p + "line_dbelow_g", l.DeltaBelow.g); Emit(p + "line_dbelow_b", l.DeltaBelow.b);
            Emit(p + "line_step", l.Step);
        }

        // The frames whose vertical row profile at the horizon is written (ten bands of nine rows each): in a look start the horizon pose, the order frames (ord*) and the baselines.
        private bool ProfileWanted(View view, string frame)
        {
            return LookMode != null && (view.Name == "horizon" || frame == "off" || frame == "off2" || frame.StartsWith("ord", StringComparison.Ordinal) || frame.StartsWith("hh", StringComparison.Ordinal));
        }

        // The rows prof_b<k> (band k below the horizon row, 0 nearest) and prof_a<k> (above): n samples, changed (against the baseline by T09Readout.ClosedChange), rejected (mound rim and
        // ridge samples dropped), and where a colour was read the frame's median colour r g b and the baseline's br bg bb over the same samples.
        private void EmitRowProfile(string p, T09Readout.RowProfile profile)
        {
            if (profile.Below == null) return;
            for (int side = 0; side < 2; side++)
            {
                T09Readout.ProfileBand[] bands = side == 0 ? profile.Below : profile.Above;
                for (int k = 0; k < bands.Length; k++)
                {
                    T09Readout.ProfileBand b = bands[k];
                    if (b.Count == 0) continue;
                    string q = p + "prof_" + (side == 0 ? "b" : "a") + k.ToString(CultureInfo.InvariantCulture) + "_";
                    Emit(q + "n", b.Count);
                    Emit(q + "changed", b.Changed);
                    Emit(q + "rejected", b.Rejected);
                    if (!b.HasColour) continue;
                    Emit(q + "r", b.Frame.r); Emit(q + "g", b.Frame.g); Emit(q + "b", b.Frame.b);
                    if (b.Baseline.a > 0f) { Emit(q + "br", b.Baseline.r); Emit(q + "bg", b.Baseline.g); Emit(q + "bb", b.Baseline.b); }
                }
            }
        }

        // The aid frame: the channels of the pass's direct light (r), light taps (g) and ambient (b) over the deck of the natural frame.
        // The rows of an aid-1 frame (the optical depth picture): the optical depth over the sky samples decoded against the baseline frame (T09Readout.MeasureAidTau), to be read
        // beside the colour-change closed share of the natural frame of the same set (K-T2d start 51 had no tau). Needs the baseline's raw picture; named otherwise.
        private void EmitAidTau(string p, T09Readout.Reading r, Color[] raw, T09Readout.Reading baseline, bool below)
        {
            if (baseline == null || baseline.Raw == null || raw == null || baseline.Raw.Length != raw.Length)
            {
                Warn("aid_tau_no_baseline", p.TrimEnd('.') + ": the baseline's raw picture is not at hand, the optical depth is not decoded");
                return;
            }
            T09Readout.AidTauReading t;
            try { t = T09Readout.MeasureAidTau(raw, baseline.Raw, r.Width, r.Height, r.HorizonV, 4, below); }
            catch (Exception e)
            {
                Warn("aid_tau_fault", p.TrimEnd('.') + ": " + e.GetType().Name + " " + e.Message);
                return;
            }
            Emit(p + "aidtau_n", t.Count);
            if (t.Count == 0) return;
            Emit(p + "aidtau_p10", t.P10);
            Emit(p + "aidtau_median", t.Median);
            Emit(p + "aidtau_p90", t.P90);
            Emit(p + "aidtau_closed_share", t.ShareClosed);
            Emit(p + "aidtau_deep_share", t.ShareDeep);
            Emit(p + "aidtau_saturated_share", t.ShareSaturated);
            Emit(p + "aidtau_top_n", t.TopCount);
            if (t.TopCount > 0) Emit(p + "aidtau_top_median", t.TopMedian);
        }

        private void EmitAid(string p, T09Readout.Reading r)
        {
            Emit(p + "deck_n", r.Deck.Count);
            if (r.Deck.Count == 0) return;
            Emit(p + "direct_median", r.Deck.R);
            Emit(p + "taps_median", r.Deck.G);
            Emit(p + "ambient_median", r.Deck.B);
            Emit(p + "direct_p90", r.Deck.P90);
            // The direct light's own spread over the deck (the red channel of the aid picture): a patch of the deck that lies in the shadow of another layer reads far under the lit part.
            Emit(p + "direct_r_p10", r.Deck.RP10);
            Emit(p + "direct_r_p90", r.Deck.RP90);
        }

        // The light and the target, at the first drawn frame and every 30 s of the scenario.
        private void LogEnvironment(bool force)
        {
            if (!force && Elapsed < nextEnvAt) return;
            nextEnvAt = Elapsed + EnvEveryS;
            IList<KeyValuePair<string, string>> lines;
            try { lines = Game.EnvironmentLines(); }
            catch (Exception e)
            {
                Warn("environment_failed", e.GetType().Name + " " + e.Message);
                return;
            }
            foreach (KeyValuePair<string, string> line in lines)
            {
                LogLine("T09d env " + line.Key + ": " + line.Value);
                if (!envEmitted) EmitText("T09d.env." + line.Key, line.Value);
            }
            envEmitted = true;
        }
    }
}
