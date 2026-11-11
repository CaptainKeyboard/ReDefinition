using System;

namespace ReDefinition.Atmospheres.Audio
{
    // FROZEN INTERFACE (for the runtime package D33): the values the main thread hands to the audio thread. Every field is a
    // volatile float (or int), so one thread writes single fields and the other reads them without a lock; the mixer smooths them
    // per sample. Nothing here is a Unity type. Ranges are listed per field; the mixer replaces NaN by the lower end and clamps.
    public struct AudioParams
    {
        // The mean wind speed in m/s, 0 to 60 (the bed reaches its top at 30).
        public volatile float windSpeed;
        // The gust wave from -1 to 1 (WindNoise.Wave): the bed's amplitude is multiplied by 1 + gustDepth x gust.
        public volatile float gust;
        // The depth of the gusts, 0 to 1 (WindNoise.Depth of the precipitation level).
        public volatile float gustDepth;
        // The sound level of the fall, 0 to 1: the hiss grows with its power 1.5, the ticks come at 40 per second at 1.
        public volatile float intensity;
        // 0 for the hiss of snow, 1 for the grit of sand and dust; between, a mix.
        public volatile float grit;
        // 0 outside, 1 inside a hull (muffled bed, duller and louder ticks).
        public volatile float interior;
        // Snow cover at the listener, 0 to 1 (drives the quiet ambience in calm air).
        public volatile float snowCover;
        // How close a storm cell is, 0 (far or none) to 1 (near): the rumble's level and cutoff.
        public volatile float cellCloseness;
        // Where the cell is, -1 (left) to 1 (right).
        public volatile float cellPan;
        // The master volume and the volume of each category, 0 to 1.
        public volatile float volume;
        public volatile float volWind;
        public volatile float volPrecip;
        public volatile float volTicks;
        public volatile float volRumble;
        public volatile float volQuiet;
        // The wind bed: 0 synthesised, 1 recorded texture, 2 both (BedSource values). Without a texture the synthesised bed plays.
        public volatile int bedSource;

        // Silent wind, no fall, outside, every volume at 1, synthesised bed.
        public static AudioParams Create()
        {
            AudioParams p = new AudioParams();
            p.volume = 1f;
            p.volWind = 1f;
            p.volPrecip = 1f;
            p.volTicks = 1f;
            p.volRumble = 1f;
            p.volQuiet = 1f;
            return p;
        }
    }

    // The values of AudioParams.bedSource.
    public static class BedSource
    {
        public const int Synth = 0;
        public const int Recorded = 1;
        public const int Mix = 2;
    }
}
