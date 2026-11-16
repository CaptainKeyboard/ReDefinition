using System;
using System.Collections.Generic;
using System.Linq;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Viewer;

namespace ReDefinition.Tests.Atmospheres.Viewer
{
    // A model of the workbench's side of the game for the K-V1 walk-through: a key store with presets and the A/B pair, a seed that decides a deterministic cover at fixed places, and a
    // rebuild that takes ten frames. The faults are the ways the real viewer could be wrong.
    internal sealed class FakeWorkbenchRig : IWorkbenchRig
    {
        private readonly FakeViewerRig rig;
        private readonly Dictionary<string, float> overrides = new Dictionary<string, float>();
        private readonly Dictionary<string, Dictionary<string, float>> presets = new Dictionary<string, Dictionary<string, float>>();
        private Dictionary<string, float> stored;
        private int seed = 4711, staged = int.MinValue, startFrame = -1;
        private long stamp = 123456789L;
        private bool showingB = true;

        internal bool SeedIgnored, SeedNotPure, RebuildOverBudget, RebuildNeverDone, NoReadout, PresetLossy, WritesSave, TodayKeepsOverrides, FlipDoesNothing, OwnCloudsIgnored, KeysNotRead;

        internal FakeWorkbenchRig(FakeViewerRig rig)
        {
            this.rig = rig;
            rig.ReadsAnyKey = true;
        }

        public IList<CloudKey> SampleKeys
        {
            get
            {
                var picked = new List<CloudKey>();
                foreach (KeySection s in new[] { KeySection.Shape, KeySection.Edge, KeySection.Light, KeySection.Quality, KeySection.Weather })
                    picked.Add(CloudKeyRegistry.All.First(k => k.Section == s));
                return picked;
            }
        }

        public void Watch(string key)
        {
            rig.ReadNotSeen = KeysNotRead;
            rig.WatchKey(key);
        }

        public void SetKey(string key, float value) { overrides[key] = value; }
        public void ResetKey(string key) { overrides.Remove(key); }
        public float KeyValue(string key) { float v; return overrides.TryGetValue(key, out v) ? v : CloudKeyRegistry.Find(key).Default; }
        public bool HasOverride(string key) { return overrides.ContainsKey(key); }

        public void SetOwnClouds(bool on) { if (!OwnCloudsIgnored) rig.PassForcedOff = !on; }

        private int requests;
        public int RebuildRequests { get { return requests; } }
        public void RebuildWeather() { startFrame = rig.Frame; requests++; if (WritesSave) stamp++; }

        public RebuildPhase Phase
        {
            get
            {
                if (startFrame < 0) return RebuildPhase.Idle;
                if (RebuildNeverDone || rig.Frame - startFrame < 10) return RebuildPhase.Building;
                if (staged != int.MinValue) { seed = staged; staged = int.MinValue; }
                return RebuildPhase.Done;
            }
        }

        public double RebuildElapsedMs { get { return 10 * 50.0; } }
        public double RebuildMainMs { get { return 6.0; } }
        public double RebuildWorstFrameMs { get { return RebuildOverBudget ? 45.0 : 3.0; } }
        public int Seed { get { RebuildPhase p = Phase; return seed; } }
        public void StageSeed(int s) { staged = s; }
        public int Places { get { return ViewerProbe.SeedPlaces; } }

        public double CoverAt(int place, double ut)
        {
            int s = SeedIgnored ? 0 : seed;
            uint h = unchecked((uint)(s * 2654435761u) ^ (uint)(place * 40503) ^ (uint)((long)ut * 7));
            if (SeedNotPure) h ^= (uint)startFrame;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            return (h % 1000) / 1000.0;
        }

        public IList<string> RefreshReadout()
        {
            return NoReadout ? new List<string>() : new List<string> { "Cells 412, 91.1 per million km2, above the real band 2 to 5 (26.0 x 3.5)" };
        }

        public bool SavePreset(string name) { presets[name] = new Dictionary<string, float>(overrides); return true; }

        public bool LoadPreset(string name)
        {
            Dictionary<string, float> p;
            if (!presets.TryGetValue(name, out p)) return false;
            overrides.Clear();
            foreach (KeyValuePair<string, float> pair in p) overrides[pair.Key] = PresetLossy ? pair.Value + 0.1f : pair.Value;
            return true;
        }

        public void DeletePreset(string name) { presets.Remove(name); }

        public bool LoadToday()
        {
            if (!TodayKeepsOverrides) overrides.Clear();
            return true;
        }

        public bool StartCompare(string a, string b)
        {
            if (!LoadPreset(a)) return false;
            stored = new Dictionary<string, float>(overrides);
            if (b == ViewerPresetBook.TodayName) LoadToday();
            else LoadPreset(b);
            showingB = true;
            return true;
        }

        public void FlipAB()
        {
            if (FlipDoesNothing || stored == null) return;
            var shown = new Dictionary<string, float>(overrides);
            overrides.Clear();
            foreach (KeyValuePair<string, float> pair in stored) overrides[pair.Key] = pair.Value;
            stored = shown;
            showingB = !showingB;
        }

        public char Showing { get { return showingB ? 'B' : 'A'; } }
        public double FrameMs { get { return 16.7; } }
        public double GpuMs { get { return double.NaN; } }
        public long WriteStamp() { return stamp; }
    }
}
