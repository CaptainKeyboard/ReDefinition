using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Turns the field's coarse local cut-out (WeatherWindow: cells of about 30 km, cloud water, updraft, temperature) into the 3D texture
    // the volume pass marches: a regular grid around the camera, east by up by north, one RGBA byte per voxel:
    //   R  cloud water, 0 to 255 over 0 to CloudWaterRef g/m3
    //   G  updraft, 0 to 255 over 0 to 40 m/s
    //   B  temperature, 0 to 255 over 180 to 340 K
    //   A  the share of the voxel's cloud water that comes from a sub-grid core, 0 to 255
    // Where a coarse cell carries a resolved convective updraft, VolumeSubGrid fills it with cores of 5 to 25 km, and each core is drawn as
    // a tower with a cauliflower profile and, near its top, an anvil that spreads to AnvilSpread times its radius: the shapes come from the
    // field's numbers (updraft, cloud depth, cloud water), none is placed by hand. The rebuild is spread over several frames, it blends
    // into the previous contents (smoothing), and Step allocates nothing.
    internal sealed class VolumeWindowResampler
    {
        internal const float UpdraftMaxMs = 40f, TemperatureMinK = 180f, TemperatureMaxK = 340f;
        // A level counts as cloud above this cloud water, g/m3.
        internal const float CloudThreshold = 0.02f;

        private const int MaxCoreCount = 1024;
        private const int MaxNear = 24;

        private readonly int nxz, ny;
        private readonly byte[] data;
        private readonly byte[] map;
        private readonly byte[] target;
        private readonly VolumeCore[] scratch = new VolumeCore[VolumeSubGrid.MaxCores];
        // The cores of the rebuild in progress: centre (m from the coarse window's centre), radius, base and top altitude, peak cloud water
        // (normalised), updraft.
        private readonly float[] coreX = new float[MaxCoreCount], coreZ = new float[MaxCoreCount], coreRadius = new float[MaxCoreCount],
            coreBase = new float[MaxCoreCount], coreDepth = new float[MaxCoreCount], corePeak = new float[MaxCoreCount],
            coreUpdraft = new float[MaxCoreCount];
        private readonly int[] near = new int[MaxNear];
        private int coreCount;

        private WeatherWindow coarse;
        // The refill of the coarse window with layered columns (WindowAdvection), in progress while refillGenerator is set.
        private AtmoSim.Core.WeatherGenerator refillGenerator;
        private AtmoSim.Core.LayerOptions refillOptions;
        private int refillNext;
        private double refillUt;
        private ulong refillSeed;
        private VolumeLook look;
        private float halfWidth, height, centreEast, centreNorth, groundAlt, smoothing;
        private int nextSlice;
        private bool building, hasData;
        private int builds;

        internal VolumeWindowResampler(int cellsAcross, int cellsUp)
        {
            if (cellsAcross < 2 || cellsUp < 2 || cellsAcross > VolumeMath.MaxWindowCells || cellsUp > VolumeMath.MaxWindowCells)
                throw new ArgumentOutOfRangeException("cellsAcross");
            nxz = cellsAcross;
            ny = cellsUp;
            data = new byte[nxz * ny * nxz * 4];
            target = new byte[data.Length];
            map = new byte[nxz * nxz * 2];
        }

        internal int CellsAcross { get { return nxz; } }
        internal int CellsUp { get { return ny; } }
        // The voxels, in the layout of a Texture3D of width nxz, height ny, depth nxz (RGBA32).
        internal byte[] Data { get { return data; } }
        // The deck map: per column of the window (east fastest, then north) coverage and type, two bytes each.
        internal byte[] DeckMap { get { return map; } }
        // True while a rebuild is in progress.
        internal bool Building { get { return building; } }
        // True once a rebuild has completed at least once.
        internal bool HasData { get { return hasData; } }
        // The number of completed rebuilds.
        internal int Builds { get { return builds; } }
        internal int CoreCount { get { return coreCount; } }

        // The cores of the rebuild (for the curtain pass and the tests): centre east and north of the coarse window's centre, radius, base
        // altitude, depth, updraft.
        internal void GetCore(int i, out float east, out float north, out float radiusM, out float baseAlt, out float depthM, out float updraft)
        {
            east = coreX[i];
            north = coreZ[i];
            radiusM = coreRadius[i];
            baseAlt = coreBase[i];
            depthM = coreDepth[i];
            updraft = coreUpdraft[i];
        }

        // Starts a rebuild around a place: the camera's offset from the coarse window's centre, the window's half-width and height, the
        // altitude of its plane above sea level, the smoothing (0 to 1, the share of the new state taken at once; 1 on the first build).
        internal bool Begin(WeatherWindow window, double ut, ulong seed, float halfWidthM, float heightM, float centreEastM, float centreNorthM,
                            float groundAltM, float smoothingShare, VolumeLook volumeLook)
        {
            if (window == null || window.Nx < 1 || window.Ny < 1 || window.Nz < 1 || window.CloudWater.Length < window.Count) return false;
            coarse = window;
            look = volumeLook;
            halfWidth = halfWidthM;
            height = heightM;
            centreEast = centreEastM;
            centreNorth = centreNorthM;
            groundAlt = groundAltM;
            smoothing = hasData ? Clamp01(smoothingShare) : 1f;
            // E43: where the generator is ours, the window is refilled with layered columns (deck carried by its own wind, growing towers, tilt, consistency) in
            // the first steps of the rebuild, and the cores are found from the refilled window; otherwise the field's window stands and the cores are found at once.
            refillGenerator = WindowAdvection.GeneratorFor(window);
            refillNext = 0;
            refillUt = ut;
            refillSeed = seed;
            if (refillGenerator != null) refillOptions = LayerWindOptions.Layers();
            else FindCores(ut, seed);
            nextSlice = 0;
            building = true;
            return true;
        }

        // Builds up to count z slices of the new state; when the last one is done the state is blended into Data. True when finished.
        internal bool Step(int count)
        {
            if (!building) return true;
            if (refillGenerator != null)
            {
                int rows = Math.Max(1, 4 * coarse.Ny * Math.Max(1, count) / nxz);
                WindowAdvection.Refill(coarse, refillNext, rows, refillGenerator, refillOptions);
                refillNext += rows;
                if (refillNext < coarse.Ny) return false;
                refillGenerator = null;
                FindCores(refillUt, refillSeed);
            }
            int end = Math.Min(nxz, nextSlice + Math.Max(1, count));
            for (int z = nextSlice; z < end; z++) BuildSlice(z);
            nextSlice = end;
            if (nextSlice < nxz) return false;
            Blend();
            building = false;
            hasData = true;
            builds++;
            coarse = null;
            return true;
        }

        // Builds everything at once (tests, the first frame).
        internal void Complete()
        {
            while (!Step(nxz)) { }
        }

        // The cores of every coarse column with an updraft, by the sub-grid rule.
        private void FindCores(double ut, ulong seed)
        {
            coreCount = 0;
            WeatherWindow w = coarse;
            float cellE = w.SpacingHorizontalM, cellN = w.SpacingHorizontalM;
            if (!(cellE > 1f)) return;
            for (int iy = 0; iy < w.Ny; iy++)
            {
                for (int ix = 0; ix < w.Nx; ix++)
                {
                    float maxUp = 0f, peak = 0f, baseAlt = float.MaxValue, topAlt = float.MinValue;
                    for (int iz = 0; iz < w.Nz; iz++)
                    {
                        int i = w.Index(ix, iy, iz);
                        float cw = w.CloudWater[i];
                        float alt = w.OriginAltM + iz * w.SpacingVerticalM;
                        if (cw > CloudThreshold)
                        {
                            if (alt < baseAlt) baseAlt = alt;
                            if (alt > topAlt) topAlt = alt;
                            if (cw > peak) peak = cw;
                        }
                        if (w.Updraft[i] > maxUp) maxUp = w.Updraft[i];
                    }
                    if (topAlt < baseAlt) continue;
                    float depth = topAlt - baseAlt + w.SpacingVerticalM;
                    long cellIndex = (long)iy * w.Nx + ix;
                    int n = VolumeSubGrid.Cores(cellIndex, ut, seed, maxUp, depth, cellE / 1000.0, cellN / 1000.0, scratch);
                    float cx = (ix + 0.5f - 0.5f * w.Nx) * cellE, cz = (iy + 0.5f - 0.5f * w.Ny) * cellN;
                    for (int k = 0; k < n && coreCount < MaxCoreCount; k++)
                    {
                        coreX[coreCount] = cx + (float)scratch[k].OffsetEastKm * 1000f;
                        coreZ[coreCount] = cz + (float)scratch[k].OffsetNorthKm * 1000f;
                        coreRadius[coreCount] = (float)scratch[k].RadiusKm * 1000f;
                        coreBase[coreCount] = baseAlt;
                        coreDepth[coreCount] = depth * (0.8f + 0.2f * Clamp01((float)scratch[k].UpdraftMs / 25f));
                        corePeak[coreCount] = peak;
                        coreUpdraft[coreCount] = (float)scratch[k].UpdraftMs;
                        coreCount++;
                    }
                }
            }
        }

        // The profile of a core: the share of its radius at the normalised height s (0 base, 1 top), a cauliflower that widens to 60 % of the
        // depth, narrows to the top and spreads into the anvil in the highest AnvilHeight of the depth.
        internal static float CoreRadiusShare(float s, float anvilHeight, float anvilSpread)
        {
            if (s < 0f || s > 1f) return 0f;
            float anvilStart = 1f - anvilHeight;
            if (s >= anvilStart)
            {
                float a = (s - anvilStart) / anvilHeight;
                // The tower's radius at the anvil's foot grows to the anvil's spread, then the sheet ends at the top.
                return Lerp(0.75f, anvilSpread, Smooth(a));
            }
            float t = s / anvilStart;
            return Lerp(0.55f, 1f, Smooth(Math.Min(1f, t / 0.6f))) * Lerp(1f, 0.75f, Smooth(Math.Max(0f, (t - 0.6f) / 0.4f)));
        }

        // The vertical weight: zero at the base and top, one in between; the anvil is a thinner sheet.
        internal static float CoreVertical(float s, float anvilHeight)
        {
            float fadeIn = Smooth(Clamp01(s / 0.06f));
            float fadeOut = 1f - Smooth(Clamp01((s - 0.9f) / 0.1f));
            float anvilStart = 1f - anvilHeight;
            float sheet = s > anvilStart ? Lerp(1f, 0.6f, Smooth(Clamp01((s - anvilStart) / anvilHeight))) : 1f;
            return fadeIn * fadeOut * sheet;
        }

        private void BuildSlice(int z)
        {
            float widthM = 2f * halfWidth;
            float cell = widthM / nxz;
            float wz = (z + 0.5f) * cell - halfWidth + centreNorth;
            float spread = look.AnvilSpread;
            float cwRef = Math.Max(look.CloudWaterRef, 0.01f);
            for (int x = 0; x < nxz; x++)
            {
                float wx = (x + 0.5f) * cell - halfWidth + centreEast;
                // The cores that can reach this column.
                int nearCount = 0;
                for (int c = 0; c < coreCount && nearCount < MaxNear; c++)
                {
                    float dx = wx - coreX[c], dz = wz - coreZ[c];
                    float reach = coreRadius[c] * spread * 1.02f;
                    if (dx * dx + dz * dz < reach * reach) near[nearCount++] = c;
                }
                for (int y = 0; y < ny; y++)
                {
                    float h = (y + 0.5f) / ny * height;
                    float alt = groundAlt + h;
                    float cw, up, temp;
                    SampleCoarse(wx, wz, alt, out cw, out up, out temp);
                    float core = 0f, coreUp = 0f;
                    for (int k = 0; k < nearCount; k++)
                    {
                        int c = near[k];
                        float s = (alt - coreBase[c]) / Math.Max(coreDepth[c], 1f);
                        if (s <= 0f || s >= 1f) continue;
                        float radius = coreRadius[c] * CoreRadiusShare(s, look.AnvilHeight, spread);
                        float dx = wx - coreX[c], dz = wz - coreZ[c];
                        float r2 = (dx * dx + dz * dz) / (radius * radius);
                        if (r2 >= 1f) continue;
                        float horiz = (1f - r2) * (1f - r2);
                        float v = corePeak[c] * 1.25f * horiz * CoreVertical(s, look.AnvilHeight);
                        if (v > core)
                        {
                            core = v;
                            coreUp = coreUpdraft[c] * horiz;
                        }
                    }
                    // The coarse cell's cloud water is a mean over 30 km: where cores stand it gives way to them.
                    float coreShare = core > 0f ? 1f : 0f;
                    float total = Math.Max(cw * (core > 0f ? 0.35f : 1f), core);
                    int o = ((z * ny + y) * nxz + x) * 4;
                    target[o] = ToByte(total / cwRef);
                    target[o + 1] = ToByte(Math.Max(up, coreUp) / UpdraftMaxMs);
                    target[o + 2] = ToByte((temp - TemperatureMinK) / (TemperatureMaxK - TemperatureMinK));
                    target[o + 3] = ToByte(coreShare * (total > 0f ? core / Math.Max(total, 1e-6f) : 0f));
                }
            }
        }

        // The coarse window at a place (metres east and north of its centre) and altitude, trilinear; zero outside it.
        private void SampleCoarse(float east, float north, float alt, out float cw, out float up, out float temp)
        {
            WeatherWindow w = coarse;
            cw = 0f;
            up = 0f;
            temp = 250f;
            float fx = east / w.SpacingHorizontalM + 0.5f * w.Nx - 0.5f;
            float fy = north / w.SpacingHorizontalM + 0.5f * w.Ny - 0.5f;
            float fz = (alt - w.OriginAltM) / Math.Max(w.SpacingVerticalM, 1f);
            if (fx < -0.5f || fy < -0.5f || fx > w.Nx - 0.5f || fy > w.Ny - 0.5f) return;
            if (fx < 0f) fx = 0f; else if (fx > w.Nx - 1) fx = w.Nx - 1;
            if (fy < 0f) fy = 0f; else if (fy > w.Ny - 1) fy = w.Ny - 1;
            if (fz < -0.5f || fz > w.Nz - 0.5f) return;
            if (fz < 0f) fz = 0f; else if (fz > w.Nz - 1) fz = w.Nz - 1;
            int x0 = (int)fx, y0 = (int)fy, z0 = (int)fz;
            int x1 = Math.Min(x0 + 1, w.Nx - 1), y1 = Math.Min(y0 + 1, w.Ny - 1), z1 = Math.Min(z0 + 1, w.Nz - 1);
            float tx = fx - x0, ty = fy - y0, tz = fz - z0;
            cw = Trilinear(w.CloudWater, w, x0, y0, z0, x1, y1, z1, tx, ty, tz);
            up = Trilinear(w.Updraft, w, x0, y0, z0, x1, y1, z1, tx, ty, tz);
            temp = w.Temperature.Length >= w.Count ? Trilinear(w.Temperature, w, x0, y0, z0, x1, y1, z1, tx, ty, tz) : 250f;
        }

        private static float Trilinear(float[] a, WeatherWindow w, int x0, int y0, int z0, int x1, int y1, int z1, float tx, float ty, float tz)
        {
            float c000 = a[w.Index(x0, y0, z0)], c100 = a[w.Index(x1, y0, z0)], c010 = a[w.Index(x0, y1, z0)], c110 = a[w.Index(x1, y1, z0)];
            float c001 = a[w.Index(x0, y0, z1)], c101 = a[w.Index(x1, y0, z1)], c011 = a[w.Index(x0, y1, z1)], c111 = a[w.Index(x1, y1, z1)];
            float a0 = Lerp(Lerp(c000, c100, tx), Lerp(c010, c110, tx), ty);
            float a1 = Lerp(Lerp(c001, c101, tx), Lerp(c011, c111, tx), ty);
            return Lerp(a0, a1, tz);
        }

        // Moves Data towards the new state by the smoothing share.
        private void Blend()
        {
            if (smoothing >= 0.999f)
            {
                Buffer.BlockCopy(target, 0, data, 0, data.Length);
                return;
            }
            int s = (int)(smoothing * 256f);
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(data[i] + (((target[i] - data[i]) * s) >> 8));
        }

        // Fills the deck map from the coverage and type maps of the body (lat, lon of the window's centre, the planet's radius).
        internal void FillDeckMap(WeatherMap coverage, WeatherMap type, double centreLatDeg, double centreLonDeg, double planetRadiusM)
        {
            float widthM = 2f * halfWidth, cell = widthM / nxz;
            double cosLat = Math.Max(0.05, Math.Cos(centreLatDeg * Math.PI / 180.0));
            for (int z = 0; z < nxz; z++)
            {
                for (int x = 0; x < nxz; x++)
                {
                    float east = (x + 0.5f) * cell - halfWidth + centreEast, north = (z + 0.5f) * cell - halfWidth + centreNorth;
                    double lat = centreLatDeg + north / planetRadiusM * 180.0 / Math.PI;
                    double lon = centreLonDeg + east / (planetRadiusM * cosLat) * 180.0 / Math.PI;
                    int o = (z * nxz + x) * 2;
                    map[o] = ToByte(coverage != null ? coverage.Sample(lat, lon) : 0f);
                    map[o + 1] = ToByte(type != null ? type.Sample(lat, lon) : 0f);
                }
            }
        }

        // Fills the deck map with one coverage and one type (the weather player, and a body whose maps are not there).
        internal void FillDeckMapUniform(float coverage01, float type01)
        {
            byte c = ToByte(coverage01), t = ToByte(type01);
            for (int i = 0; i < map.Length; i += 2)
            {
                map[i] = c;
                map[i + 1] = t;
            }
        }

        // The cloud water of the window at a place of its frame (metres east and north of its centre, height above its plane), normalised
        // 0 to 1 over 0 to CloudWaterRef, trilinear. Zero outside. Reads the voxels, so it is what the shader sees.
        internal float SampleCloudWater(float east, float north, float h)
        {
            float cell = 2f * halfWidth / nxz;
            float fx = east / cell + 0.5f * nxz - 0.5f, fz = north / cell + 0.5f * nxz - 0.5f, fy = h / height * ny - 0.5f;
            if (fx < -0.5f || fz < -0.5f || fy < -0.5f || fx > nxz - 0.5f || fz > nxz - 0.5f || fy > ny - 0.5f) return 0f;
            if (fx < 0f) fx = 0f; else if (fx > nxz - 1) fx = nxz - 1;
            if (fz < 0f) fz = 0f; else if (fz > nxz - 1) fz = nxz - 1;
            if (fy < 0f) fy = 0f; else if (fy > ny - 1) fy = ny - 1;
            int x0 = (int)fx, y0 = (int)fy, z0 = (int)fz;
            int x1 = Math.Min(x0 + 1, nxz - 1), y1 = Math.Min(y0 + 1, ny - 1), z1 = Math.Min(z0 + 1, nxz - 1);
            float tx = fx - x0, ty = fy - y0, tz = fz - z0;
            float a0 = Lerp(Lerp(V(x0, y0, z0), V(x1, y0, z0), tx), Lerp(V(x0, y1, z0), V(x1, y1, z0), tx), ty);
            float a1 = Lerp(Lerp(V(x0, y0, z1), V(x1, y0, z1), tx), Lerp(V(x0, y1, z1), V(x1, y1, z1), tx), ty);
            return Lerp(a0, a1, tz) / 255f;
        }

        // The deck coverage at a place of the window's frame, 0 to 1 (1 where no map was filled).
        internal float SampleCoverage(float east, float north)
        {
            float cell = 2f * halfWidth / nxz;
            int x = (int)Math.Floor(east / cell + 0.5f * nxz), z = (int)Math.Floor(north / cell + 0.5f * nxz);
            if (x < 0 || z < 0 || x >= nxz || z >= nxz) return 0f;
            return map[(z * nxz + x) * 2] / 255f;
        }

        private float V(int x, int y, int z)
        {
            return data[((z * ny + y) * nxz + x) * 4];
        }

        private static byte ToByte(float v01)
        {
            return v01 <= 0f ? (byte)0 : v01 >= 1f ? (byte)255 : (byte)(v01 * 255f + 0.5f);
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : v > 1f ? 1f : v;
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        private static float Smooth(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }
    }
}
