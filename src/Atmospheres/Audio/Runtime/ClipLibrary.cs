using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres.Audio
{
    // The shipped sound files by name, in the three intensity classes of the manifest (GameData/ReDefinition/Atmospheres/Sounds/SOUNDS.json),
    // loaded through two delegates (the game's GameDatabase in the module, a table in a test) so that the fallback rules are testable
    // without the game. A file that does not exist is skipped and logged once; a class without a file borrows the nearest class that has
    // one; no file at all disables the layer.
    internal sealed class ClipLibrary<T> where T : class
    {
        // The folder in the GameDatabase (no extension) and the files of the snow steps by class.
        internal const string Folder = "ReDefinition/Atmospheres/Sounds/";
        internal static readonly string[] SnowSoft = { "steps_snow_01", "steps_snow_02", "steps_snow_03", "steps_snow_04" };
        internal static readonly string[] SnowMedium = { "steps_snow_05", "steps_snow_06", "steps_snow_07", "steps_snow_08" };
        internal static readonly string[] SnowHard = { "steps_snow_09", "steps_snow_10", "steps_snow_11", "steps_snow_12" };

        // Decoded size of the footstep set at 48 kHz mono 16 bit is about 0.7 MB (SOUNDS.json: 29760 frames x 12); the budget of the audio is
        // 12 MB with the recorded bed.
        internal const long BudgetBytes = 12L * 1024 * 1024;

        private readonly List<T>[] byClass = { new List<T>(), new List<T>(), new List<T>() };
        private readonly HashSet<string> logged = new HashSet<string>();
        private readonly Action<string> log;

        internal ClipLibrary(Action<string> log)
        {
            this.log = log ?? (s => { });
        }

        // Loads the three classes. exists and get are the lookups of the game (ExistsAudioClip, GetAudioClip). Returns the number of clips.
        internal int Load(Func<string, bool> exists, Func<string, T> get, string folder = Folder)
        {
            for (int i = 0; i < byClass.Length; i++) byClass[i].Clear();
            Add(0, SnowSoft, exists, get, folder);
            Add(1, SnowMedium, exists, get, folder);
            Add(2, SnowHard, exists, get, folder);
            return Count;
        }

        // Adds the clips of one extra file list to a class (a body's own footstep clip: SoundRules.FootstepSurface).
        internal bool AddSingle(int cls, string path, Func<string, bool> exists, Func<string, T> get)
        {
            if (cls < 0 || cls >= byClass.Length || string.IsNullOrEmpty(path)) return false;
            if (!SafeExists(exists, path))
            {
                LogOnce(path, "weather sound: " + path + " does not exist; that clip is skipped");
                return false;
            }
            T clip = SafeGet(get, path);
            if (clip == null)
            {
                LogOnce(path, "weather sound: " + path + " could not be loaded; that clip is skipped");
                return false;
            }
            byClass[cls].Add(clip);
            return true;
        }

        private void Add(int cls, string[] names, Func<string, bool> exists, Func<string, T> get, string folder)
        {
            for (int i = 0; i < names.Length; i++) AddSingle(cls, folder + names[i], exists, get);
        }

        internal int Count
        {
            get { return byClass[0].Count + byClass[1].Count + byClass[2].Count; }
        }

        // Whether any clip exists: false disables the layer.
        internal bool Any
        {
            get { return Count > 0; }
        }

        // The clip for a class (0 soft, 1 medium, 2 hard) and a pick index; a class without a clip uses the nearest class that has one.
        // Null where there is no clip at all.
        internal T Pick(int cls, int index)
        {
            cls = cls < 0 ? 0 : cls > 2 ? 2 : cls;
            for (int distance = 0; distance < 3; distance++)
            {
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    int c = cls + sign * distance;
                    if (c < 0 || c > 2) continue;
                    List<T> list = byClass[c];
                    if (list.Count > 0) return list[(int)((uint)index % (uint)list.Count)];
                    if (distance == 0) break;
                }
            }
            return null;
        }

        private static bool SafeExists(Func<string, bool> exists, string path)
        {
            try { return exists != null && exists(path); }
            catch (Exception) { return false; }
        }

        private static T SafeGet(Func<string, T> get, string path)
        {
            try { return get != null ? get(path) : null; }
            catch (Exception) { return null; }
        }

        private void LogOnce(string key, string message)
        {
            if (logged.Add(key)) log(message);
        }
    }

    // What the bed source setting and the files allow: the recorded texture needs a clip, else the synthesised bed plays.
    internal static class BedSourceRule
    {
        // wanted: the setting; textureAvailable: a recorded texture is loaded. Returns the BedSource value for AudioParams.
        internal static int Effective(AudioBedSource wanted, bool textureAvailable)
        {
            if (!textureAvailable) return BedSource.Synth;
            return (int)wanted;
        }
    }
}
