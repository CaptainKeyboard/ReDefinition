using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Series K-T2, the pinned capture (-t09Pin with -t09Ut <seconds>; master plan phase 0 item 3, .local/plans/wolken-m1-fakten.md section F): one own variant, the ground view
    // only, and instead of the timing window a capture in a state that a second start of the same commit and the weather player can reproduce:
    //   1. the universal time is pinned at the -t09Ut value (IT09World.PinClock: before every physics step and at every frame's end), cloud.motionScale 0 holds the noise,
    //      and its drift, rise and flow clock are set to 0 (OwnCloudModule.PinNoise);
    //   2. a map round with a snap and a height build are asked for at the pinned time, and the probe waits until the maps equal their goal and a height build finished;
    //   3. the frame origin is set to this frame and the history is cut in it (the clouds' reset counter, CloudBridgeLights, which the pass reads in this frame's record;
    //      ApiBridge.RequestHistoryReset besides, so that the upscaler's history starts with it);
    //   4. the pass records PinFrames frames from the cut; in the frame of the last one the clouds' own output before the composite (cloud.npy, linear float32), the scene
    //      depth the pass read (depth.npy), the natural picture (natural.npy, linear, and the PNG) and the maps with pass.json are taken at that frame's end.
    // Every step is a row T09.pin.<step> and a check; a step that cannot be done fails the start by name. The files land in series-pictures/maps/<tag> (tag pin-ground-u<ut>-<start>-<time>).
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
        }

        // A step of the pinned capture: a row and a check; false ends the capture (the check has failed the start).
        private bool PinStep(string name, bool ok, string detail)
        {
            Emit("T09.pin." + name, ok);
            Check("T09.pin." + name, ok, detail);
            return ok;
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

            View view = Views[0];
            foreach (ProbeWait w in PlaceChecked(siteLat, siteLon, view)) yield return w;
            if (!PinStep("placed", placeOk, "the " + view.Name + " view")) yield break;
            foreach (ProbeWait w in StableGate(view, variant)) yield return w;
            pinActive = true;
            foreach (ProbeWait w in PinCaptureSteps(view, variant)) yield return w;
            PinCleanup();
            EndVariant();
            Game.StopHold();
        }

        private IEnumerable<ProbeWait> PinCaptureSteps(View view, string variant)
        {
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
            Emit("T09.pin.frame_origin", own.FrameOrigin);
            yield return ProbeWait.Frames(1);
            if (!PinStep("history_cut", own.HistoryCuts > cutsBefore && own.HistoryFramesSinceCut == 1,
                         "cuts " + cutsBefore + " -> " + own.HistoryCuts + ", frames since the cut " + own.HistoryFramesSinceCut)) yield break;
            int cutsAfter = own.HistoryCuts;

            // 4. The hold: the probe ticks before the frame's record, so at PinFrames - 1 the record of this frame is the PinFrames-th.
            // Each tick reads what the frame before it filled: the time the module's tick took, the sun's direction and the planet's centre of its record.
            giveUp = Elapsed + PinHoldTimeoutS;
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
            Emit("T09.pin.ut_spread_s", utSpread);
            Emit("T09.pin.sun_spread_deg", sunSpreadDeg);
            Emit("T09.pin.centre_spread_m", centreSpreadM);
            // The planet's centre moves with the floating origin and the hold; it is written into pass.json and the replay takes the capture frame's, so it is a row, not a check.
            if (!PinStep("ut_spread", utSpread <= step, "the time the module read over the hold spread " + utSpread.ToString("0.0000", CultureInfo.InvariantCulture) + " s, one physics step "
                                                         + step.ToString("0.0000", CultureInfo.InvariantCulture) + " s; the sun's direction " + sunSpreadDeg.ToString("0.00000", CultureInfo.InvariantCulture)
                                                         + " degrees, the planet's centre " + centreSpreadM.ToString("0.000", CultureInfo.InvariantCulture) + " m")) yield break;

            // 5. The capture, all at this frame's end.
            string tag = "pin-" + view.Name + "-u" + ((long)utChosen).ToString(CultureInfo.InvariantCulture) + "-" + (StartId ?? "nostart") + "-"
                         + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            string dir = null;
            OwnCloudModule.PinCapture job = null;
            bool natural = false;
            string fault = null;
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
            if (!PinStep("capture_started", fault == null && job != null && natural, fault ?? (natural ? "" : "the natural picture's read could not start"))) yield break;
            giveUp = Elapsed + PinReadTimeoutS;
            while ((!job.Done || CaptureBusy(ref fault)) && Elapsed < giveUp) yield return ProbeWait.Frames(1);
            Emit("T09.pin.frames_since_cut", job.FramesSinceCut);
            Emit("T09.pin.frame_number", job.FrameNumber);
            EmitText("T09.pin.folder", dir);
            AtmospheresLog.Say("T09 pin: capture of " + view.Name + " " + variant + " in " + dir + ": frame number " + job.FrameNumber + ", " + job.FramesSinceCut + " frames since the cut"
                               + (job.Problems.Count > 0 ? "; problems: " + string.Join("; ", job.Problems.ToArray()) : ""));
            PinStep("reads_done", job.Done, job.Done ? "" : "the reads did not finish in " + PinReadTimeoutS.ToString("0") + " s");
            PinStep("frames_since_cut", job.FramesSinceCut == PinFrames, job.FramesSinceCut + " records since the cut in the capture frame, wanted " + PinFrames);
            // The origin and the record in step: the cut's frame was number 0, so the PinFrames-th record is PinFrames - 1.
            PinStep("frame_number", job.FrameNumber == PinFrames - 1, "frame number " + job.FrameNumber + " in the capture frame, wanted " + (PinFrames - 1));
            PinStep("cloud_buffer", job.CloudWritten, job.CloudWritten ? "cloud.npy " + job.CloudWidth + "x" + job.CloudHeight : string.Join("; ", job.Problems.ToArray()));
            PinStep("depth", job.DepthWritten, job.DepthWritten ? "depth.npy " + job.DepthWidth + "x" + job.DepthHeight : string.Join("; ", job.Problems.ToArray()));
            PinStep("maps_pass_json", job.DumpWritten, job.DumpWritten ? "maps and pass.json" : string.Join("; ", job.Problems.ToArray()));
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
                    Game.SavePicture(T09Pictures.Name(StartId, view.Name, variant, Game.ScreenWidth, Game.ScreenHeight, "pin", false), image, 1);
                }
                catch (Exception e)
                {
                    pictureOk = false;
                    fault = "writing the picture threw " + e.GetType().Name + " " + e.Message;
                }
            }
            PinStep("natural_picture", pictureOk, pictureOk ? "natural.npy " + image.Width + "x" + image.Height : fault ?? "the read gave no picture");
        }
    }
}
