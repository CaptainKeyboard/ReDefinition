using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Series K-T2, the start soft59 (E40 soft density, the one game start of the second lead; the review is .local/work/e40-soft-start59-review.md): the soft density as the product
    // draws it against the density before it (layers.softDensity 0), the owner's two favourite settings, and up to four cost candidates, in two scenes of ONE start:
    //   scene "fair"   the fair-weather scene of the owner's complaint: the K-T2d low cover (ChooseCoverTimes), views ground and above
    //   scene "clock"  the K-T2 clock (ChooseUt, the one of the TVC start tvc1707: 2.59 / 4.77 / 5.30 / 2.58 ms busy-delta), views ground, above, inside, sky3
    // In each view: the baseline (the pass running, no layer drawn), then for every variant of SoftVariants three frames read one after the other (a, b, c; written at full size) and,
    // where the table says so, a cost window of SoftWindowS seconds between "T09 view begin / end: <view> <tag> <WxH>" lines (the tag is fw_<variant> in the scene fair and k_<variant>
    // in the scene clock, so that tools/weather_metrics/kt2.py reads both scenes from one log). The collector tools/weather_metrics/e40sf.py makes the tables and the sheets.
    //
    // THE VARIANT TABLE (SoftVariants) is the one place to change what is compared. A variant is a name and key = value pairs: "s:<setting>=<number>" sets a setting
    // (cloudSoftness, cloudSamples: Low 0 / Medium 1 / High 2), "t:<tuning key>=<number>" forces a tuning key. Every variant starts from the defaults (SoftBase), so the pairs
    // are the whole difference. FairCost / ClockCost name the views (g ground, i inside, a above, s sky3) that get a cost window; every variant gets its three frames in every
    // view it runs in. A slot with Pairs null is empty and not run: give it pairs, and cost views, and it runs; the plan line in the log and SoftEstimateS say what the table costs.
    //
    // Rows (series K-T2, metric T09s.<scene>.<view>.<variant>.<row>):
    //   tstd, hfx, mask_n, mask_hf, mask_hf_off, cloud_share   the grain of the pixels the clouds changed against the baseline (T09ProbeGrain.MeasureGrain; the same measure as the grain start)
    //   box_tstd, box_blob, box_lap, box_luma                  the same over the fixed boxes of the view (T09ProbeGt.MeasureGtGrain; the measure of start 56)
    //   grid_points, softness, soft_density, effective_ok      what the pass really ran with: the grid points of the march, the width of the soft rim, the soft density's switch
    //   cost_frames, cost_fps, cost_frames_drawn               the cost window; the GPU time is read from the profiler's reports by kt2.py
    //   T09s.sun.<scene>.*                                     the sun colour of the clouds (SoftSunColour)
    internal sealed partial class T09Probe
    {
        internal sealed class SoftVariant
        {
            internal string Name;
            // "s:<setting>=<number>" or "t:<tuning key>=<number>"; null: an empty slot that is not run.
            internal string[] Pairs;
            // The views with a cost window: g ground, i inside, a above, s sky3.
            internal string FairCost = "", ClockCost = "";
            internal bool Active { get { return Pairs != null; } }

            internal bool Costs(bool fair, char view)
            {
                return (fair ? FairCost : ClockCost).IndexOf(view) >= 0;
            }
        }

        private static SoftVariant Soft(string name, string fairCost, string clockCost, params string[] pairs)
        {
            return new SoftVariant { Name = name, FairCost = fairCost, ClockCost = clockCost, Pairs = pairs };
        }

        private static SoftVariant SoftSlot(string name)
        {
            return new SoftVariant { Name = name, Pairs = null };
        }

        // soft: the defaults of origin/main (the soft density on, cloudSoftness 0.5, cloudSamples High = 48 grid points: the Medium tier does not set cloudSamples, so the "Medium: 32" of the
        // brief is not what the game runs; the owner's favourite "soft48" IS this variant and has no row of its own). hard: layers.softDensity 0, the density before the soft water content.
        // soft100s24: cloudSoftness 1.0 with cloudSamples Low (24), the owner's other favourite. The slots are for the cost candidates of package A
        // (.local/plans/atmospheres/E40-ground-cost-result.md); an example that costs one window: Soft("soft32", "", "g", "s:" + AtmospheresSettings.Keys.CloudSamples + "=1") (cloudSamples Medium, 32 points).
        internal static readonly SoftVariant[] SoftVariants =
        {
            Soft("soft", "ga", "gias"),
            Soft("hard", "ga", "ga", "t:" + CloudSoftDensity.TuneOn + "=0"),
            Soft("soft100s24", "ga", "gias", "s:" + AtmospheresSettings.Keys.CloudSoftness + "=1", "s:" + AtmospheresSettings.Keys.CloudSamples + "=0"),
            // The owner's look decision between the two step offsets (E40 step (c)): the white hash the default drew before, frames only, no cost window.
            Soft("softhash", "", "", "t:" + OwnCloudOptions.TuneDitherSequence + "=1"),
            SoftSlot("slot2"), SoftSlot("slot3"), SoftSlot("slot4")
        };

        // The scenario's own limit (the runner's is 20 minutes for the whole start: the game needs about 90 s to come up). A guard only: the scenario ends when its last view is done.
        internal const double SoftMaxSeconds = 18 * 60;
        // The window of one cost reading. The profiler reports every 10 s and the first report after a begin line is dropped (kt2.py): a window of 22 s holds at least two reports, so at least
        // one clean one (the period may run a few hundredths over 10 s, which a window of 20 s would not survive).
        internal const double SoftWindowS = 22.0;
        // After a variant's keys changed: frames for the history of the half-size march, and seconds for the maps' round (the layers' numbers follow CloudSoftDensity.Stamp).
        private const int SoftSettleFrames = DiagSettleFrames + 60;
        private const double SoftSettleS = 2.0;
        // The estimate of the time (seconds) one step takes, for the plan and for the guard that leaves out a view the time no longer holds.
        internal const double SoftPlaceS = 25.0, SoftSwitchS = 4.5, SoftFrameReadS = 1.5, SoftPickClockS = 60.0, SoftPickCoverS = 90.0, SoftReserveS = 20.0;

        private bool SoftStart
        {
            get { return StartId != null && StartId.StartsWith("soft", StringComparison.Ordinal) && !IsDiag && !IsK3; }
        }

        // False only in a test whose fake world does not run the module's per-frame numbers.
        internal bool ChecksEffectiveNumbers = true;

        // --- The table ---------------------------------------------------------------------------------------------------------------------

        internal static bool SoftSplit(string pair, out bool setting, out string key, out float value)
        {
            setting = false;
            key = null;
            value = 0f;
            if (pair == null || pair.Length < 5 || pair[1] != ':' || (pair[0] != 's' && pair[0] != 't')) return false;
            int eq = pair.IndexOf('=');
            if (eq < 3 || eq == pair.Length - 1) return false;
            setting = pair[0] == 's';
            key = pair.Substring(2, eq - 2);
            return float.TryParse(pair.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // What a variant makes of the numbers the pass reads: the width of the soft rim, the cloudSamples choice, the soft density's switch, the grid points of the march.
        internal struct SoftExpectation
        {
            internal double Softness;
            internal int Samples, GridPoints;
            internal bool SoftDensity;
        }

        internal static SoftExpectation SoftExpect(SoftVariant v)
        {
            double softness = AtmospheresSettings.Definition(AtmospheresSettings.Keys.CloudSoftness).Default;
            double level = AtmospheresSettings.Definition(AtmospheresSettings.Keys.CloudSamples).Default;
            bool soft = true;
            float gridFine = OwnCloudOptions.GridFineDefault;
            if (v.Pairs != null)
            {
                foreach (string pair in v.Pairs)
                {
                    bool setting;
                    string key;
                    float value;
                    if (!SoftSplit(pair, out setting, out key, out value)) continue;
                    if (setting && key == AtmospheresSettings.Keys.CloudSoftness) softness = value;
                    else if (setting && key == AtmospheresSettings.Keys.CloudSamples) level = value;
                    else if (!setting && key == CloudSoftDensity.TuneOn) soft = value != 0f;
                    else if (!setting && key == OwnCloudOptions.TuneGridFine) gridFine = value;
                }
            }
            softness = Math.Max(CloudSoftDensity.MinSoftness, Math.Min(CloudSoftDensity.MaxSoftness, softness));
            int samples = OwnCloudOptions.SamplesOf((CloudDetailLevel)Math.Max(0, Math.Min(2, (int)level)));
            int baseMax, sun;
            float min;
            LayerSwitches.StepQuality(Math.Max(0, Math.Min(2, (int)AtmospheresSettings.Number(AtmospheresSettings.Keys.LayerStepQuality))), out baseMax, out sun, out min);
            int points = CloudSoftDensity.ScaleSamples(baseMax, samples);
            points = Math.Max(4, Math.Min(256, Mathf.RoundToInt(points * Mathf.Clamp(gridFine, 0.25f, 8f))));
            return new SoftExpectation { Softness = softness, Samples = samples, SoftDensity = soft, GridPoints = points };
        }

        // The seconds one view takes with the variants of the table: the placement, the baseline (settle, one frame, a window), then per variant the switch, three frames and its window.
        internal static double SoftEstimateViewS(bool fair, char view, IList<SoftVariant> table)
        {
            double s = SoftPlaceS + SoftFrameReadS * 2.0 + SoftWindowS;
            foreach (SoftVariant v in table)
            {
                if (!v.Active) continue;
                s += SoftSwitchS + 3.0 * SoftFrameReadS + (v.Costs(fair, view) ? SoftWindowS : 0.0);
            }
            return s;
        }

        internal static readonly char[] SoftFairViews = { 'g', 'a' };
        internal static readonly char[] SoftClockViews = { 'g', 'a', 'i', 's' };

        // The whole scenario: both clock steps and every view of both scenes.
        internal static double SoftEstimateS(IList<SoftVariant> table)
        {
            double s = SoftPickCoverS + SoftPickClockS;
            foreach (char c in SoftFairViews) s += SoftEstimateViewS(true, c, table);
            foreach (char c in SoftClockViews) s += SoftEstimateViewS(false, c, table);
            return s;
        }

        internal static char SoftLetter(View view)
        {
            switch (view.Name)
            {
                case "ground": return 'g';
                case "inside": return 'i';
                case "above": return 'a';
                case "sky3": return 's';
                default: return '?';
            }
        }

        // --- The scenario ------------------------------------------------------------------------------------------------------------------

        private double softUtAtStart;
        private bool softSunLoggedFair, softSunLoggedClock;
        private int softFramesRead, softWindows, softSections, softSectionsSkipped;
        private Color[] softOffRaw;

        // The state every variant starts from: the own pass with the product's defaults, the soft rim and the grid points at their defaults.
        private void SoftBase()
        {
            UseOwn();
            SetSetting(AtmospheresSettings.Keys.CloudSoftness, AtmospheresSettings.Definition(AtmospheresSettings.Keys.CloudSoftness).Default);
            SetSetting(AtmospheresSettings.Keys.CloudSamples, AtmospheresSettings.Definition(AtmospheresSettings.Keys.CloudSamples).Default);
        }

        private void SoftApply(SoftVariant v)
        {
            SoftBase();
            foreach (string pair in v.Pairs)
            {
                bool setting;
                string key;
                float value;
                if (!SoftSplit(pair, out setting, out key, out value))
                {
                    Check("T09s.pair_" + v.Name, false, "the pair '" + pair + "' of the variant " + v.Name + " is not 's:<setting>=<number>' or 't:<key>=<number>'");
                    continue;
                }
                if (setting)
                {
                    if (!SetSetting(key, value)) Check("T09s.setting_" + v.Name, false, "the setting " + key + " of the variant " + v.Name + " does not exist or is no number");
                }
                else ForceTuning(key, value);
            }
        }

        private IEnumerable<ProbeWait> SoftSettle()
        {
            yield return ProbeWait.Frames(SoftSettleFrames);
            yield return ProbeWait.Seconds(SoftSettleS);
        }

        // The numbers the pass ran with after the variant's keys: from the module's own frame (written in the pre-cull of every frame), against what the table says.
        private void SoftVerify(string head, SoftVariant v)
        {
            OwnCloudModule own = OwnCloudModule.Current;
            if (own == null) return;
            SoftExpectation e = SoftExpect(v);
            int points = own.Frame.MaxSteps;
            double softness = CloudSoftDensity.Softness;
            bool on = CloudSoftDensity.Enabled;
            Emit(head + "grid_points", points);
            Emit(head + "softness", softness);
            Emit(head + "soft_density", on);
            bool ok = points == e.GridPoints && Math.Abs(softness - e.Softness) < 1e-6 && on == e.SoftDensity;
            Emit(head + "effective_ok", ok);
            AtmospheresLog.Say("T09 soft variant " + v.Name + " (" + head + "): grid points " + points + " (expected " + e.GridPoints + "), softness " + softness.ToString("0.###", CultureInfo.InvariantCulture)
                               + " (expected " + e.Softness.ToString("0.###", CultureInfo.InvariantCulture) + "), soft density " + on + " (expected " + e.SoftDensity + ")");
            if (ChecksEffectiveNumbers)
                Check("T09s.effective_" + head.Replace('.', '_') + v.Name, ok, "grid points " + points + " against " + e.GridPoints + ", softness " + softness.ToString("0.###", CultureInfo.InvariantCulture) + " against "
                      + e.Softness.ToString("0.###", CultureInfo.InvariantCulture) + ", soft density " + on + " against " + e.SoftDensity);
        }

        private IEnumerator<ProbeWait> SoftScenario()
        {
            foreach (ProbeWait w in Prelude()) yield return w;
            if (!preludeOk) yield break;
            softUtAtStart = Game.UniversalTime;
            double lat = Game.VesselLatitude, lon = Game.VesselLongitude;
            LogSoftPlan();
            CheckPicturesFolder();

            // Scene fair: the K-T2d low cover. A start whose field has no such cover goes on with the clock scene.
            foreach (ProbeWait w in ChooseCoverTimes(Game.BodyIndex, lat, lon)) yield return w;
            if (!coverOk) Check("T09s.fair_clock", false, coverFailure);
            else
            {
                Emit("T09s.fair.ut", lowUt);
                Emit("T09s.fair.cover", lowCover);
                Emit("T09s.fair.sun_elev_deg", lowSun);
                foreach (ProbeWait w in SoftSceneBegin(true, lowUt)) yield return w;
                foreach (char c in SoftFairViews)
                    foreach (ProbeWait w in SoftSection(true, ViewOf(c), lat, lon)) yield return w;
            }

            // Scene clock: the clock of the K-T2 starts, from the universal time this start began at (the candidate hours count from it, as in the starts that TVC was measured in).
            ForceUniversalTime(softUtAtStart);
            yield return ProbeWait.Frames(6);
            foreach (ProbeWait w in ChooseUt(Game.BodyIndex, lat, lon)) yield return w;
            if (!utOk)
            {
                Fail(utFailure);
                yield break;
            }
            Emit("T09.ut", utChosen);
            Emit("T09.cover", coverChosen);
            Emit("T09.sun_elev_deg", sunChosen);
            Check("the sky is covered at the chosen time", coverChosen >= MinCover, coverChosen.ToString("0.00"));
            Check("the sun stands in the band at the chosen time", sunChosen >= SunMinDeg && sunChosen <= SunMaxDeg, sunChosen.ToString("0.0") + " degrees");
            foreach (ProbeWait w in SoftSceneBegin(false, utChosen)) yield return w;
            foreach (char c in SoftClockViews)
                foreach (ProbeWait w in SoftSection(false, ViewOf(c), lat, lon)) yield return w;

            Game.StopHold();
            Emit("T09s.frames_read", softFramesRead);
            Emit("T09s.cost_windows", softWindows);
            Emit("T09s.sections", softSections);
            Emit("T09s.sections_skipped", softSectionsSkipped);
            Emit("T09.pictures.saved", picturesSaved);
            Emit("T09.pictures.bytes", picturesBytes);
            Emit("T09.pictures.warnings", pictureWarnings);
            EmitVariantTotals("plain");
        }

        private static View ViewOf(char letter)
        {
            foreach (View v in Views)
                if (SoftLetter(v) == letter) return v;
            return Views[0];
        }

        private void LogSoftPlan()
        {
            var sb = new System.Text.StringBuilder();
            foreach (SoftVariant v in SoftVariants)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(v.Name);
                if (!v.Active) { sb.Append(" (empty slot)"); continue; }
                SoftExpectation e = SoftExpect(v);
                sb.Append(" [softness ").Append(e.Softness.ToString("0.###", CultureInfo.InvariantCulture)).Append(", ").Append(e.GridPoints).Append(" grid points, soft density ")
                  .Append(e.SoftDensity ? "on" : "off").Append(", cost fair '").Append(v.FairCost).Append("' clock '").Append(v.ClockCost).Append("']");
            }
            double estimate = SoftEstimateS(SoftVariants);
            AtmospheresLog.Say("T09 soft plan: " + sb + "; estimated " + estimate.ToString("0") + " s of " + SoftMaxSeconds.ToString("0") + " s");
            EmitText("T09s.plan", sb.ToString());
            Emit("T09s.plan_estimate_s", estimate);
        }

        // The clock set, the maps waited for, the own pass drawing steady: the start of a scene. Nothing is measured before the maps have followed the clock jump (the slew limit
        // 0.08 a second) and the pass has drawn a steady stretch.
        private IEnumerable<ProbeWait> SoftSceneBegin(bool fair, double ut)
        {
            string scene = fair ? "fair" : "clock";
            ForceUniversalTime(ut);
            yield return ProbeWait.Frames(6);
            AtmospheresLog.Say("T09 soft scene " + scene + ": ut " + ut.ToString("0") + ", sun " + Game.SunElevationDeg().ToString("0.0") + " degrees; waiting " + MapSettleS.ToString("0") + " s for the maps");
            yield return ProbeWait.Seconds(MapSettleS);
            if (CurrentVariant == null) BeginVariant("plain");
            CheckVariantGates("plain");
            SoftBase();
            OwnCloudModule m = OwnCloudModule.Current;
            int drawnFrom = m != null ? m.Stats.FramesDrawn : 0;
            yield return ProbeWait.Until(() =>
            {
                OwnCloudModule o = OwnCloudModule.Current;
                return o != null && o.Stats.Mode != "off" && o.Stats.FramesDrawn - drawnFrom > SteadyFrames;
            }, 150, "the own pass to draw a steady stretch (scene " + scene + ")");
            LogEnvironment(true);
        }

        // One view: placed, the baseline, then every variant of the table.
        private IEnumerable<ProbeWait> SoftSection(bool fair, View view, double lat, double lon)
        {
            char letter = SoftLetter(view);
            double estimate = SoftEstimateViewS(fair, letter, SoftVariants);
            string scene = fair ? "fair" : "clock";
            string head = "T09s." + scene + "." + view.Name + ".";
            if (Elapsed + estimate > MaxSeconds - SoftReserveS)
            {
                softSectionsSkipped++;
                Check("T09s.section_" + scene + "_" + view.Name, false, "the scenario is at " + Elapsed.ToString("0") + " s of " + MaxSeconds.ToString("0") + " s and the view needs about "
                      + estimate.ToString("0") + " s: left out");
                yield break;
            }
            softSections++;
            foreach (ProbeWait w in PlaceChecked(lat, lon, view)) yield return w;
            Emit(head + "placed_ok", placeOk);
            if (!placeOk) yield break;
            foreach (ProbeWait w in StableGate(view, "soft")) yield return w;
            if (fair ? !softSunLoggedFair : !softSunLoggedClock)
            {
                if (fair) softSunLoggedFair = true; else softSunLoggedClock = true;
                SoftSunColour(scene);
            }
            string pictureView = view.Name + (fair ? "_fair" : "");
            string tagPrefix = fair ? "fw_" : "k_";

            // The baseline: the pass runs, no layer is drawn.
            SoftBase();
            Game.SetLayerMask(NoLayersMask);
            yield return ProbeWait.Frames(DiagSettleFrames);
            foreach (ProbeWait w in ReadRawFrame(view.Name + " " + scene + " baseline")) yield return w;
            softOffRaw = noiseImage != null ? noiseImage.Raw : null;
            if (noiseImage != null) SoftSave(pictureView, "off_a", noiseImage);
            foreach (ProbeWait w in SoftCostWindow(view, head + "off.", tagPrefix + "off")) yield return w;
            Game.SetLayerMask(0);

            foreach (SoftVariant v in SoftVariants)
            {
                if (!v.Active) continue;
                SoftApply(v);
                foreach (ProbeWait w in SoftSettle()) yield return w;
                SoftVerify(head + v.Name + ".", v);
                foreach (ProbeWait w in SoftGrainRead(view, head + v.Name + ".", pictureView, v.Name)) yield return w;
                if (v.Costs(fair, letter))
                    foreach (ProbeWait w in SoftCostWindow(view, head + v.Name + ".", tagPrefix + v.Name)) yield return w;
            }
            SoftBase();
            Game.SetLayerMask(0);
            yield return ProbeWait.Frames(DiagSettleFrames);
        }

        private void SoftSave(string pictureView, string variant, T09Image image)
        {
            if (picturesStopped || image == null) return;
            string name = T09Pictures.Name(StartId, pictureView, variant, Game.ScreenWidth, Game.ScreenHeight, T09Pictures.Natural, false);
            try
            {
                picturesBytes += Game.SavePicture(name, image, 1);
                picturesSaved++;
            }
            catch (Exception e)
            {
                Warn("picture_not_saved", name + ".png: " + e.GetType().Name + " " + e.Message);
            }
        }

        // Three frames of the held pose read one after the other, written at full size, measured two ways (see the header).
        private IEnumerable<ProbeWait> SoftGrainRead(View view, string head, string pictureView, string variant)
        {
            if (picturesStopped) yield break;
            string what = view.Name + " " + variant + " grain";
            for (int i = 0; i < 12 && Game.HoldReached && !Game.HoldStable; i++) yield return ProbeWait.Frames(5);
            T09Pose before = Game.ReadPose();
            var raws = new List<Color[]>();
            var images = new List<T09Image>();
            int firstFrame = 0, lastFrame = 0;
            foreach (string letter in new[] { "a", "b", "c" })
            {
                foreach (ProbeWait w in ReadRawFrame(what + " " + letter)) yield return w;
                if (noiseImage == null) yield break;
                if (raws.Count == 0) firstFrame = noiseBeginFrame;
                lastFrame = noiseBeginFrame;
                raws.Add(noiseImage.Raw);
                images.Add(noiseImage);
                softFramesRead++;
            }
            T09Pose after = Game.ReadPose();
            string poseProblem = PoseProblem(view, before) ?? PoseProblem(view, after);
            Emit(head + "pose_ok", poseProblem == null);
            string[] letters = { "a", "b", "c" };
            for (int k = 0; k < images.Count; k++) SoftSave(pictureView, variant + "_" + letters[k], images[k]);
            if (poseProblem != null)
            {
                Warn("grain_pose", what + ": " + poseProblem + "; no grain rows");
                yield break;
            }
            Emit(head + "frames_apart", lastFrame - firstFrame);
            int w0 = images[0].Width, h0 = images[0].Height;
            float y0, y1;
            GtBox(view.Name, out y0, out y1);
            GtGrain g;
            if (MeasureGtGrain(raws, w0, h0, y0, y1, out g))
            {
                Emit(head + "box_tstd", g.Tstd);
                Emit(head + "box_blob", g.Blob);
                Emit(head + "box_lap", g.Lap);
                Emit(head + "box_luma", g.Luma);
            }
            else Warn("grain_measure", what + ": the three frames could not be measured over the box");
            int n;
            double tstd, hf, hfOff;
            if (softOffRaw != null && softOffRaw.Length == w0 * h0 && MeasureGrain(softOffRaw, raws, w0, h0, out n, out tstd, out hf, out hfOff))
            {
                Emit(head + "mask_n", n);
                Emit(head + "cloud_share", (double)n / (w0 * h0));
                Emit(head + "tstd", tstd);
                Emit(head + "mask_hf", hf);
                Emit(head + "mask_hf_off", hfOff);
                Emit(head + "hfx", hf - hfOff);
                AtmospheresLog.Say("T09 soft grain " + view.Name + " " + variant + ": tstd " + tstd.ToString("0.00000", CultureInfo.InvariantCulture) + ", hfx " + (hf - hfOff).ToString("0.00000", CultureInfo.InvariantCulture)
                                   + " over " + n + " px (" + (100.0 * n / (w0 * h0)).ToString("0.0", CultureInfo.InvariantCulture) + " percent), box tstd " + g.Tstd.ToString("0.00000", CultureInfo.InvariantCulture) + ", "
                                   + (lastFrame - firstFrame) + " frame(s) from first to last");
            }
            else Warn("grain_mask", what + ": the clouds changed no pixel against the baseline frame (or it is missing); no mask rows");
        }

        // One cost window between "T09 view begin / end" lines; the mask and the keys are set by the caller.
        private IEnumerable<ProbeWait> SoftCostWindow(View view, string head, string tag)
        {
            OwnCloudModule own = OwnCloudModule.Current;
            int drawnBefore = own != null ? own.Stats.FramesDrawn : 0;
            int framesBefore = Game.FrameCount;
            string size = Game.ScreenWidth + "x" + Game.ScreenHeight;
            AtmospheresLog.Say("T09 view begin: " + view.Name + " " + tag + " " + size);
            double dwelled = 0.0;
            viewPoseOk = true;
            while (dwelled < SoftWindowS - 1e-9)
            {
                double slice = Math.Min(HeldSliceS, SoftWindowS - dwelled);
                yield return ProbeWait.Seconds(slice);
                dwelled += slice;
                CheckHeld(view, tag, dwelled);
            }
            AtmospheresLog.Say("T09 view end: " + view.Name + " " + tag + " " + size);
            softWindows++;
            int frames = Math.Max(1, Game.FrameCount - framesBefore);
            Emit(head + "cost_frames", frames);
            Emit(head + "cost_fps", frames / SoftWindowS);
            Emit(head + "cost_pose_ok", viewPoseOk);
            if (own != null)
            {
                Emit(head + "cost_frames_drawn", own.Stats.FramesDrawn - drawnBefore);
                EmitText(head + "cost_mode", own.Stats.Mode ?? "");
            }
        }

        // --- The sun colour of the clouds (E40 sun colour, B2; 6b7ba173) -------------------------------------------------------------------------

        private static string SoftColour(Color c)
        {
            return "(" + c.r.ToString("0.000", CultureInfo.InvariantCulture) + " " + c.g.ToString("0.000", CultureInfo.InvariantCulture) + " " + c.b.ToString("0.000", CultureInfo.InvariantCulture) + ")";
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool ReadSunLight(out Color colour)
        {
            colour = Color.white;
            Light sun = Sun.Instance != null ? Sun.Instance.sunLight : null;
            if (sun == null) return false;
            colour = sun.color * sun.intensity;
            return true;
        }

        private static double BlueOverRed(Color c)
        {
            return c.r > 1e-6f ? c.b / c.r : double.NaN;
        }

        // Once per scene, after the first view is placed (the viewer 200 m up): what the pass took as the sun's colour and whether the read sees Scatterer's RESTORED colour.
        //   scene    the colour read in the pass's pre-cull (OwnCloudModule.FillFrame), while Scatterer's modulation is applied (the air above the viewer is in it)
        //   tick     the colour the module read in its tick, before the modulation (the cap of the division: the pass's sun never exceeds it)
        //   probe    the colour the probe reads now, in its own tick, outside the render (what the K-T2d environment line "sunLight" reads)
        //   pass     the colour the pass took: the scene colour with the viewer's air divided out, capped at the tick colour
        // restored_seen: the tick colour and the probe's read agree (both outside the render) and differ from the scene colour (blue over red higher by at least 0.02): the modulation is applied
        // in the render and restored outside it, so the cap is the unmodulated sun. Without Scatterer the three are the same colour and the row is false (nothing to restore).
        private void SoftSunColour(string scene)
        {
            string head = "T09s.sun." + scene + ".";
            OwnCloudModule own = OwnCloudModule.Current;
            if (own == null || own.SunRead.FrameNumber == 0)
            {
                Warn("sun_colour", "scene " + scene + ": the pass has not filled a frame; no sun colour line");
                return;
            }
            OwnCloudModule.SunColourRead s = own.SunRead;
            Color probe = Color.white;
            bool haveProbe = false;
            // The engine's sun light is touched in a method of its own: a test host cannot run the engine's calls, and the exception of one is thrown where the method is entered.
            try { haveProbe = ReadSunLight(out probe); }
            catch (Exception) { /* no sun light object: the probe's own read is left out */ }
            double brScene = BlueOverRed(s.Scene), brTick = BlueOverRed(s.Unmodulated), brProbe = haveProbe ? BlueOverRed(probe) : double.NaN, brPass = BlueOverRed(s.Pass);
            bool tickMatchesProbe = !haveProbe || Math.Abs(brTick - brProbe) < 0.02;
            bool restoredSeen = s.ScattererLoaded && s.HaveUnmodulated && tickMatchesProbe && brTick - brScene >= 0.02;
            Emit(head + "scatterer_loaded", s.ScattererLoaded);
            Emit(head + "air_from_scatterer", s.AirFromScatterer);
            Emit(head + "air_divided", s.Divided);
            Emit(head + "sun_air_height_m", s.SunAirHeightM);
            Emit(head + "viewer_altitude_m", s.ViewerAltitudeM);
            Emit(head + "sun_elevation_deg", Math.Asin(Math.Max(-1.0, Math.Min(1.0, s.SinElevation))) * 180.0 / Math.PI);
            Emit(head + "scene_blue_over_red", brScene);
            Emit(head + "tick_blue_over_red", brTick);
            if (haveProbe) Emit(head + "probe_blue_over_red", brProbe);
            Emit(head + "pass_blue_over_red", brPass);
            Emit(head + "pass_over_tick_blue", s.Unmodulated.b > 1e-6f ? s.Pass.b / s.Unmodulated.b : double.NaN);
            Emit(head + "scene_over_tick_blue", s.Unmodulated.b > 1e-6f ? s.Scene.b / s.Unmodulated.b : double.NaN);
            Emit(head + "restored_seen", restoredSeen);
            AtmospheresLog.Say("T09 sun colour (" + scene + ", frame " + s.FrameNumber + "): Scatterer loaded " + s.ScattererLoaded + ", air from Scatterer " + s.AirFromScatterer + ", viewer's air divided out " + s.Divided
                               + ", SunAirHeightM " + s.SunAirHeightM.ToString("0", CultureInfo.InvariantCulture) + ", viewer " + s.ViewerAltitudeM.ToString("0", CultureInfo.InvariantCulture) + " m, sun "
                               + (Math.Asin(Math.Max(-1.0, Math.Min(1.0, s.SinElevation))) * 180.0 / Math.PI).ToString("0.0", CultureInfo.InvariantCulture) + " degrees; read in the render " + SoftColour(s.Scene)
                               + " blue/red " + brScene.ToString("0.000", CultureInfo.InvariantCulture) + "; read in the module's tick " + (s.HaveUnmodulated ? SoftColour(s.Unmodulated) : "none") + " blue/red "
                               + brTick.ToString("0.000", CultureInfo.InvariantCulture) + "; read by the probe outside the render " + (haveProbe ? SoftColour(probe) + " blue/red " + brProbe.ToString("0.000", CultureInfo.InvariantCulture) : "none")
                               + "; the pass took " + SoftColour(s.Pass) + " blue/red " + brPass.ToString("0.000", CultureInfo.InvariantCulture) + "; the read sees the restored colour: " + restoredSeen);
        }
    }
}
