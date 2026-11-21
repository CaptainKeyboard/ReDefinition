using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Series K-T2, the pinned capture (-t09Pin with -t09Ut <seconds>; master plan phase 0 item 3, .local/plans/wolken-m1-fakten.md section F): one own variant, the ground view
    // (or each view -t09PinViews names, e.g. ground,inside,above,sky3: the M0 views, placed as the fps runs place them, turned by -t09HeadingOffset), and instead of the timing
    // window a capture in a state that a second start of the same commit and the weather player can reproduce:
    //   1. the universal time is pinned at the -t09Ut value (IT09World.PinClock: before every physics step and at every frame's end), cloud.motionScale 0 holds the noise,
    //      and its drift, rise and flow clock are set to 0 (OwnCloudModule.PinNoise);
    //   2. a map round with a snap and a height build are asked for at the pinned time, and the probe waits until the maps equal their goal and a height build finished;
    //   3. the frame origin is set to this frame and the history is cut in it (the clouds' reset counter, CloudBridgeLights, which the pass reads in this frame's record;
    //      ApiBridge.RequestHistoryReset besides, so that the upscaler's history starts with it);
    //   4. the pass records PinFrames frames from the cut; in the frame of the last one the clouds' own output before the composite (cloud.npy, linear float32), the scene
    //      depth the pass read (depth.npy), the camera target before the clouds' composite (scene.npy: the game's frame without our clouds, the background of the player's
    //      replay) and after it (composed.npy), both at the render size before Scatterer's later passes, the upscaler and the post-processing, the natural picture
    //      (natural.npy, linear, and the PNG), the screen at the display size (the PNG t09-<start>-<view>-<variant>-<w>x<h>-pinscreen.png, UI hidden with -t09HideUi) and the
    //      maps with pass.json are taken at that frame's end.
    //   5. -t09PinLimits <name=kind:key:value+...;...> (in the views of -t09PinLimitViews, default ground; master plan section 29, the straight line before the horizon): after the
    //      plain capture, each named set of switches is applied, the history cut again, and the same capture taken under the tag pin-<view>-<name>-...; kinds t (a tuning key forced), s (a setting set, put back
    //      after), p (a probe switch: surface 0 = OwnCloudModule.ProbeNoSurface, scenedepth 0 = ProbeNoSceneDepth, maxsteps N = LayerProbeSwitches.MaxSteps).
    // Every step is a row T09.pin.<view>[-<limit>].<step> and a check; a step that cannot be done fails the start by name and the next view is taken. The files land in
    // series-pictures/maps/<tag> (tag pin-<view>[-<limit>]-u<ut>-<start>-<time>).
    internal sealed partial class T09Probe
    {
        // The frames from the cut to the capture (the history's own reach is a few dozen frames at the accumulation weight 0.2: 0.8^64 is 6e-7 of the cut frame left).
        internal const int PinFrames = 64;
        // The longest wait for the maps and the heights at the pinned time: a height build takes seconds on the worker (Stats.LastHeightMs), the map round one; 90 s is far
        // beyond both and still short against the start's budget.
        private const double PinSettleTimeoutS = 90.0;
        // The map rounds (one every OwnCloudPlan.MapIntervalS) the settled state must hold before the cut: two rounds at the pinned time that left the maps at their goal show
        // that the rounds of the hold will not move them.
        private const int PinSettledRounds = 2;
        // The longest wait for the hold's frames (64 frames at the lowest frame rate a K-T2 start has logged, about 5 fps, are 13 s) and for the capture's reads.
        private const double PinHoldTimeoutS = 30.0, PinReadTimeoutS = 30.0;
        // The longest wait for the first phase of the host's jitter (a sequence of 8 to 32 phases: Fsr3Upscaler.GetJitterPhaseCount for render scales 1 to 2; 10 s is
        // dozens of sequences even at the lowest frame rate).
        private const double PinJitterTimeoutS = 10.0;

        private bool PinStart
        {
            get { return CommandLine.Has(Args(), "-t09Pin", true); }
        }

        private bool pinActive;

        // The clock runs again and the frame origin is 0 again: at the end of the capture, and from RevertVariant when the probe ends otherwise (a failed step, a timeout,
        // a lost scene), so the pass never keeps the probe's origin.
        private void PinCleanup()
        {
            if (!pinActive) return;
            pinActive = false;
            try { Game.PinClock(double.NaN); } catch (Exception) { /* the game may be gone */ }
            OwnCloudModule own = OwnCloudModule.Current;
            if (own != null) own.FrameOrigin = 0;
            PinLimitSwitchesOff();
        }

        // The probe-only switches of -t09PinLimits back at their defaults.
        private static void PinLimitSwitchesOff()
        {
            OwnCloudModule.ProbeNoSurface = false;
            OwnCloudModule.ProbeNoSceneDepth = false;
            LayerProbeSwitches.MaxSteps = 0;
            ApiBridge.ProbeSkipOwners.Clear();
            SceneWhiteBalance.ProbeOldBlit = SceneWhiteBalance.ProbeIdentityVp = SceneWhiteBalance.ProbeVp = false;
        }

        // The white balance test's probe switches (2026-11-21): wboldblit (the old two-Blit path), wbident (an identity view and projection recorded before
        // it), wbvp (the view-projection probe). Whether key is one of them; with set, the switch takes value != 0.
        private static bool WhiteBalanceSwitch(string key, float value, bool set)
        {
            bool on = value != 0f;
            switch (key)
            {
                case "wboldblit": if (set) SceneWhiteBalance.ProbeOldBlit = on; return true;
                case "wbident": if (set) SceneWhiteBalance.ProbeIdentityVp = on; return true;
                case "wbvp": if (set) SceneWhiteBalance.ProbeVp = on; return true;
                default: return false;
            }
        }

        // The post-upscale draw test of 2026-11-21: the probe switches skipprecip and skiplamp leave that draw uncalled (ApiBridge.ProbeSkipOwners), for the
        // two draws that register whenever the host has depth and have no setting that takes them away (PrecipitationRenderer.Tick, LampScatter.Tick).
        private static string SkipDrawOwner(string key)
        {
            switch (key)
            {
                case "skipprecip": return "precipitation";
                case "skiplamp": return "lamp scatter";
                default: return null;
            }
        }

        // The view (and limit set) the steps of the pinned capture belong to: the rows read T09.pin.<label>.<step>.
        private string pinLabel = "ground";

        // A step of the pinned capture: a row and a check; false ends the capture (the check has failed the start).
        private bool PinStep(string name, bool ok, string detail)
        {
            Emit("T09.pin." + pinLabel + "." + name, ok);
            Check("T09.pin." + pinLabel + "." + name, ok, detail);
            return ok;
        }

        private void PinEmit(string name, double value)
        {
            Emit("T09.pin." + pinLabel + "." + name, value);
        }

        // -t09PinViews: the views of the pinned capture by name (Views: ground, inside, above, sky3, ...), turned by -t09HeadingOffset; the ground view alone without it.
        private List<View> PinViews(out string problem)
        {
            problem = null;
            var list = new List<View>();
            string text = CommandLine.After(Args(), "-t09PinViews", true);
            foreach (string name in (string.IsNullOrEmpty(text) ? "ground" : text).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                View found = default(View);
                bool known = false;
                foreach (View v in Views)
                    if (v.Name == name.Trim()) { found = v; known = true; }
                if (!known || found.Name == "orbit_edge")
                {
                    problem = "-t09PinViews names '" + name + "', which is not a pinned view (ground, inside, above, sky3)";
                    return list;
                }
                list.Add(Turned(found));
            }
            return list;
        }

        // One switch of a -t09PinLimits set.
        private sealed class PinSwitch
        {
            internal char Kind;
            internal string Key;
            internal float Value;
        }

        private sealed class PinLimit
        {
            internal string Name;
            internal readonly List<PinSwitch> Switches = new List<PinSwitch>();
        }

        // -t09PinLimits name=kind:key:value+kind:key:value;name2=...  (kinds t, s, p; see the file's head).
        private List<PinLimit> PinLimits(out string problem)
        {
            problem = null;
            var list = new List<PinLimit>();
            string text = CommandLine.After(Args(), "-t09PinLimits", true);
            if (string.IsNullOrEmpty(text)) return list;
            foreach (string entry in text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = entry.IndexOf('=');
                var limit = new PinLimit { Name = eq > 0 ? entry.Substring(0, eq).Trim() : "" };
                if (limit.Name.Length == 0)
                {
                    problem = "-t09PinLimits entry '" + entry + "' has no name=";
                    return list;
                }
                foreach (string part in entry.Substring(eq + 1).Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] f = part.Split(':');
                    float value;
                    if (f.Length != 3 || f[0].Length != 1 || "tsp".IndexOf(f[0][0]) < 0 || !float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                        || (f[0][0] == 'p' && f[1] != "surface" && f[1] != "scenedepth" && f[1] != "maxsteps" && SkipDrawOwner(f[1]) == null
                            && !WhiteBalanceSwitch(f[1], 0f, false)))
                    {
                        problem = "-t09PinLimits switch '" + part + "' of " + limit.Name + " is not kind:key:value (kinds t, s, p; probe switches surface, scenedepth, maxsteps, "
                                  + "skipprecip, skiplamp)";
                        return list;
                    }
                    limit.Switches.Add(new PinSwitch { Kind = f[0][0], Key = f[1], Value = value });
                }
                list.Add(limit);
            }
            return list;
        }

        // Applies a limit set; the settings it changes are remembered in restore (key, value before).
        private void PinLimitApply(PinLimit limit, List<KeyValuePair<string, double>> restore)
        {
            foreach (PinSwitch s in limit.Switches)
            {
                if (s.Kind == 't') ForceTuning(s.Key, s.Value);
                else if (s.Kind == 's')
                {
                    restore.Add(new KeyValuePair<string, double>(s.Key, AtmospheresSettings.Number(s.Key)));
                    SetSetting(s.Key, s.Value);
                }
                else if (s.Key == "surface") OwnCloudModule.ProbeNoSurface = s.Value == 0f;
                else if (s.Key == "scenedepth") OwnCloudModule.ProbeNoSceneDepth = s.Value == 0f;
                else if (SkipDrawOwner(s.Key) != null)
                {
                    if (s.Value != 0f) ApiBridge.ProbeSkipOwners.Add(SkipDrawOwner(s.Key));
                }
                else if (WhiteBalanceSwitch(s.Key, s.Value, true)) { }
                else LayerProbeSwitches.MaxSteps = (int)s.Value;
                AtmospheresLog.Say("T09 pin limit " + limit.Name + ": " + s.Kind + " " + s.Key + " = " + s.Value.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        // Undoes a limit set: the forced tuning keys go (cloud.motionScale 0 is forced again), the settings come back, the probe switches are off.
        private void PinLimitRevert(List<KeyValuePair<string, double>> restore)
        {
            ClearForced();
            ForceTuning(CloudMotion.TuneScale, 0f);
            foreach (KeyValuePair<string, double> kv in restore) SetSetting(kv.Key, kv.Value);
            restore.Clear();
            PinLimitSwitchesOff();
        }

        // -t09HideUi: the stock UI and every mod that follows the game's hide event (F2: MechJeb, the toolbars) hidden for the pinned capture.
        private bool pinUiHidden;

        private void PinHideUi()
        {
            if (pinUiHidden || !CommandLine.Has(Args(), "-t09HideUi", true)) return;
            pinUiHidden = true;
            try { GameEvents.onHideUI.Fire(); } catch (Exception e) { AtmospheresLog.Say("T09 pin: onHideUI threw " + e.GetType().Name + " " + e.Message); }
            KSP.UI.UIMasterController ui = KSP.UI.UIMasterController.Instance;
            if (ui != null && ui.IsUIShowing) ui.HideUI();
        }

        private void PinShowUi()
        {
            if (!pinUiHidden) return;
            pinUiHidden = false;
            try { GameEvents.onShowUI.Fire(); } catch (Exception) { /* the game may be gone */ }
            KSP.UI.UIMasterController ui = KSP.UI.UIMasterController.Instance;
            if (ui != null && !ui.IsUIShowing) ui.ShowUI();
        }

        private IEnumerator<ProbeWait> PinScenario()
        {
            foreach (ProbeWait w in Prelude()) yield return w;
            if (!preludeOk) yield break;
            string utText = CommandLine.After(Args(), "-t09Ut", true);
            double asked;
            if (string.IsNullOrEmpty(utText) || !double.TryParse(utText, NumberStyles.Float, CultureInfo.InvariantCulture, out asked) || asked <= 0.0)
            {
                Fail("T09.pin.clock: -t09Pin needs -t09Ut <seconds> (the time master plan phase 0.1 found), got " + (utText ?? "nothing"));
                yield break;
            }
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
            AtmospheresLog.Say("T09 pin: ut " + utChosen.ToString("0", CultureInfo.InvariantCulture) + ", cover " + coverChosen.ToString("0.00") + ", sun " + sunChosen.ToString("0.0") + " degrees");
            yield return ProbeWait.Seconds(MapSettleS);
            CheckPicturesFolder();

            IList<string> variants = Variants;
            string variant = variants.Count > 0 ? variants[0] : "plain";
            if (!IsOwn(variant) || variants.Count != 1)
            {
                Fail("T09.pin.variant: the pinned capture takes one own variant, the start names " + string.Join(",", new List<string>(variants).ToArray()));
                yield break;
            }
            BeginVariant(variant);
            CheckVariantGates(variant);
            yield return ProbeWait.Seconds(4);
            OwnCloudModule started = OwnCloudModule.Current;
            int drawnFrom = started != null ? started.Stats.FramesDrawn : 0;
            yield return ProbeWait.Until(() =>
            {
                OwnCloudModule o = OwnCloudModule.Current;
                return o != null && o.Stats.Mode != "off" && o.Stats.FramesDrawn - drawnFrom > SteadyFrames;
            }, 150, "the own pass to draw a steady stretch (" + variant + ")");

            ReadHeadingOffset();
            string problem;
            List<View> views = PinViews(out problem);
            if (problem != null)
            {
                Fail("T09.pin.views: " + problem);
                yield break;
            }
            List<PinLimit> limits = PinLimits(out problem);
            if (problem != null)
            {
                Fail("T09.pin.limits: " + problem);
                yield break;
            }
            Emit("T09.pin.views", views.Count);
            Emit("T09.pin.limits", limits.Count);
            // -t09Turn (T09ProbeTurn.cs): the pinned turn instead of the captures, on the launch pad (-t09TurnPad: no placement) or in the first view of -t09PinViews.
            if (TurnFrames > 0)
            {
                bool pad = TurnPad;
                View turnView = pad ? new View { Name = "pad", CameraDistM = 60.0 } : views.Count > 0 ? views[0] : Views[0];
                pinLabel = pad ? "pad" : turnView.Name;
                if (!pad)
                {
                    foreach (ProbeWait w in PlaceChecked(siteLat, siteLon, turnView)) yield return w;
                    if (!PinStep("placed", placeOk, "the " + turnView.Name + " view")) yield break;
                    foreach (ProbeWait w in StableGate(turnView, variant)) yield return w;
                }
                pinActive = true;
                PinHideUi();
                foreach (ProbeWait w in PinSettleSteps(turnView)) yield return w;
                if (pinSettledOk)
                {
                    foreach (ProbeWait w in PinTurn(OwnCloudModule.Current, limits, turnView)) yield return w;
                }
                PinShowUi();
                PinCleanup();
                pinLabel = "ground";
                EndVariant();
                if (!pad) Game.StopHold();
                yield break;
            }
            var restore = new List<KeyValuePair<string, double>>();
            foreach (View view in views)
            {
                pinLabel = view.Name;
                foreach (ProbeWait w in PlaceChecked(siteLat, siteLon, view)) yield return w;
                if (!PinStep("placed", placeOk, "the " + view.Name + " view")) continue;
                foreach (ProbeWait w in StableGate(view, variant)) yield return w;
                pinActive = true;
                PinHideUi();
                foreach (ProbeWait w in PinSettleSteps(view)) yield return w;
                if (!pinSettledOk)
                {
                    PinShowUi();
                    PinCleanup();
                    continue;
                }
                if (PinSeriesFrames > 0)
                {
                    foreach (ProbeWait w in PinSeriesAims(view, variant, OwnCloudModule.Current)) yield return w;
                    yield break;
                }
                foreach (ProbeWait w in PinCutHoldCapture(view, variant, null)) yield return w;
                // -t09PinLimitViews (default ground): the views the limit sets are captured in.
                string limitViews = CommandLine.After(Args(), "-t09PinLimitViews", true);
                if (Array.IndexOf((string.IsNullOrEmpty(limitViews) ? "ground" : limitViews).Split(','), view.Name) >= 0)
                {
                    foreach (PinLimit limit in limits)
                    {
                        pinLabel = view.Name + "-" + limit.Name;
                        PinLimitApply(limit, restore);
                        // Two frames for the switches to reach the frame's record (a tuning override is read at the next frame).
                        yield return ProbeWait.Frames(2);
                        foreach (ProbeWait w in PinCutHoldCapture(view, variant, limit.Name)) yield return w;
                        PinLimitRevert(restore);
                    }
                }
                PinShowUi();
                PinCleanup();
            }
            pinLabel = "ground";
            EndVariant();
            Game.StopHold();
        }

        // Set by PinSettleSteps: the clock, the noise, the maps and the heights stand at the pinned time.
        private bool pinSettledOk;

        private IEnumerable<ProbeWait> PinSettleSteps(View view)
        {
            pinSettledOk = false;
            // 1. The clock and the noise.
            Game.PinClock(utChosen);
            ForceTuning(CloudMotion.TuneScale, 0f);
            yield return ProbeWait.Frames(3);
            double ut = Game.UniversalTime;
            // The pinned clock may stand one physics step past the pin where Planetarium's own step runs after the hook (see KspT09World.PinClock); 1 s is far above that.
            if (!PinStep("clock", Math.Abs(ut - utChosen) < 1.0, "ut " + ut.ToString("0.000", CultureInfo.InvariantCulture) + " against the pinned " + utChosen.ToString("0.000", CultureInfo.InvariantCulture))) yield break;
            OwnCloudModule own = OwnCloudModule.Current;
            if (!PinStep("module", own != null && own.Driver != null && own.Renderer != null, own == null ? "no own cloud module" : "the pass has no renderer or reconstruction")) yield break;
            if (!PinStep("noise", own.PinNoise(), "the noise state of every layer set to drift 0, rise 0, flow clock 0 (cloud.motionScale 0)")) yield break;

            // 2. The maps and the heights at the pinned time.
            int builds = own.HeightBuildsDone;
            own.PinRefetch();
            string why = "not looked at";
            double settledSince = double.NaN, holdS = PinSettledRounds * OwnCloudPlan.MapIntervalS;
            int settledFrames = 0;
            double giveUp = Elapsed + PinSettleTimeoutS;
            bool settled = false;
            while (!settled && Elapsed < giveUp)
            {
                yield return ProbeWait.Frames(1);
                if (own.PinSettled(builds, out why))
                {
                    if (double.IsNaN(settledSince)) settledSince = Elapsed;
                    settledFrames++;
                    settled = Elapsed - settledSince >= holdS;
                }
                else
                {
                    settledSince = double.NaN;
                    settledFrames = 0;
                }
            }
            if (!PinStep("settled", settled, settled ? "maps at their goal and a height build done, held " + holdS.ToString("0.0") + " s (" + settledFrames + " frames)"
                                                     : why + " after " + PinSettleTimeoutS.ToString("0") + " s")) yield break;
            pinSettledOk = true;
        }

        // The folder of the last capture's files (PinCutHoldCapture; null before the first).
        private string lastPinDir;

        // Steps 3 to 5 in the settled state: the cut, the hold, the capture; limit names the -t09PinLimits set in force (null: the plain capture).
        private IEnumerable<ProbeWait> PinCutHoldCapture(View view, string variant, string limit)
        {
            OwnCloudModule own = OwnCloudModule.Current;
            if (!PinStep("module_held", own != null && own.Driver != null && own.Renderer != null, "the own pass is gone or has no reconstruction")) yield break;

            // 3. The frame origin and the cut, in this frame (the pass records after the probe's tick), in the frame after one at the first phase of the host's jitter.
            double jitterGiveUp = Elapsed + PinJitterTimeoutS;
            int jitterFrames = 0;
            while (!OwnCloudModule.HostJitterAtFirstPhase() && Elapsed < jitterGiveUp)
            {
                yield return ProbeWait.Frames(1);
                jitterFrames++;
            }
            if (!PinStep("jitter_phase", OwnCloudModule.HostJitterAtFirstPhase(), "the host's jitter at its first phase after " + jitterFrames + " frames")) yield break;
            own.PinNoise();
            int cutsBefore = own.HistoryCuts;
            own.FrameOrigin = Game.FrameCount;
            CloudBridgeLights.RequestHistoryReset("pinned capture");
            try { new ApiBridge().RequestHistoryReset("ReDefinition pinned capture"); }
            catch (Exception e) { AtmospheresLog.Say("T09 pin: the host's history reset threw " + e.GetType().Name + " " + e.Message); }
            PinEmit("frame_origin", own.FrameOrigin);
            yield return ProbeWait.Frames(1);
            if (!PinStep("history_cut", own.HistoryCuts > cutsBefore && own.HistoryFramesSinceCut == 1,
                         "cuts " + cutsBefore + " -> " + own.HistoryCuts + ", frames since the cut " + own.HistoryFramesSinceCut)) yield break;
            int cutsAfter = own.HistoryCuts;

            // 4. The hold: the probe ticks before the frame's record, so at PinFrames - 1 the record of this frame is the PinFrames-th.
            // Each tick reads what the frame before it filled: the time the module's tick took, the sun's direction and the planet's centre of its record.
            double giveUp = Elapsed + PinHoldTimeoutS;
            double utMin = double.MaxValue, utMax = double.MinValue, sunSpreadDeg = 0.0, centreSpreadM = 0.0;
            Vector3 sun0 = own.Frame.TowardsSun, centre0 = own.Frame.PlanetCenter;
            while (own.HistoryFramesSinceCut < PinFrames - 1 && own.HistoryCuts == cutsAfter && Elapsed < giveUp)
            {
                yield return ProbeWait.Frames(1);
                utMin = Math.Min(utMin, own.FrameUt);
                utMax = Math.Max(utMax, own.FrameUt);
                sunSpreadDeg = Math.Max(sunSpreadDeg, Vector3.Angle(sun0, own.Frame.TowardsSun));
                centreSpreadM = Math.Max(centreSpreadM, (own.Frame.PlanetCenter - centre0).magnitude);
            }
            if (!PinStep("hold", own.HistoryFramesSinceCut == PinFrames - 1 && own.HistoryCuts == cutsAfter,
                         "frames since the cut " + own.HistoryFramesSinceCut + " (wanted " + (PinFrames - 1) + " before the capture frame), cuts " + cutsAfter + " -> " + own.HistoryCuts)) yield break;
            double utSpread = utMax >= utMin ? utMax - utMin : double.NaN, step = Game.PhysicsStepS;
            PinEmit("ut_spread_s", utSpread);
            PinEmit("sun_spread_deg", sunSpreadDeg);
            PinEmit("centre_spread_m", centreSpreadM);
            // The planet's centre moves with the floating origin and the hold; it is written into pass.json and the replay takes the capture frame's, so it is a row, not a check.
            if (!PinStep("ut_spread", utSpread <= step, "the time the module read over the hold spread " + utSpread.ToString("0.0000", CultureInfo.InvariantCulture) + " s, one physics step "
                                                         + step.ToString("0.0000", CultureInfo.InvariantCulture) + " s; the sun's direction " + sunSpreadDeg.ToString("0.00000", CultureInfo.InvariantCulture)
                                                         + " degrees, the planet's centre " + centreSpreadM.ToString("0.000", CultureInfo.InvariantCulture) + " m")) yield break;

            // 5. The capture, all at this frame's end.
            string label = view.Name + (limit != null ? "-" + limit : "");
            string pictureVariant = variant + (limit != null ? "-" + limit : "");
            string tag = "pin-" + label + "-u" + ((long)utChosen).ToString(CultureInfo.InvariantCulture) + "-" + (StartId ?? "nostart") + "-"
                         + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            string dir = null;
            OwnCloudModule.PinCapture job = null;
            bool natural = false;
            string fault = null, screenFault = null;
            string screenName = T09Pictures.Name(StartId, view.Name, pictureVariant, Game.ScreenWidth, Game.ScreenHeight, "pinscreen", false);
            if (!screenName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) screenName += ".png";
            string screenPath = System.IO.Path.Combine(SeriesPictures.Folder, screenName);
            try
            {
                dir = System.IO.Path.Combine(System.IO.Path.Combine(SeriesPictures.Folder, "maps"), tag);
                job = own.BeginPinCapture(Game.EndOfFrameHost, dir, tag);
                natural = Game.CaptureBegin();
            }
            catch (Exception e)
            {
                fault = e.GetType().Name + " " + e.Message;
            }
            // The screen as the player sees it (after the upscaler, Scatterer and the post-processing), at the display size, written at this frame's end.
            try
            {
                System.IO.Directory.CreateDirectory(SeriesPictures.Folder);
                ScreenCapture.CaptureScreenshot(screenPath);
            }
            catch (Exception e)
            {
                screenFault = e.GetType().Name + " " + e.Message;
            }
            if (!PinStep("capture_started", fault == null && job != null && natural, fault ?? (natural ? "" : "the natural picture's read could not start"))) yield break;
            double readGiveUp = Elapsed + PinReadTimeoutS;
            while ((!job.Done || CaptureBusy(ref fault) || (screenFault == null && !System.IO.File.Exists(screenPath))) && Elapsed < readGiveUp) yield return ProbeWait.Frames(1);
            PinStep("screen_picture", screenFault == null && System.IO.File.Exists(screenPath), screenFault ?? screenName);
            PinEmit("frames_since_cut", job.FramesSinceCut);
            PinEmit("frame_number", job.FrameNumber);
            EmitText("T09.pin." + pinLabel + ".folder", dir);
            lastPinDir = dir;
            AtmospheresLog.Say("T09 pin: capture of " + label + " " + variant + " in " + dir + ": frame number " + job.FrameNumber + ", " + job.FramesSinceCut + " frames since the cut"
                               + (job.Problems.Count > 0 ? "; problems: " + string.Join("; ", job.Problems.ToArray()) : ""));
            PinStep("reads_done", job.Done, job.Done ? "" : "the reads did not finish in " + PinReadTimeoutS.ToString("0") + " s");
            PinStep("frames_since_cut", job.FramesSinceCut == PinFrames, job.FramesSinceCut + " records since the cut in the capture frame, wanted " + PinFrames);
            // The origin and the record in step: the cut's frame was number 0, so the PinFrames-th record is PinFrames - 1.
            PinStep("frame_number", job.FrameNumber == PinFrames - 1, "frame number " + job.FrameNumber + " in the capture frame, wanted " + (PinFrames - 1));
            PinStep("cloud_buffer", job.CloudWritten, job.CloudWritten ? "cloud.npy " + job.CloudWidth + "x" + job.CloudHeight : string.Join("; ", job.Problems.ToArray()));
            PinStep("depth", job.DepthWritten, job.DepthWritten ? "depth.npy " + job.DepthWidth + "x" + job.DepthHeight : string.Join("; ", job.Problems.ToArray()));
            PinStep("maps_pass_json", job.DumpWritten, job.DumpWritten ? "maps and pass.json" : string.Join("; ", job.Problems.ToArray()));
            PinStep("scene_before", job.SceneWritten, job.SceneWritten ? "scene.npy (the camera target before the clouds' composite)" : string.Join("; ", job.Problems.ToArray()));
            PinStep("scene_after", job.ComposedWritten, job.ComposedWritten ? "composed.npy (the camera target after the clouds' composite)" : string.Join("; ", job.Problems.ToArray()));
            T09Image image = null;
            if (fault == null)
            {
                try { image = Game.CaptureTake(); }
                catch (Exception e) { fault = "taking the read threw " + e.GetType().Name + " " + e.Message; }
            }
            bool pictureOk = fault == null && image != null && image.Pixels != null && image.Pixels.Length == image.Width * image.Height && image.Pixels.Length > 0;
            if (pictureOk)
            {
                try
                {
                    var rgba = new float[image.Pixels.Length * 4];
                    for (int i = 0; i < image.Pixels.Length; i++)
                    {
                        rgba[i * 4] = image.Pixels[i].r;
                        rgba[i * 4 + 1] = image.Pixels[i].g;
                        rgba[i * 4 + 2] = image.Pixels[i].b;
                        rgba[i * 4 + 3] = image.Pixels[i].a;
                    }
                    PinFiles.WriteFloat32(System.IO.Path.Combine(dir, "natural.npy"), image.Height, image.Width, 4, rgba);
                    Game.SavePicture(T09Pictures.Name(StartId, view.Name, pictureVariant, Game.ScreenWidth, Game.ScreenHeight, "pin", false), image, 1);
                }
                catch (Exception e)
                {
                    pictureOk = false;
                    fault = "writing the picture threw " + e.GetType().Name + " " + e.Message;
                }
            }
            PinStep("natural_picture", pictureOk, pictureOk ? "natural.npy " + image.Width + "x" + image.Height : fault ?? "the read gave no picture");
        }

        // -t09PinSeries <n> -t09PinAims <heading:pitch,heading:pitch,...> (edge flicker look, 2026-11-20): instead of the single capture, the camera is turned to each aim (compass
        // heading and elevation in degrees, the ground view's height and distance), the history is cut, PinFrames frames are held, and then n CONSECUTIVE frames are read back at
        // full size and written as PNGs (t09-<start>-edge-a<aim>-...-f<frame>.png), the clock and the noise pinned as in the single capture. The frames are kept in memory as
        // 8-bit colours while they are read (one read per frame, no encoding between) and written after the last one.
        private int PinSeriesFrames
        {
            get { return (int)NumberArg("-t09PinSeries", 0.0); }
        }

        private IEnumerable<ProbeWait> PinSeriesAims(View view, string variant, OwnCloudModule own)
        {
            string aimText = CommandLine.After(Args(), "-t09PinAims", true);
            var aims = new List<double[]>();
            foreach (string part in (aimText ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] hp = part.Split(':');
                double h, p;
                if (hp.Length == 2 && double.TryParse(hp[0], NumberStyles.Float, CultureInfo.InvariantCulture, out h) && double.TryParse(hp[1], NumberStyles.Float, CultureInfo.InvariantCulture, out p))
                    aims.Add(new[] { h, p });
            }
            if (!PinStep("series_aims", aims.Count > 0, "-t09PinAims '" + aimText + "' gave " + aims.Count + " aims")) yield break;
            int n = PinSeriesFrames;
            // -t09PinSeriesTune key=value;key=value (HIST2): tuning keys forced for the whole series (e.g. recon.rotate=3), two frames before the first aim so the frame's record has them.
            string tuneText = CommandLine.After(Args(), "-t09PinSeriesTune", true);
            if (!string.IsNullOrEmpty(tuneText))
            {
                foreach (string kv in tuneText.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] p = kv.Split('=');
                    float value;
                    if (!PinStep("series_tune", p.Length == 2 && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out value), "-t09PinSeriesTune '" + kv + "'")) yield break;
                    ForceTuning(p[0].Trim(), float.Parse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture));
                }
                yield return ProbeWait.Frames(2);
            }
            KSP.UI.UIMasterController ui = CommandLine.Has(Args(), "-t09HideUi", true) ? KSP.UI.UIMasterController.Instance : null;
            bool hid = ui != null && ui.IsUIShowing;
            if (hid)
            {
                ui.HideUI();
                yield return ProbeWait.Frames(2);
            }
            for (int a = 0; a < aims.Count; a++)
            {
                // -t09PinSeriesTurn <deg per frame> and -t09PinSeriesDolly <m per frame> (the history's motion pictures, HIST2 2026-11-20; 0 by default: the still series as before):
                // from the cut on, through the hold and the written frames, the camera's heading turns by that much every frame and the camera moves that far towards the vessel
                // along its view (the flight camera's distance falls from -t09PinSeriesDollyFrom, default the view's distance, never below 5 m).
                double turn = NumberArg("-t09PinSeriesTurn", 0.0), dolly = NumberArg("-t09PinSeriesDolly", 0.0);
                double dollyFrom = NumberArg("-t09PinSeriesDollyFrom", view.CameraDistM);
                foreach (ProbeWait w in Aim(aims[a][0], aims[a][1], dolly > 0.0 ? dollyFrom : view.CameraDistM)) yield return w;
                double heading0 = Game.CameraHeading * 180.0 / Math.PI, pitch0 = Game.CameraPitch * 180.0 / Math.PI;
                int moved = 0;
                Action moveStep = () =>
                {
                    if (turn != 0.0) Command(heading0 + turn * moved, pitch0);
                    if (dolly > 0.0) Game.SetCameraDistance(Math.Max(5.0, dollyFrom - dolly * moved));
                    moved++;
                };
                T09Pose pose = Game.ReadPose();
                AtmospheresLog.Say("T09 pin series: aim " + a + " wanted " + aims[a][0].ToString("0.0", CultureInfo.InvariantCulture) + "/" + aims[a][1].ToString("0.0", CultureInfo.InvariantCulture)
                                   + ", got " + pose.AzimuthDeg.ToString("0.0", CultureInfo.InvariantCulture) + "/" + pose.ElevationDeg.ToString("0.0", CultureInfo.InvariantCulture)
                                   + ", camera " + pose.CameraAgl.ToString("0", CultureInfo.InvariantCulture) + " m over the terrain" + (aimNote != null ? ", aim: " + aimNote : ""));
                double jitterGiveUp = Elapsed + PinJitterTimeoutS;
                while (!OwnCloudModule.HostJitterAtFirstPhase() && Elapsed < jitterGiveUp) yield return ProbeWait.Frames(1);
                own.PinNoise();
                int cutsBefore = own.HistoryCuts;
                own.FrameOrigin = Game.FrameCount;
                CloudBridgeLights.RequestHistoryReset("pinned series");
                try { new ApiBridge().RequestHistoryReset("ReDefinition pinned series"); }
                catch (Exception e) { AtmospheresLog.Say("T09 pin series: the host's history reset threw " + e.GetType().Name + " " + e.Message); }
                yield return ProbeWait.Frames(1);
                if (!PinStep("series_cut_a" + a, own.HistoryCuts > cutsBefore, "cuts " + cutsBefore + " -> " + own.HistoryCuts)) yield break;
                int cutsAfter = own.HistoryCuts;
                double giveUp = Elapsed + PinHoldTimeoutS;
                while (own.HistoryFramesSinceCut < PinFrames - 1 && own.HistoryCuts == cutsAfter && Elapsed < giveUp)
                {
                    moveStep();
                    yield return ProbeWait.Frames(1);
                }
                if (!PinStep("series_hold_a" + a, own.HistoryFramesSinceCut >= PinFrames - 1 && own.HistoryCuts == cutsAfter, "frames since the cut " + own.HistoryFramesSinceCut)) yield break;

                // The screen as the player sees it (after the upscaler): one screenshot request per frame, written at that frame's end, so the frames are consecutive.
                var frameNumbers = new List<string>();
                string fault = null;
                string stem = "t09-" + (StartId ?? "nostart") + "-edge-a" + a + "-h" + ((int)Math.Round(aims[a][0])).ToString(CultureInfo.InvariantCulture) + "p" + ((int)Math.Round(aims[a][1] * 10)).ToString(CultureInfo.InvariantCulture)
                              + (turn != 0.0 ? "-turn" + turn.ToString("0.###", CultureInfo.InvariantCulture) : "") + (dolly > 0.0 ? "-dolly" + dolly.ToString("0.###", CultureInfo.InvariantCulture) : "");
                int sw = Game.ScreenWidth, sh = Game.ScreenHeight;
                try { System.IO.Directory.CreateDirectory(SeriesPictures.Folder); }
                catch (Exception e) { fault = e.GetType().Name + " " + e.Message; }
                for (int i = 0; i < n && fault == null; i++)
                {
                    try
                    {
                        ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(SeriesPictures.Folder, stem + "-f" + i.ToString("00", CultureInfo.InvariantCulture) + "-" + sw + "x" + sh + ".png"));
                        frameNumbers.Add(Game.FrameCount.ToString(CultureInfo.InvariantCulture));
                    }
                    catch (Exception e) { fault = "the screenshot threw " + e.GetType().Name + " " + e.Message; }
                    moveStep();
                    yield return ProbeWait.Frames(1);
                }
                yield return ProbeWait.Frames(2);
                int written = 0;
                for (int i = 0; i < n; i++)
                {
                    if (System.IO.File.Exists(System.IO.Path.Combine(SeriesPictures.Folder, stem + "-f" + i.ToString("00", CultureInfo.InvariantCulture) + "-" + sw + "x" + sh + ".png"))) written++;
                }
                PinStep("series_frames_a" + a, fault == null && written == n, fault ?? (written + " of " + n + " screenshots written, requested in game frames " + string.Join(",", frameNumbers.ToArray())));
            }
            if (hid && KSP.UI.UIMasterController.Instance != null) KSP.UI.UIMasterController.Instance.ShowUI();
            PinCleanup();
            EndVariant();
            Game.StopHold();
        }
    }
}
