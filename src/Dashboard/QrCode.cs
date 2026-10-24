using System;
using System.Collections.Generic;
using System.Text;

namespace ReDefinition.Dashboard
{
    /// <summary>
    /// A QR code for a short text: byte mode, error correction level M, the smallest version (1 to 10)
    /// that holds it, mask 0. Follows the algorithm of Project Nayuki's QR code generator
    /// (https://www.nayuki.io/page/qr-code-generator-library, MIT), cut down to what the dashboard's
    /// address needs.
    /// </summary>
    internal sealed class QrCode
    {
        // Level M, by version (index 0 unused).
        private static readonly int[] EccPerBlock = { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
        private static readonly int[] Blocks = { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };
        private const int FormatEcc = 0; // level M in the format bits

        public readonly int Size;
        private readonly bool[,] modules, isFunction; // [y, x]

        public bool this[int x, int y] { get { return modules[y, x]; } }

        public QrCode(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            int version = 1;
            for (; version <= 10; version++)
                if (4 + (version < 10 ? 8 : 16) + data.Length * 8 <= DataCodewords(version) * 8) break;
            if (version > 10) throw new ArgumentException("text too long for a QR code here");

            // Mode, length, bytes, terminator, padding.
            var bits = new List<bool>();
            Action<int, int> put = (value, count) => { for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); };
            put(4, 4);
            put(data.Length, version < 10 ? 8 : 16);
            foreach (byte b in data) put(b, 8);
            int capacity = DataCodewords(version) * 8;
            put(0, Math.Min(4, capacity - bits.Count));
            put(0, (8 - bits.Count % 8) % 8);
            for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) put(pad, 8);
            var codewords = new byte[bits.Count / 8];
            for (int i = 0; i < bits.Count; i++) if (bits[i]) codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));

            Size = version * 4 + 17;
            modules = new bool[Size, Size];
            isFunction = new bool[Size, Size];
            DrawFunctionPatterns(version);
            DrawCodewords(AddEccAndInterleave(codewords, version));
            ApplyMask();
            DrawFormatBits(0);
        }

        private static int RawDataModules(int ver)
        {
            int result = (16 * ver + 128) * ver + 64;
            if (ver >= 2)
            {
                int numAlign = ver / 7 + 2;
                result -= (25 * numAlign - 10) * numAlign - 55;
                if (ver >= 7) result -= 36;
            }
            return result;
        }

        private static int DataCodewords(int ver)
        {
            return RawDataModules(ver) / 8 - EccPerBlock[ver] * Blocks[ver];
        }

        private void SetFunction(int x, int y, bool dark)
        {
            modules[y, x] = dark;
            isFunction[y, x] = true;
        }

        private void DrawFunctionPatterns(int version)
        {
            for (int i = 0; i < Size; i++)
            {
                SetFunction(6, i, i % 2 == 0);
                SetFunction(i, 6, i % 2 == 0);
            }
            DrawFinder(3, 3);
            DrawFinder(Size - 4, 3);
            DrawFinder(3, Size - 4);
            if (version >= 2)
            {
                int numAlign = version / 7 + 2;
                int step = (version * 8 + numAlign * 3 + 5) / (numAlign * 4 - 4) * 2;
                var pos = new int[numAlign];
                pos[0] = 6;
                for (int i = numAlign - 1, p = Size - 7; i >= 1; i--, p -= step) pos[i] = p;
                for (int i = 0; i < numAlign; i++)
                    for (int j = 0; j < numAlign; j++)
                        if (!(i == 0 && j == 0 || i == 0 && j == numAlign - 1 || i == numAlign - 1 && j == 0))
                            DrawAlignment(pos[i], pos[j]);
            }
            DrawFormatBits(0); // reserves the format area; drawn again with the mask at the end
            if (version >= 7)
            {
                int rem = version;
                for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
                int bits = version << 12 | rem;
                for (int i = 0; i < 18; i++)
                {
                    bool bit = ((bits >> i) & 1) != 0;
                    int a = Size - 11 + i % 3, b = i / 3;
                    SetFunction(a, b, bit);
                    SetFunction(b, a, bit);
                }
            }
        }

        private void DrawFinder(int x, int y)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy)), xx = x + dx, yy = y + dy;
                    if (xx >= 0 && xx < Size && yy >= 0 && yy < Size) SetFunction(xx, yy, dist != 2 && dist != 4);
                }
        }

        private void DrawAlignment(int x, int y)
        {
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    SetFunction(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }

        private void DrawFormatBits(int mask)
        {
            int data = FormatEcc << 3 | mask;
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = (data << 10 | rem) ^ 0x5412;
            Func<int, bool> bit = i => ((bits >> i) & 1) != 0;
            for (int i = 0; i <= 5; i++) SetFunction(8, i, bit(i));
            SetFunction(8, 7, bit(6));
            SetFunction(8, 8, bit(7));
            SetFunction(7, 8, bit(8));
            for (int i = 9; i < 15; i++) SetFunction(14 - i, 8, bit(i));
            for (int i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, bit(i));
            for (int i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, bit(i));
            SetFunction(8, Size - 8, true);
        }

        private static byte[] AddEccAndInterleave(byte[] data, int version)
        {
            int numBlocks = Blocks[version], blockEccLen = EccPerBlock[version];
            int rawCodewords = RawDataModules(version) / 8;
            int numShortBlocks = numBlocks - rawCodewords % numBlocks;
            int shortBlockLen = rawCodewords / numBlocks;
            byte[] divisor = ReedSolomonDivisor(blockEccLen);
            var blocks = new List<byte[]>();
            for (int i = 0, k = 0; i < numBlocks; i++)
            {
                int datLen = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
                var dat = new byte[datLen];
                Array.Copy(data, k, dat, 0, datLen);
                k += datLen;
                byte[] ecc = ReedSolomonRemainder(dat, divisor);
                var block = new byte[shortBlockLen + 1];
                Array.Copy(dat, block, datLen);
                Array.Copy(ecc, 0, block, block.Length - blockEccLen, blockEccLen);
                blocks.Add(block);
            }
            var result = new List<byte>();
            for (int i = 0; i < blocks[0].Length; i++)
                for (int j = 0; j < blocks.Count; j++)
                    if (i != shortBlockLen - blockEccLen || j >= numShortBlocks) result.Add(blocks[j][i]);
            return result.ToArray();
        }

        private static byte[] ReedSolomonDivisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < result.Length; j++)
                {
                    result[j] = Multiply(result[j], root);
                    if (j + 1 < result.Length) result[j] ^= result[j + 1];
                }
                root = Multiply(root, 0x02);
            }
            return result;
        }

        private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            foreach (byte b in data)
            {
                int factor = b ^ result[0];
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (int i = 0; i < result.Length; i++) result[i] ^= Multiply(divisor[i], factor);
            }
            return result;
        }

        private static byte Multiply(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }

        private void DrawCodewords(byte[] data)
        {
            int i = 0;
            for (int right = Size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < Size; vert++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? Size - 1 - vert : vert;
                        if (!isFunction[y, x] && i < data.Length * 8)
                        {
                            modules[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                            i++;
                        }
                    }
            }
        }

        private void ApplyMask() // mask 0: (x + y) even
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    if (!isFunction[y, x] && (x + y) % 2 == 0) modules[y, x] = !modules[y, x];
        }
    }
}
