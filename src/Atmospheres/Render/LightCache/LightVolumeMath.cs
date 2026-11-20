using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The CPU twin of the light volume's addressing (ReDefinitionAtmospheresCloudLight.cginc LightVolumeFetch / LightVolumeSlice, the layer shader's pass 6): the gnomonic coordinates
    // of a direction in the anchored frame and back, a cascade's window around a point, the voxel a texel holds under wrap-around addressing, and whether a window holds a point's
    // bilinear footprint. LightVolume places its windows with it; LightVolumeMathTests checks the shader's formulas against it.
    internal static class LightVolumeMath
    {
        // The gnomonic frame's cut: a point farther than acos(MinCos) from the anchor is outside every window (LV_MIN_COS).
        internal const float MinCos = 0.5f;
        // The slices of a cascade at most: the length of the shader's _LvO (LV_MAX_SLICES).
        internal const int MaxSlices = 64;
        // The stamp of a slice not filled since the lay: far outside any window.
        internal const float NotFilled = -1e8f;
        // The least transmittance stored (a half float's normal range; LV_MIN_STORED).
        internal const float MinStored = 6.2e-5f;

        // The tangent-plane coordinates in m (at the ground under the anchor) of the direction of p; false beyond the cut.
        internal static bool Gnomonic(Vector3 p, Vector3 up, Vector3 east, Vector3 north, float planetRadius, out Vector2 xy)
        {
            Vector3 n = p.normalized;
            float c = Vector3.Dot(n, up);
            xy = Vector2.zero;
            if (c <= MinCos) return false;
            xy = planetRadius * new Vector2(Vector3.Dot(n, east), Vector3.Dot(n, north)) / c;
            return true;
        }

        // The point at this altitude whose direction has the tangent-plane coordinates xy (the fill's voxel centre).
        internal static Vector3 FromGnomonic(Vector2 xy, float altitude, Vector3 up, Vector3 east, Vector3 north, float planetRadius)
        {
            return (up + (xy.x * east + xy.y * north) / planetRadius).normalized * (planetRadius + altitude);
        }

        // The window origin (voxel index) of a cascade with this voxel size around the point xy: whole voxels, the point in the middle.
        internal static Vector2 WindowOrigin(Vector2 xy, float voxelM, int grid)
        {
            return new Vector2(Mathf.Floor(xy.x / voxelM) - grid / 2, Mathf.Floor(xy.y / voxelM) - grid / 2);
        }

        // The voxel index a texel holds in the window with this origin: the one in [origin, origin + grid) congruent to the texel modulo grid.
        internal static float VoxelOfTexel(float texel, float origin, int grid)
        {
            float rel = texel - origin;
            return origin + rel - grid * Mathf.Floor(rel / grid);
        }

        // The distance in voxels from a point's bilinear footprint to the window's edge (below 0: the window does not hold it).
        internal static float EdgeDistance(Vector2 xy, float voxelM, Vector2 origin, int grid)
        {
            Vector2 g = xy / voxelM - new Vector2(0.5f, 0.5f);
            float dx = Mathf.Min(g.x - origin.x, origin.x + grid - 1f - g.x), dy = Mathf.Min(g.y - origin.y, origin.y + grid - 1f - g.y);
            return Mathf.Min(dx, dy);
        }
    }
}
