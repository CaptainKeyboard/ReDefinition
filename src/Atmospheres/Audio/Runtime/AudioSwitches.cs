using System;

namespace ReDefinition.Atmospheres.Audio
{
    // The settings of the weather sound as one value, so that the decision code does not read the settings table: a test builds one by hand,
    // the module reads the live settings once per frame.
    internal struct AudioSwitches
    {
        // Key weatherAudio: off means there are no objects at all.
        internal bool Master;
        internal bool Wind;
        internal bool Precip;
        internal bool Footsteps;
        // Key audioFootstepsSurface: steps follow the surface type of the field (and the body's FootstepSurface entry), not snow only.
        internal bool FootstepsSurface;
        internal bool DistantStorm;
        internal bool QuietSnow;
        internal AudioMuffling Muffling;
        internal EveSoundPolicy Eve;
        internal AudioBedSource Bed;
        // Key audioProbeSolo: the other audio sources of the scene are muted.
        internal bool Solo;
        // Key weatherAudioVolume, 0 to 1.
        internal float Volume;

        // Everything on at the default volume, as the settings table delivers them (distant storm off).
        internal static AudioSwitches Defaults()
        {
            return new AudioSwitches
            {
                Master = true, Wind = true, Precip = true, Footsteps = true, FootstepsSurface = true, DistantStorm = false, QuietSnow = true,
                Muffling = AudioMuffling.Auto, Eve = EveSoundPolicy.Auto, Bed = AudioBedSource.Synth, Solo = false, Volume = 0.6f
            };
        }

        // The live settings.
        internal static AudioSwitches FromSettings()
        {
            return new AudioSwitches
            {
                Master = AtmospheresSettings.WeatherAudio && AtmospheresSettings.WeatherEffects && AtmospheresSettings.Enabled,
                Wind = AtmospheresSettings.AudioWind,
                Precip = AtmospheresSettings.AudioPrecip,
                Footsteps = AtmospheresSettings.AudioFootsteps,
                FootstepsSurface = AtmospheresSettings.AudioFootstepsSurface,
                DistantStorm = AtmospheresSettings.AudioDistantStorm,
                QuietSnow = AtmospheresSettings.AudioQuietSnow,
                Muffling = AtmospheresSettings.AudioMuffling,
                Eve = AtmospheresSettings.EveSound,
                Bed = AtmospheresSettings.AudioBedSource,
                Solo = AtmospheresSettings.AudioProbeSolo,
                Volume = AtmospheresSettings.WeatherAudioVolume
            };
        }
    }
}
