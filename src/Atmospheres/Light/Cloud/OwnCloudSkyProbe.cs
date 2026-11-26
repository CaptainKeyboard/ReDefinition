using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The own clouds in what the sea and the land mirror (master plan 46.1 part 4; the owner 11-26: "Klar soll das Meer die Wolkendecke spiegeln"). With
    // Scatterer's sky reflections off the ocean reads KSP's flight reflection probe, and the land's environment light is that probe too (PRUEFPROTOKOLL 293,
    // 312); an own probe of mode Custom with a higher importance in KSP's probe's box replaces it for both (314). Its cube is KSP's probe's cube with the own
    // clouds composited over it: each frame one face is copied from KSP's probe and the layer pass's march is drawn through it (pass 8: the face's basis,
    // no scene depth, no reconstruction, with the display stage); after the sixth face the mips are made again. KSP's probe itself is not touched; while
    // the own clouds are not drawn the own probe is off and KSP's carries on as before.
    // The cube's faces follow Direct3D's convention (texel row 0 at the top): the face bases below; light.testCubeDirs 1 with layers.devVariant 1 draws the
    // faces' ray directions as colours instead, through the same pass (a check of the convention: the land, whose ambient looks up, turns green).
    internal sealed class OwnCloudSkyProbe
    {
        internal const int PassFace = 8;
        internal const string TuneCubeDirs = "light.testCubeDirs";

        // Forward, right (times the texel's x from -1 to 1) and the row vector (times the texel's y from -1 at the top to 1 at the bottom) of faces +X, -X, +Y, -Y, +Z, -Z.
        internal static readonly Vector3[] Forward = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        internal static readonly Vector3[] Right = { Vector3.back, Vector3.forward, Vector3.right, Vector3.right, Vector3.right, Vector3.left };
        internal static readonly Vector3[] Row = { Vector3.down, Vector3.down, Vector3.forward, Vector3.back, Vector3.down, Vector3.down };

        // The world direction of a texel's centre (x, y from 0, row 0 at the top) of a face of size n: the CPU twin of the march's RayDir with the face basis.
        internal static Vector3 Direction(int face, int x, int y, int n)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            return (Forward[face] + u * Right[face] + v * Row[face]).normalized;
        }

        private static readonly int IdCamPos = Shader.PropertyToID("_CamPos"), IdCamForward = Shader.PropertyToID("_CamForward"), IdCamRight = Shader.PropertyToID("_CamRight"),
            IdCamUp = Shader.PropertyToID("_CamUp"), IdReconP = Shader.PropertyToID("_ReconP"), IdReconD = Shader.PropertyToID("_ReconD"), IdCullA = Shader.PropertyToID("_CullA"),
            IdCullB = Shader.PropertyToID("_CullB"), IdCullD = Shader.PropertyToID("_CullD"), IdDitherP = Shader.PropertyToID("_DitherP"), IdLimitP = Shader.PropertyToID("_LimitP"),
            IdSceneDepth = Shader.PropertyToID("_SceneDepth"), IdSkyProbeP = Shader.PropertyToID("_SkyProbeP");

        private ReflectionProbe ksp, own;
        private GameObject holder;
        private RenderTexture cube;
        private Texture2D noDepth;
        // Made on first use (the module is built in the tests, outside the engine).
        private CommandBuffer cb;
        private MaterialPropertyBlock block;
        private int face;
        private float nextScan;
        private bool said;

        internal bool Active { get { return own != null && own.enabled; } }

        // From the own clouds' frame, after the layer pass's material holds the frame's values: draws one face and keeps the own probe on KSP's.
        internal void Update(Material layers)
        {
            if (ksp == null && Time.realtimeSinceStartup >= nextScan)
            {
                nextScan = Time.realtimeSinceStartup + 1f;
                FlightReflectionProbe flight = Object.FindObjectOfType<FlightReflectionProbe>();
                ksp = flight != null ? flight.probeComponent : null;
            }
            Texture source = ksp != null ? ksp.realtimeTexture : null;
            if (ksp == null || source == null || layers == null)
            {
                Off();
                return;
            }
            if (cube == null || cube.width != source.width)
            {
                if (cube != null) cube.Release();
                cube = new RenderTexture(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                {
                    name = "ReDefinition own sky probe", dimension = TextureDimension.Cube, useMipMap = true, autoGenerateMips = false
                };
                cube.Create();
                face = 0;
            }
            if (holder == null)
            {
                holder = new GameObject("ReDefinition own sky probe");
                Object.DontDestroyOnLoad(holder);
                own = holder.AddComponent<ReflectionProbe>();
                own.mode = ReflectionProbeMode.Custom;
            }
            own.customBakedTexture = cube;
            own.importance = ksp.importance + 1;
            own.size = ksp.size;
            own.center = ksp.center;
            own.boxProjection = ksp.boxProjection;
            own.blendDistance = ksp.blendDistance;
            own.enabled = true;
            holder.transform.position = ksp.transform.position;

            if (cb == null) cb = new CommandBuffer { name = "ReDefinition own sky probe" };
            if (block == null) block = new MaterialPropertyBlock();
            cb.Clear();
            DrawFace(cb, layers, source, face, holder.transform.position, Tuning.Get(TuneCubeDirs, 0f) > 0.5f);
            if (face == 5) cb.GenerateMips(cube);
            Graphics.ExecuteCommandBuffer(cb);
            face = (face + 1) % 6;
            if (!said)
            {
                said = true;
                AtmospheresLog.Say("own clouds: the sky probe mirrors the own clouds (" + cube.width + " per face, one face a frame, importance " + own.importance + " over KSP's flight probe)");
            }
        }

        private void DrawFace(CommandBuffer c, Material layers, Texture source, int f, Vector3 at, bool testDirs)
        {
            c.CopyTexture(source, f, 0, cube, f, 0);
            c.SetRenderTarget(cube, 0, (CubemapFace)f);
            int n = cube.width;
            if (noDepth == null)
            {
                noDepth = new Texture2D(1, 1, TextureFormat.RFloat, false, true) { name = "ReDefinition no depth" };
                noDepth.SetPixel(0, 0, new Color(0f, 0f, 0f, 0f));
                noDepth.Apply(false);
            }
            block.Clear();
            block.SetVector(IdCamPos, new Vector4(at.x, at.y, at.z, 0f));
            block.SetVector(IdCamForward, Forward[f]);
            block.SetVector(IdCamRight, new Vector4(Right[f].x, Right[f].y, Right[f].z, 0f));
            block.SetVector(IdCamUp, new Vector4(Row[f].x, Row[f].y, Row[f].z, 0f));
            // No reconstruction, the face's own pixel grid, no tile map, no layer cap, the footprint of a face's texel, no scene depth (the product variant always reads it).
            block.SetVector(IdReconP, new Vector4(1f, 0f, 0f, 0f));
            block.SetVector(IdReconD, new Vector4(n, n, 0f, 0f));
            Vector4 cullA = layers.GetVector(IdCullA), cullB = layers.GetVector(IdCullB), cullD = layers.GetVector(IdCullD), dither = layers.GetVector(IdDitherP),
                limit = layers.GetVector(IdLimitP);
            block.SetVector(IdCullA, new Vector4(0f, cullA.y, cullA.z, cullA.w));
            block.SetVector(IdCullB, new Vector4(cullB.x, cullB.y, 0f, cullB.w));
            block.SetVector(IdCullD, new Vector4(cullD.x, 2f / n, cullD.z, cullD.w));
            block.SetVector(IdDitherP, new Vector4(dither.x, dither.y, dither.z, 1f));
            block.SetVector(IdLimitP, new Vector4(0f, 0f, 0f, 0f));
            block.SetTexture(IdSceneDepth, noDepth);
            // light.testCubeDirs (the test variant: layers.devVariant 1): the faces hold their rays' directions as colours, drawn by the same pass and basis as the clouds.
            block.SetVector(IdSkyProbeP, new Vector4(testDirs ? 1f : 0f, 0f, 0f, 0f));
            c.DrawProcedural(Matrix4x4.identity, layers, PassFace, MeshTopology.Triangles, 3, 1, block);
        }

        // The own clouds not drawn: KSP's probe carries on.
        internal void Off()
        {
            if (own != null) own.enabled = false;
        }

        internal void Dispose()
        {
            if (holder != null) Object.Destroy(holder);
            holder = null;
            own = null;
            if (cube != null) cube.Release();
            cube = null;
            if (noDepth != null) Object.Destroy(noDepth);
            noDepth = null;
        }
    }
}
