using System;

namespace ReDefinition.Atmospheres
{
    // The numbers of the screen-space vehicle cover (method ScreenSpace of variant (f)): which vessels the pass paints around, and the
    // region of each. Pure. A vessel is a bounding sphere with the height of its lowest point above the sphere's middle, along the local
    // up; the pass paints the pixels inside the sphere and above that height whose reconstructed face looks up. Package D34, finding R1.
    internal static class CoverScreenMath
    {
        // The vessels the pass handles at once (the shader's array size).
        internal const int MaxVessels = 4;

        // The vessels farther than this from the camera are not painted, in metres (near cameras only, like the shell).
        internal const float NearMetres = 400f;

        // How far above the lowest point of the vessel the region starts, in metres: the ground under the vessel is not covered twice.
        internal const float FloorMarginM = 0.15f;

        internal struct Region
        {
            internal float X, Y, Z;
            internal float Radius;
            // The height of the region's lower limit above the middle along up (negative), in metres.
            internal float FloorM;
            internal float Amount;
            internal float DistanceM;
        }

        // The half extent of an axis-aligned box (half sizes ex, ey, ez) along a unit direction.
        internal static float HalfExtentAlong(float ex, float ey, float ez, float ux, float uy, float uz)
        {
            return Math.Abs(ux) * ex + Math.Abs(uy) * ey + Math.Abs(uz) * ez;
        }

        // The region of a vessel from its world bounds (centre, half sizes) and the local up.
        internal static Region RegionOf(float cx, float cy, float cz, float ex, float ey, float ez, float ux, float uy, float uz, float amount)
        {
            return new Region
            {
                X = cx, Y = cy, Z = cz,
                Radius = (float)Math.Sqrt(ex * ex + ey * ey + ez * ez),
                FloorM = -HalfExtentAlong(ex, ey, ez, ux, uy, uz) + FloorMarginM,
                Amount = amount
            };
        }

        // The regions to paint: the vessels within NearMetres of the camera with an amount of at least minAmount, the nearest MaxVessels
        // of them, nearest first. The input list is not changed.
        internal static int Select(Region[] all, int count, float camX, float camY, float camZ, float minAmount, Region[] into)
        {
            int taken = 0;
            for (int i = 0; i < count; i++)
            {
                Region r = all[i];
                if (!(r.Amount >= minAmount)) continue;
                float dx = r.X - camX, dy = r.Y - camY, dz = r.Z - camZ;
                float d = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz) - r.Radius;
                if (d > NearMetres) continue;
                r.DistanceM = d;
                // Insert by distance; the farthest falls off the end of a full list.
                int at = taken < MaxVessels ? taken : MaxVessels - 1;
                if (taken >= MaxVessels && into[at].DistanceM <= d) continue;
                into[at] = r;
                if (taken < MaxVessels) taken++;
                while (at > 0 && into[at - 1].DistanceM > into[at].DistanceM)
                {
                    Region t = into[at - 1];
                    into[at - 1] = into[at];
                    into[at] = t;
                    at--;
                }
            }
            return taken;
        }

        // Whether a world point is inside a region: within the radius of the middle and above the lower limit along up.
        internal static bool Inside(Region r, float px, float py, float pz, float ux, float uy, float uz)
        {
            float dx = px - r.X, dy = py - r.Y, dz = pz - r.Z;
            if (dx * dx + dy * dy + dz * dz > r.Radius * r.Radius) return false;
            return dx * ux + dy * uy + dz * uz >= r.FloorM;
        }
    }
}
