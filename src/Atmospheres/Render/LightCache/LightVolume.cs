using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The light volume of the cloud pass (prototype LV1 behind cloud.lightVolume; wolken-m2a-technik.md section 11.2): the optical depth towards the sun at voxel centres, for the march's
    // samples beyond the hand-over distance (ReDefinitionAtmospheresCloudLight.cginc LightVolumePlan). Layout:
    //   - the third axis is the altitude, over the layers' shells (lowest inner to highest outer altitude), so the volume follows the planet's curvature;
    //   - the other two are the gnomonic coordinates on the tangent plane of a frame anchored where the volume was last laid (m at the ground under the anchor);
    //   - two cascades in one texture array (slices c * GridNz ..): a near one of VoxelM and a far one FarScale times coarser and wider; the march reads the finest that holds a point;
    //   - each cascade is a window of GridN x GridN voxels on a grid fixed in the anchored frame, addressed with wrap-around: the window moves in whole voxels with the camera and
    //     old voxels keep their texel and content. The window a slice was filled for is kept per slice (_LvO), so a voxel that entered the window after its slice's last refresh is
    //     never read (the march falls back to the next cascade or the full march there), and the window's edge fades over Fade voxels.
    // A frame refreshes Slices of the 2 * GridNz slices round-robin (0: all), times Warp; a new lay (anchor) happens only after RelayM of travel or when the shells' range changes, and
    // refills every slice once (Lays counts them). The fill is pass 6 of the layer shader, one draw per slice, at the head of the march in the same buffer. It uses nothing of the
    // game, so the weather player links it as it is.
    internal sealed class LightVolume
    {
        internal const int PassFill = 6;
        internal const string Keyword = "CLOUD_LIGHTVOL";
        private const int MaxSlices = 64;
        private const float NotFilled = -1e8f;

        // The shader property ids, read on the first bind (a static field of this class would need Unity's native code when the module only sets Say: the T09 tests).
        private static class Ids
        {
            internal static readonly int Volume = Shader.PropertyToID("_LightVolume"), A = Shader.PropertyToID("_LvA"), U = Shader.PropertyToID("_LvU"),
                E = Shader.PropertyToID("_LvE"), N = Shader.PropertyToID("_LvN"), Z = Shader.PropertyToID("_LvZ"), C = Shader.PropertyToID("_LvC"),
                F = Shader.PropertyToID("_LvF"), O = Shader.PropertyToID("_LvO"), Slice = Shader.PropertyToID("_LvSlice");
        }

        // Called with a line when the volume is laid anew (the module points it at its log; null: silent).
        internal static Action<string> Say;

        private RenderTexture volume;
        private Vector3 up, east, north;
        private Vector2 laidAlt;
        private float laidVoxel, laidScale;
        private int laidGrid, laidGridZ, laidCascades;
        private bool laid, fillAll;
        private readonly Vector4[] sliceO = new Vector4[MaxSlices];
        private readonly Vector2[] window = new Vector2[2];
        private int cursor;
        private float planetR;

        // The levers (CloudLookTuning's cloud.lightVolume.* keys; Configure reads them).
        public float HandOverM = 8000f, BandShare = 0.5f, PixelAngleRad;
        public int FarTaps = 3, Slices = 8, GridN = 128, GridNz = 32, Cascades = 2, FillTaps;
        public float Warp = 1f, VoxelM = 312.5f, FarScale = 4f, FadeVoxels = 4f, RelayM = 100000f, FillRatio = 1.15f;

        // What the last Record did: the slices filled, whether it was a new lay, and the lays so far.
        internal int LastFilled { get; private set; }
        internal bool LastWasLay { get; private set; }
        internal int Lays { get; private set; }
        internal RenderTexture Volume { get { return volume; } }

        // The layout for a readback (the weather player's diagnosis): the anchored frame, the altitude range, each cascade's window now and the windows each slice was filled for.
        internal string Describe()
        {
            var s = new System.Text.StringBuilder();
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            Func<Vector3, string> v3 = v => "[" + v.x.ToString("R", inv) + "," + v.y.ToString("R", inv) + "," + v.z.ToString("R", inv) + "]";
            s.Append("{\"up\":").Append(v3(up)).Append(",\"east\":").Append(v3(east)).Append(",\"north\":").Append(v3(north));
            s.Append(",\"alt\":[").Append(laidAlt.x.ToString("R", inv)).Append(',').Append(laidAlt.y.ToString("R", inv)).Append(']');
            s.Append(",\"voxel\":").Append(VoxelM.ToString("R", inv)).Append(",\"farScale\":").Append(FarScale.ToString("R", inv)).Append(",\"grid\":").Append(GridN)
             .Append(",\"gridZ\":").Append(GridNz).Append(",\"cascades\":").Append(Cascades).Append(",\"planetR\":").Append(planetR.ToString("R", inv));
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
            VoxelM = CloudLookTuning.LvVoxelM;
            GridN = CloudLookTuning.LvGrid;
            GridNz = CloudLookTuning.LvGridZ;
            FarScale = CloudLookTuning.LvFarScale;
            Cascades = CloudLookTuning.LvCascades;
            FadeVoxels = CloudLookTuning.LvFade;
            RelayM = CloudLookTuning.LvRelayM;
            FillTaps = CloudLookTuning.LvFillTaps;
            FillRatio = CloudLookTuning.LvFillRatio;
        }

        private int TotalSlices { get { return GridNz * Cascades; } }

        private void Ensure()
        {
            int depth = GridNz * 2;
            if (volume != null && volume.width == GridN && volume.volumeDepth == depth) return;
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
            laid = false;
        }

        // Places the windows around the camera (planet frame); lays the volume anew (new anchor, every slice to be refilled) on the first call, after RelayM of travel from the
        // anchor, or when the layers' altitude range (shellAlt: lowest inner, highest outer altitude in m) or the grid changed.
        internal void Follow(Vector3 camera, float planetRadius, Vector2 shellAlt)
        {
            Ensure();
            planetR = planetRadius;
            float margin = (shellAlt.y - shellAlt.x) / Mathf.Max(GridNz, 1);
            Vector2 alt = new Vector2(shellAlt.x - margin, shellAlt.y + margin);
            Vector3 dir = camera.normalized;
            bool relay = !laid || alt != laidAlt || VoxelM != laidVoxel || FarScale != laidScale || GridN != laidGrid || GridNz != laidGridZ || Cascades != laidCascades
                         || Vector3.Angle(dir, up) * Mathf.Deg2Rad * planetRadius > RelayM;
            if (relay)
            {
                up = dir;
                Vector3 reference = Mathf.Abs(up.y) < 0.9f ? Vector3.up : Vector3.right;
                east = Vector3.Cross(reference, up).normalized;
                north = Vector3.Cross(up, east);
                laidAlt = alt;
                laidVoxel = VoxelM;
                laidScale = FarScale;
                laidGrid = GridN;
                laidGridZ = GridNz;
                laidCascades = Cascades;
                for (int i = 0; i < sliceO.Length; i++) sliceO[i] = new Vector4(NotFilled, NotFilled, NotFilled, NotFilled);
                cursor = 0;
                laid = true;
                fillAll = true;
                Lays++;
                if (Say != null)
                    Say("light volume laid anew (" + Lays + "): " + GridN + " x " + GridN + " x " + GridNz + " voxels, " + Cascades + " cascades of " + VoxelM + " m and "
                        + VoxelM * FarScale + " m, altitude " + alt.x.ToString("0") + " to " + alt.y.ToString("0") + " m; every slice refilled this frame");
            }
            // The camera's place in the anchored frame (gnomonic) and each cascade's window, centred on it in whole voxels.
            float c = Mathf.Max(Vector3.Dot(dir, up), 0.5f);
            Vector2 xy = planetRadius * new Vector2(Vector3.Dot(dir, east), Vector3.Dot(dir, north)) / c;
            for (int ci = 0; ci < 2; ci++)
            {
                float vox = ci == 0 ? VoxelM : VoxelM * FarScale;
                window[ci] = new Vector2(Mathf.Floor(xy.x / vox) - GridN / 2, Mathf.Floor(xy.y / vox) - GridN / 2);
            }
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

        // Records this frame's refresh: Slices x Warp slices round-robin over both cascades (interleaved), all of them on a new lay or with Slices 0. The windows the slices are filled for
        // go into the material's _LvO now (the march recorded after this reads them).
        internal void Record(CommandBuffer cb, Material layerMaterial)
        {
            Ensure();
            int total = TotalSlices;
            int count = Slices <= 0 || fillAll ? total : Mathf.Min(total, Mathf.CeilToInt(Slices * Warp));
            LastWasLay = fillAll;
            fillAll = false;
            for (int j = 0; j < count; j++)
            {
                int s = cursor % total;
                cursor = (cursor + 1) % total;
                int ci = s % Cascades, z = s / Cascades;
                Vector2 o = window[ci];
                cb.SetGlobalVector(Ids.Slice, new Vector4(z, ci, o.x, o.y));
                cb.SetRenderTarget(new RenderTargetIdentifier(volume), 0, CubemapFace.Unknown, ci * GridNz + z);
                cb.Blit(BuiltinRenderTextureType.None, BuiltinRenderTextureType.CurrentActive, layerMaterial, PassFill);
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
            volume = null;
            laid = false;
        }
    }
}
