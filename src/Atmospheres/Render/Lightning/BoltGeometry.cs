using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // One strand of a bolt: a polyline in the bolt's frame (origin at the strike point on the ground, y up), the width of its core in m, its
    // brightness relative to the main channel and its depth (0 the main channel, 1 a branch, 2 a branch of a branch).
    internal sealed class BoltStrand
    {
        public readonly List<Vector3> Points = new List<Vector3>();
        public float WidthM;
        public float Brightness;
        public int Depth;
        // The strand it forks from and the index of the point there, -1 for the main channel.
        public int Parent = -1, ParentIndex = -1;
    }

    // The numbers of the bolt shape.
    internal sealed class BoltParams
    {
        // Midpoint-displacement levels of the main channel and of a branch (a level doubles the number of segments).
        public int MainLevels = 7, BranchLevels = 5;
        // The first displacement as a share of the segment length, and the factor per level.
        public float Roughness = 0.09f, Persistence = 0.58f;
        // The chance that a vertex of the main channel (of a branch) forks, beyond the first and last eighth of the channel.
        public float MainForkChance = 0.07f, BranchForkChance = 0.05f;
        // A branch's length as a share of the distance from its fork to the ground, and its angle away from the parent's direction in degrees.
        public float BranchLengthMin = 0.12f, BranchLengthMax = 0.4f, AngleMinDeg = 22f, AngleMaxDeg = 58f;
        // The widths of the core in m and the brightness per depth.
        public float MainWidthM = 1.1f;
        public float BranchWidthFactor = 0.55f, BranchBrightness = 0.45f, SubBranchBrightness = 0.22f;
        // The most strands a bolt may have.
        public int MaxStrands = 24;
    }

    // The shape of a bolt (package T07): a jagged main channel by midpoint displacement from the charge region to the ground, with branches
    // that leave it downwards and sideways and fork again less often. A pure function of the seed and the two end points; the same flash
    // always has the same bolt. Technique: random midpoint displacement of a polyline (Fournier, Fussell and Carpenter 1982), the usual
    // way to draw a lightning channel.
    internal static class BoltGeometry
    {
        private struct Rng
        {
            private readonly uint seed;
            private int n;
            internal Rng(uint seed) { this.seed = seed; n = 0; }
            internal float Next() { return (float)LightningSchedule.Draw(seed, 100 + n++); }
            internal float Signed() { return Next() * 2f - 1f; }
        }

        // The strands of a bolt that starts at (offsetM.x, startHeightM, offsetM.y) and ends at the origin.
        internal static List<BoltStrand> Build(uint seed, float startHeightM, Vector2 startOffsetM, BoltParams bp)
        {
            var rng = new Rng(seed);
            var strands = new List<BoltStrand>();
            Vector3 top = new Vector3(startOffsetM.x, Mathf.Max(startHeightM, 50f), startOffsetM.y);
            var main = new BoltStrand { WidthM = bp.MainWidthM, Brightness = 1f, Depth = 0 };
            Displace(main.Points, top, Vector3.zero, bp.MainLevels, bp, ref rng);
            strands.Add(main);
            Fork(strands, 0, bp, ref rng);
            return strands;
        }

        // Forks of a strand, then of the strands that were made, breadth first so the cap cuts the deepest ones.
        private static void Fork(List<BoltStrand> strands, int index, BoltParams bp, ref Rng rng)
        {
            int next = index;
            while (next < strands.Count && strands.Count < bp.MaxStrands)
            {
                BoltStrand parent = strands[next];
                if (parent.Depth < 2) ForksOf(strands, next, bp, ref rng);
                next++;
            }
        }

        private static void ForksOf(List<BoltStrand> strands, int index, BoltParams bp, ref Rng rng)
        {
            BoltStrand parent = strands[index];
            int count = parent.Points.Count;
            float chance = parent.Depth == 0 ? bp.MainForkChance : bp.BranchForkChance;
            int from = count / 8, to = count - count / 8;
            for (int i = Math.Max(from, 1); i < to; i++)
            {
                float roll = rng.Next();
                if (roll >= chance) continue;
                if (strands.Count >= bp.MaxStrands) return;
                Vector3 p = parent.Points[i];
                if (p.y < 30f) continue;
                Vector3 dir = (parent.Points[Math.Min(i + 2, count - 1)] - parent.Points[Math.Max(i - 2, 0)]).normalized;
                // A branch leaves at an angle to the parent, turned about a random axis around it; the downward part stays.
                float angle = Mathf.Lerp(bp.AngleMinDeg, bp.AngleMaxDeg, rng.Next()) * Mathf.Deg2Rad;
                float around = rng.Next() * 2f * Mathf.PI;
                Vector3 side = Perpendicular(dir);
                Vector3 side2 = Vector3.Cross(dir, side);
                Vector3 axis = side * Mathf.Cos(around) + side2 * Mathf.Sin(around);
                Vector3 bdir = (dir * Mathf.Cos(angle) + axis * Mathf.Sin(angle)).normalized;
                if (bdir.y > -0.05f) bdir.y = -0.05f - 0.25f * rng.Next();
                bdir.Normalize();
                float length = p.y * Mathf.Lerp(bp.BranchLengthMin, bp.BranchLengthMax, rng.Next());
                if (parent.Depth == 1) length *= 0.5f;
                Vector3 end = p + bdir * length;
                // A branch ends in the air or at the ground, never below it.
                if (end.y < 0f)
                {
                    float t = p.y / (p.y - end.y);
                    end = Vector3.Lerp(p, end, t);
                    end.y = 0f;
                }
                var b = new BoltStrand
                {
                    Depth = parent.Depth + 1,
                    Parent = index,
                    ParentIndex = i,
                    WidthM = parent.WidthM * bp.BranchWidthFactor,
                    Brightness = parent.Depth == 0 ? bp.BranchBrightness : bp.SubBranchBrightness
                };
                Displace(b.Points, p, end, bp.BranchLevels, bp, ref rng);
                strands.Add(b);
            }
        }

        // Midpoint displacement from a to b, clamped to y >= 0, appended to the list including both end points.
        private static void Displace(List<Vector3> into, Vector3 a, Vector3 b, int levels, BoltParams bp, ref Rng rng)
        {
            int segments = 1 << levels;
            var pts = new Vector3[segments + 1];
            pts[0] = a;
            pts[segments] = b;
            float length = (b - a).magnitude;
            float sigma = length * bp.Roughness;
            int step = segments;
            while (step > 1)
            {
                int half = step / 2;
                for (int i = half; i < segments; i += step)
                {
                    Vector3 m = (pts[i - half] + pts[i + half]) * 0.5f;
                    Vector3 axis = (pts[i + half] - pts[i - half]);
                    Vector3 u = Perpendicular(axis.normalized);
                    Vector3 v = Vector3.Cross(axis.normalized, u);
                    float ang = rng.Next() * 2f * Mathf.PI;
                    float mag = (0.35f + 0.65f * rng.Next()) * sigma;
                    m += (u * Mathf.Cos(ang) + v * Mathf.Sin(ang)) * mag;
                    if (m.y < 0f) m.y = 0f;
                    pts[i] = m;
                }
                sigma *= bp.Persistence;
                step = half;
            }
            for (int i = 0; i < pts.Length; i++) into.Add(pts[i]);
        }

        private static Vector3 Perpendicular(Vector3 d)
        {
            Vector3 helper = Mathf.Abs(d.y) < 0.9f ? Vector3.up : Vector3.right;
            return Vector3.Cross(d, helper).normalized;
        }

        // The number of segments of all strands.
        internal static int SegmentCount(List<BoltStrand> strands)
        {
            int n = 0;
            for (int i = 0; i < strands.Count; i++) n += Math.Max(0, strands[i].Points.Count - 1);
            return n;
        }

        // The length of the main channel in m (the sum of its segments).
        internal static float ChannelLengthM(BoltStrand strand)
        {
            float len = 0f;
            for (int i = 1; i < strand.Points.Count; i++) len += (strand.Points[i] - strand.Points[i - 1]).magnitude;
            return len;
        }
    }
}
