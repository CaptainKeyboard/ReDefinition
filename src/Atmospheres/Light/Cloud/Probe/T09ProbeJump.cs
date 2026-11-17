using System;
using System.Collections.Generic;
using System.Globalization;

namespace ReDefinition.Atmospheres
{
    // Series K-T2, the starts jump* (a stress test of the placement, 2026-11-17): the GPU driver reset three times (Windows nvlddmkm event 153, Unity "D3D11: Failed to create
    // RenderTexture ... 0x887a0005") in the same second as the placement from the ground view (200 m) to the inside view (4550 m), PlaceVessel (SetPosition, floating-origin
    // shift, Krakensbane reset, parts kinematic). This scenario does nothing but that jump, again and again, for ONE variant (the start's first: "off" with no clouds from us,
    // "plain" with ours), so that the rate of resets with and without our clouds can be compared:
    //   place ground, hold JumpHoldS, then inside, ground, inside ... until the budget of the probe is spent (JumpMaxCount at most).
    // The placement is the standard one (PlaceChecked, StableGate, the kinematic hold as it is); no pictures, no noise reading, no profile windows.
    // Log, one line per jump: "Probe: jump <index> <from>-><to> frame <Time.frameCount> real <clock> t <probe seconds> worst_frame_ms <longest frame in the JumpWatchS after it>"
    // (read once per frame in the placement's first 20 frames after SetPosition, where the reset happened, and while the 3 s are not yet over after the placement returns). Rows: T09.jump.count, T09.jump.worst_frame_ms (the largest of all).
    internal sealed partial class T09Probe
    {
        private const double JumpHoldS = 8.0, JumpWatchS = 3.0, JumpReserveS = 150.0;
        private const int JumpMaxCount = 80;

        private bool JumpStart
        {
            get { return StartId != null && StartId.StartsWith("jump", StringComparison.Ordinal); }
        }

        // The longest frame since JumpResetWorst, read once per frame while the placement waits (Place) and after it.
        private double jumpWorstMs;
        private bool jumpSampling;

        private void JumpSample()
        {
            if (jumpSampling) jumpWorstMs = Math.Max(jumpWorstMs, Game.LastFrameMs);
        }

        private IEnumerator<ProbeWait> JumpScenario()
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
            Emit("T09.cover", coverChosen);
            yield return ProbeWait.Seconds(MapSettleS);
            if (Variants.Count == 0)
            {
                Fail("the jump start has no variant");
                yield break;
            }

            string variant = Variants[0];
            BeginVariant(variant);
            CheckVariantGates(variant);
            yield return ProbeWait.Seconds(4);
            if (IsOwn(variant))
            {
                OwnCloudModule m = OwnCloudModule.Current;
                int drawnFrom = m != null ? m.Stats.FramesDrawn : 0;
                yield return ProbeWait.Until(() =>
                {
                    OwnCloudModule o = OwnCloudModule.Current;
                    return o != null && o.Stats.Mode != "off" && o.Stats.FramesDrawn - drawnFrom > SteadyFrames;
                }, 150, "the own pass to draw a steady stretch (" + variant + ")");
            }

            View ground = Views[0], inside = Views[1];
            // The first placement (to the ground view) is the start, not a jump.
            foreach (ProbeWait w in PlaceChecked(siteLat, siteLon, ground)) yield return w;
            foreach (ProbeWait w in StableGate(ground, variant)) yield return w;
            yield return ProbeWait.Seconds(JumpHoldS);

            int made = 0;
            double worstAll = 0.0;
            for (int i = 1; i <= JumpMaxCount && Elapsed + JumpHoldS + JumpReserveS < MaxSeconds; i++)
            {
                bool up = i % 2 == 1;
                View from = up ? ground : inside, to = up ? inside : ground;
                jumpWorstMs = 0.0;
                jumpSampling = true;
                int frameAtJump = Game.FrameCount;
                double atJump = Elapsed;
                string clock = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                foreach (ProbeWait w in PlaceChecked(siteLat, siteLon, to)) yield return w;
                bool ok = placeOk;
                foreach (ProbeWait w in StableGate(to, variant)) yield return w;
                // The rest of the watch window: one read per frame until JumpWatchS after the jump.
                while (Elapsed - atJump < JumpWatchS)
                {
                    JumpSample();
                    yield return ProbeWait.Frames(1);
                }
                jumpSampling = false;
                worstAll = Math.Max(worstAll, jumpWorstMs);
                if (ok) made++;
                string line = "jump " + i + " " + from.Name + "->" + to.Name + " frame " + frameAtJump + " real " + clock + " t " + atJump.ToString("0.0", CultureInfo.InvariantCulture)
                              + " worst_frame_ms " + jumpWorstMs.ToString("0.0", CultureInfo.InvariantCulture) + (ok ? "" : " (placement failed)");
                LogLine("Probe: " + line);
                AtmospheresLog.Say("T09 " + line);
                if (!Game.GpuDeviceAlive())
                {
                    Fail("the graphics device was removed (driver reset) at jump " + i + " " + from.Name + "->" + to.Name + " (real " + clock + ")");
                    break;
                }
                double rest = JumpHoldS - (Elapsed - atJump);
                // The hold is counted from the end of the placement; the watch window is part of it.
                if (rest > 0.0) yield return ProbeWait.Seconds(rest);
            }
            Game.StopHold();
            Emit("T09.jump.count", made);
            Emit("T09.jump.worst_frame_ms", worstAll);
        }
    }
}
