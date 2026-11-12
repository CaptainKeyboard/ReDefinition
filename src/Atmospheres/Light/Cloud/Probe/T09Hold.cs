using System;

namespace ReDefinition.Atmospheres
{
    // What the hold needs of the vessel: a read of where it is (latitude, longitude, altitude of its centre of mass, which the game refreshes only after its next physics step),
    // a move of its transform to a body-fixed point, the distance between two such points and the ground's velocity. The game's side is KspT09World, the tests' side the fake world.
    internal interface IHoldVessel
    {
        void Read(out double lat, out double lon, out double alt);
        void Set(double lat, double lon, double alt);
        double Distance(double lat1, double lon1, double alt1, double lat2, double lon2, double alt2);
        void KeepVelocity();
    }

    // The hold of the K-T2 / K-T2d probe. The vessel is read at its centre of mass but moved by its transform, a constant 9.35 m apart (start 44: every ground view read exactly
    // 9.34 to 9.35 m from the point the hold had just set), and the read lags a set by a physics step or more. So the hold keeps a command: the position given to Set, which starts
    // at the target and is corrected by what a settled read says (command += target - read), so that the centre of mass, not the transform, lands on the target. A read counts only
    // when it is settled (no set since SettleFrames frames and the last reads agree within SettleToleranceM); a placement is accepted only when a settled read is within ToleranceM
    // of the target, never because an old read happened to be near (start 44 accepted anchors 3.5 to 40.5 m away because the old position was inside the 60 m radius, and each
    // new attempt sank by the offset). The command is set again before every physics step (FixedTick) with the velocity at the ground's, since the game left the vessel moving
    // 3.5 m (175 m/s, the surface turning under it) per step between two per-frame ticks.
    internal sealed class T09Hold
    {
        // The accepted distance from the target, the distance below which a settled read needs no new command, and the agreement of consecutive reads that counts as settled.
        internal const double ToleranceM = 5.0, SettleToleranceM = 2.0, ReadAgreementM = 1.0;
        // Frames between two commands (the read must have caught up: the game's lag is 1 to 2 frames, the fake's 8), frames of stable reads a capture waits for,
        // frames after which a vessel that has not arrived is reported unreached (the hold goes on correcting), the corrections that are logged, the trace every N frames.
        internal const int CommandEveryFrames = 10, StableNeededFrames = 30, GiveUpFrames = 150, LoggedCorrections = 3, TraceEveryFrames = 120, MaxTraceLines = 24;

        private enum Phase { Idle, Placing, Holding }

        private readonly IHoldVessel vessel;
        private readonly Action<string> log;
        private Phase phase = Phase.Idle;
        private double targetLat, targetLon, targetAlt, commandLat, commandLon, commandAlt;
        private double lastLat, lastLon, lastAlt;
        private bool haveLast;
        private int frames, sinceCommand, agreeing, stableFrames, traceLines;
        private bool unreached;

        internal int Corrections { get; private set; }
        internal double WorstDriftM { get; private set; }

        internal T09Hold(IHoldVessel vessel, Action<string> log)
        {
            this.vessel = vessel;
            this.log = log ?? (m => { });
        }

        internal bool HasAnchor { get { return phase == Phase.Holding; } }
        // The vessel stands at the placed point (a settled read within ToleranceM) and is held there.
        internal bool Reached { get { return phase == Phase.Holding && !unreached; } }
        // The vessel has been within ToleranceM for StableNeededFrames frames in a row: a picture may be taken.
        internal bool Stable { get { return phase == Phase.Holding && !unreached && stableFrames >= StableNeededFrames; } }
        internal int StableFrames { get { return stableFrames; } }

        internal string Status
        {
            get
            {
                return phase == Phase.Idle ? "idle" : phase == Phase.Placing ? "waiting for the placement (" + frames + " frames)"
                    : unreached ? "placed point not reached, holding it" : stableFrames >= StableNeededFrames ? "anchored" : "anchored, settling (" + stableFrames + " frames)";
            }
        }

        private static string FormatPoint(double lat, double lon, double alt)
        {
            return "(" + lat.ToString("0.0000") + ", " + lon.ToString("0.0000") + ", " + alt.ToString("0.0") + " m)";
        }

        private static double WrapLon(double d)
        {
            while (d > 180.0) d -= 360.0;
            while (d < -180.0) d += 360.0;
            return d;
        }

        // After the placement moved the vessel's transform to the point.
        internal void Begin(double lat, double lon, double alt)
        {
            targetLat = lat; targetLon = lon; targetAlt = alt;
            commandLat = lat; commandLon = lon; commandAlt = alt;
            phase = Phase.Placing;
            frames = 0;
            sinceCommand = 0;
            agreeing = 0;
            stableFrames = 0;
            haveLast = false;
            unreached = false;
            Corrections = 0;
            WorstDriftM = 0.0;
            traceLines = 0;
        }

        internal double DriftM()
        {
            if (phase != Phase.Holding) return 0.0;
            double lat, lon, alt;
            vessel.Read(out lat, out lon, out alt);
            return vessel.Distance(lat, lon, alt, targetLat, targetLon, targetAlt);
        }

        // Before every physics step (the game's FixedUpdate): the command again and the ground's velocity, so that the vessel starts each step at the point.
        internal void FixedTick()
        {
            if (phase == Phase.Idle) return;
            vessel.Set(commandLat, commandLon, commandAlt);
            vessel.KeepVelocity();
        }

        // Every frame.
        internal void Tick()
        {
            if (phase == Phase.Idle) return;
            double lat, lon, alt;
            vessel.Read(out lat, out lon, out alt);
            frames++;
            sinceCommand++;
            double away = vessel.Distance(lat, lon, alt, targetLat, targetLon, targetAlt);
            // Settled: this read agrees with the one before.
            if (haveLast && vessel.Distance(lat, lon, alt, lastLat, lastLon, lastAlt) <= ReadAgreementM) agreeing++;
            else agreeing = 0;
            lastLat = lat; lastLon = lon; lastAlt = alt; haveLast = true;
            bool settled = sinceCommand >= CommandEveryFrames && agreeing >= 2;

            if (phase == Phase.Placing)
            {
                if (settled && away <= ToleranceM)
                {
                    phase = Phase.Holding;
                    stableFrames = 0;
                    log("T09 hold anchor: " + FormatPoint(lat, lon, alt) + " reached after " + frames + " frames, " + away.ToString("0.0") + " m from the placed point " + FormatPoint(targetLat, targetLon, targetAlt));
                }
                else if (frames >= GiveUpFrames)
                {
                    phase = Phase.Holding;
                    unreached = true;
                    stableFrames = 0;
                    log("T09 hold anchor: the vessel is still " + away.ToString("0.0") + " m from the placed point after " + frames + " frames (read " + FormatPoint(lat, lon, alt)
                        + "); holding the placed point " + FormatPoint(targetLat, targetLon, targetAlt));
                }
                else if (settled && away > SettleToleranceM)
                {
                    Command(lat, lon, alt);
                    log("T09 hold anchor: the vessel reads " + away.ToString("0.0") + " m from the placed point after " + frames + " frames; placing it again");
                }
                vessel.KeepVelocity();
                return;
            }

            WorstDriftM = Math.Max(WorstDriftM, away);
            if (away <= ToleranceM) stableFrames++;
            else stableFrames = 0;
            if (settled && away > SettleToleranceM)
            {
                Command(lat, lon, alt);
                Corrections++;
                if (Corrections <= LoggedCorrections) log("T09 hold correction " + Corrections + ": drifted " + away.ToString("0.0") + " m from the point, command moved");
            }
            else if (unreached && settled && away <= ToleranceM)
            {
                unreached = false;
                log("T09 hold anchor: reached after " + frames + " frames, " + away.ToString("0.0") + " m from the placed point");
            }
            if ((frames % TraceEveryFrames == 0 || away > 50.0) && traceLines < MaxTraceLines)
            {
                traceLines++;
                log("T09 hold trace: frame " + frames + ", " + away.ToString("0.0") + " m from the point, stable " + stableFrames + " frames, corrections " + Corrections + ", worst "
                    + WorstDriftM.ToString("0.0") + " m, command off the target by " + vessel.Distance(commandLat, commandLon, commandAlt, targetLat, targetLon, targetAlt).ToString("0.0") + " m");
            }
            vessel.KeepVelocity();
        }

        // The command moves by what the settled read is away from the target, and is set at once.
        private void Command(double lat, double lon, double alt)
        {
            commandLat += targetLat - lat;
            commandLon += WrapLon(targetLon - lon);
            commandAlt += targetAlt - alt;
            vessel.Set(commandLat, commandLon, commandAlt);
            sinceCommand = 0;
            agreeing = 0;
            stableFrames = 0;
        }
    }
}
