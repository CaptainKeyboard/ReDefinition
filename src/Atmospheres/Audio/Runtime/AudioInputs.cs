using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Audio
{
    // The weather at the listener, as the sound needs it (read from the view of the frame driver; a probe sets it by hand).
    internal struct WeatherAudioInputs
    {
        internal PrecipType Type;
        // The display level of the fall, 0 to 1.
        internal float Level01;
        // The mean wind in m/s without the gust (the mixer applies the gust wave itself).
        internal float MeanWindMs;
        // WindNoise.Wave at the listener, -1 to 1.
        internal float GustWave;
        // Snow cover at the listener, 0 to 1.
        internal float SnowCover01;
        // How close the nearest system is, 0 to 1, and where, -1 (left) to 1 (right). Zero where the distant storm sound is off.
        internal float CellCloseness;
        internal float CellPan;
        // False in a vacuum and wherever the atmosphere has no weather: nothing sounds.
        internal bool Air;
        // The body's SOUND node asks for the grit of sand for every fall.
        internal bool BodyGrit;
    }

    // Where the listener is: the camera mode decides the muffling.
    internal struct ListenerState
    {
        // IVA or the internal camera, on a vessel.
        internal bool Cabin;
        internal bool MapView;
        // The camera is a kerbal's, in a helmet.
        internal bool Eva;
    }

    // The sliders of the game's sound settings (GameSettings.AMBIENCE_VOLUME and SHIP_VOLUME), read once per second.
    internal struct GameVolumes
    {
        internal float Ambience;
        internal float Ship;

        internal static GameVolumes Full()
        {
            return new GameVolumes { Ambience = 1f, Ship = 1f };
        }
    }

    // The listener rules of plan 07 section 3.6: a cabin muffles, the map view is outside the weather, a helmet colours a little.
    internal static class ListenerModel
    {
        // cabinCamera: the camera mode is IVA or Internal (CameraManager.CameraMode); mapCamera: it is Map or the map view is open.
        internal static ListenerState From(bool cabinCamera, bool mapCamera, bool eva)
        {
            return new ListenerState { Cabin = !eva && cabinCamera, MapView = mapCamera, Eva = eva };
        }
    }

    // Maps inputs, listener, switches, volumes and the EVE decision to the fields of AudioParams. Pure; the fields are written one by one
    // on the main thread and read by the audio thread.
    internal static class AudioParamsMapper
    {
        // The sound that remains when the master switch is on but a category is off is zero for that category.
        internal static void Apply(WeatherAudioInputs w, ListenerState l, AudioSwitches sw, GameVolumes v, EveSoundDecision eve,
            float gateGain, ref AudioParams p)
        {
            bool air = w.Air;
            float level = air ? Clamp01(w.Level01) : 0f;
            float wind = air ? w.MeanWindMs : 0f;
            AudioMapping.Fill(ref p, w.Type, level, wind, air ? w.GustWave : 0f);
            if (w.BodyGrit && p.intensity > 0f) p.grit = 1f;
            p.interior = l.Cabin && !l.MapView ? 1f : 0f;
            p.snowCover = air ? Clamp01(w.SnowCover01) : 0f;
            p.cellCloseness = sw.DistantStorm && air ? Clamp01(w.CellCloseness) : 0f;
            p.cellPan = Clamp(w.CellPan, -1f, 1f);
            p.volume = Clamp01(sw.Volume * Clamp01(gateGain));
            float ambience = Clamp01(v.Ambience);
            p.volWind = sw.Wind ? ambience * Clamp01(eve.BedFactor) : 0f;
            p.volPrecip = sw.Precip ? ambience * Clamp01(eve.HissFactor) : 0f;
            p.volTicks = sw.Precip ? Clamp01(v.Ship) : 0f;
            p.volRumble = sw.DistantStorm ? ambience : 0f;
            p.volQuiet = sw.QuietSnow ? ambience : 0f;
            p.bedSource = (int)sw.Bed;
        }

        private static float Clamp01(float v)
        {
            return !(v > 0f) ? 0f : v > 1f ? 1f : v;
        }

        private static float Clamp(float v, float lo, float hi)
        {
            return !(v >= lo) ? lo : v > hi ? hi : v;
        }
    }
}
