using System;
using System.Collections.Generic;
using System.Globalization;

namespace ReDefinition.Atmospheres
{
    // Series K-T2, the starts hang_<steps> (the driver resets, 2026-11-17): five perf starts in a row lost the device at the first placement from the ground view to the inside view
    // after the variant off (the own pass off, every view visited) had been followed by plain; 240 plain jumps between the same two views did not. This scenario replays a sequence of
    // variant switches and placements, so that the step that makes the reset can be found by leaving steps out. The id after "hang_" is the sequence, one letter a step:
    //   o: the variant off, p: the variant plain (each followed by 4 s and, for plain, the pass's steady frames);
    //   G, I, A, S: the placement to the ground, inside, above, sky3 view (each held HangHoldS after its stable gate);
    //   n, s, c: the light march, the cloud shadow, the single clouds switched off from that step on (to find the part of the pass that hangs).
    // After every step the device is read back (GpuDeviceAlive); the first loss fails the start with the step's number and letter, and the scenario ends. Log, one line per step:
    // "Probe: hang step <i> <letter> ok|lost real <clock> worst_frame_ms <longest frame of the step>".
    internal sealed partial class T09Probe
    {
        // The reset came in the first frames after a placement (a 3 s frame), so a step holds 2 s; a variant switch waits 1 s and 30 frames of the pass, the maps 5 s (owner 2026-11-17: the pauses
        // between the steps were too long).
        private const double HangHoldS = 2.0, HangSwitchS = 1.0, HangMapSettleS = 5.0;
        private const int HangSteadyFrames = 30;

        private bool HangStart
        {
            get { return StartId != null && StartId.StartsWith("hang_", StringComparison.Ordinal); }
        }

        private IEnumerator<ProbeWait> HangScenario()
        {
            foreach (ProbeWait w in Prelude()) yield return w;
            if (!preludeOk) yield break;
            double siteLat = Game.VesselLatitude, siteLon = Game.VesselLongitude;
            foreach (ProbeWait w in ChooseUt(Game.BodyIndex, siteLat, siteLon)) yield return w;
            if (!utOk)
            {
                Fail(utFailure);
                yield break;
            }
            Emit("T09.ut", utChosen);
            yield return ProbeWait.Seconds(HangMapSettleS);
            // The steps end at a second underscore: what follows names a variant of the start's arguments (hang_oGpGI_noprof: without the GPU profiler).
            string steps = StartId.Substring("hang_".Length);
            int tail = steps.IndexOf('_');
            if (tail >= 0) steps = steps.Substring(0, tail);
            string variant = "off";
            for (int i = 0; i < steps.Length; i++)
            {
                char c = steps[i];
                string clock = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                jumpWorstMs = 0.0;
                jumpSampling = true;
                // Parts of the pass switched off for the rest of the start: n the light march (layers.costDebug 1), s the cloud shadow (cloudShadows 0), c the single clouds (cloud.cells 0).
                if (c == 'n' || c == 's' || c == 'c')
                {
                    if (c == 'n') ForceTuning(OwnCloudOptions.TuneCostDebug, 1f);
                    else if (c == 's') SetSetting(AtmospheresSettings.Keys.CloudShadows, 0.0);
                    else ForceTuning(CloudLookTuning.TuneCells, 0f);
                    yield return ProbeWait.Seconds(HangSwitchS);
                }
                else if (c == 'o' || c == 'p')
                {
                    variant = c == 'o' ? "off" : "plain";
                    BeginVariant(variant);
                    yield return ProbeWait.Seconds(HangSwitchS);
                    if (IsOwn(variant))
                    {
                        OwnCloudModule m = OwnCloudModule.Current;
                        int drawnFrom = m != null ? m.Stats.FramesDrawn : 0;
                        yield return ProbeWait.Until(() =>
                        {
                            OwnCloudModule o = OwnCloudModule.Current;
                            return o != null && o.Stats.Mode != "off" && o.Stats.FramesDrawn - drawnFrom > HangSteadyFrames;
                        }, 150, "the own pass to draw a steady stretch (" + variant + ")");
                    }
                }
                else
                {
                    int index = c == 'G' ? 0 : c == 'I' ? 1 : c == 'A' ? 2 : c == 'S' ? 4 : -1;
                    if (index < 0)
                    {
                        Fail("hang start: unknown step '" + c + "' in " + steps);
                        yield break;
                    }
                    View view = Views[index];
                    foreach (ProbeWait w in PlaceChecked(siteLat, siteLon, view)) yield return w;
                    foreach (ProbeWait w in StableGate(view, variant)) yield return w;
                    double from = Elapsed;
                    while (Elapsed - from < HangHoldS)
                    {
                        JumpSample();
                        yield return ProbeWait.Frames(1);
                    }
                }
                jumpSampling = false;
                bool alive = Game.GpuDeviceAlive();
                string line = "hang step " + (i + 1) + " " + c + " " + (alive ? "ok" : "lost") + " real " + clock + " worst_frame_ms " + jumpWorstMs.ToString("0.0", CultureInfo.InvariantCulture);
                LogLine("Probe: " + line);
                AtmospheresLog.Say("T09 " + line);
                if (!alive)
                {
                    Emit("T09.hang.lost_step", i + 1);
                    Fail("the graphics device was removed (driver reset) at step " + (i + 1) + " '" + c + "' of " + steps);
                    yield break;
                }
            }
            Game.StopHold();
            Emit("T09.hang.lost_step", 0);
        }
    }
}
