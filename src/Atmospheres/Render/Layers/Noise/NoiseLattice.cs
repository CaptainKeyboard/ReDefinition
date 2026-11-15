using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The lattice table of the untiling warp of the shape lookup (E40 C4). The warp is a vector value noise of an integer hash: at every sample the shader hashed the 8 corners of the lookup's cell
    // (3 values each) and blended them with the quintic fade. Here the corner values are written to a 3D texture, a slab of N x N x N cells per layer (RGB: the vector, 0 to 1 per component, as
    // half floats or as bytes), and the shader (NoiseLatticeAt in ReDefinitionAtmospheresNoise.cginc) applies the fade to the position and lets the texture's linear filter blend the 8 corners:
    // the same blend of the same corner values, in one read.
    //
    // The warp's lattice has no period (each corner is NoiseHash.Hash3 of its cell and the layer's seed, everywhere), so the table is a WINDOW onto it: the texel (i mod N) holds the corner of
    // the cell i of the N cells around the viewer's cell (the texture wraps), and the window follows the viewer in steps (FollowWarp). Inside the window a read is the function of before; a ray that
    // reaches a cell outside it reads the corner of the cell that many windows away (another corner of the same kind of noise). At the layer with the finest noise (cumulus, 250 m, cells of
    // 3.5 km) the window holds 32 cells to either side of the viewer, 24 of them after the step's hysteresis: 84 km.
    internal sealed class NoiseLattice
    {
        // The lattice cells a slab holds per side.
        internal const int N = 64;
        // The slices of a slab: the N of the table and a copy of the first, so that the texture's filter wraps inside the slab (the depth axis does not wrap: it holds all slabs).
        internal const int SlabDepth = N + 1;
        internal const int Layers = CloudLayerDef.MaxLayers;
        internal const int Depth = Layers * SlabDepth;
        // The viewer's cell may leave the middle of the window by this many cells before the window moves again.
        internal const int Hysteresis = N / 8;

        private const int SlabValues = SlabDepth * N * N * 4;
        private const ushort HalfOne = 0x3C00, HalfHalf = 0x3800;

        // The corner values as half floats in the order of the texture (slice, row, texel, channel; a fourth channel unused): the table the half-float texture holds.
        private readonly ushort[] halves = new ushort[Layers * SlabValues];
        // The same as bytes, converted from the halves when the byte format is asked for (a probe format: a quarter of the memory, to 0.004 of a corner value).
        private byte[] bytes;
        private readonly bool[] bytesDirty = new bool[Layers];
        private readonly int[,] origin = new int[Layers, 3];
        private readonly bool[] warpValid = new bool[Layers];
        private readonly uint[] warpSeed = new uint[Layers];
        private Texture3D texture;
        private bool halfBuilt;
        private bool dirty = true;

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] internal float Value;
            [FieldOffset(0)] internal int Bits;
        }

        internal NoiseLattice()
        {
            for (int i = 0; i < halves.Length; i += 4)
            {
                halves[i] = halves[i + 1] = halves[i + 2] = HalfHalf;
                halves[i + 3] = HalfOne;
            }
        }

        // The table as half floats (for the tests: FromHalf and Read).
        internal ushort[] Halves { get { return halves; } }

        // The cell index of the window's origin of a layer's warp along an axis (the window holds origin .. origin + N - 1).
        internal int WarpOrigin(int layer, int axis)
        {
            return origin[layer, axis];
        }

        internal static int Index(int slab, int z, int y, int x, int channel)
        {
            return ((((slab * SlabDepth) + z) * N + y) * N + x) * 4 + channel;
        }

        // Moves the window of a layer's warp to the lattice position (cx, cy, cz) in lattice cells when it is not yet there or the position has left the middle of the window by more than
        // the hysteresis. A new seed writes the whole window. True: the table changed.
        internal bool FollowWarp(int layer, uint seed, double cx, double cy, double cz)
        {
            int ix = (int)Math.Floor(cx), iy = (int)Math.Floor(cy), iz = (int)Math.Floor(cz);
            bool full = !warpValid[layer] || warpSeed[layer] != seed;
            if (!full)
            {
                int half = N / 2;
                if (Math.Abs(ix - (origin[layer, 0] + half)) <= Hysteresis && Math.Abs(iy - (origin[layer, 1] + half)) <= Hysteresis && Math.Abs(iz - (origin[layer, 2] + half)) <= Hysteresis)
                    return false;
            }
            int nx = ix - N / 2, ny = iy - N / 2, nz = iz - N / 2;
            // The texel t holds the cell o + ((t - o) mod N) of the window starting at o; only the texels whose cell changed are written.
            int[] cellX = new int[N], cellY = new int[N], cellZ = new int[N];
            bool[] moveX = new bool[N], moveY = new bool[N], moveZ = new bool[N];
            for (int t = 0; t < N; t++)
            {
                cellX[t] = nx + NoiseHash.Mod(t - nx, N);
                cellY[t] = ny + NoiseHash.Mod(t - ny, N);
                cellZ[t] = nz + NoiseHash.Mod(t - nz, N);
                moveX[t] = full || cellX[t] != origin[layer, 0] + NoiseHash.Mod(t - origin[layer, 0], N);
                moveY[t] = full || cellY[t] != origin[layer, 1] + NoiseHash.Mod(t - origin[layer, 1], N);
                moveZ[t] = full || cellZ[t] != origin[layer, 2] + NoiseHash.Mod(t - origin[layer, 2], N);
            }
            for (int z = 0; z < N; z++)
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        if (!(moveX[x] || moveY[y] || moveZ[z])) continue;
                        double a, b, c;
                        NoiseHash.Vec(NoiseHash.Hash3(cellX[x], cellY[y], cellZ[z], seed), out a, out b, out c);
                        int i = Index(layer, z, y, x, 0);
                        halves[i] = ToHalf((float)a);
                        halves[i + 1] = ToHalf((float)b);
                        halves[i + 2] = ToHalf((float)c);
                    }
            Array.Copy(halves, Index(layer, 0, 0, 0, 0), halves, Index(layer, N, 0, 0, 0), N * N * 4);
            origin[layer, 0] = nx;
            origin[layer, 1] = ny;
            origin[layer, 2] = nz;
            warpSeed[layer] = seed;
            warpValid[layer] = true;
            bytesDirty[layer] = true;
            dirty = true;
            return true;
        }

        // The texture of the table (created and uploaded when the table or the precision changed since the last call). half: 16-bit floats (the corner values to 0.0005); false: 8 bits (to 0.004).
        internal Texture3D Texture(bool half)
        {
            if (texture != null && halfBuilt != half)
            {
                UnityEngine.Object.Destroy(texture);
                texture = null;
            }
            if (texture == null)
            {
                texture = new Texture3D(N, N, Depth, half ? TextureFormat.RGBAHalf : TextureFormat.RGBA32, false)
                {
                    name = "cloud noise lattice",
                    filterMode = FilterMode.Bilinear,
                    wrapModeU = TextureWrapMode.Repeat,
                    wrapModeV = TextureWrapMode.Repeat,
                    wrapModeW = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
                halfBuilt = half;
                bytes = null;
                dirty = true;
            }
            if (dirty)
            {
                if (half)
                {
                    texture.SetPixelData(halves, 0);
                }
                else
                {
                    if (bytes == null)
                    {
                        bytes = new byte[halves.Length];
                        for (int l = 0; l < Layers; l++) bytesDirty[l] = true;
                    }
                    for (int l = 0; l < Layers; l++)
                    {
                        if (!bytesDirty[l]) continue;
                        for (int i = l * SlabValues; i < (l + 1) * SlabValues; i++) bytes[i] = QuantiseByte(FromHalf(halves[i]));
                        bytesDirty[l] = false;
                    }
                    texture.SetPixelData(bytes, 0);
                }
                texture.Apply(false, false);
                dirty = false;
            }
            return texture;
        }

        internal void Dispose()
        {
            if (texture != null) UnityEngine.Object.Destroy(texture);
            texture = null;
            bytes = null;
            dirty = true;
        }

        internal static byte QuantiseByte(float v)
        {
            return (byte)(v * 255f + 0.5f);
        }

        // A float of 0 to 1 as an IEEE half (round to nearest; the values below the half's smallest normal number, 6e-5, are rounded to a denormal or 0).
        internal static ushort ToHalf(float f)
        {
            if (!(f > 0f)) return 0;
            int bits = new FloatBits { Value = f }.Bits;
            int exp = ((bits >> 23) & 0xFF) - 127 + 15, mant = bits & 0x7FFFFF;
            if (exp <= 0)
            {
                if (exp < -10) return 0;
                mant |= 0x800000;
                int shift = 14 - exp;
                return (ushort)((mant >> shift) + ((mant >> (shift - 1)) & 1));
            }
            return (ushort)((exp << 10) + (mant >> 13) + ((mant >> 12) & 1));
        }

        internal static float FromHalf(ushort h)
        {
            int exp = (h >> 10) & 0x1F, mant = h & 0x3FF;
            if (exp == 0) return (float)(mant * Math.Pow(2.0, -24.0));
            return (float)((1.0 + mant / 1024.0) * Math.Pow(2.0, exp - 15));
        }

        // The table as floats of 0 to 1 (the half floats' values), or rounded to bytes first (the byte texture's values).
        internal static float[] Floats(ushort[] table, bool throughBytes)
        {
            var f = new float[table.Length];
            for (int i = 0; i < f.Length; i++) f[i] = throughBytes ? QuantiseByte(FromHalf(table[i])) / 255f : FromHalf(table[i]);
            return f;
        }

        // The shader's read in double precision (NoiseLatticeAt, then the texture's linear filter): the table's value of a channel at a position in lattice cells, 0 to 1, over the corner values
        // of Floats. For the tests of the table and the parity of the CPU twin with the GPU.
        internal static double Read(float[] table, int slab, int channel, double x, double y, double z)
        {
            double fx = Math.Floor(x), fy = Math.Floor(y), fz = Math.Floor(z);
            double ux = NoiseHash.Fade(x - fx), uy = NoiseHash.Fade(y - fy), uz = NoiseHash.Fade(z - fz);
            int x0 = NoiseHash.Mod((int)fx, N), y0 = NoiseHash.Mod((int)fy, N), z0 = NoiseHash.Mod((int)fz, N);
            double acc = 0.0;
            for (int k = 0; k < 8; k++)
            {
                int ox = k & 1, oy = (k >> 1) & 1, oz = (k >> 2) & 1;
                double w = (ox != 0 ? ux : 1.0 - ux) * (oy != 0 ? uy : 1.0 - uy) * (oz != 0 ? uz : 1.0 - uz);
                acc += w * table[Index(slab, z0 + oz, (y0 + oy) % N, (x0 + ox) % N, channel)];
            }
            return acc;
        }
    }
}
