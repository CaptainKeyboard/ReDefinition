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
            // The 10th and 90th percentile of the red channel (the aid frame of the light carries the direct light in it).
            internal float RP10, RP90;
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
            // The raw picture, kept for the baseline reading only (the closed share of the next readings is measured against its colours).
            internal Color[] Raw;
            // The share of the region's samples that belong to the deck, and the share that count as closed (ClosedShare). Zero where the frame has no baseline.
            internal double SkyDeckShare, GroundDeckShare, SkyClosedShare, GroundClosedShare;
            // E40 look B (the horizon): the band just above the horizon (HorizonMargin of the picture's height) that SkyClosedShare leaves out. The samples in it, the share that
            // changed by ClosedChange or more against the baseline (a hard edge of the deck at the horizon reads near 1, a deck that has faded into the sky near 0), and the median
            // change of a channel there. Zero where the frame has no baseline.
            internal int HorizonBandSamples;
            internal double HorizonBandClosed, HorizonBandChange;
            // The colour next to the horizon row (MeasureHorizonLine); Count 0 where the row is too near the picture's edge.
            internal HorizonLine Line;
            // The vertical row profile at the horizon (MeasureRowProfile); null bands where it was not read.
            internal RowProfile Profile;
        }

        // CLOSED, as far as a picture can say it (the target's alpha is not read): a region sample is closed where the largest change of a channel against the baseline pixel is
        // at least ClosedChange (display units, the product's stage), over ALL the region's samples. A cloud of opacity a over a background it differs from by d in some channel
        // changes the pixel by a * d; the sky, the sea and a deck's colour differ by 0.25 or more in the channel that matters (blue sky 0.7 against a beige or white deck 0.45 to 0.6),
        // so 0.15 reads a >= 0.6, which is a closed deck within the model's own ramp (CloudCoverModel: the share at optical depth 0.92 and at 4.6 differ by 0.03 or less).
        // The one threshold was set from the arithmetic above and then looked at over the saved pictures of K-T2d start 48 (2026-11-13, the p1/c42/c35 frames of the four
        // view/cover pairs against their baselines): on the ground-view sheet and the cumulus towers the share moves by 0.02 or less for any threshold from 0.10 to 0.20 (the
        // plateau of a closed deck is far from both), on the thin sheets seen from above by 0.14 (0.88 to 0.74 at the low cover). It was not fitted to them.
        // The old measure (deck samples at 0.9 of the region's 90th percentile of change) took the spread of a sheet's own brightness for its edge and read 0.25 for a sheet
        // that the pictures show closed; the luminance rule of the deck missed a beige sheet over a sky of the same luminance.
        internal const double ClosedChange = 0.15;

        internal static float ChannelChange(Color a, Color b)
        {
            return Math.Max(Math.Abs(a.r - b.r), Math.Max(Math.Abs(a.g - b.g), Math.Abs(a.b - b.b)));
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
            if (baseline == null) reading.Raw = raw;
            bool changes = compare && baseline.Raw != null && baseline.Raw.Length == raw.Length;
            var skyChange = new List<float>(); var groundChange = new List<float>(); var bandChange = new List<float>();
            int samples = 0, deckSamples = 0, skyClosed = 0, groundClosed = 0, bandClosed = 0;
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
                    if (changes)
                    {
                        // Closed by the colour change, over every sample of the region (a deck whose luminance equals the sky's is still closed).
                        bool closed = ChannelChange(c, baseline.Raw[i]) >= ClosedChange;
                        if (closed && v > horizonV + HorizonMargin) skyClosed++;
                        else if (closed && v < horizonV - HorizonMargin) groundClosed++;
                        if (v > horizonV && v <= horizonV + HorizonMargin)
                        {
                            bandChange.Add(ChannelChange(c, baseline.Raw[i]));
                            if (closed) bandClosed++;
                        }
                    }
                    if (inDeck)
                    {
                        deck.Add(c, l);
                        deckSamples++;
                        if (changes)
                        {
                            if (v > horizonV + HorizonMargin) skyChange.Add(ChannelChange(c, baseline.Raw[i]));
                            else if (v < horizonV - HorizonMargin) groundChange.Add(ChannelChange(c, baseline.Raw[i]));
                        }
                    }
                }
            }
            reading.Sky = sky.Finish();
            reading.Ground = ground.Finish();
            reading.Deck = deck.Finish();
            reading.DeckFraction = samples == 0 ? 0.0 : deckSamples / (double)samples;
            if (changes)
            {
                reading.SkyDeckShare = reading.Sky.Count == 0 ? 0.0 : skyChange.Count / (double)reading.Sky.Count;
                reading.GroundDeckShare = reading.Ground.Count == 0 ? 0.0 : groundChange.Count / (double)reading.Ground.Count;
                reading.SkyClosedShare = reading.Sky.Count == 0 ? 0.0 : skyClosed / (double)reading.Sky.Count;
                reading.GroundClosedShare = reading.Ground.Count == 0 ? 0.0 : groundClosed / (double)reading.Ground.Count;
                reading.HorizonBandSamples = bandChange.Count;
                if (bandChange.Count > 0)
                {
                    reading.HorizonBandClosed = bandClosed / (double)bandChange.Count;
                    bandChange.Sort();
                    reading.HorizonBandChange = bandChange[bandChange.Count / 2];
                }
            }
            return reading;
        }

        // --- The colour just above and just below the sea horizon's row (the order pair of K-T2d start 52) ---------------------------------------------------------

        // The columns (fractions of the width) read for the horizon line, each LineColumnPixels wide; all outside the vessel's box. The rows: LineNear to LineFar of the picture's height
        // above the horizon row (sky side) and below it (sea side). The horizon row is computed (HorizonV), not found.
        internal static readonly double[] LineColumns = { 0.10, 0.20, 0.30, 0.70, 0.80, 0.90 };
        internal const int LineColumnPixels = 3;
        internal const double LineNear = 0.008, LineFar = 0.028;

        internal struct HorizonLine
        {
            internal int Count;
            // The median colour of the frame above and below the horizon row, and the median change against the baseline frame (zero where there is none).
            internal Color Above, Below, DeltaAbove, DeltaBelow;
            internal bool HasDelta;
            // The largest channel of |DeltaAbove - DeltaBelow|: the step the cloud's own colour makes across the horizon row (0: none).
            internal float Step;
            // The largest channel of |Above - Below|: the step of the frame's own colour across the row. Where a cloud covers both sides it is near 0 (the baseline's own step is what sky
            // and sea differ by); a cloud that takes the sea's haze below the row and none above it reads far above the baseline's.
            internal float StepColour;
        }

        private static float MedianOf(List<float> v)
        {
            if (v.Count == 0) return 0f;
            v.Sort();
            return v[v.Count / 2];
        }

        // Reads the colour in the two bands next to the horizon row, over a few columns. baselineRaw: the baseline frame's raw picture of the same pose (null for the baseline itself).
        internal static HorizonLine MeasureHorizonLine(Color[] raw, Color[] baselineRaw, int width, int height, double horizonV)
        {
            var line = new HorizonLine();
            if (raw == null || raw.Length != width * height || width < 8 || height < 8) return line;
            if (horizonV - LineFar < EdgeLow || horizonV + LineFar > EdgeHigh) return line;
            bool delta = baselineRaw != null && baselineRaw.Length == raw.Length;
            var aboveC = new List<float>[3]; var belowC = new List<float>[3]; var aboveD = new List<float>[3]; var belowD = new List<float>[3];
            for (int c = 0; c < 3; c++)
            {
                aboveC[c] = new List<float>(); belowC[c] = new List<float>(); aboveD[c] = new List<float>(); belowD[c] = new List<float>();
            }
            int n = 0;
            foreach (double u in LineColumns)
            {
                int x0 = (int)Math.Round(u * width) - LineColumnPixels / 2;
                for (int dx = 0; dx < LineColumnPixels; dx++)
                {
                    int x = x0 + dx;
                    if (x < 0 || x >= width) continue;
                    for (int side = 0; side < 2; side++)
                    {
                        double lo = side == 0 ? horizonV + LineNear : horizonV - LineFar, hi = side == 0 ? horizonV + LineFar : horizonV - LineNear;
                        int yLow = (int)Math.Ceiling(lo * height - 0.5), yHigh = (int)Math.Floor(hi * height - 0.5);
                        for (int y = yLow; y <= yHigh; y++)
                        {
                            if (y < 0 || y >= height) continue;
                            int i = y * width + x;
                            Color p = raw[i];
                            List<float>[] cs = side == 0 ? aboveC : belowC, ds = side == 0 ? aboveD : belowD;
                            cs[0].Add(p.r); cs[1].Add(p.g); cs[2].Add(p.b);
                            if (delta)
                            {
                                Color b = baselineRaw[i];
                                ds[0].Add(p.r - b.r); ds[1].Add(p.g - b.g); ds[2].Add(p.b - b.b);
                            }
                            if (side == 0) n++;
                        }
                    }
                }
            }
            line.Count = n;
            line.Above = new Color(MedianOf(aboveC[0]), MedianOf(aboveC[1]), MedianOf(aboveC[2]), 1f);
            line.Below = new Color(MedianOf(belowC[0]), MedianOf(belowC[1]), MedianOf(belowC[2]), 1f);
            line.StepColour = Math.Max(Math.Abs(line.Above.r - line.Below.r), Math.Max(Math.Abs(line.Above.g - line.Below.g), Math.Abs(line.Above.b - line.Below.b)));
            if (delta)
            {
                line.HasDelta = true;
                line.DeltaAbove = new Color(MedianOf(aboveD[0]), MedianOf(aboveD[1]), MedianOf(aboveD[2]), 1f);
                line.DeltaBelow = new Color(MedianOf(belowD[0]), MedianOf(belowD[1]), MedianOf(belowD[2]), 1f);
                line.Step = Math.Max(Math.Abs(line.DeltaAbove.r - line.DeltaBelow.r), Math.Max(Math.Abs(line.DeltaAbove.g - line.DeltaBelow.g), Math.Abs(line.DeltaAbove.b - line.DeltaBelow.b)));
            }
            return line;
        }

        // --- The vertical row profile at the horizon (E40 start 52 follow-up) --------------------------------------------------------------------------------
        // MeasureHorizonLine reads six narrow columns in two thin bands, which hit the ridge and the mound rims of a deck and are as wide as the effect they look for (the noise floor
        // of its step was about 0.03 against a haze effect of 0.055). The profile reads every column of a band instead and drops the samples that are not the deck's body: of the
        // samples that changed against the baseline by ClosedChange or more, those whose luminance is more than MoundTolerance away from the band's median (a mound's lit side, a ridge's dark
        // rim, a hole onto the sea) are counted as rejected and left out of the colour. The bands lie below the horizon row (index 0 nearest the row) and above it, each ProfileBandHeight of
        // the picture's height, starting ProfileGap from the row. The colour of the baseline over the same samples is read beside it: a deck that fades into the haze approaches it
        // towards the horizon.
        internal const int ProfileBands = 5;
        internal const double ProfileBandHeight = 0.02, ProfileGap = 0.004, MoundTolerance = 0.08;
        internal const int ProfileMinSamples = 20;

        internal struct ProfileBand
        {
            // The samples read in the band (the vessel's box and the picture's edges left out), those that changed by ClosedChange or more, and of those the rejected ones.
            internal int Count, Changed, Rejected;
            // The median colour of the kept samples in the frame and in the baseline (zero where fewer than ProfileMinSamples are kept).
            internal Color Frame, Baseline;
            internal bool HasColour;
        }

        // The profile of one frame: Below[k] is the band k ProfileBandHeight below the row (k 0 nearest), Above[k] the same above it. baselineRaw null: no change is known, every sample is
        // taken as changed and the baseline colour stays zero.
        internal struct RowProfile
        {
            internal ProfileBand[] Below, Above;
        }

        internal static RowProfile MeasureRowProfile(Color[] raw, Color[] baselineRaw, int width, int height, double horizonV)
        {
            var profile = new RowProfile { Below = new ProfileBand[ProfileBands], Above = new ProfileBand[ProfileBands] };
            if (raw == null || raw.Length != width * height || width < 8 || height < 8) return profile;
            bool delta = baselineRaw != null && baselineRaw.Length == raw.Length;
            for (int side = 0; side < 2; side++)
            {
                for (int k = 0; k < ProfileBands; k++)
                {
                    double lo = side == 0 ? horizonV - ProfileGap - (k + 1) * ProfileBandHeight : horizonV + ProfileGap + k * ProfileBandHeight;
                    double hi = lo + ProfileBandHeight;
                    ProfileBand band = ReadBand(raw, delta ? baselineRaw : null, width, height, lo, hi);
                    if (side == 0) profile.Below[k] = band; else profile.Above[k] = band;
                }
            }
            return profile;
        }

        private static ProfileBand ReadBand(Color[] raw, Color[] baselineRaw, int width, int height, double lo, double hi)
        {
            var band = new ProfileBand();
            if (lo < EdgeLow || hi > EdgeHigh) return band;
            int yLow = (int)Math.Ceiling(lo * height - 0.5), yHigh = (int)Math.Floor(hi * height - 0.5);
            var indexes = new List<int>();
            var luma = new List<float>();
            for (int y = yLow; y <= yHigh; y++)
            {
                if (y < 0 || y >= height) continue;
                double v = (y + 0.5) / height;
                for (int x = 0; x < width; x += Stride)
                {
                    if (InVessel((x + 0.5) / width, v)) continue;
                    int i = y * width + x;
                    band.Count++;
                    if (baselineRaw != null && ChannelChange(raw[i], baselineRaw[i]) < ClosedChange) continue;
                    indexes.Add(i);
                    luma.Add(Luminance(raw[i]));
                }
            }
            band.Changed = indexes.Count;
            if (indexes.Count == 0) return band;
            var sorted = new List<float>(luma);
            float median = Percentile(sorted, 0.5);
            var fr = new List<float>(); var fg = new List<float>(); var fb = new List<float>();
            var br = new List<float>(); var bg = new List<float>(); var bb = new List<float>();
            for (int n = 0; n < indexes.Count; n++)
            {
                if (Math.Abs(luma[n] - median) > MoundTolerance)
                {
                    band.Rejected++;
                    continue;
                }
                Color c = raw[indexes[n]];
                fr.Add(c.r); fg.Add(c.g); fb.Add(c.b);
                if (baselineRaw != null)
                {
                    Color b = baselineRaw[indexes[n]];
                    br.Add(b.r); bg.Add(b.g); bb.Add(b.b);
                }
            }
            if (fr.Count < ProfileMinSamples) return band;
            band.HasColour = true;
            band.Frame = new Color(Percentile(fr, 0.5), Percentile(fg, 0.5), Percentile(fb, 0.5), 1f);
            if (br.Count > 0) band.Baseline = new Color(Percentile(br, 0.5), Percentile(bg, 0.5), Percentile(bb, 0.5), 1f);
            return band;
        }

        // --- Optical depth from the aid picture (aid mode 1 of the layer shader) -------------------------------------------------------------------

        // The aid picture draws a pixel as Heat(x) with x = (log10(tau) + 3) / 4.5 (the shader's Pass0, aid 1) and composites it with Blend One OneMinusSrcAlpha over the scene, alpha = 1 - exp(-tau),
        // so a pixel is Heat(x) + scene * exp(-tau). Where no cloud is (tau < 1e-4) the pixel is the scene. The decode takes the tau whose prediction is nearest the pixel; x = 1 stands for
        // tau of 31.6 or more (the heat map saturates there).
        internal const double AidTauMinimum = 1e-4, AidTauSaturated = 31.6227766;
        private const int AidSteps = 90;

        // The shader's Heat().
        internal static Color AidHeat(double x)
        {
            double t = Math.Max(0.0, Math.Min(1.0, x));
            if (t < 0.5)
            {
                float k = (float)(t * 2.0);
                return new Color(0.05f + (0.20f - 0.05f) * k, 0.05f + (0.80f - 0.05f) * k, 0.30f + (0.50f - 0.30f) * k, 1f);
            }
            float m = (float)((t - 0.5) * 2.0);
            return new Color(0.20f + (0.95f - 0.20f) * m, 0.80f + (0.20f - 0.80f) * m, 0.50f + (0.10f - 0.50f) * m, 1f);
        }

        // The pixel the aid picture shows for an optical depth over a scene colour.
        internal static Color AidPixel(double tau, Color scene)
        {
            if (tau < AidTauMinimum) return scene;
            double x = (Math.Log10(tau) + 3.0) / 4.5;
            Color h = AidHeat(x);
            float t = (float)Math.Exp(-tau);
            return new Color(h.r + scene.r * t, h.g + scene.g * t, h.b + scene.b * t, 1f);
        }

        private static double[] aidTaus;
        private static Color[] aidHeat;

        private static void AidTable()
        {
            if (aidTaus != null) return;
            var taus = new double[AidSteps + 1];
            var heat = new Color[AidSteps + 1];
            taus[0] = 0.0;
            for (int i = 1; i <= AidSteps; i++)
            {
                double x = (i - 1) / (double)(AidSteps - 1);
                taus[i] = Math.Pow(10.0, 4.5 * x - 3.0);
                heat[i] = AidHeat(x);
            }
            aidHeat = heat;
            aidTaus = taus;
        }

        // The optical depth an aid pixel shows over a scene colour (0 for no cloud, AidTauSaturated for the top of the scale).
        internal static double AidTauOf(Color pixel, Color scene)
        {
            AidTable();
            int best = 0;
            double bestErr = double.MaxValue;
            for (int i = 0; i <= AidSteps; i++)
            {
                double tau = aidTaus[i];
                double t = i == 0 ? 0.0 : Math.Exp(-tau);
                double r = i == 0 ? scene.r : aidHeat[i].r + scene.r * t, g = i == 0 ? scene.g : aidHeat[i].g + scene.g * t, b = i == 0 ? scene.b : aidHeat[i].b + scene.b * t;
                double e = (r - pixel.r) * (r - pixel.r) + (g - pixel.g) * (g - pixel.g) + (b - pixel.b) * (b - pixel.b);
                if (e < bestErr)
                {
                    bestErr = e;
                    best = i;
                }
            }
            return aidTaus[best];
        }

        internal struct AidTauReading
        {
            internal int Count;
            // The percentiles of the optical depth over the sky samples, the share at or above CloudLookModel.ClosedTau (the closed measure of the oracle) and at or above 4.6, and the
            // share at or above the heat map's top (tau 31.6).
            internal double P10, Median, P90, ShareClosed, ShareDeep, ShareSaturated;
            // The optical depth in the top rows of the picture (the sky nearest the zenith the view holds), median.
            internal double TopMedian;
            internal int TopCount;
        }

        // The optical depth over the sky samples of an aid frame (the same region as Measure's sky: above the horizon line plus the margin, the vessel box and the edges left out), read
        // against the baseline frame's colours as the scene.
        // below: read the sea or ground region (under the horizon line minus the margin) instead, for the views that look down on a deck (the 4.6 km, 2.3 km and above-the-deck frames);
        // the "top" rows are then the bottom ones (v under 0.15).
        internal static AidTauReading MeasureAidTau(Color[] aid, Color[] scene, int width, int height, double horizonV, int stride = 4, bool below = false)
        {
            if (aid == null || scene == null || aid.Length != width * height || scene.Length != aid.Length) throw new ArgumentException("the aid picture does not match the baseline");
            var all = new List<double>();
            var top = new List<double>();
            for (int y = 0; y < height; y += stride)
            {
                double v = (y + 0.5) / height;
                if (v < EdgeLow || v > EdgeHigh) continue;
                if (below ? v >= horizonV - HorizonMargin : v <= horizonV + HorizonMargin) continue;
                for (int x = 0; x < width; x += stride)
                {
                    double u = (x + 0.5) / width;
                    if (InVessel(u, v)) continue;
                    double tau = AidTauOf(aid[y * width + x], scene[y * width + x]);
                    all.Add(tau);
                    if (below ? v < 0.15 : v > 0.85) top.Add(tau);
                }
            }
            var r = new AidTauReading { Count = all.Count, TopCount = top.Count };
            if (all.Count == 0) return r;
            all.Sort();
            r.P10 = all[(int)Math.Round(0.1 * (all.Count - 1))];
            r.Median = all[(int)Math.Round(0.5 * (all.Count - 1))];
            r.P90 = all[(int)Math.Round(0.9 * (all.Count - 1))];
            int closed = 0, deep = 0, sat = 0;
            foreach (double t in all)
            {
                if (t >= 0.92) closed++;
                if (t >= 4.6) deep++;
                if (t >= AidTauSaturated * 0.999) sat++;
            }
            r.ShareClosed = closed / (double)all.Count;
            r.ShareDeep = deep / (double)all.Count;
            r.ShareSaturated = sat / (double)all.Count;
            if (top.Count > 0)
            {
                top.Sort();
                r.TopMedian = top[top.Count / 2];
            }
            return r;
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
            // baseline frames over the same region (the floor of everything that is not the pass: the camera's jitter, the sea, the sky). Both are read with frame b scaled by
            // the ratio of the two frames' mean luminance over the region (GainAB, FloorGain): a change of the whole picture's brightness between two frames (the weather
            // writer's sun factor, an exposure) is not shimmer, and in K-T2 start 46 it was most of what the plain and recon3 ground rows read (0.034 against a grain of 0.005).
            // FlickerRaw and FloorFlickerRaw are the differences as they were before the scaling.
            internal double Flicker, FloorFlicker, FlickerRaw, FloorFlickerRaw;
            // Mean luminance of b over that of a in the region, and of the second baseline frame over the first.
            internal double GainAB, FloorGain;
            // The pixels the reading was taken over (the picture's size); a fixed mask handed in is returned as it was.
            internal bool[] Region;
            // High-frequency energy: the root mean square of (luminance minus the mean of its four neighbours) over the region pixels whose four neighbours are in the region too
            // (the cloud's own edges are left out), averaged over a and b. White noise of deviation s reads 1.118 s.
            internal double Hf;
            internal int HfCount;
        }

        internal const int NoiseMinPixels = 500;

        // fixedRegion (may be null): the pixels to read over instead of the ones the frame a changed against its baseline, so that the levels of one view are all read over the
        // same pixels (the plain pass's region): with a region of its own each level averages over a different mix of flat deck, cloud edges and rain streaks.
        internal static NoiseReading Noise(Color[] baseline, Color[] baseline2, Color[] a, Color[] b, int width, int height, bool[] fixedRegion = null)
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
            if (fixedRegion != null && fixedRegion.Length != n) throw new ArgumentException("the fixed region does not match the size");
            var region = new bool[n];
            var reading = new NoiseReading { Region = region };
            double lumaSum = 0.0, lumaSumB = 0.0, lumaSum0 = 0.0, lumaSum1 = 0.0, flickerSum = 0.0, floorSum = 0.0;
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
                    if (fixedRegion != null)
                    {
                        if (!fixedRegion[i]) continue;
                    }
                    else if (Math.Abs(la[i] - o) <= Math.Max(DeckMinChange, DeckRelativeChange * o)) continue;
                    region[i] = true;
                    reading.Count++;
                    lumaSum += la[i];
                    lumaSumB += lb[i];
                    lumaSum0 += l0[i];
                    lumaSum1 += l1[i];
                    flickerSum += Math.Abs(lb[i] - la[i]);
                    floorSum += Math.Abs(l1[i] - l0[i]);
                }
            }
            reading.RegionFraction = reading.Count / (double)n;
            if (reading.Count == 0) return reading;
            reading.MeanLuma = lumaSum / reading.Count;
            reading.FlickerRaw = flickerSum / reading.Count;
            reading.FloorFlickerRaw = floorSum / reading.Count;
            reading.GainAB = lumaSum > 1e-9 ? lumaSumB / lumaSum : 1.0;
            reading.FloorGain = lumaSum0 > 1e-9 ? lumaSum1 / lumaSum0 : 1.0;
            // The scaled differences: b and the second baseline frame divided by their gain against a and the first baseline frame.
            double scaledFlicker = 0.0, scaledFloor = 0.0;
            double invB = reading.GainAB > 1e-6 ? 1.0 / reading.GainAB : 1.0, inv1 = reading.FloorGain > 1e-6 ? 1.0 / reading.FloorGain : 1.0;
            for (int i = 0; i < n; i++)
            {
                if (!region[i]) continue;
                scaledFlicker += Math.Abs(lb[i] * invB - la[i]);
                scaledFloor += Math.Abs(l1[i] * inv1 - l0[i]);
            }
            reading.Flicker = scaledFlicker / reading.Count;
            reading.FloorFlicker = scaledFloor / reading.Count;
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
                s.RP10 = Percentile(r, 0.10);
                s.RP90 = Percentile(r, 0.90);
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
