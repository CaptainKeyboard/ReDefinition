using System;
using static ReDefinition.Seasons.Mathx;

namespace ReDefinition.Seasons
{
    // The snow storms as a deterministic schedule: a pure function of the universal time, the latitude, the season and a seed. No state
    // and no random numbers from the game, so that the same inputs give the same storm in the game and in the tests.
    //
    // Time is cut into slots of SlotHours Kerbin hours, per hemisphere and per band of latitude. A slot holds at most one storm. The
    // storm's chance comes from the winter gate (Gate) and the frequency setting; its start inside the slot, its three phases and its peak
    // come from a hash of the seed, the hemisphere, the band and the slot. A storm lies wholly inside its slot, so storms of one band
    // never overlap. Draw is the rule of one storm of a slot; Intensity applies it per band, and StormCells applies it per sector of a
    // band to make storm cells that stand on the planet.
    internal static class StormModel
    {
        // A Kerbin hour is 3600 s; the numbers below are in these hours.
        internal const double HourSeconds = 3600;

        // The length of a slot: 4 Kerbin days of 6 hours.
        internal const double SlotHours = 24;

        // The chance of a storm in a slot at frequency 1 where the gate is 1: about 6 storms in a year at the real year length.
        internal const double BaseChance = 0.3;

        // The width of a band of latitude in degrees: storms in neighbouring bands are independent.
        internal const double BandDegrees = 10;

        // The ramp up, the peak and the decay of a storm, in Kerbin hours, from the shortest to the longest.
        internal const double RampUpMinHours = 1.5, RampUpMaxHours = 3;
        internal const double PeakMinHours = 2, PeakMaxHours = 8;
        internal const double DecayMinHours = 2.5, DecayMaxHours = 6;

        // The peak intensity of a storm: from this to 1.
        internal const double PeakIntensityMin = 0.45;

        // The gate opens (0 to 1) as the snow cover goes from CoverageClosed to CoverageOpen and as the hemisphere goes from the
        // mean towards its midwinter; at the permanent caps in summer the season term keeps it shut.
        internal const double CoverageClosed = 0.35, CoverageOpen = 0.85;
        internal const double WinterClosed = 0.15, WinterOpen = 0.6;

        // The most that the frequency setting is allowed to be (it is clamped to this).
        internal const double FrequencyMax = SeasonsSettings.StormFrequencyMax;

        // How cold and wintry a place is, 0 to 1, for a hemisphere phase: 1 at its midwinter (the snow trails the sun), 0 from a bit before
        // spring to autumn.
        internal static double Winterness(double hemispherePhase)
        {
            double lagged = SeasonClock.Wrap(hemispherePhase - SnowMask.LagYears);
            return SmoothStep(WinterClosed, WinterOpen, Math.Cos(2 * Math.PI * lagged));
        }

        // How far storms are possible at a latitude (north positive) and a season phase of the clock, 0 to 1: the snow cover there
        // (SnowMask, without height and noise) times how wintry the hemisphere is. A value that is not a number gives 0.
        internal static double Gate(double latitudeDegrees, double seasonPhase, double winterLineDegrees)
        {
            if (double.IsNaN(latitudeDegrees) || double.IsNaN(seasonPhase) || double.IsNaN(winterLineDegrees)) return 0;
            double cover = SnowMask.Coverage(latitudeDegrees, 0, seasonPhase, 0, winterLineDegrees);
            double winter = Winterness(SeasonClock.HemispherePhase(seasonPhase, latitudeDegrees >= 0));
            return SmoothStep(CoverageClosed, CoverageOpen, cover) * winter;
        }

        // The storm intensity from 0 to 1 at a time (universal time in seconds) and a latitude.
        // yearSeconds is the season year (SeasonClock.YearSeconds). A forcedPhase from 0 to 1 replaces the clock.
        // The frequency is brought into 0 to FrequencyMax.
        internal static double Intensity(double universalTime, double latitudeDegrees, double yearSeconds, double forcedPhase,
            double winterLineDegrees, int seed, double frequency)
        {
            if (double.IsNaN(universalTime) || double.IsInfinity(universalTime) || double.IsNaN(latitudeDegrees)) return 0;
            frequency = Clamp(double.IsNaN(frequency) ? 0 : frequency, 0, FrequencyMax);
            if (frequency <= 0) return 0;
            double lat = Clamp(latitudeDegrees, -90, 90);
            bool north = lat >= 0;
            double slotSeconds = SlotHours * HourSeconds;
            double slot = Math.Floor(universalTime / slotSeconds);
            double centre = (slot + 0.5) * slotSeconds;
            double phase = forcedPhase >= 0 && forcedPhase < 1 ? forcedPhase : SeasonClock.Phase(centre, yearSeconds);
            int band = (int)Math.Floor(Math.Abs(lat) / BandDegrees);
            // The gate is read at the middle of the band, so that all of a band shares one schedule.
            double gate = Gate((band + 0.5) * BandDegrees * (north ? 1 : -1), phase, winterLineDegrees);
            Storm storm = Draw(SlotKey(seed, north, band, slot), slot, gate, frequency);
            if (!storm.Present) return 0;
            return storm.Height * Envelope(universalTime - storm.Start, storm.Up, storm.Peak, storm.Down);
        }

        // One storm of a slot: when it starts, its three phases and its peak height, all in seconds of universal time except Height.
        internal struct Storm
        {
            internal bool Present;
            internal ulong Key;
            internal double Start, Up, Peak, Down, Height;

            internal double End
            {
                get { return Start + Up + Peak + Down; }
            }
        }

        // The key of the storm of a slot of a band of a hemisphere (the slot as a whole number from the floor of time / slot length).
        internal static ulong SlotKey(int seed, bool north, int band, double slot)
        {
            return Mix(Mix(Mix((ulong)(uint)seed ^ 0x9E3779B97F4A7C15UL, north ? 1UL : 2UL), (ulong)band + 17UL), (ulong)(long)slot);
        }

        // The storm that a key brings in a slot, if its chance (BaseChance times the frequency times the gate) is met. A storm lies
        // wholly inside its slot. This is the spawn rule of the storm cells (StormCells) and the band schedule of Intensity.
        internal static Storm Draw(ulong key, double slot, double gate, double frequency)
        {
            var storm = new Storm { Key = key };
            frequency = Clamp(double.IsNaN(frequency) ? 0 : frequency, 0, FrequencyMax);
            if (!(gate > 0) || frequency <= 0) return storm;
            if (Unit(key, 0) >= BaseChance * frequency * gate) return storm;
            double slotSeconds = SlotHours * HourSeconds;
            storm.Up = Lerp(RampUpMinHours, RampUpMaxHours, Unit(key, 1)) * HourSeconds;
            storm.Peak = Lerp(PeakMinHours, PeakMaxHours, Unit(key, 2)) * HourSeconds;
            storm.Down = Lerp(DecayMinHours, DecayMaxHours, Unit(key, 3)) * HourSeconds;
            storm.Height = Lerp(PeakIntensityMin, 1, Unit(key, 4));
            double room = slotSeconds - (storm.Up + storm.Peak + storm.Down);
            storm.Start = slot * slotSeconds + Math.Max(0, room) * Unit(key, 5);
            storm.Present = true;
            return storm;
        }

        // 0 before the storm, a smooth rise over the ramp, 1 through the peak, a smooth fall over the decay, 0 after.
        internal static double Envelope(double t, double ramp, double peak, double decay)
        {
            if (t <= 0 || t >= ramp + peak + decay) return 0;
            if (t < ramp) return SmoothStep(0, 1, t / ramp);
            if (t < ramp + peak) return 1;
            return SmoothStep(0, 1, 1 - (t - ramp - peak) / decay);
        }

        // The intensity the effect uses: the diagnostics value if it is from 0 to 1 (ForceStorm, same at every place and time),
        // otherwise the schedule's value when storms are on, otherwise 0.
        internal static double Resolve(double forceStorm, bool stormsEnabled, double scheduled)
        {
            if (!double.IsNaN(forceStorm) && forceStorm >= 0) return Clamp(forceStorm, 0, 1);
            if (!stormsEnabled || double.IsNaN(scheduled)) return 0;
            return Clamp(scheduled, 0, 1);
        }

        // A 64-bit mixing step (splitmix64).
        internal static ulong Mix(ulong x, ulong y)
        {
            unchecked
            {
                ulong z = x + 0x9E3779B97F4A7C15UL + y * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        // The n-th independent value of a key, in [0, 1).
        internal static double Unit(ulong key, int n)
        {
            ulong h = Mix(key, (ulong)n + 101UL);
            return (h >> 11) / (double)(1UL << 53);
        }
    }
}
