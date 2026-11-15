namespace ReDefinition.Atmospheres
{
    // The blue-noise tile of the layer march's per-pixel offsets (E40 F2): the ranks 0 to 255 of a 16 x 16 void-and-cluster arrangement (Ulichney, "The void-and-cluster
    // method for dither array generation", SPIE 1993; gaussian sigma 1.5 on the torus, seed 7), row-major with row 0 first. A pixel reads the rank of (x mod 16, y mod 16)
    // and the shader turns it into (rank + 0.5) / 256, so that any 2 x 2 or 4 x 4 neighbourhood of the march target holds offsets that lie far apart (the gradient noise's
    // offsets are a lattice of a few strong spatial frequencies). Engine-free so that the tests can read it.
    internal static class DitherMatrix
    {
        internal const int Side = 16, Count = Side * Side;

        private static readonly int[] Ranks =
        {
            143,  15, 159, 103,  67,   7, 237, 172, 125, 196, 253, 140, 208,   4,  41, 247,
            202, 113, 217,  35, 198, 151,  80,  48,  96, 161,  17,  86, 222, 115, 163,  79,
             21,  66, 177, 138, 240, 116, 219,  29, 242,  60, 183,  44, 155,  57, 231, 184,
            100, 254,   3,  84,  51,  19, 182, 129, 203, 107, 227, 132, 194,  94,  25, 134,
            207, 154, 121, 221, 192,  97, 158,  69,   0, 146,  32,  78,   8, 244, 175,  50,
            235,  38, 173,  68, 145,  30, 251, 216,  88, 174, 249, 123, 213, 148, 110,  75,
            142,  93,  18, 243, 106, 204,  55, 120,  40, 195,  64, 166,  37,  58, 199,  14,
            118, 188, 214,  45, 133,   6, 170, 141, 234,  98,  20, 108, 238,  85, 169, 226,
             56, 152,  77, 165, 233,  91, 190,  73,  11, 160, 209, 137, 185,   2, 130,  33,
             89, 241,  24, 114,  63, 215,  34, 246, 112, 223,  46,  71, 220, 101, 252, 206,
             10, 136, 200, 179,  16, 127, 150,  53, 168,  82, 144,  27, 156,  42,  65, 162,
            105, 228,  39,  95, 255, 186,  99, 205,  22, 189, 250, 111, 201, 180, 124, 191,
             49,  72, 167, 147,  47,  76,   9, 239, 122,  62,   5,  90, 236,  23,  81, 245,
            117, 211,   1, 218, 119, 225, 164, 139,  87, 178, 210, 131,  54, 149,  12, 157,
             31, 176, 128,  83,  26, 193,  59,  36, 232, 153,  43, 229, 171, 102, 224, 197,
             92, 230,  52, 248, 181, 135, 104, 212,  13,  70, 109,  28,  74, 187, 126,  61
        };

        // The rank of the texel (x, y) of the tile.
        internal static int RankAt(int x, int y)
        {
            return Ranks[(y & (Side - 1)) * Side + (x & (Side - 1))];
        }

        // The offset of the texel in [0, 1): the middle of its rank's stratum.
        internal static float ValueAt(int x, int y)
        {
            return (RankAt(x, y) + 0.5f) / Count;
        }

        // The tile's 256 offsets, row-major: the shader's _DitherM holds them four to a vector.
        internal static float[] Values()
        {
            var v = new float[Count];
            for (int i = 0; i < Count; i++) v[i] = (Ranks[i] + 0.5f) / Count;
            return v;
        }
    }
}
