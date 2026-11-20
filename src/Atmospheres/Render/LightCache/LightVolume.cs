using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The light volume of the cloud pass (prototype behind cloud.lightVolume; wolken-m2a-technik.md sections 11.2 and 11.5): the optical depth towards the sun at voxel centres, for
    // the march's samples beyond the hand-over distance (ReDefinitionAtmospheresCloudLight.cginc LightVolumePlan). Layout:
    //   - the third axis is the altitude, over the shells of the layers the march lights (kind 0: lowest inner to highest outer altitude), so the volume follows the planet's curvature;
    //   - the other two are the gnomonic coordinates on the tangent plane of a frame anchored where the volume was last laid (m at the ground under the anchor; LightVolumeMath);
    //   - cascades in one texture array (slices c * GridNz ..): a near one of VoxelM and, with two, a far one FarScale times coarser and wider; the march reads the finest that holds a point;
    //   - each cascade is a window of GridN x GridN voxels on a grid fixed in the anchored frame, addressed with wrap-around: the window moves in whole voxels with the camera and
    //     old voxels keep their texel and content. The window a slice was filled for is kept per slice (_LvO), so a voxel that entered the window after its slice's last refresh is
    //     never read (the march falls back to the next cascade or the full march there), and the window's edge fades over Fade voxels.
    // The fill (pass 6 of the layer shader, one draw per slice into a scratch target copied into the array, at the head of the march in the same buffer) is a direct march per voxel
    // (the default, with the march's own far taps, stored as transmittance exp(-tau / StoreK)) or propagation: a slice takes the slice above where the sun ray crosses it and adds the
    // segment between them, so the slices are refreshed from the top down.
    // A frame refreshes Slices of the slices round-robin (0: all), times Warp and the time warp's rate (TimeWarpRate, which the host sets); above WarpLimit the far path is off and the
    // march takes the full sun march. A new lay (anchor) happens after RelayM of travel (at most 0.8 of the planet's radius), when the shells' range, the grid, the planet or anything
    // the fill depends on changed, or after Invalidate; it refills every slice once (Lays counts them). It uses nothing of the game, so the weather player links it as it is.
    internal sealed class LightVolume
    {
        internal const int PassFill = 6;
        internal const string Keyword = "CLOUD_LIGHTVOL";

        // The shader property ids, read on the first bind (a static field of this class would need Unity's native code when the module only sets Say: the T09 tests).
        private static class Ids
        {
            internal static readonly int Volume = Shader.PropertyToID("_LightVolume"), A = Shader.PropertyToID("_LvA"), U = Shader.PropertyToID("_LvU"),
                E = Shader.PropertyToID("_LvE"), N = Shader.PropertyToID("_LvN"), Z = Shader.PropertyToID("_LvZ"), C = Shader.PropertyToID("_LvC"),
                F = Shader.PropertyToID("_LvF"), O = Shader.PropertyToID("_LvO"), Slice = Shader.PropertyToID("_LvSlice"), P = Shader.PropertyToID("_LvP"),
                D = Shader.PropertyToID("_LvD"), R = Shader.PropertyToID("_LvR");
        }

        // Called with a line when the volume is laid anew (the module points it at its log; null: silent).
        internal static Action<string> Say;
        // The game's time warp rate (1: real time); the host sets it each frame, the weather player from its scene.
        internal static float TimeWarpRate = 1f;

        private RenderTexture volume, scratch;
        private Vector3 up, east, north;
        private Vector2 laidAlt;
        private float[] laidFill = new float[0];
        private bool laid, fillAll;
        private readonly Vector4[] sliceO = new Vector4[LightVolumeMath.MaxSlices];
        private readonly Vector2[] window = new Vector2[2];
        private int cursor;
        private float planetR;

        // The levers (CloudLookTuning's cloud.lightVolume.* keys; Configure reads them).
        public float HandOverM = 8000f, BandShare = 0.15f, PixelAngleRad;
        public int FarTaps = 3, Slices = 8, GridN = 128, GridNz = 32, Cascades = 2, FillTaps;
        public float Warp = 1f, WarpLimit = 1000f, VoxelM = 312.5f, FarScale = 4f, FadeVoxels = 4f, RelayM = 100000f, FillRatio = 1.15f;
        // The fill (0 direct, 1 propagation, 2 / 3 reference), the propagation's samples per segment and its limit in voxels, the diagnosis, the near length's factor, the reference's
        // taps and growth; the read: k of the stored transmittance (0: the optical depth is stored), the read point past the trilinear footprint and its longest near length.
        public int Fill, PropSteps = 2, PropCap = 16, Diagnosis, RefTaps = 64;
        public float PropLimit = 8f, NearScale = 1f, RefRatio = 1.03f, StoreK = 4f, ReadPast, ReadPastMaxM = 4000f, CapRead;

        // What the last Record did: the slices filled, whether it was a new lay, and the lays so far.
        internal int LastFilled { get; private set; }
        internal bool LastWasLay { get; private set; }
        internal int Lays { get; private set; }
        internal RenderTexture Volume { get { return volume; } }
        // Whether the far path may read the volume this frame (the time warp's rate at most WarpLimit).
        internal bool WarpAllows { get { return TimeWarpRate <= WarpLimit; } }

        // The layout for a readback (the weather player's diagnosis): the anchored frame, the altitude range, each cascade's window now and the windows each slice was filled for.
        internal string Describe()
        {
            var s = new System.Text.StringBuilder();
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            Func<Vector3, string> v3 = v => "[" + v.x.ToString("R", inv) + "," + v.y.ToString("R", inv) + "," + v.z.ToString("R", inv) + "]";
            s.Append("{\"up\":").Append(v3(up)).Append(",\"east\":").Append(v3(east)).Append(",\"north\":").Append(v3(north));
            s.Append(",\"alt\":[").Append(laidAlt.x.ToString("R", inv)).Append(',').Append(laidAlt.y.ToString("R", inv)).Append(']');
            s.Append(",\"voxel\":").Append(VoxelM.ToString("R", inv)).Append(",\"farScale\":").Append(FarScale.ToString("R", inv)).Append(",\"grid\":").Append(GridN)
             .Append(",\"gridZ\":").Append(GridNz).Append(",\"cascades\":").Append(Cascades).Append(",\"fill\":").Append(Fill).Append(",\"storeK\":").Append(StoreK.ToString("R", inv))
             .Append(",\"planetR\":").Append(planetR.ToString("R", inv));
            s.Append(",\"window\":[[").Append(window[0].x).Append(',').Append(window[0].y).Append("],[").Append(window[1].x).Append(',').Append(window[1].y).Append("]]");
            s.Append(",\"sliceO\":[");
            for (int z = 0; z < GridNz; z++)
            {
                if (z > 0) s.Append(',');
                s.Append('[').Append(sliceO[z].x.ToString("R", inv)).Append(',').Append(sliceO[z].y.ToString("R", inv)).Append(',').Append(sliceO[z].z.ToString("R", inv))
                 .Append(',').Append(sliceO[z].w.ToString("R", inv)).Append(']');
            }
            return s.Append("]}").ToString();
        }

        internal void Configure()
        {
            HandOverM = CloudLookTuning.LvHandOverM;
            BandShare = CloudLookTuning.LvBand;
            FarTaps = CloudLookTuning.LvFarTaps;
            Slices = CloudLookTuning.LvSlices;
            Warp = CloudLookTuning.LvWarp;
            WarpLimit = CloudLookTuning.LvWarpLimit;
            VoxelM = CloudLookTuning.LvVoxelM;
            GridN = CloudLookTuning.LvGrid;
            GridNz = Math.Min(CloudLookTuning.LvGridZ, LightVolumeMath.MaxSlices);
            FarScale = CloudLookTuning.LvFarScale;
            Cascades = CloudLookTuning.LvCascades;
            FadeVoxels = CloudLookTuning.LvFade;
            RelayM = CloudLookTuning.LvRelayM;
            FillTaps = CloudLookTuning.LvFillTaps;
            FillRatio = CloudLookTuning.LvFillRatio;
            Fill = CloudLookTuning.LvFill;
            PropSteps = CloudLookTuning.LvPropSteps;
            PropCap = Math.Max(CloudLookTuning.LvPropCap, PropSteps);
            PropLimit = CloudLookTuning.LvPropLimit;
            Diagnosis = CloudLookTuning.LvDebug;
            NearScale = CloudLookTuning.LvNearScale;
            RefTaps = CloudLookTuning.LvRefTaps;
            RefRatio = CloudLookTuning.LvRefRatio;
            StoreK = CloudLookTuning.LvStoreK;
            ReadPast = CloudLookTuning.LvReadPast ? 1f : 0f;
            ReadPastMaxM = CloudLookTuning.LvReadPastMaxM;
            CapRead = CloudLookTuning.LvCapRead ? 1f : 0f;
        }

        private int TotalSlices { get { return GridNz * Cascades; } }

        private void Ensure()
        {
            int depth = GridNz * Cascades;
            if (volume != null && volume.IsCreated() && scratch != null && scratch.IsCreated() && volume.width == GridN && volume.volumeDepth == depth) return;
            Dispose();
            volume = new RenderTexture(GridN, GridN, 0, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear)
            {
                name = "cloud light volume",
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = depth,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Repeat
            };
            volume.Create();
            // The fill draws a slice here and copies it into the array: the propagation reads the array (the slice above) while it draws.
            scratch = new RenderTexture(GridN, GridN, 0, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear) { name = "cloud light volume slice" };
            scratch.Create();
            laid = false;
        }

        // Places the windows around the camera (planet frame); lays the volume anew (new anchor, every slice to be refilled) on the first call, after RelayM of travel from the
        // anchor, after Invalidate, or when the layers' altitude range (shellAlt: lowest inner, highest outer altitude in m), the grid, the planet or the fill's inputs (march: the
        // sun march's taps, length and tap growth, which decide the near length and the direct fill's taps) changed.
        internal void Follow(Vector3 camera, float planetRadius, Vector2 shellAlt, int sunSteps, float sunMarchM, float tapRatio)
        {
            Ensure();
            float margin = (shellAlt.y - shellAlt.x) / Mathf.Max(GridNz, 1);
            Vector2 alt = new Vector2(shellAlt.x - margin, shellAlt.y + margin);
            float[] fill =
            {
                alt.x, alt.y, planetRadius, VoxelM, FarScale, GridN, GridNz, Cascades, FarTaps, FillTaps, FillRatio, Fill, PropSteps, PropCap, PropLimit, NearScale, RefTaps, RefRatio,
                StoreK, ReadPast, ReadPastMaxM, sunSteps, sunMarchM, tapRatio
            };
            Vector3 dir = camera.normalized;
            float relayM = Mathf.Min(RelayM, 0.8f * planetRadius);
            bool relay = !laid || !Same(fill, laidFill) || Vector3.Angle(dir, up) * Mathf.Deg2Rad * planetRadius > relayM;
            if (relay)
            {
                up = dir;
                Vector3 reference = Mathf.Abs(up.y) < 0.9f ? Vector3.up : Vector3.right;
                east = Vector3.Cross(reference, up).normalized;
                north = Vector3.Cross(up, east);
                laidAlt = alt;
                laidFill = fill;
                planetR = planetRadius;
                for (int i = 0; i < sliceO.Length; i++) sliceO[i] = new Vector4(LightVolumeMath.NotFilled, LightVolumeMath.NotFilled, LightVolumeMath.NotFilled, LightVolumeMath.NotFilled);
                cursor = 0;
                laid = true;
                fillAll = true;
                Lays++;
                if (Say != null)
                    Say("light volume laid anew (" + Lays + "): " + GridN + " x " + GridN + " x " + GridNz + " voxels, " + Cascades + " cascades of " + VoxelM + " m and "
                        + VoxelM * FarScale + " m, altitude " + alt.x.ToString("0") + " to " + alt.y.ToString("0") + " m, fill " + Fill + "; every slice refilled this frame");
            }
            // The camera's place in the anchored frame and each cascade's window, centred on it in whole voxels (a camera beyond the cut is laid anew above).
            Vector2 xy;
            if (!LightVolumeMath.Gnomonic(camera, up, east, north, planetRadius, out xy)) xy = Vector2.zero;
            for (int ci = 0; ci < 2; ci++) window[ci] = LightVolumeMath.WindowOrigin(xy, ci == 0 ? VoxelM : VoxelM * FarScale, GridN);
        }

        private static bool Same(float[] a, float[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!a[i].Equals(b[i])) return false;
            return true;
        }

        internal void Bind(Material m)
        {
            Ensure();
            m.EnableKeyword(Keyword);
            m.SetTexture(Ids.Volume, volume);
            m.SetVector(Ids.A, new Vector4(HandOverM, Mathf.Max(BandShare * Mathf.Max(HandOverM, 0f), 1f), Mathf.Clamp(FarTaps, 0, 3), PixelAngleRad));
            m.SetVector(Ids.U, up);
            m.SetVector(Ids.E, east);
            m.SetVector(Ids.N, north);
            m.SetVector(Ids.Z, new Vector4(laidAlt.x, laidAlt.y, GridNz, FadeVoxels));
            m.SetVector(Ids.C, new Vector4(VoxelM, VoxelM * FarScale, GridN, Cascades));
            m.SetVector(Ids.F, new Vector4(FillTaps, FillRatio, 0f, 0f));
            m.SetVectorArray(Ids.O, sliceO);
            m.SetVector(Ids.P, new Vector4(Mathf.Clamp(Fill, 0, 3), PropSteps, PropCap, PropLimit));
            m.SetVector(Ids.D, new Vector4(Diagnosis, NearScale, RefTaps, RefRatio));
            m.SetVector(Ids.R, new Vector4(StoreK, ReadPast, ReadPastMaxM, CapRead));
        }

        // The windows the slices were filled for, on another material that draws the march with this volume (the edge pass's copy, taken before Record).
        internal void ApplyStamps(Material m)
        {
            if (m != null) m.SetVectorArray(Ids.O, sliceO);
        }

        // The next Follow lays the volume anew (the reconstruction's Reset: a teleport, a scene change, a changed layer stack).
        internal void Invalidate()
        {
            laid = false;
        }

        // The keyword off (the march as today) on a material that had the volume bound.
        internal static void Unbind(Material m)
        {
            if (m != null) m.DisableKeyword(Keyword);
        }

        // Records this frame's refresh: Slices x Warp x the time warp's rate slices round-robin over the cascades (interleaved) from the top slice down (the propagation reads the
        // slice above, filled before), all of them on a new lay or with Slices 0; nothing while the sun is more than about 15 degrees below the horizon at the anchor (no sample is lit
        // then; the slices keep their content and catch up after sunrise). The windows the slices are filled for go into the material's _LvO now (the fill's reads of the slice above
        // and the march recorded after this read them).
        internal void Record(CommandBuffer cb, Material layerMaterial, Vector3 towardsSun)
        {
            Ensure();
            LastFilled = 0;
            LastWasLay = false;
            if (!fillAll && Vector3.Dot(towardsSun.normalized, up) < -0.26f) return;
            int total = TotalSlices;
            float rate = Mathf.Max(Warp * Mathf.Max(TimeWarpRate, 1f), 1f);
            int count = Slices <= 0 || fillAll ? total : Mathf.Min(total, Mathf.CeilToInt(Slices * rate));
            LastWasLay = fillAll;
            fillAll = false;
            for (int j = 0; j < count; j++)
            {
                int s = cursor % total;
                cursor = (cursor + 1) % total;
                int ci = s % Cascades, z = GridNz - 1 - s / Cascades;
                Vector2 o = window[ci];
                cb.SetGlobalVector(Ids.Slice, new Vector4(z, ci, o.x, o.y));
                cb.SetRenderTarget(scratch);
                cb.Blit(BuiltinRenderTextureType.None, BuiltinRenderTextureType.CurrentActive, layerMaterial, PassFill);
                cb.CopyTexture(scratch, 0, 0, volume, ci * GridNz + z, 0);
                Vector4 v = sliceO[z];
                if (ci == 0) { v.x = o.x; v.y = o.y; }
                else { v.z = o.x; v.w = o.y; }
                sliceO[z] = v;
            }
            LastFilled = count;
            layerMaterial.SetVectorArray(Ids.O, sliceO);
        }

        internal void Dispose()
        {
            if (volume != null)
            {
                volume.Release();
                UnityEngine.Object.Destroy(volume);
            }
            if (scratch != null)
            {
                scratch.Release();
                UnityEngine.Object.Destroy(scratch);
            }
            volume = scratch = null;
            laid = false;
        }
    }
}
