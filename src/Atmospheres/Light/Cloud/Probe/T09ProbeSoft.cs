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
    // view it runs in (all views of both scenes unless In(...) restricts it). A slot with Pairs null is empty and not run: give it pairs, and cost views, and it runs; the plan line in the log and SoftEstimateS say what the table costs.
    //
    // Rows (series K-T2, metric T09s.<scene>.<view>.<variant>.<row>):
    //   tstd, hfx, mask_n, mask_hf, mask_hf_off, cloud_share   the grain of the pixels the clouds changed against the baseline (T09ProbeGrain.MeasureGrain; the same measure as the grain start)
    //   box_tstd, box_blob, box_lap, box_luma                  the same over the fixed boxes of the view (T09ProbeGt.MeasureGtGrain; the measure of start 56)
    //   grid_points, softness, soft_density, effective_ok      what the pass really ran with: the grid points of the march, the width of the soft rim, the soft density's switch
    //   march_w, march_h, span_tight, drop_empty, skip_empty, dither_sequence, layers_none, layers_total, spans   the march target's size, the cost candidates' switches, the layers left out of the march
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
            // The views the variant runs in at all (frames and windows) per scene: "" every view of the scene, "-" none, else the letters (g ground, i inside, a above, s sky3).
            // A cost window outside these views is a table error (TableProblem).
            internal string FairViews = "", ClockViews = "";
            internal bool Active { get { return Pairs != null; } }

            internal bool Costs(bool fair, char view)
            {
                return (fair ? FairCost : ClockCost).IndexOf(view) >= 0;
            }

            internal bool Runs(bool fair, char view)
            {
                string views = fair ? FairViews : ClockViews;
                return views.Length == 0 || (views != "-" && views.IndexOf(view) >= 0);
            }

            // The variant restricted to the views of each scene (see FairViews).
            internal SoftVariant In(string fairViews, string clockViews)
            {
                FairViews = fairViews;
                ClockViews = clockViews;
                return this;
            }

            // Null when the table row is sound: every cost view is a view the variant runs in, and the view letters are the four known ones.
            internal string TableProblem()
            {
                foreach (bool fair in new[] { true, false })
                {
                    string scene = fair ? "fair" : "clock";
                    string views = fair ? FairViews : ClockViews;
                    foreach (char c in views) if (c != '-' && "gias".IndexOf(c) < 0) return Name + ": the " + scene + " view letter '" + c + "' is none of g i a s";
                    foreach (char c in fair ? FairCost : ClockCost)
                    {
                        if ("gias".IndexOf(c) < 0) return Name + ": the " + scene + " cost view letter '" + c + "' is none of g i a s";
                        if (!Runs(fair, c)) return Name + ": a cost window in the " + scene + " view " + c + " that the variant does not run in";
                    }
                }
                return null;
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
        // soft100s24: cloudSoftness 1.0 with cloudSamples Low (24), the owner's other favourite; no grid points are left of the old sampling keys, the count is cloudSamples alone.
        // The table of 2026-11-14 evening (E40 L2 package B2): the cost candidates of .local/plans/atmospheres/E40-ground-cost-result.md against TVC, sized to the time plan
        // (SoftEstimateS at most 1020 s of SoftMaxSeconds). In: the views a variant runs in (frames and windows), "" every view of the scene, "-" none.
        //   softhash   layers.ditherSequence 1, the white hash the default drew before (the owner's look decision, E40 step (c)): the fair views' frames, no window, not in the clock scene
        //   softr33    the march target at 0.33 of the full size (recon.resolution) with cloudSkipEmpty on (the tile test that starts no ray in a tile the maps prove empty; its default is OFF)
        //   softcut16  the span cut to the cloud types around the camera (layers.spanTight) with a grid of 16 points (layers.gridFine 0.3333 of 48) at the march target 0.33
        //   softnodrop layers.dropEmpty 0: every layer is marched over its span as before the drop of layers with no visible cloud (default on); what the drop saves is soft against this
        // Cut for the time (the lead's order): soft100s24's clock window and softnodrop's clock view (the plan: 1058 s with them, 1005 s without, the test pins 1020).
        // Slots are gone: the table is full. The size of one more candidate is what SoftEstimateS says; an example that costs one window: Soft("soft32", "", "g", "s:" + AtmospheresSettings.Keys.CloudSamples + "=1").
        internal static readonly SoftVariant[] SoftVariants =
        {
            Soft("soft", "ga", "gias"),
            Soft("hard", "g", "g", "t:" + CloudSoftDensity.TuneOn + "=0"),
            Soft("soft100s24", "g", "", "s:" + AtmospheresSettings.Keys.CloudSoftness + "=1", "s:" + AtmospheresSettings.Keys.CloudSamples + "=0"),
            Soft("softhash", "", "", "t:" + OwnCloudOptions.TuneDitherSequence + "=1").In("", "-"),
            Soft("softr33", "ga", "gias", "t:" + OwnCloudOptions.TuneReconResolution + "=0.33", "s:" + AtmospheresSettings.Keys.CloudSkipEmpty + "=1"),
            Soft("softcut16", "g", "g", "t:" + OwnCloudOptions.TuneSpanTight + "=1", "t:" + OwnCloudOptions.TuneGridFine + "=0.3333", "t:" + OwnCloudOptions.TuneReconResolution + "=0.33").In("g", "g"),
            Soft("softnodrop", "g", "", "t:" + OwnCloudOptions.TuneDropEmpty + "=0").In("g", "-")
        };

        // The scenario's own limit (the runner's is 20 minutes for the whole start: the game needs about 90 s to come up). A guard only: the scenario ends when its last view is done.
        internal const double SoftMaxSeconds = 18 * 60;
        // The window of one cost reading. The profiler reports every 10 s and the first report after a begin line is dropped (kt2.py): a window of 22 s holds at least two reports, so at least
        // one clean one (the period may run a few hundredths over 10 s, which a window of 20 s would not survive).
        internal const double SoftWindowS = 22.0;
        // After a variant's keys changed: frames for the history of the half-size march, and seconds for the maps' round (the layers' numbers follow CloudSoftDensity.Stamp).
        // recon.resolution makes LayerReconstructor.Record create the march target and the history pair at the new size (Fits fails: haveLowHistory false); the history then fills
        // with the weight recon.accumulate 0.2 a frame, so 90 frames leave 0.8^90 = 2e-9 of the empty start: the settle holds for the change to 0.33 and the change back.
        private const int SoftSettleFrames = DiagSettleFrames + 60;
        private const double SoftSettleS = 2.0;
        // The estimate of the time (seconds) one step takes, for the plan and for the guard that leaves out a view the time no longer holds.
        // SoftPlaceS: MEASURED in the log of start 56 (series K-T2, start gt1707, .local/work/GT/build/atmo-series/K-T2-20261113-181107-gt1707.log): a placement took about 3.5 s, and the
        // whole gap from the end of one view's last window to the begin of the next view's first window (placement, the stable gate, the variant's switch, the first frames) was at
        // most 9.0 s (18:26:46 -> 18:26:55, 18:29:07 -> 18:29:16, 18:30:20 -> 18:30:29). 8 s is more than twice the measured placement and the gap it stands in has room for the
        // switch and frames that are counted apart. (The first plan assumed 25 s.) The grain read of a trio took 1.3 to 2 s there: SoftFrameReadS 1.5 per frame stays, a margin of 2 to 3.
        internal const double SoftPlaceS = 8.0, SoftSwitchS = 4.5, SoftFrameReadS = 1.5, SoftPickClockS = 60.0, SoftPickCoverS = 90.0, SoftReserveS = 20.0;

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
            // E40 L2 B2: the march target's side over the full target's (recon.resolution; the history path of recon.accumulate runs at it), the cut span, the drop of layers with no
            // visible cloud, the step offset's sequence and the tile test of empty sky (the setting cloudSkipEmpty).
            internal float Resolution;
            internal bool SpanTight, DropEmpty, SkipEmpty;
            internal int DitherSequence;
        }

        private static readonly float SoftCompiledResolution = new LayerFrame().Resolution;

        // The march target's size for a frame of w x h at the side ratio (LayerReconstructor.Record: Low mode, round(w * scale), at least 2).
        internal static void SoftMarchSize(int w, int h, float resolution, out int lw, out int lh)
        {
            float scale = Mathf.Clamp(resolution, 0.1f, 1f);
            lw = Math.Max(2, Mathf.RoundToInt(w * scale));
            lh = Math.Max(2, Mathf.RoundToInt(h * scale));
        }

        internal static SoftExpectation SoftExpect(SoftVariant v)
        {
            double softness = AtmospheresSettings.Definition(AtmospheresSettings.Keys.CloudSoftness).Default;
            double level = AtmospheresSettings.Definition(AtmospheresSettings.Keys.CloudSamples).Default;
            bool soft = true;
            float gridFine = OwnCloudOptions.GridFineDefault;
            float resolution = SoftCompiledResolution;
            bool spanTight = OwnCloudOptions.SpanTightDefault, dropEmpty = OwnCloudOptions.DropEmptyDefault;
            bool skipEmpty = AtmospheresSettings.Definition(AtmospheresSettings.Keys.CloudSkipEmpty).Default != 0.0;
            int dither = OwnCloudOptions.DitherSequenceDefault;
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
                    else if (setting && key == AtmospheresSettings.Keys.CloudSkipEmpty) skipEmpty = value != 0f;
                    else if (!setting && key == OwnCloudOptions.TuneReconResolution) resolution = value;
                    else if (!setting && key == OwnCloudOptions.TuneSpanTight) spanTight = value != 0f;
                    else if (!setting && key == OwnCloudOptions.TuneDropEmpty) dropEmpty = value != 0f;
                    else if (!setting && key == OwnCloudOptions.TuneDitherSequence) dither = Math.Max(1, Math.Min(2, Mathf.RoundToInt(value)));
                }
            }
            softness = Math.Max(CloudSoftDensity.MinSoftness, Math.Min(CloudSoftDensity.MaxSoftness, softness));
            int samples = OwnCloudOptions.SamplesOf((CloudDetailLevel)Math.Max(0, Math.Min(2, (int)level)));
            int baseMax, sun;
            float min;
            LayerSwitches.StepQuality(Math.Max(0, Math.Min(2, (int)AtmospheresSettings.Number(AtmospheresSettings.Keys.LayerStepQuality))), out baseMax, out sun, out min);
            int points = CloudSoftDensity.ScaleSamples(baseMax, samples);
            points = Math.Max(4, Math.Min(256, Mathf.RoundToInt(points * Mathf.Clamp(gridFine, 0.25f, 8f))));
            return new SoftExpectation
            {
                Softness = softness, Samples = samples, SoftDensity = soft, GridPoints = points, Resolution = Mathf.Clamp(resolution, 0.1f, 1f), SpanTight = spanTight, DropEmpty = dropEmpty,
                SkipEmpty = skipEmpty, DitherSequence = dither
            };
        }

        // The seconds one view takes with the variants of the table: the placement, the baseline (settle, one frame, a window), then per variant the switch, three frames and its window.
        internal static double SoftEstimateViewS(bool fair, char view, IList<SoftVariant> table)
        {
            double s = SoftPlaceS + SoftFrameReadS * 2.0 + SoftWindowS;
            foreach (SoftVariant v in table)
            {
                if (!v.Active || !v.Runs(fair, view)) continue;
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
            // A variant's setting must not leak into the next one (the tuning keys are cleared by UseOwn): cloudSkipEmpty is off by default and softr33 turns it on.
            SetSetting(AtmospheresSettings.Keys.CloudSkipEmpty, AtmospheresSettings.Definition(AtmospheresSettings.Keys.CloudSkipEmpty).Default);
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
        // E40 L2 B2: the numbers of the cost candidates too: the march target's size (rows march_w, march_h; the target the pass really drew into, read from the player's reconstructor),
        // the cut span, the drop switch, the step offset's sequence and the tile test, and the layers the pass left out ("none" in LayerRenderer.SpanStats: row layers_none of
        // layers_total). The target's size is checked where the reconstructor exists (not in a test host's fake world; MarchSizeReader is the seam).
        internal Func<OwnCloudModule, int[]> MarchSizeReader = ReadMarchSize;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int[] ReadMarchSize(OwnCloudModule own)
        {
            LayerReconstructor player = own.Driver != null ? own.Driver.Player : null;
            RenderTexture target = player != null ? player.March : null;
            return target != null ? new[] { target.width, target.height } : null;
        }

        // The layers of a SpanStats text that carry "none" (no interval: left out of the march, by the drop or by the cut span) and the layers it names.
        internal static void SoftCountNone(string spans, out int none, out int total)
        {
            none = 0;
            total = 0;
            if (string.IsNullOrEmpty(spans)) return;
            string[] tokens = spans.Split(' ');
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].Length > 1 && tokens[i][0] == 'L' && char.IsDigit(tokens[i][1])) total++;
                else if (tokens[i] == "none") none++;
            }
        }

        // The layers left out per view with the variant soft (the baseline of the drop: the same pose, the default keys).
        private readonly Dictionary<string, int> softNoneBySoft = new Dictionary<string, int>();

        private void SoftVerify(string viewHead, SoftVariant v)
        {
            string head = viewHead + v.Name + ".";
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
            string more = "";
            bool spanTight = own.Frame.SpanTight, drop = own.Frame.DropEmpty, skip = own.Frame.Cull != null && own.Frame.Cull.SkipEmpty;
            int dither = own.Frame.DitherSequence;
            Emit(head + "span_tight", spanTight);
            Emit(head + "drop_empty", drop);
            Emit(head + "skip_empty", skip);
            Emit(head + "dither_sequence", dither);
            bool framesOk = spanTight == e.SpanTight && drop == e.DropEmpty && skip == e.SkipEmpty && dither == e.DitherSequence;
            ok = ok && framesOk;
            more += ", span tight " + spanTight + "/" + e.SpanTight + ", drop " + drop + "/" + e.DropEmpty + ", skip empty " + skip + "/" + e.SkipEmpty + ", dither " + dither + "/" + e.DitherSequence;
            if (MarchSizeReader != null)
            {
                int[] size = MarchSizeReader(own);
                if (size != null)
                {
                    int lw, lh;
                    SoftMarchSize(Game.ScreenWidth, Game.ScreenHeight, e.Resolution, out lw, out lh);
                    Emit(head + "march_w", size[0]);
                    Emit(head + "march_h", size[1]);
                    bool sizeOk = size[0] == lw && size[1] == lh;
                    ok = ok && sizeOk;
                    more += ", march target " + size[0] + "x" + size[1] + " (expected " + lw + "x" + lh + ")";
                }
            }
            string spans = own.Renderer != null ? own.Renderer.SpanStats : null;
            if (spans != null)
            {
                int none, total;
                SoftCountNone(spans, out none, out total);
                Emit(head + "layers_none", none);
                Emit(head + "layers_total", total);
                EmitText(head + "spans", spans);
                more += ", layers left out " + none + " of " + total + " [" + spans + "]";
                if (v.Name == "soft") softNoneBySoft[viewHead] = none;
                int reference;
                // dropEmpty off can only leave FEWER layers out than the same pose with the drop on: more is a variant that did not take effect (or a pose that moved).
                if (!e.DropEmpty && softNoneBySoft.TryGetValue(viewHead, out reference) && none > reference)
                    Warn("drop_effect", viewHead + v.Name + ": " + none + " layers left out with dropEmpty 0 against " + reference + " with the defaults");
            }
            Emit(head + "effective_ok", ok);
            AtmospheresLog.Say("T09 soft variant " + v.Name + " (" + head + "): grid points " + points + " (expected " + e.GridPoints + "), softness " + softness.ToString("0.###", CultureInfo.InvariantCulture)
                               + " (expected " + e.Softness.ToString("0.###", CultureInfo.InvariantCulture) + "), soft density " + on + " (expected " + e.SoftDensity + ")" + more);
            if (ChecksEffectiveNumbers)
                Check("T09s.effective_" + head.Replace('.', '_') + v.Name, ok, "grid points " + points + " against " + e.GridPoints + ", softness " + softness.ToString("0.###", CultureInfo.InvariantCulture) + " against "
                      + e.Softness.ToString("0.###", CultureInfo.InvariantCulture) + ", soft density " + on + " against " + e.SoftDensity + more);
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
                  .Append(e.SoftDensity ? "on" : "off").Append(", march target ").Append(e.Resolution.ToString("0.##", CultureInfo.InvariantCulture)).Append(", in fair '").Append(v.FairViews)
                  .Append("' clock '").Append(v.ClockViews).Append("', cost fair '").Append(v.FairCost).Append("' clock '").Append(v.ClockCost).Append("']");
                string problem = v.TableProblem();
                if (problem != null) Check("T09s.table_" + v.Name, false, problem);
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
                if (!v.Active || !v.Runs(fair, letter)) continue;
                SoftApply(v);
                foreach (ProbeWait w in SoftSettle()) yield return w;
                SoftVerify(head, v);
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
