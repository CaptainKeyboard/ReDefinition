using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // What the stage reads besides the layer's own two maps: the field's other maps as bytes (null: the stage uses the neutral value), the time, and the pattern's drift.
    internal sealed class CloudMesoInputs
    {
        internal int Width, Height;
        // The field's precipitation level (the type map of the field), 0 to 255.
        internal byte[] Precip;
        // The near-surface relative humidity and the surface map (WeatherMapKind.Surface) as bytes; null: 0.5.
        internal byte[] Humidity, Surface;
        // The layer maps at the mid-level sheet's height and at the fair-weather type's height (CloudLookTuning.ApplyAlto); null: no sheet.
        internal byte[] AltoMid, AltoLow;
        internal double Ut;
        // The rate (rad/s, eastward) the patterns turn at (GeneratorField.CloudDriftRadS) and the body's seed.
        internal double DriftRadS;
        internal ulong Seed;
        // The body's scales (GeneratorField.CloudScalesOf): the radius, the depth of the troposphere (the unit of the patterns' sizes) and the latitudes (rad) of the edges of the first two
        // circulation cells (the unit of the climate's latitude bands; NaN: plain latitude). A caller sets them from its body; without a radius and a depth (0) the stage changes nothing.
        internal double RadiusKm, DepthKm;        internal double Edge1Rad = double.NaN, Edge2Rad = double.NaN;
        // The tropopause's share of the atmosphere's depth where a body does not state its tropopause (GeneratorRules.TropopauseDepthShare's default).
        internal const double DefaultTropopauseShare = 0.15;
    }

    // E40 L3: the stage between the generator's maps and the layer pass that gives a layer's cover and type the mesoscale sizes of the body: patterns of a few to ten depths of its troposphere,
    // its climate bands following its circulation cells (see CloudMesoRules.cs; P1: no size or latitude is Kerbin's).
    //
    // The global maps are 512 x 256 (7.4 km a texel on Kerbin), made of frontal systems and ITCZ clusters of the body's system size; the layer pass multiplies them with noise of a few km. This
    // stage fills the band between, with two patterns of noise on the sphere that turn with the layer's wind:
    //  - cover: the map m of a texel is replaced by (1 - a) m + a x B, B the pattern of cloud and gap whose share is m (the noise rank against 1 - m, with a soft edge), so a region keeps
    //    its mean cover and gets fields and clear gaps of the pattern's size; where the field rains the cover stays (RainKeep);
    //  - type: the types of a layer with CLIMATE nodes are chosen by their weights at the texel (latitude, surface, humidity, precipitation) against a patch noise: a region where two
    //    types are likely shows both, in patches of TypePatchKm, and the type axis is walked in order, so a transition between two types passes no third type (a zero-weight type is
    //    jumped over inside the narrow transition). The mid-level sheet is chosen by the existing rule (CloudLookTuning.AltoShare) as a share that is cut hard, not blended.
    // Everything is a pure function of (maps, layer data, seed, UT, drift). The noise is computed once per (seed, scale, octaves, stretch) on a grid twice as fine along the longitude
    // and read at the texel's longitude minus the drift, so a step costs a few lookups per texel.
    internal static class CloudMesoStage
    {
        private const int Oversample = 2;
        private const int Quantiles = 2048;
        private static readonly Dictionary<string, float[]> cache = new Dictionary<string, float[]>();

        // Fills coverOut and typeOut (allocated or reused by the caller; the same arrays as the inputs are returned where the stage changes nothing).
        internal static void Apply(CloudLayerDef layer, CloudMesoInputs inp, byte[] coverIn, byte[] typeIn, ref byte[] coverOut, ref byte[] typeOut)
        {
            CloudMesoSpec meso = layer.Meso;
            if (!(inp.RadiusKm > 0.0) || !(inp.DepthKm > 0.0))
            {
                coverOut = coverIn;
                typeOut = typeIn;
                return;
            }
            if (meso != null && meso.On) ApplyCover(layer, meso, inp, coverIn, ref coverOut);
            else coverOut = coverIn;
            if (layer.HasClimate && layer.Deck == null) ApplyType(layer, inp, typeIn, ref typeOut);
            else typeOut = typeIn;
        }

        // ---- cover ----

        private static void ApplyCover(CloudLayerDef layer, CloudMesoSpec meso, CloudMesoInputs inp, byte[] cover, ref byte[] output)
        {
            int w = inp.Width, h = inp.Height;
            if (output == null || output.Length != cover.Length || ReferenceEquals(output, cover)) output = new byte[cover.Length];
            float[] u = Pattern(inp.Seed, 11, SizeKm(meso.ScaleKm, meso.ScaleDepths, inp), meso.Octaves, meso.Stretch, w, h, inp.RadiusKm);
            float amount = meso.Amount, edge = meso.Edge, keep = meso.RainKeep, gamma = meso.Shoulder;
            double shift = ColumnShift(inp, w);
            // The edge of a field thins out into small cells (CloudMesoSpec.Scatter): a second, finer noise; null where the layer has none.
            float scatter = meso.Scatter, soft = meso.ScatterSoft;
            float[] us = scatter > 0f ? Pattern(inp.Seed, 17, SizeKm(0f, meso.ScatterDepths, inp), 2, 1f, w, h, inp.RadiusKm) : null;
            // The pattern of a cover m is a ramp over the noise rank u, from 0 at t to 1 at t + edge, raised to the power `gamma`: a field has a core of 1 and a shoulder that falls off to the gap, and
            // t is set so that the mean over u is m (the integral of the ramp is edge / (gamma + 1)); a power above 1 puts less of the area above one half than m, so that a region of m = 0.6 does not
            // close into one field. The ends of the range, where t would leave 0 to 1, scale or lift the pattern to keep its mean.
            float rampMean = edge / (gamma + 1f);
            float lift = edge * gamma / (gamma + 1f);
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    int k = j * w + i;
                    float m = cover[k] / 255f;
                    float n = Lookup(u, w, h, i, j, shift);
                    // cells: the clear part of the pattern is a band around the noise's median, a network of lanes that closes the cloud into separate cells (the ranks stay uniform)
                    if (meso.Cells) n = Math.Abs(2f * n - 1f);
                    float t = 1f - m - lift;
                    float bin;
                    if (t < 0f)
                    {
                        // a nearly closed sky: the ramp starts at 0 and the rest is lifted towards 1 so that the mean stays m
                        float ramp = Saturate(n / edge);
                        float b0 = (float)Math.Pow(ramp, gamma);
                        float mean0 = 1f - edge + rampMean;
                        bin = b0 + (m - mean0) * (1f - b0) / Math.Max(1f - mean0, 1e-4f);
                    }
                    else if (t + edge > 1f)
                    {
                        // a nearly clear sky: the ramp ends at 1 and is scaled down so that the mean stays m
                        float ramp = Saturate((n - (1f - edge)) / edge);
                        bin = (float)Math.Pow(ramp, gamma) * (m / Math.Max(rampMean, 1e-4f));
                    }
                    else bin = (float)Math.Pow(Saturate((n - t) / edge), gamma);
                    bin = Math.Min(1f, Math.Max(0f, bin));
                    if (us != null) bin += scatter * (Thin(bin, Lookup(us, w, h, i, j, shift), soft) - bin);
                    float v = m + amount * (bin - m);
                    if (keep > 0f && inp.Precip != null) v = Math.Max(v, m * keep * Smooth(0.02f, 0.25f, inp.Precip[k] / 255f));
                    output[k] = ToByte(v);
                }
            }
        }

        // The edge as a density of cells: a texel with ramp value r (0 gap, 1 core) is a cell (1) where the scatter noise u (uniform ranks) lies below r, with a soft step of width w. Over u the
        // mean of this is exactly r (the step is shifted so that the mean of its clipped ramp is r, ThinShift), so a region keeps the cover its ramp has.
        private static float Thin(float r, float u, float w)
        {
            return Saturate((ThinShift(r, w) - u) / w + 0.5f);
        }

        // The position of the soft step whose mean over a uniform u is r: the clipped ramp of width w has the mean (p + w/2)^2 / (2w) where it is cut by the lower end, and the mirror image at the upper end.
        internal static float ThinShift(float r, float w)
        {
            if (r <= 0f) return -0.5f * w;
            if (r >= 1f) return 1f + 0.5f * w;
            float half = 0.5f * w;
            if (r < half) return (float)Math.Sqrt(2f * w * r) - half;
            if (r > 1f - half) return 1f - ((float)Math.Sqrt(2f * w * (1f - r)) - half);
            return r;
        }

        // ---- type ----

        private static void ApplyType(CloudLayerDef layer, CloudMesoInputs inp, byte[] type, ref byte[] output)
        {
            int w = inp.Width, h = inp.Height;
            int n = layer.Types.Count;
            if (output == null || output.Length != type.Length || ReferenceEquals(output, type)) output = new byte[type.Length];
            int alto = layer.AltoType >= 0f && n > 1 ? (int)Math.Round(Math.Min(1f, layer.AltoType) * (n - 1)) : -1;
            var slots = new List<int>();
            for (int t = 0; t < n; t++) if (t != alto) slots.Add(t);
            int m = slots.Count;
            // The weights of each type as tables over the row's latitude and the byte of each map.
            var lat = new float[m][];
            var hum = new float[m][];
            var prec = new float[m][];
            var surf = new float[m][];
            var baseWeight = new float[m];
            for (int s = 0; s < m; s++)
            {
                CloudClimateSpec c = layer.Types[slots[s]].Climate;
                baseWeight[s] = c == null ? 1f : c.Weight;
                lat[s] = new float[h];
                for (int j = 0; j < h; j++) lat[s][j] = CloudClimateSpec.Eval(c == null ? null : c.Latitude, LatitudeCoordinate(inp, j, h));
                hum[s] = Table(c == null ? null : c.Humidity);
                prec[s] = Table(c == null ? null : c.Precipitation);
                surf[s] = Table(c == null ? null : c.Surface);
            }
            float[] altoHum = null, altoPrec = null, altoSurf = null, altoLat = null;
            float altoBase = 1f;
            if (alto >= 0)
            {
                CloudClimateSpec c = layer.Types[alto].Climate;
                altoBase = c == null ? 1f : c.Weight;
                altoLat = new float[h];
                for (int j = 0; j < h; j++) altoLat[j] = CloudClimateSpec.Eval(c == null ? null : c.Latitude, LatitudeCoordinate(inp, j, h));
                altoHum = Table(c == null ? null : c.Humidity);
                altoPrec = Table(c == null ? null : c.Precipitation);
                altoSurf = Table(c == null ? null : c.Surface);
            }
            float[] u = Pattern(inp.Seed, 29, SizeKm(layer.TypePatchKm, layer.TypePatchDepths, inp), 2, 1f, w, h, inp.RadiusKm);
            double shift = ColumnShift(inp, w);
            float blend = layer.TypePatchBlend;
            float fair = layer.FairType >= 0f ? layer.FairType : 0f;
            var weights = new float[m];
            float altoValue = alto >= 0 ? alto / (float)(n - 1) : 0f;
            bool haveAlto = alto >= 0 && inp.AltoMid != null && inp.AltoLow != null;
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    int k = j * w + i;
                    int hb = inp.Humidity == null ? 128 : inp.Humidity[k];
                    int pb = inp.Precip == null ? 0 : inp.Precip[k];
                    int sb = inp.Surface == null ? 128 : inp.Surface[k];
                    float total = 0f;
                    for (int s = 0; s < m; s++)
                    {
                        float wt = baseWeight[s] * lat[s][j] * hum[s][hb] * prec[s][pb] * surf[s][sb];
                        weights[s] = wt;
                        total += wt;
                    }
                    float value;
                    if (total <= 1e-6f) value = fair;
                    else
                    {
                        float un = Lookup(u, w, h, i, j, shift);
                        float x = 0f, cum = 0f;
                        for (int s = 0; s < m - 1; s++)
                        {
                            cum += weights[s] / total;
                            // a boundary at either end of the rank range is a type with no share: passed completely or not at all
                            x += cum <= 1e-4f ? 1f : cum >= 1f - 1e-4f ? 0f : Smooth(cum - blend, cum + blend, un);
                        }
                        int f = (int)x;
                        if (f >= m - 1) f = m - 2 < 0 ? 0 : m - 2;
                        float frac = m > 1 ? x - f : 0f;
                        float a = slots[f] / (float)(n - 1);
                        float b = slots[Math.Min(f + 1, m - 1)] / (float)(n - 1);
                        value = a + (b - a) * frac;
                    }
                    if (haveAlto)
                    {
                        float rain = pb / 255f / 0.4f;
                        rain = rain >= 1f ? 1f : rain;
                        float share = CloudLookTuning.AltoShare(inp.AltoMid[k] / 255f, inp.AltoLow[k] / 255f, layer.AltoFrom, layer.AltoFull) * (1f - rain * rain * (3f - 2f * rain));
                        share *= Math.Min(1f, altoBase * altoLat[j] * altoHum[hb] * altoPrec[pb] * altoSurf[sb]);
                        value += (altoValue - value) * Smooth(0.35f, 0.65f, share);
                    }
                    output[k] = ToByte(value);
                }
            }
        }

        // The latitude coordinate (0 at the equator, 1 at the pole) the climate's LATITUDE curves are written in: a third of the way is the edge of the first circulation cell (the tropics), two thirds
        // the edge of the second (the mid-latitudes), so a band of the curve follows the body's own circulation. A body with three cells per hemisphere at 30 and 60 degrees has the plain
        // latitude / 90 degrees; two cells (the second edge at the pole) put the middle of the range at the first edge; one cell (the edge at the pole) has the plain latitude. NaN edges: plain.
        internal static float LatitudeCoordinate(CloudMesoInputs inp, int row, int rows)
        {
            double lat = Math.Abs(90.0 - (row + 0.5) * 180.0 / rows) * (Math.PI / 180.0);
            double e1 = inp.Edge1Rad, e2 = inp.Edge2Rad, pole = 0.5 * Math.PI;
            double x = lat / pole;
            if (double.IsNaN(e1) || e1 < 0.05 || e1 >= pole - 1e-3) return (float)x;
            if (double.IsNaN(e2) || e2 >= pole - 1e-3 || e2 <= e1 + 0.05)
                return (float)(lat <= e1 ? lat / e1 / 3.0 : 1.0 / 3.0 + (lat - e1) / (pole - e1) * (2.0 / 3.0));
            if (lat <= e1) return (float)(lat / e1 / 3.0);
            if (lat <= e2) return (float)(1.0 / 3.0 + (lat - e1) / (e2 - e1) / 3.0);
            return (float)(2.0 / 3.0 + (lat - e2) / (pole - e2) / 3.0);
        }

        // The size of a pattern in km: the explicit km of the cfg where it has one, else the cfg's multiple of the body's troposphere depth; kept between two texels and a sixth of the circumference.
        internal static float SizeKm(float explicitKm, float depths, CloudMesoInputs inp)
        {
            double km = explicitKm > 0f ? explicitKm : depths * inp.DepthKm;
            double circumference = 2.0 * Math.PI * inp.RadiusKm;
            double texel = circumference / inp.Width;
            return (float)Math.Max(2.0 * texel, Math.Min(circumference / 6.0, km));
        }

        // The weight curve of a map as a table over the byte (null curve: 1).
        private static float[] Table(Curve c)
        {
            var t = new float[256];
            for (int i = 0; i < 256; i++) t[i] = CloudClimateSpec.Eval(c, i / 255f);
            return t;
        }

        // ---- the noise ----

        // How many columns of the fine pattern the longitude drift of the stage's time moves the lookup by.
        private static double ColumnShift(CloudMesoInputs inp, int w)
        {
            double turns = inp.DriftRadS * inp.Ut / (2.0 * Math.PI);
            turns -= Math.Floor(turns);
            return turns * w * Oversample;
        }

        // The pattern's value (uniform 0 to 1) at the centre of texel (i, j), the pattern turned east by `shift` columns of the fine grid.
        private static float Lookup(float[] u, int w, int h, int i, int j, double shift)
        {
            int wf = w * Oversample;
            double c = (i + 0.5) * Oversample - 0.5 - shift;
            double fl = Math.Floor(c);
            float t = (float)(c - fl);
            int c0 = (int)(((long)fl % wf + wf) % wf);
            int c1 = c0 + 1 == wf ? 0 : c0 + 1;
            int row = j * wf;
            return u[row + c0] + (u[row + c1] - u[row + c0]) * t;
        }

        // The noise on the sphere as ranks 0 to 1 (uniform), on a grid of (Oversample w) x h, made once per key. Octave o has cells of scaleKm / 2^o on the surface; the features are
        // `stretch` times longer along a parallel than across it.
        internal static float[] Pattern(ulong seed, int salt, float scaleKm, int octaves, float stretch, int w, int h, double radiusKm)
        {
            string key = seed + "|" + salt + "|" + scaleKm.ToString("R") + "|" + octaves + "|" + stretch.ToString("R") + "|" + w + "x" + h + "|" + radiusKm.ToString("R");
            lock (cache)
            {
                float[] hit;
                if (cache.TryGetValue(key, out hit)) return hit;
                float[] made = Make(seed, salt, scaleKm, octaves, stretch, w, h, radiusKm);
                if (cache.Count > 64) cache.Clear();
                cache[key] = made;
                return made;
            }
        }

        private static float[] Make(ulong seed, int salt, float scaleKm, int octaves, float stretch, int w, int h, double radiusKm)
        {
            int wf = w * Oversample;
            var v = new float[wf * h];
            uint s0 = (uint)(seed ^ (seed >> 32)) * 0x9E3779B1u + (uint)salt * 0x85EBCA6Bu;
            double baseFreq = radiusKm / Math.Max(1.0, scaleKm);
            for (int j = 0; j < h; j++)
            {
                double lat = (90.0 - (j + 0.5) * 180.0 / h) * (Math.PI / 180.0);
                double cl = Math.Cos(lat), sl = Math.Sin(lat);
                for (int i = 0; i < wf; i++)
                {
                    double lon = (-180.0 + (i + 0.5) * 360.0 / wf) * (Math.PI / 180.0);
                    double px = cl * Math.Cos(lon) / stretch, py = cl * Math.Sin(lon) / stretch, pz = sl;
                    double sum = 0.0, amp = 1.0;
                    double f = baseFreq;
                    for (int o = 0; o < octaves; o++)
                    {
                        sum += amp * Value(px * f, py * f, pz * f, s0 + (uint)o * 0x27D4EB2Fu);
                        amp *= 0.55;
                        f *= 2.0;
                    }
                    v[j * wf + i] = (float)sum;
                }
            }
            // ranks: the quantile table of the values, then each value's place in it
            var sorted = (float[])v.Clone();
            Array.Sort(sorted);
            var q = new float[Quantiles + 1];
            for (int k = 0; k <= Quantiles; k++) q[k] = sorted[Math.Min(sorted.Length - 1, (int)((long)k * (sorted.Length - 1) / Quantiles))];
            for (int k = 0; k < v.Length; k++)
            {
                int lo = 0, hi = Quantiles;
                while (hi - lo > 1)
                {
                    int mid = (lo + hi) >> 1;
                    if (q[mid] <= v[k]) lo = mid; else hi = mid;
                }
                float span = q[hi] - q[lo];
                float t = span > 1e-9f ? (v[k] - q[lo]) / span : 0.5f;
                v[k] = Math.Min(1f, Math.Max(0f, (lo + Math.Min(1f, Math.Max(0f, t))) / Quantiles));
            }
            return v;
        }

        // Smooth value noise of the integer lattice, about -1 to 1.
        private static float Value(double x, double y, double z, uint seed)
        {
            double fx = Math.Floor(x), fy = Math.Floor(y), fz = Math.Floor(z);
            int ix = (int)fx, iy = (int)fy, iz = (int)fz;
            float tx = Fade((float)(x - fx)), ty = Fade((float)(y - fy)), tz = Fade((float)(z - fz));
            float acc = 0f;
            for (int c = 0; c < 8; c++)
            {
                int dx = c & 1, dy = (c >> 1) & 1, dz = (c >> 2) & 1;
                float wgt = (dx == 1 ? tx : 1f - tx) * (dy == 1 ? ty : 1f - ty) * (dz == 1 ? tz : 1f - tz);
                acc += wgt * (Hash(ix + dx, iy + dy, iz + dz, seed) * 2f - 1f);
            }
            return acc;
        }

        private static float Fade(float t)
        {
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }

        private static float Hash(int x, int y, int z, uint seed)
        {
            uint hsh = seed ^ ((uint)x * 0x85EBCA6Bu) ^ ((uint)y * 0xC2B2AE35u) ^ ((uint)z * 0x27D4EB2Fu);
            hsh ^= hsh >> 16; hsh *= 0x7FEB352Du; hsh ^= hsh >> 15; hsh *= 0x846CA68Bu; hsh ^= hsh >> 16;
            return (hsh >> 8) * (1f / 16777216f);
        }

        // ---- small helpers ----

        private static float Saturate(float x)
        {
            return x <= 0f ? 0f : x >= 1f ? 1f : x;
        }

        private static float Smooth(float a, float b, float x)
        {
            float t = Saturate((x - a) / Math.Max(b - a, 1e-6f));
            return t * t * (3f - 2f * t);
        }

        private static byte ToByte(float v)
        {
            return (byte)Math.Min(255.0, Math.Max(0.0, Math.Round(v * 255.0, MidpointRounding.ToEven)));
        }
    }
}
