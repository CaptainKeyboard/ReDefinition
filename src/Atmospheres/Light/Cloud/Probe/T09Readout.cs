using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The numeric readout of series K-T2d: what the camera target holds in three regions of one frame, as raw values (the numbers the pass and everything after it left
    // in the target, no gamma step: ReconHost keeps them in ReconFrame.Raw). Pure arithmetic on arrays, so that a unit test runs it on a known picture.
    // The regions: the sky above the horizon, the sea or ground below it, and the deck, the pixels where the frame with the own pass differs from the frame of the same
    // pose without it (the "off" baseline). The vessel at the centre of the picture is left out of all three. Rows are numbered from the bottom, as Unity reads them.
    internal static class T09Readout
    {
        // The share of the picture's height kept clear on both sides of the horizon (the horizon row is computed from the camera's pitch and field of view, not found).
        internal const double HorizonMargin = 0.04;
        // Rows used at the top and the bottom of the picture (the very edge carries the upscaler's border).
        internal const double EdgeLow = 0.03, EdgeHigh = 0.97;
        // The box around the vessel (fractions of the width and height) left out of every region.
        internal const double VesselHalfWidth = 0.12, VesselHalfHeight = 0.20;
        // A pixel belongs to the deck when the own pass changed its luminance by more than this share of the baseline (and by more than DeckMinChange).
        internal const double DeckRelativeChange = 0.15, DeckMinChange = 0.005;
        // Every Stride-th pixel in x and y is read (the statistics need a few hundred thousand samples at most).
        internal const int Stride = 2;
        // A channel at or above this value counts as clipped.
        internal const float ClipValue = 0.999f;

        internal struct Stat
        {
            internal int Count;
            internal float P10, Median, P90, R, G, B;
            // The share of the region's samples with a channel at or above ClipValue.
            internal float Clipped;
        }

        internal sealed class Reading
        {
            internal int Width, Height;
            internal double HorizonV;
            internal Stat Sky, Ground, Deck;
            // The share of the picture's samples that belong to the deck.
            internal double DeckFraction;
            // The largest channel of the picture.
            internal float Peak;
            // Luminance per pixel (kept for the deck of the next reading), and the deck mask of this reading (null for the baseline).
            internal float[] Luma;
            internal bool[] DeckMask;
        }

        // The row (0 bottom, 1 top) of the horizon in the picture, from the camera's elevation (degrees above the horizontal), its vertical field of view, its height above
        // the sea and the body's radius. The horizon lies below the horizontal by acos(R / (R + h)); its angle from the view axis is that dip plus the elevation.
        internal static double HorizonV(double elevationDeg, double fovDeg, double cameraHeightM, double radiusM)
        {
            double dip = Math.Acos(radiusM / (radiusM + Math.Max(0.0, cameraHeightM))) * 180.0 / Math.PI;
            double fromAxis = (-dip - elevationDeg) * Math.PI / 180.0;
            double half = Math.Tan(fovDeg * 0.5 * Math.PI / 180.0);
            if (half <= 1e-9) return 0.5;
            return 0.5 + Math.Tan(fromAxis) / (2.0 * half);
        }

        internal static bool InVessel(double u, double v)
        {
            return Math.Abs(u - 0.5) < VesselHalfWidth && Math.Abs(v - 0.5) < VesselHalfHeight;
        }

        internal static float Luminance(Color c)
        {
            return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        }

        // Reads one frame. baseline is the reading of the frame without the own pass at the same pose (null while reading the baseline itself); the deck is then empty.
        // mask, where given, replaces the deck test (the aid pictures take the deck of the natural frame next to them).
        internal static Reading Measure(Color[] raw, int width, int height, double horizonV, Reading baseline, bool[] mask = null)
        {
            if (raw == null || raw.Length != width * height || width < 4 || height < 4) throw new ArgumentException("the raw picture does not match its size");
            var reading = new Reading { Width = width, Height = height, HorizonV = horizonV, Luma = new float[raw.Length] };
            bool compare = baseline != null && baseline.Width == width && baseline.Height == height;
            if (compare && mask == null) reading.DeckMask = new bool[raw.Length];
            else if (mask != null && mask.Length == raw.Length) reading.DeckMask = mask;
            var sky = new Region(); var ground = new Region(); var deck = new Region();
            int samples = 0, deckSamples = 0;
            for (int y = 0; y < height; y++)
            {
                double v = (y + 0.5) / height;
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    Color c = raw[i];
                    float l = Luminance(c);
                    reading.Luma[i] = l;
                    reading.Peak = Math.Max(reading.Peak, Math.Max(c.r, Math.Max(c.g, c.b)));
                    if (x % Stride != 0 || y % Stride != 0) continue;
                    double u = (x + 0.5) / width;
                    if (v < EdgeLow || v > EdgeHigh || InVessel(u, v)) continue;
                    samples++;
                    if (v > horizonV + HorizonMargin) sky.Add(c, l);
                    else if (v < horizonV - HorizonMargin) ground.Add(c, l);
                    bool inDeck;
                    if (mask != null && mask.Length == raw.Length) inDeck = mask[i];
                    else if (compare)
                    {
                        float o = baseline.Luma[i];
                        inDeck = Math.Abs(l - o) > Math.Max(DeckMinChange, DeckRelativeChange * o);
                        reading.DeckMask[i] = inDeck;
                    }
                    else inDeck = false;
                    if (inDeck)
                    {
                        deck.Add(c, l);
                        deckSamples++;
                    }
                }
            }
            reading.Sky = sky.Finish();
            reading.Ground = ground.Finish();
            reading.Deck = deck.Finish();
            reading.DeckFraction = samples == 0 ? 0.0 : deckSamples / (double)samples;
            return reading;
        }

        // --- Noise: two frames of one pose a frame apart -------------------------------------------------------------------------------------

        // A pixel is in the noise region when the frame with the pass (a) differs from the baseline of the same pose by the deck's rule (DeckRelativeChange, DeckMinChange); the vessel
        // box and the picture's top and bottom edge are left out. Every pixel is read (no stride): the high-pass needs the neighbours.
        internal struct NoiseReading
        {
            // Pixels in the region and their share of the picture.
            internal int Count;
            internal double RegionFraction;
            // The mean luminance of frame a in the region (the scale the other numbers are read against).
            internal double MeanLuma;
            // Flicker: the mean absolute difference of luminance between a and b over the region (what shimmers from one frame to the next), and the same between the two
            // baseline frames over the same region (the floor of everything that is not the pass: the camera's jitter, the sea, the sky).
            internal double Flicker, FloorFlicker;
            // High-frequency energy: the root mean square of (luminance minus the mean of its four neighbours) over the region pixels whose four neighbours are in the region too
            // (the cloud's own edges are left out), averaged over a and b. White noise of deviation s reads 1.118 s.
            internal double Hf;
            internal int HfCount;
        }

        internal const int NoiseMinPixels = 500;

        internal static NoiseReading Noise(Color[] baseline, Color[] baseline2, Color[] a, Color[] b, int width, int height)
        {
            int n = width * height;
            if (baseline == null || baseline2 == null || a == null || b == null || baseline.Length != n || baseline2.Length != n || a.Length != n || b.Length != n || width < 4 || height < 4)
                throw new ArgumentException("the four frames do not match the size");
            var la = new float[n]; var lb = new float[n]; var l0 = new float[n]; var l1 = new float[n];
            for (int i = 0; i < n; i++)
            {
                la[i] = Luminance(a[i]);
                lb[i] = Luminance(b[i]);
                l0[i] = Luminance(baseline[i]);
                l1[i] = Luminance(baseline2[i]);
            }
            var region = new bool[n];
            var reading = new NoiseReading();
            double lumaSum = 0.0, flickerSum = 0.0, floorSum = 0.0;
            for (int y = 0; y < height; y++)
            {
                double v = (y + 0.5) / height;
                if (v < EdgeLow || v > EdgeHigh) continue;
                for (int x = 0; x < width; x++)
                {
                    double u = (x + 0.5) / width;
                    if (InVessel(u, v)) continue;
                    int i = y * width + x;
                    float o = l0[i];
                    if (Math.Abs(la[i] - o) <= Math.Max(DeckMinChange, DeckRelativeChange * o)) continue;
                    region[i] = true;
                    reading.Count++;
                    lumaSum += la[i];
                    flickerSum += Math.Abs(lb[i] - la[i]);
                    floorSum += Math.Abs(l1[i] - l0[i]);
                }
            }
            reading.RegionFraction = reading.Count / (double)n;
            if (reading.Count == 0) return reading;
            reading.MeanLuma = lumaSum / reading.Count;
            reading.Flicker = flickerSum / reading.Count;
            reading.FloorFlicker = floorSum / reading.Count;
            double hfSum = 0.0;
            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    int i = y * width + x;
                    if (!region[i] || !region[i - 1] || !region[i + 1] || !region[i - width] || !region[i + width]) continue;
                    double da = la[i] - 0.25 * (la[i - 1] + la[i + 1] + la[i - width] + la[i + width]);
                    double db = lb[i] - 0.25 * (lb[i - 1] + lb[i + 1] + lb[i - width] + lb[i + width]);
                    hfSum += 0.5 * (da * da + db * db);
                    reading.HfCount++;
                }
            }
            reading.Hf = reading.HfCount > 0 ? Math.Sqrt(hfSum / reading.HfCount) : 0.0;
            return reading;
        }

        // The sampled values of one region.
        private sealed class Region
        {
            private readonly List<float> luma = new List<float>(), r = new List<float>(), g = new List<float>(), b = new List<float>();
            private int clipped;

            internal void Add(Color c, float l)
            {
                luma.Add(l);
                r.Add(c.r);
                g.Add(c.g);
                b.Add(c.b);
                if (c.r >= ClipValue || c.g >= ClipValue || c.b >= ClipValue) clipped++;
            }

            internal Stat Finish()
            {
                var s = new Stat { Count = luma.Count };
                if (s.Count == 0) return s;
                s.P10 = Percentile(luma, 0.10);
                s.Median = Percentile(luma, 0.50);
                s.P90 = Percentile(luma, 0.90);
                s.R = Percentile(r, 0.50);
                s.G = Percentile(g, 0.50);
                s.B = Percentile(b, 0.50);
                s.Clipped = clipped / (float)s.Count;
                return s;
            }
        }

        // The value at a fraction of the sorted list (nearest rank); the list is sorted in place.
        internal static float Percentile(List<float> values, double fraction)
        {
            if (values.Count == 0) return 0f;
            values.Sort();
            int index = (int)Math.Min(values.Count - 1, Math.Max(0, Math.Floor(fraction * (values.Count - 1) + 0.5)));
            return values[index];
        }

        // value / reference, or NaN where the reference is not usable (empty region or a value under 1e-6).
        internal static double Ratio(float value, int valueCount, float reference, int referenceCount)
        {
            if (valueCount <= 0 || referenceCount <= 0 || reference <= 1e-6f) return double.NaN;
            return value / (double)reference;
        }
    }
}
