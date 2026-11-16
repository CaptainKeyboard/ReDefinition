using System;
using System.Collections.Generic;
using System.IO;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Viewer
{
    // What the K-V1 probe's workbench section (VIEW3: the grouped key table, the modes, Rebuild Weather and the seed, the readout, the presets and the A/B compare, the cost readout) needs of
    // the game. The game's rig drives the viewer's calls (the window's controls are thin over them) and reads the weather field and the files; the tests use a model.
    internal interface IWorkbenchRig
    {
        // One tuning key of the pass per group, with its liveness (a key read when the tuning table changes is not required to be read every frame).
        IList<CloudKey> SampleKeys { get; }
        void Watch(string key);
        void SetKey(string key, float value);
        void ResetKey(string key);
        float KeyValue(string key);
        bool HasOverride(string key);

        void SetOwnClouds(bool on);

        // The weather: Rebuild Weather, its phase and what it cost, the seed in force and a staged one.
        void RebuildWeather();
        // The number of rebuilds the generator module has begun (it rises when the module takes a request).
        int RebuildRequests { get; }
        RebuildPhase Phase { get; }
        double RebuildElapsedMs { get; }
        double RebuildMainMs { get; }
        double RebuildWorstFrameMs { get; }
        int Seed { get; }
        void StageSeed(int seed);
        // The cloud cover the weather field gives at the i-th of a fixed set of places at this UT; NaN where it cannot say.
        double CoverAt(int place, double ut);
        int Places { get; }
        // Refreshes the readout and gives its lines.
        IList<string> RefreshReadout();

        bool SavePreset(string name);
        bool LoadPreset(string name);
        void DeletePreset(string name);
        bool LoadToday();
        bool StartCompare(string a, string b);
        void FlipAB();
        char Showing { get; }

        double FrameMs { get; }
        // The cloud pass's GPU milliseconds; NaN where the profiler is off.
        double GpuMs { get; }
        // The last-write times of the save's persistent.sfs and the mod's settings file, summed: a change means one of them was written.
        long WriteStamp();
    }

    internal sealed class GameWorkbenchRig : IWorkbenchRig
    {
        private readonly CloudViewer viewer = CloudViewer.Instance;
        private const int PlaceCount = 24;

        public IList<CloudKey> SampleKeys
        {
            get
            {
                // The first key of each group that the pass reads every frame (a key read only when the tuning table changes comes last), so that each group is shown to be bound.
                var picked = new List<CloudKey>();
                foreach (KeySection section in new[] { KeySection.Shape, KeySection.Edge, KeySection.Light, KeySection.Quality, KeySection.Weather })
                {
                    CloudKey best = null;
                    foreach (CloudKey k in viewer.Keys)
                    {
                        if (k.Section != section) continue;
                        if (best == null || best.Liveness == KeyLiveness.Change && k.Liveness != KeyLiveness.Change) best = k;
                        if (best.Liveness != KeyLiveness.Change) break;
                    }
                    if (best != null) picked.Add(best);
                }
                return picked;
            }
        }

        public void Watch(string key) { viewer.WatchKey(key); }
        public void SetKey(string key, float value) { viewer.SetKey(key, value); }
        public void ResetKey(string key) { viewer.ResetKey(key); }

        public float KeyValue(string key)
        {
            CloudKey k = CloudKeyRegistry.Find(key);
            return viewer.Store.Effective(key, k == null ? 0f : k.Default);
        }

        public bool HasOverride(string key)
        {
            float v;
            return viewer.Store.TryOverride(key, out v);
        }

        public void SetOwnClouds(bool on) { viewer.SetSetting(AtmospheresSettings.Keys.OwnClouds, on ? 1.0 : 0.0); }

        public void RebuildWeather() { viewer.RebuildWeather(); }
        public int RebuildRequests { get { return WeatherRebuild.Requests; } }
        public RebuildPhase Phase { get { return WeatherRebuild.Phase; } }
        public double RebuildElapsedMs { get { return WeatherRebuild.ElapsedMs; } }
        public double RebuildMainMs { get { return WeatherRebuild.MainMs; } }
        public double RebuildWorstFrameMs { get { return WeatherRebuild.WorstFrameMs; } }
        public int Seed { get { return AtmospheresSettings.GenSeed; } }
        public void StageSeed(int seed) { viewer.Lab.SetSeed(seed); }

        public int Places { get { return PlaceCount; } }

        public double CoverAt(int place, double ut)
        {
            CelestialBody b = FlightGlobals.currentMainBody;
            if (b == null) return double.NaN;
            // Places spread over the globe on a fixed spiral.
            double lat = 80.0 * Math.Sin(place * 2.399963), lon = (place * 137.50776) % 360.0 - 180.0;
            WeatherSample s;
            return WeatherHub.Sample(b.flightGlobalsIndex, lat, lon, 0.0, ut, out s) ? s.CloudCover01 : double.NaN;
        }

        public IList<string> RefreshReadout()
        {
            viewer.RefreshReadout();
            return new List<string>(viewer.ReadoutLines);
        }

        public bool SavePreset(string name) { return viewer.SavePreset(name); }
        public bool LoadPreset(string name) { return viewer.LoadPreset(name); }

        public void DeletePreset(string name)
        {
            try
            {
                string path = CloudViewer.PresetPath(ViewerPresets.SafeName(name));
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception) { /* a file that cannot be removed is left; the name says what it is */ }
        }

        public bool LoadToday()
        {
            PresetEntry today = viewer.PresetEntries().Find(e => e.Name == ViewerPresetBook.TodayName);
            return viewer.LoadEntry(today);
        }

        public bool StartCompare(string a, string b)
        {
            while (viewer.CompareA != a) { viewer.CycleCompare(true); if (viewer.CompareA == ViewerPresetBook.TodayName && a != ViewerPresetBook.TodayName && viewer.PresetEntries().Find(e => e.Name == a) == null) return false; }
            while (viewer.CompareB != b) { viewer.CycleCompare(false); if (viewer.CompareB == ViewerPresetBook.TodayName && b != ViewerPresetBook.TodayName && viewer.PresetEntries().Find(e => e.Name == b) == null) return false; }
            return viewer.StartCompare();
        }

        public void FlipAB() { viewer.FlipAB(); }
        public char Showing { get { return viewer.Flip.Showing; } }

        public double FrameMs { get { return viewer.Cost.FrameMs; } }
        public double GpuMs { get { return viewer.Cost.GpuMs; } }

        public long WriteStamp()
        {
            long sum = 0;
            try
            {
                string root = KSPUtil.ApplicationRootPath;
                string save = HighLogic.SaveFolder;
                string sfs = Path.Combine(Path.Combine(Path.Combine(root, "saves"), save ?? ""), "persistent.sfs");
                if (File.Exists(sfs)) sum += File.GetLastWriteTimeUtc(sfs).Ticks;
                string settings = ModFolder.PluginFile(AtmospheresSettings.Mod, AtmospheresSettings.FileName);
                if (File.Exists(settings)) sum += File.GetLastWriteTimeUtc(settings).Ticks;
            }
            catch (Exception) { /* the stamp is what it could read */ }
            return sum;
        }
    }
}
