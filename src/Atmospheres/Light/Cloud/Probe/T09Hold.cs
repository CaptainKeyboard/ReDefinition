using System;

namespace ReDefinition.Atmospheres
{
    // What the hold needs of the vessel: a read of where it is (latitude, longitude, altitude of its centre of mass, which the game refreshes only after its next physics step),
    // a move to a body-fixed point, the distance between two such points and the ground's velocity. The game's side is KspT09World, the tests' side the fake world.
    internal interface IHoldVessel
    {
        void Read(out double lat, out double lon, out double alt);
        void Set(double lat, double lon, double alt);
        double Distance(double lat1, double lon1, double alt1, double lat2, double lon2, double alt2);
        void KeepVelocity();
    }

    // The hold of the K-T2 / K-T2d probe: after a placement it waits until the vessel really stands where it was placed (the read of the centre of mass lags the placement by
    // a physics step, so a read in the first frames is still the old position: start 43 anchored the old point, 85 m, and pulled the vessel back to it every frame), anchors
    // there, and puts the vessel back whenever it has moved away. A vessel that does not arrive is set again every few frames and, after GiveUpFrames, held at the placed point.
    internal sealed class T09Hold
    {
        internal const double ToleranceM = 5.0, ReachedM = 60.0;
        internal const int ResetEveryFrames = 10, GiveUpFrames = 120, LoggedCorrections = 3;

        private enum Phase { Idle, Placing, Holding }

        private readonly IHoldVessel vessel;
        private readonly Action<string> log;
        private Phase phase = Phase.Idle;
        private double targetLat, targetLon, targetAlt, anchorLat, anchorLon, anchorAlt;
        private int frames;
        private bool unreached;

        internal int Corrections { get; private set; }
        internal double WorstDriftM { get; private set; }

        internal T09Hold(IHoldVessel vessel, Action<string> log)
        {
            this.vessel = vessel;
            this.log = log ?? (m => { });
        }

        internal bool HasAnchor { get { return phase == Phase.Holding; } }
        // The vessel stands at the placed point and is held there.
        internal bool Reached { get { return phase == Phase.Holding && !unreached; } }

        internal string Status
        {
            get { return phase == Phase.Idle ? "idle" : phase == Phase.Placing ? "waiting for the placement (" + frames + " frames)" : unreached ? "placed point not reached, holding it" : "anchored"; }
        }

        private static string FormatPoint(double lat, double lon, double alt)
        {
            return "(" + lat.ToString("0.0000") + ", " + lon.ToString("0.0000") + ", " + alt.ToString("0.0") + " m)";
        }

        // After the placement moved the vessel to the point.
        internal void Begin(double lat, double lon, double alt)
        {
            targetLat = lat; targetLon = lon; targetAlt = alt;
            phase = Phase.Placing;
            frames = 0;
            unreached = false;
            Corrections = 0;
            WorstDriftM = 0.0;
        }

        internal double DriftM()
        {
            if (phase != Phase.Holding) return 0.0;
            double lat, lon, alt;
            vessel.Read(out lat, out lon, out alt);
            return vessel.Distance(lat, lon, alt, anchorLat, anchorLon, anchorAlt);
        }

        // Every frame.
        internal void Tick()
        {
            if (phase == Phase.Idle) return;
            double lat, lon, alt;
            vessel.Read(out lat, out lon, out alt);
            if (phase == Phase.Placing)
            {
                frames++;
                double away = vessel.Distance(lat, lon, alt, targetLat, targetLon, targetAlt);
                if (away <= ReachedM)
                {
                    // The vessel stands at the placed point: its own centre of mass there (a few metres off the transform the placement set) is the anchor.
                    anchorLat = lat; anchorLon = lon; anchorAlt = alt;
                    phase = Phase.Holding;
                    log("T09 hold anchor: " + FormatPoint(lat, lon, alt) + " reached after " + frames + " frames, " + away.ToString("0.0") + " m from the placed point " + FormatPoint(targetLat, targetLon, targetAlt));
                }
                else if (frames >= GiveUpFrames)
                {
                    // Not there after GiveUpFrames: hold the placed point itself (the pose check names what the vessel really did).
                    anchorLat = targetLat; anchorLon = targetLon; anchorAlt = targetAlt;
                    phase = Phase.Holding;
                    unreached = true;
                    log("T09 hold anchor: the vessel is still " + away.ToString("0.0") + " m from the placed point after " + frames + " frames (read " + FormatPoint(lat, lon, alt)
                        + "); holding the placed point " + FormatPoint(targetLat, targetLon, targetAlt));
                }
                else if (frames % ResetEveryFrames == 0)
                {
                    vessel.Set(targetLat, targetLon, targetAlt);
                    log("T09 hold anchor: the vessel reads " + away.ToString("0.0") + " m from the placed point after " + frames + " frames; placing it again");
                }
                vessel.KeepVelocity();
                return;
            }
            double drift = vessel.Distance(lat, lon, alt, anchorLat, anchorLon, anchorAlt);
            WorstDriftM = Math.Max(WorstDriftM, drift);
            if (drift > ToleranceM)
            {
                vessel.Set(anchorLat, anchorLon, anchorAlt);
                Corrections++;
                if (Corrections <= LoggedCorrections) log("T09 hold correction " + Corrections + ": drifted " + drift.ToString("0.0") + " m from the anchor, put back");
            }
            vessel.KeepVelocity();
        }
    }
}
