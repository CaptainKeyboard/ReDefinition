using System;

namespace ReDefinition.Atmospheres.Viewer
{
    // Where the viewer's camera looks, as the game's FlightCamera takes it. The first guess of the free camera's heading is the compass heading itself (K-V1 start, 2026-11-15:
    // camHdg 270 degrees read back as azimuth -90, camHdg 380 as 20: the camera looks along camHdg; the K-T2 comment "behind the vessel, heading + 180" was the opposite way, which
    // K-T2's closed loop corrects). The guess is a start only: AimTuner measures the camera's real aim and turns the heading by what is missing, as K-T2 does, so that another
    // frame of the camera (the vessel's turn, the mode) cannot leave the view looking away from the pose.
    internal static class ViewerAim
    {
        // The camera's heading in radians for a compass azimuth of the view, and its pitch for an elevation above the horizon (the pitch is the elevation's opposite).
        internal static float HeadingRad(double azimuthDeg)
        {
            return (float)(Wrap(azimuthDeg) * Math.PI / 180.0);
        }

        internal static float PitchRad(double elevationDeg)
        {
            return (float)(-elevationDeg * Math.PI / 180.0);
        }

        // An angle in degrees brought to [-180, 180).
        internal static double Wrap(double deg)
        {
            double d = (deg + 180.0) % 360.0;
            if (d < 0.0) d += 360.0;
            return d - 180.0;
        }
    }

    // Turns the camera's heading and pitch until the aim read back from the camera is the one asked for. One step per settled reading: the camera follows its heading and pitch
    // with a lag, so an aim read right after a command is the old one and a correction made from it winds up (K-T2 SettleAim). A step turns each axis by the error; where the error
    // grew after a step the axis's sign is reversed (a camera whose azimuth runs against its heading, a pitch that is not the elevation's opposite).
    internal sealed class AimTuner
    {
        internal const double ToleranceDeg = 0.5, StillDeg = 0.05, FlatElevationDeg = 80.0, GrewByDeg = 1.0;
        internal const int MaxSteps = 12, MaxFrames = 300, StillGapFrames = 2;

        private double targetAz, targetEl, lastAz = double.NaN, lastEl = double.NaN;
        // The change in azimuth and in elevation the last step meant to make, and the aim it was made from.
        private double meantAz, meantEl, azAtCommand, elAtCommand;
        private double signAz = 1.0, signEl = -1.0;
        private int frames, sinceRead;
        private bool corrected;

        internal AimTuner(double azimuthDeg, double elevationDeg)
        {
            targetAz = ViewerAim.Wrap(azimuthDeg);
            targetEl = elevationDeg;
        }

        internal int Steps { get; private set; }
        internal bool Done { get; private set; }
        // Why the tuner stopped without reaching the aim (null where it reached it or is still turning).
        internal string Note { get; private set; }
        internal double ErrorAzDeg { get; private set; }
        internal double ErrorElDeg { get; private set; }

        // One frame with the aim the camera reads now. Returns the turn to make (degrees on the heading and on the pitch; the caller adds them) or false where there is none.
        internal bool Frame(double readAzDeg, double readElDeg, out double headingDeg, out double pitchDeg)
        {
            headingDeg = pitchDeg = 0.0;
            if (Done) return false;
            if (++frames > MaxFrames)
            {
                Finish("the aim did not settle within " + MaxFrames + " frames");
                return false;
            }
            // Two readings StillGapFrames frames apart that agree: the camera has stopped.
            if (++sinceRead < StillGapFrames) return false;
            sinceRead = 0;
            bool still = !double.IsNaN(lastAz) && Math.Abs(ViewerAim.Wrap(readAzDeg - lastAz)) <= StillDeg && Math.Abs(readElDeg - lastEl) <= StillDeg;
            lastAz = readAzDeg;
            lastEl = readElDeg;
            if (!still) return false;
            bool useAz = Math.Abs(targetEl) < FlatElevationDeg;
            double errAz = useAz ? ViewerAim.Wrap(targetAz - readAzDeg) : 0.0, errEl = targetEl - readElDeg;
            ErrorAzDeg = errAz;
            ErrorElDeg = errEl;
            if (Math.Abs(errAz) <= ToleranceDeg && Math.Abs(errEl) <= ToleranceDeg)
            {
                Done = true;
                return false;
            }
            if (Steps >= MaxSteps)
            {
                Finish("the aim is " + readAzDeg.ToString("0.0") + " / " + readElDeg.ToString("0.0") + " after " + Steps + " steps, wanted " + targetAz.ToString("0.0") + " / " + targetEl.ToString("0.0"));
                return false;
            }
            // What the step before did: an axis that moved against the change the step meant (by more than a quarter of it) is turned the other way from now on. An axis that did
            // not move (a pitch at its limit) keeps its sign.
            if (corrected)
            {
                if (Math.Abs(meantAz) >= GrewByDeg && ViewerAim.Wrap(readAzDeg - azAtCommand) / meantAz < -0.25) signAz = -signAz;
                if (Math.Abs(meantEl) >= GrewByDeg && (readElDeg - elAtCommand) / meantEl < -0.25) signEl = -signEl;
            }
            headingDeg = signAz * errAz;
            pitchDeg = signEl * errEl;
            meantAz = errAz;
            meantEl = errEl;
            azAtCommand = readAzDeg;
            elAtCommand = readElDeg;
            corrected = true;
            Steps++;
            return true;
        }

        private void Finish(string note)
        {
            Done = true;
            Note = note;
        }
    }
}
