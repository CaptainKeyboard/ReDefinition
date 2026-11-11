using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using static ReDefinition.Atmospheres.Mathx;

namespace ReDefinition.Atmospheres
{
    // The amount of cover on one vessel, 0 to 1: a state machine stepped once a second of game time on the CPU. It rises with the
    // ground cover under the vessel while the vessel lies landed or low, faster while the field precipitates a type the layer takes,
    // and falls with a sun-warmed surface above freezing, speed, height and heating. The shells read the amount through a property
    // block. Pure numbers; the game part (VehicleCover) feeds it and keeps the shells. Package D34, brief item 4.
    internal struct VehicleCoverInputs
    {
        // The cover depth 0 to 1 on the ground at the vessel (the CoverMap) and the field's precipitation level 0 to 1 when the type
        // is the layer's (0 otherwise).
        internal double Ground01;
        internal double Fall01;
        // The surface temperature in kelvin (the field's) and whether the sun shines on the vessel.
        internal double TemperatureK;
        internal bool Sunlit;
        // The vessel's speed over the ground in m/s, height above the ground in metres, and a heating measure 0 to 1 (reentry glow).
        internal double SpeedMs;
        internal double AltitudeM;
        internal double Heating01;
        // Whether the vessel rests on the ground (landed or splashed).
        internal bool Landed;
    }

    // What is kept of a vessel's cover between sessions: the amount at a time, and the situation it was in (the target it rose toward and
    // the rate at which it fell), so that on load a = f(stored, UT - stamp) needs no field.
    internal struct VehicleCoverStored
    {
        internal float Amount;
        internal double StampUt;
        // The ground cover the amount was rising toward when it was saved (0 if it was not rising).
        internal float Target;
        // The fall in amount per second of game time when it was saved (0 if it was not falling).
        internal float FallPerSecond;
    }

    internal static class VehicleCoverModel
    {
        // The tuning of the machine (all through Tuning in the game; these are the compiled defaults).
        internal sealed class Rates
        {
            // Time constants in seconds of game time: the rise toward the ground cover with no precipitation, with full precipitation, and
            // the melt above freezing (per kelvin of the surface above 273.15 the rate grows, up to MeltKelvinCap).
            internal double RiseSeconds = 1800.0;
            internal double RiseFallSeconds = 300.0;
            internal double MeltSeconds = 3600.0;
            internal double MeltKelvinCap = 10.0;
            // Shedding: above this speed (m/s) the amount falls with ShedSeconds; above this height with HighSeconds.
            internal double ShedSpeedMs = 20.0;
            internal double ShedSeconds = 60.0;
            internal double HighAltitudeM = 1500.0;
            internal double HighSeconds = 120.0;
            // Heating above this measure clears the vessel.
            internal double HeatingReset = 0.05;
            // The amount that counts as no cover at all (the shells are hidden below it).
            internal double Visible = 0.02;
        }

        internal const double FreezingK = 273.15;

        // One step of dt seconds of game time: the new amount, and in stored the situation for the side file.
        internal static double Step(double amount, VehicleCoverInputs i, double dt, Rates r, out float target, out float fallPerSecond)
        {
            target = 0f;
            fallPerSecond = 0f;
            amount = Clamp01(amount);
            if (!(dt > 0) || double.IsNaN(dt)) return amount;
            if (i.Heating01 >= r.HeatingReset)
            {
                fallPerSecond = (float)(amount / dt);
                return 0.0;
            }
            // Falls: speed, height, warm sun. They add up as rates; the fastest set the pace.
            double fall = 0;
            if (i.SpeedMs > r.ShedSpeedMs) fall = Math.Max(fall, 1.0 / r.ShedSeconds);
            if (i.AltitudeM > r.HighAltitudeM) fall = Math.Max(fall, 1.0 / r.HighSeconds);
            if (!double.IsNaN(i.TemperatureK) && i.TemperatureK > FreezingK)
            {
                double warm = Clamp(i.TemperatureK - FreezingK, 0, r.MeltKelvinCap) / r.MeltKelvinCap;
                // A sunlit surface melts at the full rate, one in the shade at a third of it.
                fall = Math.Max(fall, warm * (i.Sunlit ? 1.0 : 0.33) / r.MeltSeconds);
            }
            if (fall > 0)
            {
                fallPerSecond = (float)fall;
                return Math.Max(0.0, amount - fall * dt);
            }
            // Rises: only on the ground or low and slow, and not above the ground cover.
            bool low = i.Landed || i.AltitudeM <= r.HighAltitudeM;
            if (low && i.SpeedMs <= r.ShedSpeedMs)
            {
                double ground = Clamp01(i.Ground01);
                target = (float)ground;
                if (amount < ground)
                {
                    double rate = 1.0 / r.RiseSeconds + Clamp01(i.Fall01) * (1.0 / r.RiseFallSeconds);
                    return Math.Min(ground, amount + rate * dt);
                }
                // The ground cover fell below the vessel's: the vessel follows it down at the slow rise rate (the cover does not hold
                // what the ground lost, but it is not blown off either).
                if (amount > ground) return Math.Max(ground, amount - dt / r.RiseSeconds);
            }
            return amount;
        }

        // The amount on load, from what was stored and the game time now. A stamp in the future (a revert) clears the vessel.
        internal static double Restore(VehicleCoverStored s, double nowUt, Rates r)
        {
            double amount = Clamp01(s.Amount);
            if (double.IsNaN(s.StampUt) || double.IsNaN(nowUt)) return 0.0;
            double dt = nowUt - s.StampUt;
            if (dt < 0) return 0.0;
            if (dt == 0) return amount;
            if (s.FallPerSecond > 0f) return Math.Max(0.0, amount - s.FallPerSecond * dt);
            if (s.Target > amount) return Math.Min(Clamp01(s.Target), amount + dt / r.RiseSeconds);
            if (s.Target > 0f && s.Target < amount) return Math.Max(Clamp01(s.Target), amount - dt / r.RiseSeconds);
            return amount;
        }

        // Whether the shells are shown for an amount.
        internal static bool Visible(double amount, Rates r)
        {
            return amount >= r.Visible;
        }

        // Whether the shells of a vessel are kept for the setting (Off: never; Landed: while it rests on the ground or is within 50 m of
        // it; Always: for every loaded vessel; the amount itself still follows the machine, so a vessel in the air above the high
        // altitude shows none).
        internal static bool Wanted(VehicleCoverModeLite mode, bool landed, double altitudeM)
        {
            switch (mode)
            {
                case VehicleCoverModeLite.Off: return false;
                case VehicleCoverModeLite.Landed: return landed || altitudeM <= 50.0;
                default: return true;
            }
        }
    }

    // The setting's values without the settings type (so the model can be linked into the test player). Same order as VehicleCoverMode.
    internal enum VehicleCoverModeLite
    {
        Off = 0,
        Landed = 1,
        Always = 2
    }

    // The side file of the vehicle cover: one line per vessel, keyed by the persistent id, in PluginData/<save>/vehicle-cover.txt. No
    // ScenarioModule: nothing goes into the save game. Text, so that a damaged line costs one vessel and never the file.
    internal static class VehicleCoverFile
    {
        internal const string Header = "ReDefinition-vehicle-cover 1";

        internal static string Serialize(IDictionary<uint, VehicleCoverStored> entries)
        {
            var sb = new StringBuilder();
            sb.Append(Header).Append('\n');
            var keys = new List<uint>(entries.Keys);
            keys.Sort();
            foreach (uint key in keys)
            {
                VehicleCoverStored s = entries[key];
                sb.Append(key.ToString(CultureInfo.InvariantCulture)).Append(' ')
                  .Append(s.Amount.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                  .Append(s.StampUt.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                  .Append(s.Target.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                  .Append(s.FallPerSecond.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            }
            return sb.ToString();
        }

        // The entries of a file's text; a line that does not parse is skipped, a text without the header gives none.
        internal static Dictionary<uint, VehicleCoverStored> Parse(string text, out int skipped)
        {
            skipped = 0;
            var result = new Dictionary<uint, VehicleCoverStored>();
            if (string.IsNullOrEmpty(text)) return result;
            string[] lines = text.Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != Header) return result;
            for (int n = 1; n < lines.Length; n++)
            {
                string line = lines[n].Trim();
                if (line.Length == 0) continue;
                string[] f = line.Split(' ');
                uint id;
                float amount, target, fall;
                double stamp;
                if (f.Length != 5
                    || !uint.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out id)
                    || !float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out amount)
                    || !double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out stamp)
                    || !float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out target)
                    || !float.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out fall)
                    || float.IsNaN(amount) || double.IsNaN(stamp) || float.IsNaN(target) || float.IsNaN(fall))
                {
                    skipped++;
                    continue;
                }
                result[id] = new VehicleCoverStored { Amount = amount, StampUt = stamp, Target = target, FallPerSecond = fall };
            }
            return result;
        }
    }
}
