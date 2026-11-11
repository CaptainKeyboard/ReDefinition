using System;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The tile lists of the flakes and drops: the picture is cut into 32 x 18 tiles and each tile carries the bits of the flakes and drops
    // that touch it, so that the shader evaluates only those and a pixel away from every flake and drop costs a lookup. Without it the
    // pass runs 40 flakes and 24 drops at every pixel (1.1 ms at 2560 x 1440 on the A4500; the rim frost alone is 0.08 ms).
    //
    // A tile is a float4 holding three integers a float can carry exactly: x the flakes 0 to 19, y the flakes 20 to 39, z the drops 0 to
    // 23 (bit k of the integer is the k-th element). Pure, no engine call (UnityEngine.Vector4 is a plain struct).
    internal static class FrostTiles
    {
        internal const int GridX = 32;
        internal const int GridY = 18;
        internal const int Count = GridX * GridY;
        internal const int FlakeBitsPerFloat = 20;
        // How far past its shape a flake or drop is allowed to reach into the next tile: the lens offsets stay inside the shape.
        private const float Margin = 1.15f;

        // Fills tiles (Count elements) from the flakes (x, y, radius in picture heights, variant + age) and the drops (x, y, radius, trail)
        // as the shader reads them. gravity is the unit direction down the picture in the picture plane (x right, y up), aspect the width
        // over the height of the target.
        internal static void Build(Vector4[] tiles, Vector4[] flakes, int flakeCount, Vector4[] drops, int dropCount, Vector2 gravity, float aspect)
        {
            if (tiles == null || tiles.Length < Count) throw new ArgumentException("the tile array holds " + Count + " elements");
            Array.Clear(tiles, 0, Count);
            for (int k = 0; k < flakeCount && k < CameraFrost.MaxFlakes; k++)
            {
                Vector4 f = flakes[k];
                float ru = f.z * Margin / aspect, rv = f.z * Margin;
                Mark(tiles, f.x - ru, f.y - rv, f.x + ru, f.y + rv, k < FlakeBitsPerFloat ? 0 : 1, k % FlakeBitsPerFloat);
            }
            float len = (float)Math.Sqrt(gravity.x * gravity.x + gravity.y * gravity.y);
            float gx = len > 1e-4f ? gravity.x / len : 0f, gy = len > 1e-4f ? gravity.y / len : -1f;
            for (int k = 0; k < dropCount && k < CameraFrost.MaxDrops; k++)
            {
                Vector4 d = drops[k];
                // The drop's rectangle in picture heights: along the gravity from the end of the trail (behind the head) to a head
                // radius past it, across it a head radius either side; the four corners give the box in picture units.
                float s = d.z * Margin * 1.3f;
                float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
                for (int c = 0; c < 4; c++)
                {
                    float along = (c & 1) == 0 ? -d.w - s : s;
                    float across = (c & 2) == 0 ? -s : s;
                    float hx = gx * along - gy * across, hy = gy * along + gx * across;
                    float u = d.x + hx / aspect, v = d.y + hy;
                    minU = Math.Min(minU, u);
                    maxU = Math.Max(maxU, u);
                    minV = Math.Min(minV, v);
                    maxV = Math.Max(maxV, v);
                }
                Mark(tiles, minU, minV, maxU, maxV, 2, k);
            }
        }

        private static void Mark(Vector4[] tiles, float u0, float v0, float u1, float v1, int component, int bit)
        {
            int i0 = Clamp((int)Math.Floor(u0 * GridX), GridX), i1 = Clamp((int)Math.Floor(u1 * GridX), GridX);
            int j0 = Clamp((int)Math.Floor(v0 * GridY), GridY), j1 = Clamp((int)Math.Floor(v1 * GridY), GridY);
            if (u1 < 0f || u0 > 1f || v1 < 0f || v0 > 1f) return;
            float mask = 1 << bit;
            for (int j = j0; j <= j1; j++)
            {
                for (int i = i0; i <= i1; i++)
                {
                    Vector4 t = tiles[j * GridX + i];
                    // The bits are summed as integers in a float: exact below 2^24.
                    int current = component == 0 ? (int)t.x : component == 1 ? (int)t.y : (int)t.z;
                    if ((current & (1 << bit)) != 0) continue;
                    if (component == 0) t.x = current + mask;
                    else if (component == 1) t.y = current + mask;
                    else t.z = current + mask;
                    tiles[j * GridX + i] = t;
                }
            }
        }

        private static int Clamp(int v, int size)
        {
            return v < 0 ? 0 : v >= size ? size - 1 : v;
        }
    }
}
