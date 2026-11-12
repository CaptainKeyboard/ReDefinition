using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // The standardised noise fields of the height builder, kept: a field is a function of the map size, the body's radius, the wavelength and the
    // seed only (not of the weather), so the second build of a layer reads it instead of making it again. Two fields per layer (the free variation
    // and the top jitter) cost 34 ms of a build of 45 ms offline. Thread safe; the arrays are read only for every caller.
    internal sealed class HeightNoiseCache
    {
        private sealed class Entry
        {
            internal int W, H;
            internal double Radius, Wavelength;
            internal uint Seed;
            internal float[] Values;
        }

        // The cache the game's builds share.
        internal static readonly HeightNoiseCache Shared = new HeightNoiseCache(16);

        private readonly List<Entry> entries = new List<Entry>();
        private readonly int capacity;
        private readonly object gate = new object();

        internal HeightNoiseCache(int capacity)
        {
            this.capacity = Math.Max(1, capacity);
        }

        // Fields made since the cache was created (the tests read it).
        internal int Made { get; private set; }

        internal int Count
        {
            get
            {
                lock (gate)
                {
                    return entries.Count;
                }
            }
        }

        // The field of that size, radius, wavelength and seed, made when absent. Two threads asking for the same absent field may both make it;
        // the values are equal and one entry stays.
        internal float[] Get(int w, int h, double radiusM, double wavelengthM, uint seed)
        {
            lock (gate)
            {
                Entry hit = Find(w, h, radiusM, wavelengthM, seed);
                if (hit != null) return hit.Values;
            }
            float[] values = HeightMapBuilder.SphereNoise(w, h, radiusM, wavelengthM, seed);
            HeightMapBuilder.Standardise(values, h, w);
            lock (gate)
            {
                Entry hit = Find(w, h, radiusM, wavelengthM, seed);
                if (hit != null) return hit.Values;
                // The oldest entry goes when the cache is full (the layers of one body stay in use; another body's come later).
                if (entries.Count >= capacity) entries.RemoveAt(0);
                entries.Add(new Entry { W = w, H = h, Radius = radiusM, Wavelength = wavelengthM, Seed = seed, Values = values });
                Made++;
                return values;
            }
        }

        private Entry Find(int w, int h, double radiusM, double wavelengthM, uint seed)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                if (e.W == w && e.H == h && e.Seed == seed && e.Radius.Equals(radiusM) && e.Wavelength.Equals(wavelengthM)) return e;
            }
            return null;
        }
    }
}
