using System;
using System.Collections.Generic;
using ReDefinition.Engineer;
using ReDefinition.Hud.Readouts;

namespace ReDefinition.Hud
{
    // A value as the round gauge shows it. It holds its text (null for none) and where it
    // stands on the mode's scale (0 to 1). It holds its caution and warning bands on that
    // scale (from, to; null for none) and a limit's tick (NaN for none). Its level is 0 fine,
    // 1 in the caution band, 2 in the warning band.
    //
    // The gauge follows FAA AC 23.1311-1C for an electronic dial (9.4, 17.6, 22.7).
    // The digits and the arc give the same value, the arc on a scale fixed for the mode, so
    // that the margin to a limit shows at a glance. The limits are marks on the
    // scale, amber for caution and red for warning. A limit that moves with
    // the flight (the moment to brake, a safe closing speed) is a tick on it. The
    // arc is white while the value is fine, amber or red inside those bands. Where
    // the values span a wide range, the scale is logarithmic, as a vertical speed
    // indicator's is: fine near zero, compressed high up. The arc moves to a new
    // value within a fraction of a second.
    internal sealed class GaugeReading
    {
        // In the order the class's comment names them; Level 0, 1 or 2.
        internal string Text;
        internal double Fill;
        internal double[] Caution;
        internal double[] Warning;
        internal double Mark = double.NaN;
        internal int Level;
    }

    // One engine of a stage as the gauge counts it: the propellant it can reach,
    // now and with full tanks, in tonnes, and its full fuel flow in tonnes per
    // second.
    internal struct EngineFuel
    {
        // The part's name, for the log; the propellant now and full, in tonnes; the flow, in tonnes per second.
        internal string Name;
        internal double Left;
        internal double Full;
        internal double Flow;

        // How long it burns on what it can reach, at full thrust.
        internal double Seconds
        {
            get { return Flow > 0.0 ? Left / Flow : double.PositiveInfinity; }
        }
    }

    // The round gauge's figures from plain numbers (RoundGauge reads them from
    // the game): what the digits say and where the arc stands, on scales that are
    // fixed for each figure. Free of Unity, tested (GaugeReadingTests) against a
    // simulated flight.
    internal static class GaugeReadings
    {
        // The bands that do not change, made once: a reading is made every frame.
        // Read only.
        private static readonly double[] TenthToQuarter = { 0.1, 0.25 };
        private static readonly double[] LastTenth = { 0.0, 0.1 };
        private static readonly double[] LastTenSeconds = { 0.0, TimeScale(10.0) };

        // Seconds on a logarithmic scale up to ten minutes: a minute at 64 %, ten
        // seconds at 37 %.
        internal static double TimeScale(double seconds)
        {
            return Math.Min(1.0, Math.Log(1.0 + Math.Max(0.0, seconds)) / Math.Log(601.0));
        }

        // Metres per second on a logarithmic scale up to 100 m/s: 1 m/s at 35 %,
        // 10 m/s at 67 %.
        internal static double SpeedScale(double speed)
        {
            return Math.Min(1.0, Math.Log10(1.0 + Math.Max(0.0, speed) / 0.1) / Math.Log10(1001.0));
        }

        // Which of a stage's engines its burn time is counted for: the one whose
        // own time left is nearest to it; -1 for none. A stage that ends as its
        // boosters burn out is timed by them, not by the core that burns on.
        internal static int Timed(IList<EngineFuel> engines, double burnTime)
        {
            int timed = -1;
            double nearest = double.MaxValue;
            for (int i = 0; i < engines.Count; i++)
            {
                if (engines[i].Flow <= 0.0 || engines[i].Full <= 0.0) continue;
                double off = Math.Abs(engines[i].Seconds - burnTime);
                if (off >= nearest) continue;
                nearest = off;
                timed = i;
            }
            return timed;
        }

        // The stage's burn time left at full thrust. The scale: the tanks of the
        // engine that time is counted for, empty to full as built, so that the arc
        // runs out with the digits. Caution below a quarter, warning below a
        // tenth.
        internal static GaugeReading Stage(IList<EngineFuel> engines, double burnTime)
        {
            int timed = burnTime > 0.0 ? Timed(engines, burnTime) : -1;
            if (timed < 0) return null;
            double share = engines[timed].Left / engines[timed].Full;
            return new GaugeReading
            {
                Text = EngineerFormat.Time(burnTime),
                Fill = share,
                Caution = TenthToQuarter,
                Warning = LastTenth,
                Level = share < 0.1 ? 2 : share < 0.25 ? 1 : 0,
            };
        }

        // Before a burn: the time until it starts, on the time scale; caution in
        // the last ten seconds.
        internal static GaugeReading Countdown(double until)
        {
            return new GaugeReading
            {
                Text = "T-" + EngineerFormat.Time(until),
                Fill = TimeScale(until),
                Caution = LastTenSeconds,
                Level = until < 10.0 ? 1 : 0,
            };
        }

        // During a burn: the delta-v left, against what the node asked for as the
        // burn started; caution in the last tenth.
        internal static GaugeReading Burning(double left, double atStart)
        {
            double share = atStart > 0.0 ? left / atStart : 0.0;
            return new GaugeReading
            {
                Text = ReadoutMath.Speed(left),
                Fill = share,
                Caution = LastTenth,
                Level = share < 0.1 ? 1 : 0,
            };
        }

        // The time until the vessel's path meets the ground, on the time scale.
        // The limit, where `countdown` to the last moment to brake is known: the
        // time to impact left at that moment, a tick with the warning band below
        // it and caution a third above.
        internal static GaugeReading Impact(double seconds, double countdown)
        {
            GaugeReading reading = new GaugeReading { Text = EngineerFormat.Time(seconds), Fill = TimeScale(seconds) };
            if (double.IsNaN(countdown)) return reading;
            double brake = Math.Max(0.0, seconds - countdown);
            reading.Mark = TimeScale(brake);
            reading.Warning = new[] { 0.0, TimeScale(brake) };
            reading.Caution = new[] { TimeScale(brake), TimeScale(brake * 4.0 / 3.0) };
            reading.Level = seconds <= brake ? 2 : seconds <= brake * 4.0 / 3.0 ? 1 : 0;
            return reading;
        }

        // A safe speed to close at from `distance`: a hundredth of it per second,
        // at least 0.2 m/s.
        internal static double SafeClosing(double distance)
        {
            return Math.Max(0.2, distance / 100.0);
        }

        // How fast the vessel closes on its target, on the speed scale. The limit:
        // the safe speed for the distance, a tick; caution up to one and a half
        // times it, warning beyond.
        internal static GaugeReading Closing(double closing, double distance)
        {
            double safe = SafeClosing(distance);
            return new GaugeReading
            {
                Text = ReadoutMath.Speed(closing),
                Fill = SpeedScale(closing),
                Mark = SpeedScale(safe),
                Caution = new[] { SpeedScale(safe), SpeedScale(1.5 * safe) },
                Warning = new[] { SpeedScale(1.5 * safe), 1.0 },
                Level = closing > 1.5 * safe ? 2 : closing > safe ? 1 : 0,
            };
        }

        // The angle of attack, either way, from 0 to 40 degrees. The limit: the
        // angle where the wings give the most lift, warning beyond it, caution
        // from four fifths of it.
        internal const double AttackScale = 40.0;

        // The angle of attack against `stall`, the wings' angle of most lift, in degrees.
        internal static GaugeReading Attack(double attack, double stall)
        {
            double size = Math.Abs(attack);
            return new GaugeReading
            {
                Text = ReadoutMath.Angle(attack),
                Fill = size / AttackScale,
                Caution = new[] { 0.8 * stall / AttackScale, stall / AttackScale },
                Warning = new[] { stall / AttackScale, 1.0 },
                Level = size > stall ? 2 : size > 0.8 * stall ? 1 : 0,
            };
        }
    }
}
